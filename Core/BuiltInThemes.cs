namespace LabbyTwo.Core;

/// <summary>
/// The themes that ship with LabbyTwo.
///
/// The named ones use their published palettes' own colours. Where a palette has no colour
/// for a role a dashboard needs — most editor themes have two background shades and LabbyTwo
/// wants three, or no mid-grey readable enough for secondary text — the gap is filled with a
/// blend of two of the palette's own colours rather than something from outside it, and the
/// light ends of palettes whose status colours were designed for dark backgrounds use the
/// palette's darker siblings where it has them. Every variant is checked by the test suite:
/// body text must pass WCAG AA on cards, and High Contrast must pass AAA.
///
/// LabbyTwo's own values are the ones app.css has always had; a test holds the two to each
/// other, so the stylesheet's fallback and the default theme cannot drift apart.
/// </summary>
public static class BuiltInThemes
{
    public const string DefaultId = "labbytwo";

    private static readonly ThemeVariant LabbyDark = ThemeVariant.Of(
        ("ink", "#0a0e13"), ("panel", "#121821"), ("panel-2", "#182030"), ("edge", "#243044"),
        ("text", "#dde5f0"), ("muted", "#8fa0b8"), ("placeholder", "#5d6d85"),
        ("accent", "#4da3ff"), ("accent-ink", "#06121f"),
        ("up", "#35d07f"), ("down", "#ff5c6c"), ("warn", "#ffb84d"), ("code", "#ffc48a"),
        ("shadow", "rgba(0, 0, 0, .6)"));

    private static readonly ThemeVariant LabbyLight = ThemeVariant.Of(
        ("ink", "#f2f5f9"), ("panel", "#ffffff"), ("panel-2", "#eef2f7"), ("edge", "#d5dde8"),
        ("text", "#16202e"), ("muted", "#5c6b7f"), ("placeholder", "#93a1b3"),
        ("accent", "#4da3ff"), ("accent-ink", "#ffffff"),
        ("up", "#1a9e5c"), ("down", "#d32f45"), ("warn", "#b96a06"), ("code", "#b1500a"),
        ("shadow", "rgba(22, 32, 46, .18)"),
        // The one value app.css never had: the accent is too pale on white to be a focus ring
        // anybody can find (2.6:1), so the light end rings in a deeper blue of the same hue.
        ("focus", "#1a6fd6"));

    public static readonly Theme LabbyTwo = new(DefaultId, "LabbyTwo", "LabbyTwo", LabbyDark, LabbyLight,
        "Blue-black and white. The default.", BuiltIn: true);

    public static readonly Theme Slate = new("slate", "Slate", "LabbyTwo",
        LabbyDark.With(("ink", "#14181d"), ("panel", "#1c2229"), ("panel-2", "#242b34"), ("edge", "#333c47"), ("muted", "#9aa7b6")),
        LabbyLight,
        "Warmer, lighter greys — easier to read in a lit room.", BuiltIn: true);

    /// <summary>
    /// True black for an OLED wall panel: the page really is #000, so it draws no power, and
    /// the cards lift a little more than usual or every edge would vanish into it.
    /// </summary>
    public static readonly Theme Black = new("black", "True black", "LabbyTwo",
        LabbyDark.With(("ink", "#000000"), ("panel", "#0b0b0d"), ("panel-2", "#15161a"), ("edge", "#262a31"), ("shadow", "rgba(0, 0, 0, .9)")),
        LabbyLight,
        "For an OLED wall panel: the background draws no power.", BuiltIn: true);

    /// <summary>Nord by Arctic Ice Studio: Polar Night for dark, Snow Storm for light.</summary>
    public static readonly Theme Nord = new("nord", "Nord", "Arctic Ice Studio",
        ThemeVariant.Of(
            ("ink", "#2e3440"), ("panel", "#3b4252"), ("panel-2", "#434c5e"), ("edge", "#4c566a"),
            ("text", "#eceff4"), ("muted", "#d8dee9"), ("placeholder", "#7b88a1"),
            ("accent", "#88c0d0"), ("accent-ink", "#2e3440"), ("link", "#88c0d0"), ("accent-2", "#b48ead"),
            ("neutral", "#7b88a1"),
            ("up", "#a3be8c"), ("down", "#bf616a"), ("warn", "#ebcb8b"), ("info", "#81a1c1"), ("code", "#d08770"),
            ("chart-2", "#ebcb8b"), ("chart-3", "#a3be8c"), ("chart-4", "#b48ead"), ("chart-5", "#d08770"),
            ("chart-6", "#8fbcbb"), ("chart-7", "#bf616a"), ("chart-8", "#5e81ac"),
            ("shadow", "rgba(0, 0, 0, .45)")),
        ThemeVariant.Of(
            ("ink", "#e5e9f0"), ("panel", "#eceff4"), ("panel-2", "#e5e9f0"), ("edge", "#d8dee9"),
            ("text", "#2e3440"), ("muted", "#4c566a"), ("placeholder", "#7b88a1"),
            ("accent", "#5e81ac"), ("accent-ink", "#eceff4"), ("link", "#5e81ac"), ("accent-2", "#b48ead"),
            ("neutral", "#4c566a"),
            ("up", "#5e8a4a"), ("down", "#bf616a"), ("warn", "#a6782f"), ("info", "#5e81ac"), ("code", "#b05a3c"),
            ("chart-2", "#d08770"), ("chart-3", "#a3be8c"), ("chart-4", "#b48ead"), ("chart-5", "#ebcb8b"),
            ("chart-6", "#8fbcbb"), ("chart-7", "#bf616a"), ("chart-8", "#81a1c1"),
            ("shadow", "rgba(46, 52, 64, .16)")),
        "Arctic blues and frost.", BuiltIn: true);

    /// <summary>Dracula by Zeno Rocha. Dark only — the theme has never had a light end.</summary>
    public static readonly Theme Dracula = new("dracula", "Dracula", "Zeno Rocha",
        ThemeVariant.Of(
            ("ink", "#21222c"), ("panel", "#282a36"), ("panel-2", "#343746"), ("edge", "#44475a"),
            ("text", "#f8f8f2"), ("muted", "#adb5cb"), ("placeholder", "#6272a4"),
            ("accent", "#bd93f9"), ("accent-ink", "#21222c"), ("link", "#8be9fd"), ("accent-2", "#ff79c6"),
            ("neutral", "#6272a4"), ("selection", "#44475a"),
            ("up", "#50fa7b"), ("down", "#ff5555"), ("warn", "#ffb86c"), ("info", "#8be9fd"), ("code", "#f1fa8c"),
            ("chart-2", "#ffb86c"), ("chart-3", "#50fa7b"), ("chart-4", "#ff79c6"), ("chart-5", "#8be9fd"),
            ("chart-6", "#f1fa8c"), ("chart-7", "#ff5555"), ("chart-8", "#6272a4"),
            ("shadow", "rgba(0, 0, 0, .5)")),
        null,
        "Purple, pink and neon on charcoal.", BuiltIn: true);

    /// <summary>Catppuccin: Mocha for dark, Latte for light.</summary>
    public static readonly Theme Catppuccin = new("catppuccin", "Catppuccin", "Catppuccin",
        ThemeVariant.Of(
            ("ink", "#11111b"), ("panel", "#1e1e2e"), ("panel-2", "#313244"), ("edge", "#45475a"),
            ("text", "#cdd6f4"), ("muted", "#a6adc8"), ("placeholder", "#6c7086"),
            ("accent", "#cba6f7"), ("accent-ink", "#11111b"), ("link", "#89b4fa"), ("accent-2", "#f5c2e7"),
            ("neutral", "#7f849c"), ("selection", "#585b70"),
            ("up", "#a6e3a1"), ("down", "#f38ba8"), ("warn", "#f9e2af"), ("info", "#89dceb"), ("code", "#fab387"),
            ("chart-2", "#fab387"), ("chart-3", "#a6e3a1"), ("chart-4", "#89b4fa"), ("chart-5", "#f5c2e7"),
            ("chart-6", "#94e2d5"), ("chart-7", "#f9e2af"), ("chart-8", "#f38ba8"),
            ("shadow", "rgba(0, 0, 0, .5)")),
        ThemeVariant.Of(
            ("ink", "#dce0e8"), ("panel", "#eff1f5"), ("panel-2", "#e6e9ef"), ("edge", "#ccd0da"),
            ("text", "#4c4f69"), ("muted", "#5c5f77"), ("placeholder", "#8c8fa1"),
            ("accent", "#8839ef"), ("accent-ink", "#eff1f5"), ("link", "#1e66f5"), ("accent-2", "#ea76cb"),
            ("neutral", "#6c6f85"), ("selection", "#acb0be"),
            ("up", "#40a02b"), ("down", "#d20f39"), ("warn", "#df8e1d"), ("info", "#209fb5"), ("code", "#fe640b"),
            ("chart-2", "#fe640b"), ("chart-3", "#40a02b"), ("chart-4", "#1e66f5"), ("chart-5", "#ea76cb"),
            ("chart-6", "#179299"), ("chart-7", "#df8e1d"), ("chart-8", "#d20f39"),
            ("shadow", "rgba(76, 79, 105, .16)")),
        "Soothing pastels: Mocha after dark, Latte by day.", BuiltIn: true);

    /// <summary>Tokyo Night by enkia: Tokyo Night for dark, Tokyo Night Light for light.</summary>
    public static readonly Theme TokyoNight = new("tokyo-night", "Tokyo Night", "enkia",
        ThemeVariant.Of(
            ("ink", "#16161e"), ("panel", "#1a1b26"), ("panel-2", "#292e42"), ("edge", "#3b4261"),
            ("text", "#c0caf5"), ("muted", "#a9b1d6"), ("placeholder", "#565f89"),
            ("accent", "#7aa2f7"), ("accent-ink", "#16161e"), ("link", "#7aa2f7"), ("accent-2", "#bb9af7"),
            ("neutral", "#737aa2"), ("selection", "#283457"),
            ("up", "#9ece6a"), ("down", "#f7768e"), ("warn", "#e0af68"), ("info", "#7dcfff"), ("code", "#ff9e64"),
            ("chart-2", "#ff9e64"), ("chart-3", "#9ece6a"), ("chart-4", "#bb9af7"), ("chart-5", "#7dcfff"),
            ("chart-6", "#e0af68"), ("chart-7", "#f7768e"), ("chart-8", "#1abc9c"),
            ("shadow", "rgba(0, 0, 0, .5)")),
        ThemeVariant.Of(
            ("ink", "#cbccd1"), ("panel", "#d5d6db"), ("panel-2", "#e1e2e7"), ("edge", "#b4b5b9"),
            ("text", "#343b58"), ("muted", "#4c505e"), ("placeholder", "#7a7d8a"),
            ("accent", "#34548a"), ("accent-ink", "#e6e7ed"), ("link", "#34548a"), ("accent-2", "#5a4a78"),
            ("neutral", "#4c505e"), ("selection", "#b6bbd3"),
            ("up", "#485e30"), ("down", "#8c4351"), ("warn", "#8f5e15"), ("info", "#0f4b6e"), ("code", "#965027"),
            ("chart-2", "#965027"), ("chart-3", "#485e30"), ("chart-4", "#5a4a78"), ("chart-5", "#0f4b6e"),
            ("chart-6", "#8f5e15"), ("chart-7", "#8c4351"), ("chart-8", "#33635c"),
            ("shadow", "rgba(52, 59, 88, .16)")),
        "Night-time city lights, and Tokyo Night Light by day.", BuiltIn: true);

    /// <summary>Gruvbox by Pavel Pertsev: the hard-contrast dark and light ends.</summary>
    public static readonly Theme Gruvbox = new("gruvbox", "Gruvbox", "Pavel Pertsev",
        ThemeVariant.Of(
            ("ink", "#1d2021"), ("panel", "#282828"), ("panel-2", "#3c3836"), ("edge", "#504945"),
            ("text", "#ebdbb2"), ("muted", "#bdae93"), ("placeholder", "#7c6f64"),
            ("accent", "#fabd2f"), ("accent-ink", "#1d2021"), ("link", "#83a598"), ("accent-2", "#d3869b"),
            ("neutral", "#928374"), ("selection", "#504945"),
            ("up", "#b8bb26"), ("down", "#fb4934"), ("warn", "#fe8019"), ("info", "#83a598"), ("code", "#8ec07c"),
            ("chart-2", "#fe8019"), ("chart-3", "#b8bb26"), ("chart-4", "#d3869b"), ("chart-5", "#83a598"),
            ("chart-6", "#8ec07c"), ("chart-7", "#fb4934"), ("chart-8", "#928374"),
            ("shadow", "rgba(0, 0, 0, .55)")),
        ThemeVariant.Of(
            ("ink", "#f2e5bc"), ("panel", "#fbf1c7"), ("panel-2", "#f2e5bc"), ("edge", "#d5c4a1"),
            ("text", "#3c3836"), ("muted", "#665c54"), ("placeholder", "#928374"),
            ("accent", "#b57614"), ("accent-ink", "#fbf1c7"), ("link", "#076678"), ("accent-2", "#8f3f71"),
            ("neutral", "#7c6f64"), ("selection", "#d5c4a1"),
            ("up", "#79740e"), ("down", "#9d0006"), ("warn", "#af3a03"), ("info", "#076678"), ("code", "#427b58"),
            ("chart-2", "#af3a03"), ("chart-3", "#79740e"), ("chart-4", "#8f3f71"), ("chart-5", "#076678"),
            ("chart-6", "#427b58"), ("chart-7", "#9d0006"), ("chart-8", "#928374"),
            ("shadow", "rgba(60, 56, 54, .18)")),
        "Retro groove: warm browns, yellows and greens.", BuiltIn: true);

    /// <summary>Solarized by Ethan Schoonover. The one in-between surface shade is a blend of base03 and base02.</summary>
    public static readonly Theme Solarized = new("solarized", "Solarized", "Ethan Schoonover",
        ThemeVariant.Of(
            ("ink", "#002b36"), ("panel", "#073642"), ("panel-2", "#0e4150"), ("edge", "#2a5560"),
            ("text", "#eee8d5"), ("muted", "#93a1a1"), ("placeholder", "#657b83"),
            ("accent", "#268bd2"), ("accent-ink", "#fdf6e3"), ("link", "#2aa198"), ("accent-2", "#6c71c4"),
            ("neutral", "#657b83"), ("selection", "#2a5560"),
            ("up", "#859900"), ("down", "#dc322f"), ("warn", "#b58900"), ("info", "#2aa198"), ("code", "#cb4b16"),
            ("chart-2", "#b58900"), ("chart-3", "#859900"), ("chart-4", "#d33682"), ("chart-5", "#cb4b16"),
            ("chart-6", "#2aa198"), ("chart-7", "#6c71c4"), ("chart-8", "#dc322f"),
            ("shadow", "rgba(0, 0, 0, .5)")),
        ThemeVariant.Of(
            ("ink", "#eee8d5"), ("panel", "#fdf6e3"), ("panel-2", "#eee8d5"), ("edge", "#d9d2c0"),
            ("text", "#073642"), ("muted", "#586e75"), ("placeholder", "#93a1a1"),
            ("accent", "#268bd2"), ("accent-ink", "#fdf6e3"), ("link", "#1f74b0"), ("accent-2", "#6c71c4"),
            ("neutral", "#657b83"), ("selection", "#e3dcc6"),
            ("up", "#6b7a00"), ("down", "#dc322f"), ("warn", "#8f6c00"), ("info", "#1f8079"), ("code", "#cb4b16"),
            ("chart-2", "#b58900"), ("chart-3", "#859900"), ("chart-4", "#d33682"), ("chart-5", "#cb4b16"),
            ("chart-6", "#2aa198"), ("chart-7", "#6c71c4"), ("chart-8", "#dc322f"),
            ("shadow", "rgba(7, 54, 66, .16)")),
        "Precision colours for machines and people.", BuiltIn: true);

    /// <summary>GitHub's Primer palette: dark default and light default.</summary>
    public static readonly Theme GitHub = new("github", "GitHub", "GitHub (Primer)",
        ThemeVariant.Of(
            ("ink", "#010409"), ("panel", "#0d1117"), ("panel-2", "#161b22"), ("edge", "#30363d"),
            ("text", "#e6edf3"), ("muted", "#8d96a0"), ("placeholder", "#6e7681"),
            ("accent", "#2f81f7"), ("accent-ink", "#ffffff"), ("link", "#4493f8"), ("accent-2", "#a371f7"),
            ("neutral", "#6e7681"), ("selection", "#264f78"),
            ("up", "#3fb950"), ("down", "#f85149"), ("warn", "#d29922"), ("info", "#58a6ff"), ("code", "#ffa657"),
            ("chart-2", "#d29922"), ("chart-3", "#3fb950"), ("chart-4", "#a371f7"), ("chart-5", "#ffa657"),
            ("chart-6", "#39c5cf"), ("chart-7", "#f778ba"), ("chart-8", "#f85149"),
            ("shadow", "rgba(1, 4, 9, .8)")),
        ThemeVariant.Of(
            ("ink", "#f6f8fa"), ("panel", "#ffffff"), ("panel-2", "#f6f8fa"), ("edge", "#d0d7de"),
            ("text", "#1f2328"), ("muted", "#656d76"), ("placeholder", "#6e7781"),
            ("accent", "#0969da"), ("accent-ink", "#ffffff"), ("link", "#0969da"), ("accent-2", "#8250df"),
            ("neutral", "#6e7781"), ("selection", "#b6e3ff"),
            ("up", "#1a7f37"), ("down", "#d1242f"), ("warn", "#9a6700"), ("info", "#0969da"), ("code", "#953800"),
            ("chart-2", "#bf8700"), ("chart-3", "#1a7f37"), ("chart-4", "#8250df"), ("chart-5", "#bc4c00"),
            ("chart-6", "#1b7c83"), ("chart-7", "#bf3989"), ("chart-8", "#cf222e"),
            ("shadow", "rgba(31, 35, 40, .15)")),
        "The colours of github.com.", BuiltIn: true);

    /// <summary>Atom's One Dark. Dark only, as it was published.</summary>
    public static readonly Theme OneDark = new("one-dark", "One Dark", "Atom",
        ThemeVariant.Of(
            ("ink", "#21252b"), ("panel", "#282c34"), ("panel-2", "#2c313c"), ("edge", "#3e4451"),
            ("text", "#d7dae0"), ("muted", "#abb2bf"), ("placeholder", "#5c6370"),
            ("accent", "#61afef"), ("accent-ink", "#21252b"), ("link", "#61afef"), ("accent-2", "#c678dd"),
            ("neutral", "#5c6370"), ("selection", "#3e4451"),
            ("up", "#98c379"), ("down", "#e06c75"), ("warn", "#e5c07b"), ("info", "#56b6c2"), ("code", "#d19a66"),
            ("chart-2", "#e5c07b"), ("chart-3", "#98c379"), ("chart-4", "#c678dd"), ("chart-5", "#d19a66"),
            ("chart-6", "#56b6c2"), ("chart-7", "#e06c75"), ("chart-8", "#be5046"),
            ("shadow", "rgba(0, 0, 0, .5)")),
        null,
        "Atom's classic: soft blues and greens on grey.", BuiltIn: true);

    /// <summary>
    /// Maximum legibility. Every text colour clears WCAG AAA (7:1) on every surface, the
    /// borders are full-strength, and the focus ring is a colour nothing else on the page uses.
    /// </summary>
    public static readonly Theme HighContrast = new("high-contrast", "High contrast", "LabbyTwo",
        ThemeVariant.Of(
            ("ink", "#000000"), ("panel", "#000000"), ("panel-2", "#1a1a1a"), ("edge", "#ffffff"),
            ("input-bg", "#000000"), ("input-border", "#ffffff"), ("tooltip-bg", "#000000"),
            ("text", "#ffffff"), ("muted", "#e6e6e6"), ("placeholder", "#c2c2c2"),
            ("accent", "#ffff00"), ("accent-ink", "#000000"), ("link", "#ffff00"), ("accent-2", "#ff9eff"),
            ("focus", "#00ffff"), ("neutral", "#d9d9d9"), ("selection", "#0000aa"),
            ("up", "#5cff5c"), ("down", "#ff8a8a"), ("warn", "#ffc400"), ("info", "#7fd4ff"), ("code", "#ffd27f"),
            ("chart-2", "#00ffff"), ("chart-3", "#5cff5c"), ("chart-4", "#ff9eff"), ("chart-5", "#ffc400"),
            ("chart-6", "#7fd4ff"), ("chart-7", "#ff8a8a"), ("chart-8", "#ffffff"),
            ("shadow", "rgba(0, 0, 0, 1)"), ("overlay", "rgba(0, 0, 0, .8)")),
        ThemeVariant.Of(
            ("ink", "#ffffff"), ("panel", "#ffffff"), ("panel-2", "#ededed"), ("edge", "#000000"),
            ("input-bg", "#ffffff"), ("input-border", "#000000"), ("tooltip-bg", "#ffffff"),
            ("text", "#000000"), ("muted", "#1f1f1f"), ("placeholder", "#4a4a4a"),
            ("accent", "#00007a"), ("accent-ink", "#ffffff"), ("link", "#00007a"), ("accent-2", "#6a0080"),
            ("focus", "#c80000"), ("neutral", "#2b2b2b"), ("selection", "#ffff00"),
            ("up", "#005c00"), ("down", "#9e0000"), ("warn", "#6b4000"), ("info", "#004a70"), ("code", "#5c2600"),
            ("chart-2", "#9e0000"), ("chart-3", "#005c00"), ("chart-4", "#6a0080"), ("chart-5", "#6b4000"),
            ("chart-6", "#004a70"), ("chart-7", "#5c2600"), ("chart-8", "#000000"),
            ("shadow", "rgba(0, 0, 0, .35)"), ("overlay", "rgba(0, 0, 0, .7)")),
        "Black and white, full-strength borders, AAA text.", BuiltIn: true);

    /// <summary>In gallery order: LabbyTwo's own first, then the rest alphabetically, High Contrast last.</summary>
    public static readonly IReadOnlyList<Theme> All =
    [
        LabbyTwo, Slate, Black,
        Catppuccin, Dracula, GitHub, Gruvbox, Nord, OneDark, Solarized, TokyoNight,
        HighContrast,
    ];

    public static Theme? Find(string? id) => All.FirstOrDefault(t => t.Id == id);
}
