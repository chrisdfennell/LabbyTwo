using System.Collections.Concurrent;
using LabbyTwo.Core;
using LabbyTwo.Storage;
using Microsoft.Extensions.Options;

namespace LabbyTwo.Services;

/// <summary>
/// Polls every enabled connection on a timer and keeps the latest result in memory for
/// the UI. It has no idea what any provider does — it calls <see cref="IConnectionProvider.ProbeAsync"/>
/// and stores whatever comes back, which is what lets a new integration light up tiles,
/// charts and uptime with no changes here.
/// </summary>
public sealed partial class HealthMonitor(
    ConfigStore config,
    Registry registry,
    HistoryStore history,
    IOptions<LabbyOptions> options,
    ILogger<HealthMonitor> log) : BackgroundService
{
    private readonly ConcurrentDictionary<string, ProbeState> _states = new();
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    /// <summary>
    /// <see cref="IsUp"/> is null until a connection has been probed once, which the UI
    /// shows as "checking" rather than a misleading green or red.
    /// </summary>
    public sealed record ProbeState(
        string ConnectionId,
        bool? IsUp,
        string Message,
        TimeSpan Duration,
        DateTimeOffset At,
        DateTimeOffset? ChangedAt,
        int ConsecutiveFailures,
        IReadOnlyDictionary<string, double> Metrics,
        IReadOnlyDictionary<string, string> Details)
    {
        /// <summary>
        /// Set when the last probe failed while LabbyTwo could not see the lab (see
        /// <see cref="Blindness"/>): what that probe said. Everything else in the state is
        /// the last thing known before, kept rather than overwritten with a failure that was
        /// LabbyTwo's own. Null for an ordinary probe.
        /// </summary>
        public string? CantCheck { get; init; }
    }

    /// <summary>Fires after every sweep so open pages can re-render.</summary>
    public event Action? Updated;

    /// <summary>
    /// Fires only when a connection actually changes state, which is what alerting wants —
    /// a sweep that found everything the same as last time is not news.
    /// </summary>
    public event Func<StatusChange, Task>? StatusChanged;

    public sealed record StatusChange(Connection Connection, bool IsUp, string Message, TimeSpan? PreviousDuration);

    /// <summary>
    /// Fires after every probe with what it found, before the sweep's <see cref="Updated"/>.
    /// For the change detectors that read what a probe reported rather than whether it was
    /// up — a certificate's serial, a scan's new devices — and so need each probe, not only
    /// the ones that changed a status. Listeners must be quick and must not throw; anything
    /// slow belongs on a task of its own.
    /// </summary>
    public event Action<Connection, ProbeState>? Probed;

    /// <summary>Whether this connection is something the monitor polls at all.</summary>
    public bool IsMonitored(Connection connection) =>
        connection.Enabled && registry.Provider(connection.Provider)?.IsMonitored != false;

    /// <summary>
    /// Whether enough time has passed to ask this one again. Always true for the great
    /// majority, which declare no minimum; always true for a connection never probed, so
    /// a restart does not leave one blank for a quarter of an hour.
    /// </summary>
    public bool IsDue(Connection connection, DateTimeOffset now) => IsDue(
        registry.Provider(connection.Provider)?.MinimumIntervalFor(connection) ?? TimeSpan.Zero,
        State(connection.Id)?.At,
        now,
        TimeSpan.FromSeconds(Math.Clamp(options.Value.ProbeSeconds, 5, 3600)));

    /// <summary>
    /// The decision on its own, so it can be reasoned about without a database or a clock.
    /// </summary>
    /// <param name="lastProbe">Null for a connection never probed in this process.</param>
    public static bool IsDue(TimeSpan minimum, DateTimeOffset? lastProbe, DateTimeOffset now, TimeSpan sweep)
    {
        if (minimum <= TimeSpan.Zero || lastProbe is not { } last)
            return true;

        // Half a sweep of slack. Sweeps land on their own rhythm, not on this one, so
        // without it a 15-minute minimum checked every 30 seconds falls a moment short on
        // the tick that should fire, waits a whole sweep more, and drifts a little further
        // every time — 15 minutes becomes 15:30, then 16:00.
        return now - last >= minimum - (sweep / 2);
    }

    private bool IsDue(Connection connection) => IsDue(connection, DateTimeOffset.Now);

    public ProbeState? State(string connectionId) =>
        _states.TryGetValue(connectionId, out var state) ? state : null;

    public IReadOnlyCollection<ProbeState> Snapshot => [.. _states.Values];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        NoteStarted();
        try
        {
            // Bounded, because nothing is watched until it returns. Restoring is a
            // courtesy — it saves the dashboard saying "checking" after a restart — and a
            // courtesy must never be the reason monitoring does not start.
            //
            // On the thread pool, not called directly. Microsoft.Data.Sqlite's async calls
            // run synchronously, so RestoreAsync(...).WaitAsync(deadline) ran the whole
            // restore on this thread before WaitAsync was even reached: the deadline was
            // attached to a task that had already finished. On a 580 MB database on NAS
            // disks that was sixteen minutes of every connection saying "checking" after
            // each restart. Task.Run makes it a task that is still running when the clock
            // starts, so the deadline can actually end the wait. If it does, the restore
            // carries on in the background and fills in only what the sweeps have not.
            await Task.Run(() => RestoreAsync(stoppingToken), stoppingToken)
                .WaitAsync(RestoreDeadline, stoppingToken);
            NoteRestored(RestoreOutcome.Completed, null);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (TimeoutException)
        {
            NoteRestored(RestoreOutcome.TimedOut, null);
            log.LogWarning("Restoring the last known status took longer than {Seconds}s; monitoring starts without it",
                RestoreDeadline.TotalSeconds);
        }
        catch (Exception ex)
        {
            NoteRestored(RestoreOutcome.Failed, ex.GetBaseException().Message);
            // Starting from nothing is the old behaviour, not a reason not to start.
            log.LogWarning(ex, "Could not restore the last known status of anything");
        }

        // Let the app finish starting before the first sweep, so the dashboard paints
        // immediately instead of waiting behind a dozen network timeouts.
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        var period = TimeSpan.FromSeconds(Math.Clamp(options.Value.ProbeSeconds, 5, 3600));
        using var timer = new PeriodicTimer(period);
        do
        {
            try
            {
                await TimedSweepAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Probe sweep failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// Loads what the monitor knew when it was last stopped. Called once before the first
    /// sweep, and public so it can be exercised without starting a background service and
    /// waiting on real time.
    ///
    /// Probe state lives in memory, so without this every restart is a blank slate — and a
    /// blank slate is not neutral: the dashboard says "checking" for things it has watched
    /// for months, a service that was down while the app was off comes back as a first
    /// observation rather than a recovery, and every connection that declares a
    /// MinimumInterval because its upstream meters requests is due at once, which turns a
    /// restart loop into a quota loop.
    ///
    /// Nothing here probes anything. It only restores what was already recorded, so the
    /// first sweep compares against the truth rather than against nothing.
    /// </summary>
    public async Task RestoreAsync(CancellationToken ct = default)
    {
        var connections = (await config.ConnectionsAsync(ct)).Where(IsMonitored).ToList();
        if (connections.Count == 0)
            return;

        var status = await history.LatestStatusAsync(ct);
        var sampled = await history.LastSampleAtAsync([.. connections.Select(c => c.Id)], ct);
        var threshold = Math.Max(1, options.Value.FailuresBeforeDown);
        var restored = 0;

        foreach (var connection in connections)
        {
            if (!status.TryGetValue(connection.Id, out var last))
                continue;

            // When it was last actually asked, which is what IsDue needs. A probe that
            // reports numbers leaves a sample behind; one that only reports up or down
            // leaves the status event and nothing else — and that event is as old as the
            // last change, not the last probe. Both are lower bounds, so take the later,
            // and being too early only ever costs one extra probe.
            var lastProbe = sampled.TryGetValue(connection.Id, out var at) && at > last.At ? at : last.At;

            // TryAdd, not assignment: when the deadline let sweeps start first, a probe has
            // already said something newer than the last recorded event, and a restore
            // finishing late must not put the old answer back over it.
            var added = _states.TryAdd(connection.Id, new ProbeState(
                connection.Id,
                last.IsUp,
                last.Message,
                // Not recorded, and a restored state is a status rather than a measurement.
                // Nothing on a page reads it; the metrics endpoint does, so for the couple
                // of seconds before the first sweep a scrape sees a zero-length probe. The
                // alternative is a nullable that every caller has to unwrap for ever.
                TimeSpan.Zero,
                lastProbe,
                // status_events holds transitions, so the event's time is when this state
                // began — which is what lets a recovery still say how long it was down.
                last.At,
                // It was already DOWN, so it had passed the threshold before the restart.
                // Counting from zero again would make the first failed probe look like a
                // wobble and delay the recovery notice by a sweep.
                last.IsUp ? 0 : threshold,
                new Dictionary<string, double>(),
                new Dictionary<string, string>()));
            if (added)
                restored++;
        }

        if (restored == 0)
            return;

        log.LogInformation("Restored the last known status of {Count} connection(s)", restored);

        // Only now, and only if something changed: a page that repaints to show exactly
        // what it already showed is a wasted render on every start.
        Updated?.Invoke();
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var connections = (await config.ConnectionsAsync(ct)).Where(IsMonitored).ToList();

        // Forget state for connections that were deleted, or a stale tile would keep
        // reporting a service that no longer exists.
        foreach (var id in _states.Keys.Where(id => connections.All(c => c.Id != id)))
            _states.TryRemove(id, out _);

        // Some upstreams publish on a schedule and meter how often you ask — a forecast is
        // recomputed hourly, so asking every thirty seconds gets the same answer 119 times
        // and a quota error on the 120th. Those declare a MinimumInterval and are skipped
        // until it has passed, which leaves their last real reading in place rather than
        // replacing it with a cached one dressed up as a new measurement.
        var due = connections.Where(IsDue).ToList();
        NoteSweepSize(due.Count);

        // Probes are independent and mostly waiting on the network; running them together
        // keeps a sweep as slow as the slowest host rather than the sum of all of them.
        var probed = await Task.WhenAll(due.Select(connection => TrackedProbeAsync(connection, ct)));

        // Every answer is in before any is recorded, because what one failure means depends
        // on the others: half the lab failing DNS in the same thirty seconds is LabbyTwo
        // unable to see, not half the lab down, and has to be known before the first of
        // them is turned red, alerted on or opened as an incident. Probes are bounded by
        // ProbeDeadline, so this waits forty seconds at worst.
        TimeSpan? lastSweep;
        lock (_bookkeeping)
            lastSweep = _lastSweepDuration;
        var blindness = Judge(
            [.. probed.Select(p => new ProbeOutcome(p.Connection.Id, p.Result.Ok, p.Failure))], lastSweep, DateTimeOffset.Now);

        await Task.WhenAll(probed.Select(p => RecordProbeAsync(p.Connection, p.Result, blindness, ct)));

        Updated?.Invoke();

        if (DateTimeOffset.UtcNow - _lastPrune > TimeSpan.FromHours(1))
        {
            _lastPrune = DateTimeOffset.UtcNow;
            try
            {
                await history.PruneAsync(ct);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Pruning old samples failed");
            }
        }
    }

    /// <summary>
    /// Folds one probe's answer into the live state, records it and tells whoever listens.
    /// While LabbyTwo cannot see (<paramref name="blindness"/>), a failure is not recorded
    /// as one: the connection keeps what was last known about it, marked
    /// <see cref="ProbeState.CantCheck"/>, and its run of failures does not grow — so nothing
    /// turns red, nothing is alerted on, and when sight comes back it takes the usual
    /// number of real failures to call it down. A success is always recorded: an answer
    /// is proof whatever else is going on.
    /// </summary>
    private async Task RecordProbeAsync(Connection connection, ProbeResult result, Blindness blindness, CancellationToken ct)
    {
        var previous = State(connection.Id);
        var now = DateTimeOffset.Now;

        if (!result.Ok && blindness.Impaired)
        {
            _states[connection.Id] = (previous is null
                ? new ProbeState(connection.Id, null, result.Message, result.Duration, now, null, 0,
                    new Dictionary<string, double>(), new Dictionary<string, string>())
                : previous with { Duration = result.Duration, At = now }) with { CantCheck = result.Message };
            return;
        }

        // A single failed probe is usually a dropped packet, not an outage. Only flip to
        // DOWN after N in a row; recovery is immediate, since one good response proves it.
        var failures = result.Ok ? 0 : (previous?.ConsecutiveFailures ?? 0) + 1;
        var threshold = Math.Max(1, options.Value.FailuresBeforeDown);
        bool? isUp = result.Ok ? true : failures >= threshold ? false : previous?.IsUp ?? true;

        var changed = previous?.IsUp != isUp;
        var state = new ProbeState(
            connection.Id, isUp, result.Message, result.Duration, now,
            changed ? now : previous?.ChangedAt,
            failures,
            result.Metrics ?? new Dictionary<string, double>(),
            result.Details ?? new Dictionary<string, string>());
        _states[connection.Id] = state;

        try
        {
            Probed?.Invoke(connection, state);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "A probe listener threw for {Connection}", connection.Name);
        }

        if (result.Metrics is { Count: > 0 } reported)
        {
            // Live state keeps everything; history only what the provider says is new.
            IReadOnlyDictionary<string, double> metrics = result.NotRecorded is { Count: > 0 } skip
                ? reported.Where(pair => !skip.Contains(pair.Key)).ToDictionary()
                : reported;
            try
            {
                await history.RecordAsync(connection.Id, metrics, ct);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Could not record samples for {Connection}", connection.Name);
            }
        }

        if (changed && previous is not null)
        {
            log.Log(isUp == true ? LogLevel.Information : LogLevel.Warning,
                "{Connection} is {Status}: {Message}", connection.Name, isUp == true ? "UP" : "DOWN", result.Message);
            await RecordStatusAsync(connection, isUp ?? false, result.Message, ct);

            // How long the previous state lasted, so a recovery notice can say "was down
            // for 6 minutes" rather than just "is back".
            var lasted = previous.ChangedAt is { } since ? now - since : (TimeSpan?)null;
            var change = new StatusChange(connection, isUp ?? false, result.Message, lasted);

            // Invoking a multicast Func<T,Task> directly would return only the last
            // subscriber's task and leave the rest unawaited, so walk the list explicitly.
            foreach (var handler in StatusChanged?.GetInvocationList() ?? [])
            {
                try
                {
                    await ((Func<StatusChange, Task>)handler)(change);
                }
                catch (Exception ex)
                {
                    // A failing notifier must not stop the sweep or lose the recorded history.
                    log.LogError(ex, "A status-change handler threw for {Connection}", connection.Name);
                }
            }
        }
        else if (previous is null)
        {
            // A connection this process has never seen — added a moment ago, or one whose
            // history was never written. Record it so uptime has a starting point, with no
            // alert, since there is nothing it changed from. This is no longer the path a
            // restart takes: RestoreAsync has already restored what was known, so a
            // service that was up before and is up now stays quiet, and one that went down
            // while the app was off is reported above as the change it really is. The
            // write is conditional in the store either way, so a repeat costs a row of
            // nothing rather than a state change that never happened.
            await RecordStatusAsync(connection, isUp ?? false, result.Message, ct);
        }
    }

    /// <summary>
    /// Writes the status event, and survives not being able to. By the time this runs the
    /// in-memory state has already moved on, so a throw here was not a retry later — the
    /// next sweep sees no change and the alert for this one is simply never sent. It also
    /// failed the whole sweep's WhenAll, which skipped the Updated event and the prune for
    /// every other connection. A missing row in the timeline is the smaller loss, so it is
    /// logged and the handlers still run, the same bargain RecordAsync makes for samples.
    /// </summary>
    private async Task RecordStatusAsync(Connection connection, bool isUp, string message, CancellationToken ct)
    {
        try
        {
            await history.RecordStatusAsync(connection.Id, isUp, message, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Could not record the status of {Connection}", connection.Name);
        }
    }

    /// <summary>
    /// One probe, right now — the Test button when adding a connection. Runs the exact
    /// code path the monitor runs, so a green test means monitoring will work too.
    /// </summary>
    /// <summary>How long startup waits to restore the last known status before sweeping anyway.</summary>
    public TimeSpan RestoreDeadline { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Longest a single probe may take. Past the HTTP client's thirty seconds, so an
    /// ordinary timeout still reports its own clearer message first.
    /// </summary>
    public TimeSpan ProbeDeadline { get; set; } = DefaultProbeDeadline;

    /// <summary>
    /// <see cref="ProbeDeadline"/> as it ships, named so a provider with a time budget of
    /// its own — the multi-step check — can be held to finishing inside it.
    /// </summary>
    public static readonly TimeSpan DefaultProbeDeadline = TimeSpan.FromSeconds(40);

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
        (await ProbeClassifiedAsync(connection, ct)).Result;

    /// <summary>
    /// The probe, and what kind of failure it was: by the exception, as
    /// <see cref="ProbeError.Describe"/> saw it on this probe's own async flow, and only
    /// when nothing was described by the words of the message.
    /// </summary>
    private async Task<(ProbeResult Result, ProbeFailure Failure)> ProbeClassifiedAsync(Connection connection, CancellationToken ct)
    {
        using var capture = ProbeError.Capture();
        var (result, failure) = await ProbeCapturedAsync(connection, ct);
        if (result.Ok)
            return (result, ProbeFailure.None);
        if (failure == ProbeFailure.None)
            failure = capture.Kind != ProbeFailure.None ? capture.Kind : ProbeError.ClassifyMessage(result.Message);
        return (result, failure);
    }

    private async Task<(ProbeResult Result, ProbeFailure Failure)> ProbeCapturedAsync(Connection connection, CancellationToken ct)
    {
        var provider = registry.Provider(connection.Provider);
        if (provider is null)
            return (ProbeResult.Down(TimeSpan.Zero, $"No provider named \"{connection.Provider}\" is installed."), ProbeFailure.Other);
        // A deadline of the monitor's own. The HTTP client gives up after thirty seconds,
        // but not every probe is HTTP — MQTT, SSH, sockets, a plugin's own client — and the
        // sweep waits for every probe before it records any of them. One that never
        // answered used to hold every other connection's status where it was, for good.
        // The token asks the provider to stop; WaitAsync stops waiting for one that does
        // not listen.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ProbeDeadline);
        try
        {
            return (await provider.ProbeAsync(connection, deadline.Token).WaitAsync(ProbeDeadline, ct), ProbeFailure.None);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
                                   (ex is TimeoutException || deadline.IsCancellationRequested))
        {
            log.LogWarning("Probing {Connection} took longer than {Seconds}s and was abandoned",
                connection.Name, ProbeDeadline.TotalSeconds);
            return (ProbeResult.Down(ProbeDeadline,
                $"{ProbeError.AbandonedPrefix}{ProbeDeadline.TotalSeconds:0} seconds, so this check was abandoned."),
                ProbeFailure.Abandoned);
        }
        catch (Exception ex)
        {
            // A provider that throws is a bug in the provider, not a reason to lose the sweep.
            log.LogError(ex, "Provider {Provider} threw while probing {Connection}", connection.Provider, connection.Name);
            return (ProbeResult.Down(TimeSpan.Zero, ex.GetBaseException().Message), ProbeError.Classify(ex));
        }
    }

    /// <summary>
    /// Probes one connection immediately and folds the result into the live state. Judged
    /// by whether LabbyTwo could see as of the last sweep: one probe on its own is not
    /// enough to say.
    /// </summary>
    public async Task RefreshAsync(Connection connection, CancellationToken ct = default)
    {
        var (result, _) = await ProbeClassifiedAsync(connection, ct);
        await RecordProbeAsync(connection, result, Blindness, ct);
        Updated?.Invoke();
    }
}
