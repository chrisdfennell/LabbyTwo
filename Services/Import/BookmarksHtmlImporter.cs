using System.Net;
using System.Text.RegularExpressions;
using LabbyTwo.Core;

namespace LabbyTwo.Services.Import;

/// <summary>
/// The Netscape bookmark file every browser exports — Chrome, Firefox, Edge, Safari — and
/// what Heimdall, Flame and half the other dashboards offer as their way out. One format
/// covers more sources than any dashboard-specific importer.
/// </summary>
public sealed partial class BookmarksHtmlImporter : IDashboardImporter
{
    public string Key => "bookmarks-html";
    public string DisplayName => "Browser bookmarks";
    public string Icon => "🔖";
    public string Description => "A bookmarks.html exported from any browser. Folders become cards.";
    public IReadOnlyList<string> Extensions => [".html", ".htm"];

    public bool CanHandle(ImportSource source) =>
        Extensions.Contains(source.Extension) &&
        source.Text.Contains("NETSCAPE-Bookmark-file", StringComparison.OrdinalIgnoreCase);

    public ImportPlan Read(ImportSource source)
    {
        var html = source.Text;
        var groups = new List<(string Name, List<LinkRow> Rows)> { ("Bookmarks", []) };
        var current = groups[0];

        // The format nests <DL> blocks, but the flat order of <H3> and <A> tags is enough:
        // every link belongs to the folder heading most recently seen, which is how a
        // browser displays them anyway.
        foreach (Match match in Entry().Matches(html))
        {
            if (match.Groups["folder"].Success)
            {
                var name = Clean(match.Groups["folder"].Value);
                if (name.Length == 0)
                    continue;
                current = (name, []);
                groups.Add(current);
                continue;
            }

            var url = WebUtility.HtmlDecode(match.Groups["href"].Value).Trim();

            // Browsers export javascript: bookmarklets and place: internal pages alongside
            // real links, and neither is something a dashboard should show.
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                continue;

            var title = Clean(match.Groups["title"].Value);
            current.Rows.Add(new LinkRow("", title.Length > 0 ? title : url, url));
        }

        var populated = groups.Where(g => g.Rows.Count > 0).ToList();
        if (populated.Count == 0)
            throw new FormatException("No bookmarks were found in that file.");

        var tab = new ImportedTab("Bookmarks", "🔖");
        foreach (var (name, rows) in populated)
        {
            tab.Widgets.Add(new ImportedWidget(
                "links", name, 3,
                new SettingsBag { ["links"] = LinkRow.Serialize(rows) }));
        }

        var plan = new ImportPlan { Tabs = { tab } };
        plan.Notes.Add($"{populated.Sum(g => g.Rows.Count)} bookmarks in {populated.Count} folders.");
        if (populated.Count > 12)
            plan.Notes.Add("That is a lot of cards for one tab — you may want to delete some after importing.");
        return plan;
    }

    private static string Clean(string raw) =>
        WebUtility.HtmlDecode(Tags().Replace(raw, "")).Trim();

    [GeneratedRegex(
        """<h3[^>]*>(?<folder>.*?)</h3>|<a\b[^>]*\bhref\s*=\s*"(?<href>[^"]*)"[^>]*>(?<title>.*?)</a>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Entry();

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex Tags();
}
