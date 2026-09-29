#pragma warning disable BL0006 // The interactive test renderer is a Renderer, which is the whole point of it.
using System.Net;
using System.Text;
using LabbyTwo.Components.Shared;
using LabbyTwo.Components.Widgets;
using LabbyTwo.Core;
using LabbyTwo.Providers;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabbyTwo.Tests;

/// <summary>
/// The cards and the Markdown drawn by the real components against a real database, with the
/// Units setting at imperial and then switched to metric while they are on the page — which
/// is what the person who asked for this saw fail: a runbook, a tile, a gauge and the NAS
/// card all still saying °C beside a weather card in °F.
/// </summary>
public sealed class UnitsRenderTests : IAsyncDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private readonly Stub _stub = new();
    private readonly List<InteractiveRenderer> _renderers = [];

    /// <summary>A connection that reports a temperature and a disk figure.</summary>
    private sealed class Stub : IConnectionProvider
    {
        public string Type => "stub";
        public string DisplayName => "Stub";
        public string Icon => "";
        public string Description => "";
        public IReadOnlyList<FieldSpec> Fields => [];
        public IReadOnlyList<MetricSpec> Metrics =>
            [new("temp_c", "Temperature", "°C", 1), new("disk_percent", "Disk used", "%", 1)];

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
            Task.FromResult(ProbeResult.Up(TimeSpan.FromMilliseconds(3), "OK",
                new Dictionary<string, double> { ["temp_c"] = 17.6, ["disk_percent"] = 50 }));
    }

    /// <summary>A QNAP that answers with what a real TS-464 sent, from the fixtures.</summary>
    private sealed class FixtureQnap : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = path switch
            {
                _ when path.EndsWith("authLogin.cgi") => "<QDocRoot><authPassed>1</authPassed><authSid>test</authSid></QDocRoot>",
                _ when path.EndsWith("manaRequest.cgi") => Fixture("sysinfo"),
                _ when path.EndsWith("chartReq.cgi") => Fixture("volumes"),
                _ when path.EndsWith("qsmart.cgi") => Fixture("smart"),
                _ => Fixture("firmware"),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/xml"),
            });
        }

        private static string Fixture(string name) =>
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Qnap", $"ts464-qts5210-{name}.xml"));
    }

    public UnitsRenderTests()
    {
        Directory.CreateDirectory(_directory);
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        services.AddTestStorage(_directory);
        services.AddSingleton<IConnectionProvider>(_stub);
        services.AddSingleton<IEnumerable<IWidgetType>>([]);
        services.AddSingleton<IEnumerable<ITabKind>>([]);
        services.AddSingleton<Registry>();
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<AlertRuleStore>();
        services.AddSingleton<HistoryStore>();
        services.AddSingleton<HealthMonitor>();
        services.AddSingleton<CapacityForecasts>();
        services.AddSingleton<LatestReadings>();
        services.AddSingleton<Offload>();
        services.AddSingleton<SharedSeries>();
        services.AddSingleton<Markdown>();
        services.AddSingleton<ActionRunner>();
        services.AddSingleton<DisplayUnits>();
        services.AddSingleton(new QnapProvider(new FixtureQnap(), NullLogger<QnapProvider>.Instance));
        _services = services.BuildServiceProvider();
        Get<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var renderer in _renderers)
            await renderer.DisposeAsync();
        TestHost.Teardown(_services, _directory);
    }

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private async Task<Connection> ConnectionAsync(string name, string provider = "stub")
    {
        var connection = new Connection { Provider = provider, Name = name };
        await Get<ConfigStore>().SaveConnectionAsync(connection);
        if (provider == "stub")
            await Get<HealthMonitor>().RefreshAsync(connection);
        return connection;
    }

    private async Task<InteractiveRenderer> CardAsync<T>(Connection connection, params (string Key, string Value)[] settings)
        where T : Microsoft.AspNetCore.Components.IComponent
    {
        var bag = new SettingsBag();
        foreach (var (key, value) in settings)
            bag[key] = value;
        var widget = new Widget { Type = "test", ConnectionId = connection.Id, Settings = bag };
        var renderer = new InteractiveRenderer(_services);
        _renderers.Add(renderer);
        await renderer.RenderAsync<T>(new Dictionary<string, object?> { ["Context"] = new WidgetContext(widget, connection) });
        return renderer;
    }

    private async Task<InteractiveRenderer> MarkdownAsync(string markdown)
    {
        var renderer = new InteractiveRenderer(_services);
        _renderers.Add(renderer);
        await renderer.RenderMarkdownAsync(markdown);
        return renderer;
    }

    private Task ChooseAsync(string preset) => Get<AppSettingsStore>().SaveAsync(Units.Preferences.SystemKey, preset);

    private static string Text(string html) => WebUtility.HtmlDecode(html);

    [Fact]
    public async Task ALiveValueInMarkdownFollowsTheSettingAndChangesWithIt()
    {
        await ChooseAsync(Units.Imperial);
        await ConnectionAsync("Ambient Weather station");

        var page = await MarkdownAsync("Outside it is {{metric: Ambient Weather station / temp_c}}.");
        Assert.Contains("Outside it is <span class=\"sc-value\" title=\"Ambient Weather station · Temperature\">63.7°F</span>.",
            Text(await page.WaitForAsync("63.7°F")));

        // Switched on the Appearance page while the note is open: it redraws in Celsius.
        await ChooseAsync(Units.Metric);
        Assert.Contains(">17.6°C</span>", Text(await page.WaitForAsync("17.6°C")));
    }

    [Fact]
    public async Task UnitInMarkdownConvertsWhenItIsAUnitAndLabelsWhenItIsNot()
    {
        await ChooseAsync(Units.Imperial);
        await ConnectionAsync("NAS");

        var page = await MarkdownAsync(
            "{{metric: NAS / temp_c unit=°C}} | {{metric: NAS / temp_c unit=K decimals=2}} | {{metric: NAS / temp_c unit=\" degrees\"}} | {{metric: NAS / disk_percent}}");
        var html = Text(await page.WaitForAsync(" degrees"));

        Assert.Contains(">17.6°C</span>", html);
        Assert.Contains(">290.75K</span>", html);
        Assert.Contains(">63.7 degrees</span>", html);
        Assert.Contains(">50.0%</span>", html);
    }

    [Fact]
    public async Task AMetricTileFollowsTheSettingAndChangesWithIt()
    {
        await ChooseAsync(Units.Imperial);
        var nas = await ConnectionAsync("NAS");

        var tile = await CardAsync<MetricTile>(nas, ("metric", "temp_c"), ("show_sparkline", "false"));
        var html = Text(await tile.WaitForAsync("°F"));
        Assert.Matches(@"63\.7\s*<span class=""metric-unit"">°F</span>", html);

        await ChooseAsync(Units.Metric);
        html = Text(await tile.WaitForAsync("°C"));
        Assert.Matches(@"17\.6\s*<span class=""metric-unit"">°C</span>", html);
    }

    [Fact]
    public async Task AMetricTilesSuffixConvertsOrLabelsByTheSameRuleAsMarkdown()
    {
        await ChooseAsync(Units.Imperial);
        var nas = await ConnectionAsync("NAS");

        var celsius = await CardAsync<MetricTile>(nas, ("metric", "temp_c"), ("suffix", "°C"), ("show_sparkline", "false"));
        Assert.Matches(@"17\.6\s*<span class=""metric-unit"">°C</span>", Text(await celsius.WaitForAsync("°C")));

        var label = await CardAsync<MetricTile>(nas, ("metric", "temp_c"), ("suffix", " degrees"), ("show_sparkline", "false"));
        Assert.Matches(@"63\.7\s*<span class=""metric-unit""> degrees</span>", Text(await label.WaitForAsync("degrees")));
    }

    [Fact]
    public async Task AGaugeShowsItsReadingAndScaleInTheReadersUnits()
    {
        await ChooseAsync(Units.Imperial);
        var nas = await ConnectionAsync("NAS");

        var gauge = await CardAsync<GaugeCard>(nas, ("metric", "temp_c"), ("max", "100"), ("warn", "60"));
        var html = Text(await gauge.WaitForAsync("212°F"));
        Assert.Contains(">63.7°F</span>", html);
        Assert.Contains("of 212°F", html);
        Assert.Contains("Warning at 140°F", html);
        // The bar is the same length whichever units it is labelled in: 17.6 of 100.
        Assert.Contains("width: 17.6%", html);

        await ChooseAsync(Units.Metric);
        html = Text(await gauge.WaitForAsync("of 100°C"));
        Assert.Contains(">17.6°C</span>", html);
    }

    [Fact]
    public async Task TheNasCardsTemperaturesFollowTheSetting()
    {
        await ChooseAsync(Units.Imperial);
        var nas = await ConnectionAsync("QNAP", provider: "qnap");
        nas = nas with { Settings = new SettingsBag { ["host"] = "nas.test", ["username"] = "admin", ["password"] = "x" } };
        var probe = await Get<QnapProvider>().ProbeAsync(nas, CancellationToken.None);
        Assert.True(probe.Ok, probe.Message);

        var card = await CardAsync<NasCard>(nas);
        var html = Text(await card.WaitForAsync("CPU temp"));
        // 59 °C from the fixture's sysinfo; the drives read 38–41 °C.
        Assert.Contains("<div>138°F</div>", html);
        Assert.Contains("102°F", html);
        Assert.DoesNotContain("°C", html);

        await ChooseAsync(Units.Metric);
        html = Text(await card.WaitForAsync("<div>59°C</div>"));
        Assert.Contains("39°C", html);
        Assert.DoesNotContain("°F", html);
    }
}
