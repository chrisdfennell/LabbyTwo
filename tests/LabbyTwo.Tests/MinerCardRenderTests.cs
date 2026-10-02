#pragma warning disable BL0006 // The interactive test renderer is a Renderer, which is the whole point of it.
using System.Net;
using System.Text.Json;
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
/// The Miner and Miners cards drawn by the real components from the monitor's live state.
/// The readings are made by the real provider's arithmetic from the real NMMiner and AxeOS
/// payloads; only the HTTP is skipped, by a stand-in "miner" provider that hands back what
/// the test prepared.
/// </summary>
public sealed class MinerCardRenderTests : IAsyncDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private InteractiveRenderer? _renderer;

    /// <summary>Answers each connection's probe with whatever the test set for its name.</summary>
    private sealed class Prepared : IConnectionProvider
    {
        public Dictionary<string, ProbeResult> Results { get; } = [];
        public string Type => "miner";
        public string DisplayName => "Miner";
        public string Icon => "⛏️";
        public string Description => "";
        public IReadOnlyList<FieldSpec> Fields => [];
        public IReadOnlyList<MetricSpec> Metrics => new MinerProvider(new NoHttp()).Metrics;

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
            Task.FromResult(Results[connection.Name]);
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No requests in a render test.");
    }

    public MinerCardRenderTests()
    {
        Directory.CreateDirectory(_directory);
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        // One failure is down, so an offline miner does not take two probes to show.
        services.AddTestStorage(_directory, options => options.FailuresBeforeDown = 1);
        services.AddSingleton<Prepared>();
        services.AddSingleton<IConnectionProvider>(sp => sp.GetRequiredService<Prepared>());
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

    private static MinerReading Reading(string json)
    {
        using var document = JsonDocument.Parse(json);
        return MinerReading.Parse(document.RootElement)!;
    }

    /// <summary>A miner connection whose last probe is <paramref name="result"/>.</summary>
    private async Task<Connection> MinerAsync(string name, ProbeResult result)
    {
        var connection = new Connection { Provider = "miner", Name = name, Icon = "⛏️" };
        await Get<ConfigStore>().SaveConnectionAsync(connection);
        Get<Prepared>().Results[name] = result;
        await Get<HealthMonitor>().RefreshAsync(connection);
        return connection;
    }

    /// <summary>The NMMiner after two readings ten minutes apart: 60 shares at 0.002, so ≈859 KH/s from shares.</summary>
    private static ProbeResult NmMinerWithRate(string name)
    {
        var provider = new MinerProvider(new NoHttp());
        var connection = new Connection { Provider = "miner", Name = name };
        var at = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        provider.Read(connection, Reading(MinerProviderTests.NmMinerPayload(accepted: 1000, uptime: 5000)), TimeSpan.Zero, at);
        return provider.Read(connection, Reading(MinerProviderTests.NmMinerPayload(accepted: 1060, uptime: 5600)),
            TimeSpan.FromMilliseconds(40), at.AddMinutes(10));
    }

    private static ProbeResult Bitaxe() =>
        new MinerProvider(new NoHttp()).Read(new Connection { Provider = "miner", Name = "Bitaxe" },
            Reading(MinerProviderTests.BitaxePayload), TimeSpan.FromMilliseconds(20), DateTimeOffset.UtcNow);

    private Task RenderAsync<T>(Connection? connection, SettingsBag? settings = null) where T : Microsoft.AspNetCore.Components.IComponent =>
        Renderer.RenderAsync<T>(new Dictionary<string, object?>
        {
            ["Context"] = new WidgetContext(new Widget { Type = "miner", Settings = settings ?? new SettingsBag() }, connection),
        });

    [Fact]
    public async Task A_miner_reporting_zero_leads_with_the_estimate_from_shares()
    {
        var connection = await MinerAsync("NMMiner 1", NmMinerWithRate("NMMiner 1"));

        await RenderAsync<MinerCard>(connection);
        var html = WebUtility.HtmlDecode(await Renderer.WaitForAsync("859 KH/s"));

        Assert.Contains("NMMiner 1", html);
        Assert.Contains("859 KH/s", html);
        Assert.Contains("estimated from accepted shares", html);
        Assert.Contains("shares/hour", html);
        Assert.Contains("360", html);
        Assert.Contains(1060.ToString("N0"), html);
        Assert.Contains("827.47", html);
        Assert.Contains("306.59 since boot", html);
        Assert.Contains("solobtc.nmminer.com:3333", html);
        Assert.Contains(MinerProviderTests.MaskedWorker, html);
        Assert.DoesNotContain(MinerProviderTests.Wallet, html);
        Assert.Contains("📶 -51 dBm", html);
        Assert.Contains("status-up", html);
    }

    [Fact]
    public async Task A_bitaxe_shows_its_own_hashrate_and_its_hardware()
    {
        var connection = await MinerAsync("Bitaxe", Bitaxe());

        await RenderAsync<MinerCard>(connection, new SettingsBag { ["show_details"] = "false" });
        var html = WebUtility.HtmlDecode(await Renderer.WaitForAsync("512 GH/s"));

        Assert.Contains("512 GH/s", html);
        Assert.Contains("reported by the miner", html);
        Assert.Contains("4.29G", html);
        Assert.Contains("⚡ 14.2 W", html);
        Assert.Contains($"🌀 {4200:N0} rpm", html);
        // Imperial by default: the Bitaxe's 58.5 °C is shown as the reader asked.
        Assert.Contains("°F", html);
        Assert.DoesNotContain("Firmware", html);
        Assert.DoesNotContain(MinerProviderTests.BitaxeWallet, html);
    }

    [Fact]
    public async Task An_offline_miner_says_so()
    {
        var connection = await MinerAsync("NMMiner 2", ProbeResult.Down(TimeSpan.Zero, "Timed out — nothing answered."));

        await RenderAsync<MinerCard>(connection);
        var html = WebUtility.HtmlDecode(await Renderer.WaitForAsync("Offline"));

        Assert.Contains("Offline — Timed out", html);
        Assert.Contains("status-down", html);
    }

    [Fact]
    public async Task The_summary_adds_up_every_miner_and_names_the_offline_ones()
    {
        await MinerAsync("NMMiner 1", NmMinerWithRate("NMMiner 1"));
        await MinerAsync("Bitaxe", Bitaxe());
        await MinerAsync("NMMiner 2", ProbeResult.Down(TimeSpan.Zero, "Connection refused."));

        await RenderAsync<MinersCard>(null);
        var html = WebUtility.HtmlDecode(await Renderer.WaitForAsync("Offline:"));

        // 512.34 GH/s from the Bitaxe plus 0.000859 from the NMMiner; the offline one adds nothing.
        Assert.Contains(MinerUnits.Hashrate(512.34 + 60 / 600.0 * 0.002 * 4294967296 / 1e9), html);
        Assert.Contains("across 3 miners", html);
        Assert.Contains("partly estimated from shares", html);
        Assert.Contains("⚠️ Offline: NMMiner 2", html);
        // The best ever across them is the Bitaxe's 4.29G, not the NMMiner's 827.47.
        Assert.Contains("4.29G", html);
        Assert.Contains("best difficulty ever · Bitaxe", html);
        // Only the NMMiner has a share rate yet: the Bitaxe has had one probe.
        Assert.Contains("360", html);
        Assert.Contains("data-miner-row=\"NMMiner 2\"", await Renderer.HtmlAsync());
    }

    [Fact]
    public async Task With_no_miners_the_summary_points_at_the_scan()
    {
        await RenderAsync<MinersCard>(null);
        var html = WebUtility.HtmlDecode(await Renderer.WaitForAsync("No miners"));

        Assert.Contains("Find miners on my network", html);
    }
}
