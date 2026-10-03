namespace LabbyTwo.Core;

/// <summary>One page in the sidebar, outside the tabs.</summary>
/// <param name="Key">Stable name, stored when the page is hidden from the menu. Never shown.</param>
/// <param name="Href">Relative, like every other link in the app, so a path base keeps working.</param>
/// <param name="Plain">
/// Drawn as a plain link rather than a NavLink: the wall and the phone view have layouts of
/// their own, so while you are on them this sidebar is not there to be highlighted.
/// </param>
/// <param name="Keywords">Other words somebody might search for it by.</param>
public sealed record NavItem(string Key, string Label, string Icon, string Href, string Hint, bool Plain = false, string Keywords = "");

/// <summary>A collapsible group of pages in the sidebar.</summary>
public sealed record NavGroup(string Key, string Label, IReadOnlyList<NavItem> Items);

/// <summary>
/// The sidebar below the tabs. Sixty features arrived in a few days and each brought a link of
/// its own, until the bottom of the menu was a column of fourteen; these are the same pages in
/// three groups, with Settings as the one link to everything that configures something.
/// Shared by the menu, the command palette and the settings search, so a page is filed in one
/// place and found under the same name everywhere.
/// </summary>
public static class NavMap
{
    /// <summary>The app setting holding the keys of the pages hidden from the menu, comma separated.</summary>
    public const string HiddenKey = "nav_hidden";

    public static readonly NavItem Changes = new("changes", "What changed", "🕘", "changes",
        "Everything that changed in the lab, newest first", Keywords: "history timeline feed reports");
    public static readonly NavItem Incidents = new("incidents", "Incidents", "🔥", "incidents",
        "Outages grouped into incidents, with a timeline and probable causes", Keywords: "outage postmortem");
    public static readonly NavItem Logs = new("logs", "Logs", "📜", "logs",
        "Search the containers' logs", Keywords: "container log search grep");
    public static readonly NavItem Health = new("health", "LabbyTwo health", "🩺", "settings/health",
        "Whether LabbyTwo itself is working — the monitor, jobs, plugins, DNS and database", Keywords: "system self status jobs");
    public static readonly NavItem Power = new("power", "Power", "⚡", "power",
        "Power draw and what the electricity costs", Keywords: "electricity energy cost watts ups");
    public static readonly NavItem Backups = new("backups", "Backups", "🛟", "backups",
        "What is backed up, when, and when a restore was last tested", Keywords: "restore test drill");
    public static readonly NavItem Map = new("map", "Dependency map", "🗺️", "settings/connections/map",
        "What depends on what", Keywords: "diagram topology graph");
    public static readonly NavItem Phone = new("phone", "Phone view", "📱", PhoneView.Route,
        "Only what needs attention, with the fixes one tap away — for a phone", Plain: true, Keywords: "mobile");
    public static readonly NavItem Wall = new("wall", "Wall mode", "📺", "wall",
        "Rotate through the tabs full screen, for a wall display", Plain: true, Keywords: "kiosk tv display rotate");
    public static readonly NavItem Scheduled = new("scheduled", "Scheduled actions", "⏰", "settings/scheduled",
        "Actions that run on a timetable — restart this every night", Keywords: "cron timer automation");

    public static readonly IReadOnlyList<NavGroup> Groups =
    [
        new("monitor", "Monitor", [Changes, Incidents, Logs, Health]),
        new("lab", "Lab", [Power, Backups, Map]),
        new("views", "Views & actions", [Phone, Wall, Scheduled]),
    ];

    /// <summary>Every page in the groups, in menu order.</summary>
    public static IEnumerable<NavItem> Items => Groups.SelectMany(g => g.Items);

    /// <summary>The keys of the pages hidden from the menu. Unknown keys are dropped.</summary>
    public static IReadOnlySet<string> Hidden(SettingsBag settings)
    {
        var known = Items.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        return settings.Get(HiddenKey)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(known.Contains)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The stored form of a set of hidden pages: known keys only, in menu order.</summary>
    public static string Store(IEnumerable<string> hidden)
    {
        var set = hidden.ToHashSet(StringComparer.Ordinal);
        return string.Join(',', Items.Select(i => i.Key).Where(set.Contains));
    }

    /// <summary>The groups as the menu draws them: hidden pages left out, and a group left with none left out too.</summary>
    public static IReadOnlyList<NavGroup> Visible(IReadOnlySet<string> hidden) =>
    [
        .. Groups
            .Select(g => g with { Items = [.. g.Items.Where(i => !hidden.Contains(i.Key))] })
            .Where(g => g.Items.Count > 0)
    ];
}
