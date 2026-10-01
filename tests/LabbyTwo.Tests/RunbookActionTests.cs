#pragma warning disable BL0006 // The InteractiveRenderer reads the render tree back as HTML, which is the whole point of these tests.
using System.Buffers.Binary;
using System.Net;
using System.Text;
using LabbyTwo.Components.Shared;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace LabbyTwo.Tests;

/// <summary>
/// The runbook action shortcodes against a real database, a Docker Engine answering over
/// real HTTP and a machine that runs commands from a script: <c>{{ssh}}</c> and the runner's
/// guardrails, <c>{{logs}}</c> reading on demand and masked, <c>{{run}}</c> from the
/// scheduler's memory — and a note using every older shortcode, drawn with copy buttons on
/// its code, still drawing exactly what it did.
/// </summary>
public sealed class RunbookActionTests : IAsyncDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private readonly InteractiveRenderer _renderer;
    private readonly Shell _shell = new();
    private readonly Plans _plans = new();

    /// <summary>A machine that "runs" commands: answers from the test, can be held mid-command, counts.</summary>
    private sealed class Shell : IConnectionProvider, ICommandRunner
    {
        public string Type => "shell";
        public string DisplayName => "Shell";
        public string Icon => "";
        public string Description => "";
        public IReadOnlyList<FieldSpec> Fields => [RunbookCommands.AllowField];
        public IReadOnlyList<MetricSpec> Metrics => [new("disk_percent", "Disk used", "%", 1) { Capacity = CapacityLimit.Percent }];
        public IReadOnlyList<ProviderAction> Actions => [new("restart", "Restart")];

        public List<(string Command, TimeSpan Timeout)> Ran { get; } = [];
        public CommandResult Result { get; set; } = new(0, "done\n");
        public TaskCompletionSource? Hold { get; set; }
        public double Disk { get; set; } = 40;

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
            Task.FromResult(ProbeResult.Up(TimeSpan.FromMilliseconds(2), "OK", new Dictionary<string, double> { ["disk_percent"] = Disk }));

        public Task<ActionResult> RunActionAsync(Connection connection, ProviderAction action, SettingsBag input, CancellationToken ct) =>
            Task.FromResult(ActionResult.Done());

        public async Task<CommandResult> RunCommandAsync(Connection connection, string command, TimeSpan timeout, CancellationToken ct)
        {
            lock (Ran)
                Ran.Add((command, timeout));
            if (Hold is { } hold)
                await hold.Task.WaitAsync(ct);
            return Result;
        }

        public int Count
        {
            get
            {
                lock (Ran)
                    return Ran.Count;
            }
        }
    }

    /// <summary>Scheduled actions that restart nothing and say so.</summary>
    private sealed class Plans : IScheduledActionPlans
    {
        public int Ran;

        public Task<string> DescribeAsync(ScheduledAction action, CancellationToken ct) => Task.FromResult($"restart {action.Container}");

        public Task<RemediationPlan> PrepareAsync(ScheduledAction action, CancellationToken ct) =>
            Task.FromResult(new RemediationPlan($"restart {action.Container}", $"Restarted {action.Container}", null, _ =>
            {
                Interlocked.Increment(ref Ran);
                return Task.FromResult(ActionResult.Done($"Restarted {action.Container}."));
            }));

        public Task<string?> SaveProblemAsync(ScheduledAction action, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private sealed class QuietBoundaryLogger : IErrorBoundaryLogger
    {
        public ValueTask LogErrorAsync(Exception exception) => ValueTask.CompletedTask;
    }

    /// <summary>Somebody logged in as "chris", as the app's cookie login would say.</summary>
    private sealed class SignedIn : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "chris")], "test"))));
    }

    public RunbookActionTests() : this(login: true)
    {
    }

    private RunbookActionTests(bool login)
    {
        _services = TestHost.Build(_directory, services =>
        {
            var options = new LabbyOptions { DatabasePath = Path.Combine(_directory, "test.db"), FailuresBeforeDown = 1 };
            if (login)
                options.Auth.Password = "secret";
            services.AddSingleton(Options.Create(options));
            services.AddSingleton<IConnectionProvider>(_shell);
            // The {{card}} in the existing note is drawn inside a CardBoundary, which needs one.
            services.AddSingleton<IErrorBoundaryLogger, QuietBoundaryLogger>();
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
            services.AddSingleton<ScheduledActionStore>();
            services.AddSingleton<IScheduledActionPlans>(_plans);
            services.AddSingleton<ScheduledActions>();
            services.AddSingleton<RunbookCommandRunner>();
        });
        _services.GetRequiredService<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();
        _services.GetRequiredService<AlertService>().Zone = TimeZoneInfo.Utc;
        _renderer = new InteractiveRenderer(_services);
    }

    public async ValueTask DisposeAsync()
    {
        await _renderer.DisposeAsync();
        TestHost.Teardown(_services, _directory);
    }

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private static string Text(string html) => WebUtility.HtmlDecode(html);

    private async Task<Connection> ConnectionAsync(string name, string provider = "shell", params (string Key, string Value)[] settings)
    {
        var bag = new SettingsBag();
        foreach (var (key, value) in settings)
            bag[key] = value;
        var connection = new Connection { Provider = provider, Name = name, Settings = bag };
        await Get<ConfigStore>().SaveConnectionAsync(connection);
        return connection;
    }

    private Task<IReadOnlyList<Change>> CommandsFedAsync() =>
        Get<ChangeStore>().QueryAsync(new ChangeQuery(DateTimeOffset.UnixEpoch, new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero), [ChangeKinds.Command]));

    /// <summary>Renders the note signed in, as the app's own pages are.</summary>
    private Task RenderSignedInAsync(string markdown) =>
        _renderer.RenderAsync<CascadingAuth>(new Dictionary<string, object?> { [nameof(CascadingAuth.Markdown)] = markdown });

    /// <summary>The authentication state the app cascades from its router, around a note.</summary>
    public sealed class CascadingAuth : ComponentBase
    {
        [Parameter] public string Markdown { get; set; } = "";

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<Task<AuthenticationState>>>(0);
            builder.AddComponentParameter(1, "Value", new SignedIn().GetAuthenticationStateAsync());
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<LiveMarkdown>(0);
                inner.AddComponentParameter(1, nameof(LiveMarkdown.Content), Markdown);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    /// <summary>Clicks the first button whose text contains <paramref name="label"/>.</summary>
    private Task ClickAsync(string label) => _renderer.ClickAsync(label);

    // ---------- the runner's guardrails ----------

    [Fact]
    public async Task ACommandRunsOnlyWhereTheConnectionAllowsIt()
    {
        var closed = await ConnectionAsync("NAS");
        var open = await ConnectionAsync("Pi", settings: (RunbookCommands.AllowKey, "true"));
        var runner = Get<RunbookCommandRunner>();

        var refused = await runner.RunAsync(closed, "uptime", TimeSpan.FromSeconds(30), "chris");
        Assert.False(refused.Ran);
        Assert.Contains("does not allow runbook commands", refused.Message);
        Assert.Equal(0, _shell.Count);

        var ran = await runner.RunAsync(open, "uptime", TimeSpan.FromSeconds(30), "chris");
        Assert.True(ran.Ok);
        Assert.Equal(["done"], ran.Lines);
        Assert.Equal(("uptime", TimeSpan.FromSeconds(30)), Assert.Single(_shell.Ran));

        var change = Assert.Single(await CommandsFedAsync());
        Assert.Equal(ChangeActions.Completed, change.Action);
        Assert.Equal(open.Id, change.ConnectionId);
        Assert.Equal("chris ran “uptime” on Pi", change.Title);
        Assert.StartsWith("exit code 0 · ", change.Detail);
    }

    [Fact]
    public async Task AConnectionThatCannotRunCommandsSaysSo()
    {
        var docker = await ConnectionAsync("Docker", "docker", (RunbookCommands.AllowKey, "true"));

        Assert.Contains("cannot run commands", Get<RunbookCommandRunner>().Refusal(docker));
    }

    [Fact]
    public async Task OneCommandRunsOnceAtATimeAndAFailureIsRecorded()
    {
        var pi = await ConnectionAsync("Pi", settings: (RunbookCommands.AllowKey, "true"));
        var runner = Get<RunbookCommandRunner>();
        _shell.Hold = new TaskCompletionSource();
        _shell.Result = new CommandResult(3, "Error: PASSWORD=hunter2\n");

        var first = Task.Run(() => runner.RunAsync(pi, "systemctl restart smbd", TimeSpan.FromSeconds(30), "chris"));
        while (!runner.IsRunning(pi, "systemctl restart smbd"))
            await Task.Delay(10);

        var second = await runner.RunAsync(pi, "systemctl restart smbd", TimeSpan.FromSeconds(30), "sam");
        Assert.False(second.Ran);
        Assert.Contains("already running", second.Message);

        _shell.Hold.SetResult();
        var done = await first;
        Assert.False(done.Ok);
        Assert.Equal(3, done.ExitCode);
        Assert.Equal("Finished with exit code 3.", done.Message);
        Assert.DoesNotContain("hunter2", Assert.Single(done.Lines));
        Assert.Equal(1, _shell.Count);

        var change = Assert.Single(await CommandsFedAsync());
        Assert.Equal(ChangeActions.Failed, change.Action);
        Assert.StartsWith("exit code 3", change.Detail);
    }

    [Fact]
    public async Task WithoutALoginNothingRuns()
    {
        await using var bare = new RunbookActionTests(login: false);
        var pi = await bare.ConnectionAsync("Pi", settings: (RunbookCommands.AllowKey, "true"));

        var outcome = await bare.Get<RunbookCommandRunner>().RunAsync(pi, "uptime", TimeSpan.FromSeconds(30), "chris");

        Assert.False(outcome.Ran);
        Assert.Contains("need LabbyTwo to have a login", outcome.Message);
        Assert.Equal(0, bare._shell.Count);
    }

    // ---------- {{ssh}} drawn ----------

    [Fact]
    public async Task TheButtonShowsTheCommandAsksAndShowsTheOutputAsText()
    {
        await ConnectionAsync("NAS", settings: [(RunbookCommands.AllowKey, "true"), ("host", "192.168.1.50")]);
        _shell.Result = new CommandResult(0, "<b>plex</b>\nAPI_KEY=abcdef\n");

        await RenderSignedInAsync("Restart it: {{ssh: \"NAS\" / docker restart plex}}");
        var html = Text(await _renderer.WaitForAsync("docker restart plex"));
        Assert.Contains("<p>Restart it: <span class=\"md-ssh\"><button type=\"button\" class=\"btn btn-sm btn-outline-secondary\"", html);
        Assert.Contains("▶ <span class=\"md-ssh-command\">docker restart plex</span>", html);

        await ClickAsync("docker restart plex");
        html = Text(await _renderer.WaitForAsync("Run this on NAS?"));
        Assert.Contains("(192.168.1.50)", html);
        Assert.Equal(0, _shell.Count);

        await ClickAsync("Run it");
        html = await _renderer.WaitForAsync(h => h.Contains("md-ssh-output", StringComparison.Ordinal));
        // Text, not markup; masked.
        Assert.Contains("&lt;b&gt;plex&lt;/b&gt;", html);
        Assert.DoesNotContain("<b>plex", html);
        Assert.DoesNotContain("abcdef", html);
        Assert.Contains("Finished with exit code 0.", Text(html));
        Assert.Equal("chris ran “docker restart plex” on NAS", Assert.Single(await CommandsFedAsync()).Title);
    }

    [Fact]
    public async Task ADangerousCommandWaitsForTheNameToBeTyped()
    {
        await ConnectionAsync("NAS", settings: (RunbookCommands.AllowKey, "true"));

        await RenderSignedInAsync("{{ssh: NAS label=\"Wipe the cache\" / rm -rf /srv/cache}}");
        await _renderer.WaitForAsync("Wipe the cache");
        await ClickAsync("Wipe the cache");
        var html = Text(await _renderer.WaitForAsync("deletes files recursively"));

        Assert.Contains("Type <strong>NAS</strong> to run it.", html);
        Assert.Matches("<button class=\"btn btn-sm btn-danger\" disabled>Run it</button>", html);
        Assert.Equal(0, _shell.Count);
    }

    [Fact]
    public async Task AButtonThatCannotRunIsDrawnDisabledWithTheReason()
    {
        await ConnectionAsync("NAS");

        await RenderSignedInAsync("{{ssh: NAS / uptime}}");
        var html = Text(await _renderer.WaitForAsync("does not allow runbook commands"));

        Assert.Matches("<button type=\"button\" class=\"btn btn-sm btn-outline-secondary\" disabled", html);
        Assert.Contains("Tick “Allow runbook commands” on the connection", html);

        await _renderer.RenderMarkdownAsync("{{ssh: Nowhere / uptime}}");
        Assert.Contains("No connection called “Nowhere”", Text(await _renderer.WaitForAsync("sc-problem")));
    }

    // ---------- {{logs}} ----------

    private static byte[] Frame(int stream, string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var frame = new byte[8 + payload.Length];
        frame[0] = (byte)stream;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(4), payload.Length);
        payload.CopyTo(frame, 8);
        return frame;
    }

    private static string Stamp(DateTimeOffset at, string text) => $"{at.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffff00Z} {text}\n";

    private const string Containers = """
        [{"Id":"plex111","Names":["/media-plex-1"],"Image":"plex","State":"running","Status":"Up 3 hours","Labels":{"com.docker.compose.service":"plex","com.docker.compose.project":"media"}},
         {"Id":"sonarr222","Names":["/sonarr"],"Image":"sonarr","State":"running","Status":"Up 3 hours","Labels":{}}]
        """;

    private static ScriptedDocker Docker(Func<string, byte[]> logs) => new(async context =>
    {
        var path = context.Request.Url!.AbsolutePath;
        if (path.EndsWith("/containers/json", StringComparison.Ordinal))
        {
            await ScriptedDocker.Json(context, Containers);
            return;
        }
        var body = logs(path.Split('/')[^2]);
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body);
    });

    [Fact]
    public async Task TheNewestMatchingLinesAreReadMaskedAndCapped()
    {
        var now = DateTimeOffset.UtcNow;
        var log = new List<byte>();
        for (var i = 0; i < 30; i++)
            log.AddRange(Frame(1, Stamp(now.AddMinutes(-10).AddSeconds(i), i % 2 == 0 ? $"ERROR number {i} token=abc{i}" : $"fine {i}")));
        using var docker = Docker(_ => [.. log]);

        var request = new NoteLogs.Request("plex", "", TimeSpan.FromMinutes(15), true, "", 4);
        var lines = await NoteLogs.ReadAsync(docker.Endpoint, TimeSpan.FromSeconds(5), "plex111", request, now, CancellationToken.None);

        Assert.Equal(["ERROR number 22", "ERROR number 24", "ERROR number 26", "ERROR number 28"], lines.Select(l => l.Text[..l.Text.IndexOf(" token", StringComparison.Ordinal)]));
        Assert.All(lines, l => Assert.DoesNotContain("abc", l.Text));
        // Docker was asked for the window and a tail, never the whole log.
        var asked = Assert.Single(docker.Requests);
        Assert.Contains("/containers/plex111/logs?", asked);
        Assert.Contains("&since=", asked);
        Assert.Contains($"&tail={NoteLogs.ReadLines}", asked);
    }

    [Fact]
    public async Task ALogsBoxFindsAComposeServiceAndLinksToTheSearch()
    {
        var now = DateTimeOffset.UtcNow;
        using var docker = Docker(id => id == "plex111"
            ? [.. Frame(2, Stamp(now.AddMinutes(-2), "<script>alert(1)</script> failed to open database"))]
            : []);
        var connection = await ConnectionAsync("Docker", "docker", ("endpoint", docker.Endpoint));

        await _renderer.RenderMarkdownAsync("## Plex\n\n{{logs: plex last=15m errors}}\n\n{{logs: sonarr errors}}");
        var html = await _renderer.WaitForAsync(h => Text(h).Contains("No errors in the last 15 minutes", StringComparison.Ordinal)
                                                     && h.Contains("md-logs-line", StringComparison.Ordinal));

        Assert.Contains("<strong>media-plex-1</strong>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; failed to open database", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("md-logs-line is-stderr", html);
        Assert.Contains($"href=\"logs?connection={connection.Id}&amp;container=media-plex-1&amp;last=15m&amp;quick=error,exception,fatal,failed&amp;run=1\"", html);
        // A list and two logs: one list request, shared; one read each.
        Assert.Single(docker.Requests, r => r.Contains("/containers/json", StringComparison.Ordinal));
        Assert.Equal(2, docker.Requests.Count(r => r.Contains("/logs?", StringComparison.Ordinal)));

        // Nothing more is read because time passes or the lab is swept.
        await Get<HealthMonitor>().RefreshAsync(connection);
        await Task.Delay(200);
        Assert.Equal(2, docker.Requests.Count(r => r.Contains("/logs?", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ALogsBoxNamingNothingSaysSo()
    {
        using var docker = Docker(_ => []);
        await ConnectionAsync("Docker", "docker", ("endpoint", docker.Endpoint));

        await _renderer.RenderMarkdownAsync("{{logs: radarr}}\n\nIn a sentence {{logs: plex}}.");
        var html = Text(await _renderer.WaitForAsync("No container or Compose service called “radarr”"));

        Assert.Contains("goes on a line of its own", html);
    }

    // ---------- {{run}} ----------

    [Fact]
    public async Task ARunLineComesFromTheScheduleAndRunsNowWhenAsked()
    {
        var docker = await ConnectionAsync("Docker", "docker");
        var action = new ScheduledAction
        {
            Name = "Weekly Plex restart",
            TargetConnectionId = docker.Id,
            Container = "plex",
            Schedule = new ActionSchedule { Kind = ScheduleKind.Weekly, Days = [DayOfWeek.Sunday], Times = [new TimeOnly(4, 0)] },
        };
        await Get<ScheduledActions>().SaveAsync(action, DateTimeOffset.UtcNow);

        await RenderSignedInAsync("Every Sunday: {{run: \"weekly plex restart\"}}\n\n{{run: Nothing}}");
        var html = Text(await _renderer.WaitForAsync("never run yet"));
        Assert.Contains("<strong>Weekly Plex restart</strong>", html);
        Assert.Contains(", Sun ", html);
        Assert.Contains("No scheduled action called “Nothing”", html);

        await ClickAsync("Run now");
        await _renderer.WaitForAsync("Run “Weekly Plex restart” now?");
        Assert.Equal(0, _plans.Ran);

        // The dialog's own Run now, not the line's.
        await _renderer.ClickAsync("Run now", last: true);
        html = Text(await _renderer.WaitForAsync("last ok"));
        Assert.Equal(1, _plans.Ran);
        Assert.Contains("Restarted plex.", html);
    }

    [Fact]
    public async Task TheLastRunIsReadOnceThenKeptInMemory()
    {
        var docker = await ConnectionAsync("Docker", "docker");
        var action = new ScheduledAction
        {
            Name = "Nightly",
            TargetConnectionId = docker.Id,
            Container = "plex",
            Schedule = new ActionSchedule { Kind = ScheduleKind.Weekly, Days = MuteWindow.Week, Times = [new TimeOnly(4, 0)] },
        };
        await Get<ScheduledActions>().SaveAsync(action, DateTimeOffset.UtcNow);
        var earlier = await Get<ScheduledActionStore>().RecordRunAsync(
            new ScheduledRun(action.Id, DateTimeOffset.UtcNow.AddHours(-3), ScheduledTriggers.Schedule, ScheduledOutcomes.Failed, "nope", TimeSpan.Zero));

        var status = await Get<ScheduledActions>().StatusAsync("nightly", DateTimeOffset.UtcNow);
        Assert.Equal(earlier.Id, status!.Last!.Id);
        Assert.NotNull(status.Next);

        // Written behind the scheduler's back: not seen, because nothing reads the store again.
        await Get<ScheduledActionStore>().RecordRunAsync(
            new ScheduledRun(action.Id, DateTimeOffset.UtcNow, ScheduledTriggers.Schedule, ScheduledOutcomes.Ok, "", TimeSpan.Zero));
        Assert.Equal(earlier.Id, (await Get<ScheduledActions>().StatusAsync(action.Id, DateTimeOffset.UtcNow))!.Last!.Id);

        // A run through the scheduler is remembered as it happens.
        var run = await Get<ScheduledActions>().RunNowAsync(action.Id);
        Assert.Equal(ScheduledOutcomes.Ok, (await Get<ScheduledActions>().StatusAsync(action.Id, DateTimeOffset.UtcNow))!.Last!.Outcome);
        Assert.True(run.Ok);
    }

    // ---------- the note that has to keep working ----------

    /// <summary>
    /// A domain controller runbook using every shortcode written before these, with code in
    /// numbered lists and a table — drawn with copy buttons, and otherwise as it always was.
    /// </summary>
    private const string ExistingNote = """
        # DC1 runbook

        {{today}} · checked {{ago: DC1}} · cert renewal {{countdown: 2026-12-25}}

        {{down}}

        {{if all up}}
        Everything is up.
        {{else}}
        Something is down — start below.
        {{end}}

        {{alerts}}

        {{containers: stopped}}

        DC1 is {{status: DC1}}, up for {{since: DC1}}, {{uptime: DC1}} this month {{uptimebar: DC1}}.

        {{if down: DC1}}
        > [!CAUTION]
        > DC1 is down. Nobody can log in.
        {{else}}
        > [!TIP]
        > DC1 is answering.
        {{end}}

        Disk {{metric: DC1 / disk_percent}} {{sparkline: DC1 / disk_percent 24h}}, full {{forecast: DC1}}.

        {{card: gauge connection="DC1" metric="disk_percent" title="DC1 disk"}}

        {{if metric: DC1 / disk_percent > 85}}
        Clear the logs before it fills.
        {{end}}

        {{details: Check the services}}
        1. Look at them: `Get-Service NTDS,Netlogon,Kdc,DNS`
        2. Replication: `repadmin /replsummary`
        3. Still wrong? {{button: DC1 / restart}}
        4. Its page: {{link: DC1}}
        {{end}}

        | Check | Command |
        |---|---|
        | DNS | `Resolve-DnsName dc1.lab` |
        | Time | `w32tm /query /status` |

        {{renewals: days=60}}

        ```powershell
        dcdiag /v
        ```
        """;

    [Fact]
    public async Task TheExistingNoteStillDrawsEverythingWithCopyButtons()
    {
        var dc = await ConnectionAsync("DC1", settings: ("host", "dc1.lab"));
        await Get<HealthMonitor>().RefreshAsync(dc);
        using var docker = Docker(_ => []);
        var host = await ConnectionAsync("Docker", "docker", ("endpoint", docker.Endpoint));
        await Get<HealthMonitor>().RefreshAsync(host);

        await _renderer.RenderMarkdownAsync(ExistingNote);
        // The sparkline and the forecast want history this lab has not got, so they may stay
        // waiting; everything read from memory or from Docker has to have arrived.
        var html = Text(await _renderer.WaitForAsync(h => Text(h) is var t
                                                          && t.Contains("DC1 is answering", StringComparison.Ordinal)
                                                          && t.Contains("Everything is up", StringComparison.Ordinal)
                                                          && t.Contains("No stopped containers", StringComparison.Ordinal)
                                                          && t.Contains("Nothing due in the next 60 days", StringComparison.Ordinal)
                                                          && t.Contains("100.0%", StringComparison.Ordinal)
                                                          && t.Contains("gauge-value", StringComparison.Ordinal)));
        var flat = html.ReplaceLineEndings(" ");
        Assert.Contains("No alerts firing", html);
        Assert.Contains("Everything’s up", html);

        // Nothing unknown, nothing out of place, no shortcode left as text.
        Assert.DoesNotContain("is not a live value", html);
        Assert.DoesNotContain("goes on a line of its own", html);
        Assert.DoesNotContain("{{", html);
        Assert.DoesNotContain("Something is down", html);
        Assert.DoesNotContain("Nobody can log in", html);
        Assert.DoesNotContain("Clear the logs", html);

        // The live parts are there.
        Assert.Contains("status-dot status-up", html);
        Assert.Contains("md-callout md-callout-tip", html);
        Assert.Contains("<details class=\"md-details\"><summary class=\"md-details-summary\">Check the services</summary>", html);
        Assert.Contains("Restart", html);
        Assert.Contains("href=\"http://dc1.lab\"", html);
        Assert.Contains("DC1 disk", html);

        // The numbered list is one list of four, code and all, and its code has buttons.
        Assert.Matches("<ol>\\s*<li>Look at them: <span class=\"md-code-inline\"><code>Get-Service NTDS,Netlogon,Kdc,DNS</code><button type=\"button\" class=\"md-copy\"", flat);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(flat, "<ol>"));
        Assert.Matches("<li>Still wrong\\? <span class=\"md-action\">", flat);
        // The table is still a table, with its code and buttons in its cells.
        Assert.Contains("<td><span class=\"md-code-inline\"><code>w32tm /query /status</code><button", flat);
        // The block has its own.
        Assert.Matches("<div class=\"md-code\">\\s*<pre><code class=\"language-powershell\">dcdiag /v", flat);
    }
}
