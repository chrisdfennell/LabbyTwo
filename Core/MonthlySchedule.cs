using System.Globalization;

namespace LabbyTwo.Core;

/// <summary>
/// When the monthly report is made: a day of the month and a local time, off until somebody
/// turns it on. Each report is of the month before the one it is made in.
///
/// The same shape as <see cref="WeeklySchedule"/>, for the same reasons — a calendar time
/// rather than an interval, so it does not drift with restarts; one "covered until" instant
/// rather than a "last sent" date, so the restart cases are simple; everything pure, with
/// the zone and the clock passed in, so a month that starts on the night the clocks change
/// is pinned by a test. What differs is only the calendar: a day past the end of a short
/// month (the 31st, in February) is that month's last day, so "the 31st" means "the end of
/// every month" rather than "skip the short ones".
/// </summary>
/// <param name="Day">1–31; clamped to the length of each month.</param>
/// <param name="Send">Whether a short summary also goes out through the alert channels.</param>
/// <param name="Channels">Alert channel connection ids to send to. Empty means every one.</param>
public sealed record MonthlySchedule(bool Enabled, int Day, TimeOnly Time, bool Send, IReadOnlyList<string> Channels)
{
    public const string EnabledKey = "monthly_report_enabled";
    public const string DayKey = "monthly_report_day";
    public const string TimeKey = "monthly_report_time";
    public const string SendKey = "monthly_report_send";
    public const string ChannelsKey = "monthly_report_channels";

    /// <summary>See <see cref="WeeklySchedule.CoveredKey"/>: every scheduled report up to this instant has been dealt with.</summary>
    public const string CoveredKey = "monthly_report_covered_until";

    /// <summary>When a scheduled report was last made, for the Settings page. Not used to decide anything.</summary>
    public const string LastMadeKey = "monthly_report_last_made";

    /// <summary>The morning of the 1st: last month is over, and the new one has not got busy yet.</summary>
    public static MonthlySchedule Default { get; } = new(false, 1, new TimeOnly(8, 0), false, []);

    public static MonthlySchedule From(SettingsBag settings) => new(
        settings.GetBool(EnabledKey, false),
        int.TryParse(settings.Get(DayKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out var day) ? Math.Clamp(day, 1, 31) : Default.Day,
        TimeOnly.TryParse(settings.Get(TimeKey), CultureInfo.InvariantCulture, out var time) ? time : Default.Time,
        settings.GetBool(SendKey, false),
        [.. settings.Get(ChannelsKey).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]);

    public Dictionary<string, string> ToSettings() => new()
    {
        [EnabledKey] = Enabled ? "true" : "false",
        [DayKey] = Math.Clamp(Day, 1, 31).ToString(CultureInfo.InvariantCulture),
        [TimeKey] = Time.ToString("HH:mm", CultureInfo.InvariantCulture),
        [SendKey] = Send ? "true" : "false",
        [ChannelsKey] = string.Join(',', Channels),
    };

    /// <summary>The scheduled moment in the month that starts on <paramref name="month"/>.</summary>
    public DateTimeOffset In(DateOnly month, TimeZoneInfo zone)
    {
        var first = new DateOnly(month.Year, month.Month, 1);
        var day = Math.Min(Math.Clamp(Day, 1, 31), DateTime.DaysInMonth(first.Year, first.Month));
        return WeeklySchedule.At(first.AddDays(day - 1), Time, zone);
    }

    /// <summary>The most recent scheduled moment at or before <paramref name="now"/>.</summary>
    public DateTimeOffset LatestAtOrBefore(DateTimeOffset now, TimeZoneInfo zone)
    {
        var month = FirstOfMonth(now, zone);
        var at = In(month, zone);
        return at <= now ? at : In(month.AddMonths(-1), zone);
    }

    /// <summary>The first scheduled moment strictly after <paramref name="now"/>.</summary>
    public DateTimeOffset NextAfter(DateTimeOffset now, TimeZoneInfo zone)
    {
        var month = FirstOfMonth(now, zone);
        var at = In(month, zone);
        return at > now ? at : In(month.AddMonths(1), zone);
    }

    /// <summary>What to do now, and when the next report will be made if not now.</summary>
    /// <param name="NextAt">When it will be made — after quiet hours when it is also sent and the time falls inside them. Null when off.</param>
    public sealed record Decision(bool MakeNow, DateTimeOffset? NextAt, string Reason);

    /// <summary>
    /// Whether a report is due. Only the most recent scheduled moment counts, so an app that
    /// was down on the 1st makes last month's report when it comes back, once; one that was
    /// down for three months makes one, of the month just gone.
    ///
    /// Quiet hours matter only when the report is also sent: a note appearing on a tab at
    /// 3am wakes nobody, and holding it back would only make it later for no one's benefit.
    /// </summary>
    /// <param name="coveredUntil">See <see cref="CoveredKey"/>. Null means not armed yet, which is never due.</param>
    public Decision Decide(DateTimeOffset? coveredUntil, AlertPolicy policy, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (!Enabled)
            return new Decision(false, null, "Off");

        if (coveredUntil is { } covered && LatestAtOrBefore(now, zone) > covered)
        {
            return Send && policy.IsQuiet(now, zone)
                ? new Decision(false, WeeklySchedule.AfterQuietHours(policy, now, zone), "Due, held until quiet hours end")
                : new Decision(true, now, "Due");
        }

        var next = NextAfter(now, zone);
        return new Decision(false, Send ? WeeklySchedule.AfterQuietHours(policy, next, zone) : next, "Waiting");
    }

    /// <summary>
    /// The month a report made at <paramref name="at"/> is about: the one before the month
    /// <paramref name="at"/> falls in, in the lab's zone. A report made on the 1st of October
    /// is September's, and so is one made late on the 15th.
    /// </summary>
    public static DateOnly ReportedMonth(DateTimeOffset at, TimeZoneInfo zone) => FirstOfMonth(at, zone).AddMonths(-1);

    /// <summary>
    /// A month as instants: local midnight on its first day to local midnight on the next
    /// month's, each with the offset that day actually had — so October in a zone that falls
    /// back is 31 days and an hour long, as it really is.
    /// </summary>
    public static (DateTimeOffset From, DateTimeOffset To) Span(DateOnly month, TimeZoneInfo zone)
    {
        var first = new DateOnly(month.Year, month.Month, 1);
        return (WeeklySchedule.At(first, TimeOnly.MinValue, zone), WeeklySchedule.At(first.AddMonths(1), TimeOnly.MinValue, zone));
    }

    /// <summary>"September 2026".</summary>
    public static string Name(DateOnly month) => month.ToString("MMMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>The first day of the month <paramref name="at"/> falls in, in <paramref name="zone"/>.</summary>
    public static DateOnly FirstOfMonth(DateTimeOffset at, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        return new DateOnly(local.Year, local.Month, 1);
    }
}
