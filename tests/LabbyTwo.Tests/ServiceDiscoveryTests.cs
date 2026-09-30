#pragma warning disable BL0006 // The interactive test renderer reads the render tree back, which is the whole point of it.
using System.Net;
using LabbyTwo.Components.Shared;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabbyTwo.Tests;

/// <summary>
/// "Add a connection for this": which containers are recognised, the address proposed for
/// each and why, what counts as already added, the ignore list, what the editor opens with,
/// and the whole path against a Docker that answers over real HTTP.
/// </summary>
public sealed class ServiceDiscoveryTests : IAsyncDisposable
{
    private const string SelfId = "5e1f5e1f5e1f5e1f5e1f5e1f5e1f5e1f5e1f5e1f5e1f5e1f5e1f5e1f5e1f5e1f";

    /// <summary>The providers a plugin brings. The table knows them; this build does not have them.</summary>
    private static readonly HashSet<string> PluginProviders = ["gluetun", "syncthing", "paperless"];

    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private InteractiveRenderer? _renderer;

    public ServiceDiscoveryTests()
    {
        Directory.CreateDirectory(_directory);
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        services.AddTestStorage(_directory);
        services.AddModules(typeof(Registry).Assembly, Path.Combine(_directory, "plugins"),
            LoggerFactory.Create(_ => { }).CreateLogger("test"));
        services.AddSingleton<Registry>();
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<Offload>();
        services.AddSingleton<ServiceDiscovery>();
        _services = services.BuildServiceProvider();
        Get<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (_renderer is not null)
            await _renderer.DisposeAsync();
        TestHost.Teardown(_services, _directory);
    }

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private Registry Registry => Get<Registry>();

    private static readonly Connection Docker = new()
    {
        Id = "docker1",
        Provider = "docker",
        Name = "NAS Docker",
        Settings = new SettingsBag { ["endpoint"] = "/var/run/docker.sock" },
    };

    private static ContainerRow Row(
        string name,
        string image,
        string[]? networks = null,
        string mode = "",
        DockerPort[]? ports = null,
        Dictionary<string, string>? labels = null,
        string? id = null,
        string gateway = "",
        string state = "running") =>
        new(id ?? (Guid.NewGuid().ToString("n") + Guid.NewGuid().ToString("n")), name, image, "sha256:0", state, "Up 2 hours",
            DateTimeOffset.UnixEpoch, ports ?? [], labels ?? new Dictionary<string, string>())
        {
            Networks = [.. (networks ?? []).Select(n => new DockerNetworkRef(n, gateway))],
            NetworkMode = mode,
        };

    private static ContainerRow Self(params string[] networks) =>
        Row("labbytwo", "ghcr.io/chrisdfennell/labbytwo:latest", networks, id: SelfId, gateway: "172.20.0.1");

    private IReadOnlyList<DiscoveredService> Discover(
        IReadOnlyList<ContainerRow> rows, IReadOnlyList<Connection>? connections = null, IReadOnlySet<string>? ignored = null,
        Connection? docker = null) =>
        ServiceDiscovery.Discover(docker ?? Docker, rows, connections ?? [], Registry.Provider, ignored ?? new HashSet<string>(), SelfId);

    // ---- matching ---------------------------------------------------------------------

    [Theory]
    [InlineData("lscr.io/linuxserver/sonarr:latest", "sonarr")]
    [InlineData("ghcr.io/hotio/sonarr:release-4.0.9", "sonarr")]
    [InlineData("linuxserver/radarr@sha256:0123456789abcdef", "radarr")]
    [InlineData("registry.local:5000/team/prowlarr:nightly", "prowlarr")]
    [InlineData("docker.io/library/nextcloud:29-apache", "nextcloud")]
    [InlineData("plexinc/pms-docker:public", "plex")]
    [InlineData("jellyfin/jellyfin", "jellyfin")]
    [InlineData("ghcr.io/immich-app/immich-server:release", "immich")]
    [InlineData("pihole/pihole:2024.07.0", "pihole")]
    [InlineData("louislam/uptime-kuma:1", "uptime-kuma")]
    [InlineData("ghcr.io/home-assistant/home-assistant:stable", "homeassistant")]
    [InlineData("ghcr.io/blakeblackshear/frigate:0.14.1", "frigate")]
    [InlineData("fallenbagel/jellyseerr:latest", "seerr")]
    [InlineData("lscr.io/linuxserver/qbittorrent:5.0.0", "qbittorrent")]
    [InlineData("qmcgaw/gluetun", "gluetun")]
    [InlineData("syncthing/syncthing:1.27", "syncthing")]
    public void ImagesAreRecognisedWhateverTheRegistryTagOrDigest(string image, string provider) =>
        Assert.Equal(provider, KnownServices.ForImage(image)?.Provider);

    [Theory]
    [InlineData("ghcr.io/immich-app/immich-machine-learning:release")]
    [InlineData("someone/sonarr-exporter:latest")]
    [InlineData("postgres:16")]
    [InlineData("redis")]
    [InlineData("")]
    public void LookalikesAndStrangersAreNot(string image) => Assert.Null(KnownServices.ForImage(image));

    [Fact]
    public void TheSameProductServedDifferentlyGetsItsOwnPortAndScheme()
    {
        var official = KnownServices.ForImage("nextcloud:29")!;
        var linuxserver = KnownServices.ForImage("lscr.io/linuxserver/nextcloud:latest")!;

        Assert.Equal(("http", 80), (official.Scheme, official.Port));
        Assert.Equal(("https", 443), (linuxserver.Scheme, linuxserver.Port));
    }

    [Fact]
    public void EveryBuiltInEntryNamesAnInstalledProviderWithAUrlField()
    {
        foreach (var service in KnownServices.All.Where(s => !PluginProviders.Contains(s.Provider)))
        {
            var provider = Registry.Provider(service.Provider);
            Assert.True(provider is not null, $"{service.Provider} is in the table but not installed");
            Assert.True(ServiceDiscovery.UrlField(provider) is not null, $"{service.Provider} has no URL field to fill in");
        }
    }

    [Fact]
    public void LabelsOverruleTheImage()
    {
        var labelled = Row("media-tool", "me/private-build:1", labels: new()
        {
            [KnownServices.ProviderLabel] = "sonarr",
            [KnownServices.PortLabel] = "9999",
        });

        var match = ServiceDiscovery.Match(labelled)!.Value;

        Assert.Equal(("sonarr", 9999), (match.Service.Provider, match.Service.Port));
        Assert.Contains("label", match.MatchedBy);
    }

    [Fact]
    public void AContainerCanOptOut()
    {
        Assert.Null(ServiceDiscovery.Match(Row("sonarr", "linuxserver/sonarr", labels: new() { [KnownServices.DiscoverLabel] = "false" })));
        Assert.Null(ServiceDiscovery.Match(Row("sonarr", "linuxserver/sonarr", labels: new() { [KnownServices.EnableLabel] = "False" })));
    }

    [Fact]
    public void AnImageListedOnlyByIdFallsBackToTheExactName()
    {
        var pulledSince = Row("radarr", "sha256:4b1c0f3a9d2e", ["media"]);
        var exporter = Row("radarr-exporter", "sha256:4b1c0f3a9d2e", ["media"]);

        Assert.Equal("radarr", ServiceDiscovery.Match(pulledSince)?.Service.Provider);
        Assert.Null(ServiceDiscovery.Match(exporter));
    }

    [Fact]
    public void LabbyTwoItselfAndUninstalledProvidersAreNeverOffered()
    {
        var self = Row("labbytwo", "linuxserver/sonarr", ["media"], id: SelfId);
        var gluetun = Row("gluetun", "qmcgaw/gluetun", ["media"]);

        Assert.Empty(Discover([self, gluetun]));
    }

    // ---- the proposed address ---------------------------------------------------------

    [Fact]
    public void ASharedNetworkProposesTheContainerName()
    {
        var sonarr = Row("sonarr", "lscr.io/linuxserver/sonarr", ["media_default"], ports: [new DockerPort("0.0.0.0", 8989, 8989, "tcp")]);

        var found = Assert.Single(Discover([Self("media_default", "bridge"), sonarr]));

        Assert.Equal("http://sonarr:8989", found.Address.Url);
        Assert.Equal(AddressKind.SharedNetwork, found.Address.Kind);
        Assert.Contains("media_default", found.Address.Why);
    }

    [Fact]
    public void TheDefaultBridgeIsNotASharedNetworkBecauseNamesDoNotResolveThere()
    {
        var sonarr = Row("sonarr", "linuxserver/sonarr", ["bridge"], ports: [new DockerPort("0.0.0.0", 8989, 8990, "tcp")]);

        var found = Assert.Single(Discover([Self("bridge"), sonarr]));

        // No shared DNS, so the published port on the host, reached through LabbyTwo's own gateway.
        Assert.Equal(AddressKind.PublishedPort, found.Address.Kind);
        Assert.Equal("http://172.20.0.1:8990", found.Address.Url);
        Assert.Contains("8990", found.Address.Why);
    }

    [Fact]
    public void ARemoteDockerHostIsWhereItsPublishedPortsAre()
    {
        var remote = Docker with { Settings = new SettingsBag { ["endpoint"] = "tcp://192.168.1.50:2375" } };
        var radarr = Row("radarr", "hotio/radarr", ["arr"], ports: [new DockerPort("0.0.0.0", 7878, 7878, "tcp")]);

        var found = Assert.Single(Discover([radarr], docker: remote));

        Assert.Equal("http://192.168.1.50:7878", found.Address.Url);
        Assert.Equal(AddressKind.PublishedPort, found.Address.Kind);
    }

    [Fact]
    public void ASocketProxyContainerIsNotMistakenForTheHost()
    {
        var proxied = Docker with { Settings = new SettingsBag { ["endpoint"] = "tcp://socket-proxy:2375" } };
        var proxy = Row("socket-proxy", "lscr.io/linuxserver/socket-proxy", ["proxy"]);
        var radarr = Row("radarr", "hotio/radarr", ["arr"], ports: [new DockerPort("0.0.0.0", 7878, 7878, "tcp")]);

        var found = Assert.Single(Discover([Self("proxy"), proxy, radarr], docker: proxied));

        Assert.Equal("http://172.20.0.1:7878", found.Address.Url);
    }

    [Fact]
    public void APortPublishedOnOneAddressIsProposedOnThatAddress()
    {
        var kuma = Row("uptime-kuma", "louislam/uptime-kuma:1", ["kuma"], ports: [new DockerPort("192.168.1.9", 3001, 3001, "tcp")]);

        Assert.Equal("http://192.168.1.9:3001", Assert.Single(Discover([Self("other"), kuma])).Address.Url);
    }

    [Fact]
    public void WithNoWayInTheNameIsStillProposedAndTheReasonSaysHowToFixIt()
    {
        var tautulli = Row("tautulli", "tautulli/tautulli", ["plex_net"]);

        var found = Assert.Single(Discover([Self("other"), tautulli]));

        Assert.Equal(AddressKind.Unreachable, found.Address.Kind);
        Assert.Equal("http://tautulli:8181", found.Address.Url);
        Assert.Contains("plex_net", found.Address.Why);
    }

    [Fact]
    public void AServiceBehindAGatewayContainerIsReachedAtTheGatewaysName()
    {
        var gluetun = Row("gluetun", "qmcgaw/gluetun", ["vpn"], ports: [new DockerPort("0.0.0.0", 8080, 8080, "tcp")]);
        var qbit = Row("qbittorrent", "lscr.io/linuxserver/qbittorrent", mode: "container:" + gluetun.Id);

        var found = Assert.Single(Discover([Self("vpn"), gluetun, qbit]));

        Assert.Equal("http://gluetun:8080", found.Address.Url);
        Assert.Same(gluetun, found.Owner);
        Assert.Contains("gluetun's network", found.Address.Why);
    }

    [Fact]
    public void HostNetworkingIsTheHostsAddressOnTheServicesOwnPort()
    {
        var plex = Row("plex", "lscr.io/linuxserver/plex", mode: "host");

        var found = Assert.Single(Discover([Self("media"), plex]));

        Assert.Equal(AddressKind.HostNetwork, found.Address.Kind);
        Assert.Equal("http://172.20.0.1:32400", found.Address.Url);
    }

    [Fact]
    public void OutsideDockerThePublishedPortIsOnLocalhost()
    {
        var sonarr = Row("sonarr", "linuxserver/sonarr", ["media"], ports: [new DockerPort("0.0.0.0", 8989, 8989, "tcp")]);

        // No row is LabbyTwo's own: it runs on the host itself.
        Assert.Equal("http://localhost:8989", Assert.Single(Discover([sonarr])).Address.Url);
    }

    [Fact]
    public void ALabelledUrlWinsOverEverything()
    {
        var sonarr = Row("sonarr", "linuxserver/sonarr", ["media"], labels: new() { [KnownServices.UrlLabel] = "https://sonarr.lab.example" });

        var found = Assert.Single(Discover([Self("media"), sonarr]));

        Assert.Equal(("https://sonarr.lab.example", AddressKind.Label), (found.Address.Url, found.Address.Kind));
    }

    // ---- already added ----------------------------------------------------------------

    private static Connection Existing(string provider, string url, string name = "Existing") => new()
    {
        Provider = provider,
        Name = name,
        Settings = new SettingsBag { ["url"] = url, ["api_key"] = "k" },
    };

    [Fact]
    public void AConnectionReachingTheContainerByNameIsAlreadyAdded()
    {
        var sonarr = Row("sonarr", "linuxserver/sonarr", ["media"]);

        var found = Assert.Single(Discover([Self("media"), sonarr], [Existing("sonarr", "http://sonarr:8989", "My Sonarr")]));

        Assert.True(found.AlreadyAdded);
        Assert.False(found.Pending);
        Assert.Equal("My Sonarr", Assert.Single(found.Existing).Name);
    }

    [Fact]
    public void AConnectionOnThePublishedHostAndPortIsAlreadyAdded()
    {
        var remote = Docker with { Settings = new SettingsBag { ["endpoint"] = "tcp://192.168.1.50:2375" } };
        var radarr = Row("radarr", "hotio/radarr", ["arr"], ports: [new DockerPort("0.0.0.0", 7878, 17878, "tcp")]);

        var added = Assert.Single(Discover([radarr], [Existing("radarr", "http://192.168.1.50:17878")], docker: remote));
        var elsewhere = Assert.Single(Discover([radarr], [Existing("radarr", "http://192.168.1.51:17878")], docker: remote));

        Assert.True(added.AlreadyAdded);
        Assert.False(elsewhere.AlreadyAdded);
    }

    [Fact]
    public void AnotherKindOfCheckIsMentionedButDoesNotHideTheIntegration()
    {
        var sonarr = Row("sonarr", "linuxserver/sonarr", ["media"]);

        var found = Assert.Single(Discover([Self("media"), sonarr], [Existing("http", "http://sonarr:8989", "Sonarr web")]));

        Assert.True(found.Pending);
        Assert.Equal("Sonarr web", Assert.Single(found.Others).Name);
    }

    [Fact]
    public void BehindAGatewayOnlyTheServicesOwnPortCounts()
    {
        var gluetun = Row("gluetun", "qmcgaw/gluetun", ["vpn"]);
        var qbit = Row("qbittorrent", "linuxserver/qbittorrent", mode: "container:gluetun");
        var sab = Row("sabnzbd", "linuxserver/sabnzbd", mode: "container:gluetun");

        var found = Discover([Self("vpn"), gluetun, qbit, sab], [Existing("qbittorrent", "http://gluetun:8080")]);

        Assert.True(found.Single(f => f.Container.Name == "qbittorrent").AlreadyAdded);
        // Same gateway, same port 8080 — but a SABnzbd connection is what would make SABnzbd added.
        Assert.False(found.Single(f => f.Container.Name == "sabnzbd").AlreadyAdded);
    }

    // ---- pre-filling the editor -------------------------------------------------------

    [Fact]
    public void TheDraftCarriesTheAddressAndANameThatSaysWhichOne()
    {
        var sonarr = Row("sonarr-4k", "linuxserver/sonarr", ["media"]);
        var found = Assert.Single(Discover([Self("media"), sonarr]));

        var draft = ServiceDiscovery.Draft(found, []);

        Assert.Equal("sonarr", draft.Provider);
        Assert.Equal("Sonarr (sonarr-4k)", draft.Name);
        Assert.Equal("http://sonarr-4k:8989", draft.Settings["url"]);
        Assert.False(draft.Settings.ContainsKey("api_key"));
        Assert.Contains("Settings → General", found.KeyHint);
        Assert.Contains("sonarr-4k", ServiceDiscovery.Intro(found));
    }

    [Fact]
    public void TheDraftTakesLabelsAndSitsBehindTheGatewaysConnection()
    {
        var gluetun = Row("gluetun", "qmcgaw/gluetun", ["vpn"]);
        var qbit = Row("qbittorrent", "linuxserver/qbittorrent", mode: "container:gluetun",
            labels: new() { [KnownServices.NameLabel] = "Torrents", [KnownServices.IconLabel] = "🧲" });
        var vpn = new Connection { Provider = "http", Name = "VPN", Settings = new SettingsBag { ["url"] = "http://gluetun:8000" } };

        var found = Assert.Single(Discover([Self("vpn"), gluetun, qbit], [vpn]));
        var draft = ServiceDiscovery.Draft(found, [vpn]);

        Assert.Equal(("Torrents", "🧲"), (draft.Name, draft.Icon));
        Assert.Equal(vpn.Id, draft.DependsOn);
    }

    [Fact]
    public void ThePlainNameIsTheProvidersOwn()
    {
        var found = Assert.Single(Discover([Self("home"), Row("home-assistant", "ghcr.io/home-assistant/home-assistant", ["home"])]));

        Assert.Equal("Home Assistant", ServiceDiscovery.Draft(found, []).Name);
    }

    // ---- remembering ------------------------------------------------------------------

    [Fact]
    public async Task IgnoringIsRememberedAndCanBeUndone()
    {
        var discovery = Get<ServiceDiscovery>();
        var sonarr = Row("sonarr", "linuxserver/sonarr", ["media"]);
        await Get<ConfigStore>().SaveConnectionAsync(Docker);
        discovery.Observe(Docker, [Self("media"), sonarr], DateTimeOffset.Now);
        var connections = await Get<ConfigStore>().ConnectionsAsync();

        var key = Assert.Single(discovery.Found(connections, await discovery.IgnoredAsync(), SelfId)).Key;
        await discovery.SetIgnoredAsync(key, true);

        // A second instance reads only what was stored, as after a restart.
        var restarted = new ServiceDiscovery(Get<ConfigStore>(), Registry, Get<AppSettingsStore>(), NullLogger<ServiceDiscovery>.Instance);
        restarted.Observe(Docker, [Self("media"), sonarr], DateTimeOffset.Now);
        var ignored = await restarted.IgnoredAsync();
        Assert.True(Assert.Single(restarted.Found(connections, ignored, SelfId)).Ignored);

        await restarted.SetIgnoredAsync(key, false);
        Assert.False(Assert.Single(restarted.Found(connections, await restarted.IgnoredAsync(), SelfId)).Ignored);
    }

    [Fact]
    public void ObservingTheSameListTwiceSaysNothingTheSecondTime()
    {
        var discovery = Get<ServiceDiscovery>();
        var changes = 0;
        discovery.Changed += () => changes++;
        var rows = new[] { Self("media"), Row("sonarr", "linuxserver/sonarr", ["media"]) };

        discovery.Observe(Docker, rows, DateTimeOffset.Now);
        discovery.Observe(Docker, [.. rows], DateTimeOffset.Now);
        Assert.Equal(1, changes);

        discovery.Observe(Docker, [.. rows, Row("radarr", "linuxserver/radarr", ["media"])], DateTimeOffset.Now);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task ADeletedDockerConnectionStopsOfferingItsContainers()
    {
        var discovery = Get<ServiceDiscovery>();
        discovery.Observe(Docker, [Self("media"), Row("sonarr", "linuxserver/sonarr", ["media"])], DateTimeOffset.Now);

        Assert.Empty(discovery.Found([], await discovery.IgnoredAsync(), SelfId));
    }

    // ---- against a Docker daemon ------------------------------------------------------

    private static object ListEntry(string id, string name, string image, string network, string mode, params object[] ports) => new
    {
        Id = id,
        Names = new[] { "/" + name },
        Image = image,
        ImageID = "sha256:" + new string('a', 64),
        State = "running",
        Status = "Up 3 hours",
        Created = 1_700_000_000,
        Ports = ports,
        Labels = new Dictionary<string, string> { ["com.docker.compose.service"] = name },
        HostConfig = new { NetworkMode = mode },
        NetworkSettings = new
        {
            Networks = network.Length == 0
                ? new Dictionary<string, object>()
                : new Dictionary<string, object> { [network] = new { Gateway = "172.30.0.1", IPAddress = "172.30.0.5" } },
        },
    };

    private static ScriptedDocker FakeDocker() => new(async context =>
    {
        if (context.Request.Url!.AbsolutePath.EndsWith("/containers/json", StringComparison.Ordinal))
        {
            await ScriptedDocker.Json(context, new object[]
            {
                ListEntry(SelfId, "labbytwo", "ghcr.io/chrisdfennell/labbytwo:latest", "media", "media"),
                ListEntry(new string('b', 64), "sonarr", "lscr.io/linuxserver/sonarr:4.0.9", "media", "media",
                    new { IP = "0.0.0.0", PrivatePort = 8989, PublicPort = 8989, Type = "tcp" }),
                ListEntry(new string('c', 64), "gluetun", "qmcgaw/gluetun:v3", "media", "media"),
                ListEntry(new string('d', 64), "qbittorrent", "lscr.io/linuxserver/qbittorrent:5", "", "container:" + new string('c', 64)),
                ListEntry(new string('e', 64), "postgres", "postgres:16", "media", "media"),
            });
        }
        else
        {
            await ScriptedDocker.Json(context, new { message = "not in this script" }, 404);
        }
    });

    [Fact]
    public void TheListCarriesNetworksAndNetworkMode()
    {
        var rows = DockerContainers.ParseList("""
            [{"Id":"abc","Names":["/qb"],"Image":"x","HostConfig":{"NetworkMode":"container:def"},
              "NetworkSettings":{"Networks":{}}},
             {"Id":"def","Names":["/vpn"],"Image":"y","HostConfig":{"NetworkMode":"vpn_default"},
              "NetworkSettings":{"Networks":{"vpn_default":{"Gateway":"172.18.0.1"}}}}]
            """);

        Assert.Equal("container:def", rows[0].NetworkMode);
        Assert.Empty(rows[0].Networks);
        Assert.Equal(new DockerNetworkRef("vpn_default", "172.18.0.1"), Assert.Single(rows[1].Networks));
    }

    [Fact]
    public async Task LookingForServicesAsksDockerOnceAndProposesWhatItCanReach()
    {
        using var docker = FakeDocker();
        var host = Docker with { Settings = new SettingsBag { ["endpoint"] = docker.Endpoint } };
        await Get<ConfigStore>().SaveConnectionAsync(host);
        var discovery = Get<ServiceDiscovery>();

        await discovery.LookAsync();
        var found = discovery.Found(await Get<ConfigStore>().ConnectionsAsync(), await discovery.IgnoredAsync(), SelfId);

        // Gluetun's provider is a plugin this build lacks, and Postgres is nobody's.
        Assert.Equal(["qbittorrent", "sonarr"], found.Select(f => f.Container.Name));
        Assert.Equal("http://sonarr:8989", found.Single(f => f.Container.Name == "sonarr").Address.Url);
        Assert.Equal("http://gluetun:8080", found.Single(f => f.Container.Name == "qbittorrent").Address.Url);
        Assert.Single(docker.Requests, r => r.Contains("/containers/json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnreachableHostSaysWhy()
    {
        var dead = Docker with { Settings = new SettingsBag { ["endpoint"] = "tcp://127.0.0.1:1", ["timeout"] = "2" } };
        await Get<ConfigStore>().SaveConnectionAsync(dead);
        var discovery = Get<ServiceDiscovery>();

        await discovery.LookAsync();

        Assert.False(string.IsNullOrEmpty(Assert.Single(discovery.Looks).Error));
    }

    // ---- drawn ------------------------------------------------------------------------

    private InteractiveRenderer Renderer => _renderer ??= new InteractiveRenderer(_services);

    [Fact]
    public async Task TheConnectionsPanelOffersAddAndIgnoreAndListsWhatIsAdded()
    {
        using var docker = FakeDocker();
        var host = Docker with { Settings = new SettingsBag { ["endpoint"] = docker.Endpoint } };
        await Get<ConfigStore>().SaveConnectionAsync(host);
        var existing = Existing("sonarr", "http://sonarr:8989", "My Sonarr");
        await Get<ConfigStore>().SaveConnectionAsync(existing);
        var discovery = Get<ServiceDiscovery>();
        await discovery.LookAsync();

        DiscoveredService? added = null;
        await Renderer.RenderAsync<DiscoveredServices>(new Dictionary<string, object?>
        {
            [nameof(DiscoveredServices.Connections)] = await Get<ConfigStore>().ConnectionsAsync(),
            [nameof(DiscoveredServices.SelfHint)] = SelfId,
            [nameof(DiscoveredServices.OnAdd)] = Microsoft.AspNetCore.Components.EventCallback.Factory
                .Create<DiscoveredService>(this, found => added = found),
        });

        var html = WebUtility.HtmlDecode(await Renderer.WaitForAsync("qBittorrent"));

        Assert.Contains("data-discovered=\"docker1|qbittorrent|qbittorrent\"", html);
        Assert.Contains("http://gluetun:8080", html);
        Assert.Contains("1 already added", html);
        Assert.Contains("already added as “My Sonarr”", html);
        Assert.DoesNotContain("data-discovered=\"docker1|sonarr|sonarr\"", html);
        Assert.Null(added);
    }

    [Fact]
    public async Task TheContainersHintNamesWhatIsMissingAndGoesAwayWhenDismissed()
    {
        var rows = new[]
        {
            Self("media"),
            Row("sonarr", "linuxserver/sonarr", ["media"]),
            Row("radarr", "linuxserver/radarr", ["media"]),
        };
        await Get<ConfigStore>().SaveConnectionAsync(Existing("radarr", "http://radarr:7878"));

        await Renderer.RenderAsync<DiscoveryHint>(new Dictionary<string, object?>
        {
            [nameof(DiscoveryHint.Docker)] = Docker,
            [nameof(DiscoveryHint.Rows)] = rows,
            [nameof(DiscoveryHint.SelfHint)] = SelfId,
        });

        var html = WebUtility.HtmlDecode(await Renderer.WaitForAsync("recognises"));
        Assert.Contains("recognises Sonarr here", html);
        Assert.DoesNotContain("Radarr", html);
        Assert.Contains("settings/connections?discover=true", html);

        // Dismissed for these services: a fresh render — another visit — does not show it.
        await Get<ServiceDiscovery>().DismissHintAsync(["docker1|sonarr|sonarr"]);
        await using var again = new InteractiveRenderer(_services);
        await again.RenderAsync<DiscoveryHint>(new Dictionary<string, object?>
        {
            [nameof(DiscoveryHint.Docker)] = Docker,
            [nameof(DiscoveryHint.Rows)] = rows,
            [nameof(DiscoveryHint.SelfHint)] = SelfId,
        });
        await Task.Delay(200);
        Assert.DoesNotContain("recognises", await again.HtmlAsync());
    }
}
