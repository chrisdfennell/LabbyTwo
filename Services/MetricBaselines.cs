using System.Collections.Concurrent;
using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// What each watched metric usually reads at each hour, worked out in the background and
/// held in memory for the "unusual for the time" alert rules and their editor.
///
/// Off the sweep for the reason <see cref="CapacityForecasts"/> is: a baseline reads four
/// weeks of history, and SQLite is synchronous under its async API, so doing that inside an
/// evaluation pass would hold every rule behind it. Hourly is plenty — a slot is an hour
/// wide and rests on weeks of them, so an hour's new data barely moves it.
///
/// Only the series a rule actually asks about are worked out. The evaluator asks for the
/// usual value of every pair it judges on every pass; a pair nobody has asked about for a
/// few hours — the rule was deleted, the connection muted — is dropped at the next run, so
/// an install with a thousand series and one unusual rule reads one series an hour.
///
/// Nothing here is written to the samples table. "Usually about 520" is an opinion about
/// the measurements, and one stored beside them would be charted and averaged as though
/// somebody had measured it.
/// </summary>
public sealed class MetricBaselines(HistoryStore history, ILogger<MetricBaselines> log) : IBackgroundJob
{
    /// <summary>One metric's baseline on one connection, and when it was worked out.</summary>
    public sealed record Entry(string ConnectionId, string Metric, MetricBaseline Baseline, DateTimeOffset At);

    private readonly ConcurrentDictionary<(string ConnectionId, string Metric), Entry> _entries = new();

    /// <summary>When each pair was last asked about, so the hourly run knows what is still wanted.</summary>
    private readonly ConcurrentDictionary<(string ConnectionId, string Metric), Asked> _asked = new();

    /// <summary>The metric as the rule spells it, since the history's keys are case-sensitive and the lookup's are not.</summary>
    private readonly record struct Asked(string ConnectionId, string Metric, DateTimeOffset At);

    /// <summary>Pairs with a first computation already on its way, so a burst of passes starts one each.</summary>
    private readonly ConcurrentDictionary<(string ConnectionId, string Metric), byte> _starting = new();

    /// <summary>
    /// One history read at a time. A rule on "any connection" asks for twenty series on its
    /// first pass, and twenty concurrent four-week reads are twenty readers holding the
    /// database while the monitor is trying to write a sweep.
    /// </summary>
    private readonly SemaphoreSlim _reading = new(1, 1);

    public string Name => "metric-baselines";

    public TimeSpan Interval => TimeSpan.FromHours(1);

    /// <summary>
    /// No: at startup nothing has asked for anything yet. The first evaluation pass asks
    /// for exactly the series the rules need, a sweep after boot, and they are worked out then.
    /// </summary>
    public bool RunAtStartup => false;

    /// <summary>
    /// The zone "at this hour" is in. The server's own, like every other local time in the
    /// app; settable for a test that needs Saturday to be Saturday wherever it runs.
    /// </summary>
    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Local;

    private static (string, string) Key(string connectionId, string metric) => (connectionId, metric.ToLowerInvariant());

    /// <summary>The baseline as last worked out, or null if it has not been yet.</summary>
    public MetricBaseline? Get(string connectionId, string metric) =>
        _entries.TryGetValue(Key(connectionId, metric), out var entry) ? entry.Baseline : null;

    /// <summary>
    /// What is usual for this metric at <paramref name="now"/>, for the rule engine. Null
    /// while learning — and also the first time a pair is asked about, when its baseline is
    /// started in the background and the answer comes on a later pass. Either way the
    /// engine treats null as "no reading": nothing fires and nothing clears.
    /// </summary>
    public Usual? UsualAt(string connectionId, string metric, DateTimeOffset now)
    {
        var key = Key(connectionId, metric);
        _asked[key] = new Asked(connectionId, metric, now);

        if (_entries.TryGetValue(key, out var entry))
            return entry.Baseline.At(now, Zone);

        if (_starting.TryAdd(key, 0))
            _ = StartAsync(connectionId, metric, key);
        return null;
    }

    private async Task StartAsync(string connectionId, string metric, (string, string) key)
    {
        try
        {
            await RefreshAsync(connectionId, metric, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Fire-and-forget, so it must not surface as an unobserved exception. The hourly
            // run tries again, since the pair is now marked as asked for.
            log.LogWarning(ex, "Could not work out what is usual for {Metric} on {Connection}", metric, connectionId);
        }
        finally
        {
            _starting.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Works one baseline out now and keeps it. The editor calls this for its preview, which
    /// is why it is public: a rule being drawn up has not been asked about by anything yet.
    /// </summary>
    public async Task<MetricBaseline> RefreshAsync(string connectionId, string metric, CancellationToken ct)
    {
        await _reading.WaitAsync(ct);
        try
        {
            var hourly = await history.HourlyAsync(connectionId, metric, MetricBaseline.Window, ct);

            // The hour in progress is left out: its average is still moving, and it is the
            // very reading being judged — an evening the internet is slow should not already
            // be part of what "usual" means when it is compared against it.
            var thisHour = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600 * 3600;
            var baseline = MetricBaseline.Compute(
                hourly.Where(s => s.At.ToUnixTimeSeconds() < thisHour).Select(s => (s.At, s.Value)), Zone);

            _entries[Key(connectionId, metric)] = new Entry(connectionId, metric, baseline, DateTimeOffset.Now);
            return baseline;
        }
        finally
        {
            _reading.Release();
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // Asked about within the last few runs. Generous, so a rule that was briefly
        // disabled, or a connection that was down for an hour, does not lose its baseline.
        var cutoff = DateTimeOffset.Now - Interval * 3;

        foreach (var (key, asked) in _asked.ToArray())
        {
            if (asked.At < cutoff)
            {
                _asked.TryRemove(key, out _);
                continue;
            }

            try
            {
                await RefreshAsync(asked.ConnectionId, asked.Metric, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Could not work out what is usual for {Metric} on {Connection}", asked.Metric, asked.ConnectionId);
            }
        }

        // Rules deleted, connections gone, previews nobody turned into a rule.
        foreach (var key in _entries.Keys.Where(k => !_asked.ContainsKey(k)))
            _entries.TryRemove(key, out _);
    }
}
