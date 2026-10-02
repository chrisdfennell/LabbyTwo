namespace LabbyTwo.Core;

/// <summary>Where a token sits in the theme editor. The order here is the order on screen.</summary>
public enum ThemeGroup
{
    Surfaces,
    Text,
    Accent,
    Status,
    Charts,
    Code,
}

/// <summary>
/// The colour tokens a theme can set — the whole vocabulary between a theme and the
/// stylesheet.
///
/// Every colour on every page is written against one of these (or against something
/// derived from one, like --accent-soft), which is what lets a theme recolour the app
/// without the stylesheet knowing themes exist. The test suite holds the CSS to that: a
/// colour literal outside a token definition fails the build.
///
/// Two kinds. A <b>required</b> token has no sensible default and every theme variant must
/// give it. An <b>optional</b> one has a fallback in app.css — usually another token, like
/// --link following --accent — so a theme that does not care about it gets something that
/// fits, and a theme that does can say exactly what it wants. Adding a token later is
/// therefore safe: add it as optional with a fallback, and every saved and imported theme
/// keeps working.
/// </summary>
public static class ThemeTokens
{
    /// <param name="Name">The custom property, without its leading dashes.</param>
    /// <param name="Fallback">
    /// What it is when a theme does not set it — written into the theme block in its place,
    /// and given in app.css for a page with no theme block at all. Null for a required token.
    /// "initial" makes the property the guaranteed-invalid value, so the rule reading it falls
    /// through to its own default (the browser's selection colour).
    /// </param>
    public sealed record Token(string Name, string Label, ThemeGroup Group, string Hint, string? Fallback = null)
    {
        public bool Required => Fallback is null;
        public string Property => "--" + Name;
    }

    public static readonly IReadOnlyList<Token> All =
    [
        new("ink", "Page", ThemeGroup.Surfaces, "Behind everything."),
        new("panel", "Card", ThemeGroup.Surfaces, "Cards, the sidebar and dialogs."),
        new("panel-2", "Raised", ThemeGroup.Surfaces, "Inside a card: hovered rows, chips, tooltips."),
        new("edge", "Border", ThemeGroup.Surfaces, "Card outlines and dividers."),
        new("input-bg", "Field", ThemeGroup.Surfaces, "Text boxes and drop-downs.", "var(--panel-2)"),
        new("input-border", "Field border", ThemeGroup.Surfaces, "The outline of a text box.", "var(--edge)"),
        new("tooltip-bg", "Tooltip", ThemeGroup.Surfaces, "Chart read-outs and pop-ups.", "var(--panel-2)"),
        new("overlay", "Overlay", ThemeGroup.Surfaces, "The dimming behind a dialog. Usually translucent.", "rgba(0, 0, 0, 0.5)"),
        new("shadow", "Shadow", ThemeGroup.Surfaces, "Under raised cards. Usually translucent."),

        new("text", "Text", ThemeGroup.Text, "Body text."),
        new("muted", "Secondary text", ThemeGroup.Text, "Captions, labels and timestamps."),
        new("placeholder", "Placeholder", ThemeGroup.Text, "Hints inside empty boxes."),
        new("link", "Link", ThemeGroup.Text, "Links in text.", "var(--accent)"),
        new("selection", "Selection", ThemeGroup.Text, "Selected text. Left alone, the browser's own.", "initial"),

        new("accent", "Accent", ThemeGroup.Accent, "Buttons, the active tab, focus rings, the first chart line."),
        new("accent-ink", "Text on accent", ThemeGroup.Accent, "The label on a primary button."),
        new("accent-2", "Second accent", ThemeGroup.Accent, "A contrasting hue for \"important\" callouts.", "#9a6bf0"),
        new("focus", "Focus ring", ThemeGroup.Accent, "The outline round whatever the keyboard is on.", "var(--accent)"),
        new("neutral", "Neutral", ThemeGroup.Accent, "Secondary buttons and grey badges.", "#6c757d"),
        new("neutral-ink", "Text on neutral", ThemeGroup.Accent, "The label on a secondary button.", "#ffffff"),

        new("up", "Up", ThemeGroup.Status, "Healthy, success."),
        new("down", "Down", ThemeGroup.Status, "Failing, danger."),
        new("warn", "Warning", ThemeGroup.Status, "Degraded, attention."),
        new("info", "Info", ThemeGroup.Status, "Informational notices.", "var(--accent)"),
        new("up-soft", "Up, tinted", ThemeGroup.Status, "Behind a healthy row.", "color-mix(in srgb, var(--up) 14%, transparent)"),
        new("down-soft", "Down, tinted", ThemeGroup.Status, "Behind a failing row.", "color-mix(in srgb, var(--down) 14%, transparent)"),
        new("warn-soft", "Warning, tinted", ThemeGroup.Status, "Behind a degraded row.", "color-mix(in srgb, var(--warn) 14%, transparent)"),

        new("chart-1", "Series 1", ThemeGroup.Charts, "Follows the accent unless set.", "var(--accent)"),
        new("chart-2", "Series 2", ThemeGroup.Charts, "", "#ffb84d"),
        new("chart-3", "Series 3", ThemeGroup.Charts, "", "#35d07f"),
        new("chart-4", "Series 4", ThemeGroup.Charts, "", "#c792ea"),
        new("chart-5", "Series 5", ThemeGroup.Charts, "", "#ff8a65"),
        new("chart-6", "Series 6", ThemeGroup.Charts, "", "#4dd0e1"),
        new("chart-7", "Series 7", ThemeGroup.Charts, "", "#f06292"),
        new("chart-8", "Series 8", ThemeGroup.Charts, "", "#aed581"),

        new("code", "Code", ThemeGroup.Code, "Inline code and values in Markdown."),
    ];

    private static readonly Dictionary<string, Token> ByName = All.ToDictionary(t => t.Name, StringComparer.Ordinal);

    /// <summary>The names every variant must give.</summary>
    public static readonly IReadOnlyList<string> RequiredNames = [.. All.Where(t => t.Required).Select(t => t.Name)];

    /// <summary>How many chart series a theme can colour; a chart with more reuses them, mixed.</summary>
    public const int ChartSeries = 8;

    public static bool IsKnown(string? name) => name is not null && ByName.ContainsKey(name);

    public static Token? Find(string name) => ByName.GetValueOrDefault(name);

    /// <summary>
    /// The colour of chart series <paramref name="index"/> (from 0), as a CSS value. Past the
    /// eighth the palette comes round again, pulled towards the text colour so a ninth line
    /// is told apart from the first.
    /// </summary>
    public static string ChartColour(int index)
    {
        var slot = index % ChartSeries + 1;
        return index < ChartSeries
            ? $"var(--chart-{slot})"
            : $"color-mix(in srgb, var(--chart-{slot}) 60%, var(--text))";
    }
}
