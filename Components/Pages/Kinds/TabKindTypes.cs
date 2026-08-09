using LabbyTwo.Core;

namespace LabbyTwo.Components.Pages.Kinds;

// Every tab kind the app ships. Each is a descriptor plus a component; the tab page is
// written against ITabKind alone, so adding a kind here changes nothing else — not the
// router, not the nav, not the tab editor's form.

public sealed class GridTabKind : ITabKind
{
    public string Kind => TabKinds.Grid;
    public string DisplayName => "Dashboard";
    public string Icon => "🏠";
    public string Description => "A grid of widgets — status tiles, charts, bookmarks, notes, embedded pages.";
    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("subtitle", "Subtitle", FieldKind.Text, Help: "Optional line under the heading."),
    ];
    public Type Component => typeof(GridTab);
}

public sealed class EmbedTabKind : ITabKind
{
    public string Kind => TabKinds.Embed;
    public string DisplayName => "Embedded page";
    public string Icon => "🖼️";
    public string Description => "A whole page that is another app's web UI, full height.";
    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "URL", FieldKind.Url, "http://192.168.1.50:9000", Required: true,
            Help: "Loaded by your browser, so it must resolve from your devices — and an https LabbyTwo cannot frame a plain-http page."),
        new("subtitle", "Subtitle", FieldKind.Text),
        new("height", "Height (px)", FieldKind.Number, Help: "Blank fills the window."),
    ];
    public Type Component => typeof(EmbedTab);
}

public sealed class NotesTabKind : ITabKind
{
    public string Kind => TabKinds.Notes;
    public string DisplayName => "Notes";
    public string Icon => "📝";
    public string Description => "Markdown notes and runbooks with a live preview.";
    public Type Component => typeof(NotesTab);
}

public sealed class StatusTabKind : ITabKind
{
    public string Kind => TabKinds.Status;
    public string DisplayName => "Status page";
    public string Icon => "📶";
    public string Description => "Uptime for everything monitored — percentages, a daily bar strip, and an outage log.";
    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("days", "Days shown", FieldKind.Number, Default: "30"),
    ];
    public Type Component => typeof(StatusTab);
}
