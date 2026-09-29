namespace LabbyTwo.Core;

/// <summary>
/// The words and numbers behind the edit toolbar's size controls: which widths are offered,
/// what each is called, and how much of the row a card really takes on the screen it is
/// being looked at on.
///
/// A card's width is stored as twelfths, and stays that way — this is only how the editor
/// talks about it. The twelfths are what the width *is* on an ordinary monitor, so they are
/// what the preset buttons are named after. But the grid does not keep twelve columns
/// everywhere: a laptop and a tablet widen narrow cards, a phone makes everything full
/// width, and a wide screen adds columns so a card keeps its size rather than stretching.
/// A "½" card on a 5K monitor is a quarter of the row, and a label that said "½" there
/// would be the one thing on the page that was wrong. So the card's own label says how
/// much of the row it takes <em>here</em>, worked out once for each layout the stylesheet
/// has and picked between by the same breakpoints (see <see cref="Shares"/>).
/// </summary>
public static class CardSizes
{
    /// <summary>A width the toolbar offers as one button.</summary>
    /// <param name="Span">Twelfths of a standard row, as stored on the widget.</param>
    /// <param name="Symbol">What the button shows.</param>
    /// <param name="Name">What it is called, for its tooltip and for a screen reader.</param>
    public sealed record WidthPreset(int Span, string Symbol, string Name);

    /// <summary>
    /// The widths offered, narrowest first. They are the widths that tile a twelve-column
    /// row with no ragged gap (a sixth, a quarter, a third, a half, and what makes each up
    /// to a whole), which is the rule the old stepper followed too. A sixth stays because
    /// the stepper could reach it, and a card already that narrow should be able to see
    /// which button it is.
    /// </summary>
    public static readonly IReadOnlyList<WidthPreset> Presets =
    [
        new(2, "⅙", "One sixth"),
        new(3, "¼", "One quarter"),
        new(4, "⅓", "One third"),
        new(6, "½", "Half"),
        new(8, "⅔", "Two thirds"),
        new(9, "¾", "Three quarters"),
        new(12, "Full", "Full width"),
    ];

    /// <summary>
    /// The preset a stored width is, or null for a width none of the buttons stands for —
    /// a 5 from an old import, say. Out-of-range widths are clamped first, as the grid
    /// clamps them when it draws the card.
    /// </summary>
    public static WidthPreset? PresetFor(int width)
    {
        var span = PageBlocks.ClampWidth(width);
        return Presets.FirstOrDefault(p => p.Span == span);
    }

    /// <summary>The layouts the stylesheet has for the dashboard grid, narrowest first.</summary>
    public enum Layout
    {
        /// <summary>Up to 768px wide: one card per row.</summary>
        Phone,

        /// <summary>Up to 900px: halves and whole rows only.</summary>
        Tablet,

        /// <summary>Up to 1100px: nothing narrower than a third.</summary>
        Laptop,

        /// <summary>Twelve columns, spans as stored.</summary>
        Standard,

        /// <summary>A page column of 2400px or more: 24 columns, full stays full.</summary>
        Wide24,

        /// <summary>3600px or more: 36 columns.</summary>
        Wide36,

        /// <summary>6000px or more: 48 columns.</summary>
        Wide48,
    }

    /// <summary>
    /// The columns a card of this width spans in a layout, and how many columns that
    /// layout's row has. This mirrors the span rules in app.css (the twelfths, the laptop
    /// and tablet overrides, the phone's single column and the wide-screen column counts)
    /// and has to be changed with them; the tests pin the pairs so a drift shows.
    /// </summary>
    public static (int Span, int Columns) SpanIn(int width, Layout layout)
    {
        var span = PageBlocks.ClampWidth(width);
        return layout switch
        {
            Layout.Phone => (12, 12),
            Layout.Tablet => (span <= 6 ? 6 : 12, 12),
            Layout.Laptop => (span <= 3 ? 4 : span, 12),
            Layout.Standard => (span, 12),
            // Full width is full width at every count: it is meant to be a banner.
            Layout.Wide24 => span == 12 ? (24, 24) : (span, 24),
            Layout.Wide36 => span == 12 ? (36, 36) : (span, 36),
            Layout.Wide48 => span == 12 ? (48, 48) : (span, 48),
            _ => (span, 12),
        };
    }

    /// <summary>"½ of the row", "Full row" — how much of the row a card takes in a layout.</summary>
    public static string Share(int width, Layout layout)
    {
        var (span, columns) = SpanIn(width, layout);
        return span >= columns ? "Full row" : $"{Fraction(span, columns)} of the row";
    }

    /// <summary>
    /// The share of the row in every layout, for the card's label to pick from with the
    /// same media and container queries the grid uses. All of them are sent rather than
    /// one guessed on the server, because the server does not know how wide the screen is
    /// and the page can change width without re-rendering (a window resized, a phone
    /// turned round).
    /// </summary>
    public static IReadOnlyList<(Layout Layout, string Text)> Shares(int width) =>
        [.. Enum.GetValues<Layout>().Select(layout => (layout, Share(width, layout)))];

    /// <summary>A reduced fraction, as a single glyph where Unicode has one ("⅓"), else "5/12".</summary>
    public static string Fraction(int numerator, int denominator)
    {
        if (denominator <= 0)
            return "?";
        var divisor = Gcd(Math.Abs(numerator), denominator);
        var (n, d) = (numerator / divisor, denominator / divisor);
        return (n, d) switch
        {
            (1, 2) => "½",
            (1, 3) => "⅓",
            (2, 3) => "⅔",
            (1, 4) => "¼",
            (3, 4) => "¾",
            (1, 5) => "⅕",
            (1, 6) => "⅙",
            (5, 6) => "⅚",
            (1, 8) => "⅛",
            (3, 8) => "⅜",
            (5, 8) => "⅝",
            (7, 8) => "⅞",
            _ when n == d => "1",
            _ => $"{n}/{d}",
        };
    }

    /// <summary>"Auto", "1 row", "4 rows" — a block's height as the toolbar reads it out.</summary>
    public static string Height(int rows) => PageBlocks.ClampRows(rows) switch
    {
        0 => "Auto",
        1 => "1 row",
        var n => $"{n} rows",
    };

    private static int Gcd(int a, int b)
    {
        while (b != 0)
            (a, b) = (b, a % b);
        return a == 0 ? 1 : a;
    }
}
