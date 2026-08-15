using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Services.Import;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace LabbyTwo.Tests;

/// <summary>
/// Sharing one tab or one card, which is a different job from the whole-install export
/// next door and gets different answers to the same questions. These pin the two that
/// matter: a shared thing is a copy rather than the same row, and a connection travels as
/// a description rather than an id that means nothing on the far side.
/// </summary>
public sealed class ShareTransferTests : IDisposable
{
    private readonly string _directory;
    private readonly ServiceProvider _services;

    public ShareTransferTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "labbytwo-share-" + Guid.NewGuid().ToString("n"));
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
        services.AddSingleton<ShareTransfer>();
        _services = services.BuildServiceProvider();
    }

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    public void Dispose()
    {
        _services.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<(Tab Tab, Connection Nas)> SeedAsync()
    {
        var config = Get<ConfigStore>();

        var nas = new Connection
        {
            Provider = "qnap",
            Name = "The NAS",
            Settings = new SettingsBag { ["url"] = "http://nas", ["password"] = "hunter2" },
        };
        await config.SaveConnectionAsync(nas);

        var tab = new Tab { Slug = "lab", Name = "Lab", Icon = "🧪", Kind = TabKinds.Grid };
        await config.SaveTabAsync(tab);

        await config.SaveWidgetAsync(new Widget
        {
            TabId = tab.Id,
            Type = "service-tile",
            Title = "NAS",
            ConnectionId = nas.Id,
            Width = 3,
        });
        await config.SaveWidgetAsync(new Widget
        {
            TabId = tab.Id,
            Type = "clock",
            Title = "Clock",
            Sort = 1,
            Width = 3,
        });

        return (tab, nas);
    }

    [Fact]
    public async Task ATabTravelsWithItsCardsAndNoSecrets()
    {
        var (tab, _) = await SeedAsync();

        var (json, fileName) = await Get<ShareTransfer>().ExportTabAsync(tab.Id);

        Assert.Equal("labbytwo-tab-lab.json", fileName);

        // The whole point: a password cannot leave this way, because no connection
        // settings leave this way at all.
        Assert.DoesNotContain("hunter2", json);

        var share = ShareTransfer.Read(json);
        Assert.Equal(ShareTransfer.TabKind, share.Kind);
        Assert.Equal("Lab", share.Tab!.Name);
        Assert.Equal(2, share.Widgets.Count);

        // The bound connection is described rather than pointed at.
        var tile = Assert.Single(share.Widgets, w => w.Type == "service-tile");
        Assert.Equal("qnap", tile.BoundTo!.Provider);
        Assert.Equal("The NAS", tile.BoundTo.Name);
    }

    /// <summary>
    /// Ids are what separate this from the whole-config export. Somebody else's tab is a
    /// copy: importing it twice should give two tabs, and it must never land on top of one
    /// of yours.
    /// </summary>
    [Fact]
    public async Task ImportingTheSameTabTwiceGivesTwoTabs()
    {
        var (tab, _) = await SeedAsync();
        var share = Get<ShareTransfer>();
        var (json, _) = await share.ExportTabAsync(tab.Id);

        var first = await share.ApplyAsync(await share.PlanAsync(ShareTransfer.Read(json)));
        var second = await share.ApplyAsync(await share.PlanAsync(ShareTransfer.Read(json)));

        Assert.NotEqual(first.TabSlug, second.TabSlug);

        var tabs = await Get<ConfigStore>().TabsAsync();
        Assert.Equal(3, tabs.Count);                       // the original, plus two copies
        Assert.Equal(3, tabs.Select(t => t.Id).Distinct().Count());

        // And the original is untouched.
        Assert.Contains(tabs, t => t.Id == tab.Id && t.Slug == "lab");
    }

    [Fact]
    public async Task ACardBindsToTheMatchingConnectionOnTheFarSide()
    {
        var (tab, nas) = await SeedAsync();
        var share = Get<ShareTransfer>();
        var (json, _) = await share.ExportTabAsync(tab.Id);

        var result = await share.ApplyAsync(await share.PlanAsync(ShareTransfer.Read(json)));

        var widgets = await Get<ConfigStore>().WidgetsAsync();
        var copy = Assert.Single(widgets, w => w.Type == "service-tile" && w.TabId != tab.Id);

        Assert.Equal(nas.Id, copy.ConnectionId);

        // The slug note is expected — this imports back into the install it came from.
        // What matters is that nothing had to be said about the connection.
        Assert.DoesNotContain(result.Notes, n => n.Contains("NAS"));
    }

    /// <summary>
    /// The case the plan exists for. A tab whose cards want something you do not have
    /// should still import — just say so first, and leave the card unbound rather than
    /// pointed at whatever happened to be nearby.
    /// </summary>
    [Fact]
    public async Task AMissingConnectionIsReportedAndLeavesTheCardUnbound()
    {
        var (tab, nas) = await SeedAsync();
        var share = Get<ShareTransfer>();
        var (json, _) = await share.ExportTabAsync(tab.Id);

        await Get<ConfigStore>().DeleteConnectionAsync(nas.Id);

        var plan = await share.PlanAsync(ShareTransfer.Read(json));
        Assert.Contains(plan.Notes, n => n.Contains("The NAS") && n.Contains("no match"));

        var result = await share.ApplyAsync(plan);

        var widgets = await Get<ConfigStore>().WidgetsAsync();
        var copy = Assert.Single(widgets, w => w.Type == "service-tile" && w.TabId != tab.Id);
        Assert.Null(copy.ConnectionId);
        Assert.Equal(2, result.Widgets);
    }

    /// <summary>
    /// Two installs rarely give the same thing the same name, so a single candidate of the
    /// right kind is taken — and the swap is said out loud, because silently binding to
    /// the wrong NAS would be worse than not binding at all.
    /// </summary>
    [Fact]
    public async Task ASingleCandidateIsUsedEvenUnderAnotherName()
    {
        var (tab, nas) = await SeedAsync();
        var share = Get<ShareTransfer>();
        var (json, _) = await share.ExportTabAsync(tab.Id);

        var config = Get<ConfigStore>();
        await config.DeleteConnectionAsync(nas.Id);
        var mine = new Connection { Provider = "qnap", Name = "Basement box", Settings = new SettingsBag() };
        await config.SaveConnectionAsync(mine);

        var plan = await share.PlanAsync(ShareTransfer.Read(json));
        Assert.Contains(plan.Notes, n => n.Contains("Basement box"));

        await share.ApplyAsync(plan);

        var widgets = await config.WidgetsAsync();
        var copy = Assert.Single(widgets, w => w.Type == "service-tile" && w.TabId != tab.Id);
        Assert.Equal(mine.Id, copy.ConnectionId);
    }

    /// <summary>A slug already in use is moved aside rather than colliding.</summary>
    [Fact]
    public async Task TheSlugIsMadeUnique()
    {
        var (tab, _) = await SeedAsync();
        var share = Get<ShareTransfer>();
        var (json, _) = await share.ExportTabAsync(tab.Id);

        var plan = await share.PlanAsync(ShareTransfer.Read(json));

        Assert.NotEqual("lab", plan.Slug);
        Assert.Contains(plan.Notes, n => n.Contains("/t/lab") && n.Contains("taken"));
    }

    [Fact]
    public async Task ACardCanTravelOnItsOwn()
    {
        var (tab, nas) = await SeedAsync();
        var config = Get<ConfigStore>();
        var share = Get<ShareTransfer>();

        var tile = Assert.Single(await config.WidgetsForTabAsync(tab.Id), w => w.Type == "service-tile");
        var (json, fileName) = await share.ExportWidgetAsync(tile.Id);

        Assert.Equal("labbytwo-card-nas.json", fileName);

        var read = ShareTransfer.Read(json);
        Assert.Equal(ShareTransfer.WidgetKind, read.Kind);
        Assert.Null(read.Tab);

        var result = await share.ApplyAsync(await share.PlanAsync(read));

        Assert.Equal(1, result.Widgets);
        Assert.Null(result.TabSlug);

        // It lands on the dashboard that already exists, and says which.
        Assert.Contains(result.Notes, n => n.Contains("Lab"));
        Assert.Equal(3, (await config.WidgetsForTabAsync(tab.Id)).Count);
        Assert.Equal(nas.Id, (await config.WidgetsForTabAsync(tab.Id)).Last().ConnectionId);
    }

    [Fact]
    public void RubbishIsRefusedWithSomethingReadable()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ShareTransfer.Read("not json at all"));
        Assert.Contains("not a LabbyTwo tab or card file", ex.Message);

        var newer = Assert.Throws<InvalidOperationException>(
            () => ShareTransfer.Read("""{"version": 99, "kind": "tab"}"""));
        Assert.Contains("understands up to", newer.Message);

        var empty = Assert.Throws<InvalidOperationException>(
            () => ShareTransfer.Read("""{"version": 1, "kind": "tab"}"""));
        Assert.Contains("does not contain one", empty.Message);
    }

    /// <summary>The other test files each keep one of these private; this is ours.</summary>
    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "LabbyTwo.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Test";
    }
}
