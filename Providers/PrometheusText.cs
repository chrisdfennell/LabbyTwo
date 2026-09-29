using System.Globalization;
using System.Text;

namespace LabbyTwo.Providers;

/// <summary>
/// The Prometheus text format — what an exporter serves at <c>/metrics</c> — read into
/// samples. The Prometheus provider does not need this, because it asks a Prometheus
/// server's query API and gets JSON back; this is for reading an exporter directly, with
/// no Prometheus in between, which is how most home labs that run cloudflared have it.
///
/// Deliberately small. It reads sample lines (<c>name{label="value"} 1.5 [timestamp]</c>),
/// ignores <c># HELP</c> and <c># TYPE</c>, and skips any line it cannot make sense of
/// rather than failing the whole page — an exporter adding a metric in a shape this does
/// not expect should cost that one metric, not every number on the card.
/// </summary>
public static class PrometheusText
{
    /// <summary>One line of the exposition: a metric name, its labels and its value.</summary>
    public sealed record Sample(string Name, IReadOnlyDictionary<string, string> Labels, double Value)
    {
        public string Label(string key) => Labels.TryGetValue(key, out var value) ? value : "";
    }

    private static readonly IReadOnlyDictionary<string, string> NoLabels = new Dictionary<string, string>();

    public static IReadOnlyList<Sample> Parse(string text)
    {
        var samples = new List<Sample>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            if (TryParseLine(line) is { } sample)
                samples.Add(sample);
        }

        return samples;
    }

    /// <summary>The sum of every series of one metric — across connections, protocols, whatever the labels split it by.</summary>
    public static double? Sum(IEnumerable<Sample> samples, string name)
    {
        double? total = null;
        foreach (var sample in samples)
        {
            if (sample.Name == name && double.IsFinite(sample.Value))
                total = (total ?? 0) + sample.Value;
        }
        return total;
    }

    private static Sample? TryParseLine(string line)
    {
        var at = 0;
        while (at < line.Length && line[at] != '{' && !char.IsWhiteSpace(line[at]))
            at++;

        var name = line[..at];
        if (name.Length == 0)
            return null;

        var labels = NoLabels;
        if (at < line.Length && line[at] == '{')
        {
            if (ReadLabels(line, ref at) is not { } read)
                return null;
            labels = read;
        }

        // The value, then an optional timestamp this has no use for.
        var rest = line[at..].Trim();
        var end = rest.IndexOfAny([' ', '\t']);
        var token = end < 0 ? rest : rest[..end];

        return ParseValue(token) is { } value ? new Sample(name, labels, value) : null;
    }

    /// <summary>
    /// <c>{a="1",b="two \"quoted\""}</c>, leaving <paramref name="at"/> just past the brace.
    /// Values may hold commas, braces and escaped quotes, which is why this walks the
    /// characters rather than splitting on them.
    /// </summary>
    private static Dictionary<string, string>? ReadLabels(string line, ref int at)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        at++; // past '{'

        while (true)
        {
            while (at < line.Length && (char.IsWhiteSpace(line[at]) || line[at] == ','))
                at++;
            if (at >= line.Length)
                return null;
            if (line[at] == '}')
            {
                at++;
                return labels;
            }

            var keyStart = at;
            while (at < line.Length && line[at] != '=' && !char.IsWhiteSpace(line[at]))
                at++;
            var key = line[keyStart..at];

            while (at < line.Length && char.IsWhiteSpace(line[at]))
                at++;
            if (at >= line.Length || line[at] != '=')
                return null;
            at++;
            while (at < line.Length && char.IsWhiteSpace(line[at]))
                at++;
            if (at >= line.Length || line[at] != '"')
                return null;
            at++;

            var value = new StringBuilder();
            while (at < line.Length && line[at] != '"')
            {
                if (line[at] == '\\' && at + 1 < line.Length)
                {
                    at++;
                    value.Append(line[at] == 'n' ? '\n' : line[at]);
                }
                else
                {
                    value.Append(line[at]);
                }
                at++;
            }
            if (at >= line.Length)
                return null;
            at++; // past the closing quote

            if (key.Length > 0)
                labels[key] = value.ToString();
        }
    }

    private static double? ParseValue(string token) => token switch
    {
        "+Inf" or "Inf" => double.PositiveInfinity,
        "-Inf" => double.NegativeInfinity,
        "NaN" => double.NaN,
        _ => double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
    };
}
