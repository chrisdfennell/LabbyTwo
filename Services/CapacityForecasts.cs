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
    /// The forecast that answers "when does this run out" for a metric on a connection:
    /// for an aggregate like <c>disk_percent</c>, whichever of its volumes runs out first
    /// (see <see cref="Representatives"/>); for one volume, that volume. Null when there is
    /// nothing forecast at all.
    /// </summary>
    public Entry? Soonest(string connectionId, string metric)
    {
        if (VolumeMetric.TryParse(metric, out _, out _))
            return _entries.GetValueOrDefault((connectionId, metric.ToLowerInvariant()));

        return Representatives(_entries.Values.Where(e => e.ConnectionId == connectionId && VolumeMetric.BelongsTo(e.Metric, metric)))
            .OrderBy(e => e.Forecast.SortKey)
            .FirstOrDefault();
    }

    /// <summary>A metric's forecasts on one connection: the aggregate and each of its volumes.</summary>
    public IReadOnlyList<Entry> For(string connectionId, string metric) =>
        [.. _entries.Values.Where(e => e.ConnectionId == connectionId && VolumeMetric.BelongsTo(e.Metric, metric))];

    /// <summary>
    /// What a card with room for a line or two should say: the forecasts that are heading
    /// somewhere, soonest first, at most <paramref name="max"/> of them, and how many more
    /// there were. "Not filling" is left out, because under every volume bar it is noise;
    /// duplicates are left out as <see cref="Representatives"/> describes.
    /// </summary>
    public static (IReadOnlyList<Entry> Shown, int More) Filling(IEnumerable<Entry> entries, int max)
    {
        var filling = Representatives(entries)
            .Where(e => e.Forecast.State is ForecastState.Filling or ForecastState.Full)
            .OrderBy(e => e.Forecast.SortKey)
            .ThenBy(e => e.Metric, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var shown = filling.Take(Math.Max(0, max)).ToList();
        return (shown, filling.Count - shown.Count);
    }

    /// <summary>
    /// The value of a derived <c>days_until_full:</c> metric, for the alert rules. False when
    /// the key is not a forecast or there is nothing to say yet — which the rule engine
    /// treats as "no reading", exactly as it does a probe that failed.
    ///
    /// <c>days_until_full:disk_percent</c> is the soonest of every volume, not only the
    /// fullest one. A rule names a single metric, so the alternative — one suggested rule
    /// per volume — would mean a rule for volumes that exist today and silence for the one
    /// added next month. Answering the aggregate this way makes the suggested "full within
    /// 30 days" cover every volume, including on every NAS where somebody already added it.
    /// A rule on one volume's forecast (<c>days_until_full:disk_percent:vol2</c>) still
    /// watches just that volume.
    /// </summary>
    public bool TryGetValue(string connectionId, string key, out double value)
    {
        value = 0;
        if (!CapacityMetric.TryParse(key, out var metric) || Soonest(connectionId, metric)?.Forecast.AlertValue is not { } days)
            return false;
        value = days;
        return true;
    }

    /// <summary>
    /// Forecasts with the duplicates taken out, for anything that lists them. A NAS records
    /// its fullest volume as <c>disk_percent</c> and every volume as <c>disk_percent:…</c>,
    /// so listing all of them says the same thing twice — on a NAS with one volume, exactly
    /// twice. Per connection and measured metric:
    /// <list type="bullet">
    /// <item>Once any volume has a forecast of its own, the volumes are the answer and the
    /// aggregate is dropped: it is only ever one of them, and the one most likely to be
    /// sitting still.</item>
    /// <item>Until then — the first two days after this started recording volumes, or a
    /// provider that records none — the aggregate is the only forecast there is, and the
    /// volumes, which could say only "not enough history yet", are dropped instead.</item>
    /// </list>
    /// Everything else passes through untouched.
    /// </summary>
    public static IReadOnlyList<Entry> Representatives(IEnumerable<Entry> entries)
    {
        var list = entries.ToList();

        // Volumes grouped under the aggregate they belong to.
        var volumes = list
            .Select(e => VolumeMetric.TryParse(e.Metric, out var measured, out _) ? (Entry: e, Measured: measured) : default)
            .Where(v => v.Entry is not null)
            .GroupBy(v => (v.Entry.ConnectionId, Measured: v.Measured.ToLowerInvariant()))
            .ToDictionary(g => g.Key, g => g.Select(v => v.Entry).ToList());

        var aggregates = list
            .Where(e => !VolumeMetric.TryParse(e.Metric, out _, out _))
            .Select(e => (e.ConnectionId, Metric: e.Metric.ToLowerInvariant()))
            .ToHashSet();

        bool Informative(Entry e) => e.Forecast.State != ForecastState.NotEnoughHistory;

        var result = new List<Entry>(list.Count);
        foreach (var entry in list)
        {
            if (VolumeMetric.TryParse(entry.Metric, out var measured, out _))
            {
                var group = volumes[(entry.ConnectionId, measured.ToLowerInvariant())];
                var hasAggregate = aggregates.Contains((entry.ConnectionId, measured.ToLowerInvariant()));
                if (!hasAggregate || group.Any(Informative))
                    result.Add(entry);
            }
            else if (!volumes.TryGetValue((entry.ConnectionId, entry.Metric.ToLowerInvariant()), out var group)
                     || !group.Any(Informative))
            {
                result.Add(entry);
            }
        }
        return result;
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
