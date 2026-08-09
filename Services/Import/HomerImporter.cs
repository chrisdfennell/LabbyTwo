using LabbyTwo.Core;

namespace LabbyTwo.Services.Import;

/// <summary>
/// Homer's <c>config.yml</c>. Homer is a pure bookmark dashboard — no monitoring, no
/// credentials — so the mapping is clean: each service group becomes a bookmark card, and
/// the whole file becomes one tab.
/// </summary>
public sealed class HomerImporter : IDashboardImporter
{
    public string Key => "homer";
    public string DisplayName => "Homer";
    public string Icon => "🏡";
    public string Description => "Homer's config.yml — service groups become bookmark cards on one tab.";
    public IReadOnlyList<string> Extensions => [".yml", ".yaml"];

    public bool CanHandle(ImportSource source)
    {
        if (!Extensions.Contains(source.Extension))
            return false;
        try
        {
            var root = Yaml.Parse(source.Text);
            // "services" with "items" underneath is Homer's shape and nobody else's;
            // Homepage's services.yaml is a top-level sequence, not a mapping.
            return root.Child("services").AsList().Any(group => group.Child("items") is not null);
        }
        catch
        {
            return false;
        }
    }

    public ImportPlan Read(ImportSource source)
    {
        YamlDotNet.RepresentationModel.YamlNode? root;
        try
        {
            root = Yaml.Parse(source.Text);
        }
        catch (Exception ex)
        {
            throw new FormatException($"That is not readable YAML: {ex.GetBaseException().Message}");
        }

        var plan = new ImportPlan();
        var tab = new ImportedTab(
            root.Text("title", "Homer"),
            "🏡",
            TabKinds.Grid,
            new SettingsBag { ["subtitle"] = root.Text("subtitle") });

        foreach (var group in root.Child("services").AsList())
        {
            var rows = group.Child("items").AsList()
                .Select(item => new LinkRow("", item.Text("name", item.Text("url")), item.Text("url")))
                .Where(row => row.Url.Length > 0)
                .ToList();

            if (rows.Count == 0)
                continue;

            tab.Widgets.Add(new ImportedWidget(
                "links",
                group.Text("name", "Links"),
                4,
                new SettingsBag { ["links"] = LinkRow.Serialize(rows) }));
        }

        // Homer's top-level "links" are the small nav row rather than a card; they are
        // still bookmarks, so they get one of their own rather than being dropped.
        var navRows = root.Child("links").AsList()
            .Select(link => new LinkRow("", link.Text("name", link.Text("url")), link.Text("url")))
            .Where(row => row.Url.Length > 0)
            .ToList();

        if (navRows.Count > 0)
        {
            tab.Widgets.Add(new ImportedWidget(
                "links", "Links", 4,
                new SettingsBag { ["links"] = LinkRow.Serialize(navRows) }));
        }

        if (tab.Widgets.Count == 0)
            throw new FormatException("No services or links were found in that file.");

        plan.Tabs.Add(tab);
        plan.Notes.Add(
            "Homer icons are Font Awesome classes or image paths, which do not carry over — " +
            "LabbyTwo fetches each site's own icon instead.");
        plan.Notes.Add(
            "Homer does not monitor anything. To get status dots and uptime for these, add a " +
            "Web service connection for each and drop a service tile on the tab.");
        return plan;
    }
}
