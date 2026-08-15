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
    public string Description =>
        "A whole page for the weather where you are — warnings, forecast, radar, your own station and history.";
    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("connection", "Weather station", FieldKind.Connection,
            Help: "Optional. Blank uses the only Ambient Weather connection you have; with none, the " +
                  "page shows the forecast and warnings on their own.")
            { ProviderFilter = "ambient" },

        new("forecast", "Forecast", FieldKind.Connection,
            Help: "Optional. Blank uses the only forecast connection you have, and shows nothing if " +
                  "you have none — the station reports what is happening, this what is about to.")
            { ProviderFilter = "forecast" },

        new("warnings", "Weather warnings", FieldKind.Connection,
            Help: "Optional. Watches and warnings appear at the top of the page, above everything else.")
            { ProviderFilter = "nws" },

        new("air", "Air quality", FieldKind.Connection,
            Help: "Optional.") { ProviderFilter = "air-quality" },

        new("hourly_hours", "Hours in the hourly strip", FieldKind.Number, Default: "12",
            Help: "Zero hides it.") { Advanced = true },

        new("latitude", "Latitude", FieldKind.Text,
            Help: "Only for the radar and for sunrise and sunset, which are computed here rather than " +
                  "fetched. Blank uses the location set in Settings.") { Advanced = true },
        new("longitude", "Longitude", FieldKind.Text) { Advanced = true },
        new("radar", "Show radar", FieldKind.Bool, Default: "true"),
        new("radar_source", "Radar source", FieldKind.Select, Default: "rainviewer", Options: RadarSource.Options),
        new("radar_zoom", "Radar zoom", FieldKind.Number, Default: "7"),
    ];
    public Type Component => typeof(WeatherStationTab);
}

/// <summary>
/// The media stack on one page. Unlike the weather tab, this names no connections: a media
/// stack is many-of-each — Sonarr *and* Radarr *and* Lidarr — so eighteen dropdowns would
/// be a worse form than no form. It gathers by the category each provider already declares
/// instead, which also means a plugin calling itself Media joins without this file changing.
/// </summary>
public sealed class MediaTabKind : ITabKind
{
    public string Kind => "media";
    public string DisplayName => "Media stack";
    public string Icon => "🍿";
    public string Description =>
        "A whole page for the *arrs and everything around them — what is playing, what is due, " +
        "what is downloading, and how it all trends.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("subtitle", "Subtitle", FieldKind.Text, Help: "Optional. Blank shows a live summary instead."),
        new("calendar_days", "Days of calendar", FieldKind.Number, Default: "7",
            Help: "How far ahead to read the *arr calendars. Zero hides the section."),
        new("show_now_playing", "Show what is playing", FieldKind.Bool, Default: "true"),
        new("show_pipeline", "Show the queue and download clients", FieldKind.Bool, Default: "true"),
        new("show_library", "Show library counts", FieldKind.Bool, Default: "true"),
        new("show_graphs", "Show the graphs", FieldKind.Bool, Default: "true"),

        new("upcoming_limit", "Most releases to list", FieldKind.Number, Default: "12") { Advanced = true },
        new("queue_limit", "Most queue items to list", FieldKind.Number, Default: "8") { Advanced = true },
        new("growth_days", "Days on the growth graphs", FieldKind.Number, Default: "30",
            Help: "A library moves over months, so it gets a longer window than the activity graphs.")
            { Advanced = true },
    ];

    public Type Component => typeof(MediaTab);
}

public sealed class GitTabKind : ITabKind
{
    public string Kind => "git";
    public string DisplayName => "Git server";
    public string Icon => "🐙";
    public string Description => "A whole page for one Git server — counts, repositories, open pull requests and open issues. Works with any of them.";
    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("connection", "Git server", FieldKind.Connection,
            Help: "Any Git server — MyPersonalGit, Gitea, Forgejo or GitLab. Leave blank to use the only one you have.")
            { ProviderFilter = GitForges.Any },
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
