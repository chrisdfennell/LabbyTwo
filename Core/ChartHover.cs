using System.Globalization;
using System.Text.Json;

namespace LabbyTwo.Core;

/// <summary>
/// What a chart tells <c>chart-hover.js</c> about itself.
///
/// One builder rather than four charts each writing their own JSON, for the usual reason:
/// the script reads one shape, and four places writing it by hand is four chances to write
/// a slightly different one and only find out by hovering.
///
/// The values handed over are already converted into whatever units the user reads in,
/// because the server is the only thing that knows that — see <see cref="Units"/>. The
/// script only formats and positions.
/// </summary>
public static class ChartHover
{
    /// <param name="Name">Shown beside the value when a chart has more than one line. Empty for a lone series.</param>
    /// <param name="Colour">A CSS colour, usually a var() so it follows the accent.</param>
    public sealed record Series(string Name, string Colour, IReadOnlyList<double> Values);

    /// <summary>
    /// The attribute value, or null when there is nothing worth hovering. Null rather than
    /// an empty object so the caller can leave the attribute off entirely: a chart with one
    /// point has nothing to read off it, and a crosshair on it would be a promise of detail
    /// that is not there.
    /// </summary>
    /// <param name="unit">The suffix, exactly as the metric declares it — the leading space in " ms" is deliberate and survives.</param>
    /// <param name="from">First reading's time, for the label. Omit for a chart with no time axis.</param>
    public static string? Attribute(
        IReadOnlyList<Series> series,
        string unit = "",
        int decimals = 0,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null)
    {
        var real = series.Where(s => s.Values.Count > 1).ToList();
        if (real.Count == 0)
            return null;

        var payload = new Dictionary<string, object>
        {
            // Short keys because a day of readings is a few thousand numbers and this sits in
            // an attribute on every chart on the page.
            ["u"] = unit,
            ["d"] = decimals,
            ["s"] = real.Select(s => new Dictionary<string, object>
            {
                ["n"] = s.Name,
                ["c"] = s.Colour,
                // Rounded to the decimals actually shown. The raw doubles serialise as
                // 17 significant figures each, which triples the size of the attribute to
                // carry precision the tooltip then throws away.
                ["v"] = s.Values.Select(v => Math.Round(v, Math.Clamp(decimals + 1, 0, 6))).ToArray(),
            }).ToArray(),
        };

        if (from is { } start && to is { } end)
        {
            payload["t0"] = start.ToUnixTimeSeconds();
            payload["t1"] = end.ToUnixTimeSeconds();
        }

        return JsonSerializer.Serialize(payload);
    }

    /// <summary>One series, which is most of them.</summary>
    public static string? Attribute(
        IReadOnlyList<double> values,
        string unit = "",
        int decimals = 0,
        string colour = "var(--accent)",
        string name = "",
        DateTimeOffset? from = null,
        DateTimeOffset? to = null)
        => Attribute([new Series(name, colour, values)], unit, decimals, from, to);

    /// <summary>
    /// A number the script will print, formatted the same way here — used by the tests that
    /// pin the two together, since a tooltip disagreeing with the axis beside it is the whole
    /// failure this could have.
    /// </summary>
    public static string Format(double value, string unit, int decimals) =>
        value.ToString($"F{decimals}", CultureInfo.InvariantCulture) + unit;
}
