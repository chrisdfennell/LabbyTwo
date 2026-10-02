#pragma warning disable BL0006 // The interactive test renderer is a Renderer, which is the whole point of it.
using System.Net;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LabbyTwo.Tests;

/// <summary>
/// The app's side of the Home Assistant link against a real database: settings saved with
/// the password encrypted, the lab read from memory, and every button press checked again
/// and written to the change feed — and the Settings card drawn.
/// </summary>
public sealed class HomeAssistantBridgeTests : IAsyncDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private readonly Plug _plug = new();
    private InteractiveRenderer? _renderer;

    /// <summary>A smart plug with one harmless action and three that must never be a button.</summary>
    private sealed class Plug : IConnectionProvider
    {
        public string Type => "stubplug";
        public string DisplayName => "Stub plug";
        public string Icon => "";
        public string Description => "";
        public IReadOnlyList<FieldSpec> Fields => [];

        public IReadOnlyList<MetricSpec> Metrics =>
        [
            new("temp_c", "Temperature", "°C", 1),
            new("power_watts", "Power", " W"),
        ];

        public IReadOnlyList<ProviderAction> Actions =>
        [
            new("wake", "Wake on LAN") { Confirms = false },
            new("shutdown", "Shut down") { Dangerous = true },
            new("pause", "Pause") { Fields = [new FieldSpec("minutes", "Minutes", FieldKind.Number)] },
            new("cycle", "Power cycle") { Disrupts = TimeSpan.FromMinutes(2) },
        ];

        public List<string> Ran { get; } = [];

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
            Task.FromResult(ProbeResult.Up(TimeSpan.FromMilliseconds(3), "OK",
                new Dictionary<string, double> { ["temp_c"] = 21.5, ["power_watts"] = 40 }));

        public Task<ActionResult> RunActionAsync(Connection connection, ProviderAction action, SettingsBag input, CancellationToken ct)
        {
            lock (Ran)
                Ran.Add(action.Id);
            return Task.FromResult(ActionResult.Done("Sent."));
        }
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    public HomeAssistantBridgeTests()
    {
        Directory.CreateDirectory(_directory);
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        services.AddTestStorage(_directory);
        services.AddSingleton<IConnectionProvider>(_plug);
        services.AddSingleton<IEnumerable<IWidgetType>>([]);
        services.AddSingleton<IEnumerable<ITabKind>>([]);
        services.AddSingleton<Registry>();
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<AlertRuleStore>();
        services.AddSingleton<HistoryStore>();
        services.AddSingleton<HealthMonitor>();
        services.AddSingleton<LatestReadings>();
        services.AddSingleton<Offload>();
        services.AddSingleton<ActionRunner>();
        services.AddSingleton<ChangeStore>();
        services.AddSingleton<HomeAssistantBridge>();
        services.AddSingleton<IHostApplicationLifetime, Lifetime>();
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

    private HomeAssistantBridge Bridge => Get<HomeAssistantBridge>();

    private async Task<Connection> PlugAsync(string name = "Office plug")
    {
        var connection = new Connection { Provider = "stubplug", Name = name };
        await Get<ConfigStore>().SaveConnectionAsync(connection);
        await Get<HealthMonitor>().RefreshAsync(connection);
        return connection;
    }

    private static HaSettings On => HomeAssistantPlanTests.On();

    private async Task<IReadOnlyList<Change>> FeedAsync() =>
        await Get<ChangeStore>().QueryAsync(new ChangeQuery(DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now.AddMinutes(1), [ChangeKinds.HomeAssistant]));

    [Fact]
    public async Task ThePasswordIsStoredEncryptedAndReadBack()
    {
        const string password = "hunter2-very-secret";
        await Bridge.SaveAsync(On with { Username = "labby", Password = password });

        var raw = await Get<AppSettingsStore>().GetAsync(HomeAssistantBridge.PasswordKey);
        Assert.StartsWith("enc:", raw);
        Assert.DoesNotContain(password, raw);
        Assert.Equal(password, (await Bridge.SettingsAsync()).Password);

        // Saving again with the box left blank keeps it; forgetting it removes it.
        await Bridge.SaveAsync(On with { Username = "labby", Password = "" });
        Assert.Equal(password, (await Bridge.SettingsAsync()).Password);
        await Bridge.SaveAsync(On, clearPassword: true);
        Assert.Equal("", (await Bridge.SettingsAsync()).Password);
    }

    [Fact]
    public async Task OffUntilSwitchedOn()
    {
        var saved = await Bridge.SettingsAsync();
        Assert.False(saved.Enabled);
        Assert.False(saved.Active);
        Assert.Equal("homeassistant", saved.Prefix);
        Assert.Equal("labbytwo", saved.Base);
        Assert.False(saved.MaintenanceButtons);
        Assert.False(saved.ActionButtons);
    }

    [Fact]
    public async Task TheLabIsReadFromWhatTheMonitorHolds()
    {
        var plug = await PlugAsync();
        var lab = await Bridge.LabAsync();

        var view = Assert.Single(lab.Connections);
        Assert.Equal((plug.Id, "Office plug", "Stub plug", true), (view.Id, view.Name, view.Provider, view.IsUp));
        Assert.Contains(view.Metrics, m => m.Spec.Key == "temp_c" && m.Value == 21.5 && m.Spec.Unit == "°C");
        Assert.Equal(["wake"], view.Actions.Select(a => a.Id));
        Assert.Equal("1 up, 0 down", lab.Summary);
        Assert.False(lab.AnyDown);
    }

    [Fact]
    public async Task MaintenanceButtonsWorkOnlyWhenTickedAndAreRecorded()
    {
        var refused = await Bridge.CommandAsync(new HaCommand(HaCommandKind.MaintenanceStart, null, null), On, retained: false, default);
        Assert.Contains("refused", refused);
        Assert.False(Maintenance.From(await Get<AppSettingsStore>().AllAsync()).On);

        await Bridge.CommandAsync(new HaCommand(HaCommandKind.MaintenanceStart, null, null), On with { MaintenanceButtons = true }, false, default);
        var window = Maintenance.From(await Get<AppSettingsStore>().AllAsync());
        Assert.True(window.On);
        Assert.InRange(window.Until!.Value - DateTimeOffset.Now, TimeSpan.FromMinutes(59), TimeSpan.FromMinutes(61));

        await Bridge.CommandAsync(new HaCommand(HaCommandKind.MaintenanceEnd, null, null), On with { MaintenanceButtons = true }, false, default);
        Assert.False(Maintenance.From(await Get<AppSettingsStore>().AllAsync()).On);

        var feed = await FeedAsync();
        Assert.Equal(3, feed.Count);
        Assert.Contains(feed, c => c.Action == ChangeActions.Skipped);
        Assert.Contains(feed, c => c.Action == ChangeActions.Started);
        Assert.Contains(feed, c => c.Action == ChangeActions.Stopped);
    }

    [Fact]
    public async Task ASafeActionRunsThroughTheRunnerAndIsRecorded()
    {
        var plug = await PlugAsync();
        var settings = On with { ActionButtons = true };

        var said = await Bridge.CommandAsync(new HaCommand(HaCommandKind.Action, HaTopics.Id(plug.Id), "wake"), settings, false, default);

        Assert.Contains("ran", said);
        Assert.Equal(["wake"], _plug.Ran);
        var change = Assert.Single(await FeedAsync());
        Assert.Equal((ChangeActions.Completed, plug.Id), (change.Action, change.ConnectionId));
        Assert.Contains("Wake on LAN", change.Title);
    }

    [Theory]
    [InlineData("shutdown")]
    [InlineData("pause")]
    [InlineData("cycle")]
    [InlineData("format_everything")]
    public async Task AnythingUnsafeOrUnknownIsRefusedAndRecorded(string action)
    {
        var plug = await PlugAsync();
        var said = await Bridge.CommandAsync(new HaCommand(HaCommandKind.Action, plug.Id, action), On with { ActionButtons = true }, false, default);

        Assert.Contains("refused", said);
        Assert.Empty(_plug.Ran);
        Assert.Equal(ChangeActions.Skipped, Assert.Single(await FeedAsync()).Action);
    }

    [Fact]
    public async Task ActionsAreRefusedWhenSwitchedOffUnpublishedOrReplayed()
    {
        var plug = await PlugAsync();
        var wake = new HaCommand(HaCommandKind.Action, plug.Id, "wake");

        await Bridge.CommandAsync(wake, On, false, default);
        await Bridge.CommandAsync(wake, On with { ActionButtons = true, AllConnections = false, Connections = new HashSet<string>() }, false, default);
        await Bridge.CommandAsync(wake, On with { ActionButtons = true }, retained: true, default);

        Assert.Empty(_plug.Ran);
        var feed = await FeedAsync();
        Assert.Equal(3, feed.Count);
        Assert.All(feed, c => Assert.Equal(ChangeActions.Skipped, c.Action));
    }

    private InteractiveRenderer Renderer => _renderer ??= new InteractiveRenderer(_services);

    [Fact]
    public async Task TheSettingsCardSaysOffAndOffersTheChoices()
    {
        await PlugAsync();
        await Renderer.RenderAsync<LabbyTwo.Components.Shared.HomeAssistantCard>(new Dictionary<string, object?>());
        var html = WebUtility.HtmlDecode(await Renderer.WaitForAsync("Publish to Home Assistant"));

        Assert.Contains("Home Assistant (MQTT)", html);
        Assert.Contains(">Off<", html);
        Assert.Contains("Discovery prefix", html);
        Assert.Contains("placeholder=\"homeassistant\"", html);
        Assert.Contains("Every connection", html);
        Assert.Contains("Up or down only", html);
        Assert.Contains("Start maintenance (1 hour)", html);
        Assert.Contains("Nothing is sent until you switch it on", html);
        Assert.DoesNotContain("Last publish", html);
    }

    [Fact]
    public async Task TheSettingsCardShowsTheLinkOnceSwitchedOn()
    {
        await Bridge.SaveAsync(On with { Host = "broker.lan", Password = "hunter2-very-secret" });
        await Renderer.RenderAsync<LabbyTwo.Components.Shared.HomeAssistantCard>(new Dictionary<string, object?>());
        var html = WebUtility.HtmlDecode(await Renderer.WaitForAsync("Last publish"));

        Assert.Contains("Not connected", html);
        Assert.Contains("broker.lan:1883", html);
        Assert.Contains("Unchanged — leave blank to keep it", html);
        Assert.DoesNotContain("hunter2-very-secret", html);
    }
}
