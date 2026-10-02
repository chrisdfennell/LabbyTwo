using LabbyTwo.Core;
using LabbyTwo.Providers;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LabbyTwo.Tests;

/// <summary>
/// The /proc files, read from sample files shaped like the QNAP's on the afternoon it locked
/// up (load 13.4 on 4 cores, disks stalling half of every minute, swap filling).
/// </summary>
public sealed class ProcFileTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 15, 0, 0, TimeSpan.Zero);
    private readonly string _root = TestHost.TempDirectory();

    public ProcFileTests() => Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "proc-qnap-struggling"), _root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private void Write(string relative, string text) => File.WriteAllText(Path.Combine(_root, relative), text);

    [Fact]
    public void Loadavg_reads_the_three_averages_and_ignores_the_rest()
    {
        Assert.Equal((13.4, 12.1, 9.8), ProcFiles.LoadAvg("13.40 12.10 9.80 9/1234 56789\n"));
        Assert.Null(ProcFiles.LoadAvg("garbage"));
        Assert.Null(ProcFiles.LoadAvg(null));
    }

    [Fact]
    public void Meminfo_is_kilobytes_by_name_and_skips_lines_without_a_number()
    {
        var memory = ProcFiles.MemInfo(File.ReadAllText(Path.Combine(_root, "proc", "meminfo")));
        Assert.Equal(7864320UL, memory["MemTotal"]);
        Assert.Equal(393216UL, memory["MemAvailable"]);
        Assert.Equal(8388604UL, memory["SwapTotal"]);
        Assert.Equal(5242880UL, memory["SwapFree"]);
        Assert.Equal(0UL, memory["HugePages_Total"]);
    }

    [Fact]
    public void Stat_differences_give_iowait_and_busy_shares()
    {
        var before = ProcFiles.Stat(File.ReadAllText(Path.Combine(_root, "proc", "stat")))!;
        Assert.Equal(673500UL, before.Total);
        Assert.Equal(500000UL, before.Idle);
        Assert.Equal(40000UL, before.Iowait);

        var after = ProcFiles.Stat("cpu  100600 2000 30200 500200 41000 1000 500 0 0 0\n")!;
        var (iowait, busy) = ProcFiles.CpuShares(before, after)!.Value;
        Assert.Equal(50, iowait, 6);  // 1000 of 2000 ticks
        Assert.Equal(40, busy, 6);    // 2000 − 200 idle − 1000 iowait

        // Counters going backwards is not a share of anything.
        Assert.Null(ProcFiles.CpuShares(after, before));
        Assert.Null(ProcFiles.CpuShares(null, after));
    }

    [Fact]
    public void Pressure_reads_some_and_full_and_tolerates_cpu_without_full()
    {
        var io = ProcFiles.Pressure(File.ReadAllText(Path.Combine(_root, "proc", "pressure", "io")))!;
        Assert.Equal(71.44, io.SomeAvg10, 6);
        Assert.Equal(48.90, io.SomeAvg60, 6);
        Assert.Equal(39.00, io.FullAvg60!.Value, 6);

        var oldCpu = ProcFiles.Pressure("some avg10=1.50 avg60=2.25 avg300=0.10 total=10\n")!;
        Assert.Equal(2.25, oldCpu.SomeAvg60, 6);
        Assert.Null(oldCpu.FullAvg60);

        Assert.Null(ProcFiles.Pressure(""));
    }

    [Fact]
    public void Cpus_are_counted_from_cpuinfo_then_the_online_list()
    {
        Assert.Equal(4, ProcFiles.CpuInfoCount(File.ReadAllText(Path.Combine(_root, "proc", "cpuinfo"))));
        Assert.Equal(4, ProcFiles.OnlineCount("0-3\n"));
        Assert.Equal(5, ProcFiles.OnlineCount("0,2-5"));

        // No cpuinfo: the online list, not the runtime's count (which a CPU limit shrinks).
        File.Delete(Path.Combine(_root, "proc", "cpuinfo"));
        Directory.CreateDirectory(Path.Combine(_root, "sys", "devices", "system", "cpu"));
        Write(Path.Combine("sys", "devices", "system", "cpu", "online"), "0-7\n");
        Assert.Equal((8, "/sys/devices/system/cpu/online"), new HostProc(_root).CpuCount());
    }

    [Fact]
    public void Reading_the_whole_host_from_sample_files()
    {
        var proc = new HostProc(_root);
        var first = proc.Read(T0);

        Assert.True(first.Available);
        Assert.Equal(13.4, first.Load1);
        Assert.Equal(4, first.CpuCount);
        Assert.Equal("/proc/cpuinfo", first.CpuCountFrom);
        Assert.Equal(3.35, first.LoadPerCore!.Value, 6);
        Assert.Equal(5.0, first.MemAvailablePercent!.Value, 1);
        Assert.Equal(37.5, first.SwapUsedPercent!.Value, 1);
        Assert.False(first.MemoryIsContainers);
        Assert.Equal(48.9, first.Io!.SomeAvg60, 6);
        Assert.Equal(6.5, first.Memory!.SomeAvg60, 6);
        Assert.Equal(55.3, first.Cpu!.SomeAvg60, 6);
        Assert.Null(first.IowaitPercent); // nothing to difference yet

        Write(Path.Combine("proc", "stat"), "cpu  100600 2000 30200 500200 41000 1000 500 0 0 0\n");
        var second = proc.Read(T0.AddSeconds(30));
        Assert.Equal(50, second.IowaitPercent!.Value, 6);
    }

    [Fact]
    public void Meminfo_that_matches_the_cgroup_limit_is_the_containers_own()
    {
        // LXCFS: MemTotal is the container's 2 GiB limit, not the NAS's 7.5 GiB.
        Write(Path.Combine("proc", "meminfo"), "MemTotal: 2097152 kB\nMemAvailable: 1048576 kB\nSwapTotal: 0 kB\nSwapFree: 0 kB\n");
        Write(Path.Combine("sys", "fs", "cgroup", "memory.max"), "2147483648\n");
        Assert.True(new HostProc(_root).Read(T0).MemoryIsContainers);

        // Plain Docker: the host's meminfo beside a container limit of its own.
        Write(Path.Combine("proc", "meminfo"), "MemTotal: 7864320 kB\nMemAvailable: 1048576 kB\n");
        Assert.False(new HostProc(_root).Read(T0).MemoryIsContainers);

        Assert.Null(ProcFiles.CgroupLimit("max"));
        Assert.Null(ProcFiles.CgroupLimit("9223372036854771712")); // cgroup v1's "no limit"
        Assert.Equal(2147483648UL, ProcFiles.CgroupLimit("2147483648"));
    }

    [Fact]
    public void Off_linux_there_is_nothing_and_the_host_connection_says_why()
    {
        var empty = TestHost.TempDirectory();
        Directory.CreateDirectory(empty);
        try
        {
            var signals = new HostProc(empty).Read(T0);
            Assert.False(signals.Available);
            Assert.DoesNotContain(HostProvider.ToMetrics(signals), m => m.Key != "cpu_count");
        }
        finally
        {
            Directory.Delete(empty, true);
        }
    }

    /// <summary>
    /// What one pass of reading costs: every file, parsed. Printed for the pull request;
    /// asserted only as a loose ceiling, since a test machine's disk cache is not a NAS's /proc.
    /// </summary>
    [Fact]
    public void Reading_everything_costs_well_under_a_millisecond()
    {
        var proc = new HostProc(_root);
        proc.Read(T0);
        var reads = 1000;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < reads; i++)
            proc.Read(T0.AddSeconds(i));
        clock.Stop();
        var each = clock.Elapsed / reads;
        Console.WriteLine($"Host pressure: {each.TotalMicroseconds:0} µs per read of /proc");
        Assert.True(each < TimeSpan.FromMilliseconds(5), $"{each.TotalMicroseconds:0} µs per read");
    }

    [Fact]
    public void The_host_connections_metrics_are_the_signals()
    {
        var metrics = HostProvider.ToMetrics(new HostProc(_root).Read(T0));
        Assert.Equal(3.35, metrics["load_per_core"]);
        Assert.Equal(48.9, metrics["psi_io_avg60"]);
        Assert.Equal(4, metrics["cpu_count"]);
        Assert.False(metrics.ContainsKey("iowait_percent"));
    }
}

/// <summary>
/// The level: sustain before it rises, hysteresis before it falls, and one notice per episode.
/// </summary>
public sealed class PressureTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 15, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(30);

    private static HostSignals Signals(double load, int cpus = 4, Psi? io = null, ulong? available = 4_000_000,
        ulong? swapFree = 8_000_000, double? iowait = 2) =>
        new(T0, load, load, load, cpus, "test", 8_000_000, available, 8_000_000, swapFree, false, iowait, 20, null, null, io);

    /// <summary>Steps the tracker every 30 s for <paramref name="span"/>, returning the time after the last step.</summary>
    private static DateTimeOffset Run(PressureTracker tracker, DateTimeOffset from, TimeSpan span, Func<HostSignals> signals, double? qnap = null)
    {
        var at = from;
        for (; at <= from + span; at += Step)
            tracker.Step(at, signals(), qnap);
        return at;
    }

    [Fact]
    public void Load_of_thirteen_on_four_cores_is_busy_after_two_minutes_and_strained_after_five()
    {
        var tracker = new PressureTracker();
        tracker.Step(T0, Signals(13.4));
        Assert.Equal(PressureLevel.Ok, tracker.Level);
        Assert.Equal(PressureLevel.Strained, tracker.Raw);
        Assert.Contains("load 13.4 on 4 cores", tracker.Reasons);

        Run(tracker, T0.AddSeconds(30), TimeSpan.FromSeconds(60), () => Signals(13.4));
        Assert.Equal(PressureLevel.Ok, tracker.Level); // 1.5 minutes in

        tracker.Step(T0.AddMinutes(2), Signals(13.4));
        Assert.Equal(PressureLevel.Busy, tracker.Level);
        Assert.False(tracker.StartOwed);

        Run(tracker, T0.AddMinutes(2.5), TimeSpan.FromMinutes(2), () => Signals(13.4));
        Assert.Equal(PressureLevel.Busy, tracker.Level); // 4.5 minutes

        tracker.Step(T0.AddMinutes(5), Signals(13.4));
        Assert.Equal(PressureLevel.Strained, tracker.Level);
        Assert.True(tracker.StartOwed);
    }

    [Fact]
    public void A_short_burst_never_reaches_a_level()
    {
        var tracker = new PressureTracker();
        var at = Run(tracker, T0, TimeSpan.FromSeconds(90), () => Signals(20));
        Run(tracker, at, TimeSpan.FromMinutes(10), () => Signals(1));
        Assert.Equal(PressureLevel.Ok, tracker.Level);
        Assert.False(tracker.StartOwed);
    }

    [Fact]
    public void Disks_stalling_over_forty_percent_is_strained_whatever_the_load()
    {
        var tracker = new PressureTracker();
        Run(tracker, T0, TimeSpan.FromMinutes(5), () => Signals(1, io: new Psi(70, 48.9, 30)));
        Assert.Equal(PressureLevel.Strained, tracker.Level);
        Assert.Contains("tasks stalled on the disks 49% of the last minute", tracker.Reasons);
    }

    [Fact]
    public void Hovering_just_under_the_line_does_not_clear_it_and_well_under_clears_after_five_minutes()
    {
        var tracker = new PressureTracker();
        var at = Run(tracker, T0, TimeSpan.FromMinutes(5), () => Signals(13.4));
        Assert.Equal(PressureLevel.Strained, tracker.Level);

        // 1.9 per core: under the 2.0 that raised it, over the 1.5 it must fall below.
        at = Run(tracker, at, TimeSpan.FromMinutes(20), () => Signals(7.6));
        Assert.Equal(PressureLevel.Strained, tracker.Level);

        // 1.4 per core: below three quarters of Strained, still above three quarters of Busy.
        at = Run(tracker, at, TimeSpan.FromMinutes(4.5), () => Signals(5.6));
        Assert.Equal(PressureLevel.Strained, tracker.Level); // four and a half minutes below
        tracker.Step(at, Signals(5.6));
        Assert.Equal(PressureLevel.Busy, tracker.Level);

        // Fully quiet for five more minutes: OK.
        at = Run(tracker, at + Step, TimeSpan.FromMinutes(5), () => Signals(0.5));
        Assert.Equal(PressureLevel.Ok, tracker.Level);
    }

    [Fact]
    public void One_notice_per_episode_however_high_it_climbs_and_one_when_it_ends()
    {
        var tracker = new PressureTracker();
        var at = Run(tracker, T0, TimeSpan.FromMinutes(5), () => Signals(13.4));
        Assert.True(tracker.StartOwed);
        tracker.StartSent();

        // Worse: Critical, for a long time. Nothing more is owed.
        at = Run(tracker, at, TimeSpan.FromMinutes(30), () => Signals(20));
        Assert.Equal(PressureLevel.Critical, tracker.Level);
        Assert.False(tracker.StartOwed);
        Assert.False(tracker.ClearOwed);

        // Calm: one recovery, naming the worst it got.
        Run(tracker, at, TimeSpan.FromMinutes(15), () => Signals(0.5));
        Assert.Equal(PressureLevel.Ok, tracker.Level);
        Assert.True(tracker.ClearOwed);
        Assert.Equal(PressureLevel.Critical, tracker.Worst);
        tracker.ClearSent();
        Assert.False(tracker.ClearOwed);
    }

    [Fact]
    public void A_start_never_sent_is_not_followed_by_a_recovery()
    {
        var tracker = new PressureTracker();
        var at = Run(tracker, T0, TimeSpan.FromMinutes(5), () => Signals(13.4));
        Assert.True(tracker.StartOwed);

        // Held (quiet hours) the whole time, and over before the hold lifted.
        Run(tracker, at, TimeSpan.FromMinutes(15), () => Signals(0.5));
        Assert.False(tracker.StartOwed);
        Assert.False(tracker.ClearOwed);
    }

    [Fact]
    public void Swap_filling_while_memory_is_short_is_strained()
    {
        var tracker = new PressureTracker();
        ulong free = 8_000_000;
        var at = T0;
        for (var i = 0; i <= 20; i++, at += Step)
        {
            tracker.Step(at, Signals(0.5, available: 800_000, swapFree: free));
            free -= 20 * 1024; // 20 MB every 30 s: 200 MB in five minutes
        }
        Assert.True(tracker.SwapGrowthKb > PressureTracker.SwapFillingKb);
        Assert.Equal(PressureLevel.Strained, tracker.Level);
        Assert.Contains(tracker.Reasons, r => r.StartsWith("swap filling", StringComparison.Ordinal));
    }

    [Fact]
    public void Comparisons_are_strict()
    {
        Assert.Equal(PressureLevel.Busy, PressureTracker.Judge(Signals(8), null, null, 1).Level);        // exactly 2.0
        Assert.Equal(PressureLevel.Strained, PressureTracker.Judge(Signals(8.04), null, null, 1).Level);
        Assert.Equal(PressureLevel.Ok, PressureTracker.Judge(Signals(4), null, null, 1).Level);         // exactly 1.0
        Assert.Equal(PressureLevel.Ok, PressureTracker.Judge(Signals(1), null, 90, 1).Level);           // QTS at exactly 90
        Assert.Equal(PressureLevel.Busy, PressureTracker.Judge(Signals(1), null, 95, 1).Level);
    }

    [Fact]
    public void The_sentence_leads_with_why_and_names_the_busiest()
    {
        var text = PressureTracker.Sentence("NAS", ["load 13.4 on 4 cores", "swap filling (+180 MB in 5 min)"],
            ["tdarr 193% CPU, 40 MB/s read", "plex 40% CPU"]);
        Assert.Equal("The NAS is struggling: load 13.4 on 4 cores, swap filling (+180 MB in 5 min). " +
                     "Busiest: tdarr 193% CPU, 40 MB/s read; plex 40% CPU.", text);
    }
}

/// <summary>
/// The watch end to end with a real database and alert service: one notice naming the busiest
/// containers, held by maintenance, and the one-tap pause through the self-healing guardrails
/// against a scripted Docker.
/// </summary>
public sealed class HostPressureWatchTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 15, 0, 0, TimeSpan.Zero);
    private readonly string _directory = TestHost.TempDirectory();
    private readonly Recording _channel = new();
    private readonly ServiceProvider _services;

    private sealed class Recording : IAlertChannel
    {
        public string Type => "recording";
        public string DisplayName => "Recording";
        public string Icon => "";
        public string Description => "";
        public IReadOnlyList<FieldSpec> Fields => [];
        public List<Alert> Sent { get; } = [];

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) => Task.FromResult(ProbeResult.Up(TimeSpan.Zero));

        public Task SendAsync(Connection channel, Alert alert, CancellationToken ct)
        {
            lock (Sent)
                Sent.Add(alert);
            return Task.CompletedTask;
        }
    }

    public HostPressureWatchTests()
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        services.AddTestStorage(_directory);
        services.AddSingleton<IConnectionProvider>(_channel);
        services.AddSingleton<IConnectionProvider>(new DockerProvider());
        services.AddSingleton<IEnumerable<IWidgetType>>([]);
        services.AddSingleton<IEnumerable<ITabKind>>([]);
        services.AddSingleton<Registry>();
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<AlertRuleStore>();
        services.AddSingleton<HistoryStore>();
        services.AddSingleton<HealthMonitor>();
        services.AddSingleton<AlertService>();
        services.AddSingleton<ActionRunner>();
        services.AddSingleton<RemediationActions>();
        services.AddSingleton<ChangeStore>();
        services.AddSingleton<HostPressureWatch>();
        _services = services.BuildServiceProvider();
        Get<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();
        Get<AlertService>().Zone = TimeZoneInfo.Utc;
        Get<HostPressureWatch>().Zone = TimeZoneInfo.Utc;
        Get<ConfigStore>().SaveConnectionAsync(new Connection { Provider = "recording", Name = "Phone" }).GetAwaiter().GetResult();
    }

    public void Dispose() => TestHost.Teardown(_services, _directory);

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private static HostSignals Struggling(DateTimeOffset at) =>
        new(at, 13.4, 12.1, 9.8, 4, "test", 7_864_320, 393_216, 8_388_604, 5_242_880, false, 35, 60,
            new Psi(62, 55, 40), new Psi(8, 6.5, 3), new Psi(71, 48.9, 30));

    private static HostSignals Calm(DateTimeOffset at) =>
        new(at, 0.4, 0.5, 0.6, 4, "test", 7_864_320, 4_000_000, 8_388_604, 8_388_604, false, 1, 5,
            new Psi(0, 0, 0), new Psi(0, 0, 0), new Psi(0.5, 0.4, 0.3));

    private static IReadOnlyList<(ContainerResourceSnapshot, ContainerReading)> Busy(string connectionId)
    {
        ContainerReading Reading(string name, double cpu, double readMb) =>
            new(name + "-id", name, "running", cpu, 1UL << 30, 4UL << 30, readMb * 1_000_000, 0, null, null, 12, T0);
        var readings = new[] { Reading("tdarr", 193, 40), Reading("plex", 40, 0), Reading("sonarr", 12, 2) };
        var snapshot = new ContainerResourceSnapshot(connectionId, 1, T0, TimeSpan.FromSeconds(60), TimeSpan.Zero, 3, readings,
            [], new Dictionary<string, double>(), new HashSet<string>(), null);
        return [.. readings.Select(r => (snapshot, r))];
    }

    private async Task<DateTimeOffset> RunAsync(DateTimeOffset from, TimeSpan span, Func<DateTimeOffset, HostSignals> signals)
    {
        var watch = Get<HostPressureWatch>();
        var at = from;
        for (; at <= from + span; at += HostPressureWatch.Every)
            await watch.PassAsync(at, signals(at), default);
        return at;
    }

    [Fact]
    public async Task Struggling_for_five_minutes_notifies_once_naming_the_three_busiest_and_offering_the_pause()
    {
        var watch = Get<HostPressureWatch>();
        watch.BusiestNow = _ => Busy("docker-1");

        var at = await RunAsync(T0, TimeSpan.FromMinutes(4.5), Struggling);
        Assert.Empty(_channel.Sent);
        Assert.Equal(PressureLevel.Busy, watch.Report!.Level);

        at = await RunAsync(at, TimeSpan.FromMinutes(30), Struggling);
        var notice = Assert.Single(_channel.Sent);
        Assert.Equal(AlertLevel.Down, notice.Level);
        Assert.Equal("Host is struggling", notice.Title);
        Assert.StartsWith("The host is struggling: load 13.4 on 4 cores, tasks stalled on the disks 49% of the last minute", notice.Body);
        Assert.Contains("Busiest: tdarr 193% CPU, 40 MB/s read; plex 40% CPU; sonarr 12% CPU, 2 MB/s read.", notice.Body);
        Assert.Contains("pause tdarr for 2 hours", notice.Body);
        Assert.Equal("settings/health?pause=tdarr#host-pressure", notice.Link);
        Assert.Equal(PressureLevel.Strained, watch.Report.Level);
        Assert.Equal(PressureLevel.Strained, HostPressureWatch.Current!.Level);

        await RunAsync(at, TimeSpan.FromMinutes(15), Calm);
        Assert.Equal(2, _channel.Sent.Count);
        Assert.Equal(AlertLevel.Up, _channel.Sent[1].Level);
        Assert.Equal("Host has calmed down", _channel.Sent[1].Title);
    }

    [Fact]
    public async Task With_a_nas_connection_it_says_nas_and_reads_qts_figures()
    {
        await Get<ConfigStore>().SaveConnectionAsync(new Connection { Provider = "qnap", Name = "TS-464", Enabled = true });
        var watch = Get<HostPressureWatch>();
        watch.BusiestNow = _ => [];

        await RunAsync(T0, TimeSpan.FromMinutes(6), Struggling);
        Assert.Equal("NAS is struggling", Assert.Single(_channel.Sent).Title);
    }

    [Fact]
    public async Task Maintenance_holds_the_notice_until_it_ends()
    {
        var watch = Get<HostPressureWatch>();
        watch.BusiestNow = _ => [];
        var settings = Get<AppSettingsStore>();
        await settings.SaveAsync(Maintenance.Key, Maintenance.Indefinite);

        var at = await RunAsync(T0, TimeSpan.FromMinutes(10), Struggling);
        Assert.Empty(_channel.Sent);
        Assert.Equal(PressureLevel.Strained, watch.Report!.Level);

        await settings.SaveAsync(Maintenance.Key, Maintenance.Cleared);
        await RunAsync(at, TimeSpan.FromMinutes(1), Struggling);
        Assert.Single(_channel.Sent);
    }

    /// <summary>A Docker host with three containers: LabbyTwo's own, a protected tunnel, and tdarr.</summary>
    private ScriptedDocker Docker() => new(async context =>
    {
        var path = context.Request.Url!.AbsolutePath;
        if (path.EndsWith("/containers/json", StringComparison.Ordinal))
        {
            await ScriptedDocker.Json(context, $$$"""
                [{"Id":"self0000000000001","Names":["/{{{Environment.MachineName}}}"],"Image":"labbytwo","State":"running","Status":"Up","Labels":{}},
                 {"Id":"tunnel00000000002","Names":["/cloudflared"],"Image":"cloudflared","State":"running","Status":"Up","Labels":{}},
                 {"Id":"tdarr000000000003","Names":["/tdarr"],"Image":"tdarr","State":"running","Status":"Up","Labels":{}}]
                """);
        }
        else
        {
            context.Response.StatusCode = 204;
        }
    });

    private async Task<Connection> DockerConnectionAsync(ScriptedDocker docker)
    {
        var connection = new Connection { Provider = "docker", Name = "NAS Docker" };
        connection.Settings["endpoint"] = docker.Endpoint;
        await Get<ConfigStore>().SaveConnectionAsync(connection);
        var tab = new Tab { Name = "Containers", Kind = RemediationGuards.ContainersTabKind };
        tab.Settings["protected"] = "cloudflared";
        await Get<ConfigStore>().SaveTabAsync(tab);
        return connection;
    }

    [Fact]
    public async Task The_pause_button_never_touches_labbytwo_or_a_protected_container()
    {
        using var docker = Docker();
        var connection = await DockerConnectionAsync(docker);
        var watch = Get<HostPressureWatch>();

        var self = await watch.PauseAsync(connection.Id, Environment.MachineName, T0, default);
        Assert.False(self.Ok);
        Assert.Contains("the container LabbyTwo itself runs in", self.Message);

        var tunnel = await watch.PauseAsync(connection.Id, "cloudflared", T0, default);
        Assert.False(tunnel.Ok);
        Assert.Contains("protected list", tunnel.Message);

        lock (docker.Requests)
            Assert.DoesNotContain(docker.Requests, r => r.StartsWith("POST", StringComparison.Ordinal));
        Assert.Empty(watch.Unpauses);
    }

    [Fact]
    public async Task Pausing_tdarr_for_two_hours_unpauses_it_on_time_even_after_a_restart()
    {
        using var docker = Docker();
        var connection = await DockerConnectionAsync(docker);
        var watch = Get<HostPressureWatch>();
        watch.BusiestNow = _ => [];

        var paused = await watch.PauseAsync(connection.Id, "tdarr", T0, default);
        Assert.True(paused.Ok, paused.Message);
        lock (docker.Requests)
            Assert.Contains(docker.Requests, r => r == "POST /v1.41/containers/tdarr000000000003/pause");
        Assert.Equal(T0 + HostPressureWatch.PauseFor, Assert.Single(watch.Unpauses).Until);

        // A fresh watch — LabbyTwo restarted — still knows, from the settings it kept.
        var restarted = new HostPressureWatch(Get<HealthMonitor>(), Get<ConfigStore>(), Get<AppSettingsStore>(), Get<MuteWindowStore>(),
            Get<AlertService>(), Get<RemediationActions>(), Get<ChangeStore>(),
            _services.GetRequiredService<ILogger<HostPressureWatch>>())
        {
            BusiestNow = _ => [],
            Zone = TimeZoneInfo.Utc,
        };
        await restarted.PassAsync(T0.AddHours(1), Calm(T0.AddHours(1)), default);
        lock (docker.Requests)
            Assert.DoesNotContain(docker.Requests, r => r.EndsWith("/unpause", StringComparison.Ordinal));

        await restarted.PassAsync(T0.AddHours(2).AddMinutes(1), Calm(T0.AddHours(2)), default);
        lock (docker.Requests)
            Assert.Contains(docker.Requests, r => r == "POST /v1.41/containers/tdarr000000000003/unpause");
        Assert.Empty(restarted.Unpauses);

        var feed = await Get<ChangeStore>().QueryAsync(new ChangeQuery(T0.AddDays(-1), T0.AddDays(1)));
        Assert.Contains(feed, c => c.Title == "Paused tdarr for 2 hours");
        Assert.Contains(feed, c => c.Title == "Unpaused tdarr");
    }
}
