#pragma warning disable BL0006 // The interactive test renderer is a Renderer, which is the whole point of it.
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using LabbyTwo.Components.Layout;
using LabbyTwo.Components.Shared;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace LabbyTwo.Tests;

/// <summary>
/// The sidebar's groups, the settings hub's sections and the search over them — and the
/// promise that came with moving everything: every link that worked before still goes
/// somewhere, and the menu that is on every page still asks the database nothing.
/// </summary>
public sealed partial class NavigationTests : IAsyncDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private ServiceProvider? _services;
    private InteractiveRenderer? _renderer;
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    public async ValueTask DisposeAsync()
    {
        if (_renderer is not null)
            await _renderer.DisposeAsync();
        foreach (var factory in _factories)
            await factory.DisposeAsync();
        if (_services is not null)
            TestHost.Teardown(_services, _directory);
        else
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // Never created, or SQLite's pool holding a file a moment longer; a temp
                // folder left behind is harmless.
            }
        }
    }

    // ---- every old link still goes somewhere ---------------------------------------------

    /// <summary>Every settings link the app, the README or a bookmark used before the regroup.</summary>
    public static TheoryData<string> OldLinks =>
    [
        "settings",
        "settings/health",
        "settings/health?pause=tdarr#host-pressure",
        "settings/health#host-pressure",
        "settings/appearance",
        "settings/appearance?safe=1",
        "settings/appearance#day-night",
        "settings/appearance#custom-css",
        "settings/appearance/themes/nord",
        "settings/alerts",
        "settings/storage",
        "settings/scheduled",
        "settings/connections",
        "settings/connections?discover=true",
        "settings/connections?edit=plex",
        "settings/connections/map",
        "settings/tabs",
        "settings/push",
        "settings/import",
    ];

    [Theory]
    [MemberData(nameof(OldLinks))]
    public void EveryOldSettingsLinkStillHasAPage(string link) =>
        Assert.True(HasPage(link), $"Nothing answers {link} any more.");

    [Theory]
    [InlineData("settings#family", "settings/integrations#family")]
    [InlineData("settings#home-assistant", "settings/integrations#home-assistant")]
    public void ACardThatMovedOffTheOldPageIsFoundByItsOldAnchor(string link, string now)
    {
        var fragment = link[link.IndexOf('#')..];
        Assert.Equal(now, SettingsMap.MovedAnchor(fragment));
        Assert.True(HasPage(now));
    }

    [Fact]
    public void AnAnchorThatNeverMovedIsLeftAlone() => Assert.Null(SettingsMap.MovedAnchor("#nothing-here"));

    [Fact]
    public void EveryLinkInTheMapsHasAPage()
    {
        var links = SettingsMap.Sections.SelectMany(s => s.Routes.Concat(s.Entries.Select(e => e.Url)))
            .Concat(NavMap.Items.Select(i => i.Href))
            .Concat(SettingsMap.MovedAnchors.Values);
        foreach (var link in links)
            Assert.True(HasPage(link), $"{link} is in a map but nothing answers it.");
    }

    [Fact]
    public void EveryAnchorTheMapsPointAtIsOnAPage()
    {
        // The search sends you to a card by its id; an id nobody renders lands you at the top.
        var ids = Directory.EnumerateFiles(Path.Combine(Root, "Components"), "*.razor", SearchOption.AllDirectories)
            .SelectMany(f => IdAttribute().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);
        var anchors = SettingsMap.Sections.SelectMany(s => s.Entries.Select(e => e.Url))
            .Concat(SettingsMap.MovedAnchors.Values)
            .Where(u => u.Contains('#'))
            .Select(u => u[(u.IndexOf('#') + 1)..]);
        foreach (var anchor in anchors)
            Assert.True(ids.Contains(anchor), $"Nothing renders id=\"{anchor}\".");
    }

    [Fact]
    public void TheFamilyNoticeLinksToWhereTheFamilyCardIsNow() =>
        Assert.Contains("settings/integrations#family", File.ReadAllText(Path.Combine(Root, "Services", "FamilyStatus.cs")));

    [Fact]
    public void SectionsHaveTheirOwnRoutesAndNamesThatDoNotRepeat()
    {
        Assert.Equal(SettingsMap.Sections.Count, SettingsMap.Sections.Select(s => s.Key).Distinct().Count());
        Assert.Equal(SettingsMap.Sections.Count, SettingsMap.Sections.Select(s => s.Route).Distinct().Count());
        Assert.All(SettingsMap.Sections, s => Assert.StartsWith("settings/", s.Route));
    }

    [Theory]
    [InlineData("settings/connections/map", "connections")]
    [InlineData("settings/tabs", "connections")]
    [InlineData("settings/appearance/themes/nord", "appearance")]
    [InlineData("settings/health?pause=x#host-pressure", "system")]
    [InlineData("settings/scheduled", "automation")]
    [InlineData("settings/push", "notifications")]
    [InlineData("settings/storage", "data")]
    [InlineData("settings/general", "general")]
    public void APageIsFiledUnderItsSection(string path, string section) =>
        Assert.Equal(section, SettingsMap.SectionFor(path)?.Key);

    [Fact]
    public void TheHubIsInNoSection() => Assert.Null(SettingsMap.SectionFor("settings"));

    // ---- the search ------------------------------------------------------------------------

    [Fact]
    public void TheSearchFindsASettingByName()
    {
        var hit = SettingsMap.Search("quiet").First();
        Assert.Equal("Quiet hours", hit.Title);
        Assert.Equal("settings/alerts#quiet-hours", hit.Url);
        Assert.Equal("Alerts", hit.Section);
    }

    [Fact]
    public void TheSearchFindsASettingByAKeyword() =>
        Assert.Contains(SettingsMap.Search("mqtt"), h => h.Url == "settings/integrations#home-assistant");

    [Fact]
    public void EveryWordHasToMatch()
    {
        var hits = SettingsMap.Search("safe updates");
        Assert.Contains(hits, h => h.Url == "settings/automation#safe-updates");
        Assert.DoesNotContain(hits, h => h.Title == "Quiet hours");
    }

    [Fact]
    public void ASectionIsFoundByItsName() =>
        Assert.Equal("settings/integrations", SettingsMap.Search("Integrations").First().Url);

    [Fact]
    public void APageHiddenFromTheMenuIsStillFound() =>
        Assert.Contains(SettingsMap.Search("power"), h => h.Url == "power");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("zzzzqqq")]
    public void NothingTypedOrNothingMatchingFindsNothing(string query) => Assert.Empty(SettingsMap.Search(query));

    [Fact]
    public void TheSameSettingIsNotListedTwice()
    {
        var hits = SettingsMap.Search("units");
        Assert.Equal(hits.Count, hits.Select(h => h.Title + h.Url).Distinct().Count());
    }

    // ---- hiding what you do not use ------------------------------------------------------

    [Fact]
    public void HiddenPagesAreReadBackAndUnknownOnesDropped()
    {
        var hidden = NavMap.Hidden(new SettingsBag { [NavMap.HiddenKey] = "power, backups,nonsense" });
        Assert.Equal(["backups", "power"], hidden.Order());
    }

    [Fact]
    public void HiddenPagesAreStoredInMenuOrder() =>
        Assert.Equal("power,backups", NavMap.Store(["backups", "power", "nonsense"]));

    [Fact]
    public void AGroupWithEveryPageHiddenIsLeftOut()
    {
        var visible = NavMap.Visible(new HashSet<string> { "power", "backups", "map", "logs" });
        Assert.DoesNotContain(visible, g => g.Key == "lab");
        Assert.DoesNotContain(visible.Single(g => g.Key == "monitor").Items, i => i.Key == "logs");
    }

    [Fact]
    public async Task TickingAPageOffInTheMenuCardHidesIt()
    {
        await StartAsync();
        await _renderer!.RenderAsync<MenuSettings>(new Dictionary<string, object?>());
        await _renderer.WaitForAsync("menu-show-power");

        await _renderer.ChangeAsync("menu-show-power", false);
        await _renderer.ChangeAsync("menu-show-backups", false);
        Assert.Equal("power,backups", await Get<AppSettingsStore>().GetAsync(NavMap.HiddenKey));

        await _renderer.ChangeAsync("menu-show-power", true);
        Assert.Equal("backups", await Get<AppSettingsStore>().GetAsync(NavMap.HiddenKey));
    }

    // ---- the sidebar -------------------------------------------------------------------------

    [Fact]
    public async Task TheMenuDrawsItsGroupsOpenWithTheirPages()
    {
        await StartAsync();
        var html = await RenderNavAsync();

        foreach (var group in NavMap.Groups)
        {
            Assert.Contains($"aria-controls=\"nav-group-{group.Key}\"", html);
            Assert.Contains($">{WebUtility.HtmlEncode(group.Label)}</span>", html);
        }
        Assert.Contains("aria-expanded=\"true\"", html);
        Assert.DoesNotContain("aria-expanded=\"false\"", html);
        Assert.Contains("href=\"power\"", html);
        Assert.Contains("href=\"wall\"", html);
        Assert.Contains("href=\"m\"", html);
        Assert.Contains("href=\"settings\"", html);
        // Settings is one link now; its sections are in the hub.
        Assert.DoesNotContain("href=\"settings/appearance\"", html);
        Assert.DoesNotContain("href=\"settings/tabs\"", html);
    }

    [Fact]
    public async Task FoldingAGroupHidesItsPagesAndSaysSo()
    {
        await StartAsync();
        await RenderNavAsync();

        await _renderer!.ClickAsync("Lab");
        var html = await _renderer.HtmlAsync();
        Assert.Matches("aria-controls=\"nav-group-lab\"[^>]*aria-expanded=\"false\"|aria-expanded=\"false\"[^>]*aria-controls=\"nav-group-lab\"", html);
        Assert.Matches("id=\"nav-group-lab\" hidden", html);
    }

    [Fact]
    public async Task APageHiddenInSettingsLeavesTheMenu()
    {
        await StartAsync();
        await Get<AppSettingsStore>().SaveAsync(NavMap.HiddenKey, "power,backups,map");
        var html = await RenderNavAsync();

        Assert.DoesNotContain("href=\"power\"", html);
        Assert.DoesNotContain("href=\"backups\"", html);
        Assert.DoesNotContain("nav-group-lab", html);
        Assert.Contains("href=\"incidents\"", html);
    }

    [Fact]
    public async Task TheOutageAndFamilyBadgesAreStillAtTheTop()
    {
        await StartAsync();
        var down = new Connection { Provider = "down", Name = "NAS" };
        await Get<ConfigStore>().SaveConnectionAsync(down);
        await Get<HealthMonitor>().RefreshAsync(down);
        await Get<FamilyReportStore>().AddAsync(new FamilyReport(0, DateTimeOffset.Now, "i1", null, "Plex", "no picture", "Sam"));

        await _renderer!.RenderAsync<NavMenu>(new Dictionary<string, object?>());
        var html = WebUtility.HtmlDecode(await _renderer.WaitForAsync("from the family"));

        Assert.Contains("1 service is down", html);
        Assert.Contains("<a class=\"nav-alert\" href=\"settings/connections\">", html);
        Assert.Contains("<a class=\"nav-alert\" href=\"settings/integrations#family\">", html);
        Assert.True(html.IndexOf("service is down", StringComparison.Ordinal) < html.IndexOf("nav-group", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheMenuAsksTheDatabaseNothingOnceItsCachesAreWarm()
    {
        await StartAsync();
        await Get<AppSettingsStore>().SaveAsync(NavMap.HiddenKey, "power");
        // What any earlier page has already read.
        await Get<ConfigStore>().TabsAsync();
        await Get<AppSettingsStore>().AllAsync();
        await Get<FamilyReportStore>().CountAsync();

        var before = Get<Db>().Opens;
        var html = await RenderNavAsync();

        Assert.DoesNotContain("href=\"power\"", html);
        Assert.Equal(before, Get<Db>().Opens);
    }

    // ---- the hub -------------------------------------------------------------------------

    [Fact]
    public async Task TheSubNavigationListsEverySectionAndItsPages()
    {
        await StartAsync("http://labby.test/settings/alerts");
        await _renderer!.RenderAsync<SettingsNav>(new Dictionary<string, object?>());
        var html = WebUtility.HtmlDecode(await _renderer.HtmlAsync());

        foreach (var section in SettingsMap.Sections)
        {
            Assert.Contains($"href=\"{section.Route}\"", html);
            Assert.Contains(section.Title, html);
            foreach (var page in section.Pages)
                Assert.Contains($"href=\"{page.Route}\"", html);
        }
        Assert.Contains("<li class=\"is-current\">", html);
        Assert.Contains("for=\"settings-search\"", html);
    }

    [Fact]
    public async Task TypingInTheSearchNarrowsTheListToWhatMatches()
    {
        await StartAsync("http://labby.test/settings");
        await _renderer!.RenderAsync<SettingsNav>(new Dictionary<string, object?>());

        await _renderer.ChangeAsync("settings-search", "mute", "oninput");
        var html = WebUtility.HtmlDecode(await _renderer.HtmlAsync());
        Assert.Contains("href=\"settings/alerts#mute-windows\"", html);
        Assert.DoesNotContain("href=\"settings/general\"", html);
        Assert.Contains("match.", html);

        await _renderer.ChangeAsync("settings-search", "zzzzqqq", "oninput");
        Assert.Contains("Nothing matches", WebUtility.HtmlDecode(await _renderer.HtmlAsync()));

        await _renderer.ChangeAsync("settings-search", "", "oninput");
        Assert.Contains("href=\"settings/general\"", await _renderer.HtmlAsync());
    }

    [Fact]
    public async Task AnOldLinkToTheFamilyCardIsSentToItsNewSection()
    {
        await StartAsync("http://labby.test/settings#family");
        await _renderer!.RenderAsync<LabbyTwo.Components.Pages.Settings>(new Dictionary<string, object?>());

        Assert.Equal("http://labby.test/settings/integrations#family", Get<NavigationManager>().Uri);
    }

    [Fact]
    public async Task TheHubListsEverySection()
    {
        await StartAsync("http://labby.test/settings");
        await _renderer!.RenderAsync<LabbyTwo.Components.Pages.Settings>(new Dictionary<string, object?>());
        var html = WebUtility.HtmlDecode(await _renderer.HtmlAsync());

        foreach (var section in SettingsMap.Sections)
            Assert.Contains($"href=\"{section.Route}\"", html);
        Assert.Equal("http://labby.test/settings", Get<NavigationManager>().Uri);
    }

    /// <summary>Every section, served by the real app, inside the hub with its own heading.</summary>
    public static TheoryData<string> SectionRoutes => [.. SettingsMap.Sections.Select(s => s.Route)];

    [Theory]
    [MemberData(nameof(SectionRoutes))]
    public async Task EverySectionRendersInsideTheHub(string route)
    {
        var client = StartApp();
        var section = SettingsMap.Sections.Single(s => s.Route == route);

        var response = await client.GetAsync("/" + route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        // A section that is a page it always was (Connections, Alerts) keeps that page's own
        // heading; the new ones are headed by the section's name.
        Assert.Contains($"<h1>{section.Icon} ", html);
        Assert.Matches($"<li class=\"is-current\">\\s*<a [^>]*href=\"{Regex.Escape(route)}\"", html);
        Assert.Contains("class=\"settings-nav", html);
        Assert.Contains("id=\"settings-search\"", html);
        Assert.Contains("nav-group-monitor", html);
    }

    [Theory]
    [MemberData(nameof(OldLinks))]
    public async Task EveryOldSettingsLinkStillOpens(string link)
    {
        var client = StartApp();
        var path = "/" + (link.Contains('#') ? link[..link.IndexOf('#')] : link);

        var response = await client.GetAsync(path);
        // ?safe=1 may be answered by a redirect that sets the cookie, as it always was.
        if (response.StatusCode == HttpStatusCode.Redirect && link.Contains("safe=1"))
            return;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("class=\"settings-nav", await response.Content.ReadAsStringAsync());
    }

    // ---- plumbing ------------------------------------------------------------------------

    private HttpClient StartApp()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseSetting("Labby:DatabasePath", Path.Combine(_directory, Guid.NewGuid().ToString("n"), "labbytwo.db"));
            host.UseSetting("Labby:PluginPath", Path.Combine(_directory, "plugins"));
        });
        _factories.Add(factory);
        return factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation(string uri) => Initialize("http://labby.test/", uri);

        protected override void NavigateToCore(string uri, NavigationOptions options) => Uri = ToAbsoluteUri(uri).ToString();
    }

    private sealed class NoScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new JSException("No browser in a test.");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new JSException("No browser in a test.");
    }

    private sealed class DownProvider : IConnectionProvider
    {
        public string Type => "down";
        public string DisplayName => "Always down";
        public string Icon => "";
        public string Description => "";
        public IReadOnlyList<FieldSpec> Fields => [];

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
            Task.FromResult(ProbeResult.Down(TimeSpan.Zero, "refused"));
    }

    private async Task StartAsync(string address = "http://labby.test/")
    {
        Directory.CreateDirectory(_directory);
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        services.AddTestStorage(_directory, options => options.FailuresBeforeDown = 1);
        services.AddSingleton<IConnectionProvider, DownProvider>();
        services.AddSingleton<IEnumerable<IWidgetType>>([]);
        services.AddSingleton<IEnumerable<ITabKind>>([]);
        services.AddSingleton<Registry>();
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<HistoryStore>();
        services.AddSingleton<HealthMonitor>();
        services.AddSingleton<FamilyReportStore>();
        services.AddSingleton<IJSRuntime, NoScript>();
        services.AddSingleton<NavigationManager>(new TestNavigation(address));
        _services = services.BuildServiceProvider();
        await Get<Db>().EnsureSchemaAsync();
        _renderer = new InteractiveRenderer(_services);
    }

    private async Task<string> RenderNavAsync()
    {
        await _renderer!.RenderAsync<NavMenu>(new Dictionary<string, object?>());
        return await _renderer.WaitForAsync(html => html.Contains("nav-footer", StringComparison.Ordinal));
    }

    private T Get<T>() where T : notnull => _services!.GetRequiredService<T>();

    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LabbyTwo.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not find the repository root.");
    }

    [GeneratedRegex("\\bid=\"([a-z0-9-]+)\"")]
    private static partial Regex IdAttribute();

    /// <summary>Every route template a page declares, as segments.</summary>
    private static readonly IReadOnlyList<string[]> Routes =
    [
        .. typeof(Program).Assembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>())
            .Select(r => r.Template.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
    ];

    /// <summary>Whether a page answers this relative link, ignoring its query and fragment.</summary>
    private static bool HasPage(string link)
    {
        var path = link;
        var cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0)
            path = path[..cut];
        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return Routes.Any(route => route.Length == segments.Length
            && route.Zip(segments).All(p => p.First.StartsWith('{') || string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase)));
    }
}
