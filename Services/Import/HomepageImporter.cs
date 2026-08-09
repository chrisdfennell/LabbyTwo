using LabbyTwo.Core;
using YamlDotNet.RepresentationModel;

namespace LabbyTwo.Services.Import;

/// <summary>
/// gethomepage's <c>services.yaml</c> or <c>bookmarks.yaml</c>. Richer than Homer, so the
/// mapping goes further: a service with a <c>widget:</c> block naming an app LabbyTwo has
/// a provider for becomes a real monitored connection with a status tile, not just a link.
/// </summary>
public sealed class HomepageImporter : IDashboardImporter
{
    public string Key => "homepage";
    public string DisplayName => "Homepage (gethomepage)";
    public string Icon => "🧭";
    public string Description => "services.yaml or bookmarks.yaml — groups become cards, and known widgets become monitored connections.";
    public IReadOnlyList<string> Extensions => [".yml", ".yaml"];

    /// <summary>
    /// Homepage widget types that map onto a LabbyTwo provider, with the settings key the
    /// API key lands in. Anything not here still becomes a bookmark.
    /// </summary>
    private static readonly Dictionary<string, (string Provider, string? KeyField)> ProviderMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["sonarr"] = ("sonarr", "api_key"),
            ["radarr"] = ("radarr", "api_key"),
            ["plex"] = ("plex", "token"),
            ["pihole"] = ("pihole", "token"),
        };

    public bool CanHandle(ImportSource source)
    {
        if (!Extensions.Contains(source.Extension))
            return false;
        try
        {
            // Homepage's files are a top-level sequence of single-key group maps. Homer's
            // is a mapping with a "services" key, so the two never collide.
            var root = Yaml.Parse(source.Text);
            return root is YamlSequenceNode sequence
                   && sequence.Children.Count > 0
                   && sequence.Children.All(child => child is YamlMappingNode);
        }
        catch
        {
            return false;
        }
    }

    public ImportPlan Read(ImportSource source)
    {
        YamlNode? root;
        try
        {
            root = Yaml.Parse(source.Text);
        }
        catch (Exception ex)
        {
            throw new FormatException($"That is not readable YAML: {ex.GetBaseException().Message}");
        }

        var plan = new ImportPlan();
        var isBookmarks = source.FileName.Contains("bookmark", StringComparison.OrdinalIgnoreCase);
        var tab = new ImportedTab(isBookmarks ? "Bookmarks" : "Services", isBookmarks ? "🔖" : "🧭");

        foreach (var groupNode in root.AsList())
        {
            foreach (var (groupName, entries) in groupNode.Pairs())
            {
                var rows = new List<LinkRow>();

                foreach (var entryNode in entries.AsList())
                {
                    foreach (var (entryName, body) in entryNode.Pairs())
                    {
                        // bookmarks.yaml wraps each entry in an extra sequence; services.yaml
                        // does not. Unwrap so one loop reads both.
                        var detail = body is YamlSequenceNode { Children.Count: > 0 } wrapped
                            ? wrapped.Children[0]
                            : body;

                        var href = detail.Text("href", detail.Text("url"));
                        if (href.Length == 0)
                            continue;

                        if (TryConnection(entryName, detail, plan, tab, groupName))
                            continue;

                        rows.Add(new LinkRow("", entryName, href));
                    }
                }

                if (rows.Count > 0)
                {
                    tab.Widgets.Add(new ImportedWidget(
                        "links", groupName, 3,
                        new SettingsBag { ["links"] = LinkRow.Serialize(rows) }));
                }
            }
        }

        if (tab.Widgets.Count == 0)
            throw new FormatException("No groups with links were found in that file.");

        plan.Tabs.Add(tab);

        if (plan.Connections.Count > 0)
        {
            plan.Notes.Add(
                $"{plan.Connections.Count} service(s) became monitored connections. Homepage keeps API keys " +
                "in the same file, so check whether yours came across — anything missing needs re-entering " +
                "under Connections.");
        }

        plan.Notes.Add(
            "Homepage icons are names from its icon set, which do not carry over — LabbyTwo fetches " +
            "each site's own icon instead.");
        return plan;
    }

    /// <summary>
    /// Turns a Homepage <c>widget:</c> block into a connection plus a status tile when the
    /// app behind it is one LabbyTwo can talk to. Returns false when it is just a link.
    /// </summary>
    private static bool TryConnection(string name, YamlNode? detail, ImportPlan plan, ImportedTab tab, string group)
    {
        var widget = detail.Child("widget");
        var type = widget.Text("type");
        var url = widget.Text("url", detail.Text("href"));

        if (type.Length == 0 || url.Length == 0)
            return false;

        if (!ProviderMap.TryGetValue(type, out var mapping))
        {
            // A widget for something LabbyTwo has no provider for is still a service worth
            // watching, so it becomes a plain HTTP check rather than being lost.
            mapping = ("http", null);
        }

        var settings = new SettingsBag { ["url"] = url };
        if (mapping.KeyField is { } field && widget.Text("key") is { Length: > 0 } key)
            settings[field] = key;

        var reference = $"{group}/{name}".ToLowerInvariant();
        plan.Connections.Add(new ImportedConnection(reference, mapping.Provider, name, "", settings));
        tab.Widgets.Add(new ImportedWidget("service-tile", "", 3, null, reference));
        return true;
    }
}
