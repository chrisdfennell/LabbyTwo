using LabbyTwo.Components.Widgets;
using LabbyTwo.Core;

namespace LabbyTwo.Components.Pages.Kinds;

// A page put together by hand: cards, whole other pages as sections, and headings and
// rules between them, each sized freely. The blocks are ordinary widget types, so they are
// found, edited, exported and duplicated by everything that already handles a card; the
// picker only offers them on a custom page (see PageBlocks.IsBlock), which is the one
// place their extra layout means anything.

public sealed class CustomTabKind : ITabKind
{
    public string Kind => TabKinds.Custom;
    public string DisplayName => "Custom page";
    public string Icon => "🧩";
    public string Description =>
        "Mix anything on one page — cards, whole pages like the weather or the status page as " +
        "sections, and headings between them, each as wide and as tall as you like.";
    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("subtitle", "Subtitle", FieldKind.Text, Help: "Optional line under the heading."),
    ];
    public Type Component => typeof(CustomTab);
}

public sealed class SectionBlockType : IWidgetType
{
    public string Type => PageBlocks.Section;
    public string DisplayName => "Section";
    public string Icon => "📄";
    public string Description => "Another kind of page — weather, status, notes, media — drawn in place on this one.";
    public int DefaultWidth => 12;

    // The page it shows brings its own fields; the editor asks PageBlocks.FieldsFor.
    public Type Component => typeof(SectionBlock);
}

public sealed class HeadingBlockType : IWidgetType
{
    public string Type => PageBlocks.Heading;
    public string DisplayName => "Heading";
    public string Icon => "🔠";
    public string Description => "A title for the blocks under it.";
    public int DefaultWidth => 12;
    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("text", "Text", FieldKind.Text, "Downstairs", Required: true),
        new("level", "Size", FieldKind.Select, Default: "2", Options:
        [
            new("2", "Large"),
            new("3", "Medium"),
            new("4", "Small"),
        ]),
        new("icon", "Icon", FieldKind.Icon, Help: "Optional. Shown before the text."),
        new("subtitle", "Line under it", FieldKind.Text, Help: "Optional."),
    ];
    public Type Component => typeof(HeadingBlock);
}

public sealed class DividerBlockType : IWidgetType
{
    public string Type => PageBlocks.Divider;
    public string DisplayName => "Divider";
    public string Icon => "➖";
    public string Description => "A thin line across the page, with a label in it if you like.";
    public int DefaultWidth => 12;
    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("label", "Label", FieldKind.Text, Help: "Optional. Blank draws a plain line."),
    ];
    public Type Component => typeof(DividerBlock);
}
