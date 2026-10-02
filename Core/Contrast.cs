namespace LabbyTwo.Core;

/// <summary>
/// WCAG 2.1 contrast, and the pairs of a theme that it matters for.
///
/// Pure arithmetic, so the editor can run it on every keystroke and the tests can hold the
/// built-in themes to it. Translucent colours are composited first — text at 60% over a card
/// is read as whatever that blend is, not as the colour it was before the blend.
/// </summary>
public static class Contrast
{
    /// <summary>How a pair is used, which decides the bar it has to clear.</summary>
    public enum Use
    {
        /// <summary>Reading text: AA is 4.5:1, AAA 7:1.</summary>
        Text,

        /// <summary>
        /// Something that has to be seen rather than read — a focus ring, a status dot, a chart
        /// line. WCAG's non-text minimum is 3:1; 4.5:1 is counted as "AAA" here, the large-text
        /// enhanced level, since 2.1 sets no higher non-text bar.
        /// </summary>
        Graphic,
    }

    public enum Level { Fail, AA, AAA }

    /// <param name="Foreground">The token drawn on top.</param>
    /// <param name="Background">The token behind it.</param>
    public sealed record Pair(string Foreground, string Background, string Label, Use Use);

    public sealed record Result(Pair Pair, double Ratio, Level Level)
    {
        public bool PassesAA => Level != Level.Fail;

        /// <summary>"7.42:1", rounded down so a pass shown is never a fail rounded up.</summary>
        public string RatioText => $"{Math.Floor(Ratio * 100) / 100:0.00}:1";
    }

    /// <summary>
    /// The pairs that matter: everything a person reads, on every surface it is read on, and
    /// the colours that only have to be seen on the card they sit on.
    /// </summary>
    public static readonly IReadOnlyList<Pair> Pairs =
    [
        new("text", "panel", "Text on a card", Use.Text),
        new("text", "ink", "Text on the page", Use.Text),
        new("text", "panel-2", "Text on a raised surface", Use.Text),
        new("muted", "panel", "Secondary text on a card", Use.Text),
        new("link", "panel", "A link on a card", Use.Text),
        new("accent-ink", "accent", "A primary button's label", Use.Text),
        new("code", "panel", "Code on a card", Use.Text),
        new("up", "panel", "\"Up\" on a card", Use.Text),
        new("down", "panel", "\"Down\" on a card", Use.Text),
        new("warn", "panel", "A warning on a card", Use.Text),
        new("focus", "panel", "The focus ring on a card", Use.Graphic),
        new("accent", "panel", "The accent on a card", Use.Graphic),
    ];

    /// <summary>The WCAG contrast ratio of two opaque colours, from 1 (identical) to 21 (black on white).</summary>
    public static double Ratio(ThemeColor a, ThemeColor b)
    {
        var (light, dark) = a.Luminance >= b.Luminance ? (a.Luminance, b.Luminance) : (b.Luminance, a.Luminance);
        return (light + 0.05) / (dark + 0.05);
    }

    /// <summary>
    /// The ratio of <paramref name="foreground"/> over <paramref name="background"/>, with any
    /// translucency resolved against <paramref name="page"/> first.
    /// </summary>
    public static double Ratio(ThemeColor foreground, ThemeColor background, ThemeColor page)
    {
        var behind = background.Over(page.WithAlpha(1));
        return Ratio(foreground.Over(behind), behind);
    }

    public static Level Grade(double ratio, Use use) => use switch
    {
        Use.Text => ratio >= 7 ? Level.AAA : ratio >= 4.5 ? Level.AA : Level.Fail,
        _ => ratio >= 4.5 ? Level.AAA : ratio >= 3 ? Level.AA : Level.Fail,
    };

    /// <summary>Every pair checked against one variant, using the colours that really reach the screen.</summary>
    public static IReadOnlyList<Result> Check(ThemeVariant variant)
    {
        var page = variant.Effective("ink") ?? new ThemeColor(0, 0, 0);
        var results = new List<Result>();
        foreach (var pair in Pairs)
        {
            if (variant.Effective(pair.Foreground) is not { } fg || variant.Effective(pair.Background) is not { } bg)
                continue;

            var ratio = Ratio(fg, bg, page);
            results.Add(new Result(pair, ratio, Grade(ratio, pair.Use)));
        }
        return results;
    }
}
