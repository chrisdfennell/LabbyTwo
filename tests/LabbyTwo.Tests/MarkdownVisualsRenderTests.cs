#pragma warning disable BL0006 // InteractiveRenderer reads the render tree back into HTML, which is the point of it.
using System.Net;
using LabbyTwo.Components.Widgets;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LabbyTwo.Tests;

/// <summary>
/// {{table}}, {{gauge}} and diagrams drawn by the real components against a real database:
/// readings in the reader's units, cells and gauges coloured by the connection's alert rules,
/// diagram boxes coloured by status and linking to their connections, every label staying
/// text — and the runbook somebody already has drawing exactly as it did, with none of the
/// new kinds reading the database once the caches are warm.
/// </summary>
public sealed class MarkdownVisualsRenderTests : IAsyncDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private readonly InteractiveRenderer _renderer;
    private readonly Stub _stub = new();

    /// <summary>A connection whose probe reports whatever the test sets; any named in <see cref="Down"/> fail.</summary>
    private sealed class Stub : IConnectionProvider
    {
        public string Type => "stub";
        public string DisplayName => "Stub";
        public string Icon => "";
        public string Description => "";
        public IReadOnlyList<FieldSpec> Fields => [];

        public IReadOnlyList<MetricSpec> Metrics =>
        [
            new("disk_percent", "Disk used", "%", 1) { Capacity = CapacityLimit.Percent },
            new("cpu_percent", "CPU", "%"),
            new("temp_c", "Temperature", "°C", 1),
        ];

        public HashSet<string> Down { get; } = new(StringComparer.OrdinalIgnoreCase);
        public double Disk { get; set; } = 96;
        public double Cpu { get; set; } = 12;
        public double Temp { get; set; } = 61;

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
            Task.FromResult(Down.Contains(connection.Name)
                ? ProbeResult.Down(TimeSpan.Zero, "Connection refused")
                : ProbeResult.Up(TimeSpan.FromMilliseconds(3), "OK", new Dictionary<string, double>
                {
                    ["disk_percent"] = Disk,
                    ["cpu_percent"] = Cpu,
                    ["temp_c"] = Temp,
                }));
    }

    private sealed class QuietBoundaryLogger : IErrorBoundaryLogger
    {
        public ValueTask LogErrorAsync(Exception exception) => ValueTask.CompletedTask;
    }

    public MarkdownVisualsRenderTests()
    {
        Directory.CreateDirectory(_directory);
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        // The card in the runbook is drawn inside a CardBoundary, which needs one of these.
        services.AddSingleton<IErrorBoundaryLogger, QuietBoundaryLogger>();
        services.AddHttpClient();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        services.AddTestStorage(_directory, options => options.FailuresBeforeDown = 1);
        services.AddSingleton<IConnectionProvider>(_stub);
        services.AddSingleton<IEnumerable<IWidgetType>>([new GaugeWidget()]);
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

    private async Task<Connection> ConnectionAsync(string name, string? dependsOn = null, DateTimeOffset? silencedUntil = null)
    {
        var connection = new Connection { Provider = "stub", Name = name, DependsOn = dependsOn, SilencedUntil = silencedUntil };
        await Get<ConfigStore>().SaveConnectionAsync(connection);
        await Get<HealthMonitor>().RefreshAsync(connection);
        return connection;
    }

    private Task ChooseAsync(string preset) => Get<AppSettingsStore>().SaveAsync(Units.Preferences.SystemKey, preset);

    private static string Text(string html) => WebUtility.HtmlDecode(html);

    private static string Flat(string html) => Text(html).ReplaceLineEndings(" ");

    // ---------- {{table}} ----------

    [Fact]
    public async Task ATableShowsEachReadingInTheReadersUnitsColouredByItsRules()
    {
        await ChooseAsync(Units.Imperial);
        var nas = await ConnectionAsync("QNAP NAS");
        _stub.Disk = 40;
        _stub.Temp = 30;
        await ConnectionAsync("Pi");
        await Get<AlertRuleStore>().SaveAsync(new AlertRule { ConnectionId = nas.Id, Metric = "temp_c", Threshold = 60 });

        await _renderer.RenderMarkdownAsync("""{{table: "QNAP NAS", Pi, Nowhere / status, disk_percent, temp_c title="The lab"}}""");
        var html = Flat(await _renderer.WaitForAsync("is-critical"));

        Assert.Contains("<caption>The lab</caption>", html);
        Assert.Matches(@"<th scope=""col"">Connection</th>\s*<th scope=""col"">Status</th>\s*<th scope=""col"">Disk used</th>\s*<th scope=""col"">Temperature</th>", html);
        // 61 °C, past the NAS's own 60 °C rule, read in Fahrenheit and said in words.
        Assert.Matches(@"<td class=""md-cell is-critical"" title=""QNAP NAS · Temperature: 141\.8°F — critical \(critical above 140\.0°F \(from its alert rules\)\)""><span class=""md-cell-value"">141\.8°F</span>\s*<span class=""visually-hidden"">, critical</span>", html);
        // 96% used, no rule: the gauge card's lines.
        Assert.Contains("<span class=\"md-cell-value\">96.0%</span>", html);
        // The Pi has no temperature rule of its own and 30 °C is fine; its disk is 40%.
        Assert.Contains("<span class=\"md-cell-value\">86.0°F</span>", html);
        Assert.Matches(@"<td class=""md-cell is-fine"" title=""Pi · Disk used: 40\.0%", html);
        // Status is {{status}} itself.
        Assert.Contains("<span class=\"status-dot status-up\" aria-hidden=\"true\"></span>up</span>", html);
        // A name that is nothing is a question mark on its row, not the end of the table.
        Assert.Contains("No connection called “Nowhere”.", html);
    }

    [Fact]
    public async Task ATableFollowsTheLabAndTheUnitsSetting()
    {
        await ChooseAsync(Units.Imperial);
        var nas = await ConnectionAsync("NAS");
        await _renderer.RenderMarkdownAsync("{{table: NAS / cpu_percent, temp_c}}");
        Assert.Contains("141.8°F", Flat(await _renderer.WaitForAsync("141.8°F")));

        _stub.Cpu = 77;
        await Get<HealthMonitor>().RefreshAsync(nas);
        Assert.Contains("<span class=\"md-cell-value\">77%</span>", Flat(await _renderer.WaitForAsync("77%")));

        await ChooseAsync(Units.Metric);
        Assert.Contains("61.0°C", Flat(await _renderer.WaitForAsync("61.0°C")));
    }

    [Fact]
    public async Task ATableCanDrawASparklineUnderEachReading()
    {
        var nas = await ConnectionAsync("NAS");
        for (var i = 0; i < 24; i++)
            await Get<HistoryStore>().RecordAsync(nas.Id, new Dictionary<string, double> { ["cpu_percent"] = 10 + i }, CancellationToken.None);

        await _renderer.RenderMarkdownAsync("{{table: NAS / cpu_percent sparkline=24h}}");
        var html = Flat(await _renderer.WaitForAsync("<polyline"));

        Assert.Matches(@"<td class=""md-cell""[^>]*><span class=""md-cell-value"">\d+%</span>\s*<div class=""md-cell-spark""><span class=""md-spark""", html);
    }

    [Fact]
    public async Task ATableCanTakeItsRowsFromATab()
    {
        var plex = await ConnectionAsync("Plex");
        var sonarr = await ConnectionAsync("Sonarr");
        await ConnectionAsync("Not on the tab");
        var tab = new Tab { Name = "Media", Slug = "media" };
        await Get<ConfigStore>().SaveTabAsync(tab);
        await Get<ConfigStore>().SaveWidgetAsync(new Widget { TabId = tab.Id, Type = "service", ConnectionId = sonarr.Id, Sort = 1 });
        await Get<ConfigStore>().SaveWidgetAsync(new Widget { TabId = tab.Id, Type = "service", ConnectionId = plex.Id, Sort = 0 });

        await _renderer.RenderMarkdownAsync("""{{table: tab "Media" / status, uptime}}""");
        var html = Flat(await _renderer.WaitForAsync("Sonarr"));

        Assert.Matches(@"<th scope=""row"">Plex</th>.*<th scope=""row"">Sonarr</th>", html);
        Assert.DoesNotContain("Not on the tab", html);
        Assert.Contains("<th scope=\"col\">Uptime</th>", html);
    }

    [Theory]
    [InlineData("{{table: NAS / cpu_percent sparkline=45d}}", "between 1h and 30d")]
    [InlineData("""{{table: tab "Nope" / status}}""", "No tab called “Nope”")]
    [InlineData("{{table: NAS}}", "Say what goes in the rows and the columns")]
    public async Task ATableThatCannotBeDrawnSaysWhy(string markdown, string why)
    {
        await ConnectionAsync("NAS");
        await _renderer.RenderMarkdownAsync(markdown);
        Assert.Contains(why, Text(await _renderer.WaitForAsync("sc-problem")));
    }

    // ---------- {{gauge}} ----------

    [Fact]
    public async Task AGaugeSitsInTheSentenceAsAMeter()
    {
        var nas = await ConnectionAsync("QNAP NAS");
        await Get<AlertRuleStore>().SaveAsync(new AlertRule { ConnectionId = nas.Id, Metric = "disk_percent", Threshold = 90 });

        await _renderer.RenderMarkdownAsync("""Storage {{gauge: "QNAP NAS" / disk_percent}} used.""");
        var html = Flat(await _renderer.WaitForAsync("is-critical"));

        Assert.Matches(@"<p>Storage <span class=""md-gauge is-ring is-small is-critical"" role=""meter"" tabindex=""0"" aria-valuenow=""96"" aria-valuemin=""0"" aria-valuemax=""100"" aria-valuetext=""96\.0%, critical"" aria-label=""QNAP NAS · Disk used"" title=""QNAP NAS · Disk used: 96\.0% of 100\.0% — critical \(critical above 90\.0% \(from its alert rules\)\)""><svg class=""md-gauge-ring""", html);
        Assert.Contains("stroke-dasharray=\"96 100\"", html);
        Assert.Contains("<span class=\"md-gauge-text\" aria-hidden=\"true\">96.0%</span></span> used.</p>", html);
    }

    [Fact]
    public async Task ABarGaugeWorksInATableCellWithItsOwnLinesAndUnits()
    {
        await ChooseAsync(Units.Imperial);
        await ConnectionAsync("Pi");

        await _renderer.RenderMarkdownAsync("""
            | Host | Heat |
            |---|---|
            | Pi | {{gauge: Pi / temp_c max=100 warn=50 crit=70 style=bar size=medium label="CPU heat"}} |
            """);
        var html = Flat(await _renderer.WaitForAsync("md-gauge"));

        Assert.Matches(@"<td><span class=""md-gauge is-bar is-medium is-warm"" role=""meter""[^>]*aria-valuenow=""141\.8"" aria-valuemin=""32"" aria-valuemax=""212"" aria-valuetext=""141\.8°F, warning"" aria-label=""CPU heat""", html);
        Assert.Contains("<span class=\"md-gauge-fill\" style=\"width: 61%\"></span>", html);
        // The warning and critical marks, at 50 and 70 on a 0–100 °C scale.
        Assert.Contains("<span class=\"md-gauge-mark\" style=\"left: 50%\"></span><span class=\"md-gauge-mark\" style=\"left: 70%\"></span>", html);
        Assert.Contains("<span class=\"md-gauge-label\" aria-hidden=\"true\">CPU heat</span>", html);
    }

    [Theory]
    [InlineData("{{gauge: Nowhere / disk_percent}}", "No connection called “Nowhere”")]
    [InlineData("{{gauge: NAS / nothing_here}}", "has not reported a metric called “nothing_here”")]
    [InlineData("{{gauge: NAS / disk_percent style=dial}}", "Use ring or bar")]
    public async Task AGaugeThatCannotBeDrawnSaysWhy(string markdown, string why)
    {
        await ConnectionAsync("NAS");
        await _renderer.RenderMarkdownAsync(markdown);
        Assert.Contains(why, Text(await _renderer.WaitForAsync("sc-problem")));
    }

    // ---------- diagrams ----------

    [Fact]
    public async Task ADiagramColoursTheConnectionsItNamesAndLinksToThem()
    {
        _stub.Down.Add("Router");
        var router = await ConnectionAsync("Router");
        var nas = await ConnectionAsync("QNAP NAS");
        await ConnectionAsync("Plex", silencedUntil: DateTimeOffset.Now.AddHours(1));

        await _renderer.RenderMarkdownAsync("""
            ```diagram
            Internet -> Router -> "QNAP NAS" -> Plex
            "QNAP NAS" ->|nightly| "<script>alert(1)</script>"
            ```
            """);
        var raw = await _renderer.WaitForAsync(h => h.Contains("is-down", StringComparison.Ordinal));
        var html = Flat(raw);

        Assert.Contains("<figure class=\"md-embed md-diagram\" role=\"group\" aria-label=\"Diagram\">", html);
        Assert.Contains($"<a class=\"md-diagram-node is-down\" href=\"settings/connections?edit={router.Id}\" aria-label=\"Router — down. Click to edit.\">", html);
        Assert.Contains($"<a class=\"md-diagram-node is-up\" href=\"settings/connections?edit={nas.Id}\"", html);
        Assert.Contains("<a class=\"md-diagram-node is-maintenance\"", html);
        Assert.Contains("<g class=\"md-diagram-node is-neutral\"><title>Internet</title>", html);
        Assert.Contains(">nightly</text>", html);
        Assert.Contains("Diagram of Internet, Router (down), QNAP NAS (up), Plex (maintenance)", html);
        // Every label is text: the script is shown, encoded, and never becomes an element.
        Assert.Contains("&lt;script&gt;", raw);
        Assert.DoesNotContain("<script", raw);

        // The router comes back: its box changes colour without the note being touched.
        _stub.Down.Clear();
        await Get<HealthMonitor>().RefreshAsync(router);
        html = Flat(await _renderer.WaitForAsync(h => !h.Contains("is-down", StringComparison.Ordinal)));
        Assert.Contains($"<a class=\"md-diagram-node is-up\" href=\"settings/connections?edit={router.Id}\"", html);
    }

    [Fact]
    public async Task AMermaidFlowchartIsDrawnAndASequenceDiagramStaysCode()
    {
        await ConnectionAsync("NAS");
        await _renderer.RenderMarkdownAsync("""
            ```mermaid
            graph TD
              R[Router] --> N[NAS]
            ```

            ```mermaid
            sequenceDiagram
              A->>B: hi
            ```
            """);
        var html = Flat(await _renderer.WaitForAsync("md-diagram-node is-up"));

        Assert.Contains("<a class=\"md-diagram-node is-up\"", html);
        Assert.Contains("<g class=\"md-diagram-node is-neutral\"><title>Router</title>", html);
        Assert.Contains("<pre class=\"mermaid\">sequenceDiagram", html);
    }

    [Fact]
    public async Task ADiagramThatCannotBeReadSaysWhereAndTheRestOfTheNoteStillDraws()
    {
        await _renderer.RenderMarkdownAsync("Before.\n\n```diagram\nA -> \n```\n\nAfter.");
        var html = Text(await _renderer.WaitForAsync("sc-problem"));

        Assert.Contains("Line 1 has an arrow with nothing at one end", html);
        Assert.Contains("<p>Before.</p>", html);
        Assert.Contains("<p>After.</p>", html);
    }

    [Fact]
    public async Task DiagramAutoIsTheDependencyMap()
    {
        var router = await ConnectionAsync("Router");
        await ConnectionAsync("NAS", dependsOn: router.Id);

        await _renderer.RenderMarkdownAsync("{{diagram: auto title=\"What sits behind what\"}}");
        var html = Flat(await _renderer.WaitForAsync("depmap-svg"));

        Assert.Contains("<figure class=\"md-embed md-diagram is-auto\" role=\"group\" aria-label=\"What sits behind what\">", html);
        Assert.Contains("<figcaption class=\"md-diagram-title\">What sits behind what</figcaption>", html);
        Assert.Contains("depmap-node is-up", html);
        Assert.Contains("NAS", html);
    }

    // ---------- the runbook somebody already has ----------

    /// <summary>
    /// Every kind the note in use today has in it, in one page. Nothing new may change how
    /// any of it draws: the card gauge is still the card, a bash block is still code with its
    /// braces as text, and no table, gauge or diagram appears that was not asked for.
    /// </summary>
    [Fact]
    public async Task TheExistingRunbookStillDrawsEveryPartAsBefore()
    {
        var nas = await ConnectionAsync("QNAP NAS");
        for (var i = 0; i < 6; i++)
            await Get<HistoryStore>().RecordAsync(nas.Id, new Dictionary<string, double> { ["disk_percent"] = 90 + i }, CancellationToken.None);

        await _renderer.RenderMarkdownAsync("""
            # When something breaks

            {{today}} · the NAS was checked {{ago: QNAP NAS}} · renewal {{countdown: 2099-01-01}}

            {{down}}

            {{if down: QNAP NAS}}
            ## The NAS is down
            {{else}}
            The NAS is {{status: QNAP NAS}}, up for {{since: QNAP NAS}} ({{uptime: QNAP NAS}}) {{uptimebar: QNAP NAS days=14}}
            {{end}}

            {{alerts}}

            {{containers: stopped}}

            | What | Now |
            |---|---|
            | CPU | {{metric: QNAP NAS / cpu_percent}} |
            | Disk | {{sparkline: QNAP NAS / disk_percent 24h}} |

            Full {{forecast: QNAP NAS}}.

            {{card: gauge connection="QNAP NAS" metric="disk_percent" title="NAS storage"}}

            {{details: Restart steps}}
            1. {{button: QNAP NAS / restart}}
            2. Open {{link: QNAP NAS}}
            {{end}}

            {{renewals}}

            > [!WARNING]
            > Stop Plex before restarting the NAS.

            ```bash
            docker restart plex  # {{status: QNAP NAS}} stays as written
            ```

            Inline `{{metric: QNAP NAS / cpu_percent}}` is an example.
            """);
        var html = Flat(await _renderer.WaitForAsync(h => h.Contains("<polyline", StringComparison.Ordinal)
                                                          && h.Contains("No alerts firing", StringComparison.Ordinal)
                                                          && h.Contains("role=\"meter\"", StringComparison.Ordinal)));

        Assert.Contains("<h1 id=\"when-something-breaks\">When something breaks</h1>", html);
        Assert.Contains("in ", html); // the countdown, in days
        Assert.Contains("Everything", html); // {{down}} says everything is up
        Assert.DoesNotContain("The NAS is down", html);
        Assert.Contains("The NAS is <span class=\"sc-value\"", html);
        Assert.Contains("<span class=\"status-dot status-up\" aria-hidden=\"true\"></span>up</span>", html);
        Assert.Contains("No alerts firing", html);
        Assert.Matches(@"<td>CPU</td>\s*<td><span class=""sc-value""[^>]*>12%</span></td>", html);
        Assert.Matches(@"<td>Disk</td>\s*<td><span class=""md-spark""", html);
        // The gauge card is still the card, not the new inline gauge.
        Assert.Contains("<div class=\"gauge\" role=\"meter\"", html);
        Assert.Contains("NAS storage", html);
        Assert.Contains("<details class=\"md-details\"><summary class=\"md-details-summary\">Restart steps</summary>", html);
        Assert.Contains("<div class=\"md-callout md-callout-warning\" role=\"note\">", html);
        Assert.Contains("<pre><code class=\"language-bash\">docker restart plex  # {{status: QNAP NAS}} stays as written", html);
        Assert.Contains("<code>{{metric: QNAP NAS / cpu_percent}}</code>", html);

        Assert.DoesNotContain("md-gauge", html);
        Assert.DoesNotContain("md-live-table", html);
        Assert.DoesNotContain("md-diagram", html);
    }

    // ---------- the promise ----------

    [Fact]
    public async Task OnceTheCachesAreWarmNoneOfThemReadsTheDatabase()
    {
        var nas = await ConnectionAsync("QNAP NAS");
        await ConnectionAsync("Pi");
        await Get<AlertRuleStore>().SaveAsync(new AlertRule { ConnectionId = nas.Id, Metric = "disk_percent", Threshold = 90 });
        const string note = """
            {{table: "QNAP NAS", Pi / status, disk_percent, cpu_percent, temp_c}}

            Storage is {{gauge: "QNAP NAS" / disk_percent}} and {{gauge: Pi / temp_c style=bar}}.

            ```diagram
            Internet -> "QNAP NAS" -> Pi
            ```
            """;

        // The first page may read what is not in memory yet: the connections, the rules,
        // each connection's stored readings.
        await _renderer.RenderMarkdownAsync(note);
        await _renderer.WaitForAsync(h => h.Contains("is-critical", StringComparison.Ordinal) && h.Contains("md-diagram-node is-up", StringComparison.Ordinal));
        await Task.Delay(200);

        var before = Get<Db>().Opens;
        await using var second = new InteractiveRenderer(_services);
        await second.RenderMarkdownAsync(note);
        var html = await second.WaitForAsync(h => h.Contains("is-critical", StringComparison.Ordinal)
                                                  && h.Contains("md-diagram-node is-up", StringComparison.Ordinal)
                                                  && h.Contains("md-gauge is-bar", StringComparison.Ordinal));
        await Task.Delay(300);
        Assert.Contains("md-live-table", html);
        Assert.Equal(before, Get<Db>().Opens);

        // A probe landing redraws every one of them. The probe records its reading — a write
        // the monitor makes, not the page — so what is counted is from after it.
        _stub.Disk = 50;
        await Get<HealthMonitor>().RefreshAsync(nas);
        var afterProbe = Get<Db>().Opens;
        await second.WaitForAsync("50.0%");
        await Task.Delay(300);
        Assert.Equal(afterProbe, Get<Db>().Opens);
    }
}
