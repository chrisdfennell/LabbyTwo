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
            "mypersonalgit",
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
            foreach (var type in widget.ProviderTypes.Where(t => t != "*"))
            {
                Assert.True(
                    registry.Provider(type) is not null,
                    $"Widget “{widget.Type}” binds to provider “{type}”, which is not registered.");
            }
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
}
