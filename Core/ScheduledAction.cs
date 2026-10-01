using System.Globalization;

namespace LabbyTwo.Core;

/// <summary>The four ways a <see cref="ActionSchedule"/> can say when.</summary>
public enum ScheduleKind
{
    /// <summary>Chosen weekdays at one or more times: "Sundays at 04:00".</summary>
    Weekly,

    /// <summary>Every N minutes or hours: "every 15 minutes", "every 6 hours".</summary>
    Interval,

    /// <summary>One day of the month at one or more times: "the 1st at 03:00".</summary>
    Monthly,

    /// <summary>A five-field cron expression, for anything the other three cannot say.</summary>
    Cron,
}

/// <summary>
/// When a scheduled action runs. Pure — the zone and the instant are always passed in — so
/// the nights the clocks change are pinned by tests, as for <see cref="WeeklySchedule"/> and
/// <see cref="MuteWindow"/>, rather than met at four in the morning on somebody's server.
///
/// Every calendar kind (weekly, monthly, cron, and intervals of whole hours) is a set of
/// wall-clock times on each local date, turned into instants by <see cref="WeeklySchedule.At"/>,
/// which is what gives them the same behaviour on the nights the clocks change:
/// <list type="bullet">
/// <item>A time the clocks skip — 02:30 when they spring forward — runs just after the jump,
/// at 03:30 on the new clock, rather than being lost for the year.</item>
/// <item>A time that happens twice when they fall back runs once, the first time. A
/// restart at 01:30 is never done twice because the hour repeated.</item>
/// </list>
///
/// Intervals shorter than an hour, or not a whole number of hours, are different on
/// purpose: "every 15 minutes" means every fifteen real minutes, counted from a fixed point
/// (the Unix epoch) rather than from whenever LabbyTwo last started, so a restart never
/// shifts them and a clock change neither doubles nor skips any. Whole hours count from
/// local midnight — "every 6 hours" is 00:00, 06:00, 12:00 and 18:00, which is what people mean.
/// </summary>
public sealed record ActionSchedule
{
    public ScheduleKind Kind { get; init; } = ScheduleKind.Weekly;

    /// <summary>For <see cref="ScheduleKind.Weekly"/>: the days it runs on.</summary>
    public IReadOnlyList<DayOfWeek> Days { get; init; } = [];

    /// <summary>For <see cref="ScheduleKind.Weekly"/> and <see cref="ScheduleKind.Monthly"/>: the local times it runs at.</summary>
    public IReadOnlyList<TimeOnly> Times { get; init; } = [];

    /// <summary>For <see cref="ScheduleKind.Interval"/>: how many minutes apart, <see cref="MinimumMinutes"/> to a day.</summary>
    public int IntervalMinutes { get; init; } = 60;

    /// <summary>
    /// For <see cref="ScheduleKind.Monthly"/>: 1–31. A month too short for the day runs on its
    /// last day instead — "the 31st" in February is the 28th — because a monthly job that
    /// silently skips five months a year is not monthly.
    /// </summary>
    public int DayOfMonth { get; init; } = 1;

    /// <summary>For <see cref="ScheduleKind.Cron"/>: the expression.</summary>
    public string Cron { get; init; } = "";

    /// <summary>
    /// The least time between two runs. A restart every minute is a crash loop somebody
    /// built on purpose, and the job that runs these ticks once a minute anyway.
    /// </summary>
    public const int MinimumMinutes = 5;

    /// <summary>The longest interval: past a day, a weekly or cron schedule says it better.</summary>
    public const int MaximumIntervalMinutes = 24 * 60;

    /// <summary>
    /// How far ahead to look for the next run. Eight years is enough for a cron on the
    /// 29th of February that also wants it to be a particular month; a valid schedule that
    /// still finds nothing in that time is one <see cref="Problem"/> rejects.
    /// </summary>
    private const int SearchDays = 366 * 8 + 2;

    /// <summary>Whether an interval runs in real minutes rather than on the clock face.</summary>
    public bool IsRealTime => Kind == ScheduleKind.Interval && IntervalMinutes % 60 != 0;

    private CronExpression? ParsedCron => Kind == ScheduleKind.Cron ? CronExpression.Parse(Cron, out _) : null;

    /// <summary>The wall-clock times it runs at on <paramref name="date"/>, local to whatever zone the caller means.</summary>
    public IReadOnlyList<TimeOnly> TimesOn(DateOnly date) => TimesOn(date, ParsedCron);

    private IReadOnlyList<TimeOnly> TimesOn(DateOnly date, CronExpression? cron) => Kind switch
    {
        ScheduleKind.Weekly => Days.Contains(date.DayOfWeek) ? Times : [],
        ScheduleKind.Monthly => date.Day == Math.Min(Math.Clamp(DayOfMonth, 1, 31), DateTime.DaysInMonth(date.Year, date.Month)) ? Times : [],
        ScheduleKind.Interval => IsRealTime ? [] : HourSlots(),
        ScheduleKind.Cron => cron is not null && cron.Matches(date) ? cron.Times : [],
        _ => [],
    };

    private IReadOnlyList<TimeOnly> HourSlots()
    {
        var hours = Math.Clamp(IntervalMinutes / 60, 1, 24);
        return [.. Enumerable.Range(0, 24).Where(h => h % hours == 0).Select(h => new TimeOnly(h, 0))];
    }

    /// <summary>The first run strictly after <paramref name="after"/>, or null when it never runs.</summary>
    public DateTimeOffset? NextAfter(DateTimeOffset after, TimeZoneInfo zone)
    {
        if (IsRealTime)
        {
            var step = Step;
            var next = Math.Floor(after.ToUnixTimeSeconds() / (double)step) * step + step;
            return TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds((long)next), zone);
        }

        var cron = ParsedCron;
        var today = LocalDate(after, zone);
        // From yesterday: a time just after midnight can still belong to the day before
        // when the zone's offset moved in between. Harmless otherwise — it is in the past.
        for (var ahead = -1; ahead <= SearchDays; ahead++)
        {
            var date = today.AddDays(ahead);
            DateTimeOffset? best = null;
            // Collected rather than taking the first: a time in a skipped hour moves past
            // the jump, so on that one date the order of instants need not be the order of times.
            foreach (var time in TimesOn(date, cron))
            {
                var at = WeeklySchedule.At(date, time, zone);
                if (at > after && (best is null || at < best))
                    best = at;
            }
            if (best is not null)
                return best;
        }
        return null;
    }

    /// <summary>The most recent run at or before <paramref name="now"/>, or null when there has been none in the search window.</summary>
    public DateTimeOffset? LatestAtOrBefore(DateTimeOffset now, TimeZoneInfo zone)
    {
        if (IsRealTime)
        {
            var step = Step;
            var latest = Math.Floor(now.ToUnixTimeSeconds() / (double)step) * step;
            return TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds((long)latest), zone);
        }

        var cron = ParsedCron;
        var today = LocalDate(now, zone);
        for (var back = -1; back <= SearchDays; back++)
        {
            var date = today.AddDays(-back);
            DateTimeOffset? best = null;
            foreach (var time in TimesOn(date, cron))
            {
                var at = WeeklySchedule.At(date, time, zone);
                if (at <= now && (best is null || at > best))
                    best = at;
            }
            if (best is not null)
                return best;
        }
        return null;
    }

    /// <summary>The next <paramref name="count"/> runs after <paramref name="after"/>, for "next: …" on the page.</summary>
    public IReadOnlyList<DateTimeOffset> Upcoming(DateTimeOffset after, TimeZoneInfo zone, int count)
    {
        var list = new List<DateTimeOffset>();
        var from = after;
        while (list.Count < count && NextAfter(from, zone) is { } next)
        {
            list.Add(next);
            from = next;
        }
        return list;
    }

    private long Step => Math.Max(MinimumMinutes, IntervalMinutes) * 60L;

    private static DateOnly LocalDate(DateTimeOffset at, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);

    /// <summary>
    /// Why this schedule cannot be saved, or null. Each problem would make a schedule that
    /// silently never runs, or runs far more often than anyone meant, which is worse than
    /// being told.
    /// </summary>
    public string? Problem()
    {
        switch (Kind)
        {
            case ScheduleKind.Weekly:
                if (Days.Count == 0)
                    return "Pick at least one day, or it never runs.";
                return TimesProblem(Times);

            case ScheduleKind.Monthly:
                if (DayOfMonth is < 1 or > 31)
                    return "The day of the month has to be between 1 and 31.";
                return TimesProblem(Times);

            case ScheduleKind.Interval:
                if (IntervalMinutes < MinimumMinutes)
                    return $"Every {IntervalMinutes} minutes is too often — the shortest is every {MinimumMinutes} minutes.";
                if (IntervalMinutes > MaximumIntervalMinutes)
                    return "Past a day, use days of the week or a day of the month instead.";
                return null;

            case ScheduleKind.Cron:
                if (CronExpression.Parse(Cron, out var problem) is not { } cron)
                    return problem;
                if (Gap(cron.Minutes) is { } gap && gap < MinimumMinutes)
                    return $"That runs {gap} minute{(gap == 1 ? "" : "s")} apart — the shortest allowed is {MinimumMinutes}.";
                // A valid expression that never matches a real date: 0 0 31 2 *.
                if (NextAfter(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc) is null)
                    return "That expression never matches a real date, so it would never run.";
                return null;

            default:
                return "Choose when it runs.";
        }
    }

    /// <summary>Times on a day, at least <see cref="MinimumMinutes"/> apart — across midnight too.</summary>
    private static string? TimesProblem(IReadOnlyList<TimeOnly> times)
    {
        if (times.Count == 0)
            return "Add at least one time, or it never runs.";
        var minutes = times.Select(t => t.Hour * 60 + t.Minute).Distinct().Order().ToList();
        if (minutes.Count != times.Count)
            return "The same time is in the list twice.";
        if (Gap(minutes, 24 * 60) is { } gap && gap < MinimumMinutes)
            return $"Two of the times are {gap} minute{(gap == 1 ? "" : "s")} apart — the shortest allowed is {MinimumMinutes}.";
        return null;
    }

    /// <summary>The smallest gap between sorted values, going round the end of the range; null for fewer than two.</summary>
    private static int? Gap(IReadOnlyList<int> sorted, int range = 60)
    {
        if (sorted.Count < 2)
            return null;
        var smallest = sorted[0] + range - sorted[^1];
        for (var i = 1; i < sorted.Count; i++)
            smallest = Math.Min(smallest, sorted[i] - sorted[i - 1]);
        return smallest;
    }

    /// <summary>"Sundays at 04:00", "Every 15 minutes", "On the 1st of every month at 03:00".</summary>
    public string Describe() => Kind switch
    {
        ScheduleKind.Weekly => $"{MuteWindow.DaysText(Days)} at {TimesText(Times)}",
        ScheduleKind.Monthly => $"On the {CronExpression.Ordinal(DayOfMonth)} of every month at {TimesText(Times)}"
                                + (DayOfMonth > 28 ? " (the last day, in a shorter month)" : ""),
        ScheduleKind.Interval => IntervalText(IntervalMinutes),
        ScheduleKind.Cron => CronExpression.Parse(Cron, out _)?.Describe() ?? "Not a valid cron expression",
        _ => "",
    };

    private static string IntervalText(int minutes) => minutes switch
    {
        24 * 60 => "Every day at midnight",
        60 => "Every hour, on the hour",
        _ when minutes % 60 == 0 => $"Every {minutes / 60} hours, from midnight",
        _ => $"Every {minutes} minutes",
    };

    private static string TimesText(IReadOnlyList<TimeOnly> times) =>
        CronExpression.Join([.. times.Order().Select(StoredTime)]);

    // ---- stored forms -----------------------------------------------------------------

    public static string StoredKind(ScheduleKind kind) => kind.ToString().ToLowerInvariant();

    /// <summary>A stored kind, read back. Anything unrecognised is weekly, whose empty day list then refuses to run.</summary>
    public static ScheduleKind ParseKind(string? stored) =>
        Enum.TryParse<ScheduleKind>(stored?.Trim(), ignoreCase: true, out var kind) && Enum.IsDefined(kind)
            ? kind
            : ScheduleKind.Weekly;

    public static string StoredTime(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"04:00,16:30". Short and readable in a backup.</summary>
    public static string StoredTimes(IEnumerable<TimeOnly> times) => string.Join(',', times.Order().Select(StoredTime));

    /// <summary>Stored times, read back. Anything unreadable is dropped rather than guessed at.</summary>
    public static IReadOnlyList<TimeOnly> ParseTimes(string? stored) =>
    [
        .. (stored ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => TimeOnly.TryParse(part, CultureInfo.InvariantCulture, out var time) ? time : (TimeOnly?)null)
            .OfType<TimeOnly>()
            .Distinct()
            .Order(),
    ];
}

/// <summary>What a scheduled action acts on.</summary>
public enum ScheduledTarget
{
    /// <summary>Restart, start or stop one container on a Docker connection.</summary>
    Container,

    /// <summary>One of a connection's own action buttons, through the ActionRunner every button uses.</summary>
    ProviderAction,
}

/// <summary>What a scheduled action does to a container.</summary>
public enum ContainerVerb
{
    Restart,
    Start,
    Stop,
}

/// <summary>
/// "Restart the flaky container every Sunday at 4am", "trigger the backup script nightly":
/// an action button pressed on a timetable instead of by hand.
///
/// Built from parts that already exist, on purpose. The target is exactly what self-healing
/// can act on — a container on a Docker connection, or one of a connection's own action
/// buttons — and it goes through the same preparation (<c>RemediationActions</c>), so the
/// same guardrails apply without being written twice: never LabbyTwo's own container, a
/// protected container or a dangerous action only with an explicit opt-in, and an action
/// that asks a question never, because nobody is there to answer it.
/// </summary>
public sealed record ScheduledAction
{
    public string Id { get; init; } = Ids.New();
    public string Name { get; init; } = "";
    public bool Enabled { get; init; } = true;

    public ScheduledTarget Target { get; init; } = ScheduledTarget.Container;

    /// <summary>The Docker connection for a container, or the connection whose action runs.</summary>
    public string TargetConnectionId { get; init; } = "";

    /// <summary>The container's name or Compose service. Ignored for an action.</summary>
    public string Container { get; init; } = "";

    public ContainerVerb Verb { get; init; } = ContainerVerb.Restart;

    /// <summary>The provider action's id. Ignored for a container.</summary>
    public string ActionId { get; init; } = "";

    public ActionSchedule Schedule { get; init; } = new();

    /// <summary>
    /// Run even while the lab is in maintenance. Off by default: maintenance is somebody
    /// saying "I am working on it", and a restart they did not ask for in the middle of their
    /// own work is the last thing they want. On for the jobs that do not care — a nightly
    /// backup script should still run while you tinker with the reverse proxy.
    /// </summary>
    public bool RunInMaintenance { get; init; }

    /// <summary>Allowed to act on a protected container, or to run an action the provider marks dangerous.</summary>
    public bool AllowProtected { get; init; }

    /// <summary>Tell the alert channels when a run fails. Quiet hours and mute windows still apply.</summary>
    public bool NotifyOnFailure { get; init; } = true;

    /// <summary>The one channel to tell; empty for every channel.</summary>
    public string ChannelId { get; init; } = "";

    /// <summary>
    /// The instant up to which every scheduled run has been dealt with — run, skipped or
    /// missed. The same idea as <see cref="WeeklySchedule.CoveredKey"/>: set to "now" when the
    /// action is saved, so an edit never runs an occurrence it "missed" a minute ago, and
    /// again at every run, so a restart knows exactly what it has and has not done.
    /// </summary>
    public DateTimeOffset? CoveredUntil { get; init; }

    /// <summary>Whether enough is filled in to run.</summary>
    public bool IsComplete => TargetConnectionId.Length > 0 && Target switch
    {
        ScheduledTarget.Container => Container.Trim().Length > 0,
        _ => ActionId.Length > 0,
    };

    /// <summary>Why this cannot be saved, or null.</summary>
    public string? Problem()
    {
        if (Name.Trim().Length == 0)
            return "Give it a name — it is what the change feed and any notification call it.";
        if (!IsComplete)
            return Target == ScheduledTarget.Container
                ? "Choose the Docker connection and write the container's name."
                : "Choose the connection and the action to run.";
        return Schedule.Problem();
    }

    public static string StoredTarget(ScheduledTarget target) => target == ScheduledTarget.ProviderAction ? "action" : "container";

    public static ScheduledTarget ParseTarget(string? stored) =>
        stored == "action" ? ScheduledTarget.ProviderAction : ScheduledTarget.Container;

    public static string StoredVerb(ContainerVerb verb) => verb.ToString().ToLowerInvariant();

    public static ContainerVerb ParseVerb(string? stored) =>
        Enum.TryParse<ContainerVerb>(stored?.Trim(), ignoreCase: true, out var verb) && Enum.IsDefined(verb)
            ? verb
            : ContainerVerb.Restart;
}

/// <summary>One run of a scheduled action, for the short history each one keeps.</summary>
/// <param name="Trigger">One of <see cref="ScheduledTriggers"/>.</param>
/// <param name="Outcome">One of <see cref="ScheduledOutcomes"/>.</param>
/// <param name="Message">What happened, in words: the action's own answer, or why it did not run.</param>
public sealed record ScheduledRun(
    string ActionId,
    DateTimeOffset At,
    string Trigger,
    string Outcome,
    string Message,
    TimeSpan Duration)
{
    public long Id { get; init; }

    public bool Ok => Outcome == ScheduledOutcomes.Ok;

    /// <summary>The status dot: green for done, red for failed, grey for skipped.</summary>
    public string Dot => Outcome switch
    {
        ScheduledOutcomes.Ok => "status-up",
        ScheduledOutcomes.Failed => "status-down",
        _ => "status-unknown",
    };
}

/// <summary>Why a run happened. Stored as written, so never rename one.</summary>
public static class ScheduledTriggers
{
    /// <summary>Its time came.</summary>
    public const string Schedule = "schedule";

    /// <summary>Its time came while LabbyTwo was not running, and it is still within the grace period.</summary>
    public const string CatchUp = "catch-up";

    /// <summary>Somebody pressed "Run now".</summary>
    public const string Manual = "manual";
}

/// <summary>How a run went. Stored as written, so never rename one.</summary>
public static class ScheduledOutcomes
{
    public const string Ok = "ok";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
}

/// <summary>
/// The decisions, apart from anything that runs or stores them, so each is a test rather
/// than a night spent waiting for a clock.
///
/// The one rule that matters is about time LabbyTwo was not running. Only the most recent
/// scheduled moment is ever considered: if it is within the grace period it runs, once; if
/// it is older, it is recorded as missed and nothing runs. Being down for a weekend never
/// comes back as a burst of every restart it slept through — the same catch-up rule as
/// <see cref="WeeklySchedule.Decide"/>, with a grace period because "restart plex at 4am"
/// done at 4pm is a restart in the middle of somebody's film.
/// </summary>
public static class ScheduledRules
{
    /// <summary>What to do about one action right now.</summary>
    public enum Step
    {
        /// <summary>Not due yet — see <see cref="Decision.Next"/>.</summary>
        Wait,

        /// <summary>Due: run it.</summary>
        Run,

        /// <summary>Due, but the moment passed longer ago than the grace period. Record it and move on.</summary>
        Missed,
    }

    /// <param name="Next">When it is next due, for <see cref="Step.Wait"/>. Null when it never runs.</param>
    /// <param name="Occurrence">The scheduled moment being run or missed.</param>
    /// <param name="Late">Run well after its moment — LabbyTwo was down — so the history says "caught up".</param>
    public sealed record Decision(Step Step, DateTimeOffset? Next, DateTimeOffset? Occurrence = null, bool Late = false);

    /// <summary>
    /// Anything later than this after its moment counts as a catch-up rather than an
    /// ordinary run. The job ticks every minute, and a tick can wait behind a slow run.
    /// </summary>
    public static readonly TimeSpan LateAfter = TimeSpan.FromMinutes(5);

    public static Decision Decide(ScheduledAction action, DateTimeOffset now, TimeZoneInfo zone, TimeSpan grace)
    {
        if (!action.Enabled || action.Problem() is not null)
            return new Decision(Step.Wait, null);

        // Never armed — an action from before this column, or one written by hand. Arming
        // it now means it waits for its next moment rather than guessing about the past.
        var covered = action.CoveredUntil ?? now;
        if (action.Schedule.NextAfter(covered, zone) is not { } due)
            return new Decision(Step.Wait, null);
        if (due > now)
            return new Decision(Step.Wait, due);

        var latest = action.Schedule.LatestAtOrBefore(now, zone) ?? due;
        return now - latest <= grace
            ? new Decision(Step.Run, null, latest, now - latest > LateAfter)
            : new Decision(Step.Missed, null, latest);
    }
}

/// <summary>
/// The installation-wide switches: whether scheduled actions run at all, and how late a
/// missed one may still run after LabbyTwo comes back. App settings, like
/// <see cref="RemediationSettings"/>, since they are about the installation, not an action.
/// </summary>
public sealed record ScheduledSettings(bool Enabled, int GraceMinutes)
{
    public const string EnabledKey = "scheduled_enabled";
    public const string GraceKey = "scheduled_grace_minutes";

    /// <summary>An hour: late enough to cover a container update, early enough that 4am is not done at lunchtime.</summary>
    public const int DefaultGraceMinutes = 60;

    /// <summary>A week. Anything longer is not a grace period.</summary>
    public const int MaxGraceMinutes = 7 * 24 * 60;

    public TimeSpan Grace => TimeSpan.FromMinutes(Math.Clamp(GraceMinutes, 0, MaxGraceMinutes));

    public static ScheduledSettings From(SettingsBag settings) => new(
        settings.GetBool(EnabledKey, true),
        Math.Clamp(settings.GetInt(GraceKey, DefaultGraceMinutes), 0, MaxGraceMinutes));

    public Dictionary<string, string> ToSettings() => new()
    {
        [EnabledKey] = Enabled ? "true" : "false",
        [GraceKey] = Math.Clamp(GraceMinutes, 0, MaxGraceMinutes).ToString(CultureInfo.InvariantCulture),
    };
}
