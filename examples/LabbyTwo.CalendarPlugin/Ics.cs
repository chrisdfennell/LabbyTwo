using System.Globalization;

namespace LabbyTwo.CalendarPlugin;

/// <summary>
/// Enough of RFC 5545 to read a published calendar feed: events, times with or without a
/// zone, all-day events, and the recurrence rules people's calendars actually contain.
///
/// Deliberately not a general iCalendar implementation. It is a pure function from text to
/// a list of occurrences, which means the awkward parts — a Tuesday-and-Thursday rule that
/// ends in March, a timezone the host has never heard of — can be checked with a string
/// and no network.
/// </summary>
public static class Ics
{
    public sealed record Event(string Summary, string Location, DateTimeOffset Start, DateTimeOffset End, bool AllDay)
    {
        public bool IsNow(DateTimeOffset now) => Start <= now && End > now;
    }

    /// <summary>Every occurrence between <paramref name="from"/> and <paramref name="to"/>, in order.</summary>
    public static IReadOnlyList<Event> Read(string text, DateTimeOffset from, DateTimeOffset to)
    {
        var events = new List<Event>();

        foreach (var block in Blocks(Unfold(text), "VEVENT"))
        {
            var start = ParseDate(block, "DTSTART");
            if (start is null)
                continue;

            var end = ParseDate(block, "DTEND");
            var allDay = IsAllDay(block, "DTSTART");

            // No end time is legal. An all-day event runs to the next midnight; anything
            // else gets an hour, which is what a calendar client shows too.
            var length = end is null
                ? (allDay ? TimeSpan.FromDays(1) : TimeSpan.FromHours(1))
                : end.Value - start.Value;

            var summary = Unescape(Value(block, "SUMMARY"));
            var location = Unescape(Value(block, "LOCATION"));
            var rule = Value(block, "RRULE");
            var excluded = ExcludedDates(block);

            foreach (var occurrence in Occurrences(start.Value, rule, from, to))
            {
                if (excluded.Contains(occurrence))
                    continue;
                if (occurrence + length <= from || occurrence >= to)
                    continue;

                events.Add(new Event(
                    summary.Length > 0 ? summary : "(untitled)",
                    location, occurrence, occurrence + length, allDay));
            }
        }

        return [.. events.OrderBy(e => e.Start)];
    }

    // ---- recurrence -------------------------------------------------------------------

    private static readonly string[] WeekdayCodes = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];

    /// <summary>
    /// Every start time this event has in the window. Without an RRULE that is one date;
    /// with one it is a bounded expansion — bounded twice over, by the window and by a
    /// hard iteration cap, because a malformed rule should not hang a probe.
    /// </summary>
    private static IEnumerable<DateTimeOffset> Occurrences(
        DateTimeOffset start, string rule, DateTimeOffset from, DateTimeOffset to)
    {
        if (rule.Length == 0)
        {
            yield return start;
            yield break;
        }

        var parts = rule.Split(';')
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim().ToUpperInvariant(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);

        var frequency = parts.GetValueOrDefault("FREQ", "").ToUpperInvariant();
        var interval = int.TryParse(parts.GetValueOrDefault("INTERVAL"), out var every) && every > 0 ? every : 1;
        var count = int.TryParse(parts.GetValueOrDefault("COUNT"), out var limit) && limit > 0 ? limit : int.MaxValue;
        var until = parts.TryGetValue("UNTIL", out var untilText) ? ParseStamp(untilText, null) : null;

        // BYDAY on a weekly rule is the "Tuesdays and Thursdays" case, which is common
        // enough that ignoring it would drop half of a typical calendar.
        var byDay = parts.GetValueOrDefault("BYDAY", "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Array.IndexOf(WeekdayCodes, d.Trim()[^2..].ToUpperInvariant()))
            .Where(index => index >= 0)
            .ToArray();

        var emitted = 0;
        var cursor = start;

        for (var iteration = 0; iteration < 2000; iteration++)
        {
            foreach (var moment in frequency == "WEEKLY" && byDay.Length > 0
                         ? WeekOf(cursor, byDay)
                         : [cursor])
            {
                if (moment < start)
                    continue;
                if (until is not null && moment > until)
                    yield break;
                if (emitted >= count)
                    yield break;

                emitted++;
                if (moment >= to)
                    yield break;
                if (moment + TimeSpan.FromDays(1) >= from)
                    yield return moment;
            }

            cursor = frequency switch
            {
                "DAILY" => cursor.AddDays(interval),
                "WEEKLY" => cursor.AddDays(7 * interval),
                "MONTHLY" => cursor.AddMonths(interval),
                "YEARLY" => cursor.AddYears(interval),
                // An unsupported frequency (HOURLY, MINUTELY, SECONDLY) expands to a
                // single occurrence rather than silently to nothing.
                _ => DateTimeOffset.MaxValue,
            };

            if (cursor == DateTimeOffset.MaxValue || cursor >= to)
                yield break;
        }
    }

    private static IEnumerable<DateTimeOffset> WeekOf(DateTimeOffset cursor, int[] byDay)
    {
        var sunday = cursor.AddDays(-(int)cursor.DayOfWeek);
        foreach (var day in byDay.Order())
            yield return sunday.AddDays(day);
    }

    private static HashSet<DateTimeOffset> ExcludedDates(IReadOnlyList<string> block) =>
    [
        .. block
            .Where(line => line.StartsWith("EXDATE", StringComparison.OrdinalIgnoreCase))
            .SelectMany(line => Split(line).Value.Split(','))
            .Select(value => ParseStamp(value, null))
            .Where(date => date is not null)
            .Select(date => date!.Value)
    ];

    // ---- parsing ----------------------------------------------------------------------

    /// <summary>
    /// Undoes RFC 5545 line folding. A long summary is split across lines with a space or
    /// tab at the start of each continuation, so anything that reads line by line without
    /// this gets half a title and a stray fragment.
    /// </summary>
    private static List<string> Unfold(string text)
    {
        var lines = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t') && lines.Count > 0)
                lines[^1] += raw[1..];
            else
                lines.Add(raw);
        }
        return lines;
    }

    private static IEnumerable<List<string>> Blocks(List<string> lines, string name)
    {
        List<string>? current = null;
        foreach (var line in lines)
        {
            if (line.Equals($"BEGIN:{name}", StringComparison.OrdinalIgnoreCase))
                current = [];
            else if (line.Equals($"END:{name}", StringComparison.OrdinalIgnoreCase))
            {
                if (current is not null)
                    yield return current;
                current = null;
            }
            else
                current?.Add(line);
        }
    }

    private static (string Name, string Parameters, string Value) Split(string line)
    {
        var colon = line.IndexOf(':');
        if (colon < 0)
            return (line, "", "");

        var head = line[..colon];
        var semicolon = head.IndexOf(';');
        return semicolon < 0
            ? (head, "", line[(colon + 1)..])
            : (head[..semicolon], head[(semicolon + 1)..], line[(colon + 1)..]);
    }

    private static string Value(IReadOnlyList<string> block, string name) =>
        block.Where(line => line.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            .Select(Split)
            .Where(parsed => parsed.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(parsed => parsed.Value)
            .FirstOrDefault() ?? "";

    private static bool IsAllDay(IReadOnlyList<string> block, string name) =>
        block.Where(line => line.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            .Select(Split)
            .Any(parsed => parsed.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                           && parsed.Parameters.Contains("VALUE=DATE", StringComparison.OrdinalIgnoreCase));

    private static DateTimeOffset? ParseDate(IReadOnlyList<string> block, string name)
    {
        foreach (var line in block)
        {
            var parsed = Split(line);
            if (!parsed.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;

            var zone = parsed.Parameters
                .Split(';')
                .FirstOrDefault(p => p.StartsWith("TZID=", StringComparison.OrdinalIgnoreCase))?[5..];

            return ParseStamp(parsed.Value, zone);
        }

        return null;
    }

    private static DateTimeOffset? ParseStamp(string value, string? zoneId)
    {
        value = value.Trim();

        // A date with no time: an all-day event, which belongs at local midnight rather
        // than UTC midnight or it lands on the wrong day west of Greenwich.
        if (DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
            return new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date));

        if (value.EndsWith('Z') &&
            DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc))
            return new DateTimeOffset(utc, TimeSpan.Zero).ToLocalTime();

        if (!DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local))
            return null;

        if (zoneId is { Length: > 0 })
        {
            try
            {
                // .NET resolves IANA names ("America/Denver") on Windows too, but a feed
                // can carry a zone this machine has never heard of — that is a reason to
                // fall back to local time, not to drop the event.
                var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
                return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToLocalTime();
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
            }
        }

        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    /// <summary>Text values escape commas, semicolons and newlines. Titles contain all three.</summary>
    private static string Unescape(string value) => value
        .Replace("\\n", "\n", StringComparison.OrdinalIgnoreCase)
        .Replace("\\,", ",")
        .Replace("\\;", ";")
        .Replace("\\\\", "\\");
}
