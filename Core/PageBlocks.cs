namespace LabbyTwo.Core;

/// <summary>
/// The pieces a custom page is made of, and the arithmetic of laying them out.
///
/// A block is a widget row like any other — same table, same editor, same export — so a
/// custom page gets reordering, duplicating, undo and sharing for nothing. What makes it a
/// page rather than a dashboard is three extra widget types (a section, a heading and a
/// divider) and a height, all of which live here so the rules are in one place and can be
/// tested without a browser.
/// </summary>
public static class PageBlocks
{
    /// <summary>Another tab kind's whole page, drawn inline.</summary>
    public const string Section = "page-section";

    /// <summary>A line of large text, to title a group of blocks.</summary>
    public const string Heading = "page-heading";

    /// <summary>A thin rule across the page, optionally labelled.</summary>
    public const string Divider = "page-divider";

    /// <summary>
    /// Which kind a section shows. Dotted so it cannot meet a field of the kind it holds:
    /// a section's settings are that kind's settings, and field keys are plain snake_case.
    /// </summary>
    public const string KindKey = "section.kind";

    /// <summary>The tallest a block can be made, in row units.</summary>
    public const int MaxRows = 12;

    /// <summary>
    /// The widths the stepper walks through. The same ones a grid tab uses, because they
    /// are the ones that tile evenly into twelve columns.
    /// </summary>
    public static readonly IReadOnlyList<int> WidthSteps = [2, 3, 4, 6, 8, 12];

    public static bool IsBlock(string? type) => type is Section or Heading or Divider;

    /// <summary>
    /// Blocks drawn without a card round them. A heading in a box is a card with a title
    /// and nothing in it, and a section is a page that already draws its own panels.
    /// </summary>
    public static bool IsBare(string? type) => IsBlock(type);

    // ---- layout ------------------------------------------------------------------------

    public static int ClampWidth(int width) => Math.Clamp(width, 1, 12);

    /// <summary>Zero is "as tall as its content"; anything else is rows, up to <see cref="MaxRows"/>.</summary>
    public static int ClampRows(int rows) => Math.Clamp(rows, 0, MaxRows);

    /// <summary>One step wider or narrower, snapping an odd width onto the steps first.</summary>
    public static int StepWidth(int width, int step)
    {
        var index = -1;
        for (var i = 0; i < WidthSteps.Count; i++)
        {
            if (WidthSteps[i] >= width)
            {
                index = i;
                break;
            }
        }
        if (index < 0)
            index = WidthSteps.Count - 1;
        return WidthSteps[Math.Clamp(index + step, 0, WidthSteps.Count - 1)];
    }

    /// <summary>
    /// One row taller or shorter. Every row is a step here, unlike width, because a height
    /// has nothing it has to tile into — and shrinking past one row goes back to auto
    /// rather than stopping, so "fit the content" is reachable from the stepper too.
    /// </summary>
    public static int StepRows(int rows, int step) => ClampRows(ClampRows(rows) + step);

    /// <summary>
    /// The classes that place a block. Width stays a class, as on a grid tab, so the
    /// breakpoints can widen a narrow block. Height is a class that switches the rule on
    /// plus a custom property that says how many rows (see <see cref="Style"/>): one rule
    /// rather than twelve, and one the phone layout can switch off in one place.
    /// </summary>
    public static string Classes(Widget widget)
    {
        var classes = $"w-{ClampWidth(widget.Width)}";
        if (ClampRows(widget.Height) > 0)
            classes += " has-rows";
        if (IsBare(widget.Type))
            classes += " is-bare";
        return classes;
    }

    /// <summary>The inline style that carries a fixed height, or null for an auto one.</summary>
    public static string? Style(Widget widget) =>
        ClampRows(widget.Height) is > 0 and var rows ? $"--rows: {rows}" : null;

    /// <summary>How a block's size reads in the edit bar: "½ · auto", "⅓ · 4 rows".</summary>
    public static string Describe(Widget widget)
    {
        var width = ClampWidth(widget.Width) switch
        {
            12 => "full",
            8 => "⅔",
            6 => "½",
            4 => "⅓",
            3 => "¼",
            2 => "⅙",
            var other => $"{other}/12",
        };
        var rows = ClampRows(widget.Height);
        return $"{width} · {(rows == 0 ? "auto" : rows == 1 ? "1 row" : $"{rows} rows")}";
    }

    // ---- sections ----------------------------------------------------------------------

    /// <summary>What a section can show, and why not when it cannot.</summary>
    public enum SectionState
    {
        /// <summary>A kind is chosen, installed and allowed.</summary>
        Ready,

        /// <summary>Nothing chosen yet — a section fresh from the picker, or edited to blank.</summary>
        Unchosen,

        /// <summary>The kind it names is not installed: a plugin that has been removed.</summary>
        Missing,

        /// <summary>
        /// A kind a section may not hold — a custom page, which would draw itself inside
        /// itself for ever, or a dashboard, which is what the page around it already is.
        /// </summary>
        NotEmbeddable,
    }

    /// <summary>
    /// Whether a kind may appear as a section. A custom page may not, or a page could
    /// hold itself and draw until the circuit ran out of stack. A dashboard may not either:
    /// its cards belong to the tab they are on, so an embedded one would have nowhere to
    /// keep them, and the page around it is already a grid of cards to put them on.
    /// </summary>
    public static bool CanEmbed(string? kind) =>
        kind is { Length: > 0 }
        && !string.Equals(kind, TabKinds.Custom, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(kind, TabKinds.Grid, StringComparison.OrdinalIgnoreCase);

    /// <summary>The kinds offered for a section, in the order the tab picker shows them.</summary>
    public static IReadOnlyList<ITabKind> Embeddable(Registry registry) =>
        [.. registry.TabKinds.Where(kind => CanEmbed(kind.Kind))];

    public static string SectionKind(SettingsBag settings) => settings.Get(KindKey);

    public static (SectionState State, ITabKind? Kind) Resolve(Registry registry, Widget widget)
    {
        var key = SectionKind(widget.Settings);
        if (key.Length == 0)
            return (SectionState.Unchosen, null);
        if (!CanEmbed(key))
            return (SectionState.NotEmbeddable, null);
        return registry.TabKind(key) is { } kind
            ? (SectionState.Ready, kind)
            : (SectionState.Missing, null);
    }

    /// <summary>
    /// The tab a section hands to the kind it shows. Its id is the block's, so anything the
    /// kind keeps per tab — a notes section's notes, say — belongs to this block and not to
    /// the page, and two notes sections on one page are two sets of notes.
    /// </summary>
    public static Tab SectionTab(Widget widget, ITabKind kind)
    {
        var settings = widget.Settings.Clone();
        settings.Remove(KindKey);
        return new Tab
        {
            Id = widget.Id,
            Name = widget.Title is { Length: > 0 } title ? title : kind.DisplayName,
            Icon = "",
            Kind = kind.Kind,
            Settings = settings,
        };
    }

    /// <summary>
    /// Every field a block's settings hold. For most widgets that is the type's own list;
    /// a section's settings are the embedded kind's, so its fields come from that kind.
    /// Sharing needs this to find the connections in a section's settings — a weather
    /// section holds four — and turn them into names that mean something elsewhere.
    /// </summary>
    public static IReadOnlyList<FieldSpec> FieldsFor(Registry registry, string type, SettingsBag settings)
    {
        var own = registry.WidgetType(type)?.Fields ?? [];
        if (type != Section)
            return own;

        var key = SectionKind(settings);
        return CanEmbed(key) && registry.TabKind(key) is { } kind ? [.. own, .. kind.Fields] : own;
    }
}
