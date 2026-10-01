#pragma warning disable BL0006 // The interactive test renderer reads its own render tree back as HTML; that is what it is for.
using System.Net;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Services.Offsite;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LabbyTwo.Tests;

/// <summary>
/// Conditions on anything, {{status: tab …}} and {{who}} drawn by the real components
/// against a real database, monitor and alert evaluator: a runbook written the old way
/// behaving exactly as it did, the new tests switching as the lab changes, and — once the
/// caches are warm — drawing it all without reading the database.
/// </summary>
public sealed class MarkdownConditionsRenderTests : IAsyncDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private readonly InteractiveRenderer _renderer;
    private readonly Lab _lab = new();

    /// <summary>A lab whose every reading and verdict the test sets, by connection name.</summary>
    private sealed class Lab : IConnectionProvider
    {
        public string Type => "lab";
        public string DisplayName => "Lab";
        public string Icon => "";
        public string Description => "";
        public IReadOnlyList<FieldSpec> Fields => [];

        public IReadOnlyList<MetricSpec> Metrics =>
        [
            new("disk_percent", "Disk used", "%", 1),
            new("firmware_update", "Firmware update"),
            new("download_mbps", "Download", " Mbps", 1),
        ];

        public HashSet<string> Down { get; } = [];
        public Dictionary<string, Dictionary<string, double>> Readings { get; } = [];

        public IReadOnlyList<ProviderAction> Actions => [new("reboot", "Reboot") { Dangerous = true }];

        public Task<ActionResult> RunActionAsync(Connection connection, ProviderAction action, SettingsBag input, CancellationToken ct) =>
            Task.FromResult(ActionResult.Done("Sent."));

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
            Task.FromResult(Down.Contains(connection.Name)
                ? ProbeResult.Down(TimeSpan.Zero, "Connection refused")
                : ProbeResult.Up(TimeSpan.FromMilliseconds(2), "OK", Readings.GetValueOrDefault(connection.Name) ?? []));
    }

    /// <summary>The Who's home plugin, as far as {{who: home}} can tell: its type and a metric per device.</summary>
    private sealed class Presence : IConnectionProvider
    {
        public string Type => "presence";
        public string DisplayName => "Who's home";
        public string Icon => "";
        public string Description => "";
        public IReadOnlyList<FieldSpec> Fields => [];
        public IReadOnlyList<MetricSpec> MetricsFor(Connection connection) => [new("home_chris", "Chris"), new("home_sam", "Sam")];

        public Dictionary<string, double> Home { get; } = new() { ["home_chris"] = 1, ["home_sam"] = 0 };

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
            Task.FromResult(ProbeResult.Up(TimeSpan.Zero, "OK", new Dictionary<string, double>(Home)));
    }

    /// <summary>A Plex server as its probe reports one: the stream list kept in the details.</summary>
    private sealed class Plex : IConnectionProvider
    {
        public string Type => "plex";
        public string DisplayName => "Plex";
        public string Icon => "";
        public string Description => "";
        public IReadOnlyList<FieldSpec> Fields => [];

        public List<NowPlayingStream> Playing { get; } = [];

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
            Task.FromResult(ProbeResult.Up(TimeSpan.Zero, "OK", new Dictionary<string, double> { ["stream_count"] = Playing.Count },
                new Dictionary<string, string> { [NowPlaying.DetailKey] = NowPlaying.Encode(Playing) }));
    }

    private readonly Presence _presence = new();
    private readonly Plex _plex = new();

    public MarkdownConditionsRenderTests()
    {
        Directory.CreateDirectory(_directory);
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        // One failed probe is down, so a test can take something down in one step.
        services.AddTestStorage(_directory, options => options.FailuresBeforeDown = 1);
        services.AddSingleton<IConnectionProvider>(_lab);
        services.AddSingleton<IConnectionProvider>(_presence);
        services.AddSingleton<IConnectionProvider>(_plex);
        services.AddSingleton<IEnumerable<IWidgetType>>([]);
        services.AddSingleton<IEnumerable<ITabKind>>([]);
        services.AddSingleton<Registry>();
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<AlertRuleStore>();
        services.AddSingleton<HistoryStore>();
        services.AddSingleton<HealthMonitor>();
        services.AddSingleton<AlertService>();
        services.AddSingleton<CapacityForecasts>();
        services.AddSingleton<MetricBaselines>();
        services.AddSingleton<MetricAlertService>();
        services.AddSingleton<ChangeStore>();
        services.AddSingleton<IncidentStore>();
        services.AddSingleton<IncidentTracker>();
        services.AddSingleton<OffsiteSettingsStore>();
        services.AddSingleton<BackupStore>();
        services.AddSingleton<BackupProof>();
        services.AddSingleton<LatestReadings>();
        services.AddSingleton<Offload>();
        services.AddSingleton<DisplayUnits>();
        services.AddSingleton<SharedSeries>();
        services.AddSingleton<Markdown>();
        services.AddSingleton<ActionRunner>();
        _services = services.BuildServiceProvider();
        Get<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();
        _renderer = new InteractiveRenderer(_services);
    }

    public async ValueTask DisposeAsync()
    {
        await _renderer.DisposeAsync();
        TestHost.Teardown(_services, _directory);
    }

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private static string Text(string html) => WebUtility.HtmlDecode(html);

    private async Task<Connection> ConnectionAsync(string name, string provider = "lab")
    {
        var connection = new Connection { Provider = provider, Name = name };
        await Get<ConfigStore>().SaveConnectionAsync(connection);
        return connection;
    }

    private async Task SweepAsync(params Connection[] connections)
    {
        foreach (var connection in connections)
            await Get<HealthMonitor>().RefreshAsync(connection);
    }

    // ---------- the note written before any of this existed ----------

    /// <summary>
    /// A runbook in the style of the one in use: every condition the old grammar had, and
    /// status, metric, sparkline, details, button, callouts and a table around them.
    /// </summary>
    private const string OldNote = """
        # The lab

        {{if all up}}
        > [!TIP]
        > Everything is up.
        {{end}}

        {{if down: QNAP NAS}}
        ## The NAS is down

        1. Wake it: {{button: QNAP NAS / reboot}}
        {{else}}
        The NAS is {{status: QNAP NAS}}, {{metric: QNAP NAS / disk_percent}} used {{sparkline: QNAP NAS / disk_percent 24h}}.
        {{end}}

        {{if metric: QNAP NAS / disk_percent > 85}}
        > [!WARNING]
        > Disk almost full.
        {{end}}

        {{if metric: Internet speed test / download_mbps < 200}}
        Slow internet today.
        {{else}}
        Internet is fast.
        {{end}}

        {{if metric: QNAP NAS / firmware_update > 0}}
        A firmware update is waiting.
        {{end}}

        {{details: Every service}}
        | Service | State |
        |---|---|
        | NAS | {{status: QNAP NAS}} |
        | Speed test | {{status: Internet speed test}} |
        {{end}}
        """;

    [Fact]
    public void EveryConditionInTheOldNoteReadsAsTheOldGrammarReadIt()
    {
        var page = Get<Markdown>().PreparePage(OldNote);
        var sections = page.Sections().ToList();

        Assert.Equal(5, sections.Count);
        foreach (var section in sections)
        {
            Assert.Null(section.Section.Problem);
            var written = Shortcodes.Parse(section.Section.Source)!.Part(0);
            var old = Runbook.ParseCondition(written, out var problem);
            Assert.Null(problem);
            Assert.Equal(new RunbookAtom(old!), section.Section.Expression);
            Assert.Equal(old, section.Section.Condition);
        }
    }

    [Fact]
    public async Task TheOldNoteBehavesAsItAlwaysDid()
    {
        var nas = await ConnectionAsync("QNAP NAS");
        var speed = await ConnectionAsync("Internet speed test");
        _lab.Readings["QNAP NAS"] = new() { ["disk_percent"] = 40, ["firmware_update"] = 0 };
        _lab.Readings["Internet speed test"] = new() { ["download_mbps"] = 500 };
        await SweepAsync(nas, speed);

        await _renderer.RenderMarkdownAsync(OldNote);
        var html = Text(await _renderer.WaitForAsync("Internet is fast"));

        Assert.Contains("Everything is up.", html);
        Assert.Contains("md-callout-tip", html);
        Assert.Contains("The NAS is <span class=\"sc-value\"", html);
        Assert.Contains("40.0%", html);
        Assert.DoesNotContain("Disk almost full", html);
        Assert.DoesNotContain("Slow internet", html);
        Assert.DoesNotContain("firmware update is waiting", html);
        Assert.DoesNotContain("The NAS is down", html);
        Assert.Contains("<details class=\"md-details\"><summary class=\"md-details-summary\">Every service</summary>", html);
        Assert.Contains("<table>", html);
        Assert.DoesNotContain("sc-problem", html);

        // Full disk, a firmware update and a slow line: each of its own sections appears.
        _lab.Readings["QNAP NAS"] = new() { ["disk_percent"] = 91, ["firmware_update"] = 1 };
        _lab.Readings["Internet speed test"] = new() { ["download_mbps"] = 150 };
        await SweepAsync(nas, speed);
        html = Text(await _renderer.WaitForAsync("Slow internet today"));
        Assert.Contains("Disk almost full.", html);
        Assert.Contains("md-callout-warning", html);
        Assert.Contains("A firmware update is waiting.", html);
        Assert.DoesNotContain("Internet is fast", html);

        // The NAS goes: the down branch with its button, and "all up" goes.
        _lab.Down.Add("QNAP NAS");
        await SweepAsync(nas);
        html = Text(await _renderer.WaitForAsync("The NAS is down"));
        Assert.Contains("Reboot", html);
        Assert.DoesNotContain("Everything is up.", html);
        Assert.DoesNotContain("The NAS is <span", html);
    }

    // ---------- the new tests, live ----------

    [Fact]
    public async Task NewConditionsSwitchAsTheLabChanges()
    {
        var nas = await ConnectionAsync("QNAP NAS");
        var plex = await ConnectionAsync("Plex");
        _lab.Readings["QNAP NAS"] = new() { ["disk_percent"] = 91 };
        await SweepAsync(nas, plex);
        await Get<AlertRuleStore>().SaveAsync(new AlertRule
        {
            Name = "Disk almost full", Metric = "disk_percent", Comparison = Comparison.Above, Threshold = 95, ConnectionId = nas.Id,
        });

        await _renderer.RenderMarkdownAsync("""
            {{if metric: QNAP NAS / disk_percent > 85 and not maintenance}}
            Clear the recycle bin.
            {{elif maintenance}}
            Somebody is working on it.
            {{else}}
            Nothing to do.
            {{end}}

            {{if alert: "Disk almost full"}}
            The alert is firing.
            {{elif any alert}}
            Some other alert.
            {{else}}
            No alert.
            {{end}}

            {{if alert on: "QNAP NAS" or down: Plex}}
            Look at the NAS or Plex.
            {{end}}

            {{if metric: QNAP NAS / disk_percent between 90 and 95}}
            Between.
            {{end}}
            """);
        var html = Text(await _renderer.WaitForAsync("Clear the recycle bin"));
        Assert.Contains("No alert.", html);
        Assert.Contains("Between.", html);
        Assert.DoesNotContain("Look at the NAS", html);

        // Maintenance: the elif takes over.
        await Get<AppSettingsStore>().SaveAsync(Maintenance.Key, Maintenance.Indefinite);
        html = Text(await _renderer.WaitForAsync("Somebody is working on it"));
        Assert.DoesNotContain("Clear the recycle bin", html);

        // The rule fires.
        _lab.Readings["QNAP NAS"] = new() { ["disk_percent"] = 97 };
        await SweepAsync(nas);
        await Get<MetricAlertService>().EvaluateAsync(DateTimeOffset.Now, CancellationToken.None);
        html = Text(await _renderer.WaitForAsync("The alert is firing"));
        Assert.Contains("Look at the NAS or Plex.", html);
        Assert.DoesNotContain("Between.", html);
    }

    [Fact]
    public async Task AConditionNamingNothingIsAQuestionMarkNotACrash()
    {
        await ConnectionAsync("QNAP NAS");

        await _renderer.RenderMarkdownAsync("""
            {{if maintenance or down: Fridge}}
            A
            {{else}}
            B
            {{end}}

            {{if down: tab "Garage"}}
            C
            {{end}}

            {{if alert: "Nope"}}
            D
            {{end}}

            {{if maintenance and down:}}
            E
            {{end}}
            """);
        var html = Text(await _renderer.WaitForAsync("No alert rule called"));
        Assert.Contains("Say which connection", html);

        Assert.Contains("No connection called “Fridge”. ({{if maintenance or down: Fridge}})", html);
        Assert.Contains("No tab called “Garage”.", html);
        Assert.Contains("No alert rule called “Nope”.", html);
        foreach (var hidden in new[] { "<p>A</p>", "<p>B</p>", "<p>C</p>", "<p>D</p>", "<p>E</p>" })
            Assert.DoesNotContain(hidden, html);
    }

    // ---------- {{status: tab …}} ----------

    private async Task<(Connection Plex, Connection Sonarr, Connection Nas)> MediaTabAsync()
    {
        var plex = await ConnectionAsync("Plex");
        var sonarr = await ConnectionAsync("Sonarr");
        var radarr = await ConnectionAsync("Radarr");
        var nas = await ConnectionAsync("QNAP NAS");
        var tab = new Tab { Name = "Media", Slug = "media" };
        await Get<ConfigStore>().SaveTabAsync(tab);
        foreach (var on in new[] { plex, sonarr, radarr })
            await Get<ConfigStore>().SaveWidgetAsync(new Widget { TabId = tab.Id, Type = "status", ConnectionId = on.Id });
        await SweepAsync(plex, sonarr, nas);
        return (plex, sonarr, nas);
    }

    [Fact]
    public async Task StatusSummarisesATabAndTheLabAndKeepsUp()
    {
        var (plex, _, _) = await MediaTabAsync();

        await _renderer.RenderMarkdownAsync("""
            Media: {{status: tab "Media"}}

            Everything: {{status: all}}

            {{status: tab Media full}}

            {{if down: tab "Media"}}
            Media has a problem.
            {{end}}
            """);
        var html = Text(await _renderer.WaitForAsync("Media: 2 fine"));
        Assert.Contains("Media: 2 fine, 1 checking", html);
        Assert.Contains("Everything: 3 fine, 1 checking", html);
        Assert.Contains("md-status-item", html);
        Assert.Contains("Radarr <span class=\"tile-meta\">checking</span>", html);
        Assert.DoesNotContain("Media has a problem", html);

        _lab.Down.Add("Plex");
        await SweepAsync(plex);
        html = Text(await _renderer.WaitForAsync("1 down (Plex)"));
        Assert.Contains("Media: 1 fine, 1 down (Plex), 1 checking", html);
        Assert.Contains("Media has a problem.", html);
    }

    [Fact]
    public async Task StatusOfATabThatIsNotThereSaysSo()
    {
        await ConnectionAsync("QNAP NAS");
        await _renderer.RenderMarkdownAsync("The garage: {{status: tab Garage}}. The NAS: {{status: QNAP NAS}}.");
        var html = Text(await _renderer.WaitForAsync("sc-problem"));

        Assert.Contains("No tab called “Garage”.", html);
        Assert.Contains("status-dot status-unknown", html);
    }

    // ---------- {{who}} ----------

    [Fact]
    public async Task WhoIsHomeAndWhatIsPlayingFollowTheSweeps()
    {
        var house = await ConnectionAsync("House", "presence");
        var plex = await ConnectionAsync("Plex server", "plex");
        _plex.Playing.Add(new NowPlayingStream("Chris", "The Office", "Dinner Party", "Living room TV", 42, true));
        await SweepAsync(house, plex);

        await _renderer.RenderMarkdownAsync("Home: {{who: home}}.\n\nPlaying: {{who: watching}}");
        var html = Text(await _renderer.WaitForAsync("Home: <span class=\"sc-value md-who-home\""));
        Assert.Contains("Chris</span>.", html);
        Assert.Contains("Chris — The Office (Dinner Party) on Living room TV, 42%, transcoding", html);

        _presence.Home["home_sam"] = 1;
        _plex.Playing.Clear();
        await SweepAsync(house, plex);
        html = Text(await _renderer.WaitForAsync("Chris and Sam"));
        Assert.Contains("nothing playing", html);
    }

    [Fact]
    public async Task WhoWithNothingSetUpSaysSoInWords()
    {
        await ConnectionAsync("QNAP NAS");
        await _renderer.RenderMarkdownAsync("Home: {{who: home}}. Playing: {{who: watching}}. Odd: {{who: dancing}}.");
        var html = Text(await _renderer.WaitForAsync("presence isn't set up"));

        Assert.Contains("nothing to watch on", html);
        Assert.Contains("Ask {{who: home}} or {{who: watching}}.", html);
    }

    // ---------- the promise ----------

    [Fact]
    public async Task OnceTheCachesAreWarmDecidingAndDrawingReadsNothingFromTheDatabase()
    {
        var (_, _, nas) = await MediaTabAsync();
        var house = await ConnectionAsync("House", "presence");
        await SweepAsync(house);
        await Get<AlertRuleStore>().SaveAsync(new AlertRule { Name = "Disk almost full", Metric = "disk_percent", Threshold = 95 });
        await Get<AppSettingsStore>().SaveAsync(Maintenance.Key, Maintenance.Cleared);
        _lab.Readings["QNAP NAS"] = new() { ["disk_percent"] = 91 };
        await SweepAsync(nas);

        const string note = """
            {{status: tab "Media" full}} · {{status: all}} · home: {{who: home}} · {{who: watching}}

            {{if metric: QNAP NAS / disk_percent > 85 and not maintenance and not blind}}
            Disk is high.
            {{elif any alert or backup late or incident open}}
            Something else.
            {{else}}
            Fine.
            {{end}}

            {{if down: tab "Media" or alert: "Disk almost full" or alert on: "QNAP NAS"}}
            Media trouble.
            {{end}}
            """;

        // The first page warms whatever is read once — the layout, the rules, the settings,
        // the latest readings.
        await _renderer.RenderMarkdownAsync(note);
        await _renderer.WaitForAsync("Disk is high");

        var before = Get<Db>().Opens;
        await using var second = new InteractiveRenderer(_services);
        await second.RenderMarkdownAsync(note);
        var html = Text(await second.WaitForAsync("Disk is high"));
        await Task.Delay(200);

        Assert.Contains("Media: 2 fine, 1 checking", html);
        Assert.Contains("Chris", html);
        Assert.Equal(before, Get<Db>().Opens);
    }
}
