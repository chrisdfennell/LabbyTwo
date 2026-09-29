#pragma warning disable BL0006 // Reading the render tree back into HTML is the whole point of the renderer below.
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using LabbyTwo.Components.Shared;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabbyTwo.Tests;

/// <summary>
/// A renderer that says it is interactive, so components start their background loads the
/// way they do in a browser — the framework's own HTML renderer is "static", and a static
/// render deliberately loads nothing. What it has drawn is read back as HTML from the render
/// tree: elements, attributes, text (encoded), markup and child components.
/// </summary>
internal sealed partial class InteractiveRenderer(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
{
    [System.Text.RegularExpressions.GeneratedRegex("^b-[a-z0-9]{10}$")]
    private static partial System.Text.RegularExpressions.Regex ScopeAttribute();

    private int _root = -1;

    public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

    protected override RendererInfo RendererInfo { get; } = new("Test", isInteractive: true);

    protected override void HandleException(Exception exception) => ExceptionDispatchInfo.Capture(exception).Throw();

    protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;

    public Task RenderAsync<T>(IDictionary<string, object?> parameters) where T : IComponent => Dispatcher.InvokeAsync(async () =>
    {
        _root = AssignRootComponentId(InstantiateComponent(typeof(T)));
        await RenderRootComponentAsync(_root, ParameterView.FromDictionary(parameters));
    });

    public Task RenderMarkdownAsync(string markdown) =>
        RenderAsync<LiveMarkdown>(new Dictionary<string, object?> { [nameof(LiveMarkdown.Content)] = markdown });

    /// <summary>The page as a browser would be sent it: text encoded, attributes quoted.</summary>
    public Task<string> HtmlAsync() => Dispatcher.InvokeAsync(() =>
    {
        var html = new StringBuilder();
        WriteComponent(html, _root);
        return html.ToString();
    });

    /// <summary>The HTML once <paramref name="done"/> says so, or whatever it was when time ran out.</summary>
    public async Task<string> WaitForAsync(Func<string, bool> done, TimeSpan? deadline = null)
    {
        var until = DateTime.UtcNow + (deadline ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            var html = await HtmlAsync();
            if (done(html) || DateTime.UtcNow > until)
                return html;
            await Task.Delay(25);
        }
    }

    public Task<string> WaitForAsync(string expected) =>
        WaitForAsync(html => WebUtility.HtmlDecode(html).Contains(expected, StringComparison.Ordinal));

    private void WriteComponent(StringBuilder html, int componentId)
    {
        var frames = GetCurrentRenderTreeFrames(componentId);
        WriteFrames(html, frames.Array, 0, frames.Count);
    }

    private void WriteFrames(StringBuilder html, RenderTreeFrame[] frames, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            var frame = frames[i];
            switch (frame.FrameType)
            {
                case RenderTreeFrameType.Element:
                {
                    html.Append('<').Append(frame.ElementName);
                    var child = i + 1;
                    var last = i + frame.ElementSubtreeLength;
                    for (; child < last && frames[child].FrameType == RenderTreeFrameType.Attribute; child++)
                    {
                        var attribute = frames[child];
                        // The scoped-CSS marker (b-xxxxxxxxxx) says nothing about the page.
                        if (ScopeAttribute().IsMatch(attribute.AttributeName))
                            continue;
                        switch (attribute.AttributeValue)
                        {
                            case bool:
                                html.Append(' ').Append(attribute.AttributeName);
                                break;
                            case string or IFormattable:
                                html.Append(' ').Append(attribute.AttributeName).Append("=\"")
                                    .Append(WebUtility.HtmlEncode(Convert.ToString(attribute.AttributeValue, System.Globalization.CultureInfo.InvariantCulture)))
                                    .Append('"');
                                break;
                        }
                    }
                    html.Append('>');
                    WriteFrames(html, frames, child, last);
                    html.Append("</").Append(frame.ElementName).Append('>');
                    i = last - 1;
                    break;
                }
                case RenderTreeFrameType.Text:
                    html.Append(WebUtility.HtmlEncode(frame.TextContent));
                    break;
                case RenderTreeFrameType.Markup:
                    html.Append(frame.MarkupContent);
                    break;
                case RenderTreeFrameType.Component:
                    WriteComponent(html, frame.ComponentId);
                    i += frame.ComponentSubtreeLength - 1;
                    break;
                case RenderTreeFrameType.Region:
                    WriteFrames(html, frames, i + 1, i + frame.RegionSubtreeLength);
                    i += frame.RegionSubtreeLength - 1;
                    break;
            }
        }
    }
}

/// <summary>
/// The second batch of Markdown features drawn by the real components against a real
/// database: folds, callouts with live values in them, lists of alerts, containers and
/// renewals, the sparkline and uptime strip filling in after the page has drawn, links,
/// dates — and script in any argument staying text.
/// </summary>
public sealed class MarkdownExtrasRenderTests : IAsyncDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private readonly InteractiveRenderer _renderer;
    private readonly Stub _stub = new();

    /// <summary>A connection whose probe reports whatever the test sets.</summary>
    private sealed class Stub : IConnectionProvider
    {
        public string Type => "stub";
        public string DisplayName => "Stub";
        public string Icon => "";
        public string Description => "";
        public IReadOnlyList<FieldSpec> Fields => [];
        public IReadOnlyList<MetricSpec> Metrics => [new("disk_percent", "Disk used", "%", 1), new("latency_ms", "Response time", " ms")];

        public bool Up { get; set; } = true;
        public double Disk { get; set; } = 50;

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
            Task.FromResult(Up
                ? ProbeResult.Up(TimeSpan.FromMilliseconds(3), "OK", new Dictionary<string, double> { ["disk_percent"] = Disk })
                : ProbeResult.Down(TimeSpan.Zero, "Connection refused"));
    }

    public MarkdownExtrasRenderTests()
    {
        Directory.CreateDirectory(_directory);
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        // One failed probe is down, so a test can take something down in one step.
        services.AddTestStorage(_directory, options => options.FailuresBeforeDown = 1);
        services.AddSingleton<IConnectionProvider>(_stub);
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

    private async Task<Connection> ConnectionAsync(string name, string provider = "stub", params (string Key, string Value)[] settings)
    {
        var bag = new SettingsBag();
        foreach (var (key, value) in settings)
            bag[key] = value;
        var connection = new Connection { Provider = provider, Name = name, Settings = bag };
        await Get<ConfigStore>().SaveConnectionAsync(connection);
        return connection;
    }

    private async Task SqlAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var db = await Get<Db>().OpenAsync();
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private Task SampleAsync(string connectionId, string metric, DateTimeOffset at, double value) =>
        SqlAsync("INSERT INTO samples (connection_id, metric, ts, value) VALUES ($c, $m, $t, $v)",
            ("$c", connectionId), ("$m", metric), ("$t", at.ToUnixTimeSeconds()), ("$v", value));

    private static string Text(string html) => WebUtility.HtmlDecode(html);

    // ---------- folds ----------

    [Fact]
    public async Task AFoldIsANativeDetailsHoldingLiveParts()
    {
        var nas = await ConnectionAsync("NAS");
        await Get<HealthMonitor>().RefreshAsync(nas);

        await _renderer.RenderMarkdownAsync("""
            Intro.

            {{details: Full restart procedure}}
            1. The NAS is {{status: NAS}}.
            {{if up: NAS}}
            It is up, so stop Plex first.
            {{end}}
            {{end}}

            {{details: Already open open=true}}
            Shown.
            {{end}}
            """);
        var html = Text(await _renderer.WaitForAsync("stop Plex first"));

        Assert.Contains("<details class=\"md-details\"><summary class=\"md-details-summary\">Full restart procedure</summary>", html);
        Assert.Contains("<details class=\"md-details\" open><summary class=\"md-details-summary\">Already open</summary>", html);
        Assert.Matches("<details class=\"md-details\">.*<li>The NAS is <span class=\"sc-value\"[^>]*><span class=\"status-dot status-up\"", html.ReplaceLineEndings(" "));
        Assert.DoesNotContain("{{", html);

        // The condition inside the fold is still decided as the lab changes.
        _stub.Up = false;
        await Get<HealthMonitor>().RefreshAsync(nas);
        await Get<HealthMonitor>().RefreshAsync(nas);
        html = Text(await _renderer.WaitForAsync(h => !h.Contains("stop Plex first", StringComparison.Ordinal)));
        Assert.DoesNotContain("stop Plex first", html);
        Assert.Contains("Full restart procedure", html);
    }

    [Fact]
    public async Task AMismatchedEndIsAQuestionMark()
    {
        await _renderer.RenderMarkdownAsync("{{details: Steps}}\nA\n{{else}}\nB\n{{end}}\n{{end}}\n");
        var html = Text(await _renderer.WaitForAsync("sc-problem"));

        Assert.Contains("is inside {{details: Steps}}, which has no “otherwise”", html);
        Assert.Contains("{{end}} on line 6 has no {{if …}} or {{details: …}} to end.", html);
    }

    // ---------- callouts ----------

    [Fact]
    public async Task ACalloutKeepsItsFrameAroundALiveValue()
    {
        var nas = await ConnectionAsync("NAS");
        await Get<HealthMonitor>().RefreshAsync(nas);

        await _renderer.RenderMarkdownAsync("> [!WARNING]\n> The NAS is {{status: NAS}} — check before you restart it.\n");
        var html = Text(await _renderer.WaitForAsync("status-dot status-up"));

        Assert.Contains("<div class=\"md-callout md-callout-warning\" role=\"note\">", html);
        Assert.Contains("</svg>Warning</p>", html);
        Assert.Contains("check before you restart it.", html);
    }

    // ---------- {{alerts}} ----------

    [Fact]
    public async Task AlertsListWhatIsFiringAndSayWhenNothingIs()
    {
        var nas = await ConnectionAsync("NAS");
        await Get<AlertRuleStore>().SaveAsync(new AlertRule { Name = "Disk nearly full", Metric = "disk_percent", Threshold = 90 });

        await _renderer.RenderMarkdownAsync("{{alerts}}");
        Assert.Contains("No alerts firing", Text(await _renderer.WaitForAsync("No alerts firing")));

        _stub.Disk = 95;
        await Get<HealthMonitor>().RefreshAsync(nas);
        await Get<MetricAlertService>().EvaluateAsync(DateTimeOffset.Now, CancellationToken.None);

        var html = Text(await _renderer.WaitForAsync("Disk nearly full"));
        Assert.Contains("<strong>Disk nearly full</strong>", html);
        Assert.Contains("<span class=\"tile-meta\">NAS</span>", html);
        Assert.Contains("Disk used is <span class=\"md-alert-value\">95.0%</span> — above 90.0%", html);
        Assert.Contains("for under a minute", html);
        Assert.DoesNotContain("No alerts firing", html);
    }

    [Fact]
    public async Task AlertsNamingNothingSayWhich()
    {
        await ConnectionAsync("NAS");
        await _renderer.RenderMarkdownAsync("{{alerts: only=\"Nowhere\"}}");
        var html = Text(await _renderer.WaitForAsync("sc-problem"));

        Assert.Contains("No connection called “Nowhere”.", html);
        Assert.Contains("No alerts firing", html);
    }

    [Fact]
    public async Task AListInsideASentenceIsAQuestionMark()
    {
        await _renderer.RenderMarkdownAsync("See {{alerts}} for more.");
        var html = Text(await _renderer.WaitForAsync("sc-problem"));

        Assert.Contains("{{alerts}} goes on a line of its own, not inside a sentence.", html);
    }

    // ---------- {{containers}} ----------

    private const string ContainerList = """
        [{"Id":"aaa111","Names":["/sonarr"],"Image":"lscr.io/linuxserver/sonarr:latest","State":"running","Status":"Up 3 hours (healthy)","Labels":{"com.docker.compose.project":"media"}},
         {"Id":"bbb222","Names":["/radarr"],"Image":"lscr.io/linuxserver/radarr:latest","State":"running","Status":"Up 5 minutes (unhealthy)","Labels":{"com.docker.compose.project":"media"}},
         {"Id":"ccc333","Names":["/old-backup"],"Image":"alpine:3","State":"exited","Status":"Exited (137) 2 days ago","Labels":{}}]
        """;

    [Fact]
    public async Task ContainersShowWaitingThenTheList()
    {
        var release = new TaskCompletionSource();
        using var docker = new ScriptedDocker(async context =>
        {
            if (context.Request.Url!.AbsolutePath.EndsWith("/containers/json", StringComparison.Ordinal))
            {
                await release.Task;
                await ScriptedDocker.Json(context, ContainerList);
            }
            else
            {
                await ScriptedDocker.NoSuchContainer(context);
            }
        });
        await ConnectionAsync("Docker", "docker", ("endpoint", docker.Endpoint));

        await _renderer.RenderMarkdownAsync("{{containers: all}}\n\n{{containers: stopped}}\n\n{{containers: unhealthy}}");
        // Drawn and waiting while Docker has not answered: nothing waited for it.
        var waiting = await _renderer.WaitForAsync(h => h.Contains("is-waiting", StringComparison.Ordinal) && !h.Contains("Loading", StringComparison.Ordinal));
        Assert.Contains("md-down is-waiting", waiting);
        Assert.DoesNotContain("sonarr", waiting);

        release.SetResult();
        var html = Text(await _renderer.WaitForAsync(h => !h.Contains("is-waiting", StringComparison.Ordinal)));

        Assert.Contains("<strong class=\"text-truncate\">sonarr</strong>", html);
        Assert.Contains("unhealthy</span>", html);
        Assert.Contains("title=\"Compose project\">media</span>", html);
        Assert.Contains("· Exited (137) 2 days ago", html);
        Assert.Contains("status-dot status-down", html);
        // Three lists, one request: the list is shared.
        Assert.Single(docker.Requests, r => r.Contains("/containers/json", StringComparison.Ordinal));
        // Read-only: nothing to press.
        Assert.DoesNotContain("<button", html);
    }

    [Fact]
    public async Task AProxyThatRefusesTheListSaysWhichFlag()
    {
        using var docker = new ScriptedDocker(ScriptedDocker.Forbidden);
        await ConnectionAsync("Docker", "docker", ("endpoint", docker.Endpoint));

        await _renderer.RenderMarkdownAsync("{{containers: stopped}}");
        var html = Text(await _renderer.WaitForAsync("CONTAINERS=1"));

        Assert.Contains("sc-problem", html);
        Assert.Contains("Docker:", html);
    }

    [Fact]
    public async Task NoDockerConnectionIsAQuestion()
    {
        await _renderer.RenderMarkdownAsync("{{containers: running}}");
        var html = Text(await _renderer.WaitForAsync("sc-problem"));

        Assert.Contains("There is no Docker connection", html);
    }

    // ---------- {{renewals}} ----------

    [Fact]
    public async Task RenewalsComeFromCountdownMetrics()
    {
        var soon = await ConnectionAsync("shop.example.com", "certificate");
        var gone = await ConnectionAsync("old.example.com", "certificate");
        var later = await ConnectionAsync("example.com", "certificate");
        var now = DateTimeOffset.UtcNow;
        await SampleAsync(soon.Id, "cert_days_left", now, 9);
        await SampleAsync(gone.Id, "cert_days_left", now, -2);
        await SampleAsync(later.Id, "cert_days_left", now, 120);

        await _renderer.RenderMarkdownAsync("{{renewals}}");
        var html = Text(await _renderer.WaitForAsync(h => Text(h).Contains("expired 2 days ago", StringComparison.Ordinal)
                                                          && Text(h).Contains("in 9 days", StringComparison.Ordinal)));

        Assert.True(html.IndexOf("old.example.com", StringComparison.Ordinal) < html.IndexOf("shop.example.com", StringComparison.Ordinal));
        Assert.Contains("md-down-row is-overdue", html);
        // Past the sixty-day window.
        Assert.DoesNotContain(">example.com<", html);

        await _renderer.RenderMarkdownAsync("{{renewals: days=200 limit=1}}");
        html = Text(await _renderer.WaitForAsync("expired 2 days ago"));
        Assert.DoesNotContain("in 9 days", html);
    }

    [Fact]
    public async Task NoRenewalsSaysSo()
    {
        await _renderer.RenderMarkdownAsync("{{renewals: days=30}}");
        Assert.Contains("Nothing due in the next 30 days", Text(await _renderer.WaitForAsync("Nothing due")));
    }

    // ---------- {{sparkline}} ----------

    [Fact]
    public async Task ASparklineFillsInAfterThePageAndFollowsNewReadings()
    {
        var nas = await ConnectionAsync("QNAP NAS");
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 48; i++)
            await SampleAsync(nas.Id, "disk_percent", now.AddMinutes(-30 * (48 - i)), 40 + i % 5);
        await SampleAsync(nas.Id, "disk_percent", now.AddSeconds(-5), 42);

        await _renderer.RenderMarkdownAsync("Disk {{sparkline: QNAP NAS / Disk used 24h}} lately.");
        var html = Text(await _renderer.WaitForAsync("<polyline"));

        Assert.Contains("<span class=\"md-spark\" title=\"QNAP NAS · Disk used over the last 24 hours: now 42.0% (low 40.0%, high 44.0%)\">", html);
        Assert.Contains("<p>Disk <span class=\"md-spark\"", html);
        Assert.Contains("lately.</p>", html);

        await Get<HistoryStore>().RecordAsync(nas.Id, new Dictionary<string, double> { ["disk_percent"] = 77 }, CancellationToken.None);
        html = Text(await _renderer.WaitForAsync("now 77.0%"));
        Assert.Contains("high 77.0%", html);
    }

    [Theory]
    [InlineData("{{sparkline: QNAP NAS / disk_percent 45d}}", "between 1h and 30d")]
    [InlineData("{{sparkline: QNAP NAS / nothing_here}}", "has not reported a metric called “nothing_here”")]
    [InlineData("{{sparkline: Nowhere / disk_percent}}", "No connection called “Nowhere”")]
    public async Task ASparklineThatCannotBeDrawnSaysWhy(string markdown, string why)
    {
        await ConnectionAsync("QNAP NAS");
        await _renderer.RenderMarkdownAsync(markdown);
        Assert.Contains(why, Text(await _renderer.WaitForAsync("sc-problem")));
    }

    // ---------- {{uptimebar}} ----------

    [Fact]
    public async Task TheUptimeBarColoursDaysAsTheStatusPageDoes()
    {
        var nas = await ConnectionAsync("QNAP NAS");
        var today = DateTime.Now.Date;
        // Monitored from ten days ago; down for four hours at noon three days ago.
        await SqlAsync("INSERT INTO status_events (connection_id, ts, is_up, message) VALUES ($c, $t, 1, '')",
            ("$c", nas.Id), ("$t", new DateTimeOffset(today.AddDays(-10).AddHours(9)).ToUnixTimeSeconds()));
        await SqlAsync("INSERT INTO status_events (connection_id, ts, is_up, message) VALUES ($c, $t, 0, 'down')",
            ("$c", nas.Id), ("$t", new DateTimeOffset(today.AddDays(-3).AddHours(12)).ToUnixTimeSeconds()));
        await SqlAsync("INSERT INTO status_events (connection_id, ts, is_up, message) VALUES ($c, $t, 1, '')",
            ("$c", nas.Id), ("$t", new DateTimeOffset(today.AddDays(-3).AddHours(16)).ToUnixTimeSeconds()));

        await _renderer.RenderMarkdownAsync("Last fortnight: {{uptimebar: QNAP NAS days=14}}");
        var html = Text(await _renderer.WaitForAsync("uptime-day"));

        var cells = System.Text.RegularExpressions.Regex.Matches(html, "class=\"uptime-day (is-[a-z]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(14, cells.Count);
        Assert.Equal("is-bad", cells[^4]);
        Assert.Equal(3, cells.Count(c => c == "is-unknown"));
        Assert.Equal(10, cells.Count(c => c == "is-good"));
        Assert.Contains("aria-label=\"QNAP NAS · daily uptime over the last 14 days\"", html);
    }

    // ---------- {{link}} ----------

    [Fact]
    public async Task ALinkOpensTheConnectionsOwnPage()
    {
        await ConnectionAsync("Plex Media Server", "stub", ("url", "http://plex:32400"), ("open_url", "https://plex.example.com/web"));
        await ConnectionAsync("Ping only");

        await _renderer.RenderMarkdownAsync("Open {{link: Plex Media Server}} or {{link: Plex Media Server label=\"Plex\"}}; {{link: Ping only}}.");
        var html = Text(await _renderer.WaitForAsync("sc-problem"));

        Assert.Contains("<a class=\"md-link\" href=\"https://plex.example.com/web\" target=\"_blank\" rel=\"noopener noreferrer\" title=\"https://plex.example.com/web\">Plex Media Server</a>", html);
        Assert.Contains(">Plex</a>", html);
        Assert.Contains("Ping only has no web address", html);
    }

    // ---------- {{today}}, {{countdown}}, {{ago}} ----------

    private Task<string> ClockAsync(string shortcode, DateTimeOffset now) =>
        _renderer.RenderAsync<LiveClock>(new Dictionary<string, object?>
        {
            [nameof(LiveClock.Code)] = Shortcodes.Parse(shortcode),
            [nameof(LiveClock.Clock)] = (Func<DateTimeOffset>)(() => now),
        }).ContinueWith(_ => _renderer.HtmlAsync()).Unwrap();

    [Fact]
    public async Task DatesReadAsWords()
    {
        var tuesday = new DateTimeOffset(new DateTime(2026, 9, 29, 10, 0, 0, DateTimeKind.Local));

        Assert.Contains(">Tuesday, 29 September 2026</span>", Text(await ClockAsync("{{today}}", tuesday)));
        Assert.Contains(">2026-09-29</span>", Text(await ClockAsync("{{today: format=iso}}", tuesday)));
        Assert.Contains(">today</span>", Text(await ClockAsync("{{countdown: 2026-09-29}}", tuesday)));
        Assert.Contains(">tomorrow</span>", Text(await ClockAsync("{{countdown: 2026-09-30}}", tuesday)));
        Assert.Contains(">Christmas in 87 days</span>", Text(await ClockAsync("{{countdown: 2026-12-25 label=Christmas}}", tuesday)));
        Assert.Contains(">3 days ago</span>", Text(await ClockAsync("{{countdown: 2026-09-26}}", tuesday)));
        Assert.Contains("is not a date", Text(await ClockAsync("{{countdown: 2026-13-01}}", tuesday)));
        Assert.Contains("sc-problem", await ClockAsync("{{today: format=\"%Q\"}}", tuesday));
    }

    [Fact]
    public async Task AgoCountsUpBetweenSweeps()
    {
        var nas = await ConnectionAsync("QNAP NAS");
        await Get<HealthMonitor>().RefreshAsync(nas);

        await _renderer.RenderMarkdownAsync("Checked {{ago: QNAP NAS}}.");
        var first = Text(await _renderer.WaitForAsync(" s ago"));
        var seconds = int.Parse(System.Text.RegularExpressions.Regex.Match(first, @"(\d+) s ago").Groups[1].Value);

        // No sweep runs: only its own timer can move it on.
        var later = Text(await _renderer.WaitForAsync(h => System.Text.RegularExpressions.Regex.Match(Text(h), @"(\d+) s ago") is { Success: true } m
                                                           && int.Parse(m.Groups[1].Value) > seconds + 1, TimeSpan.FromSeconds(6)));
        Assert.Matches(@"Checked <span class=""sc-value md-clock"" title=""QNAP NAS was last checked at [0-9:]+"">\d+ s ago</span>\.", later);
        Assert.NotEqual(first, later);
    }

    // ---------- script stays text ----------

    [Fact]
    public async Task ScriptInAnyArgumentStaysText()
    {
        await ConnectionAsync("Plex", "stub", ("url", "http://plex:32400"));

        await _renderer.RenderMarkdownAsync("""
            {{details: <script>alert(1)</script>}}
            x
            {{end}}

            {{link: Plex label="<img src=x onerror=alert(2)>"}} {{countdown: 2026-12-25 label="<script>alert(3)</script>"}}
            {{today: format="<b>"}} {{sparkline: "<script>alert(4)</script>" / disk_percent}} {{uptimebar: "<svg onload=alert(5)>"}}

            {{alerts: only="<script>alert(6)</script>"}}

            {{containers: "<script>alert(7)</script>"}}

            > [!NOTE]
            > <script>alert(8)</script>
            """);
        var raw = await _renderer.WaitForAsync(h => h.Contains("alert(7)", StringComparison.Ordinal) && h.Contains("alert(6)", StringComparison.Ordinal)
                                                     && h.Contains("alert(5)", StringComparison.Ordinal) && h.Contains("&lt;img", StringComparison.Ordinal));

        Assert.DoesNotContain("<script", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<svg onload", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<b>", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;alert(1)", raw);
        Assert.Contains("&lt;script&gt;alert(8)", raw);
    }
}
