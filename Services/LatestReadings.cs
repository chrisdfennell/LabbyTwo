using System.Collections.Concurrent;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// The newest value of every metric on every connection, held in memory, so that a card
/// can show "the last thing we saw" without asking the database while the page is drawn.
///
/// That is the whole reason this exists. Microsoft.Data.Sqlite is synchronous underneath
/// its async API, so a card awaiting <see cref="HistoryStore.LatestAsync"/> from
/// OnInitializedAsync was really blocking the circuit's render thread for as long as the
/// query took — and a dashboard with an aggregate card over a dozen connections once
/// never opened at all because the render thread was sat inside SQLite. Making the query
/// fast fixed that day; this makes page rendering not depend on the database at all.
///
/// Kept current two ways:
/// <list type="bullet">
/// <item>On write. <see cref="HistoryStore.Recorded"/> fires after every committed batch,
/// so after the first sweep everything a card asks for is already here.</item>
/// <item>On miss. Straight after a restart nothing has been recorded yet, so a card asking
/// about a connection this process has not seen gets whatever is in memory (usually
/// nothing — the card shows its "waiting" state) and a background load is started. When it
/// lands <see cref="Changed"/> fires with the connection's id and the card redraws.</item>
/// </list>
/// </summary>
public sealed class LatestReadings : IDisposable
{
    /// <summary>
    /// How far back a warm-up looks. Effectively "ever": the query seeks straight to the
    /// newest sample of each metric, so a wider window costs nothing, and loading the
    /// newest value regardless of age lets one load answer every window a card asks for
    /// (six hours for most, thirty days for the speed test) by filtering in memory.
    /// Pruning keeps what is actually there to the retention period anyway.
    /// </summary>
    private static readonly TimeSpan Everything = TimeSpan.FromDays(3650);

    private readonly HistoryStore _history;
    private readonly ConfigStore _config;
    private readonly ILogger<LatestReadings> _log;

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    // A restart with a large dashboard open asks about every connection at once. Two loads
    // at a time is plenty for a query that is a handful of index seeks, and keeps a burst
    // of misses from queueing that many SQLite connections against the monitor's writes.
    private readonly SemaphoreSlim _loads = new(2, 2);

    public LatestReadings(HistoryStore history, ConfigStore config, ILogger<LatestReadings> log)
    {
        _history = history;
        _config = config;
        _log = log;
        _history.Recorded += OnRecorded;
        _config.Changed += OnConfigChanged;
    }

    /// <summary>
    /// Raised, off the render thread, when a background load for a connection completes.
    /// The argument is the connection's id, so a card can ignore loads that are not its own
    /// and redraw through InvokeAsync when one is.
    /// </summary>
    public event Action<string>? Changed;

    /// <summary>
    /// The newest value of each metric on a connection recorded within
    /// <paramref name="window"/> — the same answer <see cref="HistoryStore.LatestAsync"/>
    /// gives, from memory. Never blocks and never throws: on a connection this process has
    /// not loaded yet it returns what it has (possibly nothing) and starts a load, and
    /// <see cref="Changed"/> says when to ask again.
    /// </summary>
    public IReadOnlyDictionary<string, double> Get(string connectionId, TimeSpan window)
    {
        var entry = _entries.GetOrAdd(connectionId, _ => new Entry());
        // Whole seconds, compared inclusively, because that is how samples are stamped and
        // how LatestAsync compares them: the two must agree about a reading on the edge.
        var since = DateTimeOffset.UtcNow.Subtract(window).ToUnixTimeSeconds();
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        bool load;

        lock (entry)
        {
            // Only the newest value of each metric is kept, so "newest is older than the
            // window" is exactly "nothing in the window" — the rule the query applies.
            foreach (var (metric, reading) in entry.Metrics)
            {
                if (reading.At.ToUnixTimeSeconds() >= since)
                    result[metric] = reading.Value;
            }

            load = entry.State == LoadState.NotLoaded;
            if (load)
                entry.State = LoadState.Loading;
        }

        if (load)
        {
            // Task.Run rather than calling the async method directly: its first stretch —
            // opening the database and running the query — is synchronous in SQLite, and
            // would otherwise run right here on the caller's render thread.
            _ = Task.Run(() => LoadAsync(connectionId, entry));
        }

        return result;
    }

    private async Task LoadAsync(string connectionId, Entry entry)
    {
        var loaded = false;
        await _loads.WaitAsync();
        try
        {
            var stored = await _history.LatestReadingsAsync(connectionId, Everything);
            lock (entry)
            {
                foreach (var (metric, reading) in stored)
                    Merge(entry, metric, reading);
                entry.State = LoadState.Loaded;
            }
            loaded = true;
        }
        catch (Exception ex)
        {
            // Put it back so the next card to ask tries again. Nothing is raised: the card
            // is already showing its empty state, which is the honest answer for now.
            _log.LogWarning(ex, "Could not load the latest readings for connection {Connection}", connectionId);
            lock (entry)
                entry.State = LoadState.NotLoaded;
        }
        finally
        {
            _loads.Release();
        }

        // A connection deleted while its load was running has had its entry dropped; the
        // load filled an orphan, and there is nobody to tell.
        if (loaded && _entries.TryGetValue(connectionId, out var current) && ReferenceEquals(current, entry))
            Raise(connectionId);
    }

    private void OnRecorded(string connectionId, IReadOnlyDictionary<string, double> metrics, DateTimeOffset at)
    {
        // An entry is made even for a connection nobody has asked about yet, so the first
        // page after a restart's first sweep has something. It stays NotLoaded, though: a
        // probe need not report every metric every time, so the first Get still loads the
        // rest from the database, and Merge keeps whichever of the two is newer.
        var entry = _entries.GetOrAdd(connectionId, _ => new Entry());
        lock (entry)
        {
            foreach (var (metric, value) in metrics)
                Merge(entry, metric, new HistoryStore.Reading(value, at));
        }
    }

    /// <summary>
    /// Newer wins, and a tie goes to what is already here. Samples are stamped in whole
    /// seconds, so a load that read the database just before a sweep wrote to it can come
    /// back with the same second as the value the sweep has already put in memory — and
    /// the one in memory is the later of the two.
    /// </summary>
    private static void Merge(Entry entry, string metric, HistoryStore.Reading reading)
    {
        if (!entry.Metrics.TryGetValue(metric, out var existing) || reading.At > existing.At)
            entry.Metrics[metric] = reading;
    }

    private void OnConfigChanged() => _ = Task.Run(async () =>
    {
        try
        {
            await ForgetDeletedAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not reconcile the latest readings with the connection list");
        }
    });

    /// <summary>
    /// Drops everything held for a connection that no longer exists. Deleting a connection
    /// deletes its samples, and this is the memory's half of that: a card that somehow
    /// still asked would otherwise be shown readings from something that is gone. Run on
    /// every config change, off the thread that made it — which is often a render thread —
    /// because the connection list is cached and the check is cheap.
    /// </summary>
    public async Task ForgetDeletedAsync(CancellationToken ct = default)
    {
        var existing = (await _config.ConnectionsAsync(ct)).Select(c => c.Id).ToHashSet();
        foreach (var id in _entries.Keys)
        {
            if (!existing.Contains(id))
                _entries.TryRemove(id, out _);
        }
    }

    private void Raise(string connectionId)
    {
        if (Changed is not { } handlers)
            return;

        // One card's handler failing should not stop the rest hearing about it.
        foreach (var handler in handlers.GetInvocationList().Cast<Action<string>>())
        {
            try
            {
                handler(connectionId);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "A latest-readings subscriber threw");
            }
        }
    }

    public void Dispose()
    {
        _history.Recorded -= OnRecorded;
        _config.Changed -= OnConfigChanged;
    }

    private enum LoadState { NotLoaded, Loading, Loaded }

    /// <summary>One connection's newest readings. Locked on itself; every access is short.</summary>
    private sealed class Entry
    {
        public Dictionary<string, HistoryStore.Reading> Metrics { get; } = new(StringComparer.OrdinalIgnoreCase);
        public LoadState State { get; set; }
    }
}
