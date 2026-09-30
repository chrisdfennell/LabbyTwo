using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// <see cref="HistoryStore.SamplesAsync"/> for cards: off the render thread through
/// <see cref="Offload"/>, and with identical requests that overlap sharing one query.
///
/// Identical requests overlap a lot, and all at the same moment. The weather tab alone
/// asks for outdoor temperature over the same window four times — its chart, the
/// indoor-vs-outdoor chart, the summary and the readings table — and every sweep makes
/// every card on every open browser ask again at once. Sharing the query that is already
/// running answers all of them for the price of one, without holding anything back: a
/// request that arrives after the query finished starts a new one.
///
/// That is all it does for a caller that does not say otherwise. A cache kept for a few
/// seconds after the query would catch a little more, but would also hand a card an answer
/// from before the sweep that just told it to reload. One further guard for the same
/// reason: when a connection records, any query still running for it stops being shared,
/// because it may have read the table before the write.
///
/// A caller can say otherwise, though. A <c>{{chart}}</c> over a month does not want the
/// sample that landed a second ago — one more point is a two-hundredth of a pixel — and it
/// asks for a recent answer with <c>freshFor</c>: anything read no longer ago than that is
/// handed back without a query, whichever page read it. That is what lets a month's chart
/// in three open runbooks, and the same chart in the monthly report, cost one read an hour
/// between them rather than one each per sweep. The answers kept are few and small (a few
/// hundred points each), dropped once older than the longest anyone may ask for, and
/// capped in number.
/// </summary>
public sealed class SharedSeries : IDisposable
{
    /// <summary>The oldest answer kept for a caller that will take one. Nothing asks for more.</summary>
    public static readonly TimeSpan LongestFresh = TimeSpan.FromHours(1);

    /// <summary>How many answers are kept at most — every open chart's lines, several times over.</summary>
    public const int MaxKept = 256;

    private readonly HistoryStore _history;
    private readonly Offload _offload;
    private readonly TimeProvider _clock;
    private readonly object _sync = new();
    private readonly Dictionary<Key, Task<IReadOnlyList<HistoryStore.Sample>>> _running = [];
    private readonly Dictionary<Key, (DateTimeOffset ReadAt, IReadOnlyList<HistoryStore.Sample> Samples)> _kept = [];
    private int _queries;

    /// <summary>
    /// One read. A moving window has <see cref="From"/> and <see cref="To"/> at zero; a fixed
    /// span has them as Unix seconds and no window.
    /// </summary>
    private readonly record struct Key(string ConnectionId, string Metric, TimeSpan Window, long From = 0, long To = 0);

    public SharedSeries(HistoryStore history, Offload offload, TimeProvider? clock = null)
    {
        _history = history;
        _offload = offload;
        _clock = clock ?? TimeProvider.System;
        _history.Recorded += OnRecorded;
    }

    /// <summary>How many distinct queries are running. For tests.</summary>
    public int Running
    {
        get
        {
            lock (_sync)
                return _running.Count;
        }
    }

    /// <summary>How many queries have been started in all. For tests, and for the benchmark.</summary>
    public int Queries => Volatile.Read(ref _queries);

    /// <summary>
    /// The same answer as <see cref="HistoryStore.SamplesAsync"/>. Cancelling stops this
    /// caller waiting; the shared query itself runs to the end, since others may want it.
    /// </summary>
    public Task<IReadOnlyList<HistoryStore.Sample>> SamplesAsync(
        string connectionId, string metric, TimeSpan window, CancellationToken ct = default) =>
        SamplesAsync(connectionId, metric, window, TimeSpan.Zero, ct);

    /// <summary>
    /// As above, but an answer read no more than <paramref name="freshFor"/> ago is good
    /// enough, and is handed back without asking the database at all.
    /// </summary>
    public Task<IReadOnlyList<HistoryStore.Sample>> SamplesAsync(
        string connectionId, string metric, TimeSpan window, TimeSpan freshFor, CancellationToken ct = default) =>
        ReadAsync(new Key(connectionId, metric, window), freshFor,
            () => _history.SamplesAsync(connectionId, metric, window, CancellationToken.None), ct);

    /// <summary>
    /// <see cref="HistoryStore.SamplesBetweenAsync"/>, shared the same way: a fixed span —
    /// last month, in the monthly report — read by every chart that shows it at once.
    /// </summary>
    public Task<IReadOnlyList<HistoryStore.Sample>> BetweenAsync(
        string connectionId, string metric, DateTimeOffset from, DateTimeOffset to, TimeSpan freshFor, CancellationToken ct = default) =>
        ReadAsync(new Key(connectionId, metric, TimeSpan.Zero, from.ToUnixTimeSeconds(), to.ToUnixTimeSeconds()), freshFor,
            () => _history.SamplesBetweenAsync(connectionId, metric, from, to, CancellationToken.None), ct);

    private Task<IReadOnlyList<HistoryStore.Sample>> ReadAsync(
        Key key, TimeSpan freshFor, Func<Task<IReadOnlyList<HistoryStore.Sample>>> read, CancellationToken ct)
    {
        if (freshFor > LongestFresh)
            freshFor = LongestFresh;
        Task<IReadOnlyList<HistoryStore.Sample>> task;

        lock (_sync)
        {
            var now = _clock.GetUtcNow();
            if (freshFor > TimeSpan.Zero && _kept.TryGetValue(key, out var kept) && now - kept.ReadAt <= freshFor)
                return Task.FromResult(kept.Samples);

            if (!_running.TryGetValue(key, out task!))
            {
                // Stamped with when it started rather than when it finished: a write that
                // lands while it runs may or may not be in the answer, so the answer is as
                // old as the moment it could last have missed something.
                var startedAt = now;
                _queries++;
                // Wrapped in Task.Run so nothing of the query can run under this lock, even
                // when the caller is already inside an offloaded read and Run goes inline.
                task = Task.Run(() => _offload.Run(_ => read()));
                _running[key] = task;
                _ = task.ContinueWith(done => Finished(key, done, startedAt), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }

        return task.WaitAsync(ct);
    }

    private void Finished(Key key, Task<IReadOnlyList<HistoryStore.Sample>> done, DateTimeOffset startedAt)
    {
        lock (_sync)
        {
            // Only if it is still this query: a write may already have dropped it and a
            // newer one taken its place.
            if (_running.TryGetValue(key, out var current) && ReferenceEquals(current, done))
                _running.Remove(key);

            if (done.IsCompletedSuccessfully)
            {
                if (!_kept.TryGetValue(key, out var previous) || previous.ReadAt <= startedAt)
                    _kept[key] = (startedAt, done.Result);
                Tidy(_clock.GetUtcNow());
            }
        }
    }

    /// <summary>Drops answers nobody may ask for any more, then the oldest if there are still too many. Under the lock.</summary>
    private void Tidy(DateTimeOffset now)
    {
        foreach (var stale in _kept.Where(k => now - k.Value.ReadAt > LongestFresh).Select(k => k.Key).ToList())
            _kept.Remove(stale);
        if (_kept.Count <= MaxKept)
            return;
        foreach (var oldest in _kept.OrderBy(k => k.Value.ReadAt).Take(_kept.Count - MaxKept).Select(k => k.Key).ToList())
            _kept.Remove(oldest);
    }

    private void OnRecorded(string connectionId, IReadOnlyDictionary<string, double> metrics, DateTimeOffset at)
    {
        lock (_sync)
        {
            foreach (var key in _running.Keys.Where(k => k.ConnectionId == connectionId).ToList())
                _running.Remove(key);
        }
    }

    public void Dispose() => _history.Recorded -= OnRecorded;
}
