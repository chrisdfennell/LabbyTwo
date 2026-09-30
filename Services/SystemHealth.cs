using System.Diagnostics;
using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// Whether LabbyTwo itself is well, as opposed to the things it watches. Everything here
/// was learned the hard way — over SSH, from log greps and a stack dump — and every piece
/// of it was already sitting in memory: when the monitor last swept, whether a background
/// job is failing, whether a plugin loaded. This gathers it for the health page and for
/// <c>/api/health/details</c>.
///
/// The split matters. <see cref="Live"/> reads memory only, so a page can call it every
/// second and an endpoint can answer while the database is wedged — which is exactly when
/// it is needed. Anything that touches the disk or the network is a separate call, off the
/// render thread, and the network ones only ever on a button.
/// </summary>
public sealed class SystemHealth(
    HealthMonitor monitor,
    BackgroundJobRunner jobs,
    ModuleCatalog catalog,
    ConfigStore config,
    Db db,
    HistoryStore history,
    LoginThrottle logins)
{
    public enum Level
    {
        Ok,
        Warn,
        Bad,
    }

    /// <summary>One sentence for the summary at the top, in words rather than numbers.</summary>
    public sealed record Finding(Level Level, string Area, string Text);

    public sealed record PluginStatus(
        string HostVersion,
        string Directory,
        bool DirectoryExists,
        int DllsFound,
        IReadOnlyList<string> Loaded,
        IReadOnlyList<string> BuiltForAnother,
        IReadOnlyList<ModuleFailure> Failures);

    /// <param name="ThreadPoolPending">
    /// Work queued for the thread pool and not yet started. Near zero on a healthy box; a
    /// number that keeps growing means something is blocking pool threads, which is how a
    /// synchronous database read on a render thread shows up from outside.
    /// </param>
    public sealed record ProcessStatus(
        string Version,
        string Runtime,
        string Os,
        bool InContainer,
        DateTimeOffset StartedAt,
        TimeSpan Uptime,
        long ManagedBytes,
        long WorkingSetBytes,
        int ThreadCount,
        long ThreadPoolPending,
        int ThreadPoolThreads,
        int ProcessorCount);

    /// <param name="Logins">What the login throttle has done since startup. Null only in tests.</param>
    public sealed record LiveStatus(
        DateTimeOffset At,
        MonitorStatus Monitor,
        IReadOnlyList<JobRun> Jobs,
        PluginStatus Plugins,
        ProcessStatus Process,
        LoginThrottle.Status? Logins = null);

    /// <param name="SamplesEstimate">
    /// From the rowid range rather than COUNT(*), which reads the whole table. An upper
    /// bound — pruned rows leave gaps — but the right order of magnitude, for the cost of
    /// two index seeks.
    /// </param>
    public sealed record DatabaseStatus(
        string Path,
        long FileBytes,
        long WalBytes,
        long TotalBytes,
        long? SamplesEstimate,
        string? Error);

    /// <summary>What the JSON endpoint returns: the live picture, the cheap database facts, and the verdict.</summary>
    public sealed record Report(
        LiveStatus Live,
        DatabaseStatus Database,
        IReadOnlyDictionary<string, string> ConnectionNames,
        IReadOnlyList<Finding> Findings);

    /// <summary>Timings from actually running the dashboard's hot query, on request.</summary>
    public sealed record QueryTiming(int Connections, TimeSpan Total, TimeSpan Slowest, string? SlowestConnectionId, string? Error);

    /// <summary>The live picture, from memory alone. Cheap enough for every tick of a clock.</summary>
    public LiveStatus Live()
    {
        var now = DateTimeOffset.Now;
        using var process = Process.GetCurrentProcess();
        var started = new DateTimeOffset(process.StartTime);

        var plugins = catalog.Plugins.ToList();
        var pluginStatus = new PluginStatus(
            catalog.HostVersion,
            catalog.PluginDirectory,
            catalog.PluginDirectoryExists,
            catalog.DllsFound,
            [.. plugins.Select(p => p.Version.Length > 0 ? $"{p.Name} {p.Version}" : p.Name)],
            [.. plugins.Where(catalog.BuiltForAnother).Select(p => $"{p.Name} (built for {p.Version})")],
            // A copy: endpoint mapping appends to this list after startup.
            [.. catalog.Failures]);

        var processStatus = new ProcessStatus(
            UpdateChecker.Installed,
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            File.Exists("/.dockerenv") || string.Equals(
                Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true",
                StringComparison.OrdinalIgnoreCase),
            started,
            now - started,
            GC.GetTotalMemory(forceFullCollection: false),
            Environment.WorkingSet,
            process.Threads.Count,
            ThreadPool.PendingWorkItemCount,
            ThreadPool.ThreadCount,
            Environment.ProcessorCount);

        return new LiveStatus(now, monitor.Status, jobs.Runs, pluginStatus, processStatus, logins.Snapshot());
    }

    /// <summary>
    /// File sizes and a row estimate. Run on the thread pool: Microsoft.Data.Sqlite's async
    /// methods complete synchronously, so awaiting one from a component would still hold
    /// the circuit while SQLite worked.
    /// </summary>
    /// <summary>
    /// The rough sample count, as two separate lookups. SQLite only turns MIN or MAX into a
    /// single seek to one end of the table when it is the whole query; "MAX(rowid) -
    /// MIN(rowid)" in one SELECT reads every row instead. On a 555 MB database on NAS
    /// disks that was long enough for Cloudflare to give up on the health page (524).
    /// A test checks the plan, because on a fast disk the scan is quick enough to hide.
    /// </summary>
    public const string SampleEstimateSql =
        "SELECT (SELECT MAX(rowid) FROM samples) - (SELECT MIN(rowid) FROM samples) + 1";

    public Task<DatabaseStatus> DatabaseAsync(CancellationToken ct = default) => Task.Run(async () =>
    {
        var path = db.FilePath;
        var file = Length(path);
        var wal = Length(path + "-wal");
        try
        {
            await using var connection = await db.OpenAsync(ct);
            var cmd = connection.CreateCommand();
            cmd.CommandText = SampleEstimateSql;
            var value = await cmd.ExecuteScalarAsync(ct);
            long? estimate = value is null or DBNull ? 0 : Convert.ToInt64(value);
            return new DatabaseStatus(path, file, wal, db.SizeBytes, estimate, null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return new DatabaseStatus(path, file, wal, db.SizeBytes, null, ex.GetBaseException().Message);
        }

        static long Length(string path) => new FileInfo(path) is { Exists: true } info ? info.Length : 0;
    }, ct);

    /// <summary>The exact row count. A full scan on a big table, so only ever on a button.</summary>
    public Task<(long Rows, TimeSpan Took)> CountSamplesAsync(CancellationToken ct = default) => Task.Run(async () =>
    {
        var stopwatch = Stopwatch.StartNew();
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM samples";
        var rows = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        return (rows, stopwatch.Elapsed);
    }, ct);

    /// <summary>
    /// Times the query every live card makes while a dashboard draws — the latest reading
    /// of each metric — for every enabled connection in turn. The one that got slow on a
    /// big database was this one, and a number here would have said so without a trace.
    /// </summary>
    public Task<QueryTiming> TimeLatestReadingsAsync(CancellationToken ct = default) => Task.Run(async () =>
    {
        var connections = (await config.ConnectionsAsync(ct)).Where(c => c.Enabled).Take(50).ToList();
        var total = Stopwatch.StartNew();
        var slowest = TimeSpan.Zero;
        string? slowestId = null;
        try
        {
            foreach (var connection in connections)
            {
                var one = Stopwatch.StartNew();
                await history.LatestAsync(connection.Id, TimeSpan.FromDays(1), ct);
                if (one.Elapsed > slowest)
                {
                    slowest = one.Elapsed;
                    slowestId = connection.Id;
                }
            }
            return new QueryTiming(connections.Count, total.Elapsed, slowest, slowestId, null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return new QueryTiming(connections.Count, total.Elapsed, slowest, slowestId, ex.GetBaseException().Message);
        }
    }, ct);

    /// <summary>Connection names by id, so timings can say "Plex" rather than an id.</summary>
    public Task<IReadOnlyDictionary<string, string>> NamesAsync(CancellationToken ct = default) => Task.Run(async () =>
        (IReadOnlyDictionary<string, string>)(await config.ConnectionsAsync(ct)).ToDictionary(c => c.Id, c => c.Name), ct);

    /// <summary>Everything the endpoint shows, with no network involved.</summary>
    public async Task<Report> ReportAsync(CancellationToken ct = default)
    {
        var live = Live();
        var database = await DatabaseAsync(ct);
        var names = await NamesAsync(ct);
        return new Report(live, database, names, Assess(live, null, monitor.RestoreDeadline));
    }

    /// <summary>
    /// The verdict, as sentences. Pure, so every threshold here can be tested with a
    /// made-up status and a made-up clock.
    /// </summary>
    /// <param name="dns">The last "check now" result, if there is one.</param>
    public static IReadOnlyList<Finding> Assess(
        LiveStatus live, IReadOnlyList<DnsCheck.Lookup>? dns, TimeSpan restoreDeadline)
    {
        var findings = new List<Finding>();
        var now = live.At;
        var m = live.Monitor;
        var period = m.SweepPeriod;

        // The monitor waits for the restore, then two seconds, then sweeps. Give it all of
        // that plus two whole periods before calling a missing sweep a fault, so a slow
        // start is not reported as a dead one.
        var firstSweepGrace = restoreDeadline + TimeSpan.FromSeconds(2) + period * 2;

        if (m.StartedAt is null)
        {
            if (live.Process.Uptime > TimeSpan.FromMinutes(1))
                findings.Add(new(Level.Bad, "Monitor",
                    "The monitor never started, so nothing on the dashboard is being checked. " +
                    "Something held up startup — look at the log from when LabbyTwo started."));
        }
        else if (m.IsSweepStuck(now))
        {
            var stuck = m.InFlight.Count > 0 ? $" Still waiting on {m.InFlight.Count} probe(s)." : "";
            findings.Add(new(Level.Bad, "Monitor",
                $"A sweep has been running for {Seconds(m.RunningFor(now)!.Value)} — it should take well under " +
                $"{Seconds(period)}. Every status on the dashboard is frozen until it finishes.{stuck}"));
        }
        else if (m.LastSweepFinished is null && m.CurrentSweepStarted is null)
        {
            var waited = now - m.StartedAt.Value;
            if (waited > firstSweepGrace)
                findings.Add(new(Level.Bad, "Monitor",
                    $"The monitor has not run a single sweep in the {Seconds(waited)} since it started, " +
                    "so nothing on the dashboard is current."));
            else
                findings.Add(new(Level.Warn, "Monitor", "Starting up — the first sweep has not run yet."));
        }
        else if (m.CurrentSweepStarted is null && m.LastSweepFinished is { } finished && now - finished > period * 3)
        {
            findings.Add(new(Level.Bad, "Monitor",
                $"The last sweep finished {Seconds(now - finished)} ago, but one should start every {Seconds(period)}. " +
                "The monitor has stopped."));
        }

        // First among the monitor's findings when it applies, because it changes how every
        // other red thing on the dashboard should be read.
        if (m.Blindness is { Impaired: true } blind)
            findings.Add(new(Level.Bad, "Monitor",
                $"{blind.Headline} It has lasted {Seconds(blind.For(now))}. {blind.Detail}"));

        if (m.LastSweepError is { } sweepError)
            findings.Add(new(Level.Warn, "Monitor", $"The last sweep failed: {sweepError}"));

        switch (m.Restore)
        {
            case RestoreOutcome.TimedOut:
                findings.Add(new(Level.Warn, "Monitor",
                    "Restoring the last known status timed out at startup, which usually means the database is slow."));
                break;
            case RestoreOutcome.Failed:
                findings.Add(new(Level.Warn, "Monitor", $"Restoring the last known status failed: {m.RestoreError}"));
                break;
        }

        foreach (var job in live.Jobs)
        {
            if (job.RunningSince is { } since && now - since > job.Interval && job.Interval > TimeSpan.Zero)
                findings.Add(new(Level.Warn, "Jobs",
                    $"The background job \"{job.Name}\" has been running for {Seconds(now - since)}, longer than its interval."));
            else if (!job.Ok)
                findings.Add(new(Level.Warn, "Jobs", $"The background job \"{job.Name}\" failed last time: {job.Message}"));
        }

        if (live.Plugins.Failures.Count > 0)
            findings.Add(new(Level.Warn, "Plugins",
                $"{live.Plugins.Failures.Count} plugin problem(s) at startup — see Plugins below."));
        if (live.Plugins.BuiltForAnother.Count > 0)
            findings.Add(new(Level.Warn, "Plugins",
                $"{live.Plugins.BuiltForAnother.Count} plugin(s) were built for a different version of LabbyTwo."));

        // A handful queued is normal under load; a hundred means pool threads are blocked.
        if (live.Process.ThreadPoolPending >= 100)
            findings.Add(new(Level.Warn, "Process",
                $"{live.Process.ThreadPoolPending} work items are waiting for a thread. If that keeps growing, " +
                "something is blocking threads and pages will stop responding."));

        // Somebody guessing the password is worth knowing about after they have stopped,
        // so the last day's lockouts are reported rather than only the ones still running.
        if (live.Logins is { LastLockAt: { } lockedAt } logins && now - lockedAt < TimeSpan.FromDays(1))
            findings.Add(new(Level.Warn, "Sign-in",
                $"{logins.Failures} failed sign-in(s) since LabbyTwo started, and {logins.Refused} refused for coming too fast. " +
                $"The last lockout was {Seconds(now - lockedAt)} ago, for {logins.LastLockAddress}." +
                (logins.GlobalLockedUntil is not null ? " Every sign-in is paused for now." : "")));

        if (dns is not null)
        {
            if (dns.FirstOrDefault(l => l.IsPublic && !l.Resolved && l.Host == DnsCheck.PublicProbe) is { } publicFail)
                findings.Add(new(Level.Bad, "Network",
                    $"This container cannot resolve {publicFail.Host}, so it has no working DNS for public names. " +
                    "Update checks and every cloud integration will fail. Give it a DNS server with \"dns:\" in docker-compose.yml."));

            var lanFails = dns.Where(l => !l.Resolved && l.Host != DnsCheck.PublicProbe).ToList();
            if (lanFails.Count > 0)
                findings.Add(new(Level.Warn, "Network",
                    $"{lanFails.Count} host name(s) used by your connections do not resolve here: " +
                    string.Join(", ", lanFails.Select(l => l.Host)) + ". Use IP addresses for those."));
        }

        return findings;
    }

    /// <summary>"42 s", "3 min", "2 h" — one unit, enough for a sentence.</summary>
    public static string Seconds(TimeSpan span) => span.TotalSeconds switch
    {
        < 120 => $"{span.TotalSeconds:0} s",
        < 7200 => $"{span.TotalMinutes:0} min",
        _ => $"{span.TotalHours:0.#} h",
    };
}
