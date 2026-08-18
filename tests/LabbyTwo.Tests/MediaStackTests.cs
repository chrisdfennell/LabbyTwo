using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace LabbyTwo.Tests;

/// <summary>
/// The media page gathers by the category a provider declares rather than by a list of
/// names. That is the whole design, and it is also the thing that quietly breaks when a
/// provider's category is changed — so it is what these test.
/// </summary>
public sealed class MediaStackTests : IDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private readonly ConfigStore _config;
    private readonly HistoryStore _history;
    private readonly MediaStack _stack;

    public MediaStackTests()
    {
        _services = TestHost.Build(_directory);
        _services.GetRequiredService<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();
        _config = _services.GetRequiredService<ConfigStore>();
        _history = _services.GetRequiredService<HistoryStore>();

        var health = ActivatorUtilities.CreateInstance<HealthMonitor>(_services);
        _stack = ActivatorUtilities.CreateInstance<MediaStack>(_services, health);
    }

    /// <summary>An unreachable address, so nothing here waits on a real network timeout.</summary>
    private static SettingsBag Nowhere(params (string Key, string Value)[] extra)
    {
        var bag = new SettingsBag { ["url"] = "http://127.0.0.1:1", ["api_key"] = "x" };
        foreach (var (key, value) in extra)
            bag[key] = value;
        return bag;
    }

    private async Task<Connection> AddAsync(string provider, string name, bool enabled = true)
    {
        var connection = new Connection
        {
            Provider = provider,
            Name = name,
            Enabled = enabled,
            Settings = Nowhere(),
        };
        await _config.SaveConnectionAsync(connection);
        return connection;
    }

    [Fact]
    public async Task OnlyMediaAndDownloadConnectionsAreGathered()
    {
        await AddAsync("sonarr", "Sonarr");          // Media
        await AddAsync("sabnzbd", "SAB");            // Downloads
        await AddAsync("qnap", "NAS");               // Storage — not this page's business
        await AddAsync("pihole", "Pi-hole");         // Network

        var gathered = (await _stack.ConnectionsAsync()).Select(c => c.Name).ToList();

        // Order is the store's, not this method's, so compare as a set.
        Assert.Equal(["SAB", "Sonarr"], gathered.Order().ToList());
    }

    [Fact]
    public async Task ADisabledConnectionIsLeftOut()
    {
        await AddAsync("sonarr", "Paused one", enabled: false);
        await AddAsync("radarr", "Live one");

        var gathered = (await _stack.ConnectionsAsync()).Select(c => c.Name).ToList();

        Assert.Equal(["Live one"], gathered);
    }

    [Fact]
    public async Task WithNothingConnectedThePageGetsAnEmptySnapshotRatherThanWork()
    {
        await AddAsync("qnap", "NAS");

        var snapshot = await _stack.ReadAsync();

        Assert.False(snapshot.Any);
        Assert.Same(MediaStack.Snapshot.Empty, snapshot);
    }

    [Fact]
    public async Task LibraryCountsComeFromWhicheverMetricsWereRecorded()
    {
        // Named by metric rather than by provider, so a library server nobody has written
        // an integration for yet still counts — as long as it reports a number we know.
        var jellyfin = await AddAsync("jellyfin", "Jellyfin");
        await _history.RecordAsync(jellyfin.Id,
            new Dictionary<string, double> { ["items"] = 8110, ["libraries"] = 4 }, CancellationToken.None);

        var snapshot = await _stack.ReadAsync();
        var library = Assert.Single(snapshot.Libraries);

        Assert.Equal("Jellyfin", library.Name);
        Assert.Contains(("Items", 8110d), library.Counts);
        Assert.Contains(("Libraries", 4d), library.Counts);
    }

    [Fact]
    public async Task ADownloadClientReportsWhicheverSpeedMetricItUses()
    {
        // SABnzbd records speed_mbps; the torrent clients record download_mbps. The page
        // must not care which, or half the stack shows a dash.
        var sab = await AddAsync("sabnzbd", "SAB");
        await _history.RecordAsync(sab.Id,
            new Dictionary<string, double> { ["speed_mbps"] = 38.2, ["disk_free_gb"] = 900 },
            CancellationToken.None);

        var client = Assert.Single((await _stack.ReadAsync()).Clients);

        Assert.Equal(38.2, client.DownMbps);
        Assert.Equal(900, client.FreeDiskGb);
        Assert.False(client.Paused);
    }

    [Fact]
    public async Task RemainingMegabytesBecomeGigabytesSoOneColumnCanShowBoth()
    {
        var nzbget = await AddAsync("nzbget", "NZBGet");
        await _history.RecordAsync(nzbget.Id,
            new Dictionary<string, double> { ["remaining_mb"] = 20480 }, CancellationToken.None);

        var client = Assert.Single((await _stack.ReadAsync()).Clients);

        Assert.Equal(20, client.RemainingGb);
    }

    [Fact]
    public async Task APausedDownloaderIsSomethingToActOn()
    {
        // Paused is the failure that looks exactly like working: nothing is broken, the
        // queue simply never moves.
        var sab = await AddAsync("sabnzbd", "SAB");
        await _history.RecordAsync(sab.Id,
            new Dictionary<string, double> { ["paused"] = 1 }, CancellationToken.None);

        var snapshot = await _stack.ReadAsync();

        Assert.True(Assert.Single(snapshot.Clients).Paused);
        Assert.Contains(snapshot.NeedsAttention, a => a.Source == "SAB" && a.Message.Contains("paused"));
    }

    [Fact]
    public async Task SubtitlesWantedAcrossBothCountsAreAddedUp()
    {
        var bazarr = await AddAsync("bazarr", "Bazarr");
        await _history.RecordAsync(bazarr.Id,
            new Dictionary<string, double>
            {
                ["subtitles_wanted_episodes"] = 12,
                ["subtitles_wanted_movies"] = 3,
            },
            CancellationToken.None);

        var snapshot = await _stack.ReadAsync();

        Assert.Contains(snapshot.NeedsAttention, a => a.Message.StartsWith("15 items want subtitles"));
    }

    [Fact]
    public async Task OnlyMetricsThatWereActuallyRecordedOfferAChart()
    {
        // What stops the page drawing four empty axes for services you do not run.
        var sonarr = await AddAsync("sonarr", "Sonarr");
        await _history.RecordAsync(sonarr.Id,
            new Dictionary<string, double> { ["queue_count"] = 2 }, CancellationToken.None);

        var recorded = await _stack.RecordedMetricsAsync();

        Assert.Contains("queue_count", recorded);
        Assert.DoesNotContain("stream_count", recorded);
    }

    [Fact]
    public async Task MetricsAreNotBorrowedFromAConnectionThatIsNotOnThisPage()
    {
        // A NAS reporting "files" must not put a chart on the media page.
        var nas = await AddAsync("qnap", "NAS");
        await _history.RecordAsync(nas.Id,
            new Dictionary<string, double> { ["disk_percent"] = 71 }, CancellationToken.None);

        Assert.DoesNotContain("disk_percent", await _stack.RecordedMetricsAsync());
    }

    public void Dispose() => TestHost.Teardown(_services, _directory);
}

/// <summary>
/// The media tab is only its widgets, arranged. It renders every panel by looking the
/// widget up in the registry rather than naming a component type, so a section that is
/// not in the picker simply does not draw — these pin the keys it looks up, because a
/// typo there would silently blank a panel rather than fail to compile.
/// </summary>
public class MediaWidgetTests
{
    private static Registry Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddTestStorage(TestHost.TempDirectory());
        services.AddModules(
            typeof(Registry).Assembly,
            Path.Combine(Path.GetTempPath(), "labbytwo-no-plugins-" + Guid.NewGuid().ToString("n")),
            Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { }).CreateLogger("test"));
        services.AddSingleton<Registry>();
        return services.BuildServiceProvider().GetRequiredService<Registry>();
    }

    /// <summary>Every key <c>MediaTab</c> asks the registry for.</summary>
    private static readonly string[] SectionsTheTabRenders =
    [
        "media-attention",
        "media-now-playing",
        "media-upcoming",
        "media-queue",
        "media-clients",
        "media-library",
        "compare-chart",
    ];

    [Theory]
    [MemberData(nameof(Keys))]
    public void EverySectionOfTheMediaTabIsAWidgetYouCouldHavePlacedYourself(string key)
    {
        var widget = Build().WidgetType(key);

        Assert.True(widget is not null,
            $"MediaTab renders \"{key}\", but no widget is registered under that key — so that panel " +
            "would be blank on the page and missing from the picker.");
    }

    public static TheoryData<string> Keys => [.. SectionsTheTabRenders];

    [Fact]
    public void TheMediaWidgetsNeedNoConnection()
    {
        // They gather the whole stack themselves. If one declared ProviderTypes it would
        // need binding, and the picker would hide it on a tab with no such connection —
        // which is the opposite of the point.
        var registry = Build();

        foreach (var key in SectionsTheTabRenders)
            Assert.False(registry.WidgetType(key)!.NeedsConnection,
                $"\"{key}\" wants a connection, so it cannot be dropped on a dashboard unbound.");
    }
}
