using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>The things about itself LabbyTwo can tell you, each of which can be turned off.</summary>
public enum SelfCheck
{
    /// <summary>Rounds of checks repeatedly taking longer than the probe interval.</summary>
    SweepsLate,

    /// <summary>A round of checks that has not finished, or no round starting at all.</summary>
    SweepStuck,

    /// <summary>LabbyTwo unable to see the lab (see <see cref="Blindness"/>) for a quarter of an hour.</summary>
    Blind,

    /// <summary>A background job failing run after run.</summary>
    JobFailing,

    /// <summary>History writes failing — "database is locked".</summary>
    DatabaseWrites,

    /// <summary>The database growing far faster than it usually does.</summary>
    DatabaseGrowth,

    /// <summary>The volume the database is on running out of space.</summary>
    DiskSpace,

    /// <summary>More slow database statements than the budget, while the slow-query log is on.</summary>
    SlowQueries,
}

/// <summary>What one check says right now.</summary>
public enum SelfState
{
    /// <summary>Not enough to go on — just started, or the facts are not available. Changes nothing.</summary>
    Unknown,
    Clear,
    Holds,
}

/// <summary>Free space where the database lives, and how big the database is.</summary>
public sealed record DiskFacts(long Free, long Total, long DatabaseBytes);

/// <summary>The slow-query log's count for the budget window. Null facts mean the log is off.</summary>
public sealed record SlowQueryFacts(int Count, TimeSpan Total, TimeSpan Threshold, TimeSpan Window);

/// <summary>How fast the database has been growing: the last day against the days before it.</summary>
/// <param name="LastDay">Growth in the last 24 hours, in bytes of data (free pages not counted). Null until a day is known.</param>
/// <param name="UsualPerDay">The typical day before that — the median of up to a week. Null until three days are known.</param>
public sealed record GrowthFacts(long? LastDay, long? UsualPerDay, int DaysKnown);

/// <summary>Everything the rules look at, gathered from memory.</summary>
/// <param name="Names">Connection names by id, so a stuck sweep can say what it is waiting on.</param>
public sealed record SelfFacts(
    DateTimeOffset At,
    TimeSpan Uptime,
    MonitorStatus Monitor,
    IReadOnlyList<JobRun> Jobs,
    WriteHealthStatus Writes,
    GrowthFacts? Growth,
    DiskFacts? Disk,
    SlowQueryFacts? Slow,
    IReadOnlyDictionary<string, string> Names);

/// <summary>
/// One check's verdict, with the words for both ends: what to send if it has started, and
/// what to send if it has stopped. Both are worked out every time, from the numbers as they
/// are now, so the "cleared" notice says what it looks like at the moment it cleared.
/// </summary>
/// <param name="Key">What is being watched: one per check, or per job for <see cref="SelfCheck.JobFailing"/>.</param>
public sealed record SelfReading(
    string Key,
    SelfCheck Check,
    SelfState State,
    SystemHealth.Level Level,
    string Title,
    string Body,
    string ClearTitle,
    string ClearBody);

/// <summary>
/// How LabbyTwo judges itself. Pure: handed the facts and which keys are already firing, it
/// says what each check sees — so every threshold is a test rather than a night of messages.
///
/// Each check has two thresholds, a higher one to start and a lower one to keep going
/// (hysteresis), plus a sustain time before it starts and a quiet time before it ends
/// (<see cref="Sustain"/>, <see cref="ClearAfter"/>). Between them a number hovering at a
/// threshold says so once, not every time it crosses.
///
/// Nothing here reads the database. Every fact comes from what LabbyTwo already keeps in
/// memory — the monitor's bookkeeping, the job runner's last runs, the history writer's
/// tally, the slow-query log — or from the file system, which is where a check about the
/// database has to look when the database is the thing that is stuck.
/// </summary>
public static class SelfWatchRules
{
    /// <summary>How many recent sweeps the lateness check needs before it will judge.</summary>
    public const int SweepSample = HealthMonitor.SweepsRemembered;

    /// <summary>Late sweeps out of <see cref="SweepSample"/> that start the alert, and that keep it going.</summary>
    public const int LateToStart = 5, LateToStay = 3;

    /// <summary>Failures in a row that make a job "failing"; two for one that runs hourly or less often.</summary>
    public const int JobFailuresToStart = 3, SlowJobFailuresToStart = 2;

    /// <summary>Of the last ten minutes, how many with a failed history write start the alert.</summary>
    public const int FailedMinutesToStart = 3;

    /// <summary>Free space that starts the disk alert — below either — and that ends it — above both.</summary>
    public const long DiskFreeToStart = 1L << 30, DiskFreeToClear = 3L << 29;

    public const double DiskShareToStart = 0.05, DiskShareToClear = 0.075;

    /// <summary>Slow statements, or time spent in them, over <see cref="SlowWindow"/> that exceed the budget.</summary>
    public const int SlowCountBudget = 50;

    public static readonly TimeSpan SlowTimeBudget = TimeSpan.FromMinutes(1);

    public static readonly TimeSpan SlowWindow = TimeSpan.FromMinutes(10);

    /// <summary>Growth that is never news, however fast relative to usual: 50 MB a day.</summary>
    public const long GrowthFloor = 50L << 20;

    /// <summary>"Usual" is never taken as less than this, so a database that barely grows does not alarm at 3 MB.</summary>
    public const long UsualFloor = 5L << 20;

    /// <summary>How long a check must hold before it is reported.</summary>
    public static TimeSpan Sustain(SelfCheck check) => check switch
    {
        SelfCheck.DatabaseGrowth => TimeSpan.FromHours(1),
        SelfCheck.DiskSpace or SelfCheck.SlowQueries => TimeSpan.FromMinutes(5),
        _ => TimeSpan.Zero,
    };

    /// <summary>How long a reported check must stay clear before it is reported as cleared.</summary>
    public static TimeSpan ClearAfter(SelfCheck check) => check switch
    {
        SelfCheck.SweepsLate => TimeSpan.FromMinutes(5),
        SelfCheck.SweepStuck => TimeSpan.FromMinutes(1),
        SelfCheck.DatabaseGrowth => TimeSpan.FromHours(1),
        SelfCheck.DiskSpace or SelfCheck.SlowQueries => TimeSpan.FromMinutes(10),
        _ => TimeSpan.Zero,
    };

    /// <summary>What the settings list calls each one.</summary>
    public static string Describe(SelfCheck check) => check switch
    {
        SelfCheck.SweepsLate => "Checks keep running late (5 of the last 10 rounds over the probe interval)",
        SelfCheck.SweepStuck => "A round of checks is stuck, or the monitor has stopped",
        SelfCheck.Blind => "LabbyTwo can't see the lab for 15 minutes (DNS, Docker, its own database)",
        SelfCheck.JobFailing => "A background job fails again and again (3 runs in a row)",
        SelfCheck.DatabaseWrites => "Readings can't be saved — \"database is locked\"",
        SelfCheck.DatabaseGrowth => "The database grows far faster than it usually does",
        SelfCheck.DiskSpace => "The disk the database is on is nearly full (under 5% or 1 GB free)",
        SelfCheck.SlowQueries => "Too many slow database statements (needs Labby__SlowQueryMs)",
        _ => check.ToString(),
    };

    /// <summary>The stored name of a check, for the settings list: "sweeps-late".</summary>
    public static string Stored(SelfCheck check) => check switch
    {
        SelfCheck.SweepsLate => "sweeps-late",
        SelfCheck.SweepStuck => "sweep-stuck",
        SelfCheck.Blind => "blind",
        SelfCheck.JobFailing => "jobs",
        SelfCheck.DatabaseWrites => "db-writes",
        SelfCheck.DatabaseGrowth => "db-growth",
        SelfCheck.DiskSpace => "disk",
        SelfCheck.SlowQueries => "slow-queries",
        _ => check.ToString().ToLowerInvariant(),
    };

    public static SelfCheck? Parse(string stored) =>
        Enum.GetValues<SelfCheck>().Where(c => Stored(c) == stored.Trim()).Select(c => (SelfCheck?)c).FirstOrDefault();

    /// <summary>Every check's verdict.</summary>
    /// <param name="firing">Keys already reported, which are held to the lower threshold.</param>
    public static IReadOnlyList<SelfReading> Evaluate(SelfFacts facts, IReadOnlySet<string> firing)
    {
        var readings = new List<SelfReading>
        {
            SweepsLate(facts, firing.Contains("sweeps-late")),
            SweepStuck(facts),
            Blind(facts, firing.Contains("blind")),
            Writes(facts, firing.Contains("db-writes")),
            Growth(facts, firing.Contains("db-growth")),
            Disk(facts, firing.Contains("disk")),
            Slow(facts, firing.Contains("slow-queries")),
        };
        readings.AddRange(facts.Jobs.Select(job => Job(job, firing.Contains(JobKey(job.Name)))));
        return readings;
    }

    public static string JobKey(string name) => "job:" + name;

    private static SelfReading SweepsLate(SelfFacts facts, bool firing)
    {
        var m = facts.Monitor;
        var sweeps = m.RecentSweeps;
        var period = m.SweepPeriod;
        var late = sweeps.Count(d => d > period);
        var slowest = sweeps.Count > 0 ? sweeps.Max() : TimeSpan.Zero;
        var state = sweeps.Count < SweepSample ? SelfState.Unknown
            : late >= (firing ? LateToStay : LateToStart) ? SelfState.Holds
            : SelfState.Clear;
        var slowNames = SlowestNames(facts);
        return new SelfReading("sweeps-late", SelfCheck.SweepsLate, state, SystemHealth.Level.Warn,
            "LabbyTwo's checks are running late",
            $"{late} of the last {sweeps.Count} rounds of checks took longer than the {Words(period)} they have; the slowest took " +
            $"{Words(slowest)}. Statuses on the dashboard are older than they look and alerts arrive late. Usually one connection " +
            $"that answers slowly holds every round up{slowNames} — the health page lists the slowest checks. Give that one a " +
            "shorter timeout or fix what it waits on, or set LABBY_PROBE_SECONDS higher.",
            "LabbyTwo's checks are on time again",
            $"{late} of the last {sweeps.Count} rounds ran over the {Words(period)} interval.");
    }

    private static string SlowestNames(SelfFacts facts)
    {
        var names = facts.Monitor.Slowest
            .Where(p => p.Duration > facts.Monitor.SweepPeriod / 2)
            .Take(3)
            .Select(p => $"{Name(facts, p.ConnectionId)} ({Words(p.Duration)})")
            .ToList();
        return names.Count == 0 ? "" : $": the slowest now are {string.Join(", ", names)}";
    }

    private static SelfReading SweepStuck(SelfFacts facts)
    {
        var m = facts.Monitor;
        var now = facts.At;
        var period = m.SweepPeriod;
        const string clearTitle = "LabbyTwo's checks are running again";

        if (m.StartedAt is null)
        {
            var never = facts.Uptime > TimeSpan.FromMinutes(2);
            return new SelfReading("sweep-stuck", SelfCheck.SweepStuck, never ? SelfState.Holds : SelfState.Unknown,
                SystemHealth.Level.Bad, "LabbyTwo's monitor never started",
                $"LabbyTwo has been running for {Words(facts.Uptime)} and its monitor has not started, so nothing on the dashboard " +
                "is being checked. Something held up startup — the log from when LabbyTwo started says what. Restarting LabbyTwo " +
                "usually gets it going.",
                clearTitle, "The monitor has started.");
        }

        if (m.IsSweepStuck(now))
        {
            var waiting = m.InFlight.Count > 0
                ? $" It is still waiting on {string.Join(", ", m.InFlight.Take(5).Select(p => Name(facts, p.ConnectionId)))}" +
                  (m.InFlight.Count > 5 ? $" and {m.InFlight.Count - 5} more." : ".")
                : " Every check has answered, so something after them — saving the results, an alert being sent — is what it is waiting on.";
            return new SelfReading("sweep-stuck", SelfCheck.SweepStuck, SelfState.Holds, SystemHealth.Level.Bad,
                "LabbyTwo's checks are stuck",
                $"A round of checks has been running for {Words(m.RunningFor(now)!.Value)}; it should take well under " +
                $"{Words(period)}. Every status on the dashboard is frozen where it was, and nothing is alerted on until it " +
                $"finishes.{waiting} The health page shows it live. If it does not clear, restart LabbyTwo.",
                clearTitle, $"A round of checks finished; the last took {Words(m.LastSweepDuration ?? TimeSpan.Zero)}.");
        }

        if (m.CurrentSweepStarted is null && m.LastSweepFinished is { } finished && now - finished > period * 3)
        {
            return new SelfReading("sweep-stuck", SelfCheck.SweepStuck, SelfState.Holds, SystemHealth.Level.Bad,
                "LabbyTwo's monitor has stopped",
                $"The last round of checks finished {Words(now - finished)} ago, but one should start every {Words(period)}. " +
                "Nothing on the dashboard is being checked. The log will say why the monitor stopped; restarting LabbyTwo starts it again.",
                clearTitle, "Rounds of checks are starting on time again.");
        }

        var state = m.LastSweepFinished is null ? SelfState.Unknown : SelfState.Clear;
        return new SelfReading("sweep-stuck", SelfCheck.SweepStuck, state, SystemHealth.Level.Bad,
            "LabbyTwo's checks are stuck", "", clearTitle,
            $"Rounds of checks are finishing again; the last took {Words(m.LastSweepDuration ?? TimeSpan.Zero)}.");
    }

    private static SelfReading Blind(SelfFacts facts, bool firing)
    {
        var blind = facts.Monitor.Blindness;
        var holds = blind.Impaired && (firing || blind.For(facts.At) >= BlindnessRules.NotifyAfter);
        return new SelfReading("blind", SelfCheck.Blind, holds ? SelfState.Holds : SelfState.Clear, SystemHealth.Level.Bad,
            "LabbyTwo can't see the lab",
            blind.Impaired ? $"{blind.Headline} It has lasted {Ago.Duration(blind.For(facts.At))}. {blind.Detail}" : "",
            "LabbyTwo can see the lab again",
            "Checks are being recorded again. Anything that really went down while it could not see is reported by the next " +
            "checks in the usual way.");
    }

    private static SelfReading Job(JobRun job, bool firing)
    {
        var needed = firing ? 1 : job.Interval >= TimeSpan.FromHours(1) ? SlowJobFailuresToStart : JobFailuresToStart;
        var state = job.At is null ? SelfState.Unknown
            : !job.Ok && job.ConsecutiveFailures >= needed ? SelfState.Holds
            : SelfState.Clear;
        return new SelfReading(JobKey(job.Name), SelfCheck.JobFailing, state, SystemHealth.Level.Warn,
            $"LabbyTwo's \"{job.Name}\" job keeps failing",
            $"The background job \"{job.Name}\" has failed {job.ConsecutiveFailures} times in a row, and runs every " +
            $"{Words(job.Interval)}. The last error: {job.Message}. What it does is not happening until it works. The health page " +
            "lists it under Background jobs, and the log has the whole error.",
            $"LabbyTwo's \"{job.Name}\" job is working again",
            $"It ran without an error{(job.At is { } at ? $" at {at:HH:mm}" : "")}.");
    }

    private static SelfReading Writes(SelfFacts facts, bool firing)
    {
        var w = facts.Writes;
        var state = w.Compacting ? SelfState.Unknown
            : w.FailedMinutes >= (firing ? 1 : FailedMinutesToStart) ? SelfState.Holds
            : SelfState.Clear;
        return new SelfReading("db-writes", SelfCheck.DatabaseWrites, state, SystemHealth.Level.Bad,
            "LabbyTwo can't save readings",
            $"Saving readings failed in {w.FailedMinutes} of the last 10 minutes ({w.Failures} failed, {w.Succeeded} worked). " +
            $"The last error: \"{w.LastError}\". Charts will have gaps, and statuses and alerts are recorded in the same database. " +
            "Usually something else has the database open and busy — a backup or sync tool copying labbytwo.db, sqlite3 on the " +
            "command line — or the disk is too busy to keep up. Settings → Storage shows how big it has become; " +
            "Labby__SlowQueryMs=200 makes the log say which statements are slow.",
            "LabbyTwo is saving readings again",
            $"No write has failed for 10 minutes; {w.Succeeded} worked.");
    }

    private static SelfReading Growth(SelfFacts facts, bool firing)
    {
        if (facts.Growth is not { LastDay: { } day, UsualPerDay: { } usual } growth)
        {
            return new SelfReading("db-growth", SelfCheck.DatabaseGrowth, SelfState.Unknown, SystemHealth.Level.Warn,
                "LabbyTwo's database is growing fast", "", "LabbyTwo's database is growing at its usual rate", "");
        }

        var baseline = Math.Max(usual, UsualFloor);
        var holds = firing
            ? day >= GrowthFloor * 3 / 5 && day >= baseline * 2
            : day >= GrowthFloor && day >= baseline * 3;
        var runway = facts.Disk is { } disk && day > 0 ? $" At this rate the disk's {StorageFormat.Bytes(disk.Free)} free lasts about {Math.Max(1, disk.Free / day)} days." : "";
        return new SelfReading("db-growth", SelfCheck.DatabaseGrowth, holds ? SelfState.Holds : SelfState.Clear, SystemHealth.Level.Warn,
            "LabbyTwo's database is growing fast",
            $"The database took on {StorageFormat.Bytes(day)} in the last day; it usually grows about " +
            $"{StorageFormat.Bytes(Math.Max(0, usual))} a day (the median of the {growth.DaysKnown} days before).{runway} Usually " +
            "something new is reporting far more readings than before — a new connection, or one whose metrics multiplied. " +
            "Settings → Storage shows what each connection writes a day, and can keep less of it or stop recording a metric.",
            "LabbyTwo's database is growing at its usual rate again",
            $"It took on {StorageFormat.Bytes(day)} in the last day, against about {StorageFormat.Bytes(Math.Max(0, usual))} usually.");
    }

    private static SelfReading Disk(SelfFacts facts, bool firing)
    {
        if (facts.Disk is not { Total: > 0 } disk)
        {
            return new SelfReading("disk", SelfCheck.DiskSpace, SelfState.Unknown, SystemHealth.Level.Bad,
                "LabbyTwo's disk is nearly full", "", "LabbyTwo's disk has room again", "");
        }

        var share = (double)disk.Free / disk.Total;
        var holds = firing
            ? disk.Free < DiskFreeToClear || share < DiskShareToClear
            : disk.Free < DiskFreeToStart || share < DiskShareToStart;
        return new SelfReading("disk", SelfCheck.DiskSpace, holds ? SelfState.Holds : SelfState.Clear, SystemHealth.Level.Bad,
            "LabbyTwo's disk is nearly full",
            $"The volume LabbyTwo's database is on has {StorageFormat.Bytes(disk.Free)} free of {StorageFormat.Bytes(disk.Total)} " +
            $"({share * 100:0}%). When it fills, LabbyTwo cannot save anything — history, settings, alerts already sent — and the " +
            $"database may not open. The database is {StorageFormat.Bytes(disk.DatabaseBytes)}. Free some space on that volume; " +
            "Settings → Storage shows what LabbyTwo keeps and can keep less.",
            "LabbyTwo's disk has room again",
            $"{StorageFormat.Bytes(disk.Free)} free of {StorageFormat.Bytes(disk.Total)} ({share * 100:0}%).");
    }

    private static SelfReading Slow(SelfFacts facts, bool firing)
    {
        if (facts.Slow is not { } slow)
        {
            return new SelfReading("slow-queries", SelfCheck.SlowQueries, SelfState.Unknown, SystemHealth.Level.Warn,
                "LabbyTwo's database is slow", "", "LabbyTwo's database is keeping up again", "");
        }

        var countBudget = firing ? SlowCountBudget / 2 : SlowCountBudget;
        var timeBudget = firing ? SlowTimeBudget / 2 : SlowTimeBudget;
        var holds = slow.Count >= countBudget || slow.Total >= timeBudget;
        var minutes = $"{slow.Window.TotalMinutes:0} minutes";
        return new SelfReading("slow-queries", SelfCheck.SlowQueries, holds ? SelfState.Holds : SelfState.Clear, SystemHealth.Level.Warn,
            "LabbyTwo's database is slow",
            $"{slow.Count} database statements took longer than {Words(slow.Threshold)} in the last {minutes}, " +
            $"{Words(slow.Total)} between them — over the budget of {SlowCountBudget} slow statements or " +
            $"{Words(SlowTimeBudget)} of waiting in {minutes}. Pages and the monitor are waiting on the disk. The log names each " +
            "one and what asked for it (search it for \"Slow query\"), and every few minutes lists the statements that read the most.",
            "LabbyTwo's database is keeping up again",
            $"{slow.Count} slow statements in the last {minutes}, {Words(slow.Total)} between them.");
    }

    private static string Name(SelfFacts facts, string connectionId) =>
        facts.Names.TryGetValue(connectionId, out var name) ? name : connectionId;

    /// <summary>"450 ms", "42 s", "3 min", "2 h".</summary>
    public static string Words(TimeSpan span) => span < TimeSpan.FromSeconds(1)
        ? $"{span.TotalMilliseconds:0} ms"
        : SystemHealth.Seconds(span);
}
