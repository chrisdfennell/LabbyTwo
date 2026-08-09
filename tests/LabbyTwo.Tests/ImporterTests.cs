using System.Text;
using LabbyTwo.Core;
using LabbyTwo.Services.Import;
using Microsoft.Data.Sqlite;

namespace LabbyTwo.Tests;

/// <summary>
/// Importers are pure — a file in, a plan out — which is the whole reason they were
/// written that way: the interesting cases can be checked without a database.
/// </summary>
public class ImporterTests
{
    private static ImportSource Source(string name, string text) =>
        new(name, Encoding.UTF8.GetBytes(text));

    private static List<LinkRow> LinksOf(ImportedWidget widget) =>
        LinkRow.Parse(widget.Values.Get("links"));

    // ---------- Homer ----------

    private const string HomerConfig = """
        ---
        title: "Home lab"
        subtitle: "Everything at once"
        services:
          - name: "Media"
            icon: "fas fa-film"
            items:
              - name: "Plex"
                url: "http://192.168.1.50:32400"
              - name: "Jellyfin"
                url: "http://192.168.1.51:8096"
          - name: "Infra"
            items:
              - name: "Proxmox"
                url: "https://192.168.1.2:8006"
        links:
          - name: "Docs"
            url: "https://example.invalid/docs"
        """;

    [Fact]
    public void HomerConfigBecomesOneTabOfBookmarkCards()
    {
        var plan = new HomerImporter().Read(Source("config.yml", HomerConfig));

        var tab = Assert.Single(plan.Tabs);
        Assert.Equal("Home lab", tab.Name);
        Assert.Equal("Everything at once", tab.Values.Get("subtitle"));

        // Two service groups plus the top-level links row.
        Assert.Equal(3, tab.Widgets.Count);
        Assert.All(tab.Widgets, w => Assert.Equal("links", w.Type));

        var media = tab.Widgets.First(w => w.Title == "Media");
        Assert.Equal(["Plex", "Jellyfin"], LinksOf(media).Select(l => l.Name));
        Assert.Equal("http://192.168.1.50:32400", LinksOf(media)[0].Url);
    }

    [Fact]
    public void HomerIsDetectedAndHomepageIsNot()
    {
        var homer = new HomerImporter();
        Assert.True(homer.CanHandle(Source("config.yml", HomerConfig)));
        Assert.False(homer.CanHandle(Source("services.yaml", HomepageServices)));
        Assert.False(homer.CanHandle(Source("config.txt", HomerConfig)));
    }

    [Fact]
    public void HomerRejectsAFileWithNothingInIt()
    {
        var error = Assert.Throws<FormatException>(() =>
            new HomerImporter().Read(Source("config.yml", "title: Empty\nservices: []\n")));
        Assert.Contains("No services or links", error.Message);
    }

    [Fact]
    public void UnreadableYamlGivesAMessageRatherThanAParserDump()
    {
        var error = Assert.Throws<FormatException>(() =>
            new HomerImporter().Read(Source("config.yml", "title: [unclosed\n  bad: : :")));
        Assert.Contains("not readable YAML", error.Message);
    }

    // ---------- Homepage ----------

    private const string HomepageServices = """
        - Media:
            - Sonarr:
                href: http://192.168.1.60:8989
                widget:
                  type: sonarr
                  url: http://192.168.1.60:8989
                  key: abc123
            - Overseerr:
                href: http://192.168.1.61:5055
        - Infra:
            - Router:
                href: http://192.168.1.1
        """;

    [Fact]
    public void HomepageWidgetsBecomeMonitoredConnections()
    {
        var plan = new HomepageImporter().Read(Source("services.yaml", HomepageServices));

        var sonarr = Assert.Single(plan.Connections);
        Assert.Equal("sonarr", sonarr.Provider);
        Assert.Equal("Sonarr", sonarr.Name);
        Assert.Equal("http://192.168.1.60:8989", sonarr.Values.Get("url"));
        Assert.Equal("abc123", sonarr.Values.Get("api_key"));

        var tab = Assert.Single(plan.Tabs);
        var tile = Assert.Single(tab.Widgets, w => w.Type == "service-tile");
        Assert.Equal(sonarr.Ref, tile.ConnectionRef);
    }

    [Fact]
    public void HomepageEntriesWithoutAWidgetStayBookmarks()
    {
        var plan = new HomepageImporter().Read(Source("services.yaml", HomepageServices));
        var tab = plan.Tabs[0];

        var media = tab.Widgets.First(w => w is { Type: "links", Title: "Media" });
        Assert.Equal(["Overseerr"], LinksOf(media).Select(l => l.Name));

        var infra = tab.Widgets.First(w => w is { Type: "links", Title: "Infra" });
        Assert.Equal(["Router"], LinksOf(infra).Select(l => l.Name));
    }

    [Fact]
    public void HomepageBookmarksFileWithItsExtraNestingIsRead()
    {
        const string bookmarks = """
            - Developer:
                - GitHub:
                    - abbr: GH
                      href: https://github.com/
                - Docs:
                    - abbr: DO
                      href: https://learn.microsoft.com/
            """;

        var plan = new HomepageImporter().Read(Source("bookmarks.yaml", bookmarks));

        var tab = Assert.Single(plan.Tabs);
        Assert.Equal("Bookmarks", tab.Name);
        var card = Assert.Single(tab.Widgets);
        Assert.Equal(["GitHub", "Docs"], LinksOf(card).Select(l => l.Name));
    }

    [Fact]
    public void AHomepageWidgetForSomethingUnsupportedStillGetsAnHttpCheck()
    {
        const string yaml = """
            - Tools:
                - Some App:
                    href: http://192.168.1.70:9000
                    widget:
                      type: somethingnobodyhas
                      url: http://192.168.1.70:9000
            """;

        var plan = new HomepageImporter().Read(Source("services.yaml", yaml));

        var connection = Assert.Single(plan.Connections);
        Assert.Equal("http", connection.Provider);
        Assert.Equal("http://192.168.1.70:9000", connection.Values.Get("url"));
    }

    // ---------- Browser bookmarks ----------

    private const string BookmarksHtml = """
        <!DOCTYPE NETSCAPE-Bookmark-file-1>
        <META HTTP-EQUIV="Content-Type" CONTENT="text/html; charset=UTF-8">
        <TITLE>Bookmarks</TITLE>
        <H1>Bookmarks</H1>
        <DL><p>
            <DT><H3>Home lab</H3>
            <DL><p>
                <DT><A HREF="http://192.168.1.2:8006" ADD_DATE="1700000000">Proxmox &amp; friends</A>
                <DT><A HREF="javascript:void(0)">A bookmarklet</A>
                <DT><A HREF="https://grafana.lan">Grafana</A>
            </DL><p>
            <DT><H3>Reading</H3>
            <DL><p>
                <DT><A HREF="https://example.invalid/blog">A blog</A>
            </DL><p>
        </DL><p>
        """;

    [Fact]
    public void BrowserBookmarkFoldersBecomeCards()
    {
        var plan = new BookmarksHtmlImporter().Read(Source("bookmarks.html", BookmarksHtml));

        var tab = Assert.Single(plan.Tabs);
        Assert.Equal(2, tab.Widgets.Count);

        var lab = tab.Widgets.First(w => w.Title == "Home lab");
        var links = LinksOf(lab);

        // The bookmarklet is dropped, and the entity is decoded.
        Assert.Equal(["Proxmox & friends", "Grafana"], links.Select(l => l.Name));
    }

    [Fact]
    public void OnlyANetscapeFileIsClaimed()
    {
        var importer = new BookmarksHtmlImporter();
        Assert.True(importer.CanHandle(Source("bookmarks.html", BookmarksHtml)));
        Assert.False(importer.CanHandle(Source("index.html", "<html><body>hello</body></html>")));
    }

    // ---------- Heimdall ----------

    private static byte[] BuildHeimdallDatabase()
    {
        var path = Path.Combine(Path.GetTempPath(), $"heimdall-test-{Guid.NewGuid():N}.sqlite");
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                connection.Open();
                var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE items (id INTEGER PRIMARY KEY, title TEXT, url TEXT, type INTEGER, deleted_at TEXT);
                    CREATE TABLE item_tag (item_id INTEGER, tag_id INTEGER);
                    INSERT INTO items VALUES (1, 'Media', '', 1, NULL);
                    INSERT INTO items VALUES (2, 'Plex', 'http://192.168.1.50:32400', 0, NULL);
                    INSERT INTO items VALUES (3, 'Radarr', 'http://192.168.1.60:7878', 0, NULL);
                    INSERT INTO items VALUES (4, 'Deleted thing', 'http://gone.invalid', 0, '2024-01-01');
                    INSERT INTO items VALUES (5, 'Loose link', 'http://192.168.1.9', 0, NULL);
                    INSERT INTO item_tag VALUES (2, 1);
                    INSERT INTO item_tag VALUES (3, 1);
                    """;
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            return File.ReadAllBytes(path);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void HeimdallItemsBecomeCardsGroupedByTag()
    {
        var plan = new HeimdallImporter().Read(new ImportSource("app.sqlite", BuildHeimdallDatabase()));

        var tab = Assert.Single(plan.Tabs);
        var media = tab.Widgets.First(w => w.Title == "Media");
        Assert.Equal(["Plex", "Radarr"], LinksOf(media).Select(l => l.Name));

        // An untagged item is not lost — it lands in a default group.
        var loose = tab.Widgets.First(w => w.Title == "Applications");
        Assert.Equal(["Loose link"], LinksOf(loose).Select(l => l.Name));

        // A soft-deleted row is not resurrected.
        Assert.DoesNotContain(tab.Widgets.SelectMany(LinksOf), l => l.Name == "Deleted thing");
    }

    [Fact]
    public void HeimdallDetectionUsesTheSqliteHeader()
    {
        var importer = new HeimdallImporter();
        Assert.True(importer.CanHandle(new ImportSource("app.sqlite", BuildHeimdallDatabase())));
        Assert.False(importer.CanHandle(Source("config.yml", HomerConfig)));
        Assert.False(importer.CanHandle(new ImportSource("tiny.db", [1, 2, 3])));
    }

    [Fact]
    public void ASqliteFileThatIsNotHeimdallSaysSo()
    {
        var path = Path.Combine(Path.GetTempPath(), $"other-{Guid.NewGuid():N}.sqlite");
        byte[] bytes;
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                connection.Open();
                var cmd = connection.CreateCommand();
                cmd.CommandText = "CREATE TABLE something_else (id INTEGER)";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            bytes = File.ReadAllBytes(path);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path))
                File.Delete(path);
        }

        var error = Assert.Throws<FormatException>(() =>
            new HeimdallImporter().Read(new ImportSource("other.sqlite", bytes)));
        Assert.Contains("no items table", error.Message);
    }
}
