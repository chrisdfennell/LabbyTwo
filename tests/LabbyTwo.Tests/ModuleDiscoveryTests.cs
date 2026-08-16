using LabbyTwo.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LabbyTwo.Tests;

/// <summary>
/// Discovery is what replaced the hand-maintained list in Program.cs. If it silently
/// found nothing the app would still start — just with an empty picker — so these tests
/// exist to make that failure loud.
/// </summary>
public class ModuleDiscoveryTests
{
    private static (Registry Registry, ModuleCatalog Catalog) Build(string? pluginDirectory = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddTestStorage(TestHost.TempDirectory());
        var catalog = services.AddModules(
            typeof(Registry).Assembly,
            pluginDirectory ?? Path.Combine(Path.GetTempPath(), "labbytwo-no-plugins-" + Guid.NewGuid().ToString("n")),
            LoggerFactory.Create(_ => { }).CreateLogger("test"));
        services.AddSingleton<Registry>();
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<Registry>(), catalog);
    }

    [Fact]
    public void FindsEveryBuiltInProvider()
    {
        var (registry, _) = Build();

        // Named rather than counted: a count assertion passes when one provider is
        // accidentally swapped for another.
        string[] expected =
        [
            "http", "ping", "qnap", "ambient", "sonarr", "radarr",
            "plex", "json", "docker", "pihole", "webhook", "pushover",
            "proxmox", "truenas", "jellyfin", "qbittorrent",
            "homeassistant", "adguard", "nut", "unifi",
            "lidarr", "readarr", "prowlarr",
            "bazarr", "tautulli", "nzbget", "seerr", "ersatztv", "unmanic",
            "mypersonalgit", "gitea", "gitlab",
            "uptime-kuma", "speedtest-tracker", "speedtest", "immich", "nextcloud",
            "prometheus", "pbs", "duplicati", "scrutiny", "frigate", "tailscale", "synology",
            "healthchecks", "email", "ifttt",
            "cloudflare", "opnsense", "shelly", "forecast", "nws", "air-quality",
            "audiobookshelf", "navidrome",
            "sabnzbd", "transmission", "tdarr", "mylar3", "whisparr", "komga",
            "certificate", "github", "mqtt",
        ];

        Assert.Equal(
            [.. expected.Order()],
            [.. registry.Providers.Select(p => p.Type).Order()]);
    }

    [Fact]
    public void FindsWidgetsTabKindsAndImporters()
    {
        var (registry, _) = Build();

        Assert.Contains(registry.Widgets, w => w.Type == "service-tile");
        Assert.Contains(registry.Widgets, w => w.Type == "search");
        Assert.Contains(registry.Widgets, w => w.Type == "greeting");

        Assert.NotNull(registry.TabKind(TabKinds.Grid));
        Assert.NotNull(registry.TabKind(TabKinds.Embed));
        Assert.NotNull(registry.TabKind(TabKinds.Notes));
        Assert.NotNull(registry.TabKind(TabKinds.Status));
    }

    [Fact]
    public void EveryExtensionKeyIsUnique()
    {
        var (registry, _) = Build();

        // Registry silently lets a later registration win, which is what makes plugin
        // overrides work — so a collision between two built-ins would go unnoticed.
        Assert.Equal(registry.Providers.Count, registry.Providers.Select(p => p.Type).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(registry.Widgets.Count, registry.Widgets.Select(w => w.Type).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(registry.TabKinds.Count, registry.TabKinds.Select(k => k.Kind).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void EveryWidgetPointsAtAComponent()
    {
        var (registry, _) = Build();

        foreach (var widget in registry.Widgets)
        {
            Assert.True(
                typeof(Microsoft.AspNetCore.Components.IComponent).IsAssignableFrom(widget.Component),
                $"{widget.Type} points at {widget.Component.Name}, which is not a component.");
        }

        foreach (var kind in registry.TabKinds)
        {
            Assert.True(
                typeof(Microsoft.AspNetCore.Components.IComponent).IsAssignableFrom(kind.Component),
                $"{kind.Kind} points at {kind.Component.Name}, which is not a component.");
        }
    }

    [Fact]
    public void WidgetsOnlyNameProvidersThatExist()
    {
        var (registry, _) = Build();

        foreach (var widget in registry.Widgets)
        {
            foreach (var type in widget.ProviderTypes.Where(t => t is not ("*" or GitForges.Any)))
            {
                Assert.True(
                    registry.Provider(type) is not null,
                    $"Widget “{widget.Type}” binds to provider “{type}”, which is not registered.");
            }
        }

        // The capability wildcard gets the same guard, for the same reason: a widget asking
        // for a kind of provider that nothing implements offers an empty list, which on
        // screen is indistinguishable from having none configured.
        if (registry.Widgets.Any(w => w.ProviderTypes.Contains(GitForges.Any)))
        {
            Assert.Contains(registry.Providers, p => p is IGitForge);
        }
    }

    [Fact]
    public void MissingPluginDirectoryIsNotAnError()
    {
        var (registry, catalog) = Build(Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid().ToString("n")));

        Assert.Empty(catalog.Failures);
        Assert.Empty(catalog.Plugins);
        Assert.NotEmpty(registry.Providers);
    }

    [Fact]
    public void ADllThatIsNotAnAssemblyIsReportedRatherThanThrown()
    {
        var directory = Path.Combine(Path.GetTempPath(), "labbytwo-bad-plugin-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "not-really.dll"), "this is not a managed assembly");

        try
        {
            var (registry, catalog) = Build(directory);

            // The app must still come up with its own extensions intact.
            Assert.NotEmpty(registry.Providers);
            Assert.Single(catalog.Failures);
            Assert.Contains("not-really.dll", catalog.Failures[0].Path);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TheHostAssemblyIsListedAsAModule()
    {
        var (_, catalog) = Build();

        var host = Assert.Single(catalog.Modules, m => !m.IsPlugin);
        Assert.Equal("LabbyTwo", host.Name);
        Assert.True(host.TypeCount > 20, "The host module should list every built-in extension.");
    }

    private static ModuleInfo Plugin(string version, bool isPlugin = true) =>
        new("LabbyTwo.SomePlugin", version, "/plugins/some.dll", isPlugin, [], [], [], [], [], []);

    /// <summary>
    /// The whole point of stamping. A plugin built against another LabbyTwo half-loads —
    /// the types that still resolve are kept and the rest are dropped — so this has to be
    /// said while everything still appears to work, not at the render that finally throws.
    /// </summary>
    [Fact]
    public void APluginBuiltForAnotherVersionIsFlagged()
    {
        var catalog = new ModuleCatalog { HostVersion = "v1.3.0" };

        Assert.True(catalog.BuiltForAnother(Plugin("v1.1.0")));
        Assert.False(catalog.BuiltForAnother(Plugin("v1.3.0")));

        // Case only, which is the same build by any sane reading.
        Assert.False(catalog.BuiltForAnother(Plugin("V1.3.0")));
    }

    /// <summary>
    /// An unstamped build is the normal state of a local <c>dotnet run</c> and of a plugin
    /// somebody compiled themselves. Treating "I do not know" as "these differ" would put
    /// a warning on every development machine, which is how warnings stop being read.
    /// </summary>
    [Fact]
    public void NothingIsFlaggedWhenEitherSideIsUnstamped()
    {
        Assert.False(new ModuleCatalog { HostVersion = "" }.BuiltForAnother(Plugin("v1.1.0")));
        Assert.False(new ModuleCatalog { HostVersion = "v1.3.0" }.BuiltForAnother(Plugin("")));
    }

    /// <summary>The host is not a plugin, and cannot be built for a different itself.</summary>
    [Fact]
    public void TheHostIsNeverFlagged()
    {
        var catalog = new ModuleCatalog { HostVersion = "v1.3.0" };
        Assert.False(catalog.BuiltForAnother(Plugin("v1.1.0", isPlugin: false)));
    }

    /// <summary>
    /// The Git cards used to name one provider, which meant a second Git server could be
    /// monitored and charted and still not appear on any of them. They now ask for a
    /// capability, so this checks the capability is actually declared by everything that
    /// should have it — a forge that forgets the interface fails silently and invisibly.
    /// </summary>
    [Fact]
    public void EveryGitServerIsAForge()
    {
        var (registry, _) = Build();

        foreach (var type in (string[])["mypersonalgit", "gitea", "gitlab"])
            Assert.True(registry.IsForge(type), $"{type} should implement IGitForge.");

        Assert.False(registry.IsForge("plex"));
        Assert.False(registry.IsForge("nonsense"));
    }

    /// <summary>
    /// And that the wildcard reaches them. This is the half that would break silently: a
    /// widget naming a capability nothing resolves simply offers no connections, which
    /// looks exactly like having none configured.
    /// </summary>
    [Fact]
    public void TheGitCardsAcceptEveryForge()
    {
        var (registry, _) = Build();

        foreach (var key in (string[])["git-summary", "git-repos", "git-activity"])
        {
            var widget = Assert.Single(registry.Widgets, w => w.Type == key);

            Assert.True(registry.Accepts(widget, "mypersonalgit"), $"{key} should accept MyPersonalGit.");
            Assert.True(registry.Accepts(widget, "gitea"), $"{key} should accept Gitea.");
            Assert.True(registry.Accepts(widget, "gitlab"), $"{key} should accept GitLab.");

            // Still narrow: the wildcard means "any forge", not "anything at all".
            Assert.False(registry.Accepts(widget, "plex"), $"{key} should not accept Plex.");
        }
    }

    /// <summary>GitLab calls them merge requests, and the page should use the server's word.</summary>
    [Fact]
    public void EachForgeNamesItsOwnKindOfRequest()
    {
        var (registry, _) = Build();

        Assert.Equal("merge request", ((IGitForge)registry.Provider("gitlab")!).PullNoun);
        Assert.Equal("pull request", ((IGitForge)registry.Provider("gitea")!).PullNoun);
        Assert.Equal("pull requests", ((IGitForge)registry.Provider("mypersonalgit")!).PullNounPlural);
    }
}
