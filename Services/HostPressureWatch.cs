using System.Text.Json;
using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// The early warning the NAS never gave: every thirty seconds, read the host's load, memory,
/// swap, iowait and pressure-stall figures from /proc, decide how hard it is working
/// (<see cref="PressureTracker"/>), and when it has been falling behind for minutes say so —
/// once — naming the containers doing it, with a button to pause the worst of them for a
/// couple of hours.
///
/// <para>Written after a QNAP with 4 cores locked up twice in a day: load 13, SMB shares
/// dropping, and Tdarr at 193% CPU having read 12.5 TB, found by hand with <c>docker stats</c>
/// once the box answered again. Everything here is aimed at that afternoon: the level would
/// have reached Strained five minutes into it, and the notice would have said "Busiest: tdarr
/// 193% CPU, 40 MB/s read".</para>
///
/// <para><b>It must never be load itself.</b> A handful of small /proc reads every thirty
/// seconds, all in memory; no database on the way (the readings reach history only if
/// somebody adds a "This host" connection, through the monitor's ordinary sample write); the
/// busiest containers come from the resource poller's last round, not a new request. The
/// only database touch is the settings it already shares with every other notice —
/// maintenance, quiet hours, mute windows, all cached — and only when there is a notice to
/// send, plus a write when somebody pauses a container from the health page.</para>
///
/// <para>Notices are held exactly as <see cref="SelfWatch"/>'s are: by maintenance, by a mute
/// window covering everything or naming "this-host", and by quiet hours; and sent when the
/// hold lifts if the NAS is still struggling. The episode bookkeeping is in memory, so a
/// restart in the middle of one may announce it once more.</para>
/// </summary>
public sealed class HostPressureWatch(
    HealthMonitor monitor,
    ConfigStore config,
    AppSettingsStore settings,
    MuteWindowStore mutes,
    AlertService alerts,
    RemediationActions actions,
    ChangeStore changes,
    ILogger<HostPressureWatch> log) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    /// <summary>What mute windows see this as: an alert about a connection called "this-host".</summary>
    public const string ConnectionId = "this-host";

    /// <summary>The settings row holding pauses waiting to be undone, so a restart still unpauses on time.</summary>
    public const string UnpauseKey = "host_pressure_unpause";

    /// <summary>How long the one-tap pause lasts.</summary>
    public static readonly TimeSpan PauseFor = TimeSpan.FromHours(2);

    /// <summary>The latest reading of the running watch, for the "This host" provider, which is made by reflection.</summary>
    public static HostPressureReport? Current { get; private set; }

    private readonly PressureTracker _tracker = new();
    private readonly SemaphoreSlim _pass = new(1, 1);
    private List<PendingUnpause>? _unpauses;

    /// <summary>Where /proc is. Settable for tests, which point it at sample files.</summary>
    public HostProc Proc { get; set; } = HostProc.Default;

    /// <summary>
    /// Where the busiest containers come from: the resource poller's last rounds, in memory.
    /// Settable so a test names its own without sharing the static readings with every other test.
    /// </summary>
    public Func<DateTimeOffset, IReadOnlyList<(ContainerResourceSnapshot Host, ContainerReading Reading)>> BusiestNow { get; set; } =
        now => ContainerResourceSnapshots.Busiest(3, now);

    /// <summary>The zone quiet hours are read in; the alert service's unless a test sets one.</summary>
    public TimeZoneInfo? Zone { get; set; }

    /// <summary>The last reading, for the health page and the phone view. Memory only.</summary>
    public HostPressureReport? Report { get; private set; }

    /// <summary>Raised after each pass, so open pages redraw.</summary>
    public event Action? Changed;

    /// <summary>Pauses that will be undone, soonest first.</summary>
    public IReadOnlyList<PendingUnpause> Unpauses => [.. (_unpauses ?? []).OrderBy(u => u.Until)];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Off Linux there is no /proc and nothing to watch; the page says so once.
        using var timer = new PeriodicTimer(Every);
        do
        {
            try
            {
                await PassAsync(DateTimeOffset.Now, null, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Could not check how hard the host is working");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// One pass: read, judge, unpause what is due, send what is owed. The clock and the
    /// readings are parameters so the whole path — sustain, the single notice, the hold — is
    /// tested without waiting or a Linux host.
    /// </summary>
    public async Task PassAsync(DateTimeOffset now, HostSignals? signals, CancellationToken ct)
    {
        await _pass.WaitAsync(ct);
        try
        {
            signals ??= Proc.Read(now);
            var qnap = await QnapAsync(ct);
            _tracker.Step(now, signals, qnap?.Cpu);

            var busiest = BusiestNow(now.ToUniversalTime());
            Report = new HostPressureReport(now, signals, _tracker.Level, _tracker.Raw, _tracker.Since, _tracker.Reasons,
                _tracker.SwapGrowthKb, qnap, busiest);
            Current = Report;

            await UnpauseDueAsync(now, ct);
            await DeliverAsync(now, busiest, ct);
        }
        finally
        {
            _pass.Release();
        }
        Changed?.Invoke();
    }

    /// <summary>The QNAP connection's own figures, from the monitor's last probe — memory only.</summary>
    private async Task<QnapFigures?> QnapAsync(CancellationToken ct)
    {
        var qnap = (await config.ConnectionsAsync(ct))
            .FirstOrDefault(c => c.Enabled && string.Equals(c.Provider, "qnap", StringComparison.OrdinalIgnoreCase));
        if (qnap is null || monitor.State(qnap.Id) is not { IsUp: true } state)
            return null;
        double? Get(string key) => state.Metrics.TryGetValue(key, out var value) ? value : null;
        return new QnapFigures(qnap.Name, Get("cpu_percent"), Get("ram_percent"));
    }

    private async Task DeliverAsync(DateTimeOffset now, IReadOnlyList<(ContainerResourceSnapshot Host, ContainerReading Reading)> busiest, CancellationToken ct)
    {
        if (!_tracker.StartOwed && !_tracker.ClearOwed)
            return;

        if (await HeldAsync(now, ct) is { } reason)
        {
            log.LogDebug("A notice about the host's load is waiting: {Reason}", reason);
            return;
        }

        var host = await HostNameAsync(ct);
        if (_tracker.StartOwed)
        {
            var named = busiest.Select(b => ContainerUsage.Describe(b.Reading)).ToList();
            var body = PressureTracker.Sentence(host, _tracker.Reasons, named);
            var worst = busiest.FirstOrDefault();
            if (worst.Reading is not null)
                body += $" Open LabbyTwo's health page to pause {worst.Reading.Name} for {PauseFor.TotalHours:0} hours.";

            log.LogWarning("{Body}", body);
            var sent = await alerts.BroadcastAsync(new Alert(AlertLevel.Down, $"{Capital(host)} is struggling", body)
            {
                Tag = "labbytwo:host-pressure",
                Link = worst.Reading is { } top
                    ? $"settings/health?pause={Uri.EscapeDataString(top.Name)}#host-pressure"
                    : "settings/health#host-pressure",
            }, null, now, ct);

            // Marked sent even when no channel took it: a notice with nowhere to go now will
            // have nowhere to go in thirty seconds either, and repeating the attempt every
            // pass would only fill the log. The health page and phone view still show it.
            _tracker.StartSent();
            if (sent == 0)
                log.LogInformation("No alert channel took the notice about {Host}", host);
        }

        if (_tracker.ClearOwed)
        {
            await alerts.BroadcastAsync(new Alert(AlertLevel.Up, $"{Capital(host)} has calmed down",
                $"The {host} is coping again — it had reached {PressureTracker.Word(_tracker.Worst).ToLowerInvariant()}.")
            {
                Tag = "labbytwo:host-pressure",
                Link = "settings/health#host-pressure",
            }, null, now, ct);
            _tracker.ClearSent();
        }
    }

    /// <summary>"NAS" when there is a NAS connection to say it of, "host" otherwise.</summary>
    private async Task<string> HostNameAsync(CancellationToken ct)
    {
        var connections = await config.ConnectionsAsync(ct);
        return connections.Any(c => c.Enabled && c.Provider is "qnap" or "synology" or "truenas") ? "NAS" : "host";
    }

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>Why a notice should wait, or null — the same holds as LabbyTwo's own health notices.</summary>
    public async Task<string?> HeldAsync(DateTimeOffset now, CancellationToken ct)
    {
        var bag = await settings.AllAsync(ct);
        var zone = Zone ?? alerts.Zone;
        if (Maintenance.From(bag, now) is { On: true } maintenance)
            return maintenance.Reason;
        if (MuteWindow.Muting(await mutes.AllAsync(ct), null, ConnectionId, now, zone) is { } window)
            return $"muted by {window.Name}";
        if (AlertPolicy.From(bag).IsQuiet(now, zone))
            return "quiet hours";
        return null;
    }

    // ---- pausing ---------------------------------------------------------------------

    /// <summary>A container paused from the health page, and when it will be unpaused.</summary>
    public sealed record PendingUnpause(string DockerConnectionId, string Container, DateTimeOffset Until);

    /// <summary>
    /// Pauses a container for <see cref="PauseFor"/>, through exactly the guardrails
    /// self-healing uses: never LabbyTwo's own container, and never one on a Containers
    /// page's protected list. Pause rather than stop because it is undone in a moment and
    /// loses nothing — a transcoder paused mid-file carries on from the same frame.
    /// </summary>
    /// <returns>What happened, in words, for the page.</returns>
    public async Task<ActionResult> PauseAsync(string dockerConnectionId, string container, DateTimeOffset now, CancellationToken ct)
    {
        var plan = await actions.PrepareContainerAsync(dockerConnectionId, container, ContainerAction.Pause, false,
            "the health page", "Host pressure", ct);
        if (plan.Refused is { } refused || plan.Run is null)
            return ActionResult.Failed(plan.Refused ?? "It cannot be paused.");

        var result = await plan.Run(ct);
        if (!result.Ok)
            return result;

        var until = now + PauseFor;
        await LoadUnpausesAsync(ct);
        _unpauses!.RemoveAll(u => u.DockerConnectionId == dockerConnectionId && u.Container.Equals(container, StringComparison.OrdinalIgnoreCase));
        _unpauses.Add(new PendingUnpause(dockerConnectionId, container, until));
        await SaveUnpausesAsync(ct);
        await NoteAsync(dockerConnectionId, container, $"Paused {container} for {PauseFor.TotalHours:0} hours",
            $"From the health page while the host was {PressureTracker.Word(_tracker.Level).ToLowerInvariant()}. It will be unpaused at {until.ToLocalTime():HH:mm}.", now, ct);
        Changed?.Invoke();
        return ActionResult.Done($"Paused {container} until {until.ToLocalTime():HH:mm}.");
    }

    /// <summary>Unpauses one now, ahead of time.</summary>
    public async Task<ActionResult> UnpauseAsync(string dockerConnectionId, string container, DateTimeOffset now, CancellationToken ct)
    {
        await LoadUnpausesAsync(ct);
        var result = await RunUnpauseAsync(dockerConnectionId, container, ct);
        if (result.Ok)
        {
            _unpauses!.RemoveAll(u => u.DockerConnectionId == dockerConnectionId && u.Container.Equals(container, StringComparison.OrdinalIgnoreCase));
            await SaveUnpausesAsync(ct);
            await NoteAsync(dockerConnectionId, container, $"Unpaused {container}", "Ahead of time, from the health page.", now, ct);
            Changed?.Invoke();
        }
        return result;
    }

    private async Task UnpauseDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        await LoadUnpausesAsync(ct);
        var due = _unpauses!.Where(u => u.Until <= now).ToList();
        if (due.Count == 0)
            return;

        foreach (var pause in due)
        {
            var result = await RunUnpauseAsync(pause.DockerConnectionId, pause.Container, ct);
            // Dropped whether or not it worked: a container somebody already unpaused, or
            // removed, answers with an error that retrying every thirty seconds will not fix.
            _unpauses!.Remove(pause);
            await NoteAsync(pause.DockerConnectionId, pause.Container,
                result.Ok ? $"Unpaused {pause.Container}" : $"Could not unpause {pause.Container}",
                result.Ok ? $"Its {PauseFor.TotalHours:0} hours were up." : result.Message, now, ct);
        }
        await SaveUnpausesAsync(ct);
    }

    private async Task<ActionResult> RunUnpauseAsync(string dockerConnectionId, string container, CancellationToken ct)
    {
        var plan = await actions.PrepareContainerAsync(dockerConnectionId, container, ContainerAction.Unpause, true,
            "the health page", "Host pressure", ct);
        return plan.Run is null ? ActionResult.Failed(plan.Refused ?? "It cannot be unpaused.") : await plan.Run(ct);
    }

    private async Task NoteAsync(string connectionId, string container, string title, string detail, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            await changes.RecordAsync(new Change(now, ChangeKinds.Remediation, "ran", connectionId, container, title, detail), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogDebug(ex, "Could not record \"{Title}\" in the feed", title);
        }
    }

    private async Task LoadUnpausesAsync(CancellationToken ct)
    {
        if (_unpauses is not null)
            return;
        try
        {
            var stored = (await settings.AllAsync(ct)).Get(UnpauseKey);
            _unpauses = stored.Length == 0 ? [] : JsonSerializer.Deserialize<List<PendingUnpause>>(stored) ?? [];
        }
        catch (JsonException)
        {
            _unpauses = [];
        }
    }

    private async Task SaveUnpausesAsync(CancellationToken ct)
    {
        try
        {
            await settings.SaveAsync(UnpauseKey, _unpauses is { Count: > 0 } list ? JsonSerializer.Serialize(list) : "", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not remember when to unpause a container");
        }
    }
}

/// <summary>QTS's own figures for the NAS, when there is a QNAP connection.</summary>
public sealed record QnapFigures(string Name, double? Cpu, double? Ram);

/// <summary>One pass of <see cref="HostPressureWatch"/>, as the pages show it.</summary>
public sealed record HostPressureReport(
    DateTimeOffset At,
    HostSignals Signals,
    PressureLevel Level,
    PressureLevel Raw,
    DateTimeOffset? Since,
    IReadOnlyList<string> Reasons,
    double? SwapGrowthKb,
    QnapFigures? Qnap,
    IReadOnlyList<(ContainerResourceSnapshot Host, ContainerReading Reading)> Busiest)
{
    /// <summary>"Strained — load 13.4 on 4 cores": the phone view's line.</summary>
    public string Line => Reasons.Count == 0
        ? PressureTracker.Word(Level)
        : $"{PressureTracker.Word(Level)} — {string.Join(", ", Reasons.Take(2))}";

    /// <summary>The level as a number for history and alert rules: 0 OK, 1 busy, 2 strained, 3 critical.</summary>
    public double LevelNumber => (int)Level;
}
