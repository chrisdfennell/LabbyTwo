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
public sealed class HealthMonitor(
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
        IReadOnlyDictionary<string, string> Details);

    /// <summary>Fires after every sweep so open pages can re-render.</summary>
    public event Action? Updated;

    /// <summary>
    /// Fires only when a connection actually changes state, which is what alerting wants —
    /// a sweep that found everything the same as last time is not news.
    /// </summary>
    public event Func<StatusChange, Task>? StatusChanged;

    public sealed record StatusChange(Connection Connection, bool IsUp, string Message, TimeSpan? PreviousDuration);

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
        try
        {
            await RestoreAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
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
                await SweepAsync(stoppingToken);
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
        var sampled = await history.LastSampleAtAsync(ct);
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

            _states[connection.Id] = new ProbeState(
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
                new Dictionary<string, string>());
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

        // Probes are independent and mostly waiting on the network; running them together
        // keeps a sweep as slow as the slowest host rather than the sum of all of them.
        await Task.WhenAll(due.Select(connection => ProbeAndRecordAsync(connection, ct)));

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

    private async Task ProbeAndRecordAsync(Connection connection, CancellationToken ct)
    {
        var result = await ProbeAsync(connection, ct);
        var previous = State(connection.Id);
        var now = DateTimeOffset.Now;

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

        if (result.Metrics is { Count: > 0 } metrics)
        {
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
            await history.RecordStatusAsync(connection.Id, isUp ?? false, result.Message, ct);

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
            await history.RecordStatusAsync(connection.Id, isUp ?? false, result.Message, ct);
        }
    }

    /// <summary>
    /// One probe, right now — the Test button when adding a connection. Runs the exact
    /// code path the monitor runs, so a green test means monitoring will work too.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var provider = registry.Provider(connection.Provider);
        if (provider is null)
            return ProbeResult.Down(TimeSpan.Zero, $"No provider named \"{connection.Provider}\" is installed.");
        try
        {
            return await provider.ProbeAsync(connection, ct);
        }
        catch (Exception ex)
        {
            // A provider that throws is a bug in the provider, not a reason to lose the sweep.
            log.LogError(ex, "Provider {Provider} threw while probing {Connection}", connection.Provider, connection.Name);
            return ProbeResult.Down(TimeSpan.Zero, ex.GetBaseException().Message);
        }
    }

    /// <summary>Probes one connection immediately and folds the result into the live state.</summary>
    public async Task RefreshAsync(Connection connection, CancellationToken ct = default)
    {
        await ProbeAndRecordAsync(connection, ct);
        Updated?.Invoke();
    }
}
