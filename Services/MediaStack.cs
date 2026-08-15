using LabbyTwo.Core;
using LabbyTwo.Providers;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// The whole media stack, read once and handed to the page in one shape.
///
/// Which connections belong here is decided by the category a provider already declares
/// rather than by a list of names kept in this file. That is the difference between a
/// page that covers what shipped in the box and one that covers what you run: a plugin
/// declaring <c>Category =&gt; "Media"</c> joins the page having never heard of it, and
/// nothing here needs editing when the eighth *arr is released.
///
/// The gathering is deliberately fault-tolerant per connection. A media stack is a dozen
/// services that each restart on their own schedule, so "one of them is down" is the
/// normal state rather than an exception — and a page that goes blank because Lidarr is
/// restarting would be useless exactly when you opened it to find out why.
/// </summary>
public sealed class MediaStack(
    Registry registry,
    ConfigStore config,
    HealthMonitor health,
    HistoryStore history,
    ILogger<MediaStack> log)
{
    public const string MediaCategory = "Media";
    public const string DownloadsCategory = "Downloads";

    /// <summary>
    /// The whole page's worth of fetching. Generous, because it covers a dozen services
    /// in parallel and the slowest one sets the pace — but bounded, because a page that
    /// spins forever is worse than one that renders with a gap in it.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How far back a "current" reading may come from. Wide enough to survive a provider
    /// with a long <see cref="IConnectionProvider.MinimumInterval"/> and a LabbyTwo that
    /// has just restarted; narrow enough that last night's stream count is not presented
    /// as what is playing now.
    /// </summary>
    private static readonly TimeSpan ReadingWindow = TimeSpan.FromHours(1);

    public sealed record Playing(
        string Source, string User, string Item, string Detail, double PercentDone, bool Transcoding);

    public sealed record Release(string Source, string Title, string Detail, DateTimeOffset When, bool HaveIt);

    public sealed record Queued(string Source, string Title, string Status, double PercentDone, string TimeLeft);

    /// <summary>A download client, described by whichever of these numbers it reports.</summary>
    public sealed record Client(
        string Name, string Connection, double? DownMbps, double? UpMbps,
        double? RemainingGb, double? FreeDiskGb, bool Paused);

    public sealed record Library(string Name, string Connection, IReadOnlyList<(string Label, double Value)> Counts);

    /// <summary>One thing worth acting on — a stuck queue, subtitles missing, requests waiting.</summary>
    public sealed record Attention(string Source, string Message, bool Bad);

    public sealed record Snapshot(
        IReadOnlyList<Playing> NowPlaying,
        IReadOnlyList<Release> Upcoming,
        IReadOnlyList<Queued> Queue,
        IReadOnlyList<Client> Clients,
        IReadOnlyList<Library> Libraries,
        IReadOnlyList<Attention> NeedsAttention,
        IReadOnlyList<Connection> Down,
        int Connections)
    {
        public static readonly Snapshot Empty = new([], [], [], [], [], [], [], 0);

        public bool Any => Connections > 0;
        public int Transcoding => NowPlaying.Count(p => p.Transcoding);
    }

    /// <summary>Every enabled connection this page covers, in whatever order the store returns.</summary>
    public async Task<IReadOnlyList<Connection>> ConnectionsAsync(CancellationToken ct = default)
    {
        var all = await config.ConnectionsAsync(ct);
        return [.. all.Where(connection => connection.Enabled && IsMedia(connection))];
    }

    public bool IsMedia(Connection connection)
    {
        try
        {
            return registry.Provider(connection.Provider) is { } provider
                && provider.Category is MediaCategory or DownloadsCategory;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "{Provider} could not say what category it is", connection.Provider);
            return false;
        }
    }

    /// <summary>
    /// Every metric any media connection has actually recorded. The page draws a chart
    /// only for what is in here, which is what stops a stack with one download client from
    /// showing four empty axes — and means a chart appears on its own the day you add the
    /// service that reports it.
    /// </summary>
    public async Task<IReadOnlySet<string>> RecordedMetricsAsync(CancellationToken ct = default)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var connection in await ConnectionsAsync(ct))
        {
            try
            {
                foreach (var metric in await history.MetricsAsync(connection.Id, ct))
                    found.Add(metric);
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Could not list metrics for {Connection}", connection.Name);
            }
        }
        return found;
    }

    /// <summary>
    /// How long one gather serves everybody. Every section of the media page is a separate
    /// widget that asks for the whole stack — which is what makes each of them usable on
    /// your own dashboard — and without this, opening the page would ask a dozen services
    /// six times over. Short enough that Refresh still means refresh.
    /// </summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(10);

    private sealed class Cached
    {
        public Snapshot Snapshot = Snapshot.Empty;
        public DateTimeOffset At = DateTimeOffset.MinValue;
        public readonly SemaphoreSlim Gate = new(1, 1);
    }

    // Keyed by the calendar window, because two widgets asking for different amounts of
    // future are asking different questions and must not share an answer.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Cached> _cache = new();

    /// <summary>
    /// Raised when the cache is dropped, so every section on screen reloads together
    /// rather than each drifting to its own schedule.
    /// </summary>
    public event Action? Changed;

    /// <summary>Throws the cache away, so the next read really goes and asks.</summary>
    public void Invalidate()
    {
        _cache.Clear();
        Changed?.Invoke();
    }

    public async Task<Snapshot> ReadAsync(int calendarDays = 7, CancellationToken ct = default)
    {
        var entry = _cache.GetOrAdd(calendarDays, _ => new Cached());

        if (IsFresh(entry))
            return entry.Snapshot;

        await entry.Gate.WaitAsync(ct);
        try
        {
            // Somebody else gathered it while this call was queued behind them, which is
            // the entire point of the gate — six widgets rendering at once produce one
            // round of requests, not six.
            if (IsFresh(entry))
                return entry.Snapshot;

            entry.Snapshot = await GatherAllAsync(calendarDays, ct);
            entry.At = DateTimeOffset.Now;
            return entry.Snapshot;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private static bool IsFresh(Cached entry) => DateTimeOffset.Now - entry.At < CacheFor;

    private async Task<Snapshot> GatherAllAsync(int calendarDays, CancellationToken ct)
    {
        var connections = await ConnectionsAsync(ct);
        if (connections.Count == 0)
            return Snapshot.Empty;

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(Timeout);

        // What the monitor already knows is unreachable is not asked again. Every one of
        // these calls would otherwise sit out its own timeout, and ten dead services would
        // be ten timeouts deep before the page drew anything.
        var reachable = connections.Where(c => health.State(c.Id)?.IsUp != false).ToList();
        var down = connections.Where(c => health.State(c.Id)?.IsUp == false).ToList();

        var playing = new List<Playing>();
        var upcoming = new List<Release>();
        var queue = new List<Queued>();
        var clients = new List<Client>();
        var libraries = new List<Library>();
        var attention = new List<Attention>();

        var work = reachable.Select(connection =>
            GatherAsync(connection, calendarDays, bounded.Token)).ToList();

        foreach (var part in await Task.WhenAll(work))
        {
            playing.AddRange(part.NowPlaying);
            upcoming.AddRange(part.Upcoming);
            queue.AddRange(part.Queue);
            clients.AddRange(part.Clients);
            libraries.AddRange(part.Libraries);
            attention.AddRange(part.NeedsAttention);
        }

        foreach (var connection in down)
            attention.Add(new Attention(connection.Name, health.State(connection.Id)?.Message ?? "Not answering.", true));

        return new Snapshot(
            [.. playing.OrderByDescending(p => p.PercentDone)],
            [.. upcoming.OrderBy(r => r.When)],
            [.. queue.OrderByDescending(q => q.PercentDone)],
            [.. clients.OrderBy(c => c.Name)],
            [.. libraries.OrderBy(l => l.Name)],
            [.. attention],
            down,
            connections.Count);
    }

    /// <summary>
    /// One connection's contribution — deliberately not a <see cref="Snapshot"/>, whose
    /// Down list and Connections count are properties of the page rather than of any one
    /// service in it.
    /// </summary>
    private sealed record Part(
        IReadOnlyList<Playing> NowPlaying,
        IReadOnlyList<Release> Upcoming,
        IReadOnlyList<Queued> Queue,
        IReadOnlyList<Client> Clients,
        IReadOnlyList<Library> Libraries,
        IReadOnlyList<Attention> NeedsAttention);

    /// <summary>
    /// What one connection can tell us. Never throws: whatever it managed is what the page
    /// gets, and what it did not is a debug line rather than a hole in the render.
    /// </summary>
    private async Task<Part> GatherAsync(Connection connection, int calendarDays, CancellationToken ct)
    {
        var playing = new List<Playing>();
        var upcoming = new List<Release>();
        var queue = new List<Queued>();
        var clients = new List<Client>();
        var libraries = new List<Library>();
        var attention = new List<Attention>();

        // The last recorded numbers rather than a fresh probe. The monitor took these
        // moments ago, and asking a dozen services again to redraw one page is the mistake
        // the NAS card already had to be talked out of.
        IReadOnlyDictionary<string, double> readings;
        try
        {
            readings = await history.LatestAsync(connection.Id, ReadingWindow, ct);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "No recorded metrics for {Connection}", connection.Name);
            readings = new Dictionary<string, double>();
        }

        double? Reading(params string[] keys)
        {
            foreach (var key in keys)
                if (readings.TryGetValue(key, out var value))
                    return value;
            return null;
        }

        var provider = registry.Provider(connection.Provider);

        try
        {
            switch (provider)
            {
                case ArrProviderBase arr:
                    // Prowlarr manages indexers and has neither, and answers both with
                    // nothing rather than an error — so no special case is needed here.
                    foreach (var item in await arr.QueueAsync(connection, ct))
                        queue.Add(new Queued(connection.Name, item.Title, item.Status, item.PercentDone, item.TimeLeft));

                    if (calendarDays > 0)
                    {
                        foreach (var item in await arr.CalendarAsync(connection, calendarDays, ct))
                            upcoming.Add(new Release(connection.Name, item.Title, item.Episode, item.When, item.HaveIt));
                    }
                    break;

                case PlexProvider plex:
                    foreach (var session in await plex.SessionsAsync(connection, ct))
                    {
                        // Plex's own session list does not say whether it is transcoding;
                        // the count does, and Tautulli says it properly. Rather than guess
                        // per stream, leave it false here and let the header's count — which
                        // comes from the metric — be the honest answer.
                        playing.Add(new Playing(connection.Name, session.User, session.Title,
                            string.Join(" · ", new[] { session.Subtitle, session.Player }.Where(s => s.Length > 0)),
                            session.PercentDone, false));
                    }
                    break;

                case JellyfinProvider jellyfin:
                    foreach (var session in await jellyfin.SessionsAsync(connection, ct))
                        playing.Add(new Playing(connection.Name, session.User, session.Item,
                            session.Device, session.PercentDone, session.Transcoding));
                    break;
            }
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not read the details from {Connection}", connection.Name);
        }

        // ---- Everything below is metrics only, so it works for any provider in these
        // ---- categories including ones this file has never heard of.

        if (provider?.Category == DownloadsCategory)
        {
            clients.Add(new Client(
                connection.Name,
                connection.Id,
                Reading("download_mbps", "speed_mbps"),
                Reading("upload_mbps"),
                Reading("remaining_gb") ?? Reading("remaining_mb") / 1024,
                Reading("disk_free_gb", "free_disk_gb"),
                IsPaused()));
        }

        var counts = new List<(string, double)>();
        foreach (var (key, label) in LibraryMetrics)
            if (readings.TryGetValue(key, out var value))
                counts.Add((label, value));

        if (counts.Count > 0)
            libraries.Add(new Library(connection.Name, connection.Id, counts));

        // The handful of numbers that mean "go and do something", rather than merely
        // describing the state of things.
        var wanted = (Reading("subtitles_wanted_episodes") ?? 0) + (Reading("subtitles_wanted_movies") ?? 0);
        if (wanted > 0)
            attention.Add(new Attention(connection.Name, $"{wanted:0} items want subtitles.", false));

        if (Reading("providers_failing") > 0)
            attention.Add(new Attention(connection.Name, $"{Reading("providers_failing"):0} subtitle providers failing.", true));

        if (Reading("requests_pending") > 0)
            attention.Add(new Attention(connection.Name, $"{Reading("requests_pending"):0} requests waiting for approval.", false));

        if (Reading("queue_health") > 0)
            attention.Add(new Attention(connection.Name, $"{Reading("queue_health"):0} files waiting for a health check.", false));

        if (IsPaused())
            attention.Add(new Attention(connection.Name, "Downloading is paused.", true));

        return new Part(playing, upcoming, queue, clients, libraries, attention);

        // Providers spell this one two ways, and both record it as 1 or 0 rather than a
        // boolean, because a metric is a number by definition.
        bool IsPaused() => Reading("paused", "download_paused") >= 1;
    }

    /// <summary>
    /// Metrics that count what is in a library rather than what it is doing. Listed by
    /// metric name, so any provider reporting one of them appears without being named.
    /// </summary>
    private static readonly (string Key, string Label)[] LibraryMetrics =
    [
        ("movies", "Films"),
        ("series", "Series"),
        ("episodes", "Episodes"),
        ("books", "Books"),
        ("albums", "Albums"),
        ("artists", "Artists"),
        ("songs", "Songs"),
        ("items", "Items"),
        ("libraries", "Libraries"),
        ("photos", "Photos"),
        ("videos", "Videos"),
        ("channels", "Channels"),
        ("files", "Files known"),
    ];
}
