using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace LabbyTwo.Tests;

/// <summary>
/// Saving a tab as a template and starting tabs from it. The template is a shared-tab file
/// kept in the database, so these pin what that promises: the whole tab comes back — kind,
/// settings, every card with its layout and order — connections are found again by what
/// they are, and the person is asked, rather than guessed for, when that is not certain.
/// </summary>
public sealed class TabTemplateTests : IDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;

    public TabTemplateTests() => _services = TestHost.ReadyHost(_directory);

    public void Dispose() => TestHost.Teardown(_services, _directory);

    private ConfigStore Config => _services.GetRequiredService<ConfigStore>();

    private static TabTemplates TemplatesFor(ServiceProvider services)
    {
        var config = services.GetRequiredService<ConfigStore>();
        var registry = services.GetRequiredService<Registry>();
        var share = new ShareTransfer(config, registry, services.GetRequiredService<AppSettingsStore>());
        return new TabTemplates(services.GetRequiredService<TemplateStore>(), share, config, registry);
    }

    private TabTemplates Templates => TemplatesFor(_services);

    private static ConfigTransfer TransferFor(ServiceProvider services) => new(
        services.GetRequiredService<ConfigStore>(), services.GetRequiredService<AlertRuleStore>(),
        services.GetRequiredService<Registry>(), services.GetRequiredService<TemplateStore>());

    /// <summary>
    /// A dashboard with a NAS card bound to a connection, a clock, and a card whose settings
    /// carry layout this code has never heard of — which is how a tab kind that places cards
    /// freely keeps them, and why capture has to be generic.
    /// </summary>
    private async Task<(Tab Tab, Connection Nas)> SeedAsync()
    {
        var nas = new Connection
        {
            Provider = "qnap",
            Name = "Fennell-NAS",
            Settings = new SettingsBag { ["url"] = "http://nas", ["password"] = "hunter2" },
        };
        await Config.SaveConnectionAsync(nas);

        var tab = new Tab
        {
            Slug = "lab",
            Name = "Lab",
            Icon = "🧪",
            Kind = TabKinds.Grid,
            Settings = new SettingsBag { ["subtitle"] = "Everything in the rack" },
        };
        await Config.SaveTabAsync(tab);

        // Saved out of order on purpose: the sort is what orders them, not the insert.
        await Config.SaveWidgetAsync(new Widget
        {
            TabId = tab.Id, Type = "clock", Title = "Clock", Sort = 2, Width = 3,
            Settings = new SettingsBag { ["x"] = "8", ["y"] = "0", ["h"] = "2" },
        });
        await Config.SaveWidgetAsync(new Widget
        {
            TabId = tab.Id, Type = "service-tile", Title = "NAS", ConnectionId = nas.Id, Sort = 0, Width = 6,
        });
        await Config.SaveWidgetAsync(new Widget
        {
            TabId = tab.Id, Type = "clock", Title = "Second clock", Sort = 1, Width = 12,
            Settings = new SettingsBag { ["timezone"] = "Europe/London" },
        });

        return (tab, nas);
    }

    [Fact]
    public async Task ATabComesBackWithItsSettingsCardsLayoutAndOrder()
    {
        var (tab, nas) = await SeedAsync();

        var template = await Templates.SaveFromTabAsync(tab.Id, "  Rack page ", "🗄️", "A page for the rack");
        Assert.Equal("Rack page", template.Name);
        Assert.DoesNotContain("hunter2", template.Content);

        var prepared = await Templates.PrepareAsync(template.Id);
        Assert.False(prepared.NeedsReview);         // the NAS is here under its own name

        var result = await Templates.CreateTabAsync(template.Id, "Rack", "🗄️");
        Assert.Equal("rack", result.TabSlug);
        Assert.Equal(3, result.Widgets);

        var created = (await Config.TabBySlugAsync("rack"))!;
        Assert.NotEqual(tab.Id, created.Id);
        Assert.Equal(TabKinds.Grid, created.Kind);
        Assert.Equal("Rack", created.Name);
        Assert.Equal("🗄️", created.Icon);
        Assert.Equal("Everything in the rack", created.Settings.Get("subtitle"));

        var original = await Config.WidgetsForTabAsync(tab.Id);
        var copies = await Config.WidgetsForTabAsync(created.Id);

        Assert.Equal(original.Select(w => w.Title), copies.Select(w => w.Title));
        Assert.Equal(original.Select(w => w.Width), copies.Select(w => w.Width));
        Assert.Equal(original.Select(w => w.Type), copies.Select(w => w.Type));
        for (var i = 0; i < original.Count; i++)
            Assert.Equal(original[i].Settings.ToJson(), copies[i].Settings.ToJson());

        Assert.Equal(nas.Id, copies.Single(w => w.Title == "NAS").ConnectionId);
        Assert.DoesNotContain(copies, w => original.Any(o => o.Id == w.Id));
    }

    [Fact]
    public async Task ATemplateIsACopyThatLaterEditsToTheTabDoNotReach()
    {
        var (tab, _) = await SeedAsync();
        var template = await Templates.SaveFromTabAsync(tab.Id, "Lab", "", "");

        foreach (var widget in await Config.WidgetsForTabAsync(tab.Id))
            await Config.DeleteWidgetAsync(widget.Id);

        var result = await Templates.CreateTabAsync(template.Id, "Again", "");
        Assert.Equal(3, result.Widgets);
    }

    [Fact]
    public async Task ConnectionsInTheTabKindsOwnSettingsAreMatchedToo()
    {
        var station = new Connection { Provider = "ambient", Name = "Garden" };
        await Config.SaveConnectionAsync(station);

        var tab = new Tab
        {
            Slug = "weather", Name = "Weather", Kind = "weather-station",
            Settings = new SettingsBag { ["connection"] = station.Id, ["radar_zoom"] = "9" },
        };
        await Config.SaveTabAsync(tab);

        var template = await Templates.SaveFromTabAsync(tab.Id, "Weather", "🌦️", "");
        Assert.DoesNotContain(station.Id, template.Content);   // named, not pointed at

        await Templates.CreateTabAsync(template.Id, "Weather", "");
        var created = (await Config.TabsAsync()).Single(t => t.Id != tab.Id);

        Assert.Equal("weather-station", created.Kind);
        Assert.Equal(station.Id, created.Settings.Get("connection"));
        Assert.Equal("9", created.Settings.Get("radar_zoom"));
    }

    // ---- matching ----------------------------------------------------------------------

    /// <summary>The template made, then the NAS it was made against taken away.</summary>
    private async Task<TabTemplate> TemplateWithoutItsNasAsync()
    {
        var (tab, nas) = await SeedAsync();
        var template = await Templates.SaveFromTabAsync(tab.Id, "Lab", "", "");
        await Config.DeleteConnectionAsync(nas.Id);
        return template;
    }

    [Fact]
    public async Task AnExactMatchIsUsedWithoutAsking()
    {
        var (tab, nas) = await SeedAsync();
        var template = await Templates.SaveFromTabAsync(tab.Id, "Lab", "", "");

        // Case is not a difference worth asking about.
        await Config.SaveConnectionAsync(nas with { Name = "FENNELL-nas" });

        var binding = Assert.Single((await Templates.PrepareAsync(template.Id)).Bindings);
        Assert.Equal(ShareTransfer.MatchKind.Exact, binding.Match.How);
        Assert.Equal(nas.Id, binding.Match.Found!.Id);
    }

    [Fact]
    public async Task TheOnlyOneOfItsTypeIsOfferedButAskedAbout()
    {
        var template = await TemplateWithoutItsNasAsync();
        var other = new Connection { Provider = "qnap", Name = "Basement NAS" };
        await Config.SaveConnectionAsync(other);

        var prepared = await Templates.PrepareAsync(template.Id);
        var binding = Assert.Single(prepared.Bindings);

        Assert.Equal(ShareTransfer.MatchKind.OnlyOne, binding.Match.How);
        Assert.Equal(other.Id, binding.Match.Found!.Id);
        Assert.True(prepared.NeedsReview);
        Assert.Contains("the “NAS” card", binding.UsedBy);

        // Accepting the suggestion as it stands binds the card to it.
        await Templates.CreateTabAsync(template.Id, "Lab two", "");
        var tile = (await Config.WidgetsAsync()).Single(w => w.Title == "NAS" && w.ConnectionId is not null);
        Assert.Equal(other.Id, tile.ConnectionId);
    }

    [Fact]
    public async Task SeveralCandidatesAreNeverGuessedBetween()
    {
        var template = await TemplateWithoutItsNasAsync();
        var first = new Connection { Provider = "qnap", Name = "Upstairs" };
        var second = new Connection { Provider = "qnap", Name = "Downstairs" };
        await Config.SaveConnectionAsync(first);
        await Config.SaveConnectionAsync(second);
        await Config.SaveConnectionAsync(new Connection { Provider = "http", Name = "Fennell-NAS" });

        var binding = Assert.Single((await Templates.PrepareAsync(template.Id)).Bindings);
        Assert.Equal(ShareTransfer.MatchKind.Several, binding.Match.How);
        Assert.Null(binding.Match.Found);

        // Only the right kind is on offer: an HTTP check sharing the name is not a NAS.
        Assert.Equal(
            new[] { first.Id, second.Id }.Order(),
            binding.Match.Candidates.Select(c => c.Id).Order());

        // Left alone, it stays unbound rather than picking one.
        var unanswered = await Templates.CreateTabAsync(template.Id, "Unanswered", "");
        var unbound = (await Config.WidgetsForTabAsync((await Config.TabBySlugAsync(unanswered.TabSlug!))!.Id))
            .Single(w => w.Title == "NAS");
        Assert.Null(unbound.ConnectionId);

        // Answered, it uses the answer.
        var answered = await Templates.CreateTabAsync(template.Id, "Answered", "",
            new Dictionary<ShareTransfer.ConnectionRef, string> { [binding.Reference] = second.Id });
        var bound = (await Config.WidgetsForTabAsync((await Config.TabBySlugAsync(answered.TabSlug!))!.Id))
            .Single(w => w.Title == "NAS");
        Assert.Equal(second.Id, bound.ConnectionId);
    }

    [Fact]
    public async Task WithNothingOfThatKindTheCardIsAddedUnbound()
    {
        var template = await TemplateWithoutItsNasAsync();

        var prepared = await Templates.PrepareAsync(template.Id);
        var binding = Assert.Single(prepared.Bindings);
        Assert.Equal(ShareTransfer.MatchKind.None, binding.Match.How);
        Assert.Empty(binding.Match.Candidates);
        Assert.True(prepared.NeedsReview);

        var result = await Templates.CreateTabAsync(template.Id, "Lab", "");
        Assert.Equal(3, result.Widgets);
        Assert.Contains(result.Notes, n => n.Contains("Fennell-NAS"));
    }

    [Fact]
    public async Task LeavingItUnboundOverridesEvenACertainMatch()
    {
        var (tab, nas) = await SeedAsync();
        var template = await Templates.SaveFromTabAsync(tab.Id, "Lab", "", "");

        var result = await Templates.CreateTabAsync(template.Id, "Loose", "",
            new Dictionary<ShareTransfer.ConnectionRef, string> { [new("QNAP", "fennell-nas")] = "" });

        var created = (await Config.TabBySlugAsync(result.TabSlug!))!;
        Assert.All(await Config.WidgetsForTabAsync(created.Id), w => Assert.Null(w.ConnectionId));
        Assert.Equal(nas.Id, (await Config.WidgetsForTabAsync(tab.Id)).Single(w => w.Title == "NAS").ConnectionId);
    }

    [Fact]
    public async Task ACardPlacedTwiceOnOneConnectionIsOneQuestion()
    {
        var (tab, nas) = await SeedAsync();
        await Config.SaveWidgetAsync(new Widget { TabId = tab.Id, Type = "service-tile", Title = "NAS again", ConnectionId = nas.Id, Sort = 3 });
        var template = await Templates.SaveFromTabAsync(tab.Id, "Lab", "", "");

        var binding = Assert.Single((await Templates.PrepareAsync(template.Id)).Bindings);
        Assert.Equal(2, binding.UsedBy.Count);
    }

    // ---- slugs ----------------------------------------------------------------------------

    [Fact]
    public async Task EveryTabFromATemplateGetsItsOwnSlug()
    {
        var (tab, _) = await SeedAsync();
        var template = await Templates.SaveFromTabAsync(tab.Id, "Lab", "", "");

        var first = await Templates.CreateTabAsync(template.Id, "Lab", "");
        var second = await Templates.CreateTabAsync(template.Id, "Lab", "");
        var blank = await Templates.CreateTabAsync(template.Id, "   ", "");

        Assert.Equal("lab-2", first.TabSlug);
        Assert.Equal("lab-3", second.TabSlug);

        // No name given falls back to the template's, which is taken twice over by now.
        Assert.Equal("lab-4", blank.TabSlug);

        var tabs = await Config.TabsAsync();
        Assert.Equal(4, tabs.Count);
        Assert.Equal(tabs.Count, tabs.Select(t => t.Slug).Distinct().Count());

        // And at the end of the nav rather than on top of anything.
        Assert.Equal(tabs.Max(t => t.Sort), tabs.Single(t => t.Slug == "lab-4").Sort);
    }

    // ---- managing ----------------------------------------------------------------------

    [Fact]
    public async Task ATemplateCanBeRenamedAndDeleted()
    {
        var (tab, _) = await SeedAsync();
        var template = await Templates.SaveFromTabAsync(tab.Id, "Lab", "🧪", "");
        var store = _services.GetRequiredService<TemplateStore>();

        await Templates.RenameAsync(template.Id, "Rack", "🗄️", "The rack");
        var renamed = (await store.GetAsync(template.Id))!;
        Assert.Equal(("Rack", "🗄️", "The rack"), (renamed.Name, renamed.Icon, renamed.Description));
        Assert.Equal(template.Content, renamed.Content);

        await Templates.DeleteAsync(template.Id);
        Assert.Empty(await store.AllAsync());
        Assert.NotNull(await Config.TabAsync(tab.Id));      // the tab it came from stays
    }

    // ---- files -----------------------------------------------------------------------------

    [Fact]
    public async Task ATemplateFileCarriesItsNameAndArrivesAsATemplate()
    {
        var (tab, _) = await SeedAsync();
        var template = await Templates.SaveFromTabAsync(tab.Id, "Rack page", "🗄️", "A page for the rack");

        var (json, fileName) = await Templates.ExportAsync(template.Id);
        Assert.Equal("labbytwo-template-rack-page.json", fileName);
        Assert.DoesNotContain("hunter2", json);

        var file = ShareTransfer.Read(json);
        Assert.Equal(ShareTransfer.TemplateKind, file.Kind);
        Assert.Equal("A page for the rack", file.Template!.Description);

        var elsewhere = TestHost.TempDirectory();
        var other = TestHost.ReadyHost(elsewhere);
        try
        {
            var imported = await TemplatesFor(other).ImportAsync(json);
            Assert.Equal(("Rack page", "🗄️", "A page for the rack"), (imported.Name, imported.Icon, imported.Description));
            Assert.NotEqual(template.Id, imported.Id);

            // Nothing there has a NAS, so starting from it stops to say so.
            var prepared = await TemplatesFor(other).PrepareAsync(imported.Id);
            Assert.Equal(ShareTransfer.MatchKind.None, Assert.Single(prepared.Bindings).Match.How);

            var result = await TemplatesFor(other).CreateTabAsync(imported.Id, "Rack", "");
            Assert.Equal(3, result.Widgets);
        }
        finally
        {
            TestHost.Teardown(other, elsewhere);
        }
    }

    [Fact]
    public async Task AnExportedTabFileCanBecomeATemplateButACardCannot()
    {
        var (tab, _) = await SeedAsync();
        var share = new ShareTransfer(Config, _services.GetRequiredService<Registry>(),
            _services.GetRequiredService<AppSettingsStore>());

        var (tabJson, _) = await share.ExportTabAsync(tab.Id);
        var fromTab = await Templates.ImportAsync(tabJson);
        Assert.Equal("Lab", fromTab.Name);
        Assert.Equal("🧪", fromTab.Icon);

        var widget = (await Config.WidgetsForTabAsync(tab.Id))[0];
        var (cardJson, _) = await share.ExportWidgetAsync(widget.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Templates.ImportAsync(cardJson));
    }

    [Fact]
    public async Task ATemplateFileHandedToTheTabImportBecomesATab()
    {
        var (tab, _) = await SeedAsync();
        var template = await Templates.SaveFromTabAsync(tab.Id, "Rack page", "", "");
        var (json, _) = await Templates.ExportAsync(template.Id);

        var share = new ShareTransfer(Config, _services.GetRequiredService<Registry>(),
            _services.GetRequiredService<AppSettingsStore>());
        var plan = await share.PlanAsync(ShareTransfer.Read(json));
        Assert.True(plan.IsTab);

        var result = await share.ApplyAsync(plan);
        Assert.Equal(3, result.Widgets);
        Assert.NotNull(result.TabSlug);
    }

    [Fact]
    public void AFileOfSomeUnknownKindIsRefusedRatherThanReadAsACard()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ShareTransfer.Read("""{ "version": 1, "kind": "spaceship", "widgets": [ { "type": "clock" } ] }"""));
        Assert.Contains("spaceship", ex.Message);
    }

    // ---- full backups --------------------------------------------------------------------

    [Fact]
    public async Task AFullExportCarriesTemplatesAndRestoresThemInPlace()
    {
        var (tab, _) = await SeedAsync();
        var template = await Templates.SaveFromTabAsync(tab.Id, "Rack page", "🗄️", "For the rack");

        var json = await TransferFor(_services).ExportAsync(includeSecrets: false);
        Assert.Contains("Rack page", json);
        Assert.DoesNotContain("hunter2", json);

        var elsewhere = TestHost.TempDirectory();
        var other = TestHost.ReadyHost(elsewhere);
        try
        {
            var result = await TransferFor(other).ImportAsync(json);
            Assert.Equal(1, result.Templates);

            // A second restore of the same backup leaves one, not two.
            await TransferFor(other).ImportAsync(json);

            var restored = Assert.Single(await other.GetRequiredService<TemplateStore>().AllAsync());
            Assert.Equal(template.Id, restored.Id);
            Assert.Equal("For the rack", restored.Description);

            // And it works there: the NAS came across in the same backup, under its name.
            var prepared = await TemplatesFor(other).PrepareAsync(restored.Id);
            Assert.False(prepared.NeedsReview);
            var created = await TemplatesFor(other).CreateTabAsync(restored.Id, "Rack", "");
            Assert.Equal(3, created.Widgets);
        }
        finally
        {
            TestHost.Teardown(other, elsewhere);
        }
    }

    /// <summary>A file written before templates existed, byte for byte the shape it had.</summary>
    [Fact]
    public async Task AFullExportFromBeforeTemplatesStillImports()
    {
        const string old = """
            {
              "version": 2,
              "exportedAt": "2026-01-01T00:00:00+00:00",
              "includesSecrets": false,
              "includesLinks": true,
              "connections": [
                { "id": "c1", "provider": "http", "name": "Router", "icon": "", "enabled": true,
                  "alerts": true, "sort": 0, "settings": { "url": "http://router" } }
              ],
              "tabs": [
                { "id": "t1", "slug": "home", "name": "Home", "icon": "🏠", "kind": "grid",
                  "sort": 0, "enabled": true, "settings": {} }
              ],
              "widgets": [
                { "id": "w1", "tabId": "t1", "type": "service-tile", "title": "Router",
                  "connectionId": "c1", "sort": 0, "width": 4, "settings": {} }
              ],
              "rules": []
            }
            """;

        var result = await TransferFor(_services).ImportAsync(old);

        Assert.Equal((1, 1, 1, 0), (result.Connections, result.Tabs, result.Widgets, result.Templates));
        Assert.Empty(await _services.GetRequiredService<TemplateStore>().AllAsync());
    }

    [Fact]
    public async Task AnExportMadeWithoutTheTemplateStoreStillWorks()
    {
        // The shape every other test that builds one by hand uses.
        var transfer = new ConfigTransfer(Config, _services.GetRequiredService<AlertRuleStore>(),
            _services.GetRequiredService<Registry>());
        var (tab, _) = await SeedAsync();
        await Templates.SaveFromTabAsync(tab.Id, "Lab", "", "");

        var json = await transfer.ExportAsync(includeSecrets: false);
        Assert.Equal(0, (await transfer.ImportAsync(json)).Templates);
    }

    // ---- the migration ---------------------------------------------------------------------

    [Fact]
    public async Task AnExistingDatabaseGainsTheTemplatesTableAndKeepsItsTabs()
    {
        var (tab, _) = await SeedAsync();

        // Back to how a database looked the day before this shipped.
        await using (var connection = await _services.GetRequiredService<Db>().OpenAsync())
        {
            var rewind = connection.CreateCommand();
            rewind.CommandText = "DROP TABLE tab_templates; PRAGMA user_version = 13;";
            await rewind.ExecuteNonQueryAsync();
        }

        var reopened = new Db(Options.Create(new LabbyOptions { DatabasePath = Path.Combine(_directory, "test.db") }),
            new Environment(_directory));
        var store = new TemplateStore(reopened);

        Assert.Empty(await store.AllAsync());
        await store.SaveAsync(new TabTemplate { Name = "After", Content = "{}" });
        Assert.Single(await store.AllAsync());

        await using var check = await reopened.OpenAsync();
        var read = check.CreateCommand();
        read.CommandText = "SELECT (SELECT name FROM tabs WHERE id = $id) || '|' || (PRAGMA_user_version.user_version) FROM PRAGMA_user_version";
        read.Parameters.AddWithValue("$id", tab.Id);
        var answer = (string)(await read.ExecuteScalarAsync())!;
        Assert.StartsWith("Lab|", answer);
        Assert.True(int.Parse(answer.Split('|')[1]) >= 14);
    }

    private sealed class Environment(string root) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "LabbyTwo.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Test";
    }
}
