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
/// That is deliberately all it does. A cache kept for a few seconds after the query would
/// catch a little more, but would also hand a card an answer from before the sweep that
/// just told it to reload. One further guard for the same reason: when a connection
/// records, any query still running for it stops being shared, because it may have read
/// the table before the write.
/// </summary>
public sealed class SharedSeries : IDisposable
{
    private readonly HistoryStore _history;
    private readonly Offload _offload;
    private readonly object _sync = new();
    private readonly Dictionary<Key, Task<IReadOnlyList<HistoryStore.Sample>>> _running = [];

    private readonly record struct Key(string ConnectionId, string Metric, TimeSpan Window);

    public SharedSeries(HistoryStore history, Offload offload)
    {
        _history = history;
        _offload = offload;
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

    /// <summary>
    /// The same answer as <see cref="HistoryStore.SamplesAsync"/>. Cancelling stops this
    /// caller waiting; the shared query itself runs to the end, since others may want it.
    /// </summary>
    public Task<IReadOnlyList<HistoryStore.Sample>> SamplesAsync(
        string connectionId, string metric, TimeSpan window, CancellationToken ct = default)
    {
        var key = new Key(connectionId, metric, window);
        Task<IReadOnlyList<HistoryStore.Sample>> task;

        lock (_sync)
        {
            if (!_running.TryGetValue(key, out task!))
            {
                // Wrapped in Task.Run so nothing of the query can run under this lock, even
                // when the caller is already inside an offloaded read and Run goes inline.
                task = Task.Run(() => _offload.Run(_ =>
                    _history.SamplesAsync(connectionId, metric, window, CancellationToken.None)));
                _running[key] = task;
                _ = task.ContinueWith(done => Forget(key, done), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }

        return task.WaitAsync(ct);
    }

    private void Forget(Key key, Task<IReadOnlyList<HistoryStore.Sample>> done)
    {
        lock (_sync)
        {
            // Only if it is still this query: a write may already have dropped it and a
            // newer one taken its place.
            if (_running.TryGetValue(key, out var current) && ReferenceEquals(current, done))
                _running.Remove(key);
        }
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
