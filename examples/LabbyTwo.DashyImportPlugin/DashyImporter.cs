using LabbyTwo.Core;
using YamlDotNet.RepresentationModel;

namespace LabbyTwo.DashyImportPlugin;

/// <summary>
/// Dashy's <c>conf.yml</c>. Dashy is a bookmark dashboard with sections of items, which
/// maps cleanly onto one LabbyTwo tab with a bookmark card per section.
///
/// An importer is the easiest extension point to get right, because it is a pure function
/// from a file to an <see cref="ImportPlan"/>. It never touches the database — the host
/// shows the user the plan and only writes if they accept it — so it can be unit-tested
/// with a string and no running app.
/// </summary>
public sealed class DashyImporter : IDashboardImporter
{
    public string Key => "dashy";
    public string DisplayName => "Dashy";
    public string Icon => "🚀";
    public string Description => "Dashy's conf.yml — each section becomes a bookmark card on one tab.";
    public IReadOnlyList<string> Extensions => [".yml", ".yaml"];

    /// <summary>
    /// Cheap, and must not throw: this runs against every uploaded file when the user
    /// picks "detect automatically", including files meant for a different importer.
    /// </summary>
    public bool CanHandle(ImportSource source)
    {
        if (!Extensions.Contains(source.Extension))
            return false;

        try
        {
            // "sections" with "items" underneath is Dashy's shape. Homer nests its groups
            // under "services", and Homepage's services.yaml is a sequence at the top
            // level, so neither is claimed by accident.
            return Root(source.Text) is { } root
                   && Child(root, "sections") is YamlSequenceNode sections
                   && sections.Children.Any(section => Child(section, "items") is not null);
        }
        catch
        {
            return false;
        }
    }

    public ImportPlan Read(ImportSource source)
    {
        var root = Root(source.Text)
                   ?? throw new FormatException("That file is empty.");

        var pageInfo = Child(root, "pageInfo");
        var plan = new ImportPlan();
        var tab = new ImportedTab(
            Text(pageInfo, "title", "Dashy"),
            "🚀",
            TabKinds.Grid,
            new SettingsBag { ["subtitle"] = Text(pageInfo, "description") });

        var skipped = 0;

        foreach (var section in (Child(root, "sections") as YamlSequenceNode)?.Children ?? [])
        {
            var rows = new List<LinkRow>();

            foreach (var item in (Child(section, "items") as YamlSequenceNode)?.Children ?? [])
            {
                var url = Text(item, "url");
                if (url.Length == 0)
                {
                    // A Dashy item can be a widget rather than a link. There is nothing to
                    // turn that into, so count it and say so rather than dropping it
                    // silently — an import that quietly loses things is worse than one
                    // that admits to it.
                    skipped++;
                    continue;
                }

                rows.Add(new LinkRow("", Text(item, "title", url), url));
            }

            if (rows.Count == 0)
                continue;

            tab.Widgets.Add(new ImportedWidget(
                "links",
                Text(section, "name", "Links"),
                4,
                new SettingsBag { ["links"] = LinkRow.Serialize(rows) }));
        }

        if (tab.Widgets.Count == 0)
            throw new FormatException("No sections with links were found in that file.");

        plan.Tabs.Add(tab);

        plan.Notes.Add(
            "Dashy icons are Font Awesome classes, Simple Icons names or image paths, none of which " +
            "carry over — pick an emoji for each card afterwards, or leave them blank.");

        if (skipped > 0)
            plan.Notes.Add($"{skipped} item{(skipped == 1 ? "" : "s")} had no URL and were left out.");

        return plan;
    }

    // ---- the smallest YAML helpers this needs -----------------------------------------
    //
    // LabbyTwo's own Yaml helper is internal, so a plugin brings its own. That is the
    // general rule for extending from outside: the four interfaces and the types they
    // mention are the contract, and anything else in the app may change without notice.

    private static YamlNode? Root(string text)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        return stream.Documents.Count == 0 ? null : stream.Documents[0].RootNode;
    }

    private static YamlNode? Child(YamlNode? node, string key) =>
        node is YamlMappingNode map && map.Children.TryGetValue(new YamlScalarNode(key), out var value)
            ? value
            : null;

    private static string Text(YamlNode? node, string key, string fallback = "") =>
        Child(node, key) is YamlScalarNode { Value: { Length: > 0 } value } ? value : fallback;
}
