using System.Collections.Concurrent;
using System.Net;
using System.Text;
using LabbyTwo.Core;
using LabbyTwo.Providers;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LabbyTwo.Tests;

/// <summary>
/// The dead man's switch, against a fake Healthchecks / Uptime Kuma on a loopback port. What
/// is pinned down is what would make it worse than nothing: a ping sent while monitoring is
/// hung (a switch that never trips), a ping sent before anybody asked for one, the token
/// sitting in the database or on the page in the clear, and the shapes each service needs.
/// </summary>
public sealed class OutsideAlarmTests : IDisposable
{
    private const string Token = "5f0e7c1a-9b2d-4c3e-8f7a-1234567890ab";

    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private readonly FakeHeartbeat _server = new();

    private sealed class QuietProvider : IConnectionProvider
    {
        public string Type => "alarmtest";
        public string DisplayName => "Quiet";
        public string Icon => "🔕";
        public string Description => "Test double.";
        public IReadOnlyList<FieldSpec> Fields => [];

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
            Task.FromResult(ProbeResult.Up(TimeSpan.FromMilliseconds(3)));
    }

    public OutsideAlarmTests()
    {
        Directory.CreateDirectory(_directory);

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient(ProviderHttp.ClientName);
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        services.AddTestStorage(_directory, options => options.ProbeSeconds = 5);
        services.AddSingleton<IConnectionProvider, QuietProvider>();
        services.AddSingleton<IEnumerable<IWidgetType>>([]);
        services.AddSingleton<IEnumerable<ITabKind>>([]);
        services.AddSingleton<Registry>();
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<HistoryStore>();
        services.AddSingleton<HealthMonitor>();
        services.AddSingleton<OutsideAlarm>();
        _services = services.BuildServiceProvider();

        Get<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _server.Dispose();
        TestHost.Teardown(_services, _directory);
    }

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private OutsideAlarm Alarm => Get<OutsideAlarm>();

    private static readonly DateTimeOffset T0 = new(2026, 9, 29, 3, 0, 0, TimeSpan.Zero);

    /// <summary>A monitor that has been sweeping every 30 seconds for ten minutes, the last ten seconds ago.</summary>
    private static MonitorStatus Healthy(DateTimeOffset now) => new(
        now - TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(30), RestoreOutcome.Completed,
        now - TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(1), null,
        20, null, now - TimeSpan.FromSeconds(12), now - TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2), 3, null,
        [], []);

    /// <summary>A sweep that started five minutes ago and has not come back — every tile frozen.</summary>
    private static MonitorStatus Stuck(DateTimeOffset now) => Healthy(now) with
    {
        CurrentSweepStarted = now - TimeSpan.FromMinutes(5),
        LastSweepFinished = now - TimeSpan.FromMinutes(5, 30),
        InFlight = [new ProbeInFlight("nas", now - TimeSpan.FromMinutes(5))],
    };

    private static IReadOnlyCollection<HealthMonitor.ProbeState> States(int up, int down) =>
    [
        .. Enumerable.Range(0, up).Select(i => State($"up{i}", true)),
        .. Enumerable.Range(0, down).Select(i => State($"down{i}", false)),
    ];

    private static HealthMonitor.ProbeState State(string id, bool? isUp) => new(
        id, isUp, "private message with 192.168.1.10 in it", TimeSpan.FromMilliseconds(5), T0, null, 0,
        new Dictionary<string, double>(), new Dictionary<string, string>());

    private Task ConfigureAsync(string url, HeartbeatKind kind = HeartbeatKind.Auto, int interval = 60, bool summary = true) =>
        Alarm.SaveAsync(new OutsideAlarmSettings(url, kind, interval, summary));

    // ---- Nothing until asked ----------------------------------------------------------------

    [Fact]
    public async Task Nothing_is_sent_until_an_address_is_configured()
    {
        for (var minute = 0; minute < 5; minute++)
        {
            var now = T0 + TimeSpan.FromMinutes(minute);
            Assert.Equal(HeartbeatSignal.None, await Alarm.TickAsync(now, Healthy(now), States(3, 0), default));
        }

        Assert.Empty(_server.Requests);
        Assert.Null(Alarm.Status.LastAttemptAt);
        Assert.False((await Alarm.TestAsync(null, default)).Ok);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task Turning_it_off_stops_the_pings()
    {
        await ConfigureAsync(_server.Url($"/ping/{Token}"));
        await Alarm.TickAsync(T0, Healthy(T0), States(1, 0), default);
        Assert.Single(_server.Requests);

        await ConfigureAsync("");
        var later = T0 + TimeSpan.FromMinutes(5);
        Assert.Equal(HeartbeatSignal.None, await Alarm.TickAsync(later, Healthy(later), States(1, 0), default));
        Assert.Single(_server.Requests);
    }

    // ---- On schedule ----------------------------------------------------------------------------

    [Fact]
    public async Task Pings_go_out_on_the_chosen_interval_and_not_between()
    {
        await ConfigureAsync(_server.Url($"/ping/{Token}"), interval: 300);

        var sent = new List<int>();
        for (var minute = 0; minute <= 15; minute++)
        {
            // Ticks land a couple of seconds late, as the runner's do.
            var now = T0 + TimeSpan.FromMinutes(minute) + TimeSpan.FromSeconds(minute % 3);
            if (await Alarm.TickAsync(now, Healthy(now), States(2, 0), default) == HeartbeatSignal.Up)
                sent.Add(minute);
        }

        Assert.Equal([0, 5, 10, 15], sent);
        Assert.Equal(4, _server.Requests.Count);
    }

    [Fact]
    public async Task Every_minute_by_default()
    {
        await ConfigureAsync(_server.Url($"/ping/{Token}"));

        for (var minute = 0; minute < 3; minute++)
        {
            var now = T0 + TimeSpan.FromMinutes(minute);
            await Alarm.TickAsync(now, Healthy(now), States(1, 0), default);
            // A second tick inside the same minute sends nothing.
            await Alarm.TickAsync(now + TimeSpan.FromSeconds(20), Healthy(now), States(1, 0), default);
        }

        Assert.Equal(3, _server.Requests.Count);
        Assert.True(Alarm.Status.LastOk);
        Assert.Equal(T0 + TimeSpan.FromMinutes(2), Alarm.Status.LastSuccessAt);
    }

    // ---- Only when monitoring is really running ----------------------------------------------

    [Fact]
    public async Task A_stuck_monitor_reports_fail_to_Healthchecks_with_the_reason()
    {
        await ConfigureAsync(_server.Url($"/ping/{Token}"));

        Assert.Equal(HeartbeatSignal.Fail, await Alarm.TickAsync(T0, Stuck(T0), States(3, 0), default));

        var request = Assert.Single(_server.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal($"/ping/{Token}/fail", request.Path);
        Assert.Contains("stuck", request.Body);
        Assert.Equal(HeartbeatSignal.Fail, Alarm.Status.LastSignal);
    }

    [Fact]
    public async Task A_stuck_monitor_sends_nothing_to_a_plain_heartbeat()
    {
        // No way to say "bad" to it, so silence is how: its own timeout goes off.
        await ConfigureAsync(_server.Url($"/api/v1/heartbeat/{Token}"), HeartbeatKind.Plain);

        Assert.Equal(HeartbeatSignal.None, await Alarm.TickAsync(T0, Stuck(T0), States(3, 0), default));
        Assert.Empty(_server.Requests);
        Assert.Contains("stuck", Alarm.Status.Verdict);

        // And it picks up again as soon as monitoring does.
        var later = T0 + TimeSpan.FromMinutes(1);
        Assert.Equal(HeartbeatSignal.Up, await Alarm.TickAsync(later, Healthy(later), States(3, 0), default));
        Assert.Equal("GET", Assert.Single(_server.Requests).Method);
    }

    [Fact]
    public async Task A_stopped_monitor_reports_down_to_Uptime_Kuma()
    {
        await ConfigureAsync(_server.Url($"/api/push/AbCdEf1234?status=up&msg=OK&ping="));
        var stopped = Healthy(T0) with { LastSweepFinished = T0 - TimeSpan.FromMinutes(10) };

        Assert.Equal(HeartbeatSignal.Fail, await Alarm.TickAsync(T0, stopped, States(1, 0), default));

        var request = Assert.Single(_server.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("/api/push/AbCdEf1234", request.Path);
        Assert.Equal("down", request.Query["status"]);
        Assert.Contains("stopped", request.Query["msg"]);
        // Kuma's own parameters replaced rather than repeated.
        Assert.Single(request.Query.GetValues("status")!);
    }

    [Fact]
    public async Task Nothing_is_sent_while_the_monitor_is_still_starting()
    {
        await ConfigureAsync(_server.Url($"/ping/{Token}"));
        var starting = Healthy(T0) with
        {
            StartedAt = T0 - TimeSpan.FromSeconds(5), LastSweepStarted = null, LastSweepFinished = null,
            LastSweepDuration = null, SweepsCompleted = 0,
        };

        Assert.Equal(HeartbeatSignal.None, await Alarm.TickAsync(T0, starting, States(0, 0), default));
        Assert.Empty(_server.Requests);
        Assert.Contains("Starting", Alarm.Status.Verdict);
    }

    [Theory]
    [InlineData(0, 0, HeartbeatSignal.Up)]
    [InlineData(60, 0, HeartbeatSignal.Up)] // A sweep running a minute of a 30 s period: slow, not stuck.
    [InlineData(61 + 60, 0, HeartbeatSignal.Fail)] // Running for more than two periods.
    [InlineData(0, 95, HeartbeatSignal.Fail)] // Nothing has started for over three periods.
    public void The_verdict_follows_the_health_pages_thresholds(int runningFor, int finishedAgo, HeartbeatSignal expected)
    {
        var status = Healthy(T0) with
        {
            CurrentSweepStarted = runningFor > 0 ? T0 - TimeSpan.FromSeconds(runningFor) : null,
            LastSweepFinished = T0 - TimeSpan.FromSeconds(Math.Max(finishedAgo, runningFor + 1)),
        };

        Assert.Equal(expected, OutsideAlarm.Judge(status, T0, TimeSpan.FromSeconds(30), T0 - TimeSpan.FromHours(1)).Signal);
    }

    [Fact]
    public void A_monitor_that_never_started_is_a_failure_once_start_up_is_long_over()
    {
        var never = Healthy(T0) with { StartedAt = null, LastSweepFinished = null, LastSweepStarted = null };

        Assert.Equal(HeartbeatSignal.None, OutsideAlarm.Judge(never, T0, TimeSpan.FromSeconds(30), T0 - TimeSpan.FromSeconds(20)).Signal);
        Assert.Equal(HeartbeatSignal.Fail, OutsideAlarm.Judge(never, T0, TimeSpan.FromSeconds(30), T0 - TimeSpan.FromMinutes(5)).Signal);
    }

    [Fact]
    public async Task The_real_monitor_vouches_for_the_ping_once_it_has_swept()
    {
        await Get<ConfigStore>().SaveConnectionAsync(new Connection { Provider = "alarmtest", Name = "NAS" });
        await ConfigureAsync(_server.Url($"/ping/{Token}"));

        // Before the monitor has run, the job has nothing to vouch for.
        await Alarm.RunAsync(default);
        Assert.Empty(_server.Requests);

        var monitor = Get<HealthMonitor>();
        await monitor.StartAsync(default);
        try
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
            while (monitor.Status.SweepsCompleted < 1)
            {
                Assert.True(DateTimeOffset.UtcNow < deadline, "Timed out waiting for the first sweep.");
                await Task.Delay(25);
            }

            await Alarm.RunAsync(default);
        }
        finally
        {
            await monitor.StopAsync(default);
        }

        var request = Assert.Single(_server.Requests);
        Assert.Equal($"/ping/{Token}", request.Path);
        Assert.Equal("1 up, 0 down", request.Body);
    }

    // ---- What goes over the wire ---------------------------------------------------------------

    [Fact]
    public async Task Healthchecks_gets_the_summary_as_the_body_and_nothing_private()
    {
        await ConfigureAsync(_server.Url($"/ping/{Token}"));

        await Alarm.TickAsync(T0, Healthy(T0), [.. States(12, 1), State("new", null)], default);

        var request = Assert.Single(_server.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal($"/ping/{Token}", request.Path);
        Assert.Equal("12 up, 1 down, 1 checking", request.Body);
        Assert.DoesNotContain("192.168", request.Body);
    }

    [Fact]
    public async Task Without_the_summary_Healthchecks_gets_a_plain_GET()
    {
        await ConfigureAsync(_server.Url($"/ping/{Token}"), summary: false);

        await Alarm.TickAsync(T0, Healthy(T0), States(2, 0), default);

        var request = Assert.Single(_server.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("", request.Body);
    }

    [Theory]
    [InlineData("https://hc-ping.com/abc", HeartbeatKind.Healthchecks)]
    [InlineData("https://hc-ping.com/pingkey123/backup-slug", HeartbeatKind.Healthchecks)]
    [InlineData("https://hc.example.com/ping/abc", HeartbeatKind.Healthchecks)]
    [InlineData("https://kuma.example.com/api/push/Xy12?status=up&msg=OK&ping=", HeartbeatKind.UptimeKuma)]
    [InlineData("https://uptime.betterstack.com/api/v1/heartbeat/abc", HeartbeatKind.Plain)]
    [InlineData("https://cronitor.link/p/key/job", HeartbeatKind.Plain)]
    [InlineData("not a url", HeartbeatKind.Plain)]
    public void The_service_is_worked_out_from_the_address(string url, HeartbeatKind expected) =>
        Assert.Equal(expected, OutsideAlarm.Detect(url));

    [Theory]
    [InlineData("https://hc-ping.com/abc/fail")]
    [InlineData("https://hc-ping.com/abc/start")]
    [InlineData("https://hc-ping.com/abc/1")]
    [InlineData("https://hc-ping.com/abc/")]
    public void A_pasted_Healthchecks_signal_is_replaced_not_stacked(string pasted)
    {
        var settings = new OutsideAlarmSettings(pasted, HeartbeatKind.Auto, 60, false);

        using var up = OutsideAlarm.BuildRequest(settings, HeartbeatSignal.Up, "");
        using var fail = OutsideAlarm.BuildRequest(settings, HeartbeatSignal.Fail, "why");

        Assert.Equal("https://hc-ping.com/abc", up.RequestUri!.ToString());
        Assert.Equal("https://hc-ping.com/abc/fail", fail.RequestUri!.ToString());
    }

    [Fact]
    public async Task A_refusal_is_recorded_without_throwing_and_kept_after_recovery()
    {
        await ConfigureAsync(_server.Url($"/ping/{Token}"));
        _server.Status = HttpStatusCode.NotFound;

        // The job never throws for a failed ping: the runner would log an error and flag the
        // job every minute of an internet outage.
        await Alarm.TickAsync(T0, Healthy(T0), States(1, 0), default);
        await Alarm.TickAsync(T0.AddMinutes(1), Healthy(T0.AddMinutes(1)), States(1, 0), default);

        Assert.False(Alarm.Status.LastOk);
        Assert.Equal(2, Alarm.Status.ConsecutiveFailures);
        Assert.Contains("404", Alarm.Status.LastError);
        Assert.DoesNotContain(Token, Alarm.Status.LastError);

        _server.Status = HttpStatusCode.OK;
        await Alarm.TickAsync(T0.AddMinutes(2), Healthy(T0.AddMinutes(2)), States(1, 0), default);

        Assert.True(Alarm.Status.LastOk);
        Assert.Equal(0, Alarm.Status.ConsecutiveFailures);
        Assert.NotNull(Alarm.Status.LastError);
    }

    [Fact]
    public async Task An_unreachable_service_is_a_recorded_failure_not_an_exception()
    {
        // A port nothing is listening on — the nearest a test gets to "the internet is down".
        var dead = LoopbackListener.Start(out var port);
        dead.Close();
        await ConfigureAsync($"http://127.0.0.1:{port}/ping/{Token}");

        Assert.Equal(HeartbeatSignal.Up, await Alarm.TickAsync(T0, Healthy(T0), States(1, 0), default));
        Assert.False(Alarm.Status.LastOk);
        Assert.DoesNotContain(Token, Alarm.Status.LastMessage);
    }

    [Fact]
    public void Uptime_Kuma_saying_no_is_believed_over_its_status_code()
    {
        var (ok, message) = OutsideAlarm.Interpret(HeartbeatKind.UptimeKuma, HttpStatusCode.OK,
            """{"ok":false,"msg":"Monitor not found or not active."}""");

        Assert.False(ok);
        Assert.Contains("not active", message);
        Assert.True(OutsideAlarm.Interpret(HeartbeatKind.UptimeKuma, HttpStatusCode.OK, """{"ok":true}""").Ok);
    }

    [Fact]
    public async Task The_test_button_sends_now_whatever_the_schedule()
    {
        await ConfigureAsync(_server.Url($"/ping/{Token}"));
        await Alarm.TickAsync(DateTimeOffset.Now, Healthy(DateTimeOffset.Now), States(1, 0), default);

        // The real monitor has not started, so it is "starting" — a test is still an up.
        var (ok, message) = await Alarm.TestAsync(null, default);

        Assert.True(ok, message);
        Assert.Equal(2, _server.Requests.Count);
        Assert.Contains("Test ping", _server.Requests.Last().Body);
        Assert.DoesNotContain(Token, message);
    }

    [Fact]
    public async Task The_test_button_can_try_an_address_before_it_is_saved()
    {
        var unsaved = new OutsideAlarmSettings(_server.Url($"/ping/{Token}"), HeartbeatKind.Auto, 60, false);

        var (ok, _) = await Alarm.TestAsync(unsaved, default);

        Assert.True(ok);
        Assert.Single(_server.Requests);
        Assert.False((await Alarm.SettingsAsync()).IsConfigured);
    }

    // ---- The token is a secret --------------------------------------------------------------------

    [Fact]
    public async Task The_address_is_stored_encrypted_and_read_back_whole()
    {
        var url = $"https://hc-ping.com/{Token}";
        await ConfigureAsync(url, HeartbeatKind.Healthchecks, 300, false);

        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "test.db")};Pooling=False");
        await connection.OpenAsync();
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM app_settings WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", OutsideAlarm.UrlKey);
        var raw = (string)(await cmd.ExecuteScalarAsync())!;

        Assert.StartsWith("enc:", raw);
        Assert.DoesNotContain(Token, raw);
        Assert.DoesNotContain("hc-ping", raw);

        var read = await Alarm.SettingsAsync();
        Assert.Equal(url, read.Url);
        Assert.Equal(HeartbeatKind.Healthchecks, read.Kind);
        Assert.Equal(300, read.IntervalSeconds);
        Assert.False(read.IncludeSummary);
    }

    [Theory]
    [InlineData("https://hc-ping.com/5f0e7c1a-9b2d-4c3e-8f7a-1234567890ab", "https://hc-ping.com/••••90ab")]
    [InlineData("https://hc.example.com:8000/ping/5f0e7c1a-9b2d-4c3e-8f7a-1234567890ab/fail", "https://hc.example.com:8000/ping/••••90ab/fail")]
    [InlineData("https://kuma.example.com/api/push/AbCdEf1234?status=up&msg=OK&ping=", "https://kuma.example.com/api/push/••••?…")]
    [InlineData("https://uptime.betterstack.com/api/v1/heartbeat/Zq8Lr3Pm2Nx7Wc5Tk", "https://uptime.betterstack.com/api/v1/heartbeat/••••c5Tk")]
    public void The_token_is_masked_for_the_page_and_the_log(string url, string expected)
    {
        var masked = OutsideAlarm.Mask(url);

        Assert.Equal(expected, masked);
        Assert.DoesNotContain("AbCdEf1234", masked);
        Assert.DoesNotContain(Token, masked);
    }

    [Fact]
    public void Masking_hides_credentials_in_the_address_too() =>
        Assert.Equal("https://hc.example.com/ping/••••", OutsideAlarm.Mask("https://user:secret@hc.example.com/ping/abc"));

    // ---- A fake Healthchecks / Kuma ------------------------------------------------------------

    private sealed record Received(string Method, string Path, System.Collections.Specialized.NameValueCollection Query, string Body);

    private sealed class FakeHeartbeat : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly int _port;
        private readonly ConcurrentQueue<Received> _received = new();

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public IReadOnlyCollection<Received> Requests => [.. _received];

        public FakeHeartbeat()
        {
            _listener = LoopbackListener.Start(out _port);
            _ = Task.Run(ServeAsync);
        }

        public string Url(string pathAndQuery) => $"http://127.0.0.1:{_port}{pathAndQuery}";

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception) when (_stop.IsCancellationRequested || !_listener.IsListening)
                {
                    return;
                }

                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();

                // Recorded before answering, so a test that has its response has its request.
                _received.Enqueue(new Received(
                    context.Request.HttpMethod, context.Request.Url!.AbsolutePath, context.Request.QueryString, body));

                var bytes = Encoding.UTF8.GetBytes(Status == HttpStatusCode.OK ? "OK" : "not found");
                context.Response.StatusCode = (int)Status;
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
        }
    }
}
