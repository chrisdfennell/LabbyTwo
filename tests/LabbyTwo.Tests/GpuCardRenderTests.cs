#pragma warning disable BL0006 // The interactive test renderer is a Renderer, which is the whole point of it.
using System.Net;
using LabbyTwo.Components.Widgets;
using LabbyTwo.Core;
using LabbyTwo.Providers;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LabbyTwo.Tests;

/// <summary>
/// The GPU card drawn by the real component from the monitor's live state. Stand-in Plex,
/// Tdarr, Tunarr and Intel GPU providers hand back what each test prepared, so what is under
/// test is the card's reading of the probes, not anybody's HTTP.
/// </summary>
public sealed class GpuCardRenderTests : IAsyncDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private InteractiveRenderer? _renderer;

    private sealed class Prepared(string type) : IConnectionProvider
    {
        public Dictionary<string, ProbeResult> Results { get; } = [];
        public string Type => type;
        public string DisplayName => type;
        public string Icon => "";
        public string Description => "";
        public IReadOnlyList<FieldSpec> Fields => [];

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
            Task.FromResult(Results[connection.Name]);
    }

    private readonly Dictionary<string, Prepared> _providers = new()
    {
        ["plex"] = new("plex"),
        ["tdarr"] = new("tdarr"),
        ["tunarr"] = new("tunarr"),
        [IntelGpuProvider.ProviderType] = new(IntelGpuProvider.ProviderType),
    };

    public GpuCardRenderTests()
    {
        Directory.CreateDirectory(_directory);
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        services.AddTestStorage(_directory, options => options.FailuresBeforeDown = 1);
        foreach (var provider in _providers.Values)
            services.AddSingleton<IConnectionProvider>(provider);
        services.AddSingleton<IEnumerable<IWidgetType>>([]);
        services.AddSingleton<IEnumerable<ITabKind>>([]);
        services.AddSingleton<Registry>();
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<AlertRuleStore>();
        services.AddSingleton<HistoryStore>();
        services.AddSingleton<HealthMonitor>();
        services.AddSingleton<DisplayUnits>();
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

    private InteractiveRenderer Renderer => _renderer ??= new InteractiveRenderer(_services);

    private async Task AddAsync(string provider, string name, ProbeResult result)
    {
        var connection = new Connection { Provider = provider, Name = name };
        await Get<ConfigStore>().SaveConnectionAsync(connection);
        _providers[provider].Results[name] = result;
        await Get<HealthMonitor>().RefreshAsync(connection);
    }

    private static ProbeResult Up(params (string Key, double Value)[] metrics) =>
        ProbeResult.Up(TimeSpan.Zero, "OK", metrics.ToDictionary(m => m.Key, m => m.Value));

    private Task RenderAsync(SettingsBag? settings = null) =>
        Renderer.RenderAsync<GpuCard>(new Dictionary<string, object?>
        {
            ["Context"] = new WidgetContext(new Widget { Type = "gpu", Settings = settings ?? new SettingsBag() }, null),
        });

    [Fact]
    public async Task Plex_and_Tdarr_on_the_GPU_together_get_a_warning_and_no_host_load_says_so()
    {
        await AddAsync("plex", "Plex", Up(("transcodes_hw", 2), ("transcodes_sw", 0)));
        await AddAsync("tdarr", "Tdarr", Up(("gpu_workers_active", 1)));
        await AddAsync("tunarr", "Tunarr", Up(("active_sessions", 0)));

        await RenderAsync();
        var html = WebUtility.HtmlDecode(await Renderer.WaitForAsync("may stutter"));

        Assert.Contains("2 apps on Quick Sync", html);
        Assert.Contains("2 hardware transcodes", html);
        Assert.Contains("1 GPU worker", html);
        Assert.Contains("nothing streaming", html);
        Assert.Contains("Tdarr is transcoding on the GPU while Plex has 2 hardware transcodes — Plex may stutter.", html);
        Assert.Contains(GpuSysfs.NotVisible, html);
    }

    [Fact]
    public async Task The_GPU_connection_adds_its_load_and_clock()
    {
        await AddAsync("plex", "Plex", Up(("transcodes_hw", 1)));
        await AddAsync(IntelGpuProvider.ProviderType, "iGPU", Up(("gpu_busy_percent", 63.4), ("freq_act_mhz", 1100), ("freq_max_mhz", 1350)));

        await RenderAsync();
        var html = WebUtility.HtmlDecode(await Renderer.WaitForAsync("63% busy"));

        Assert.Contains("GPU 63% busy", html);
        Assert.Contains("1100/1350 MHz", html);
        Assert.Contains("1 app on Quick Sync", html);
        Assert.DoesNotContain(GpuSysfs.NotVisible, html);
        Assert.DoesNotContain("may stutter", html);
    }

    [Fact]
    public async Task Idle_apps_can_be_hidden()
    {
        await AddAsync("plex", "Plex", Up(("transcodes_hw", 0)));
        await AddAsync("tdarr", "Tdarr box", Up(("gpu_workers_active", 2)));

        await RenderAsync(new SettingsBag { ["show_idle"] = "false" });
        var html = WebUtility.HtmlDecode(await Renderer.WaitForAsync("2 GPU workers"));

        Assert.Contains("Tdarr — Tdarr box", html);
        Assert.DoesNotContain("data-gpu-row=\"Plex\"", await Renderer.HtmlAsync());
    }

    [Fact]
    public async Task With_nothing_connected_it_says_what_to_connect()
    {
        await RenderAsync();
        var html = WebUtility.HtmlDecode(await Renderer.WaitForAsync("Nothing to show yet"));

        Assert.Contains("Connect Plex (or Tautulli), Tdarr or Tunarr", html);
    }
}
