using System.Globalization;

namespace LabbyTwo.Core;

/// <summary>
/// The choices for when a theme shows its light end and when its dark one.
///
/// Stored in the same "theme" setting the old three-way switch used, so "system", "dark" and
/// "light" keep meaning exactly what they always did and an existing install changes nothing.
/// "sun" and "schedule" are the two that change by themselves.
/// </summary>
public static class ThemeModes
{
    /// <summary>Follow each device's own light/dark setting. The stored word predates this class.</summary>
    public const string Auto = "system";
    public const string Dark = "dark";
    public const string Light = "light";

    /// <summary>Light from sunrise to sunset at the home location, dark otherwise.</summary>
    public const string Sun = "sun";

    /// <summary>Light between two clock times, dark otherwise.</summary>
    public const string Schedule = "schedule";

    public static readonly (string Value, string Label, string Hint)[] All =
    [
        (Auto, "Follow the device", "Light or dark, whichever each device is set to."),
        (Dark, "Dark", "Always dark."),
        (Light, "Light", "Always light."),
        (Sun, "Follow the sun", "Light from sunrise to sunset where you live, dark the rest of the time."),
        (Schedule, "On a schedule", "Light between two times of day, dark the rest of the time."),
    ];

    public static bool IsKnown(string? value) => All.Any(m => m.Value == value);

    public static string Label(string value) => All.FirstOrDefault(m => m.Value == value).Label ?? value;
}

/// <summary>
/// The times the two automatic modes work from: how far either side of sunrise and sunset to
/// switch, and the light window for the schedule.
///
/// One set for the whole install rather than one per screen. A wall that follows the sun and
/// a phone that follows the sun want the same sun; what differs between screens is which of
/// the modes they use, and that is per screen (see ThemeService).
/// </summary>
/// <param name="SunriseOffsetMinutes">Added to sunrise: 30 turns light half an hour after the sun is up, -30 half an hour before.</param>
/// <param name="SunsetOffsetMinutes">Added to sunset: -30 goes dark half an hour before the sun does.</param>
public sealed record ThemeTimes(int SunriseOffsetMinutes, int SunsetOffsetMinutes, TimeOnly LightFrom, TimeOnly LightTo)
{
    public const string SunriseOffsetKey = "theme_sunrise_offset";
    public const string SunsetOffsetKey = "theme_sunset_offset";
    public const string LightFromKey = "theme_light_from";
    public const string LightToKey = "theme_light_to";

    /// <summary>
    /// Three hours either way. Further than that is no longer "a little before sunset", and
    /// near the polar circles it would let the offsets swallow the whole day.
    /// </summary>
    public const int MaxOffsetMinutes = 180;

    public static ThemeTimes Default { get; } = new(0, 0, new TimeOnly(7, 0), new TimeOnly(19, 0));

    public static ThemeTimes From(SettingsBag settings) => new(
        ClampOffset(settings.GetInt(SunriseOffsetKey, 0)),
        ClampOffset(settings.GetInt(SunsetOffsetKey, 0)),
        Time(settings.Get(LightFromKey), Default.LightFrom),
        Time(settings.Get(LightToKey), Default.LightTo));

    public static int ClampOffset(int minutes) => Math.Clamp(minutes, -MaxOffsetMinutes, MaxOffsetMinutes);

    /// <summary>"HH:mm", read invariantly; anything else is the default rather than an error.</summary>
    public static TimeOnly Time(string value, TimeOnly fallback) =>
        TimeOnly.TryParseExact(value, ["HH:mm", "H:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : fallback;

    public static string Format(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>
/// What a mode comes to at one moment: the data-theme attribute to stamp now, and when that
/// next changes.
/// </summary>
/// <param name="Attribute">"light", "dark", or null to leave it to the device (prefers-color-scheme).</param>
/// <param name="NextSwitch">When <paramref name="Attribute"/> next changes; null if it never does by itself.</param>
/// <param name="NextAttribute">What it changes to then.</param>
/// <param name="NextReason">"sunrise", "sunset" or "schedule" — said beside the time so a switch at 19:42 is explained.</param>
/// <param name="Problem">Set when the mode cannot do what it says and has fallen back — sun with nowhere set.</param>
public sealed record ModeDecision(
    string? Attribute,
    DateTimeOffset? NextSwitch = null,
    string? NextAttribute = null,
    string? NextReason = null,
    string? Problem = null)
{
    public static ModeDecision Device { get; } = new(Attribute: null);
}

/// <summary>
/// Decides light or dark for the automatic modes. Pure: the location, the zone and the clock
/// are all passed in, so the awkward cases — a day the clocks change, a summer where the sun
/// never sets, a schedule that runs past midnight — are pinned by tests rather than found on
/// somebody's wall at two in the morning.
///
/// <para>Both automatic modes are the same shape: a set of light intervals on a timeline,
/// dark everywhere else. The sun gives one interval a day from (offset) sunrise to (offset)
/// sunset, or a whole day for a day the sun never sets, or nothing for one it never rises;
/// the schedule gives one a day between its two clock times. Overlapping intervals are
/// merged, and then "now" is either inside one (light) or not (dark), and the next switch is
/// simply the nearest interval edge ahead. Working from merged intervals rather than from
/// "today's sunrise and sunset" is what makes the edges of the year come out right: a sunset
/// after local midnight, the first day after a polar summer, and offsets that eat a short
/// winter day entirely all fall out of the same arithmetic.</para>
/// </summary>
public static class ThemeSchedule
{
    /// <summary>
    /// How far ahead to look for a switch. A year is enough for anywhere people live: even at
    /// the poles the sun rises and sets once a year.
    /// </summary>
    private const int HorizonDays = 370;

    public const string NoLocation =
        "Following the sun needs to know where you are. Set it under Settings → Where you are; until then this follows the device.";

    private readonly record struct Span(DateTimeOffset Start, DateTimeOffset End, string StartReason, string EndReason);

    public static ModeDecision Decide(string mode, ThemeTimes times, HomeLocation home, TimeZoneInfo zone, DateTimeOffset now)
    {
        switch (mode)
        {
            case ThemeModes.Dark:
            case ThemeModes.Light:
                return new ModeDecision(mode);

            case ThemeModes.Sun when home is { Latitude: { } lat, Longitude: { } lon }:
                return FromSpans(now, (from, to) => SunSpans(times, lat, lon, zone, from, to), zone);

            case ThemeModes.Sun:
                return ModeDecision.Device with { Problem = NoLocation };

            case ThemeModes.Schedule:
                return FromSpans(now, (from, to) => ScheduleSpans(times, zone, from, to), zone);

            default:
                return ModeDecision.Device;
        }
    }

    /// <summary>
    /// Looks a few days either side first, which settles almost every case; only a polar
    /// summer or winter, with no edge in sight, needs the long look.
    /// </summary>
    private static ModeDecision FromSpans(DateTimeOffset now, Func<DateOnly, DateOnly, List<Span>> spans, TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);

        // A span cut off by the end of the short look would end at a midnight that is only
        // the edge of the window, not a sunset; anything that close to the end is checked
        // again with the long one.
        var near = Pick(Merge(spans(today.AddDays(-2), today.AddDays(3))), now);
        return near.NextSwitch is { } next && next < Midnight(today.AddDays(3), zone)
            ? near
            : Pick(Merge(spans(today.AddDays(-2), today.AddDays(HorizonDays))), now);
    }

    private static ModeDecision Pick(List<Span> merged, DateTimeOffset now)
    {
        foreach (var span in merged)
        {
            if (span.End <= now)
                continue;
            if (span.Start <= now)
                return new ModeDecision(ThemeModes.Light, span.End, ThemeModes.Dark, span.EndReason);

            // The first span still ahead: dark until it starts.
            return new ModeDecision(ThemeModes.Dark, span.Start, ThemeModes.Light, span.StartReason);
        }

        // Nothing light anywhere ahead — inside a polar night with more than a year to go,
        // which only a bad latitude can produce. Dark, and nothing to wait for.
        return new ModeDecision(ThemeModes.Dark);
    }

    private static List<Span> SunSpans(ThemeTimes times, double latitude, double longitude, TimeZoneInfo zone, DateOnly from, DateOnly to)
    {
        var spans = new List<Span>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var noon = date.ToDateTime(new TimeOnly(12, 0));
            var day = SunTimes.For(date, latitude, longitude, zone.GetUtcOffset(noon));

            if (day.PolarDay)
            {
                // Light the whole local day. Neighbouring polar days merge into one span.
                spans.Add(new Span(Midnight(date, zone), Midnight(date.AddDays(1), zone), "sunrise", "sunset"));
            }
            else if (day is { Sunrise: { } rise, Sunset: { } set })
            {
                var start = rise.AddMinutes(times.SunriseOffsetMinutes);
                var end = set.AddMinutes(times.SunsetOffsetMinutes);
                // Offsets can close a short winter day entirely; then it stays dark.
                if (end > start)
                    spans.Add(new Span(start, end, "sunrise", "sunset"));
            }
            // A polar night adds nothing: dark all day.
        }
        return spans;
    }

    private static List<Span> ScheduleSpans(ThemeTimes times, TimeZoneInfo zone, DateOnly from, DateOnly to)
    {
        var spans = new List<Span>();

        // The same time twice is no window at all; the setting page refuses it, and a value
        // that got in another way is read as "always dark" rather than guessed at.
        if (times.LightFrom == times.LightTo)
            return spans;

        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var start = WeeklySchedule.At(date, times.LightFrom, zone);
            // A window that runs past midnight (light 22:00 to 06:00, for a night shift) ends
            // on the next day.
            var endDate = times.LightTo > times.LightFrom ? date : date.AddDays(1);
            var end = WeeklySchedule.At(endDate, times.LightTo, zone);
            if (end > start)
                spans.Add(new Span(start, end, "schedule", "schedule"));
        }
        return spans;
    }

    /// <summary>Sorted, with touching or overlapping spans joined, so every edge left is a real switch.</summary>
    private static List<Span> Merge(List<Span> spans)
    {
        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<Span>();
        foreach (var span in spans)
        {
            if (merged.Count > 0 && span.Start <= merged[^1].End)
            {
                var last = merged[^1];
                if (span.End > last.End)
                    merged[^1] = last with { End = span.End, EndReason = span.EndReason };
                continue;
            }
            merged.Add(span);
        }
        return merged;
    }

    private static DateTimeOffset Midnight(DateOnly date, TimeZoneInfo zone) => WeeklySchedule.At(date, TimeOnly.MinValue, zone);

    /// <summary>
    /// The decision in a sentence for the settings page: "Light now — switches to dark at
    /// 19:42 (sunset)." A switch beyond tomorrow, which only happens near the poles, gets
    /// its date.
    /// </summary>
    public static string Describe(ModeDecision decision, TimeZoneInfo zone, DateTimeOffset now)
    {
        if (decision.Problem is { } problem)
            return problem;

        var current = decision.Attribute switch
        {
            ThemeModes.Light => "Light now",
            ThemeModes.Dark => "Dark now",
            _ => "Light or dark as each device is set",
        };

        if (decision.NextSwitch is not { } next)
            return current + ".";

        var local = TimeZoneInfo.ConvertTime(next, zone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var day = DateOnly.FromDateTime(local.DateTime);
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        var when = day == today
            ? $"at {time}"
            : day == today.AddDays(1)
                ? $"tomorrow at {time}"
                : $"on {local.ToString("d MMM", CultureInfo.InvariantCulture)} at {time}";

        var reason = decision.NextReason is "sunrise" or "sunset" ? $" ({decision.NextReason})" : "";
        return $"{current} — switches to {decision.NextAttribute} {when}{reason}.";
    }
}
