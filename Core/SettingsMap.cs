namespace LabbyTwo.Core;

/// <summary>A page of its own inside a settings section — Tabs inside Connections &amp; tabs.</summary>
public sealed record SettingsPage(string Title, string Icon, string Route);

/// <summary>One setting, or one card of them, that the settings search can find.</summary>
/// <param name="Url">Where it is: a section route, usually with the card's anchor.</param>
/// <param name="Keywords">Other words somebody might type for it.</param>
public sealed record SettingsEntry(string Name, string Url, string Keywords = "");

/// <summary>A section of the settings hub, with its own route.</summary>
/// <param name="Route">The section's own page, relative.</param>
/// <param name="Pages">
/// Pages that belong to the section but have their own address — kept, because links to them
/// are all over the app, the README and people's bookmarks.
/// </param>
public sealed record SettingsSection(
    string Key,
    string Title,
    string Icon,
    string Summary,
    string Route,
    IReadOnlyList<SettingsPage> Pages,
    IReadOnlyList<SettingsEntry> Entries,
    string Keywords = "")
{
    /// <summary>Every address that counts as being in this section, its own first.</summary>
    public IEnumerable<string> Routes => Pages.Select(p => p.Route).Prepend(Route);
}

/// <summary>A settings search result.</summary>
/// <param name="Section">The section it is in, for the line under it. Empty for a section itself.</param>
public sealed record SettingsHit(string Title, string Icon, string Url, string Section, SettingsHitKind Kind);

public enum SettingsHitKind { Section, Page, Setting }

/// <summary>
/// Every settings section, the pages inside each and the settings worth finding by name.
/// Static on purpose: the hub's sub-navigation, its search and the command palette are all
/// drawn from this without asking the database anything — the menu that waited on the
/// database is the reason v1.10.0 was slow.
///
/// Settings used to be one page of twenty cards plus nine pages of their own, four of them in
/// the sidebar and the rest reachable only from a link somewhere else. Now each card lives in
/// one section, every page that already had an address keeps it, and the old long page is the
/// hub that lists them.
/// </summary>
public static class SettingsMap
{
    public const string HubRoute = "settings";

    public static readonly IReadOnlyList<SettingsSection> Sections =
    [
        new("general", "General", "⚙️", "Where you are, and which pages the menu shows.",
            "settings/general", [],
            [
                new("Where you are", "settings/general#location", "location place town city postcode coordinates latitude longitude weather forecast radar air quality sunrise"),
                new("Menu", "settings/general#menu", "sidebar hide show pages navigation power backups logs unused"),
                new("Dashboard name", "settings/appearance#brand", "brand title name"),
                new("Units", "settings/appearance#unit-temp", "celsius fahrenheit temperature wind pressure rain metric imperial"),
                new("Login", "settings/security#login", "password username sign in auth"),
            ],
            "name units location login menu"),

        new("appearance", "Appearance", "🎨", "Themes, day and night, each screen's look, the background and your own CSS.",
            "settings/appearance", [],
            [
                new("Themes", "settings/appearance#themes", "theme gallery colours dark light nord solarized customise import"),
                new("Day and night", "settings/appearance#day-night", "light dark auto follow the sun schedule"),
                new("Per screen", "settings/appearance#screen-themes", "wall phone family theme of its own"),
                new("Accent colour", "settings/appearance#accent", "colour color accent"),
                new("Shape and type", "settings/appearance#radius", "corners radius density surface wide screens text size font"),
                new("Font", "settings/appearance#font", "typeface web font upload"),
                new("Dashboard name", "settings/appearance#brand", "brand title name"),
                new("Units", "settings/appearance#unit-temp", "celsius fahrenheit temperature wind pressure rain"),
                new("Background", "settings/appearance#background", "backdrop picture image gradient frosted glass blur"),
                new("Wall mode", "settings/appearance#wall-mode", "kiosk rotate seconds tabs tv display"),
                new("Custom CSS", "settings/appearance#custom-css", "stylesheet css advanced safe mode"),
            ],
            "look theme style colour"),

        new("connections", "Connections & tabs", "🔌", "What LabbyTwo watches, the tabs it shows them on, and how they depend on each other.",
            "settings/connections",
            [
                new("Tabs", "🗂️", "settings/tabs"),
                new("Dependency map", "🗺️", "settings/connections/map"),
            ],
            [
                new("Connections", "settings/connections", "services providers probe add edit docker"),
                new("Find services on the network", "settings/connections?discover=true", "discover scan detect"),
                new("Alert channels", "settings/connections", "notification channel ntfy discord telegram email gotify pushover"),
                new("Tabs", "settings/tabs", "dashboards pages order export share"),
                new("Tab templates", "settings/tabs", "template starter"),
                new("Dependency map", "settings/connections/map", "depends on diagram topology"),
            ],
            "services dashboards"),

        new("alerts", "Alerts", "🚨", "Rules, quiet hours, mute windows, escalation and self-healing.",
            "settings/alerts", [],
            [
                new("Alert rules", "settings/alerts", "threshold metric rule disk cpu temperature forecast unusual"),
                new("Quiet hours", "settings/alerts#quiet-hours", "night silence do not disturb"),
                new("Mute windows", "settings/alerts#mute-windows", "maintenance window recurring silence"),
                new("Escalation", "settings/alerts#escalation", "repeat still firing remind"),
                new("Self-healing", "settings/alerts#self-healing", "remediation restart automatically fix"),
            ],
            "alarm warn notify"),

        new("notifications", "Notifications", "📣", "Where alerts are sent, browser push, and the weekly and monthly reports.",
            "settings/notifications",
            [
                new("Browser push", "📳", "settings/push"),
            ],
            [
                new("Alert channels", "settings/notifications#channels", "ntfy discord telegram email gotify pushover"),
                new("Browser push", "settings/push", "push notification device phone browser"),
                new("Weekly summary", "settings/notifications#weekly-summary", "week report digest email"),
                new("Monthly report", "settings/notifications#monthly-report", "month report uptime"),
            ],
            "channels push report summary"),

        new("automation", "Automation", "⏰", "Scheduled actions and safe container updates.",
            "settings/automation",
            [
                new("Scheduled actions", "⏰", "settings/scheduled"),
            ],
            [
                new("Scheduled actions", "settings/scheduled", "cron timer restart nightly timetable"),
                new("Safe updates", "settings/automation#safe-updates", "roll back rollback watch container update"),
                new("Self-healing", "settings/alerts#self-healing", "remediation restart automatically"),
            ],
            "schedule automatic"),

        new("updates", "Updates", "⬆️", "Updating LabbyTwo and the containers it watches, and restarting it.",
            "settings/updates", [],
            [
                new("Check for a LabbyTwo update", "settings/updates#labbytwo", "version release github new"),
                new("Update now", "settings/updates#labbytwo", "watchtower self update upgrade"),
                new("Restart LabbyTwo", "settings/updates#restart", "reboot reload"),
                new("Container updates", "settings/updates#containers", "image registry docker hub newer tag schedule check"),
            ],
            "upgrade version"),

        new("integrations", "Integrations", "🔗", "Home Assistant, the outside alarm, the family page and the public status page.",
            "settings/integrations", [],
            [
                new("Home Assistant (MQTT)", "settings/integrations#home-assistant", "mqtt broker mosquitto entities sensors"),
                new("Outside alarm", "settings/integrations#outside-alarm", "healthchecks dead man switch ping uptime kuma"),
                new("Family status page", "settings/integrations#family", "family household report problem"),
                new("Public status page", "settings/integrations#public-status", "share link public uptime"),
            ],
            "home assistant family public"),

        new("data", "Data & storage", "💾", "Backups, off-site copies, imports and exports, and what takes the space.",
            "settings/data",
            [
                new("Storage", "💽", "settings/storage"),
                new("Import a dashboard", "📥", "settings/import"),
            ],
            [
                new("Download the database", "settings/data#backup", "backup copy sqlite"),
                new("Export configuration", "settings/data#backup", "export json secrets"),
                new("Off-site copies", "settings/data#offsite", "s3 backblaze usb encrypted backup restore passphrase"),
                new("Import a tab or a card", "settings/data#import-share", "share file shared"),
                new("Import configuration", "settings/data#import-config", "restore json paste"),
                new("Import a dashboard", "settings/import", "homer homepage heimdall dashy migrate"),
                new("Storage and history retention", "settings/storage", "database size compact vacuum retention history space"),
            ],
            "backup export import retention"),

        new("security", "Security", "🔒", "Passwords left in containers, and the login.",
            "settings/security", [],
            [
                new("Secrets in containers", "settings/security#secrets", "password token api key environment plain text hygiene"),
                new("Login", "settings/security#login", "password username sign in auth"),
            ],
            "password secret login"),

        new("system", "System", "🩺", "This install, its health, extensions and plugins.",
            "settings/system",
            [
                new("LabbyTwo health", "🩺", "settings/health"),
            ],
            [
                new("This install", "settings/system#install", "version probe interval database size"),
                new("LabbyTwo health", "settings/health", "monitor jobs dns process host pressure"),
                new("Tell me when LabbyTwo is struggling", "settings/health", "self watch self-watch"),
                new("Background jobs", "settings/health", "jobs scheduler"),
                new("Installed extensions", "settings/system#extensions", "providers widgets tab kinds importers endpoints"),
                new("Plugins", "settings/system#plugins", "plugin dll install browse update"),
            ],
            "about version health plugins"),
    ];

    /// <summary>
    /// Anchors that were cards on the old single Settings page, and where each card lives now.
    /// The fragment never reaches the server, so the hub sends the browser on once it can read it.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> MovedAnchors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["family"] = "settings/integrations#family",
        ["home-assistant"] = "settings/integrations#home-assistant",
        ["outside-alarm"] = "settings/integrations#outside-alarm",
        ["public-status"] = "settings/integrations#public-status",
        ["location"] = "settings/general#location",
        ["updates"] = "settings/updates#labbytwo",
        ["plugins"] = "settings/system#plugins",
        ["backup"] = "settings/data#backup",
        ["offsite"] = "settings/data#offsite",
        ["secrets"] = "settings/security#secrets",
    };

    /// <summary>Where an old <c>settings#anchor</c> link should go now, or null when it is not one of the moved cards.</summary>
    public static string? MovedAnchor(string? fragment) =>
        fragment is { Length: > 0 } && MovedAnchors.TryGetValue(fragment.TrimStart('#'), out var target) ? target : null;

    /// <summary>The section a relative path is in, or null for the hub itself and anything outside settings.</summary>
    public static SettingsSection? SectionFor(string relativePath)
    {
        var path = Normalise(relativePath);
        return Sections
            .SelectMany(s => s.Routes.Select(r => (Section: s, Route: r)))
            .Where(x => path == x.Route || path.StartsWith(x.Route + "/", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Route.Length)
            .Select(x => x.Section)
            .FirstOrDefault();
    }

    /// <summary>A relative path with no query, fragment or slashes at the ends, lower-cased.</summary>
    public static string Normalise(string relativePath)
    {
        var path = relativePath;
        var cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0)
            path = path[..cut];
        return path.Trim('/').ToLowerInvariant();
    }

    /// <summary>
    /// Sections, their pages, the settings in them and the menu's own pages, matching every
    /// word typed against the name and its keywords. Names that start with the first word come
    /// first; then names that contain it; then the ones only found by a keyword.
    /// </summary>
    public static IReadOnlyList<SettingsHit> Search(string query, int limit = 20)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
            return [];

        var hits = new List<(SettingsHit Hit, int Rank)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Consider(SettingsHit hit, string keywords)
        {
            var haystack = $"{hit.Title} {hit.Section} {keywords}";
            if (!words.All(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase)))
                return;
            // The same setting is listed under two sections now and then — the name in General
            // and in Appearance — and one result for it is enough.
            if (!seen.Add(hit.Title + "|" + hit.Url))
                return;
            var rank = hit.Title.StartsWith(words[0], StringComparison.OrdinalIgnoreCase) ? 0
                : hit.Title.Contains(words[0], StringComparison.OrdinalIgnoreCase) ? 1
                : 2;
            hits.Add((hit, rank * 10 + (int)hit.Kind));
        }

        foreach (var section in Sections)
        {
            Consider(new SettingsHit(section.Title, section.Icon, section.Route, "", SettingsHitKind.Section),
                section.Summary + " " + section.Keywords);
            foreach (var page in section.Pages)
                Consider(new SettingsHit(page.Title, page.Icon, page.Route, section.Title, SettingsHitKind.Page), "");
            foreach (var entry in section.Entries)
                Consider(new SettingsHit(entry.Name, section.Icon, entry.Url, section.Title, SettingsHitKind.Setting), entry.Keywords);
        }

        // The menu's pages too: a page hidden from the sidebar has to be findable somewhere.
        foreach (var group in NavMap.Groups)
        foreach (var item in group.Items)
            Consider(new SettingsHit(item.Label, item.Icon, item.Href, group.Label, SettingsHitKind.Page), item.Hint + " " + item.Keywords);

        return [.. hits.OrderBy(h => h.Rank).Take(limit).Select(h => h.Hit)];
    }
}
