using LabbyTwo.Core;

namespace LabbyTwo.Components.Widgets;

// Every widget the app ships. Each is a descriptor plus a component — adding one means
// adding a class here and a .razor file, with no change to the tab page, the picker, or
// the editor, all of which are written against IWidgetType.

public sealed class ServiceTileWidget : IWidgetType
{
    public string Type => "service-tile";
    public string DisplayName => "Service tile";
    public string Icon => "🟢";
    public string Description => "Up/down status, response time and a one-hour sparkline for any connection.";
    // Empty means "any provider" would be wrong here — a tile needs something probed,
    // and every provider is probed, so it lists them all via the registry at bind time.
    public IReadOnlyList<string> ProviderTypes => AnyProvider.Types;
    public int DefaultWidth => 3;
    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("show_sparkline", "Show sparkline", FieldKind.Bool, Default: "true"),
        new("show_uptime", "Show 24h uptime", FieldKind.Bool, Default: "true"),
    ];
    public Type Component => typeof(ServiceTile);
}

public sealed class MetricWidget : IWidgetType
{
    public string Type => "metric";
    public string DisplayName => "Metric";
    public string Icon => "🔢";
    public string Description => "One number from a connection's latest probe, big and legible.";
    public IReadOnlyList<string> ProviderTypes => AnyProvider.Types;
    public int DefaultWidth => 3;
    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("metric", "Metric", FieldKind.Metric, "cpu_percent", Required: true,
            Help: "Pick from what this connection reports, or type any metric name."),
        new("suffix", "Unit override", FieldKind.Text, Help: "Optional. Blank uses the metric's usual unit."),
        new("decimals", "Decimal places", FieldKind.Number,
            Help: "Blank uses whatever the metric normally shows."),
        new("show_sparkline", "Show sparkline", FieldKind.Bool, Default: "true"),
        new("show_connection", "Show the connection's name", FieldKind.Bool, Default: "true",
            Help: "Turn off when several tiles on the page all read the same thing."),
    ];
    public Type Component => typeof(MetricTile);
}

public sealed class ChartWidget : IWidgetType
{
    public string Type => "chart";
    public string DisplayName => "Chart";
    public string Icon => "📈";
    public string Description => "Any recorded metric plotted over time.";
    public IReadOnlyList<string> ProviderTypes => AnyProvider.Types;
    public int DefaultWidth => 6;
    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("metric", "Metric", FieldKind.Metric, "latency_ms", Required: true),
        new("compare", "Compare with", FieldKind.Metric,
            Help: "Optional second line on the same axis — temperature against feels-like, wind against gust."),
        new("hours", "Window (hours)", FieldKind.Number, Default: "24"),
    ];
    public Type Component => typeof(MetricChart);
}

public sealed class LinksWidget : IWidgetType
{
    public string Type => "links";
    public string DisplayName => "Bookmarks";
    public string Icon => "🔗";
    public string Description => "A list of links with site icons. No connection, no health checks — just shortcuts.";
    public int DefaultWidth => 3;
    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("links", "Links", FieldKind.LinkList),
        new("new_tab", "Open in a new tab", FieldKind.Bool, Default: "true"),
        new("favicons", "Fetch site icons", FieldKind.Bool, Default: "true",
            Help: "Fetched by the LabbyTwo server and cached, so LAN-only services get an icon on a phone too. An emoji you set yourself always wins."),
    ];
    public Type Component => typeof(LinksCard);
}

public sealed class SearchWidget : IWidgetType
{
    public string Type => "search";
    public string DisplayName => "Search";
    public string Icon => "🔍";
    public string Description => "A search box. Submits straight to the engine, so nothing you type touches LabbyTwo.";
    public int DefaultWidth => 6;
    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("engine", "Engine", FieldKind.Select, Default: "duckduckgo", Options: SearchEngine.Options),
        new("custom_url", "Custom search URL", FieldKind.Url, "http://searx.lan/search?q=",
            Help: "Only used when the engine is “Custom”. Paste the URL your engine uses; the query parameter is read from it."),
        new("placeholder", "Placeholder", FieldKind.Text, Help: "Blank names the engine."),
        new("new_tab", "Open results in a new tab", FieldKind.Bool, Default: "false"),
        new("autofocus", "Focus on page load", FieldKind.Bool, Default: "false",
            Help: "Handy on a browser home page. Use it on one widget only."),
    ];
    public Type Component => typeof(SearchCard);
}

public sealed class GreetingWidget : IWidgetType
{
    public string Type => "greeting";
    public string DisplayName => "Greeting";
    public string Icon => "👋";
    public string Description => "Good morning / afternoon / evening, and today's date.";
    public int DefaultWidth => 6;
    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("name", "Your name", FieldKind.Text, Help: "Optional — blank just says “Good morning”."),
        new("show_date", "Show the date", FieldKind.Bool, Default: "true"),
    ];
    public Type Component => typeof(GreetingCard);
}

public sealed class MarkdownWidget : IWidgetType
{
    public string Type => "markdown";
    public string DisplayName => "Text / Markdown";
    public string Icon => "📝";
    public string Description => "A note, a runbook snippet, a reminder. Markdown is rendered.";
    public int DefaultWidth => 4;
    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("content", "Content", FieldKind.Markdown, "## Notes\n\n- Anything you like"),
    ];
    public Type Component => typeof(MarkdownCard);
}

public sealed class IframeWidget : IWidgetType
{
    public string Type => "iframe";
    public string DisplayName => "Embedded page";
    public string Icon => "🖼️";
    public string Description => "Any page inside a card. Loaded by your browser, so the URL must resolve from your devices.";
    public int DefaultWidth => 6;
    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "URL", FieldKind.Url, "http://192.168.1.50:3000", Required: true),
        new("height", "Height (px)", FieldKind.Number, Default: "320"),
    ];
    public Type Component => typeof(IframeCard);
}

public sealed class ClockWidget : IWidgetType
{
    public string Type => "clock";
    public string DisplayName => "Clock";
    public string Icon => "🕐";
    public string Description => "Local date and time.";
    public int DefaultWidth => 3;
    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("show_date", "Show the date", FieldKind.Bool, Default: "true"),
        new("seconds", "Show seconds", FieldKind.Bool, Default: "false"),
    ];
    public Type Component => typeof(ClockCard);
}

public sealed class StatusSummaryWidget : IWidgetType
{
    public string Type => "status-summary";
    public string DisplayName => "Status summary";
    public string Icon => "📊";
    public string Description => "How many connections are up, down, or still being checked.";
    public int DefaultWidth => 3;
    public Type Component => typeof(StatusSummary);
}

public sealed class ActiveAlertsWidget : IWidgetType
{
    public string Type => "active-alerts";
    public string DisplayName => "Active alerts";
    public string Icon => "🚨";
    public string Description => "Threshold rules that are currently breached, and by how much.";
    public int DefaultWidth => 4;
    public Type Component => typeof(ActiveAlerts);
}

public sealed class WeatherWidget : IWidgetType
{
    public string Type => "weather";
    public string DisplayName => "Weather station";
    public string Icon => "🌤️";
    public string Description => "Current readings from an Ambient Weather station.";
    public IReadOnlyList<string> ProviderTypes => ["ambient"];
    public int DefaultWidth => 4;
    public IReadOnlyList<FieldSpec> Fields => [WeatherUnits.Field];
    public Type Component => typeof(WeatherCard);
}

public sealed class WeatherSummaryWidget : IWidgetType
{
    public string Type => "weather-summary";
    public string DisplayName => "Weather — today's extremes";
    public string Icon => "🌅";
    public string Description => "High and low, peak gust, rain, max UV and peak solar, plus sunrise and sunset.";
    public IReadOnlyList<string> ProviderTypes => ["ambient"];
    public int DefaultWidth => 12;
    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("hours", "Window (hours)", FieldKind.Number, Default: "24"),
        new("latitude", "Latitude", FieldKind.Text, "39.7392",
            Help: "For sunrise and sunset, which are computed rather than fetched. Leave blank to omit them."),
        new("longitude", "Longitude", FieldKind.Text, "-104.9903"),
    ];
    public Type Component => typeof(WeatherSummary);
}

public sealed class ReadingsTableWidget : IWidgetType
{
    public string Type => "readings-table";
    public string DisplayName => "Readings table";
    public string Icon => "📋";
    public string Description => "The raw recorded numbers for any connection, as a table you can fold away.";
    public IReadOnlyList<string> ProviderTypes => AnyProvider.Types;
    public int DefaultWidth => 12;
    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("metrics", "Metrics", FieldKind.Text,
            Help: "Comma-separated. Blank shows whatever this connection records."),
        new("hours", "Window (hours)", FieldKind.Number, Default: "24"),
        new("limit", "Rows", FieldKind.Number, Default: "240"),
        new("open", "Start expanded", FieldKind.Bool, Default: "false"),
    ];
    public Type Component => typeof(ReadingsTable);
}

public sealed class RadarWidget : IWidgetType
{
    public string Type => "radar";
    public string DisplayName => "Weather radar";
    public string Icon => "📡";
    public string Description => "A rain radar picture from one of several sources, or any image or map URL of your own.";
    public int DefaultWidth => 6;
    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("source", "Source", FieldKind.Select, Default: "rainviewer", Options: RadarSource.Options,
            Help: "Loaded by your browser, not by LabbyTwo — so the source sees your IP address and, for map sources, the coordinates below."),
        new("latitude", "Latitude", FieldKind.Text, "51.5072",
            Help: "Decimal degrees. Ignored by the whole-country and custom sources."),
        new("longitude", "Longitude", FieldKind.Text, "-0.1276"),
        new("zoom", "Zoom", FieldKind.Number, Default: "7",
            Help: "1 is the whole world, 15 is a few streets. Around 7 covers a region."),
        new("station", "Radar site", FieldKind.Text, "KTLX",
            Help: "Only for the NOAA per-site source — the four-letter code of your nearest radar, from radar.weather.gov."),
        new("custom_url", "Custom URL", FieldKind.Url, "https://example.invalid/radar.gif",
            Help: "Only for the custom sources. May contain {lat}, {lon}, {zoom} and {station}."),
        new("height", "Height (px)", FieldKind.Number, Default: "320"),
        new("refresh_minutes", "Refresh every (minutes)", FieldKind.Number, Default: "5",
            Help: "Image sources only. An embedded map animates itself."),
        new("attribution", "Credit the source", FieldKind.Bool, Default: "true"),
    ];
    public Type Component => typeof(RadarCard);
}

public sealed class WindCompassWidget : IWidgetType
{
    public string Type => "wind-compass";
    public string DisplayName => "Wind";
    public string Icon => "🧭";
    public string Description => "A compass dial showing wind speed, gust and which way it is coming from.";
    public IReadOnlyList<string> ProviderTypes => ["ambient"];
    public int DefaultWidth => 3;
    public IReadOnlyList<FieldSpec> Fields => [WeatherUnits.Field];
    public Type Component => typeof(WindCompass);
}

public sealed class WeatherTodayWidget : IWidgetType
{
    public string Type => "weather-today";
    public string DisplayName => "Weather — today";
    public string Icon => "🌡️";
    public string Description => "High, low and where the temperature sits between them, plus rain and the last hour's trend.";
    public IReadOnlyList<string> ProviderTypes => ["ambient"];
    public int DefaultWidth => 4;
    public IReadOnlyList<FieldSpec> Fields =>
    [
        WeatherUnits.Field,
        new("hours", "Window (hours)", FieldKind.Number, Default: "24"),
    ];
    public Type Component => typeof(WeatherToday);
}

public sealed class IndoorOutdoorWidget : IWidgetType
{
    public string Type => "indoor-outdoor";
    public string DisplayName => "Inside vs outside";
    public string Icon => "🪟";
    public string Description => "Both temperatures side by side, and whether opening the windows would help.";
    public IReadOnlyList<string> ProviderTypes => ["ambient"];
    public int DefaultWidth => 4;
    public IReadOnlyList<FieldSpec> Fields =>
    [
        WeatherUnits.Field,
        new("comfort_c", "Comfortable indoor temperature (°C)", FieldKind.Number, Default: "21",
            Help: "What the advice is measured against."),
    ];
    public Type Component => typeof(IndoorOutdoor);
}

/// <summary>The units toggle is identical on every weather card, so it is declared once.</summary>
public static class WeatherUnits
{
    public static FieldSpec Field => new("units", "Units", FieldKind.Select, Default: "imperial", Options:
    [
        new SelectOption("imperial", "°F, mph, inHg"),
        new SelectOption("metric", "°C, km/h, hPa"),
    ]);
}

public sealed class NasWidget : IWidgetType
{
    public string Type => "nas";
    public string DisplayName => "NAS overview";
    public string Icon => "💾";
    public string Description => "Model, uptime, CPU, memory and volume usage from a QNAP.";
    public IReadOnlyList<string> ProviderTypes => ["qnap"];
    public int DefaultWidth => 6;
    public Type Component => typeof(NasCard);
}

public sealed class PlexNowPlayingWidget : IWidgetType
{
    public string Type => "plex-now-playing";
    public string DisplayName => "Plex — now playing";
    public string Icon => "🎬";
    public string Description => "Who is watching what, and how far in.";
    public IReadOnlyList<string> ProviderTypes => ["plex"];
    public int DefaultWidth => 6;
    public Type Component => typeof(PlexNowPlaying);
}

public sealed class JellyfinNowPlayingWidget : IWidgetType
{
    public string Type => "jellyfin-now-playing";
    public string DisplayName => "Jellyfin — now playing";
    public string Icon => "🎞️";
    public string Description => "Who is watching what, how far in, and whether it is being transcoded.";
    public IReadOnlyList<string> ProviderTypes => ["jellyfin"];
    public int DefaultWidth => 6;
    public Type Component => typeof(JellyfinNowPlaying);
}

public sealed class ArrQueueWidget : IWidgetType
{
    public string Type => "arr-queue";
    public string DisplayName => "Download queue";
    public string Icon => "⬇️";
    public string Description => "What is downloading right now, with progress.";
    // Prowlarr is deliberately absent: it manages indexers and has no queue.
    public IReadOnlyList<string> ProviderTypes => ["sonarr", "radarr", "lidarr", "readarr"];
    public int DefaultWidth => 6;
    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("limit", "Rows", FieldKind.Number, Default: "6"),
    ];
    public Type Component => typeof(ArrQueue);
}

public sealed class ContainerListWidget : IWidgetType
{
    public string Type => "containers";
    public string DisplayName => "Containers";
    public string Icon => "🐳";
    public string Description => "Which containers are running on a Docker host, and for how long.";
    public IReadOnlyList<string> ProviderTypes => ["docker"];
    public int DefaultWidth => 4;
    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("limit", "Rows", FieldKind.Number, Default: "10"),
        new("running_only", "Only show running containers", FieldKind.Bool, Default: "false"),
    ];
    public Type Component => typeof(ContainerList);
}

public sealed class GitSummaryWidget : IWidgetType
{
    public string Type => "git-summary";
    public string DisplayName => "Git — summary";
    public string Icon => "🐙";
    public string Description => "Repository, pull request and issue counts for a Git server, and what was touched last.";
    public IReadOnlyList<string> ProviderTypes => ["mypersonalgit"];
    public int DefaultWidth => 3;
    public Type Component => typeof(GitSummary);
}

public sealed class GitRepoListWidget : IWidgetType
{
    public string Type => "git-repos";
    public string DisplayName => "Git — repositories";
    public string Icon => "📚";
    public string Description => "Repositories with their commit, pull request and issue counts, most recently updated first.";
    public IReadOnlyList<string> ProviderTypes => ["mypersonalgit"];
    public int DefaultWidth => 6;
    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("limit", "Rows", FieldKind.Number, Default: "8"),
    ];
    public Type Component => typeof(GitRepoList);
}

public sealed class GitActivityWidget : IWidgetType
{
    public string Type => "git-activity";
    public string DisplayName => "Git — open work";
    public string Icon => "🔀";
    public string Description => "Open pull requests or open issues across every repository on a Git server.";
    public IReadOnlyList<string> ProviderTypes => ["mypersonalgit"];
    public int DefaultWidth => 4;
    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("show", "Show", FieldKind.Select, Default: "pulls", Options:
        [
            new("pulls", "Open pull requests"),
            new("issues", "Open issues"),
        ]),
        new("limit", "Rows", FieldKind.Number, Default: "8"),
    ];
    public Type Component => typeof(GitActivity);
}

/// <summary>
/// Marker for widgets that work with any probed connection. Kept as a single list so the
/// generic widgets don't have to name every provider — the picker treats an entry of "*"
/// as "anything".
/// </summary>
public static class AnyProvider
{
    public const string Wildcard = "*";
    public static IReadOnlyList<string> Types => [Wildcard];
}
