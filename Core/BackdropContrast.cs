namespace LabbyTwo.Core;

/// <summary>
/// Whether text stays readable over a background — an estimate, worked out with the same
/// WCAG arithmetic as the theme editor's checker (<see cref="Contrast"/>).
///
/// A picture is not one colour, so it is judged by samples of it: the colours of a coarse
/// grid over the picture, measured by the browser that drew it. Each sample is dimmed the way
/// the page dims it, a glass card's fill is laid over that, and the text is measured against
/// the result. The <em>worst</em> sample is what counts: text over the bright sky in the top
/// corner is unreadable however dark the rest of the photo is. A gradient is judged at its
/// stops, which are its extremes; a solid colour is one sample.
///
/// It stays an estimate. The glass's saturation boost and the blur of what is behind it
/// shift the colours a little, and a grid of averages smooths over a small bright detail —
/// which, blurred behind a card, is also roughly what the eye gets.
/// </summary>
public static class BackdropContrast
{
    /// <summary>The pairs worth checking, against the page and against a glass card.</summary>
    public static readonly IReadOnlyList<Contrast.Pair> Pairs =
    [
        new("text", "backdrop", "Headings on the background", Contrast.Use.Text),
        new("muted", "backdrop", "Secondary text on the background", Contrast.Use.Text),
        new("text", "glass", "Text on a glass card", Contrast.Use.Text),
        new("muted", "glass", "Secondary text on a glass card", Contrast.Use.Text),
    ];

    /// <summary>
    /// The colours behind the cards before dimming: the picture's samples, a gradient's stops,
    /// or the one solid colour. Empty when there is nothing to judge (no background, or a
    /// picture whose samples have not arrived).
    /// </summary>
    public static IReadOnlyList<ThemeColor> Behind(Backdrop backdrop, ThemeVariant variant, IReadOnlyList<ThemeColor> imageSamples)
    {
        var page = variant.Effective("ink") ?? new ThemeColor(0, 0, 0);
        IEnumerable<BackdropColour> colours = backdrop.Kind switch
        {
            BackdropKind.Solid => [backdrop.Solid],
            BackdropKind.Gradient => backdrop.Stops,
            _ => [],
        };

        if (backdrop.Kind == BackdropKind.Image)
            return [.. imageSamples.Select(s => s.WithAlpha(1))];

        return [.. colours.Select(c => c.Resolve(variant)).OfType<ThemeColor>().Select(c => c.Over(page.WithAlpha(1)))];
    }

    /// <summary>One sample as the page shows it: the dim laid over it, the way <see cref="BackdropCss"/> draws it.</summary>
    public static ThemeColor Dimmed(ThemeColor behind, Backdrop backdrop, ThemeVariant variant)
    {
        var amount = Math.Clamp(backdrop.Dim, 0, Backdrop.MaxDim) / 100d;
        if (amount <= 0)
            return behind.WithAlpha(1);

        // color-mix(in srgb, X p%, transparent) keeps X's colour and multiplies its alpha by p.
        var dim = backdrop.DimWith == BackdropDimWith.Overlay
            ? variant.Effective("overlay") ?? new ThemeColor(0, 0, 0, 0.5)
            : variant.Effective("ink") ?? new ThemeColor(0, 0, 0);
        return dim.WithAlpha(dim.A * amount).Over(behind.WithAlpha(1));
    }

    /// <summary>A glass card over one dimmed sample: the card colour at the glass opacity.</summary>
    public static ThemeColor Glass(ThemeColor dimmed, Backdrop backdrop, ThemeVariant variant)
    {
        var panel = variant.Effective("panel") ?? dimmed;
        var page = variant.Effective("ink") ?? dimmed;
        var opaque = panel.Over(page.WithAlpha(1));
        return opaque.WithAlpha(backdrop.GlassOpacity / 100d).Over(dimmed);
    }

    /// <summary>
    /// The worst case of each pair across every sample. Glass pairs only when glass is on —
    /// otherwise the cards are solid and the theme's own checker already covers them.
    /// Empty when there is nothing to judge.
    /// </summary>
    public static IReadOnlyList<Contrast.Result> Check(Backdrop backdrop, ThemeVariant variant, IReadOnlyList<ThemeColor> imageSamples)
    {
        var behind = Behind(backdrop, variant, imageSamples);
        if (behind.Count == 0)
            return [];

        var results = new List<Contrast.Result>();
        foreach (var pair in Pairs)
        {
            if (pair.Background == "glass" && !backdrop.Glass)
                continue;
            if (variant.Effective(pair.Foreground) is not { } fg)
                continue;

            var worst = double.MaxValue;
            foreach (var sample in behind)
            {
                var dimmed = Dimmed(sample, backdrop, variant);
                var surface = pair.Background == "glass" ? Glass(dimmed, backdrop, variant) : dimmed;
                worst = Math.Min(worst, Contrast.Ratio(fg.Over(surface), surface));
            }

            results.Add(new Contrast.Result(pair, worst, Contrast.Grade(worst, pair.Use)));
        }
        return results;
    }
}
