using System.Globalization;

namespace LabbyTwo.Core;

/// <summary>What a <see cref="MuteWindow"/> holds back.</summary>
public enum MuteScope
{
    /// <summary>Every rule and every down alert. Quiet hours with a name and a weekday.</summary>
    Everything,

    /// <summary>Every rule on the chosen connections, and those connections going down.</summary>
    Connections,

    /// <summary>Only the chosen rules, on whichever connections they watch.</summary>
    Rules,
}

/// <summary>
/// A named, recurring time when chosen alerts are expected and should not be delivered —
/// "NAS scrub, Sundays 01:00–05:00, mutes the QNAP", "Backup window, every day 02:00–03:30,
/// mutes CPU above 90%".
///
/// Not quiet hours, and not maintenance. <see cref="AlertPolicy"/> is one blanket window
/// about *you* ("am I asleep"); <see cref="Maintenance"/> is ad hoc ("I am working on it").
/// This is about *the thing*: a scrub pegs the NAS's disks every Sunday night, so a rule on
/// its disk latency firing then is not news, while the same rule firing on a Tuesday is.
/// Muting the rule for good to get through Sunday would lose Tuesday; this keeps both.
///
/// Muted means the notification is held, not that the alert is hidden: the rule still
/// fires, the dashboard still shows it as firing with "muted by …" beside it, and if it is
/// still firing when the window ends it is delivered then, once. A scrub that finished and
/// left the disks unhappy is exactly what somebody wants to hear about at 05:00.
///
/// Everything here is pure — the zone and the instant are passed in — so the wrap past
/// midnight and the nights the clocks change are pinned by tests, as for
/// <see cref="WeeklySchedule"/>.
/// </summary>
public sealed record MuteWindow
{
    public string Id { get; init; } = Ids.New();
    public string Name { get; init; } = "";

    /// <summary>
    /// The days the window *starts* on. A Sunday 23:00–02:00 window runs into Monday
    /// morning and is still Sunday's: that is how people say it, and it means a window
    /// never needs two weekday lists for its two halves.
    /// </summary>
    public IReadOnlyList<DayOfWeek> Days { get; init; } = [];

    public TimeOnly Start { get; init; }

    /// <summary>
    /// When it ends, local. At or before <see cref="Start"/> means the next day — so
    /// 23:00–02:00 wraps midnight, and equal to <see cref="Start"/> is a whole day, which is
    /// the only thing "from 00:00 to 00:00" can sensibly mean on a mute.
    /// </summary>
    public TimeOnly End { get; init; }

    public MuteScope Scope { get; init; } = MuteScope.Everything;

    /// <summary>Connection ids or rule ids, depending on <see cref="Scope"/>. Ignored for everything.</summary>
    public IReadOnlyList<string> Targets { get; init; } = [];

    public bool Enabled { get; init; } = true;

    /// <summary>Whether the window ends on the day after it starts.</summary>
    public bool Wraps => End <= Start;

    /// <summary>
    /// Whether this window, if active, holds back an alert. <paramref name="ruleId"/> is null
    /// for a connection going down, which only an everything or a connection window covers:
    /// a window naming "CPU above 90%" is about that rule, not about the box vanishing.
    /// </summary>
    public bool Covers(string? ruleId, string connectionId) => Scope switch
    {
        MuteScope.Everything => true,
        MuteScope.Connections => Targets.Contains(connectionId),
        MuteScope.Rules => ruleId is not null && Targets.Contains(ruleId),
        _ => false,
    };

    /// <summary>
    /// The occurrence of this window that <paramref name="at"/> falls in, or null. Only the
    /// one starting today and the one starting yesterday can contain it, since no window is
    /// longer than a day.
    ///
    /// Worked out from wall-clock times turned into instants (<see cref="WeeklySchedule.At"/>)
    /// rather than by comparing times of day, so the nights the clocks change behave: a
    /// window from 01:00 to 05:00 on the night they spring forward is three real hours long
    /// and ends at 05:00 on the clock, not at 06:00; on the night they fall back it is five,
    /// and a start in the hour that happens twice is the first of the two.
    /// </summary>
    public (DateTimeOffset Start, DateTimeOffset End)? OccurrenceAt(DateTimeOffset at, TimeZoneInfo zone)
    {
        if (!Enabled || Days.Count == 0)
            return null;

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
        for (var back = 0; back <= 1; back++)
        {
            var date = today.AddDays(-back);
            if (!Days.Contains(date.DayOfWeek))
                continue;

            var (start, end) = Occurrence(date, zone);
            if (at >= start && at < end)
                return (start, end);
        }
        return null;
    }

    public bool IsActive(DateTimeOffset at, TimeZoneInfo zone) => OccurrenceAt(at, zone) is not null;

    /// <summary>The next time this window opens, strictly after <paramref name="after"/>. Null when it never does.</summary>
    public DateTimeOffset? NextStart(DateTimeOffset after, TimeZoneInfo zone)
    {
        if (!Enabled || Days.Count == 0)
            return null;

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(after, zone).DateTime);
        for (var ahead = 0; ahead <= 8; ahead++)
        {
            var date = today.AddDays(ahead);
            if (Days.Contains(date.DayOfWeek) && WeeklySchedule.At(date, Start, zone) is var start && start > after)
                return start;
        }
        return null;
    }

    /// <summary>The window that opens on <paramref name="date"/>, as instants.</summary>
    public (DateTimeOffset Start, DateTimeOffset End) Occurrence(DateOnly date, TimeZoneInfo zone) =>
        (WeeklySchedule.At(date, Start, zone), WeeklySchedule.At(Wraps ? date.AddDays(1) : date, End, zone));

    /// <summary>
    /// The first enabled window muting this alert at <paramref name="at"/>, or null. First
    /// in the order given, which is the store's — by name — so the note on the page is
    /// stable rather than flickering between two overlapping windows.
    /// </summary>
    public static MuteWindow? Muting(
        IEnumerable<MuteWindow> windows, string? ruleId, string connectionId, DateTimeOffset at, TimeZoneInfo zone) =>
        windows.FirstOrDefault(w => w.Covers(ruleId, connectionId) && w.IsActive(at, zone));

    /// <summary>
    /// Why this window cannot be saved, or null. Each problem would make a window that
    /// silently does nothing, which is worse than being told.
    /// </summary>
    public string? Problem()
    {
        if (Name.Trim().Length == 0)
            return "Give it a name — it is what the dashboard says an alert is muted by.";
        if (Days.Count == 0)
            return "Pick at least one day, or it never opens.";
        if (Scope != MuteScope.Everything && Targets.Count == 0)
            return Scope == MuteScope.Rules
                ? "Pick at least one rule to mute, or choose “everything”."
                : "Pick at least one connection to mute, or choose “everything”.";
        return null;
    }

    /// <summary>"Sundays 01:00–05:00", "Every day 02:00–03:30", "Weekdays 23:00–02:00 (next day)".</summary>
    public string Describe()
    {
        var times = Start == End
            ? $"all day from {Start:HH\\:mm}"
            : $"{Start:HH\\:mm}–{End:HH\\:mm}{(Wraps ? " (next day)" : "")}";
        return $"{DaysText(Days)} {times}";
    }

    /// <summary>The days in words, the way somebody would say them.</summary>
    public static string DaysText(IReadOnlyCollection<DayOfWeek> days)
    {
        var set = days.ToHashSet();
        if (set.Count == 7)
            return "Every day";
        if (set.Count == 0)
            return "Never";
        if (set.SetEquals(Weekdays))
            return "Weekdays";
        if (set.SetEquals([DayOfWeek.Saturday, DayOfWeek.Sunday]))
            return "Weekends";
        if (set.Count == 1)
            return set.Single() + "s";

        // Monday first, as a week is read, not Sunday first as the enum has it.
        return string.Join(", ", Week.Where(set.Contains).Select(d => d.ToString()[..3]));
    }

    /// <summary>Monday to Sunday, the order the editor and <see cref="DaysText"/> list them in.</summary>
    public static IReadOnlyList<DayOfWeek> Week { get; } =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    ];

    public static IReadOnlyList<DayOfWeek> Weekdays { get; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    /// <summary>How the days are stored: "mon,tue". Short and readable in a backup.</summary>
    public static string StoredDays(IEnumerable<DayOfWeek> days) =>
        string.Join(',', Week.Where(days.Contains).Select(d => d.ToString()[..3].ToLowerInvariant()));

    /// <summary>Stored days, read back. Anything unrecognised is dropped rather than guessed at.</summary>
    public static IReadOnlyList<DayOfWeek> ParseDays(string? stored) =>
    [
        .. (stored ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => Week.FirstOrDefault(d => d.ToString().StartsWith(part, StringComparison.OrdinalIgnoreCase), (DayOfWeek)(-1)))
            .Where(d => (int)d >= 0)
            .Distinct(),
    ];

    public static string StoredScope(MuteScope scope) => scope.ToString().ToLowerInvariant();

    /// <summary>A stored scope, read back. Anything unrecognised is "everything" — the one that cannot silently mute nothing.</summary>
    public static MuteScope ParseScope(string? stored) =>
        Enum.TryParse<MuteScope>(stored?.Trim(), ignoreCase: true, out var scope) && Enum.IsDefined(scope)
            ? scope
            : MuteScope.Everything;

    public static string StoredTime(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static TimeOnly ParseTime(string? stored) =>
        TimeOnly.TryParse(stored, CultureInfo.InvariantCulture, out var parsed) ? parsed : default;
}
