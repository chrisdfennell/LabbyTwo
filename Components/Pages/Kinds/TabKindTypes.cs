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

public sealed class WeatherStationTabKind : ITabKind
{
    public string Kind => "weather-station";
    public string DisplayName => "Weather station";
    public string Icon => "🌦️";
    public string Description => "A whole page for one Ambient Weather station — readings, radar, today's extremes and history.";
    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("connection", "Weather station", FieldKind.Connection,
            Help: "Leave blank to use the only Ambient Weather connection you have.")
            { ProviderFilter = "ambient" },
        new("latitude", "Latitude", FieldKind.Text, "39.7392",
            Help: "Decimal degrees. Used for the radar and for sunrise and sunset, which are computed here rather than fetched."),
        new("longitude", "Longitude", FieldKind.Text, "-104.9903"),
        new("radar", "Show radar", FieldKind.Bool, Default: "true"),
        new("radar_source", "Radar source", FieldKind.Select, Default: "rainviewer", Options: RadarSource.Options),
        new("radar_zoom", "Radar zoom", FieldKind.Number, Default: "7"),
    ];
    public Type Component => typeof(WeatherStationTab);
}

public sealed class GitTabKind : ITabKind
{
    public string Kind => "git";
    public string DisplayName => "Git server";
    public string Icon => "🐙";
    public string Description => "A whole page for one MyPersonalGit server — counts, repositories, open pull requests and open issues.";
    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("connection", "Git server", FieldKind.Connection,
            Help: "Leave blank to use the only MyPersonalGit connection you have.")
            { ProviderFilter = "mypersonalgit" },
        new("subtitle", "Subtitle", FieldKind.Text),
    ];
    public Type Component => typeof(GitTab);
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
