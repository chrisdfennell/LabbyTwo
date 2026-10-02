using System.Collections.Concurrent;
using System.Diagnostics;
using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// "Start a round now?" for one Docker host, decided without a clock of its own so the rules
/// are tests rather than timing: a round starts when the interval has passed since the last
/// one started, and never while the last one is still going. A round that is due while the
/// previous one is still running is skipped, not queued — the NAS being slow to answer is
/// exactly when a second round on top of the first would make it worse — and counted, so
/// the health page can say it happened.
/// </summary>
public sealed class PollGate
{
    private readonly object _sync = new();

    public bool Running { get; private set; }

    public DateTimeOffset? LastStarted { get; private set; }

    /// <summary>Rounds that came due while the one before was still running.</summary>
    public int Skipped { get; private set; }

    public bool TryStart(DateTimeOffset now, TimeSpan interval)
    {
        lock (_sync)
        {
            if (LastStarted is { } last && now - last < interval)
                return false;
            if (Running)
            {
                Skipped++;
                return false;
            }
            Running = true;
            LastStarted = now;
            return true;
        }
    }

    public void Finish()
    {
        lock (_sync)
            Running = false;
    }
}

/// <summary>
/// One round of container stats on one Docker host, as everything else reads it: the live
/// readings for every container, which of them were written to history, and the metrics the
/// next Docker probe hands the monitor.
/// </summary>
/// <param name="Round">Counts up per host, so a probe can tell a round it has already passed on from a new one.</param>
/// <param name="Took">How long the round took, list and stats together.</param>
/// <param name="Requests">Stats requests it sent — what it cost the Docker daemon.</param>
/// <param name="Readings">Every container in the list: running ones measured, the rest with their state only.</param>
/// <param name="Recorded">The containers chosen for history this round (<see cref="ContainerUsage.Choose"/>).</param>
/// <param name="Metrics">The live metrics: the chosen and the lingering containers' keys, and the three "busiest" numbers.</param>
/// <param name="RecordedKeys">Which of <paramref name="Metrics"/> are written to history.</param>
/// <param name="Error">Why there are no readings, in words — a socket proxy refusing stats, say.</param>
/// <param name="HostMemory">The host's memory in bytes, when known, for telling a real limit from "no limit".</param>
public sealed record ContainerResourceSnapshot(
    string ConnectionId,
    long Round,
    DateTimeOffset At,
    TimeSpan Interval,
    TimeSpan Took,
    int Requests,
    IReadOnlyList<ContainerReading> Readings,
    IReadOnlyList<string> Recorded,
    IReadOnlyDictionary<string, double> Metrics,
    IReadOnlySet<string> RecordedKeys,
    string? Error,
    ulong HostMemory = 0)
{
    public ContainerReading? Find(string name) =>
        Readings.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<ContainerReading> Running => Readings.Where(r => r.IsRunning);

    /// <summary>
    /// Whether this round is recent enough to report as current: two intervals and a sweep.
    /// Past that the poller has stopped or keeps failing, and a probe passing on a reading
    /// from ten minutes ago as "now" would keep an alert firing on something long over.
    /// </summary>
    public bool IsFresh(DateTimeOffset now) => now - At <= Interval * 2 + TimeSpan.FromSeconds(60);
}

/// <summary>
/// The latest round per Docker connection, in memory. Static, like <see cref="DockerContainers"/>'
/// own caches, because the Docker provider that hands these readings to the monitor is made
/// by reflection with no services to ask — and because there is only ever one answer per
/// connection, whoever asks.
/// </summary>
public static class ContainerResourceSnapshots
{
    private static readonly ConcurrentDictionary<string, ContainerResourceSnapshot> Latest = new();
    private static readonly ConcurrentDictionary<string, long> HandedOn = new();

    public static ContainerResourceSnapshot? Get(string connectionId) =>
        Latest.TryGetValue(connectionId, out var snapshot) ? snapshot : null;

    public static void Set(ContainerResourceSnapshot snapshot) => Latest[snapshot.ConnectionId] = snapshot;

    public static void Remove(string connectionId)
    {
        Latest.TryRemove(connectionId, out _);
        HandedOn.TryRemove(connectionId, out _);
    }

    public static IReadOnlyCollection<ContainerResourceSnapshot> All => [.. Latest.Values];

    /// <summary>
    /// True the first time a round is asked about by a probe, false after. The probe that gets
    /// true records the round's chosen metrics; every later probe until the next round passes
    /// the same values on as live state only, so a 30-second sweep over a 60-second poll does
    /// not write every reading twice.
    /// </summary>
    public static bool TakeRound(string connectionId, long round)
    {
        while (true)
        {
            if (!HandedOn.TryGetValue(connectionId, out var last))
            {
                if (HandedOn.TryAdd(connectionId, round))
                    return true;
                continue;
            }
            if (last >= round)
                return false;
            if (HandedOn.TryUpdate(connectionId, round, last))
                return true;
        }
    }

    /// <summary>The busiest few across every Docker host, for the pressure warning's "Busiest: …".</summary>
    public static IReadOnlyList<(ContainerResourceSnapshot Host, ContainerReading Reading)> Busiest(int count, DateTimeOffset now) =>
        [.. All.Where(s => s.IsFresh(now))
            .SelectMany(s => ContainerUsage.Busiest(s.Readings, count).Select(r => (s, r)))
            .OrderByDescending(p => (p.r.CpuPercent ?? 0) / 100 + (p.r.DiskPerSecond ?? 0) / (10 * ContainerUsage.Megabyte))
            .Take(count)];
}

/// <summary>
/// Polls the running containers on every Docker connection for CPU, memory, disk and
/// network, and keeps the answer in memory for the cards, the Containers tab, the pressure
/// warning and the next Docker probe.
///
/// <para><b>What it costs, and why it is shaped like this.</b> This exists because a NAS
/// locked up with Tdarr reading at full speed and nothing said so beforehand — so it must
/// not be the next thing loading the NAS. Each round is one container list (normally the
/// one the Docker probe fetched moments ago, so no request at all) and one
/// <c>GET /containers/{id}/stats?stream=false&amp;one-shot=true</c> per running container:
/// one-shot answers at once rather than holding the connection for the second Docker would
/// otherwise spend taking a sample of its own. At most <see cref="DockerContainers.StatsConcurrency"/>
/// are in flight per host, shared with the Containers tab; each has the connection's timeout;
/// the whole round is cancelled if it is still going when the next is due; and a round due
/// while the last is still running is skipped (<see cref="PollGate"/>). Every 60 seconds by
/// default, 30 to 300 on the connection's advanced settings, or off.</para>
///
/// <para><b>No database.</b> Nothing here reads the database, and it writes nothing itself:
/// the readings reach history as ordinary metrics of the Docker connection, through the
/// monitor's normal sample write on the next probe. CPU and rates are differenced against
/// this process's own previous sample of each container, not Docker's precpu block, which
/// a one-shot answer leaves empty.</para>
/// </summary>
public sealed class ContainerResources(
    ConfigStore config,
    HealthMonitor monitor,
    ILogger<ContainerResources> log) : BackgroundService
{
    /// <summary>How often the scheduler looks for a host that is due. Only memory is read on each look.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How old a container list may be and still decide which containers to ask about. A
    /// little over the default sweep, so the probe's own list is nearly always the one used;
    /// a container started since then waits one round to be measured.
    /// </summary>
    public static readonly TimeSpan ListReuse = TimeSpan.FromSeconds(45);

    /// <summary>The longest a single stats request may take, whatever the connection's own timeout says.</summary>
    public static readonly TimeSpan MaxRequestTimeout = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<string, PollGate> _gates = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DateTimeOffset>> _chosenAt = new();
    private readonly ConcurrentDictionary<string, long> _rounds = new();

    /// <summary>The host's memory, for telling "no limit" from a limit. Settable for tests.</summary>
    public Func<ulong> HostMemory { get; set; } = () => HostProc.Default.MemTotalBytes();

    public PollGate? Gate(string connectionId) => _gates.TryGetValue(connectionId, out var gate) ? gate : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await TickAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Could not schedule container stats this time");
            }
        }
    }

    /// <summary>
    /// Starts a round on every Docker host that is due and not still busy, and does not wait
    /// for them. Returns the rounds started, so a test can await them.
    /// </summary>
    public async Task<IReadOnlyList<Task>> TickAsync(DateTimeOffset now, CancellationToken ct)
    {
        // ConfigStore's cached copy: memory after the first read, and the monitor reads it every sweep anyway.
        var connections = await config.ConnectionsAsync(ct);
        var dockers = connections.Where(IsDocker).ToList();
        var started = new List<Task>();

        foreach (var connection in dockers)
        {
            var seconds = ContainerUsage.Seconds(connection.Settings.GetInt("stats_seconds", ContainerUsage.DefaultSeconds));
            if (seconds == 0)
            {
                ContainerResourceSnapshots.Remove(connection.Id);
                continue;
            }

            var interval = TimeSpan.FromSeconds(seconds);
            var gate = _gates.GetOrAdd(connection.Id, _ => new PollGate());
            if (!gate.TryStart(now, interval))
                continue;

            started.Add(Task.Run(async () =>
            {
                try
                {
                    await RoundAsync(connection, interval, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    log.LogDebug(ex, "Container stats round failed on {Connection}", connection.Name);
                }
                finally
                {
                    gate.Finish();
                }
            }, CancellationToken.None));
        }

        foreach (var gone in _gates.Keys.Where(id => dockers.All(c => c.Id != id)).ToList())
        {
            _gates.TryRemove(gone, out _);
            _chosenAt.TryRemove(gone, out _);
            ContainerResourceSnapshots.Remove(gone);
        }
        return started;
    }

    private bool IsDocker(Connection connection) =>
        string.Equals(connection.Provider, "docker", StringComparison.OrdinalIgnoreCase) && monitor.IsMonitored(connection);

    /// <summary>
    /// One round on one host: list, stats for the running containers, choose what to record,
    /// keep the answer. Public so the whole path runs against a scripted Docker in tests.
    /// </summary>
    public async Task<ContainerResourceSnapshot?> RoundAsync(Connection connection, TimeSpan interval, CancellationToken ct)
    {
        // The probe already says the host is down; asking forty stats of it would only wait out forty timeouts.
        if (monitor.State(connection.Id) is { IsUp: false })
            return null;

        var endpoint = connection.Settings.Get("endpoint", DockerSocket.DefaultEndpoint);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(connection.Settings.GetInt("timeout", 10), 1, (int)MaxRequestTimeout.TotalSeconds));
        var top = ContainerUsage.Top(connection.Settings.GetInt("stats_top", ContainerUsage.DefaultTop));
        var pinned = ContainerUsage.ParsePinned(connection.Settings.Get("stats_pinned"));
        var stopwatch = Stopwatch.StartNew();
        var round = _rounds.AddOrUpdate(connection.Id, 1, (_, r) => r + 1);

        // Never longer than the interval: a round still going when the next is due is the NAS
        // telling us it cannot keep up, and the gate would skip the next one anyway.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(interval);

        IReadOnlyList<ContainerRow> rows;
        int requests;
        IReadOnlyDictionary<string, ContainerStats> stats;
        try
        {
            // The list the Docker probe fetched within the last sweep, so normally no request at all.
            rows = DockerContainers.Remembered(endpoint, ListReuse)
                   ?? await DockerContainers.SharedListAsync(endpoint, timeout, limit.Token);
            if (DockerContainers.CachedCapabilities(endpoint).Why(DockerCapabilities.Stats) is { } refused)
                return Keep(connection, round, interval, stopwatch.Elapsed, 0, [], refused);

            var running = rows.Where(r => r.IsRunning).Select(r => r.Id).ToList();
            var before = DockerContainers.StatsRequestsMade;
            stats = await DockerContainers.StatsAsync(endpoint, timeout, running, limit.Token, oneShot: true);
            requests = (int)Math.Max(0, DockerContainers.StatsRequestsMade - before);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var why = ex is OperationCanceledException
                ? $"The round took longer than {interval.TotalSeconds:0} s and was stopped — Docker is answering slowly."
                : DockerContainers.Explain(ex, endpoint);
            return Keep(connection, round, interval, stopwatch.Elapsed, 0, [], why);
        }

        if (DockerContainers.CachedCapabilities(endpoint).Why(DockerCapabilities.Stats) is { } denied)
            return Keep(connection, round, interval, stopwatch.Elapsed, requests, [], denied);

        var readings = new List<ContainerReading>(rows.Count);
        foreach (var row in rows)
        {
            if (stats.TryGetValue(row.Id, out var s))
            {
                readings.Add(new ContainerReading(row.Id, row.Name, row.State, s.CpuPercent, s.MemoryUsed, s.MemoryLimit,
                    s.ReadPerSecond, s.WritePerSecond, s.RxPerSecond, s.TxPerSecond, s.Pids, s.At));
            }
            else if (!row.IsRunning)
            {
                readings.Add(new ContainerReading(row.Id, row.Name, row.State, null, 0, 0, null, null, null, null, null, DateTimeOffset.UtcNow));
            }
            // A running container whose stats did not come back has most likely just stopped;
            // it is left out rather than shown at zero, and the next round will know.
        }

        return Keep(connection, round, interval, stopwatch.Elapsed, requests, readings, null, top, pinned);
    }

    private ContainerResourceSnapshot Keep(
        Connection connection, long round, TimeSpan interval, TimeSpan took, int requests,
        IReadOnlyList<ContainerReading> readings, string? error, int top = 0, IReadOnlyList<string>? pinned = null)
    {
        var now = DateTimeOffset.UtcNow;
        var hostMemory = SafeHostMemory();
        var chosen = error is null ? ContainerUsage.Choose(readings, top, pinned ?? []) : [];

        var chosenAt = _chosenAt.GetOrAdd(connection.Id, _ => new ConcurrentDictionary<string, DateTimeOffset>(StringComparer.Ordinal));
        foreach (var name in chosen)
            chosenAt[name] = now;
        foreach (var (name, at) in chosenAt.ToList())
            if (now - at > ContainerUsage.Linger)
                chosenAt.TryRemove(name, out _);

        var metrics = new Dictionary<string, double>(StringComparer.Ordinal);
        var recordedKeys = new HashSet<string>(StringComparer.Ordinal);
        var chosenSet = chosen.ToHashSet(StringComparer.Ordinal);
        foreach (var reading in readings)
        {
            if (!chosenAt.ContainsKey(reading.Name))
                continue;
            var own = new Dictionary<string, double>(StringComparer.Ordinal);
            ContainerUsage.AddMetrics(own, reading);
            foreach (var (key, value) in own)
            {
                metrics[key] = value;
                if (chosenSet.Contains(reading.Name))
                    recordedKeys.Add(key);
            }
        }

        var busiest = new Dictionary<string, double>(StringComparer.Ordinal);
        ContainerUsage.AddBusiest(busiest, readings, hostMemory);
        foreach (var (key, value) in busiest)
        {
            metrics[key] = value;
            recordedKeys.Add(key);
        }

        var snapshot = new ContainerResourceSnapshot(connection.Id, round, now, interval, took, requests, readings, chosen,
            metrics, recordedKeys, error, hostMemory);

        // A failed round keeps the last good readings on show, marked with why, until they go stale.
        if (error is not null && ContainerResourceSnapshots.Get(connection.Id) is { Error: null } last && last.IsFresh(now))
            snapshot = last with { Error = error };

        ContainerResourceSnapshots.Set(snapshot);
        return snapshot;
    }

    private ulong SafeHostMemory()
    {
        try
        {
            return HostMemory();
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
