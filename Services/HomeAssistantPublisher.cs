using System.Threading.Channels;
using LabbyTwo.Core;
using MQTTnet;
using MQTTnet.Protocol;

namespace LabbyTwo.Services;

/// <summary>What the publisher needs from the rest of the app — an interface so the MQTT half is tested against a real broker with a made-up lab.</summary>
public interface IHomeAssistantSource
{
    /// <summary>The saved settings. Cached by the settings store after the first read.</summary>
    Task<HaSettings> SettingsAsync(CancellationToken ct);

    /// <summary>The lab as it is right now, from memory.</summary>
    Task<HaLab> LabAsync(CancellationToken ct);

    /// <summary>
    /// A button pressed in Home Assistant: checks it may run, runs it, and records it in the
    /// change feed whatever happened — including when it was refused or ignored.
    /// </summary>
    /// <param name="retained">The message was retained on the broker, so it is a replay of an old press rather than a new one, and is ignored.</param>
    /// <returns>What happened, in words.</returns>
    Task<string> CommandAsync(HaCommand command, HaSettings settings, bool retained, CancellationToken ct);
}

/// <summary>How the link to Home Assistant is doing, for the Settings page. In memory only.</summary>
public sealed record HaStatus
{
    public bool Connected { get; init; }
    public DateTimeOffset? ConnectedSince { get; init; }
    public DateTimeOffset? LastPublishAt { get; init; }

    /// <summary>How many messages the last publish sent — zero when nothing had changed.</summary>
    public int LastPublishSent { get; init; }

    public long Sent { get; init; }
    public int Entities { get; init; }

    /// <summary>The most recent problem, kept after a later success so an intermittent one is still visible.</summary>
    public string? LastError { get; init; }

    public DateTimeOffset? LastErrorAt { get; init; }
    public DateTimeOffset? LastCommandAt { get; init; }
    public string? LastCommand { get; init; }
}

/// <summary>
/// The MQTT half of the Home Assistant link: one broker session, discovery, availability,
/// and sending what changed.
///
/// Why each part is the way it is:
/// <list type="bullet">
/// <item><b>One loop owns the session.</b> Connecting, publishing and reconnecting all happen
/// on <see cref="RunAsync"/>, never on the monitor's sweep: events only <see cref="Kick"/>
/// it. A broker that is slow or gone therefore costs this loop its time and nobody else's.</item>
/// <item><b>Debounced.</b> A sweep raises several events in a row — the monitor, the alert
/// pass, the incident tracker — and each would otherwise be its own publish. A kick waits
/// <see cref="Debounce"/> for the rest to land, then publishes once.</item>
/// <item><b>Deltas.</b> Every message is retained, and the last payload sent to each topic is
/// remembered, so a sweep where nothing changed sends nothing. A full refresh every few
/// minutes resends the lot, in case something on the broker's side was lost.</item>
/// <item><b>Availability.</b> The broker holds a will saying "offline", sent for us if the
/// connection drops; the birth message says "online". Every entity points at that topic, so
/// HA greys everything out when LabbyTwo stops rather than showing stale greens.</item>
/// <item><b>HA restarts.</b> Home Assistant announces itself on <c>homeassistant/status</c>;
/// "online" there means it may have forgotten everything, so discovery and states go again.</item>
/// <item><b>Leftovers.</b> This subscribes to its own discovery configs. Retained ones that
/// the current plan does not include — a connection deleted while LabbyTwo was off, one
/// unticked, a metric no longer chosen — are cleared with an empty retained payload, which
/// is how MQTT discovery removes an entity.</item>
/// </list>
///
/// Failures are logged and shown on the Settings page, and never sent to the alert
/// channels: a broker hiccup alerting through a channel that might itself be HA is a loop.
/// </summary>
public sealed class HomeAssistantPublisher(IHomeAssistantSource source, ILogger log) : IAsyncDisposable
{
    /// <summary>How long a kick waits for the rest of a sweep's events before publishing.</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The first wait after a failed connection; doubled each time up to <see cref="MaxBackoff"/>.</summary>
    public TimeSpan MinBackoff { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A press of the same button within this long of the last is dropped. An automation
    /// stuck in a loop, or a flaky Wi-Fi switch that sends three presses, should run the
    /// action once.
    /// </summary>
    public TimeSpan RepeatGuard { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Settable so a test need not wait for the retained configs to arrive.</summary>
    public TimeSpan RetainedWait { get; init; } = TimeSpan.FromMilliseconds(500);

    private readonly Channel<bool> _kicks = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
    });

    private readonly object _gate = new();
    private readonly Dictionary<string, string> _sentConfigs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sentStates = new(StringComparer.Ordinal);

    /// <summary>Discovery configs believed to be on the broker under this install's node — sent by us, or found retained.</summary>
    private readonly HashSet<string> _knownConfigs = new(StringComparer.Ordinal);

    private readonly Dictionary<string, DateTimeOffset> _lastPress = new(StringComparer.Ordinal);

    private IMqttClient? _client;
    private HaSettings? _session;
    private HaPlan? _plan;
    private bool _fullPending = true;
    private DateTimeOffset _lastFull = DateTimeOffset.MinValue;
    private HaStatus _status = new();

    public HaStatus Status => Volatile.Read(ref _status);

    /// <summary>Raised whenever <see cref="Status"/> changes, so an open Settings page can redraw.</summary>
    public event Action? StatusChanged;

    /// <summary>Something may have changed. Cheap and never blocks: at most one kick is ever queued.</summary>
    public void Kick() => _kicks.Writer.TryWrite(true);

    /// <summary>Resend every config and state on the next publish, as after a reconnect or an HA restart.</summary>
    public void RequestFull()
    {
        lock (_gate)
            _fullPending = true;
        Kick();
    }

    /// <summary>The loop. Returns when <paramref name="ct"/> is cancelled, saying goodbye to the broker first.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var backoff = MinBackoff;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var settings = await source.SettingsAsync(ct);
                if (!settings.Active || settings.Problem() is not null)
                {
                    await StopSessionAsync(clearEverything: true);
                    await WaitForKickAsync(Timeout.InfiniteTimeSpan, ct);
                    continue;
                }

                if (_client is not { IsConnected: true } || _session?.Fingerprint != settings.Fingerprint)
                {
                    try
                    {
                        await StopSessionAsync(clearEverything: _session is not null && _session.Node != settings.Node);
                        await ConnectAsync(settings, ct);
                        backoff = MinBackoff;
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        Fail($"Could not connect to {settings.Host}:{settings.Port}: {Describe(ex)}", warn: backoff == MinBackoff);
                        await WaitForKickAsync(backoff, ct);
                        backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
                        continue;
                    }
                }

                _session = settings;
                try
                {
                    await PublishAsync(settings, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    Fail($"Publishing to Home Assistant failed: {Describe(ex)}", warn: true);
                }

                var untilRefresh = _lastFull + settings.Refresh - DateTimeOffset.Now;
                if (await WaitForKickAsync(untilRefresh > TimeSpan.Zero ? untilRefresh : TimeSpan.Zero, ct))
                {
                    // Let the rest of the sweep's events land, then take them all as one.
                    await Task.Delay(Debounce, ct);
                    while (_kicks.Reader.TryRead(out _)) { }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Nothing above should get here; if something does, the loop must not die
                // with it — that would be the link going quiet with no word on the page.
                Fail($"The Home Assistant link hit a problem: {Describe(ex)}", warn: true);
                await Task.Delay(MinBackoff, ct).ContinueWith(_ => { }, CancellationToken.None);
            }
        }

        await StopSessionAsync(clearEverything: false);
    }

    /// <summary>True when kicked, false when the time ran out.</summary>
    private async Task<bool> WaitForKickAsync(TimeSpan wait, CancellationToken ct)
    {
        if (_kicks.Reader.TryRead(out _))
            return true;
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (wait != Timeout.InfiniteTimeSpan)
            window.CancelAfter(wait);
        try
        {
            await _kicks.Reader.ReadAsync(window.Token);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task ConnectAsync(HaSettings s, CancellationToken ct)
    {
        var client = new MqttClientFactory().CreateMqttClient();
        var availability = HaTopics.Availability(s);

        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(s.Host.Trim(), s.Port)
            // Fixed per install, so the broker's log says who this is, and a reconnect
            // replaces the old session rather than sitting beside it.
            .WithClientId($"labbytwo-ha-{s.Node}")
            .WithCleanSession()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .WithTimeout(TimeSpan.FromSeconds(15))
            .WithWillTopic(availability)
            .WithWillPayload(HaTopics.Offline)
            .WithWillRetain()
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);

        if (s.Username.Length > 0)
            builder = builder.WithCredentials(s.Username, s.Password);

        if (s.Tls)
        {
            // Self-signed is the norm on a LAN broker — the same reasoning as MqttPool.
            builder = builder.WithTlsOptions(options => options
                .UseTls()
                .WithCertificateValidationHandler(_ => true));
        }

        client.ApplicationMessageReceivedAsync += args =>
        {
            OnMessage(s, args.ApplicationMessage);
            return Task.CompletedTask;
        };

        client.DisconnectedAsync += args =>
        {
            if (!ReferenceEquals(_client, client))
                return Task.CompletedTask;
            if (args.ClientWasConnected)
            {
                Fail($"Lost the connection to {s.Host}: {args.Exception?.GetBaseException().Message ?? args.Reason.ToString()}", warn: true);
                Kick();
            }
            return Task.CompletedTask;
        };

        try
        {
            await client.ConnectAsync(builder.Build(), ct);

            // Birth before anything else, so the entities about to be announced are
            // available from the moment HA learns of them.
            await PublishRawAsync(client, availability, HaTopics.Online, ct);

            var filters = new List<string> { HaTopics.HomeAssistantStatus(s), HaTopics.OwnConfigs(s) };
            if (s.AnyControls)
                filters.AddRange(HaTopics.CommandFilters(s));
            var subscribe = new MqttClientSubscribeOptionsBuilder();
            foreach (var filter in filters)
                subscribe = subscribe.WithTopicFilter(f => f.WithTopic(filter).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce));
            await client.SubscribeAsync(subscribe.Build(), ct);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        lock (_gate)
        {
            _client = client;
            _session = s;
            _sentConfigs.Clear();
            _sentStates.Clear();
            _fullPending = true;
        }

        log.LogInformation("Home Assistant link connected to {Host}:{Port}", s.Host.Trim(), s.Port);
        Record(x => x with { Connected = true, ConnectedSince = DateTimeOffset.Now });

        // The broker hands over retained configs straight after the subscribe; give them a
        // moment, so the first publish already knows what to clear.
        if (RetainedWait > TimeSpan.Zero)
            await Task.Delay(RetainedWait, ct);
    }

    private void OnMessage(HaSettings s, MqttApplicationMessage message)
    {
        var topic = message.Topic;
        var payload = message.ConvertPayloadToString() ?? "";

        if (topic == HaTopics.HomeAssistantStatus(s))
        {
            if (string.Equals(payload.Trim(), HaTopics.Online, StringComparison.OrdinalIgnoreCase) && !message.Retain)
            {
                log.LogInformation("Home Assistant restarted; announcing everything again");
                RequestFull();
            }
            return;
        }

        if (topic.StartsWith(s.Prefix + "/", StringComparison.Ordinal) && topic.EndsWith("/config", StringComparison.Ordinal))
        {
            var clear = false;
            lock (_gate)
            {
                if (payload.Length == 0)
                {
                    _knownConfigs.Remove(topic);
                }
                else
                {
                    _knownConfigs.Add(topic);
                    clear = _plan is { } plan && !plan.Keeps(topic);
                }
            }
            if (clear)
                Kick();
            return;
        }

        if (HaTopics.ParseCommand(s, topic) is not { } command)
            return;

        var now = DateTimeOffset.Now;
        lock (_gate)
        {
            if (!message.Retain && _lastPress.TryGetValue(command.Key, out var last) && now - last < RepeatGuard)
            {
                log.LogDebug("Ignored a repeat press of {Command} from Home Assistant", command.Key);
                return;
            }
            if (!message.Retain)
                _lastPress[command.Key] = now;
        }

        // Off the client's receive loop: an action can take a minute and a half, and
        // MQTTnet does not deliver the next message until this handler returns.
        var retained = message.Retain;
        _ = Task.Run(async () =>
        {
            try
            {
                var said = await source.CommandAsync(command, s, retained, CancellationToken.None);
                Record(x => x with { LastCommandAt = now, LastCommand = said });
                Kick();
            }
            catch (Exception ex)
            {
                log.LogError(ex, "A command from Home Assistant failed: {Command}", command.Key);
            }
        });
    }

    /// <summary>
    /// Builds the plan from memory and sends what differs from what was last sent: configs
    /// first, so HA is listening before the state arrives, then clears, then states.
    /// </summary>
    private async Task PublishAsync(HaSettings s, CancellationToken ct)
    {
        var client = _client ?? throw new InvalidOperationException("Not connected.");
        var lab = await source.LabAsync(ct);
        var plan = HaPlan.Build(s, lab);
        var now = DateTimeOffset.Now;

        bool full;
        lock (_gate)
        {
            full = _fullPending || now - _lastFull >= s.Refresh;
            _fullPending = false;
            _plan = plan;
            if (full)
            {
                _sentConfigs.Clear();
                _sentStates.Clear();
            }
        }

        if (full)
            _lastFull = now;

        var sent = 0;

        foreach (var entity in plan.Entities)
        {
            if (Sent(_sentConfigs, entity.ConfigTopic) == entity.Config)
                continue;
            await PublishRawAsync(client, entity.ConfigTopic, entity.Config, ct);
            lock (_gate)
            {
                _sentConfigs[entity.ConfigTopic] = entity.Config;
                _knownConfigs.Add(entity.ConfigTopic);
            }
            sent++;
        }

        List<string> clear;
        lock (_gate)
            clear = [.. _knownConfigs.Where(topic => !plan.Keeps(topic))];
        foreach (var topic in clear)
        {
            await PublishRawAsync(client, topic, "", ct);
            lock (_gate)
            {
                _knownConfigs.Remove(topic);
                _sentConfigs.Remove(topic);
            }
            sent++;
        }

        // States of entities that are gone, so a removed connection's last "ON" does not sit
        // retained on the broker for ever. Only the ones sent in this session are known.
        List<string> staleStates;
        lock (_gate)
            staleStates = [.. _sentStates.Keys.Where(topic => !plan.States.ContainsKey(topic))];
        foreach (var topic in staleStates)
        {
            await PublishRawAsync(client, topic, "", ct);
            lock (_gate)
                _sentStates.Remove(topic);
            sent++;
        }

        foreach (var (topic, payload) in plan.States)
        {
            if (Sent(_sentStates, topic) == payload)
                continue;
            var body = plan.LastChecked.TryGetValue(topic, out var checkedAt) ? HaPlan.WithLastChecked(payload, checkedAt) : payload;
            await PublishRawAsync(client, topic, body, ct);
            lock (_gate)
                _sentStates[topic] = payload;
            sent++;
        }

        Record(x => x with
        {
            Connected = client.IsConnected,
            LastPublishAt = now,
            LastPublishSent = sent,
            Sent = x.Sent + sent,
            Entities = plan.Entities.Count,
        });
    }

    private string? Sent(Dictionary<string, string> sent, string topic)
    {
        lock (_gate)
            return sent.TryGetValue(topic, out var value) ? value : null;
    }

    private static Task PublishRawAsync(IMqttClient client, string topic, string payload, CancellationToken ct) =>
        client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag()
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build(), ct);

    /// <summary>
    /// Ends the session. Switched off — or moved to a different base topic — it first
    /// removes every entity it announced, so HA is left with nothing pointing at topics
    /// nobody will publish to again. A plain shutdown leaves them, marked offline, so they
    /// come back as they were when LabbyTwo does.
    /// </summary>
    private async Task StopSessionAsync(bool clearEverything)
    {
        var client = _client;
        var session = _session;
        if (client is null)
            return;

        lock (_gate)
            _client = null;

        try
        {
            if (client.IsConnected && session is not null)
            {
                using var window = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                if (clearEverything)
                {
                    List<string> topics;
                    lock (_gate)
                        topics = [.. _knownConfigs, .. _sentStates.Keys];
                    foreach (var topic in topics)
                        await PublishRawAsync(client, topic, "", window.Token);
                    log.LogInformation("Removed {Count} LabbyTwo entities from Home Assistant", topics.Count);
                }

                // A clean disconnect does not trigger the will, so say it ourselves.
                await PublishRawAsync(client, HaTopics.Availability(session), HaTopics.Offline, window.Token);
                await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), window.Token);
            }
        }
        catch (Exception ex)
        {
            log.LogInformation("Home Assistant link did not close cleanly: {Reason}", Describe(ex));
        }
        finally
        {
            client.Dispose();
            lock (_gate)
            {
                if (clearEverything)
                    _knownConfigs.Clear();
                _sentConfigs.Clear();
                _sentStates.Clear();
                _plan = null;
            }
            if (clearEverything)
                _session = null;
            Record(x => x with { Connected = false, ConnectedSince = null });
        }
    }

    /// <summary>
    /// Logs a failure and shows it on the Settings page. Never the settings themselves: the
    /// password stays out of every log line by not being handed to any of them.
    /// </summary>
    private void Fail(string message, bool warn)
    {
        if (warn)
            log.LogWarning("{Message}", message);
        else
            log.LogDebug("{Message}", message);
        Record(x => x with { Connected = _client?.IsConnected == true, LastError = message, LastErrorAt = DateTimeOffset.Now });
    }

    private void Record(Func<HaStatus, HaStatus> change)
    {
        lock (_gate)
            Volatile.Write(ref _status, change(Status));
        try
        {
            StatusChanged?.Invoke();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "A Home Assistant status listener threw");
        }
    }

    private static string Describe(Exception ex) => ex.GetBaseException().Message;

    public async ValueTask DisposeAsync()
    {
        await StopSessionAsync(clearEverything: false);
        _kicks.Writer.TryComplete();
    }
}
