using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LabbyTwo.Core;

/// <summary>One line on a <c>{{chart}}</c>: a connection and one of its metrics, as written.</summary>
/// <param name="Connection">The connection's name or id, as written — looked up later.</param>
/// <param name="Metric">A metric key or label, as written. Empty means the response time, as a sparkline's does.</param>
public sealed record ChartLine(string Connection, string Metric);

/// <summary>
/// What a <c>{{chart: …}}</c> asks for, read from the text and nothing else: which lines,
/// over what span, and how to draw them. Looking the names up is the component's job; this
/// only says whether what was written makes sense, so every awkward thing somebody can type
/// is pinned by a test rather than met in a runbook.
/// </summary>
/// <param name="Window">How far back from now, for a chart that moves with the clock. Ignored when <see cref="From"/> is set.</param>
/// <param name="From">The first day of a fixed span, in the lab's zone — <c>from=2026-09-01</c>. Null for a moving window.</param>
/// <param name="To">The day after the last one of a fixed span (<c>to=</c> is exclusive, like a month's end); null for "until now".</param>
/// <param name="Height">Pixels, 80–600.</param>
/// <param name="Min">The bottom of the scale, in the units the chart is read in; null to fit the data.</param>
/// <param name="Max">The top of the scale; null to fit the data.</param>
/// <param name="Title">The words above the chart; null for the automatic ones.</param>
/// <param name="Unit">What <c>unit=</c> said, exactly as <c>{{metric}}</c> reads it: a unit to convert to, or a label.</param>
public sealed record ChartSpec(
    IReadOnlyList<ChartLine> Lines,
    TimeSpan Window,
    DateOnly? From,
    DateOnly? To,
    int Height,
    double? Min,
    double? Max,
    string? Title,
    string? Unit)
{
    /// <summary>A span of fixed dates rather than "the last so long".</summary>
    public bool IsFixed => From is not null;

    /// <summary>
    /// The span as instants in <paramref name="zone"/>: midnight at the start of
    /// <see cref="From"/> to midnight at the start of <see cref="To"/> (or now), each with the
    /// offset that day actually had — a month that crosses a clock change is not an hour short.
    /// For a moving window, the window back from <paramref name="now"/>.
    /// </summary>
    public (DateTimeOffset From, DateTimeOffset To) Span(DateTimeOffset now, TimeZoneInfo zone)
    {
        if (From is not { } from)
            return (now - Window, now);
        var start = WeeklySchedule.At(from, TimeOnly.MinValue, zone);
        var end = To is { } to ? WeeklySchedule.At(to, TimeOnly.MinValue, zone) : now;
        return (start, end < start ? start : end);
    }

    /// <summary>
    /// Whether anything newer can still arrive inside the span. A span that ended in the
    /// past is history that will never change, so it is read once and never again.
    /// </summary>
    public bool IsLive(DateTimeOffset now, TimeZoneInfo zone) => Span(now, zone).To >= now - TimeSpan.FromMinutes(1);
}

/// <summary>
/// Reads <c>{{chart: "NAS" / cpu_percent, "NAS" / mem_percent last=7d height=200}}</c>.
///
/// The general shortcode grammar has no word for "several targets", so the lines are
/// split at commas here — commas outside quotes, so a name with one in it is written
/// <c>"Home, office"</c> — and each piece is read by <see cref="Shortcodes.Parse"/> exactly
/// as a one-line shortcode would be. That keeps every rule about quotes, slashes and escapes
/// the one the rest of the app follows, rather than a second parser that nearly agrees.
/// Options may sit on any piece; they belong to the chart as a whole.
/// </summary>
public static partial class ChartShortcode
{
    public const int DefaultHeight = 200;
    public const int MinHeight = 80;
    public const int MaxHeight = 600;

    /// <summary>
    /// More lines than this on one chart stop being tellable apart — the compare card stops
    /// at a dozen, and that is with a legend beside each colour.
    /// </summary>
    public const int MaxLines = 8;

    public static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(24);
    public static readonly TimeSpan Shortest = TimeSpan.FromHours(1);

    /// <summary>
    /// A year: as far back as hourly summaries are kept by default. A window past what the
    /// install keeps simply draws what there is.
    /// </summary>
    public static readonly TimeSpan Longest = TimeSpan.FromDays(366);

    /// <summary>
    /// The most points drawn per line. A week of 30-second readings is twenty thousand, and
    /// a chart a few hundred pixels wide can show a few hundred of them; the rest is attribute
    /// weight for the page and JSON for the hover script to parse.
    /// </summary>
    public const int MaxPoints = 480;

    [GeneratedRegex(@"^(\d{1,4})\s*(m|min|h|d|w)$", RegexOptions.IgnoreCase)]
    private static partial Regex WindowWord();

    /// <summary>Whether <paramref name="text"/> is written like a window at all, in range or not.</summary>
    public static bool LooksLikeWindow(string text) => WindowWord().IsMatch(text.Trim());

    /// <summary>The window written, or null with why: not a window, or outside an hour to a year.</summary>
    public static TimeSpan? ParseWindow(string text, out string? problem)
    {
        problem = null;
        var match = WindowWord().Match(text.Trim());
        if (!match.Success)
        {
            problem = $"“{text}” is not a window. Write it like 1h, 24h, 7d or 30d.";
            return null;
        }
        var count = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var window = match.Groups[2].Value.ToLowerInvariant() switch
        {
            "m" or "min" => TimeSpan.FromMinutes(count),
            "h" => TimeSpan.FromHours(count),
            "d" => TimeSpan.FromDays(count),
            _ => TimeSpan.FromDays(count * 7),
        };
        if (window < Shortest || window > Longest)
        {
            problem = $"A chart covers between 1h and 365d; “{text}” is outside that.";
            return null;
        }
        return window;
    }

    /// <summary>
    /// What the shortcode asks for, or null with a sentence saying what is wrong. Only the
    /// text is checked here — whether "NAS" is a connection is for whoever draws it.
    /// </summary>
    public static ChartSpec? Read(Shortcode code, out string? problem)
    {
        problem = null;
        var lines = new List<ChartLine>();
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? trailingWindow = null;

        foreach (var piece in Pieces(Arguments(code.Source)))
        {
            if (Shortcodes.Parse("{{chart:" + piece + "}}") is not { } part)
            {
                problem = $"“{piece.Trim()}” is not a line this can read. Write it as Connection / metric.";
                return null;
            }
            foreach (var (key, value) in part.Options)
                options[key] = value;
            if (part.Target.Count == 0)
                continue;
            if (part.Target.Count > 2)
            {
                problem = $"“{piece.Trim()}” has more than a connection and a metric. A name with a slash in it goes in quotes.";
                return null;
            }

            // The sparkline's shorthand: a last word that looks like a window is the window.
            var (metric, windowWord) = SplitWindow(part.Part(1));
            if (windowWord.Length > 0)
                trailingWindow = windowWord;
            lines.Add(new ChartLine(part.Part(0), metric));
        }

        if (lines.Count == 0)
        {
            problem = "Say what to draw: {{chart: NAS / cpu_percent last=24h}}.";
            return null;
        }
        if (lines.Count > MaxLines)
        {
            problem = $"A chart draws at most {MaxLines} lines; this asks for {lines.Count}.";
            return null;
        }

        var windowText = Option(options, "last") ?? Option(options, "window") ?? trailingWindow;
        var window = DefaultWindow;
        if (windowText is not null)
        {
            if (ParseWindow(windowText, out problem) is not { } parsed)
                return null;
            window = parsed;
        }

        DateOnly? from = null, to = null;
        if (Option(options, "from") is { } fromText)
        {
            if (!DateOnly.TryParseExact(fromText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
            {
                problem = $"from=“{fromText}” is not a date. Write it year first: from=2026-09-01.";
                return null;
            }
            from = start;
        }
        if (Option(options, "to") is { } toText)
        {
            if (!DateOnly.TryParseExact(toText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
            {
                problem = $"to=“{toText}” is not a date. Write it year first: to=2026-10-01.";
                return null;
            }
            if (from is null)
            {
                problem = "to= needs a from= to go with it.";
                return null;
            }
            to = end;
        }
        if (from is { } first && to is { } last)
        {
            if (last <= first)
            {
                problem = $"to= is the day after the last one drawn, so it has to come after from= ({first:yyyy-MM-dd}).";
                return null;
            }
            if (last.DayNumber - first.DayNumber > Longest.TotalDays)
            {
                problem = "A chart covers at most 365 days.";
                return null;
            }
        }

        var height = DefaultHeight;
        if (Option(options, "height") is { } heightText)
        {
            var pixels = heightText.EndsWith("px", StringComparison.OrdinalIgnoreCase) ? heightText[..^2] : heightText;
            if (!int.TryParse(pixels, NumberStyles.Integer, CultureInfo.InvariantCulture, out height)
                || height < MinHeight || height > MaxHeight)
            {
                problem = $"height=“{heightText}” should be a number of pixels from {MinHeight} to {MaxHeight}.";
                return null;
            }
        }

        if (!Number(options, "min", out var min, out problem) || !Number(options, "max", out var max, out problem))
            return null;
        if (min is { } low && max is { } high && high <= low)
        {
            problem = $"max= ({Plain(high)}) has to be above min= ({Plain(low)}).";
            return null;
        }

        // unit="" is a request for no unit at all, so present-and-empty is kept apart from absent.
        string? unit = options.TryGetValue("unit", out var u) ? u : options.TryGetValue("suffix", out var s) ? s : null;

        return new ChartSpec(lines, window, from, to, height, min, max, Option(options, "title"), unit);
    }

    /// <summary>
    /// How often a chart over <paramref name="window"/> is worth reading again. One more
    /// sample on a 30-day line is a two-hundredth of a pixel, so reading a month of history
    /// every thirty-second sweep would be the database working flat out to draw the same
    /// picture. Roughly one point's width of time, then: half a minute for an hour's chart,
    /// seven minutes for a day's, an hour for anything a week or longer.
    /// </summary>
    public static TimeSpan RefreshEvery(TimeSpan window)
    {
        var every = TimeSpan.FromTicks(window.Ticks / 200);
        if (every < TimeSpan.FromSeconds(30))
            return TimeSpan.FromSeconds(30);
        return every > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : every;
    }

    /// <summary>
    /// At most <paramref name="points"/> values, each the average of its share of the line.
    /// The first and last are kept as they are, so "now" on the chart is the newest reading
    /// rather than the average of the last few minutes.
    /// </summary>
    public static IReadOnlyList<double> Thin(IReadOnlyList<double> values, int points = MaxPoints)
    {
        if (values.Count <= points || points < 3)
            return values;
        var thinned = new double[points];
        thinned[0] = values[0];
        thinned[^1] = values[^1];
        var inner = values.Count - 2;
        for (var i = 1; i < points - 1; i++)
        {
            var from = 1 + (int)((long)(i - 1) * inner / (points - 2));
            var to = 1 + (int)((long)i * inner / (points - 2));
            var sum = 0d;
            for (var j = from; j < to; j++)
                sum += values[j];
            thinned[i] = sum / Math.Max(1, to - from);
        }
        return thinned;
    }

    /// <summary>The words for a span: "the last 24 hours", "1–30 Sep 2026".</summary>
    public static string Describe(ChartSpec spec)
    {
        if (spec.From is not { } from)
        {
            var window = spec.Window;
            var words = window.TotalHours > 48 && window.TotalDays % 1 == 0 ? $"{window.TotalDays:0} days"
                : window.TotalHours >= 1 && window.TotalHours % 1 == 0 ? $"{window.TotalHours:0} hour{(window.TotalHours == 1 ? "" : "s")}"
                : $"{window.TotalMinutes:0} minutes";
            return $"the last {words}";
        }
        if (spec.To is not { } to)
            return $"since {from.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}";
        var last = to.AddDays(-1);
        if (last == from)
            return from.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        return from.Year == last.Year && from.Month == last.Month
            ? $"{from.Day}–{last.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}"
            : $"{from.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} – {last.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Writes a chart shortcode that <see cref="Read"/> reads back: each line's names quoted
    /// as they need, options after. What the insert helper and the monthly report type.
    /// </summary>
    public static string Write(IEnumerable<ChartLine> lines, IEnumerable<KeyValuePair<string, string>>? options = null)
    {
        var text = new StringBuilder("{{chart: ");
        text.AppendJoin(", ", lines.Select(line => line.Metric.Length > 0
            ? $"{Shortcodes.Quote(line.Connection)} / {Shortcodes.Quote(line.Metric)}"
            : Shortcodes.Quote(line.Connection)));
        foreach (var (key, value) in options ?? [])
        {
            if (value.Length > 0)
                text.Append(' ').Append(key).Append('=').Append(Shortcodes.Quote(value));
        }
        return text.Append("}}").ToString();
    }

    /// <summary>Everything after <c>{{chart:</c> and before the closing braces.</summary>
    private static string Arguments(string source)
    {
        var text = source.Trim();
        if (text.StartsWith("{{", StringComparison.Ordinal))
            text = text[2..];
        if (text.EndsWith("}}", StringComparison.Ordinal))
            text = text[..^2];
        var colon = text.IndexOf(':');
        return colon < 0 ? "" : text[(colon + 1)..];
    }

    /// <summary>The arguments split at commas that are not inside quotes. Empty pieces are dropped.</summary>
    public static IReadOnlyList<string> Pieces(string arguments)
    {
        var pieces = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < arguments.Length; i++)
        {
            var c = arguments[i];
            if (quoted && c == '\\' && i + 1 < arguments.Length)
            {
                current.Append(c).Append(arguments[++i]);
                continue;
            }
            if (c == '"')
                quoted = !quoted;
            if (c == ',' && !quoted)
            {
                pieces.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        pieces.Add(current.ToString());
        return [.. pieces.Where(p => p.Trim().Length > 0)];
    }

    /// <summary>"cpu_percent 24h" → ("cpu_percent", "24h"); a metric with no window keeps its words.</summary>
    private static (string Metric, string Window) SplitWindow(string part)
    {
        var text = part.Trim();
        var space = text.LastIndexOf(' ');
        if (space > 0 && LooksLikeWindow(text[(space + 1)..]))
            return (text[..space].TrimEnd(), text[(space + 1)..]);
        return LooksLikeWindow(text) ? ("", text) : (text, "");
    }

    private static string? Option(Dictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var value) && value.Trim().Length > 0 ? value.Trim() : null;

    private static bool Number(Dictionary<string, string> options, string key, out double? value, out string? problem)
    {
        value = null;
        problem = null;
        if (Option(options, key) is not { } text)
            return true;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || !double.IsFinite(parsed))
        {
            problem = $"{key}=“{text}” is not a number.";
            return false;
        }
        value = parsed;
        return true;
    }

    private static string Plain(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>
/// The drawing arithmetic of a full-size chart, kept pure so the scale, the clamping and the
/// SVG points are checked by tests rather than by eye.
/// </summary>
public static class ChartPlot
{
    /// <summary>One line ready to draw: its polyline points on a 100×40 box.</summary>
    public sealed record Line(string Points, double First, double Last);

    /// <summary>The scale every line shares, and each line's points on it.</summary>
    /// <param name="Low">The value at the bottom of the box.</param>
    /// <param name="High">The value at the top.</param>
    public sealed record Plot(double Low, double High, IReadOnlyList<Line> Lines);

    /// <summary>
    /// Lays every line out on one scale: from <paramref name="min"/> and <paramref name="max"/>
    /// where they were given, else from the lowest and highest value of all the lines — the
    /// same rule as the compare card, since lines on one axis are there to be compared. A
    /// value outside a fixed scale is drawn at the edge rather than off the box, where it
    /// would vanish and the line would look like it had a gap.
    /// </summary>
    public static Plot Build(IReadOnlyList<IReadOnlyList<double>> lines, double? min = null, double? max = null)
    {
        var values = lines.SelectMany(l => l).ToList();
        var low = min ?? (values.Count > 0 ? values.Min() : 0);
        var high = max ?? (values.Count > 0 ? values.Max() : 1);
        if (max is null && min is not null && high <= low)
            high = low + 1;
        if (min is null && max is not null && low >= high)
            low = high - 1;

        var range = high - low;
        var scale = range > 0 ? range : 1;
        var offset = range > 0 ? low : low - 0.5;

        var drawn = lines.Select(line =>
        {
            var step = 100d / Math.Max(1, line.Count - 1);
            var points = string.Join(' ', line.Select((value, index) =>
            {
                var clamped = Math.Clamp(value, Math.Min(low, high), Math.Max(low, high));
                var x = line.Count == 1 ? 50 : index * step;
                var y = 38 - (clamped - offset) / scale * 36;
                return $"{x.ToString("0.##", CultureInfo.InvariantCulture)},{y.ToString("0.##", CultureInfo.InvariantCulture)}";
            }));
            return new Line(points, line.Count > 0 ? line[0] : 0, line.Count > 0 ? line[^1] : 0);
        }).ToList();

        return new Plot(low, high, drawn);
    }
}
