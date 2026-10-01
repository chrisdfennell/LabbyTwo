using System.Globalization;
using System.Text;

namespace LabbyTwo.Core;

/// <summary>Where a reading's warning and critical lines came from, said in the tooltip.</summary>
public enum BandSource
{
    /// <summary>Nothing to judge by: the reading is shown uncoloured.</summary>
    None,

    /// <summary><c>warn=</c> and <c>crit=</c> written in the shortcode.</summary>
    Written,

    /// <summary>The connection's own alert rules for the metric, or the rules for every connection.</summary>
    AlertRules,

    /// <summary>The metric's own declaration — a percentage that fills up.</summary>
    MetricHint,
}

/// <summary>
/// The lines a reading is coloured against: amber past <see cref="Warn"/>, red past
/// <see cref="Crit"/>. Both are in the metric's stored unit, like an alert rule's threshold,
/// so the colour never changes when somebody switches the dashboard to Fahrenheit.
/// </summary>
/// <param name="Falling">True when lower is worse — a battery, free space — so the lines are crossed going down.</param>
/// <param name="Strict">
/// True when the value has to be strictly past the line, which is how an alert rule reads its
/// threshold (see <see cref="AlertRule.IsBreaching"/>): a table cell should not go red at a
/// reading the alert itself calls fine. Written lines and hints count the line itself, as
/// the gauge card always has.
/// </param>
public sealed record MetricBands(double? Warn, double? Crit, bool Falling, BandSource Source, bool Strict = false)
{
    public static MetricBands None { get; } = new(null, null, false, BandSource.None);

    public bool IsEmpty => Warn is null && Crit is null;

    /// <summary>"is-critical", "is-warm", "is-fine" — or "" when there are no lines to judge by.</summary>
    public string Level(double value)
    {
        if (IsEmpty)
            return "";
        if (Crit is { } crit && Past(value, crit))
            return "is-critical";
        if (Warn is { } warn && Past(value, warn))
            return "is-warm";
        return "is-fine";
    }

    private bool Past(double value, double line) => (Falling, Strict) switch
    {
        (false, true) => value > line,
        (false, false) => value >= line,
        (true, true) => value < line,
        (true, false) => value <= line,
    };

    /// <summary>The level in words, for a tooltip and a screen reader — colour alone never says it.</summary>
    public static string Word(string level) => level switch
    {
        "is-critical" => "critical",
        "is-warm" => "warning",
        "is-fine" => "fine",
        _ => "",
    };

    /// <summary>
    /// The lines for one reading. Written lines win; then the alert rules — the connection's
    /// own if it has any for this metric, otherwise the ones for every connection — since
    /// those are where somebody already said what "too high" means for this machine; then
    /// the metric's own hint. Unusual-for-the-time rules have no fixed line and are skipped,
    /// as are disabled ones.
    ///
    /// One rule is the critical line. Two or more on the same side make the nearest the
    /// warning and the furthest the critical: "disk above 85" and "disk above 95" is exactly
    /// a warning and an emergency.
    /// </summary>
    public static MetricBands For(
        IReadOnlyList<AlertRule>? rules,
        string connectionId,
        string metricKey,
        MetricSpec spec,
        double? warn = null,
        double? crit = null)
    {
        var fromRules = FromRules(rules, connectionId, metricKey);

        if (warn is not null || crit is not null)
        {
            // Both written: their order says which way is worse. One written: the rules'
            // direction if there are rules, otherwise higher is worse.
            var falling = warn is { } w && crit is { } c ? w > c : fromRules.Falling;
            return new MetricBands(warn, crit, falling, BandSource.Written);
        }

        if (!fromRules.IsEmpty)
            return fromRules;

        return Hint(spec);
    }

    private static MetricBands FromRules(IReadOnlyList<AlertRule>? rules, string connectionId, string metricKey)
    {
        if (rules is null || rules.Count == 0)
            return None;

        var usable = rules
            .Where(r => r.Enabled && !r.IsUnusual && r.Comparison != Comparison.Either
                        && string.Equals(r.Metric, metricKey, StringComparison.OrdinalIgnoreCase)
                        && (r.ConnectionId is null || r.ConnectionId == connectionId))
            .ToList();
        if (usable.Count == 0)
            return None;

        // A rule written for this machine says more about it than one written for all of them.
        var own = usable.Where(r => r.ConnectionId == connectionId).ToList();
        var chosen = own.Count > 0 ? own : usable;

        // "Above" wins a tie: a rule set with both sides is almost always "too hot" plus
        // something odd, and too hot is the one worth colouring.
        var falling = !chosen.Any(r => r.Comparison == Comparison.Above);
        var lines = chosen
            .Where(r => r.Comparison == (falling ? Comparison.Below : Comparison.Above))
            .Select(r => r.Threshold)
            .Distinct()
            .OrderBy(t => falling ? -t : t)
            .ToList();

        return lines.Count == 1
            ? new MetricBands(null, lines[0], falling, BandSource.AlertRules, Strict: true)
            : new MetricBands(lines[0], lines[^1], falling, BandSource.AlertRules, Strict: true);
    }

    /// <summary>
    /// What the metric itself suggests. Only a percentage that fills up says anything — the
    /// gauge card's 80% and 95% — because a CPU at 90% for a minute is a busy machine, not a
    /// fault, and colouring it red teaches people to stop looking at the colour.
    /// </summary>
    public static MetricBands Hint(MetricSpec spec) =>
        spec.Capacity is { Rising: true, Full: 100 } && spec.Unit.Trim() == "%"
            ? new MetricBands(80, 95, false, BandSource.MetricHint)
            : None;

    /// <summary>"warning at 80%, critical at 95% (from its alert rules)" — for a tooltip.</summary>
    public string Describe(Func<double, string> format)
    {
        if (IsEmpty)
            return "";
        var parts = new List<string>();
        if (Warn is { } warn)
            parts.Add($"warning {(Falling ? "below" : "above")} {format(warn)}");
        if (Crit is { } crit)
            parts.Add($"critical {(Falling ? "below" : "above")} {format(crit)}");
        var from = Source switch
        {
            BandSource.AlertRules => " (from its alert rules)",
            BandSource.MetricHint => " (the usual lines for a disk or volume)",
            _ => "",
        };
        return string.Join(", ", parts) + from;
    }
}

/// <summary>
/// The parts of a shortcode's arguments that the general grammar has no word for — several
/// names separated by commas, two groups separated by one slash — shared by
/// <see cref="TableShortcode"/> and the gauge. Each piece is still read by
/// <see cref="Shortcodes.Parse"/>, so quoting and escaping follow the one set of rules.
/// </summary>
public static class ShortcodeArguments
{
    /// <summary>Everything after <c>{{kind:</c> and before the closing braces.</summary>
    public static string Of(string source)
    {
        var text = source.Trim();
        if (text.StartsWith("{{", StringComparison.Ordinal))
            text = text[2..];
        if (text.EndsWith("}}", StringComparison.Ordinal))
            text = text[..^2];
        var colon = text.IndexOf(':');
        return colon < 0 ? "" : text[(colon + 1)..];
    }

    /// <summary>The text split at <paramref name="separator"/> wherever it is not inside quotes.</summary>
    public static IReadOnlyList<string> Split(string text, char separator)
    {
        var pieces = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted && c == '\\' && i + 1 < text.Length)
            {
                current.Append(c).Append(text[++i]);
                continue;
            }
            if (c == '"')
                quoted = !quoted;
            if (c == separator && !quoted)
            {
                pieces.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        pieces.Add(current.ToString());
        return pieces;
    }

    /// <summary>One name as the shortcode grammar reads it — quotes taken off, options dropped. Empty when there is none.</summary>
    public static string Name(string piece) =>
        Shortcodes.Parse("{{x:" + piece + "}}") is { Target.Count: > 0 } parsed ? string.Join(" / ", parsed.Target) : "";

    public static bool Number(Shortcode code, string key, out double? value, out string? problem)
    {
        value = null;
        problem = null;
        if (code.Option(key).Trim() is not { Length: > 0 } text)
            return true;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || !double.IsFinite(parsed))
        {
            problem = $"{key}=“{text}” is not a number.";
            return false;
        }
        value = parsed;
        return true;
    }
}

/// <summary>What <c>{{table: …}}</c> asks for, before any name is looked up.</summary>
/// <param name="Connections">The rows, by name or id, in the order written. Empty when <paramref name="Tab"/> is set.</param>
/// <param name="Tab">A tab whose cards' connections are the rows, or null.</param>
/// <param name="Columns">Metric keys or labels, or one of <see cref="TableShortcode.SpecialColumns"/>.</param>
/// <param name="Sparkline">The window for a trend line under each reading, or null for none.</param>
public sealed record TableSpec(
    IReadOnlyList<string> Connections,
    string? Tab,
    IReadOnlyList<string> Columns,
    TimeSpan? Sparkline,
    string? Title);

/// <summary>
/// Reads <c>{{table: "QNAP NAS", Pi, PC / status, cpu_percent, temp_c sparkline=24h}}</c>:
/// rows before the slash, columns after it, both separated by commas outside quotes. The
/// rows may instead be <c>tab "Media"</c> — every connection a card on that tab is bound to.
/// </summary>
public static class TableShortcode
{
    /// <summary>Columns that are not a metric: the status word, 30-day availability, how long it has been as it is.</summary>
    public static readonly IReadOnlyList<string> SpecialColumns = ["status", "uptime", "since"];

    /// <summary>More than this is a page, not a table in a note; the Connections page is for that.</summary>
    public const int MaxRows = 40;

    public const int MaxColumns = 12;

    public static TableSpec? Read(Shortcode code, out string? problem)
    {
        problem = null;
        var halves = ShortcodeArguments.Split(ShortcodeArguments.Of(code.Source), '/');
        if (halves.Count != 2)
        {
            problem = halves.Count < 2
                ? "Say what goes in the rows and the columns: {{table: NAS, Pi / status, cpu_percent}}."
                : "A table has one slash, between the rows and the columns. A name with a slash in it goes in quotes.";
            return null;
        }

        string? tab = code.Option("tab") is { Length: > 0 } namedTab ? namedTab.Trim() : null;
        var rows = new List<string>();
        var rowText = halves[0].Trim();
        if (tab is null && rowText.Length > 4 && rowText.StartsWith("tab", StringComparison.OrdinalIgnoreCase) && char.IsWhiteSpace(rowText[3]))
        {
            tab = ShortcodeArguments.Name(rowText[4..]);
            if (tab.Length == 0)
            {
                problem = "Say which tab: {{table: tab \"Media\" / status}}.";
                return null;
            }
        }
        else if (tab is null)
        {
            foreach (var piece in ShortcodeArguments.Split(rowText, ','))
            {
                if (ShortcodeArguments.Name(piece) is { Length: > 0 } name)
                    rows.Add(name);
            }
            if (rows.Count == 0)
            {
                problem = "Say which connections go in the rows: {{table: NAS, Pi / status}}.";
                return null;
            }
            if (rows.Count > MaxRows)
            {
                problem = $"A table holds at most {MaxRows} rows; this asks for {rows.Count}.";
                return null;
            }
        }

        var columns = new List<string>();
        foreach (var piece in ShortcodeArguments.Split(halves[1], ','))
        {
            if (ShortcodeArguments.Name(piece) is { Length: > 0 } name)
                columns.Add(name);
        }
        if (columns.Count == 0)
        {
            problem = "Say what goes in the columns: {{table: NAS / status, cpu_percent}}.";
            return null;
        }
        if (columns.Count > MaxColumns)
        {
            problem = $"A table holds at most {MaxColumns} columns; this asks for {columns.Count}.";
            return null;
        }

        TimeSpan? spark = null;
        if (code.Option("sparkline").Trim() is { Length: > 0 } sparkText && !IsNo(sparkText))
        {
            if (IsYes(sparkText))
            {
                spark = TimeSpan.FromHours(24);
            }
            else if (ParseWindow(sparkText, out problem) is { } window)
            {
                spark = window;
            }
            else
            {
                return null;
            }
        }

        return new TableSpec(rows, tab, columns, spark, code.Option("title") is { Length: > 0 } title ? title : null);
    }

    public static bool IsSpecial(string column) =>
        SpecialColumns.Contains(column.Trim(), StringComparer.OrdinalIgnoreCase);

    private static bool IsYes(string text) => text.ToLowerInvariant() is "true" or "yes" or "on";

    private static bool IsNo(string text) => text.ToLowerInvariant() is "false" or "no" or "off";

    /// <summary>A sparkline's window — the same 1h to 30d a {{sparkline}} takes.</summary>
    private static TimeSpan? ParseWindow(string text, out string? problem)
    {
        problem = null;
        var trimmed = text.Trim();
        var digits = trimmed.TakeWhile(char.IsAsciiDigit).Count();
        if (digits is 0 or > 4)
        {
            problem = $"sparkline=“{text}” is not a window. Write it like 1h, 24h, 7d or 30d.";
            return null;
        }
        var count = int.Parse(trimmed[..digits], CultureInfo.InvariantCulture);
        TimeSpan? window = trimmed[digits..].Trim().ToLowerInvariant() switch
        {
            "m" or "min" => TimeSpan.FromMinutes(count),
            "h" => TimeSpan.FromHours(count),
            "d" => TimeSpan.FromDays(count),
            "w" => TimeSpan.FromDays(count * 7),
            _ => null,
        };
        if (window is null)
        {
            problem = $"sparkline=“{text}” is not a window. Write it like 1h, 24h, 7d or 30d.";
            return null;
        }
        if (window < TimeSpan.FromHours(1) || window > TimeSpan.FromDays(30))
        {
            problem = $"A sparkline covers between 1h and 30d; “{text}” is outside that.";
            return null;
        }
        return window;
    }
}

public enum GaugeStyle { Ring, Bar }

/// <summary>What <c>{{gauge: …}}</c> asks for. Every number is in the metric's stored unit, as the gauge card's are.</summary>
public sealed record GaugeSpec(
    string Connection,
    string Metric,
    double? Min,
    double? Max,
    double? Warn,
    double? Crit,
    string? Label,
    bool Medium,
    GaugeStyle Style);

/// <summary>
/// Reads <c>{{gauge: "QNAP NAS" / disk_percent max=100 warn=80 crit=90 size=medium style=bar}}</c>
/// and does the arithmetic of drawing one: where the needle sits between the ends.
/// </summary>
public static class GaugeShortcode
{
    public static GaugeSpec? Read(Shortcode code, out string? problem)
    {
        problem = null;
        var connection = code.Part(0) is { Length: > 0 } part ? part : code.Option("connection");
        if (connection.Length == 0)
        {
            problem = "Say which connection: {{gauge: NAS / disk_percent}}.";
            return null;
        }
        var metric = code.Part(1) is { Length: > 0 } named ? named : code.Option("metric");
        if (metric.Length == 0)
        {
            problem = $"Say which metric: {{{{gauge: {Shortcodes.QuoteTarget(connection)} / disk_percent}}}}.";
            return null;
        }
        if (code.Target.Count > 2)
        {
            problem = "A gauge is one connection and one metric. A name with a slash in it goes in quotes.";
            return null;
        }

        if (!ShortcodeArguments.Number(code, "min", out var min, out problem)
            || !ShortcodeArguments.Number(code, "max", out var max, out problem)
            || !ShortcodeArguments.Number(code, "warn", out var warn, out problem)
            || !ShortcodeArguments.Number(code, "crit", out var crit, out problem))
        {
            return null;
        }
        if (min is { } low && max is { } high && high <= low)
        {
            problem = $"max= has to be above min=.";
            return null;
        }

        var size = code.Option("size", "small").Trim().ToLowerInvariant();
        if (size is not ("small" or "medium"))
        {
            problem = $"size=“{size}” is not a size. Use small or medium.";
            return null;
        }
        var style = code.Option("style", "ring").Trim().ToLowerInvariant();
        if (style is not ("ring" or "bar"))
        {
            problem = $"style=“{style}” is not a style. Use ring or bar.";
            return null;
        }

        return new GaugeSpec(connection, metric, min, max, warn, crit,
            code.Option("label") is { Length: > 0 } label ? label : null,
            size == "medium", style == "bar" ? GaugeStyle.Bar : GaugeStyle.Ring);
    }

    /// <summary>
    /// The ends of the scale. Written ends win; otherwise a percentage runs 0 to 100, and
    /// anything else 0 to 100 as the gauge card has always assumed — unless a line sits past
    /// 100, in which case the scale reaches a quarter beyond the furthest one, so the lines
    /// are on it.
    /// </summary>
    public static (double Min, double Max) Scale(GaugeSpec spec, MetricSpec metric, MetricBands bands)
    {
        var min = spec.Min ?? 0;
        if (spec.Max is { } max)
            return (min, max);
        var furthest = new[] { bands.Warn, bands.Crit }.Where(v => v is not null).Select(v => v!.Value).DefaultIfEmpty(0).Max();
        var top = metric.Unit.Trim() == "%" || furthest <= 100 ? 100 : furthest * 1.25;
        return (min, top <= min ? min + 1 : top);
    }

    /// <summary>Where a value sits on the scale, 0 to 1, pinned to the ends.</summary>
    public static double Fraction(double value, double min, double max) =>
        max <= min ? 0 : Math.Clamp((value - min) / (max - min), 0, 1);
}
