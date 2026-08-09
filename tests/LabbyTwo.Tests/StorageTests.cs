using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Services.Import;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace LabbyTwo.Tests;

/// <summary>
/// Everything that touches SQLite, against a real database in a temp directory. Faster
/// than mocking the store and it catches the schema and migration mistakes a mock never
/// would.
/// </summary>
public sealed class StorageTests : IDisposable
{
    private readonly string _directory;
    private readonly ServiceProvider _services;

    public StorageTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "labbytwo-tests-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_directory);

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        services.AddSingleton<IHostEnvironment>(new TestEnvironment(_directory));
        services.AddSingleton(Options.Create(new LabbyOptions { DatabasePath = Path.Combine(_directory, "test.db") }));
        services.AddModules(typeof(Registry).Assembly, Path.Combine(_directory, "plugins"),
            LoggerFactory.Create(_ => { }).CreateLogger("test"));
        services.AddSingleton<Registry>();
        services.AddSingleton<Db>();
        services.AddSingleton<AppSettingsStore>();
        services.AddSingleton<AlertRuleStore>();
        services.AddSingleton<HistoryStore>();
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<ConfigTransfer>();
        services.AddSingleton<DashboardImportService>();
        services.AddSingleton<Seeder>();
        _services = services.BuildServiceProvider();
    }

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    public void Dispose()
    {
        _services.Dispose();
        // SQLite pools connections, so the file stays locked until the pool is emptied.
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory should not fail an otherwise passing run.
        }
    }

    // ---------- Widget ordering ----------

    private async Task<(Tab Tab, List<Widget> Widgets)> SeedGridAsync(int count)
    {
        var config = Get<ConfigStore>();
        var tab = new Tab { Slug = "grid", Name = "Grid", Kind = TabKinds.Grid };
        await config.SaveTabAsync(tab);

        var widgets = new List<Widget>();
        for (var i = 0; i < count; i++)
        {
            var widget = new Widget { TabId = tab.Id, Type = "clock", Title = $"w{i}", Sort = i };
            await config.SaveWidgetAsync(widget);
            widgets.Add(widget);
        }
        return (tab, widgets);
    }

    private async Task<string[]> OrderAsync(string tabId) =>
        [.. (await Get<ConfigStore>().WidgetsForTabAsync(tabId)).Select(w => w.Title)];

    [Fact]
    public async Task DraggingACardEarlierPutsItBeforeTheTarget()
    {
        var (tab, widgets) = await SeedGridAsync(4);

        await Get<ConfigStore>().MoveWidgetAsync(widgets[3].Id, widgets[1].Id);

        Assert.Equal(["w0", "w3", "w1", "w2"], await OrderAsync(tab.Id));
    }

    [Fact]
    public async Task DraggingACardToTheEndUsesANullTarget()
    {
        var (tab, widgets) = await SeedGridAsync(3);

        await Get<ConfigStore>().MoveWidgetAsync(widgets[0].Id, null);

        Assert.Equal(["w1", "w2", "w0"], await OrderAsync(tab.Id));
    }

    [Fact]
    public async Task MovingACardOntoItselfChangesNothing()
    {
        var (tab, widgets) = await SeedGridAsync(3);

        await Get<ConfigStore>().MoveWidgetAsync(widgets[1].Id, widgets[1].Id);

        Assert.Equal(["w0", "w1", "w2"], await OrderAsync(tab.Id));
    }

    [Fact]
    public async Task AnUnknownTargetSendsTheCardToTheEndRatherThanLosingTheMove()
    {
        var (tab, widgets) = await SeedGridAsync(3);

        await Get<ConfigStore>().MoveWidgetAsync(widgets[0].Id, "no-such-widget");

        Assert.Equal(["w1", "w2", "w0"], await OrderAsync(tab.Id));
    }

    [Fact]
    public async Task ReorderingLeavesNoTiedSortValues()
    {
        var (tab, widgets) = await SeedGridAsync(5);
        var config = Get<ConfigStore>();

        await config.MoveWidgetAsync(widgets[4].Id, widgets[0].Id);
        await config.MoveWidgetAsync(widgets[1].Id, null);
        await config.MoveWidgetAsync(widgets[2].Id, widgets[3].Id);

        var sorts = (await config.WidgetsForTabAsync(tab.Id)).Select(w => w.Sort).ToList();
        Assert.Equal(Enumerable.Range(0, 5), sorts);
    }

    // ---------- Secrets ----------

    [Fact]
    public async Task PasswordFieldsAreEncryptedOnDiskAndReadBackInTheClear()
    {
        var config = Get<ConfigStore>();
        var connection = new Connection
        {
            Provider = "sonarr",
            Name = "Sonarr",
            Settings = new SettingsBag { ["url"] = "http://nas:8989", ["api_key"] = "super-secret-key" },
        };
        await config.SaveConnectionAsync(connection);

        var raw = await ReadRawSettingsAsync(connection.Id);
        Assert.DoesNotContain("super-secret-key", raw);
        Assert.Contains("http://nas:8989", raw);

        var loaded = await config.ConnectionAsync(connection.Id);
        Assert.Equal("super-secret-key", loaded!.Settings.Get("api_key"));
    }

    [Fact]
    public async Task ResavingDoesNotDoubleEncryptASecret()
    {
        var config = Get<ConfigStore>();
        var connection = new Connection
        {
            Provider = "sonarr",
            Name = "Sonarr",
            Settings = new SettingsBag { ["api_key"] = "key-one" },
        };
        await config.SaveConnectionAsync(connection);

        var loaded = await config.ConnectionAsync(connection.Id);
        await config.SaveConnectionAsync(loaded! with { Name = "Renamed" });

        var again = await config.ConnectionAsync(connection.Id);
        Assert.Equal("key-one", again!.Settings.Get("api_key"));
    }

    private async Task<string> ReadRawSettingsAsync(string id)
    {
        await using var connection = await Get<Db>().OpenAsync();
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT settings FROM connections WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    // ---------- Export and import ----------

    [Fact]
    public async Task ExportWithoutSecretsStripsThemAndTheRoundTripKeepsBindings()
    {
        var config = Get<ConfigStore>();
        var connection = new Connection
        {
            Provider = "sonarr",
            Name = "Sonarr",
            Settings = new SettingsBag { ["url"] = "http://nas:8989", ["api_key"] = "hunter2" },
        };
        await config.SaveConnectionAsync(connection);

        var tab = new Tab { Slug = "media", Name = "Media", Kind = TabKinds.Grid };
        await config.SaveTabAsync(tab);
        await config.SaveWidgetAsync(new Widget
        {
            TabId = tab.Id,
            Type = "service-tile",
            ConnectionId = connection.Id,
        });

        var json = await Get<ConfigTransfer>().ExportAsync(includeSecrets: false);
        Assert.DoesNotContain("hunter2", json);
        Assert.Contains("http://nas:8989", json);

        var result = await Get<ConfigTransfer>().ImportAsync(json);

        // An upsert by id means re-importing your own export is a no-op, not a duplicate.
        Assert.Single(await config.ConnectionsAsync());
        Assert.Single(await config.TabsAsync());
        var widget = Assert.Single(await config.WidgetsAsync());
        Assert.Equal(connection.Id, widget.ConnectionId);
        Assert.Contains(result.Warnings, w => w.Contains("credentials"));
    }

    [Fact]
    public async Task ExportWithSecretsCarriesThem()
    {
        await Get<ConfigStore>().SaveConnectionAsync(new Connection
        {
            Provider = "sonarr",
            Name = "Sonarr",
            Settings = new SettingsBag { ["api_key"] = "hunter2" },
        });

        var json = await Get<ConfigTransfer>().ExportAsync(includeSecrets: true);
        Assert.Contains("hunter2", json);
    }

    [Fact]
    public async Task ImportingAConnectionForAnUninstalledProviderIsReportedNotDropped()
    {
        var json = """
            {
              "version": 1,
              "connections": [
                { "id": "abc", "provider": "nosuchthing", "name": "Mystery box", "icon": "",
                  "enabled": true, "alerts": true, "sort": 0, "settings": {} }
              ],
              "tabs": [], "widgets": []
            }
            """;

        var result = await Get<ConfigTransfer>().ImportAsync(json);

        Assert.Empty(await Get<ConfigStore>().ConnectionsAsync());
        Assert.Contains(result.Warnings, w => w.Contains("nosuchthing"));
    }

    [Fact]
    public async Task ImportingAFutureVersionRefusesRatherThanGuessing()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Get<ConfigTransfer>().ImportAsync("""{ "version": 99, "connections": [], "tabs": [], "widgets": [] }"""));
        Assert.Contains("version 99", error.Message);
    }

    // ---------- Slugs ----------

    [Fact]
    public async Task SlugsAreCleanedAndDeduplicated()
    {
        var config = Get<ConfigStore>();

        Assert.Equal("my-home-lab", await config.UniqueSlugAsync("My Home  Lab!"));

        await config.SaveTabAsync(new Tab { Slug = "media", Name = "Media" });
        Assert.Equal("media-2", await config.UniqueSlugAsync("Media"));
    }

    [Fact]
    public async Task ATabNameOfNothingButPunctuationStillGetsAUsableSlug()
    {
        Assert.Equal("tab", await Get<ConfigStore>().UniqueSlugAsync("!!!"));
    }

    // ---------- Applying an import plan ----------

    [Fact]
    public async Task ApplyingAPlanCreatesTabsWidgetsAndResolvesConnectionReferences()
    {
        var plan = new ImportPlan
        {
            Connections =
            {
                new ImportedConnection("media/sonarr", "sonarr", "Sonarr", "",
                    new SettingsBag { ["url"] = "http://nas:8989" }),
            },
            Tabs =
            {
                new ImportedTab("Imported", "📥")
                {
                    Widgets =
                    {
                        new ImportedWidget("service-tile", "", 3, null, "media/sonarr"),
                        new ImportedWidget("links", "Bookmarks", 3,
                            new SettingsBag { ["links"] = LinkRow.Serialize([new LinkRow("", "Router", "http://192.168.1.1")]) }),
                    },
                },
            },
        };

        var result = await Get<DashboardImportService>().ApplyAsync(plan);

        Assert.Equal(1, result.Connections);
        Assert.Equal(2, result.Widgets);
        Assert.Equal("imported", result.FirstSlug);

        var connection = Assert.Single(await Get<ConfigStore>().ConnectionsAsync());
        var tile = (await Get<ConfigStore>().WidgetsAsync()).First(w => w.Type == "service-tile");
        Assert.Equal(connection.Id, tile.ConnectionId);
    }

    [Fact]
    public async Task APlanNamingAnUninstalledWidgetSkipsItAndSaysSo()
    {
        var plan = new ImportPlan
        {
            Tabs = { new ImportedTab("Odd") { Widgets = { new ImportedWidget("not-a-widget") } } },
        };

        var result = await Get<DashboardImportService>().ApplyAsync(plan);

        Assert.Equal(0, result.Widgets);
        Assert.Contains(result.Notes, n => n.Contains("not-a-widget"));
    }

    [Fact]
    public void PreviewingAnUnrecognisedFileExplainsItself()
    {
        var source = new ImportSource("mystery.txt", "nothing recognisable"u8.ToArray());
        var error = Assert.Throws<FormatException>(() => Get<DashboardImportService>().Preview(source));
        Assert.Contains("mystery.txt", error.Message);
    }

    // ---------- Starter dashboard ----------

    [Fact]
    public async Task TheStarterDashboardOnlyUsesWidgetsThatExist()
    {
        // A typo in a widget type here would give a new user a dashboard of
        // "unknown widget" cards on their very first screen.
        var slug = await Get<Seeder>().CreateStarterLayoutAsync();
        var registry = Get<Registry>();

        var tab = await Get<ConfigStore>().TabBySlugAsync(slug);
        Assert.NotNull(tab);

        var widgets = await Get<ConfigStore>().WidgetsForTabAsync(tab!.Id);
        Assert.NotEmpty(widgets);

        foreach (var widget in widgets)
        {
            Assert.True(registry.WidgetType(widget.Type) is not null,
                $"The starter layout places a “{widget.Type}” widget, which is not registered.");

            // Every starter card is connection-free, since a fresh install has none.
            Assert.False(registry.WidgetType(widget.Type)!.NeedsConnection,
                $"“{widget.Type}” needs a connection, which a fresh install does not have.");
        }

        foreach (var seeded in await Get<ConfigStore>().TabsAsync())
            Assert.True(registry.TabKind(seeded.Kind) is not null, $"Unknown tab kind “{seeded.Kind}”.");
    }

    [Fact]
    public async Task TheStarterDashboardFitsTheTwelveColumnGrid()
    {
        var slug = await Get<Seeder>().CreateStarterLayoutAsync();
        var tab = await Get<ConfigStore>().TabBySlugAsync(slug);
        var widgets = await Get<ConfigStore>().WidgetsForTabAsync(tab!.Id);

        // Widths that do not divide into 12 leave ragged gaps on the first screen a new
        // user sees.
        int[] allowed = [2, 3, 4, 6, 8, 12];
        Assert.All(widgets, w => Assert.Contains(w.Width, allowed));
        Assert.Equal(0, widgets.Sum(w => w.Width) % 12);
    }

    // ---------- Latest readings ----------

    [Fact]
    public async Task LatestReturnsTheNewestValueOfEachMetric()
    {
        var connection = new Connection { Provider = "http", Name = "Station" };
        await Get<ConfigStore>().SaveConnectionAsync(connection);
        var history = Get<HistoryStore>();

        await history.RecordAsync(connection.Id,
            new Dictionary<string, double> { ["temp_outdoor_c"] = 5, ["humidity"] = 80 }, default);
        await history.RecordAsync(connection.Id,
            new Dictionary<string, double> { ["temp_outdoor_c"] = 9, ["humidity"] = 70 }, default);

        var latest = await history.LatestAsync(connection.Id, TimeSpan.FromHours(6));

        // Both metrics present, each at its most recent value — not the first, and not
        // mixed between rows.
        Assert.Equal(9, latest["temp_outdoor_c"]);
        Assert.Equal(70, latest["humidity"]);
    }

    [Fact]
    public async Task LatestIgnoresReadingsOlderThanTheWindow()
    {
        var connection = new Connection { Provider = "http", Name = "Station" };
        await Get<ConfigStore>().SaveConnectionAsync(connection);

        // Written directly, because RecordAsync always stamps "now".
        await using (var db = await Get<Db>().OpenAsync())
        {
            var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO samples (connection_id, metric, ts, value) VALUES ($id, 'temp_outdoor_c', $ts, 5)";
            cmd.Parameters.AddWithValue("$id", connection.Id);
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.AddHours(-9).ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync();
        }

        Assert.Empty(await Get<HistoryStore>().LatestAsync(connection.Id, TimeSpan.FromHours(6)));
        Assert.NotEmpty(await Get<HistoryStore>().LatestAsync(connection.Id, TimeSpan.FromHours(12)));
    }

    [Fact]
    public async Task LatestLooksMetricsUpCaseInsensitively()
    {
        var connection = new Connection { Provider = "http", Name = "Station" };
        await Get<ConfigStore>().SaveConnectionAsync(connection);
        await Get<HistoryStore>().RecordAsync(connection.Id,
            new Dictionary<string, double> { ["temp_outdoor_c"] = 5 }, default);

        var latest = await Get<HistoryStore>().LatestAsync(connection.Id, TimeSpan.FromHours(6));
        Assert.True(latest.ContainsKey("TEMP_OUTDOOR_C"));
    }

    [Fact]
    public async Task LatestOnAConnectionWithNoHistoryIsEmptyRatherThanNull()
    {
        Assert.Empty(await Get<HistoryStore>().LatestAsync("nobody", TimeSpan.FromHours(6)));
    }

    // ---------- App settings ----------

    [Fact]
    public async Task AppSettingsSurviveARoundTripAndTheCacheIsInvalidated()
    {
        var settings = Get<AppSettingsStore>();

        Assert.Equal("system", (await settings.AllAsync()).Get(Appearance.ThemeKey, "system"));

        await settings.SaveAsync(new Dictionary<string, string>
        {
            [Appearance.ThemeKey] = "light",
            [Appearance.AccentKey] = "#35d07f",
        });

        var look = Appearance.From(await settings.AllAsync());
        Assert.Equal("light", look.Theme);
        Assert.Equal("#35d07f", look.Accent);
        Assert.Equal("light", look.ThemeAttribute);
    }

    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "LabbyTwo.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Test";
    }
}

file sealed class NullFileProvider : IFileProvider
{
    public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;
    public IFileInfo GetFileInfo(string subpath) => new NotFoundFileInfo(subpath);
    public IChangeToken Watch(string filter) => NullChangeToken.Singleton;
}
