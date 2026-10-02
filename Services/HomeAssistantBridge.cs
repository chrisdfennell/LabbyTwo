using System.Globalization;
using System.Security.Cryptography;
using LabbyTwo.Core;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet;

namespace LabbyTwo.Services;

/// <summary>
/// LabbyTwo, published into Home Assistant over MQTT discovery: each connection a device
/// with a connectivity sensor and, if chosen, its metrics; the lab itself a hub device with
/// "anything down", "alert firing", maintenance, blindness, late backups and open incidents;
/// and, only when asked for, a few buttons.
///
/// The other direction already exists — LabbyTwo reads Home Assistant as a connection — and
/// this is deliberately not that. A connection is something LabbyTwo polls and draws; this
/// talks outwards only, and its failures go to the log and to the Settings page, never to
/// the alert channels, because one of those channels may well be Home Assistant itself.
///
/// The split: <see cref="HomeAssistantPublisher"/> owns the broker session and knows nothing
/// of the app; this class is the app's side — the saved settings, the lab read from memory,
/// the events that say something changed, and the buttons with their guardrails. Every
/// publish is built from what the monitor, the alert evaluator, the incident tracker and the
/// backup checks already hold; nothing on that path asks the database.
///
/// Off until switched on, and nothing is sent before then — not even a connection attempt.
/// </summary>
public sealed class HomeAssistantBridge : BackgroundService, IHomeAssistantSource
{
    public const string EnabledKey = "ha_mqtt_enabled";
    public const string HostKey = "ha_mqtt_host";
    public const string PortKey = "ha_mqtt_port";
    public const string TlsKey = "ha_mqtt_tls";
    public const string UsernameKey = "ha_mqtt_username";
    public const string PasswordKey = "ha_mqtt_password";
    public const string PrefixKey = "ha_mqtt_discovery_prefix";
    public const string BaseKey = "ha_mqtt_base_topic";
    public const string AllConnectionsKey = "ha_mqtt_all_connections";
    public const string ConnectionsKey = "ha_mqtt_connections";
    public const string ReadingsKey = "ha_mqtt_readings";
    public const string MetricsKey = "ha_mqtt_metrics";
    public const string MaintenanceButtonsKey = "ha_mqtt_maintenance_buttons";
    public const string ActionButtonsKey = "ha_mqtt_action_buttons";
    public const string RefreshKey = "ha_mqtt_refresh_minutes";

    /// <summary>How long "Start maintenance" from Home Assistant holds alerts for.</summary>
    public static readonly TimeSpan MaintenanceSpan = TimeSpan.FromHours(1);

    private const string SecretPrefix = "enc:";

    private readonly AppSettingsStore _settings;
    private readonly ConfigStore _config;
    private readonly Registry _registry;
    private readonly HealthMonitor _monitor;
    private readonly ActionRunner _runner;
    private readonly ChangeStore _changes;
    private readonly IServiceProvider _services;
    private readonly ILogger<HomeAssistantBridge> _log;
    private readonly IDataProtector _protector;

    private MetricAlertService? _alerts;
    private IncidentTracker? _incidents;
    private BackupProof? _backups;
    private LatestReadings? _latest;
    private int _incidentsLoading;

    public HomeAssistantBridge(
        AppSettingsStore settings,
        IDataProtectionProvider protection,
        ConfigStore config,
        Registry registry,
        HealthMonitor monitor,
        ActionRunner runner,
        ChangeStore changes,
        IServiceProvider services,
        ILogger<HomeAssistantBridge> log)
    {
        _settings = settings;
        _config = config;
        _registry = registry;
        _monitor = monitor;
        _runner = runner;
        _changes = changes;
        _services = services;
        _log = log;
        _protector = protection.CreateProtector("LabbyTwo.HomeAssistantMqtt");
        Publisher = new HomeAssistantPublisher(this, log);
    }

    /// <summary>The MQTT half. Exposed for its status, and so a test can shorten its waits.</summary>
    public HomeAssistantPublisher Publisher { get; private set; }

    /// <summary>For tests: a publisher with shorter waits, built against this bridge.</summary>
    public void UsePublisher(Func<IHomeAssistantSource, HomeAssistantPublisher> make) => Publisher = make(this);

    public HaStatus Status => Publisher.Status;

    // ---- Lifetime and events --------------------------------------------------------------

    public override Task StartAsync(CancellationToken ct)
    {
        // Resolved here rather than in the constructor: these are optional in a test host,
        // and between them they pull in half the app — the bridge should not be what decides
        // the order singletons are built in.
        _alerts = _services.GetService<MetricAlertService>();
        _incidents = _services.GetService<IncidentTracker>();
        _backups = _services.GetService<BackupProof>();
        _latest = _services.GetService<LatestReadings>();

        _monitor.Updated += Kick;
        _monitor.BlindnessChanged += OnBlindness;
        _config.Changed += Kick;
        _settings.Changed += Kick;
        if (_alerts is not null)
            _alerts.Updated += Kick;
        if (_incidents is not null)
            _incidents.OpenChanged += Kick;
        if (_backups is not null)
            _backups.Judged += Kick;
        if (_latest is not null)
            _latest.Changed += OnReading;

        return base.StartAsync(ct);
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        _monitor.Updated -= Kick;
        _monitor.BlindnessChanged -= OnBlindness;
        _config.Changed -= Kick;
        _settings.Changed -= Kick;
        if (_alerts is not null)
            _alerts.Updated -= Kick;
        if (_incidents is not null)
            _incidents.OpenChanged -= Kick;
        if (_backups is not null)
            _backups.Judged -= Kick;
        if (_latest is not null)
            _latest.Changed -= OnReading;
        await base.StopAsync(ct);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        // Its own task from the first line: the publisher's loop must never run on the
        // thread that is starting the host.
        Task.Run(() => Publisher.RunAsync(stoppingToken), stoppingToken);

    private void Kick() => Publisher.Kick();
    private void OnBlindness(Blindness before, Blindness after) => Publisher.Kick();
    private void OnReading(string connectionId) => Publisher.Kick();

    // ---- Settings -------------------------------------------------------------------------

    /// <summary>What is saved, with the password decrypted. One that no longer decrypts reads as blank.</summary>
    public async Task<HaSettings> SettingsAsync(CancellationToken ct = default) => From(await _settings.AllAsync(ct));

    private HaSettings From(SettingsBag bag)
    {
        var off = HaSettings.Off;
        return new HaSettings(
            bag.Get(EnabledKey) == "true",
            bag.Get(HostKey),
            int.TryParse(bag.Get(PortKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ? port : off.Port,
            bag.Get(TlsKey) == "true",
            bag.Get(UsernameKey),
            Unprotect(bag.Get(PasswordKey)),
            bag.Get(PrefixKey, off.DiscoveryPrefix),
            bag.Get(BaseKey, off.BaseTopic),
            bag.Get(AllConnectionsKey, "true") != "false",
            Lines(bag.Get(ConnectionsKey)),
            Enum.TryParse<HaReadings>(bag.Get(ReadingsKey), ignoreCase: true, out var readings) ? readings : off.Readings,
            Lines(bag.Get(MetricsKey)),
            bag.Get(MaintenanceButtonsKey) == "true",
            bag.Get(ActionButtonsKey) == "true",
            int.TryParse(bag.Get(RefreshKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out var refresh)
                ? refresh : off.RefreshMinutes);
    }

    private static HashSet<string> Lines(string value) =>
        [.. value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>Saves the lot. A blank password keeps the saved one; <paramref name="clearPassword"/> removes it.</summary>
    public async Task SaveAsync(HaSettings chosen, bool clearPassword = false, CancellationToken ct = default)
    {
        var saved = await SettingsAsync(ct);
        var password = clearPassword ? "" : chosen.Password.Length > 0 ? chosen.Password : saved.Password;
        await _settings.SaveAsync(new Dictionary<string, string>
        {
            [EnabledKey] = chosen.Enabled ? "true" : "false",
            [HostKey] = chosen.Host.Trim(),
            [PortKey] = Math.Clamp(chosen.Port, 1, 65535).ToString(CultureInfo.InvariantCulture),
            [TlsKey] = chosen.Tls ? "true" : "false",
            [UsernameKey] = chosen.Username.Trim(),
            [PasswordKey] = Protect(password),
            [PrefixKey] = HaTopics.CleanTopic(chosen.DiscoveryPrefix, HaSettings.DefaultDiscoveryPrefix),
            [BaseKey] = HaTopics.CleanTopic(chosen.BaseTopic, HaSettings.DefaultBaseTopic),
            [AllConnectionsKey] = chosen.AllConnections ? "true" : "false",
            [ConnectionsKey] = string.Join('\n', chosen.Connections.Order(StringComparer.Ordinal)),
            [ReadingsKey] = chosen.Readings.ToString(),
            [MetricsKey] = string.Join('\n', chosen.Metrics.Order(StringComparer.Ordinal)),
            [MaintenanceButtonsKey] = chosen.MaintenanceButtons ? "true" : "false",
            [ActionButtonsKey] = chosen.ActionButtons ? "true" : "false",
            [RefreshKey] = Math.Clamp(chosen.RefreshMinutes, 1, 1440).ToString(CultureInfo.InvariantCulture),
        }, ct);
    }

    /// <summary>
    /// The Settings page's button: connect, say hello, leave. Uses the form as it stands, so
    /// a broker can be tried before anything is saved — and sends nothing but the connect.
    /// </summary>
    public static async Task<(bool Ok, string Message)> TestAsync(HaSettings s, CancellationToken ct)
    {
        if (s.Host.Trim().Length == 0)
            return (false, "Give the broker's address first.");

        using var client = new MqttClientFactory().CreateMqttClient();
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(s.Host.Trim(), s.Port)
            .WithClientId($"labbytwo-ha-test-{Guid.NewGuid():n}"[..32])
            .WithCleanSession()
            .WithTimeout(TimeSpan.FromSeconds(10));
        if (s.Username.Length > 0)
            builder = builder.WithCredentials(s.Username, s.Password);
        if (s.Tls)
            builder = builder.WithTlsOptions(o => o.UseTls().WithCertificateValidationHandler(_ => true));

        try
        {
            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(TimeSpan.FromSeconds(15));
            await client.ConnectAsync(builder.Build(), window.Token);
            await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), window.Token);
            return (true, $"Connected to {s.Host.Trim()}:{s.Port}.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return (false, $"Could not connect to {s.Host.Trim()}:{s.Port}: {ex.GetBaseException().Message}");
        }
    }

    private string Protect(string value) => value.Length == 0 ? "" : SecretPrefix + _protector.Protect(value);

    private string Unprotect(string value)
    {
        if (!value.StartsWith(SecretPrefix, StringComparison.Ordinal))
            return "";
        try
        {
            return _protector.Unprotect(value[SecretPrefix.Length..]);
        }
        catch (CryptographicException)
        {
            return "";
        }
    }

    // ---- The lab, from memory -------------------------------------------------------------

    /// <summary>
    /// Every monitored connection with its status, metrics and safe actions, and the lab-wide
    /// facts. The connections and the settings come from caches that the stores keep; the
    /// rest is what each service already holds. The one exception is the open incidents
    /// straight after a start, before the tracker has loaded them: that load is started in
    /// the background, counted as none meanwhile, and the tracker's event republishes.
    /// </summary>
    public async Task<HaLab> LabAsync(CancellationToken ct = default)
    {
        var connections = await _config.ConnectionsAsync(ct);
        var bag = await _settings.AllAsync(ct);
        var now = DateTimeOffset.Now;
        var maintenance = Maintenance.From(bag, now);

        var monitored = connections.Where(_monitor.IsMonitored).ToList();
        var views = new List<HaConnectionView>(monitored.Count);
        var states = new List<HealthMonitor.ProbeState>();
        foreach (var connection in monitored)
        {
            var state = _monitor.State(connection.Id);
            if (state is not null)
                states.Add(state);

            // Live readings from the last probe; straight after a restart, before the first
            // sweep, the newest stored ones that LatestReadings keeps in memory.
            IReadOnlyDictionary<string, double> readings = state?.Metrics is { Count: > 0 } live
                ? live
                : _latest?.Get(connection.Id, TimeSpan.FromDays(1)) ?? new Dictionary<string, double>();

            views.Add(new HaConnectionView(
                connection.Id,
                connection.Name,
                _registry.Provider(connection.Provider)?.DisplayName ?? connection.Provider,
                state?.IsUp,
                state?.ChangedAt,
                state?.At,
                state?.CantCheck ?? state?.Message ?? "",
                connection.IsSilenced(now),
                state?.CantCheck is not null,
                [.. readings.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => new HaMetricView(_registry.Metric(connection, r.Key), r.Value))],
                [.. _runner.ActionsFor(connection).Where(a => HaControls.Refusal(a, connection) is null)]));
        }

        var open = _incidents?.Open;
        if (open is null && _incidents is not null && Interlocked.Exchange(ref _incidentsLoading, 1) == 0)
        {
            var tracker = _incidents;
            _ = Task.Run(async () =>
            {
                try
                {
                    await tracker.OpenAsync(CancellationToken.None);
                    Publisher.Kick();
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Could not load the open incidents for Home Assistant");
                    Interlocked.Exchange(ref _incidentsLoading, 0);
                }
            });
        }

        return new HaLab(
            UpdateChecker.Installed is { Length: > 0 } version ? version : "dev",
            views,
            OutsideAlarm.Summary(states),
            states.Any(s => s.IsUp == false),
            _alerts?.Firing.Count > 0,
            maintenance,
            _monitor.Blindness,
            _backups?.Latest?.Any(r => r.Status.State == BackupState.Late) == true,
            open?.Count ?? 0);
    }

    // ---- Buttons --------------------------------------------------------------------------

    /// <summary>
    /// One press, checked again here whatever the published buttons said: the settings may
    /// have changed since, the connection may have gone, and a topic can be published to by
    /// anything on the broker, not only Home Assistant. Every press is written to the change
    /// feed — run, failed, refused or ignored — so "who rebooted that?" always has an answer.
    /// </summary>
    public async Task<string> CommandAsync(HaCommand command, HaSettings s, bool retained, CancellationToken ct)
    {
        var now = DateTimeOffset.Now;
        string? connectionId = null;
        string title;
        string action;
        string detail;

        if (retained)
        {
            title = $"Home Assistant: ignored a saved press of {command.Key}";
            action = ChangeActions.Skipped;
            detail = "The message was retained on the broker, so it was an old press being replayed rather than a new one. " +
                     "Buttons are published without retain; something else on the broker sent this.";
        }
        else
        {
            switch (command.Kind)
            {
                case HaCommandKind.MaintenanceStart or HaCommandKind.MaintenanceEnd when !s.MaintenanceButtons:
                    title = "Home Assistant: refused a maintenance button";
                    action = ChangeActions.Skipped;
                    detail = "The maintenance buttons are switched off on the Settings page.";
                    break;

                case HaCommandKind.MaintenanceStart:
                    await _settings.SaveAsync(Maintenance.Key, Maintenance.Value(MaintenanceSpan, now), ct);
                    title = "Home Assistant started maintenance for an hour";
                    action = ChangeActions.Started;
                    detail = $"All alerts are held until {(now + MaintenanceSpan).ToLocalTime():HH:mm}.";
                    break;

                case HaCommandKind.MaintenanceEnd:
                    await _settings.SaveAsync(Maintenance.Key, Maintenance.Cleared, ct);
                    title = "Home Assistant ended maintenance";
                    action = ChangeActions.Stopped;
                    detail = "Alerts are being sent again.";
                    break;

                default:
                    (connectionId, title, action, detail) = await RunActionAsync(command, s, ct);
                    break;
            }
        }

        _log.LogInformation("{Title}", title);
        try
        {
            await _changes.RecordAsync(new Change(now, ChangeKinds.HomeAssistant, action, connectionId, command.Key, title, detail), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Could not record a Home Assistant command in the change feed");
        }
        return title;
    }

    private async Task<(string? ConnectionId, string Title, string Action, string Detail)> RunActionAsync(
        HaCommand command, HaSettings s, CancellationToken ct)
    {
        var connections = await _config.ConnectionsAsync(ct);
        var connection = connections.FirstOrDefault(c => HaTopics.Id(c.Id) == command.ConnectionId);
        if (!s.ActionButtons)
            return (connection?.Id, "Home Assistant: refused an action button", ChangeActions.Skipped,
                "Action buttons are switched off on the Settings page.");
        if (connection is null || !_monitor.IsMonitored(connection) || !s.Publishes(connection.Id))
            return (connection?.Id, "Home Assistant: refused an action on a connection it is not given", ChangeActions.Skipped,
                $"No published connection matches “{command.ConnectionId}”.");

        var found = _runner.ActionsFor(connection).FirstOrDefault(a => HaTopics.Id(a.Id) == command.ActionId);
        if (found is null)
            return (connection.Id, $"Home Assistant: refused an unknown action on {connection.Name}", ChangeActions.Skipped,
                $"{connection.Name} does not offer “{command.ActionId}” with its current settings.");
        if (HaControls.Refusal(found, connection) is { } why)
            return (connection.Id, $"Home Assistant: refused “{found.Label}” on {connection.Name}", ChangeActions.Skipped, why);

        var result = await _runner.RunAsync(connection, found, new SettingsBag(), ct);
        return result.Ok
            ? (connection.Id, $"Home Assistant ran “{found.Label}” on {connection.Name}", ChangeActions.Completed, result.Message)
            : (connection.Id, $"Home Assistant could not run “{found.Label}” on {connection.Name}", ChangeActions.Failed, result.Message);
    }

    public override void Dispose()
    {
        // Synchronous by contract; the publisher's goodbye is best effort on the way out,
        // and the broker's will covers a goodbye that never arrives.
        Publisher.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        base.Dispose();
    }
}
