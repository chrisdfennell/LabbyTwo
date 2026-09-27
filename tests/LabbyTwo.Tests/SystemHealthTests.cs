using System.Net;
using System.Net.Sockets;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabbyTwo.Tests;

/// <summary>
/// The health page exists because the questions it answers — has the monitor swept at all,
/// is one stuck, is a job failing, can the container resolve names — took SSH and a stack
/// dump to answer. These pin the bookkeeping those answers come from, so the page cannot
/// quietly go back to showing nothing.
/// </summary>
public sealed class SystemHealthTests : IDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private readonly GatedProvider _provider = new();

    /// <summary>A probe the test can hold open, to catch a sweep in the act.</summary>
    private sealed class GatedProvider : IConnectionProvider
    {
        public string Type => "healthtest";
        public string DisplayName => "Gated";
        public string Icon => "🚦";
        public string Description => "Test double.";
        public IReadOnlyList<FieldSpec> Fields => [];

        public TaskCompletionSource Gate { get; set; } = CompletedGate();

        public static TaskCompletionSource CompletedGate()
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gate.SetResult();
            return gate;
        }

        public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
        {
            await Gate.Task.WaitAsync(ct);
            return ProbeResult.Up(TimeSpan.FromMilliseconds(5));
        }
    }

    public SystemHealthTests()
    {
        Directory.CreateDirectory(_directory);

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        // The shortest sweep the monitor allows, so a test sees a second one in seconds.
        services.AddTestStorage(_directory, options => options.ProbeSeconds = 5);
        services.AddSingleton<IConnectionProvider>(_provider);
        services.AddSingleton<IEnumerable<IWidgetType>>([]);
        services.AddSingleton<IEnumerable<ITabKind>>([]);
        services.AddSingleton<Registry>();
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<HistoryStore>();
        services.AddSingleton<HealthMonitor>();
        _services = services.BuildServiceProvider();
    }

    public void Dispose() => TestHost.Teardown(_services, _directory);

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
                Assert.Fail($"Timed out waiting for {what}.");
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task A_sweep_records_when_it_started_and_finished_and_what_it_probed()
    {
        await Get<ConfigStore>().SaveConnectionAsync(new Connection { Provider = "healthtest", Name = "NAS" });
        var monitor = Get<HealthMonitor>();

        var before = monitor.Status;
        Assert.Null(before.StartedAt);
        Assert.Equal(RestoreOutcome.Pending, before.Restore);
        Assert.Null(before.LastSweepFinished);

        await monitor.StartAsync(CancellationToken.None);
        try
        {
            await Until(() => monitor.Status.SweepsCompleted >= 1, "the first sweep");
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
        }

        var status = monitor.Status;
        Assert.NotNull(status.StartedAt);
        Assert.Equal(RestoreOutcome.Completed, status.Restore);
        Assert.NotNull(status.RestoreDuration);
        Assert.NotNull(status.LastSweepStarted);
        Assert.NotNull(status.LastSweepFinished);
        Assert.True(status.LastSweepFinished >= status.LastSweepStarted);
        Assert.Equal(status.LastSweepFinished - status.LastSweepStarted, status.LastSweepDuration);
        Assert.Equal(1, status.LastSweepProbed);
        Assert.Null(status.CurrentSweepStarted);
        Assert.Empty(status.InFlight);
        Assert.Contains(status.Slowest, probe => probe.Duration == TimeSpan.FromMilliseconds(5));
    }

    [Fact]
    public async Task A_sweep_in_progress_shows_as_running_and_names_what_it_waits_on()
    {
        var connection = new Connection { Provider = "healthtest", Name = "Slow" };
        await Get<ConfigStore>().SaveConnectionAsync(connection);
        _provider.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = Get<HealthMonitor>();

        await monitor.StartAsync(CancellationToken.None);
        try
        {
            await Until(() => monitor.Status.CurrentSweepStarted is not null, "a sweep to start");

            var status = monitor.Status;
            Assert.Equal(0, status.SweepsCompleted);
            Assert.Contains(status.InFlight, probe => probe.ConnectionId == connection.Id);

            // The red flag is two periods, measured from the sweep's own start.
            var started = status.CurrentSweepStarted!.Value;
            Assert.False(status.IsSweepStuck(started + status.SweepPeriod));
            Assert.True(status.IsSweepStuck(started + status.SweepPeriod * 2 + TimeSpan.FromSeconds(1)));

            _provider.Gate.SetResult();
            await Until(() => monitor.Status.SweepsCompleted >= 1, "the held sweep to finish");
            Assert.Empty(monitor.Status.InFlight);
        }
        finally
        {
            _provider.Gate.TrySetResult();
            await monitor.StopAsync(CancellationToken.None);
        }
    }

    // ---- Background jobs ---------------------------------------------------------------

    private sealed class StartupJob(Func<Task> run) : IBackgroundJob
    {
        public string Name { get; init; } = "startup-job";
        public TimeSpan Interval => TimeSpan.FromHours(6);
        public bool RunAtStartup => true;
        public Task RunAsync(CancellationToken ct) => run();
    }

    [Fact]
    public async Task A_job_run_records_when_it_ran_and_when_it_is_next_due()
    {
        var runner = new BackgroundJobRunner(
            [new StartupJob(() => Task.Delay(10)) { Name = "tidy" }], NullLogger<BackgroundJobRunner>.Instance);

        await runner.StartAsync(CancellationToken.None);
        try
        {
            await Until(() => runner.Runs.Any(r => r.At is not null && r.NextDue is not null), "the job to run");
        }
        finally
        {
            await runner.StopAsync(CancellationToken.None);
        }

        var run = Assert.Single(runner.Runs);
        Assert.True(run.Ok);
        Assert.Null(run.LastError);
        Assert.Null(run.RunningSince);
        Assert.Equal(TimeSpan.FromHours(6), run.Interval);
        Assert.True(run.Duration > TimeSpan.Zero);
        Assert.True(run.NextDue > run.At);
    }

    [Fact]
    public async Task A_failing_job_keeps_its_error()
    {
        var runner = new BackgroundJobRunner(
            [new StartupJob(() => throw new InvalidOperationException("share is read-only")) { Name = "backup" }],
            NullLogger<BackgroundJobRunner>.Instance);

        await runner.StartAsync(CancellationToken.None);
        try
        {
            await Until(() => runner.Runs.Any(r => r.At is not null), "the job to fail");
        }
        finally
        {
            await runner.StopAsync(CancellationToken.None);
        }

        var run = Assert.Single(runner.Runs);
        Assert.False(run.Ok);
        Assert.Equal("share is read-only", run.LastError);
        Assert.NotNull(run.LastErrorAt);

        // And the page says so in words.
        var findings = SystemHealth.Assess(Live(Healthy(Now), jobs: [run]), null, TimeSpan.FromSeconds(30));
        Assert.Contains(findings, f => f.Area == "Jobs" && f.Text.Contains("share is read-only"));
    }

    // ---- The verdict -------------------------------------------------------------------

    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private static MonitorStatus Healthy(DateTimeOffset now) => new(
        StartedAt: now - TimeSpan.FromHours(1),
        SweepPeriod: TimeSpan.FromSeconds(30),
        Restore: RestoreOutcome.Completed,
        RestoreStarted: now - TimeSpan.FromHours(1),
        RestoreDuration: TimeSpan.FromMilliseconds(40),
        RestoreError: null,
        SweepsCompleted: 120,
        CurrentSweepStarted: null,
        LastSweepStarted: now - TimeSpan.FromSeconds(12),
        LastSweepFinished: now - TimeSpan.FromSeconds(10),
        LastSweepDuration: TimeSpan.FromSeconds(2),
        LastSweepProbed: 8,
        LastSweepError: null,
        InFlight: [],
        Slowest: []);

    private static SystemHealth.LiveStatus Live(MonitorStatus monitor, IReadOnlyList<JobRun>? jobs = null) => new(
        Now,
        monitor,
        jobs ?? [],
        new SystemHealth.PluginStatus("v1.0.0", "/app/data/plugins", true, 0, [], [], []),
        new SystemHealth.ProcessStatus("v1.0.0", ".NET", "Linux", true, Now - TimeSpan.FromHours(1),
            TimeSpan.FromHours(1), 50_000_000, 150_000_000, 30, 0, 8, 4));

    [Fact]
    public void A_monitor_sweeping_on_time_has_nothing_to_report()
    {
        Assert.Empty(SystemHealth.Assess(Live(Healthy(Now)), null, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void A_monitor_that_never_swept_is_the_red_flag()
    {
        // What actually happened: up for an hour, and not one sweep.
        var monitor = Healthy(Now) with
        {
            SweepsCompleted = 0, LastSweepStarted = null, LastSweepFinished = null, LastSweepDuration = null,
        };

        var finding = Assert.Single(SystemHealth.Assess(Live(monitor), null, TimeSpan.FromSeconds(30)));
        Assert.Equal(SystemHealth.Level.Bad, finding.Level);
        Assert.Contains("not run a single sweep", finding.Text);

        // A moment after startup is only "starting", not a fault.
        var starting = monitor with { StartedAt = Now - TimeSpan.FromSeconds(5) };
        Assert.Equal(SystemHealth.Level.Warn,
            Assert.Single(SystemHealth.Assess(Live(starting), null, TimeSpan.FromSeconds(30))).Level);
    }

    [Fact]
    public void A_sweep_running_past_two_periods_is_reported_stuck()
    {
        var monitor = Healthy(Now) with { CurrentSweepStarted = Now - TimeSpan.FromSeconds(75) };
        var finding = Assert.Single(SystemHealth.Assess(Live(monitor), null, TimeSpan.FromSeconds(30)));
        Assert.Equal(SystemHealth.Level.Bad, finding.Level);
        Assert.Contains("75 s", finding.Text);

        var busy = monitor with { CurrentSweepStarted = Now - TimeSpan.FromSeconds(45) };
        Assert.Empty(SystemHealth.Assess(Live(busy), null, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void A_public_name_that_does_not_resolve_says_the_container_has_no_dns()
    {
        IReadOnlyList<DnsCheck.Lookup> dns =
        [
            new(DnsCheck.PublicProbe, true, false, TimeSpan.FromSeconds(5), [], "No answer", ["Update check"]),
            new("nas", false, true, TimeSpan.FromMilliseconds(3), ["192.168.1.10"], null, ["NAS"]),
        ];

        var finding = Assert.Single(SystemHealth.Assess(Live(Healthy(Now)), dns, TimeSpan.FromSeconds(30)));
        Assert.Equal(SystemHealth.Level.Bad, finding.Level);
        Assert.Contains("no working DNS", finding.Text);
    }

    // ---- DNS ---------------------------------------------------------------------------

    [Fact]
    public void The_dns_check_asks_github_first_and_each_named_host_once()
    {
        Connection With(string name, string key, string value, bool enabled = true) => new()
        {
            Name = name, Enabled = enabled, Settings = new SettingsBag { [key] = value },
        };

        var targets = DnsCheck.TargetsFor(
        [
            With("NAS", "url", "http://nas.lan:8080"),
            With("NAS SSH", "host", "nas.lan"),
            With("Router", "url", "http://192.168.1.1"),
            With("Plex", "url", "https://plex.example.com:32400"),
            With("Old", "url", "http://retired.lan", enabled: false),
        ]);

        Assert.Equal(DnsCheck.PublicProbe, targets[0].Host);
        Assert.True(targets[0].IsPublic);

        var nas = Assert.Single(targets, t => t.Host == "nas.lan");
        Assert.False(nas.IsPublic);
        Assert.Equal(["NAS", "NAS SSH"], nas.UsedBy);

        Assert.True(Assert.Single(targets, t => t.Host == "plex.example.com").IsPublic);
        Assert.DoesNotContain(targets, t => t.Host == "192.168.1.1" || t.Host == "retired.lan");
    }

    [Fact]
    public async Task A_resolver_that_hangs_is_reported_at_the_deadline()
    {
        var target = new DnsCheck.Target("nas", false, ["NAS"]);
        var lookup = await DnsCheck.ResolveAsync(target,
            (_, _) => new TaskCompletionSource<IPAddress[]>().Task,   // deaf to its token, like a stuck resolver
            TimeSpan.FromMilliseconds(100), CancellationToken.None);

        Assert.False(lookup.Resolved);
        Assert.Contains("No answer", lookup.Error);
    }

    [Fact]
    public async Task A_public_name_that_fails_gets_the_no_dns_advice()
    {
        var target = new DnsCheck.Target(DnsCheck.PublicProbe, true, ["Update check"]);
        var lookup = await DnsCheck.ResolveAsync(target,
            (_, _) => Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound)),
            TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(lookup.Resolved);
        Assert.Contains("no working DNS", lookup.Error);
    }

    [Fact]
    public async Task A_name_that_resolves_lists_its_addresses()
    {
        var target = new DnsCheck.Target("nas", false, ["NAS"]);
        var lookup = await DnsCheck.ResolveAsync(target,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("192.168.1.10") }),
            TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(lookup.Resolved);
        Assert.Equal(["192.168.1.10"], lookup.Addresses);
    }
}
