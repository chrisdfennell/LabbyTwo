using System.Globalization;

namespace LabbyTwo.Core;

/// <summary>
/// When the weekly summary goes out: a weekday and a local time, off until somebody turns
/// it on.
///
/// A calendar time rather than an interval, because "every 168 hours from whenever the
/// container last started" drifts with every restart and every clock change, and a digest
/// that arrived on Monday morning for a month and then on Wednesday night would be read as
/// broken. Everything here is pure — the zone and the clock are passed in — so the
/// awkward cases (a time the clocks skip, a restart straddling the send, a week the app was
/// down for) are pinned by tests rather than met on somebody's server.
/// </summary>
/// <param name="Channels">Alert channel connection ids to send to. Empty means every one.</param>
public sealed record WeeklySchedule(bool Enabled, DayOfWeek Day, TimeOnly Time, IReadOnlyList<string> Channels)
{
    public const string EnabledKey = "weekly_summary_enabled";
    public const string DayKey = "weekly_summary_day";
    public const string TimeKey = "weekly_summary_time";
    public const string ChannelsKey = "weekly_summary_channels";

    /// <summary>
    /// The instant up to which every scheduled send has been dealt with. Set to "now" when
    /// the summary is switched on — so turning it on on a Wednesday does not immediately
    /// send the Monday one it "missed" — and again at each send. A single instant rather
    /// than a "last sent" date is what makes the restart cases simple: an occurrence is due
    /// exactly when it is later than this, however many times the process starts.
    /// </summary>
    public const string CoveredKey = "weekly_summary_covered_until";

    /// <summary>When a scheduled summary was last actually sent, for the Settings page. Not used to decide anything.</summary>
    public const string LastSentKey = "weekly_summary_last_sent";

    /// <summary>Monday morning: the start of the week, when a list of things to deal with is most useful.</summary>
    public static WeeklySchedule Default { get; } = new(false, DayOfWeek.Monday, new TimeOnly(9, 0), []);

    public static WeeklySchedule From(SettingsBag settings) => new(
        settings.GetBool(EnabledKey, false),
        Enum.TryParse<DayOfWeek>(settings.Get(DayKey), ignoreCase: true, out var day) && Enum.IsDefined(day)
            ? day
            : Default.Day,
        TimeOnly.TryParse(settings.Get(TimeKey), CultureInfo.InvariantCulture, out var time) ? time : Default.Time,
        [.. settings.Get(ChannelsKey).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]);

    public Dictionary<string, string> ToSettings() => new()
    {
        [EnabledKey] = Enabled ? "true" : "false",
        [DayKey] = Day.ToString(),
        [TimeKey] = Time.ToString("HH:mm", CultureInfo.InvariantCulture),
        [ChannelsKey] = string.Join(',', Channels),
    };

    /// <summary>
    /// A local date and time as an instant, in a zone that may change its clocks.
    ///
    /// A time the clocks skip — 02:30 on the morning they spring forward — is taken with
    /// the offset from before the jump, which lands it just after: 03:30 in the new time,
    /// the same distance past the change as it was meant to be past two. A time that
    /// happens twice, when they fall back, is the first of the two, so the summary is never
    /// an hour late and never sent twice for the one wall-clock time.
    /// </summary>
    public static DateTimeOffset At(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time);
        if (zone.IsInvalidTime(local))
            return new DateTimeOffset(local, zone.GetUtcOffset(local.AddHours(-3)));

        var offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max()
            : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    /// <summary>The most recent scheduled moment at or before <paramref name="now"/>.</summary>
    public DateTimeOffset LatestAtOrBefore(DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        for (var back = 0; back <= 8; back++)
        {
            var date = today.AddDays(-back);
            if (date.DayOfWeek == Day && At(date, Time, zone) is var at && at <= now)
                return at;
        }
        // Unreachable: some day in the last eight is the right weekday and already past.
        return At(today.AddDays(-7), Time, zone);
    }

    /// <summary>The first scheduled moment strictly after <paramref name="now"/>.</summary>
    public DateTimeOffset NextAfter(DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        for (var ahead = 0; ahead <= 8; ahead++)
        {
            var date = today.AddDays(ahead);
            if (date.DayOfWeek == Day && At(date, Time, zone) is var at && at > now)
                return at;
        }
        return At(today.AddDays(7), Time, zone);
    }

    /// <summary>
    /// <paramref name="at"/>, or the end of the quiet hours it falls in. A summary is the
    /// least urgent message LabbyTwo sends, so it waits out quiet hours whatever their mode
    /// — "down only" lets outages through, and this is not one.
    /// </summary>
    public static DateTimeOffset AfterQuietHours(AlertPolicy policy, DateTimeOffset at, TimeZoneInfo zone)
    {
        if (!policy.IsQuiet(at, zone))
            return at;

        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
        var end = At(date, policy.QuietTo, zone);
        return end > at ? end : At(date.AddDays(1), policy.QuietTo, zone);
    }

    /// <summary>What to do now, and when the next send will happen if not now.</summary>
    /// <param name="NextAt">When it will go out — after quiet hours, if the scheduled time is inside them. Null when off.</param>
    public sealed record Decision(bool SendNow, DateTimeOffset? NextAt, string Reason);

    /// <summary>
    /// Whether a summary is due. Only the most recent scheduled moment is ever considered,
    /// which is the whole of the catch-up rule: an app that was down on Monday morning sends
    /// when it comes back, once, and an app that was down for three Mondays sends once too
    /// — three digests of the same week would be worse than none.
    /// </summary>
    /// <param name="coveredUntil">See <see cref="CoveredKey"/>. Null means not armed yet, which is never due.</param>
    public Decision Decide(DateTimeOffset? coveredUntil, AlertPolicy policy, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (!Enabled)
            return new Decision(false, null, "Off");

        if (coveredUntil is { } covered && LatestAtOrBefore(now, zone) > covered)
        {
            return policy.IsQuiet(now, zone)
                ? new Decision(false, AfterQuietHours(policy, now, zone), "Due, held until quiet hours end")
                : new Decision(true, now, "Due");
        }

        return new Decision(false, AfterQuietHours(policy, NextAfter(now, zone), zone), "Waiting");
    }

    /// <summary>A stored instant, or null for anything missing or unreadable.</summary>
    public static DateTimeOffset? ParseInstant(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    public static string FormatInstant(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
