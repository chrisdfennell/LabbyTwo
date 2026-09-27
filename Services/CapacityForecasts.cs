using System.Collections.Concurrent;
using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// When every capacity metric on every connection runs out, worked out in the background
/// and held in memory for the "Running out" card, the NAS card and the alert rules.
///
/// Computed here, on a timer, rather than by the cards: a forecast reads a fortnight of
/// history, and SQLite is synchronous underneath its async API, so a card doing that while
/// the page is drawn would block the circuit for as long as the query took — the mistake
/// <see cref="LatestReadings"/> exists to avoid. Every quarter of an hour is plenty for a
/// trend measured in weeks.
///
/// Published as a derived metric (<see cref="CapacityMetric"/>) that the alert rules read
/// from here, never written to the samples table. Recorded history is measurements; a
/// forecast is an opinion about them, and one stored alongside would be charted,
/// averaged and forecast in turn as though somebody had measured it.
/// </summary>
public sealed class CapacityForecasts(
    ConfigStore config,
    Registry registry,
    HistoryStore history,
    ILogger<CapacityForecasts> log) : IBackgroundJob
{
    /// <summary>One metric's forecast on one connection.</summary>
    public sealed record Entry(string ConnectionId, string Metric, CapacityForecast Forecast, DateTimeOffset At);

    private readonly ConcurrentDictionary<(string ConnectionId, string Metric), Entry> _entries = new();

    public string Name => "capacity-forecasts";

    public TimeSpan Interval => TimeSpan.FromMinutes(15);

    /// <summary>
    /// Yes, despite reading history: it is a handful of index range reads, one per volume,
    /// and without it the card and the alert say nothing for the first quarter hour after
    /// every restart.
    /// </summary>
    public bool RunAtStartup => true;

    /// <summary>Whether a pass has finished since startup, so the card can tell "working it out" from "nothing to forecast".</summary>
    public bool HasRun { get; private set; }

    /// <summary>Raised, off the render thread, after each pass.</summary>
    public event Action? Changed;

    public IReadOnlyCollection<Entry> All => [.. _entries.Values];

    public CapacityForecast? Get(string connectionId, string metric) =>
        _entries.TryGetValue((connectionId, metric.ToLowerInvariant()), out var entry) ? entry.Forecast : null;

    /// <summary>
    /// The value of a derived <c>days_until_full:</c> metric, for the alert rules. False when
    /// the key is not a forecast or there is nothing to say yet — which the rule engine
    /// treats as "no reading", exactly as it does a probe that failed.
    /// </summary>
    public bool TryGetValue(string connectionId, string key, out double value)
    {
        value = 0;
        if (!CapacityMetric.TryParse(key, out var metric) || Get(connectionId, metric)?.AlertValue is not { } days)
            return false;
        value = days;
        return true;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.Now;
        var seen = new HashSet<(string, string)>();

        var connections = (await config.ConnectionsAsync(ct))
            .Where(c => c.Enabled && registry.Provider(c.Provider)?.IsMonitored != false)
            .ToList();

        foreach (var connection in connections)
        {
            // What the provider declares and what history has actually seen: a JSON API
            // connection reporting disk_percent declares nothing, and still deserves a forecast.
            IReadOnlyList<string> recorded;
            try
            {
                recorded = await history.MetricsAsync(connection.Id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Could not list the metrics of {Connection} to forecast", connection.Name);
                continue;
            }

            var candidates = registry.MetricsFor(connection).Select(m => m.Key)
                .Concat(recorded)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var metric in candidates)
            {
                if (registry.CapacityOf(connection, metric) is not { } limit)
                    continue;

                IReadOnlyList<HistoryStore.Sample> samples;
                try
                {
                    samples = await history.SamplesAsync(connection.Id, metric, CapacityForecast.Window, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning(ex, "Could not read the history of {Connection} {Metric} to forecast", connection.Name, metric);
                    continue;
                }

                // A declared metric that has never been reported is not something running
                // out; it is something this connection does not have.
                if (samples.Count == 0)
                    continue;

                var key = (connection.Id, metric.ToLowerInvariant());
                seen.Add(key);
                var forecast = CapacityForecast.Compute(samples.Select(s => (s.At, s.Value)), limit, now);
                _entries[key] = new Entry(connection.Id, metric, forecast, now);
            }
        }

        // Deleted connections, disabled ones, metrics that stopped being reported.
        foreach (var key in _entries.Keys.Where(k => !seen.Contains(k)))
            _entries.TryRemove(key, out _);

        HasRun = true;
        Raise();
    }

    private void Raise()
    {
        if (Changed is not { } handlers)
            return;

        // One card's handler failing should not stop the rest hearing about it.
        foreach (var handler in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "A capacity forecast subscriber threw");
            }
        }
    }
}
