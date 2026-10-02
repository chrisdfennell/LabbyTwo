#pragma warning disable BL0006 // The interactive test renderer reads the render tree back, which is the point of it.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using LabbyTwo.Components.Shared;
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
/// Stats payloads as the two cgroup versions write them. Crafted from real answers (ids,
/// names and most of the unrelated fields cut), keeping every field the parser reads and
/// the ones it must ignore — v1's Sync, Async and Total block-I/O rows above all.
/// </summary>
internal static class StatsPayloads
{
    public static string V1(ulong cpu, ulong system, string read, ulong blockRead, ulong blockWrite, ulong pids = 42, bool oneShot = true) => $$"""
        {
          "read": "{{read}}",
          "preread": "0001-01-01T00:00:00Z",
          "pids_stats": { "current": {{pids}} },
          "blkio_stats": {
            "io_service_bytes_recursive": [
              { "major": 8, "minor": 0, "op": "Read", "value": {{blockRead - blockRead / 4}} },
              { "major": 8, "minor": 0, "op": "Write", "value": {{blockWrite}} },
              { "major": 8, "minor": 0, "op": "Sync", "value": {{blockRead}} },
              { "major": 8, "minor": 0, "op": "Async", "value": {{blockWrite}} },
              { "major": 8, "minor": 0, "op": "Discard", "value": 0 },
              { "major": 8, "minor": 0, "op": "Total", "value": {{blockRead + blockWrite}} },
              { "major": 8, "minor": 16, "op": "Read", "value": {{blockRead / 4}} },
              { "major": 8, "minor": 16, "op": "Write", "value": 0 },
              { "major": 8, "minor": 16, "op": "Total", "value": {{blockRead / 4}} }
            ],
            "io_serviced_recursive": [ { "major": 8, "minor": 0, "op": "Read", "value": 12345 } ],
            "io_queue_recursive": [], "io_service_time_recursive": [], "io_wait_time_recursive": [],
            "io_merged_recursive": [], "io_time_recursive": [], "sectors_recursive": []
          },
          "num_procs": 0,
          "storage_stats": {},
          "cpu_stats": {
            "cpu_usage": {
              "total_usage": {{cpu}},
              "percpu_usage": [ {{cpu / 4}}, {{cpu / 4}}, {{cpu / 4}}, {{cpu / 4}} ],
              "usage_in_kernelmode": {{cpu / 10}},
              "usage_in_usermode": {{cpu - cpu / 10}}
            },
            "system_cpu_usage": {{system}},
            "online_cpus": 4,
            "throttling_data": { "periods": 0, "throttled_periods": 0, "throttled_time": 0 }
          },
          "precpu_stats": {
            "cpu_usage": { "total_usage": 0, "usage_in_kernelmode": 0, "usage_in_usermode": 0 },
            "throttling_data": { "periods": 0, "throttled_periods": 0, "throttled_time": 0 }
          },
          "memory_stats": {
            "usage": 1610612736,
            "max_usage": 2147483648,
            "stats": { "cache": 600000000, "rss": 1000000000, "total_inactive_file": 536870912, "total_rss": 1000000000 },
            "limit": 4294967296
          },
          "name": "/tdarr",
          "id": "4d1bd83d3e5f2a8c9f0e7b6a5d4c3b2a1f0e9d8c7b6a5f4e3d2c1b0a9f8e7d6c",
          "networks": {
            "eth0": { "rx_bytes": 1000, "rx_packets": 10, "rx_errors": 0, "rx_dropped": 0, "tx_bytes": 2000, "tx_packets": 20, "tx_errors": 0, "tx_dropped": 0 }
          }
        }
        """;

    public static string V2(ulong cpu, ulong system, string read, ulong blockRead, ulong blockWrite, ulong pids = 17, bool nullBlkio = false) => $$"""
        {
          "read": "{{read}}",
          "preread": "0001-01-01T00:00:00Z",
          "pids_stats": { "current": {{pids}}, "limit": 4615 },
          "blkio_stats": {
            "io_service_bytes_recursive": {{(nullBlkio ? "null" : $$$"""
              [
                { "major": 259, "minor": 0, "op": "read", "value": {{{blockRead - blockRead / 4}}} },
                { "major": 259, "minor": 0, "op": "write", "value": {{{blockWrite}}} },
                { "major": 8, "minor": 0, "op": "read", "value": {{{blockRead / 4}}} },
                { "major": 8, "minor": 0, "op": "write", "value": 0 }
              ]
              """)}},
            "io_serviced_recursive": null, "io_queue_recursive": null, "io_service_time_recursive": null,
            "io_wait_time_recursive": null, "io_merged_recursive": null, "io_time_recursive": null, "sectors_recursive": null
          },
          "num_procs": 0,
          "storage_stats": {},
          "cpu_stats": {
            "cpu_usage": { "total_usage": {{cpu}}, "usage_in_kernelmode": {{cpu / 10}}, "usage_in_usermode": {{cpu - cpu / 10}} },
            "system_cpu_usage": {{system}},
            "online_cpus": 4,
            "throttling_data": { "periods": 0, "throttled_periods": 0, "throttled_time": 0 }
          },
          "precpu_stats": {
            "cpu_usage": { "total_usage": 0, "usage_in_kernelmode": 0, "usage_in_usermode": 0 },
            "throttling_data": { "periods": 0, "throttled_periods": 0, "throttled_time": 0 }
          },
          "memory_stats": {
            "usage": 838860800,
            "stats": { "active_anon": 500000000, "active_file": 10000000, "anon": 600000000, "file": 230000000, "inactive_file": 209715200 },
            "limit": 8236548096
          },
          "name": "/plex",
          "id": "9f8e7d6c5b4a3f2e1d0c9b8a7f6e5d4c3b2a1f0e9d8c7b6a5f4e3d2c1b0a9f8e",
          "networks": {
            "eth0": { "rx_bytes": 5000, "tx_bytes": 7000 },
            "eth1": { "rx_bytes": 1000, "tx_bytes": 0 }
          }
        }
        """;
}

/// <summary>The parsing, the arithmetic, and the choice of what to record.</summary>
public sealed class ContainerUsageTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_cgroup_v1_answer_counts_only_read_and_write_rows_over_every_device()
    {
        var sample = DockerContainers.ParseSample(StatsPayloads.V1(400_000_000_000, 4_000_000_000_000_000, "2026-10-02T15:00:00.123456789Z",
            blockRead: 1_200_000_000, blockWrite: 200_000_000));

        // Read 900 MB on sda and 300 MB on sdb; Sync, Async and Total are the same bytes again.
        Assert.Equal(1_200_000_000UL, sample.BlockRead);
        Assert.Equal(200_000_000UL, sample.BlockWrite);
        Assert.Equal(42UL, sample.Pids);
        Assert.Equal(4, sample.OnlineCpus);
        Assert.Equal(0UL, sample.PreSystemCpu); // one-shot leaves precpu empty
        Assert.Equal(1610612736UL - 536870912UL, sample.MemoryUsed); // page cache taken off, as docker stats does
        Assert.Equal(4294967296UL, sample.MemoryLimit);
    }

    [Fact]
    public void A_cgroup_v2_answer_has_lower_case_ops_and_may_have_no_list_at_all()
    {
        var sample = DockerContainers.ParseSample(StatsPayloads.V2(1, 1, "2026-10-02T15:00:00Z", blockRead: 400_000_000, blockWrite: 50_000_000));
        Assert.Equal(400_000_000UL, sample.BlockRead);
        Assert.Equal(50_000_000UL, sample.BlockWrite);
        Assert.Equal(17UL, sample.Pids);
        Assert.Equal(838860800UL - 209715200UL, sample.MemoryUsed);
        Assert.Equal(6000UL, sample.RxBytes);

        var none = DockerContainers.ParseSample(StatsPayloads.V2(1, 1, "2026-10-02T15:00:00Z", 0, 0, nullBlkio: true));
        Assert.Equal(0UL, none.BlockRead);
        Assert.Equal(0UL, none.BlockWrite);
    }

    [Fact]
    public void One_shot_cpu_and_rates_come_from_our_own_previous_sample()
    {
        // 60 s apart; the container used 116 s of CPU while the host's 4 CPUs did 240 s.
        var first = DockerContainers.ParseSample(StatsPayloads.V2(1_000_000_000_000, 100_000_000_000_000, "2026-10-02T15:00:00Z",
            blockRead: 10_000_000_000, blockWrite: 1_000_000_000));
        var second = DockerContainers.ParseSample(StatsPayloads.V2(1_116_000_000_000, 100_240_000_000_000, "2026-10-02T15:01:00Z",
            blockRead: 12_400_000_000, blockWrite: 1_060_000_000));

        Assert.Null(DockerContainers.Compute(first, null).CpuPercent); // nothing to difference on a one-shot first ask
        var stats = DockerContainers.Compute(second, first);
        Assert.Equal(193.33, stats.CpuPercent!.Value, 2);             // 116/240 × 4 cores × 100
        Assert.Equal(40_000_000, stats.ReadPerSecond!.Value, 3);       // 2.4 GB in 60 s
        Assert.Equal(1_000_000, stats.WritePerSecond!.Value, 3);
        Assert.Equal(17UL, stats.Pids);
    }

    [Fact]
    public void A_restart_under_the_same_id_resets_the_counters_and_gives_no_reading_rather_than_exabytes()
    {
        var before = DockerContainers.ParseSample(StatsPayloads.V1(900_000_000_000, 100_000_000_000_000, "2026-10-02T15:00:00Z",
            blockRead: 50_000_000_000, blockWrite: 5_000_000_000));
        var after = DockerContainers.ParseSample(StatsPayloads.V1(2_000_000_000, 100_240_000_000_000, "2026-10-02T15:01:00Z",
            blockRead: 1_000_000, blockWrite: 0));

        var stats = DockerContainers.Compute(after, before);
        Assert.Null(stats.CpuPercent);
        Assert.Null(stats.ReadPerSecond);
        Assert.Null(stats.WritePerSecond);

        Assert.Null(DockerContainers.Rate(5, 10, 60));
        Assert.Null(DockerContainers.Rate(10, 5, 0));
        Assert.Equal(1.0, DockerContainers.Rate(70, 10, 60));
    }

    private static ContainerReading Reading(string name, double? cpu, double readMb = 0, double writeMb = 0, string state = "running",
        ulong memory = 100UL << 20, ulong limit = 0) =>
        new(name + "-id", name, state, cpu, memory, limit, readMb * 1_000_000, writeMb * 1_000_000, 1000, 2000, 5, T0);

    [Fact]
    public void The_busiest_by_cpu_and_by_disk_take_turns_idle_ones_never_count_and_pinned_ones_always_do()
    {
        var readings = new[]
        {
            Reading("tdarr", 193, readMb: 40),
            Reading("plex", 80),
            Reading("sonarr", 30),
            Reading("radarr", 25),
            Reading("syncthing", 2, readMb: 12),
            Reading("restic", 1, writeMb: 30),
            Reading("idle", 0.1),
            Reading("sleepy", 0.2, readMb: 0.01),
            Reading("stopped", null, state: "exited"),
        };

        Assert.Equal(["tdarr", "plex", "restic", "sonarr", "syncthing"], ContainerUsage.Choose(readings, 5, []));
        Assert.Equal(["tdarr", "plex", "restic"], ContainerUsage.Choose(readings, 3, []));
        // Everything busy fits in 10; idle ones are left out even with room to spare.
        Assert.Equal(6, ContainerUsage.Choose(readings, 10, []).Count);
        // Pinned on top, whatever it is doing, matched without regard to case.
        Assert.Equal(["tdarr", "plex", "idle"], ContainerUsage.Choose(readings, 2, ["IDLE", "not-there"]));
        // Zero means pinned only.
        Assert.Equal(["sleepy"], ContainerUsage.Choose(readings, 0, ["sleepy"]));
    }

    [Fact]
    public void Each_container_gets_its_own_keys_and_the_busiest_three_cover_everything_running()
    {
        var metrics = new Dictionary<string, double>();
        ContainerUsage.AddMetrics(metrics, Reading("Tdarr Node", 193.04, readMb: 40, writeMb: 1.234, memory: 1536UL << 20));
        Assert.Equal(193.0, metrics["container_cpu:tdarr-node"]);
        Assert.Equal(1536, metrics["container_mem_mb:tdarr-node"]);
        Assert.Equal(40, metrics["container_read_mbs:tdarr-node"]);
        Assert.Equal(1.23, metrics["container_write_mbs:tdarr-node"]);

        // Stopped: zero CPU and disk, which is what lets an alert on it clear; memory not claimed.
        ContainerUsage.AddMetrics(metrics, Reading("gone", null, state: "exited"));
        Assert.Equal(0, metrics["container_cpu:gone"]);
        Assert.False(metrics.ContainsKey("container_mem_mb:gone"));

        var busiest = new Dictionary<string, double>();
        var host = 8UL << 30;
        ContainerUsage.AddBusiest(busiest, [
            Reading("tdarr", 193, readMb: 40, memory: 1UL << 30, limit: host), // no limit of its own: reports the host's
            Reading("db", 5, memory: 950UL << 20, limit: 1UL << 30),
            Reading("off", null, state: "exited"),
        ], host);
        Assert.Equal(193, busiest[ContainerUsage.BusiestCpuMetric]);
        Assert.Equal(40, busiest[ContainerUsage.BusiestReadMetric]);
        Assert.Equal(92.8, busiest[ContainerUsage.FullestMemoryMetric], 1);

        Assert.True(ContainerUsage.TryParse("container_cpu:tdarr", out var metric, out var name));
        Assert.Equal(("container_cpu", "tdarr"), (metric, name));
        Assert.False(ContainerUsage.TryParse("disk_percent:vol2", out _, out _));
    }

    [Fact]
    public void A_busy_container_is_described_by_what_matters()
    {
        Assert.Equal("tdarr 193% CPU, 40 MB/s read", ContainerUsage.Describe(Reading("tdarr", 193.4, readMb: 40)));
        Assert.Equal("restic 1.5% CPU, 350 kB/s written", ContainerUsage.Describe(Reading("restic", 1.5, writeMb: 0.35)));
        Assert.Equal("idle (quiet)", ContainerUsage.Describe(Reading("idle", 0.1)));
        Assert.Equal(["tdarr", "restic"], ContainerUsage.Busiest([Reading("restic", 1, writeMb: 30), Reading("tdarr", 193, readMb: 40), Reading("idle", 0)], 3).Select(r => r.Name));
    }

    [Fact]
    public void Settings_are_held_inside_what_the_nas_can_afford()
    {
        Assert.Equal(30, ContainerUsage.Seconds(5));
        Assert.Equal(300, ContainerUsage.Seconds(3600));
        Assert.Equal(0, ContainerUsage.Seconds(0));
        Assert.Equal(60, ContainerUsage.Seconds(60));
        Assert.Equal(25, ContainerUsage.Top(500));
        Assert.Equal(["tdarr", "plex"], ContainerUsage.ParsePinned(" tdarr, /plex ,tdarr"));
    }

    [Fact]
    public void A_round_starts_only_when_due_and_never_on_top_of_the_last()
    {
        var gate = new PollGate();
        var minute = TimeSpan.FromMinutes(1);

        Assert.True(gate.TryStart(T0, minute));
        Assert.False(gate.TryStart(T0.AddSeconds(30), minute)); // not due
        Assert.Equal(0, gate.Skipped);
        Assert.False(gate.TryStart(T0.AddSeconds(65), minute)); // due, but still running
        Assert.Equal(1, gate.Skipped);

        gate.Finish();
        Assert.True(gate.TryStart(T0.AddSeconds(70), minute));
        Assert.False(gate.TryStart(T0.AddSeconds(100), minute));
    }
}

/// <summary>
/// The poller against a scripted Docker over real HTTP: concurrency, one-shot, the rounds
/// that skip, what reaches history through the Docker probe, the "busiest" self-healing
/// target, and the cards that draw it.
/// </summary>
public sealed class ContainerResourcesTests : IAsyncDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private InteractiveRenderer? _renderer;

    public ContainerResourcesTests()
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        services.AddTestStorage(_directory);
        services.AddSingleton<IConnectionProvider>(new DockerProvider());
        services.AddSingleton<IEnumerable<IWidgetType>>([]);
        services.AddSingleton<IEnumerable<ITabKind>>([]);
        services.AddSingleton<Registry>();
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<AlertRuleStore>();
        services.AddSingleton<HistoryStore>();
        services.AddSingleton<HealthMonitor>();
        services.AddSingleton<Offload>();
        services.AddSingleton<SharedSeries>();
        services.AddSingleton<ActionRunner>();
        services.AddSingleton<RemediationActions>();
        services.AddSingleton<RemediationStore>();
        services.AddSingleton<ContainerResources>();
        _services = services.BuildServiceProvider();
        Get<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();
        Get<ContainerResources>().HostMemory = () => 8UL << 30;
    }

    public async ValueTask DisposeAsync()
    {
        if (_renderer is not null)
            await _renderer.DisposeAsync();
        TestHost.Teardown(_services, _directory);
    }

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private InteractiveRenderer Renderer => _renderer ??= new InteractiveRenderer(_services);

    /// <summary>
    /// A Docker host with <paramref name="running"/> running containers c0…cN and one stopped.
    /// Each stats answer moves its container's counters on by one "minute": container i uses
    /// 4·i % CPU and reads 0.1·i MB/s, so the numbers that come out are known in advance.
    /// </summary>
    private sealed class FakeHost : IDisposable
    {
        private readonly ConcurrentDictionary<string, int> _asked = new();

        public FakeHost(int running, TimeSpan delay, string? self = null)
        {
            Docker = new ScriptedDocker(async context =>
            {
                var path = context.Request.Url!.AbsolutePath;
                if (path.EndsWith("/containers/json", StringComparison.Ordinal))
                {
                    var rows = Enumerable.Range(0, running).Select(i =>
                            $$$"""{"Id":"c{{{i}}}","Names":["/{{{(i == 0 && self is not null ? self : $"c{i}")}}}"],"Image":"img","State":"running","Status":"Up","Labels":{}}""")
                        .Append("""{"Id":"off","Names":["/off"],"Image":"img","State":"exited","Status":"Exited (0)","Labels":{}}""");
                    await ScriptedDocker.Json(context, "[" + string.Join(",", rows) + "]");
                    return;
                }
                if (path.Contains("/stats", StringComparison.Ordinal))
                {
                    await Task.Delay(delay);
                    var id = path.Split('/')[3];
                    var i = ulong.Parse(id[1..]);
                    var n = (ulong)_asked.AddOrUpdate(id, 1, (_, x) => x + 1);
                    var read = new DateTimeOffset(2026, 10, 2, 15, 0, 0, TimeSpan.Zero).AddMinutes(n).ToString("O");
                    // 4 CPUs over 60 s is 240 s of host CPU; 4·i % of a core is 0.04·i·60 s = 2.4·i s.
                    await ScriptedDocker.Json(context, StatsPayloads.V2(
                        cpu: n * i * 2_400_000_000, system: n * 240_000_000_000, read,
                        blockRead: n * i * 6_000_000, blockWrite: 0));
                    return;
                }
                context.Response.StatusCode = 204;
            });
        }

        public ScriptedDocker Docker { get; }

        public void Dispose() => Docker.Dispose();
    }

    private async Task<Connection> ConnectionAsync(FakeHost host, string top = "10", string pinned = "", string seconds = "60")
    {
        var connection = new Connection { Provider = "docker", Name = "NAS Docker" };
        connection.Settings["endpoint"] = host.Docker.Endpoint;
        connection.Settings["stats_top"] = top;
        connection.Settings["stats_pinned"] = pinned;
        connection.Settings["stats_seconds"] = seconds;
        await Get<ConfigStore>().SaveConnectionAsync(connection);
        return connection;
    }

    private async Task<ContainerResourceSnapshot> TwoRoundsAsync(Connection connection, FakeHost host)
    {
        await Get<ContainerResources>().RoundAsync(connection, TimeSpan.FromMinutes(1), default);
        DockerContainers.ForgetRecentStats(host.Docker.Endpoint);
        return (await Get<ContainerResources>().RoundAsync(connection, TimeSpan.FromMinutes(1), default))!;
    }

    [Fact]
    public async Task A_round_asks_one_shot_four_at_a_time_and_the_second_round_has_cpu_and_rates()
    {
        using var host = new FakeHost(12, TimeSpan.FromMilliseconds(60));
        var connection = await ConnectionAsync(host, top: "3", pinned: "c1");

        var first = (await Get<ContainerResources>().RoundAsync(connection, TimeSpan.FromMinutes(1), default))!;
        Assert.Null(first.Error);
        Assert.Equal(12, first.Requests);
        Assert.All(first.Running, r => Assert.Null(r.CpuPercent)); // nothing to difference yet
        Assert.Equal("exited", first.Find("off")!.State);

        DockerContainers.ForgetRecentStats(host.Docker.Endpoint);
        var second = (await Get<ContainerResources>().RoundAsync(connection, TimeSpan.FromMinutes(1), default))!;
        Assert.Equal(44, second.Find("c11")!.CpuPercent!.Value, 6);
        Assert.Equal(1_100_000, second.Find("c11")!.ReadPerSecond!.Value, 3);

        List<string> stats;
        lock (host.Docker.Requests)
            stats = [.. host.Docker.Requests.Where(r => r.Contains("/stats", StringComparison.Ordinal))];
        Assert.Equal(24, stats.Count);
        Assert.All(stats, r => Assert.Contains("stream=false&one-shot=true", r));
        Assert.DoesNotContain(stats, r => r.Contains("/off/", StringComparison.Ordinal)); // stopped: not asked
        Assert.InRange(host.Docker.MaxInFlight, 1, DockerContainers.StatsConcurrency);
        lock (host.Docker.Requests)
            Assert.Single(host.Docker.Requests, r => r.Contains("/containers/json", StringComparison.Ordinal)); // the second round reused the list

        // Top three by CPU or disk (c11, c10, c9 lead both), plus pinned c1.
        Assert.Equal(["c11", "c10", "c9", "c1"], second.Recorded);
        Assert.Contains("container_cpu:c11", second.RecordedKeys);
        Assert.Contains("container_cpu:c1", second.RecordedKeys);
        Assert.DoesNotContain("container_cpu:c5", second.Metrics.Keys);
        Assert.Equal(44, second.Metrics[ContainerUsage.BusiestCpuMetric], 6);
        Assert.Equal(1.1, second.Metrics[ContainerUsage.BusiestReadMetric], 6);
    }

    [Fact]
    public async Task A_round_due_while_the_last_is_still_going_is_skipped_and_polling_can_be_turned_off()
    {
        using var host = new FakeHost(8, TimeSpan.FromMilliseconds(250));
        var connection = await ConnectionAsync(host, seconds: "30");
        var poller = Get<ContainerResources>();
        var t0 = DateTimeOffset.UtcNow;

        var started = await poller.TickAsync(t0, default);
        var round = Assert.Single(started);
        Assert.Empty(await poller.TickAsync(t0.AddSeconds(35), default)); // due, but the first is still asking
        Assert.Equal(1, poller.Gate(connection.Id)!.Skipped);

        await round;
        await Assert.Single(await poller.TickAsync(t0.AddSeconds(40), default));
        Assert.Empty(await poller.TickAsync(t0.AddSeconds(45), default)); // not due again yet

        await Get<ConfigStore>().SaveConnectionAsync(connection with { Settings = new SettingsBag(connection.Settings) { ["stats_seconds"] = "0" } });
        Assert.Empty(await poller.TickAsync(t0.AddHours(1), default));
        Assert.Null(ContainerResourceSnapshots.Get(connection.Id));
    }

    [Fact]
    public async Task The_first_probe_after_a_round_records_it_and_later_probes_only_pass_it_on()
    {
        using var host = new FakeHost(4, TimeSpan.Zero);
        var connection = await ConnectionAsync(host, top: "2");
        var snapshot = await TwoRoundsAsync(connection, host);
        var now = DateTimeOffset.UtcNow;

        var first = new Dictionary<string, double>();
        var skip = DockerProvider.WithContainerResources(connection, first, now);
        Assert.Equal(44 / 11.0 * 3, first["container_cpu:c3"], 6);
        Assert.Contains(ContainerUsage.BusiestCpuMetric, first.Keys);
        Assert.True(skip is null || snapshot.RecordedKeys.All(k => !skip.Contains(k)));

        var again = new Dictionary<string, double>();
        var skipAgain = DockerProvider.WithContainerResources(connection, again, now);
        Assert.Equal(first.Keys.Order(), again.Keys.Order());
        Assert.Equal(again.Keys.Order(), skipAgain!.Order()); // same round: live only, nothing written twice

        // Stale — the poller stopped — and nothing is passed on at all.
        var stale = new Dictionary<string, double>();
        Assert.Null(DockerProvider.WithContainerResources(connection, stale, now.AddMinutes(10)));
        Assert.Empty(stale);
    }

    /// <summary>Hands back whatever the test set, with a NotRecorded set, so the monitor's filter is what is tested.</summary>
    private sealed class Stub : IConnectionProvider
    {
        public ProbeResult Result { get; set; } = ProbeResult.Up(TimeSpan.Zero);
        public string Type => "stub-resources";
        public string DisplayName => "Stub";
        public string Icon => "";
        public string Description => "";
        public IReadOnlyList<FieldSpec> Fields => [];
        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) => Task.FromResult(Result);
    }

    [Fact]
    public async Task The_monitor_keeps_not_recorded_metrics_live_and_out_of_history()
    {
        var stub = new Stub();
        var services = new ServiceCollection();
        var directory = TestHost.TempDirectory();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(directory, "keys")));
        services.AddTestStorage(directory);
        services.AddSingleton<IConnectionProvider>(stub);
        services.AddSingleton<IEnumerable<IWidgetType>>([]);
        services.AddSingleton<IEnumerable<ITabKind>>([]);
        services.AddSingleton<Registry>();
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<HistoryStore>();
        services.AddSingleton<HealthMonitor>();
        await using var provider = services.BuildServiceProvider();
        try
        {
            await provider.GetRequiredService<Db>().EnsureSchemaAsync();
            var connection = new Connection { Provider = "stub-resources", Name = "Docker" };
            await provider.GetRequiredService<ConfigStore>().SaveConnectionAsync(connection);
            IReadOnlyDictionary<string, double>? written = null;
            provider.GetRequiredService<HistoryStore>().Recorded += (_, metrics, _) => written = metrics;

            stub.Result = ProbeResult.Up(TimeSpan.Zero, "OK", new Dictionary<string, double>
            {
                ["container_count"] = 4,
                ["container_cpu:tdarr"] = 193,
                ["container_cpu:plex"] = 3,
            }) with { NotRecorded = new HashSet<string> { "container_cpu:plex" } };
            await provider.GetRequiredService<HealthMonitor>().RefreshAsync(connection);

            var live = provider.GetRequiredService<HealthMonitor>().State(connection.Id)!.Metrics;
            Assert.Equal(3, live["container_cpu:plex"]);
            Assert.Equal(["container_count", "container_cpu:tdarr"], written!.Keys.Order());
        }
        finally
        {
            TestHost.Teardown(provider, directory);
        }
    }

    [Fact]
    public async Task Metric_labels_name_each_container_and_the_busiest_one()
    {
        using var host = new FakeHost(4, TimeSpan.Zero);
        var connection = await ConnectionAsync(host);
        await TwoRoundsAsync(connection, host);

        Assert.Equal("c3: CPU", Get<Registry>().Metric(connection, "container_cpu:c3").Label);
        Assert.Equal("%", Get<Registry>().Metric(connection, "container_cpu:c3").Unit);
        Assert.Equal("Busiest container's CPU (c3)", Get<Registry>().Metric(connection, ContainerUsage.BusiestCpuMetric).Label);
        // A container nobody has measured in this process still gets the unit.
        Assert.Equal(" MB/s", Get<Registry>().Metric(connection, "container_read_mbs:never-seen").Unit);
    }

    [Fact]
    public async Task Self_healing_can_pause_the_busiest_container_but_never_labbytwo_itself()
    {
        using var host = new FakeHost(3, TimeSpan.Zero);
        var connection = await ConnectionAsync(host);
        await TwoRoundsAsync(connection, host);
        var actions = Get<RemediationActions>();

        var fix = new Remediation
        {
            Trigger = "rule:x", Kind = RemediationKind.PauseContainer, TargetConnectionId = connection.Id, Container = Remediation.BusiestByCpu,
        };
        Assert.Equal("pause the busiest container (by CPU)", await actions.DescribeAsync(fix, default));
        var plan = await actions.PrepareAsync(fix, default);
        Assert.Null(plan.Refused);
        Assert.Equal("pause c2", plan.Doing);
        Assert.True((await plan.Run!(default)).Ok);
        lock (host.Docker.Requests)
            Assert.Contains("POST /v1.41/containers/c2/pause", host.Docker.Requests);

        // The busiest is LabbyTwo's own: refused, not passed over for the next one.
        using var selfHost = new FakeHost(1, TimeSpan.Zero, self: Environment.MachineName);
        var selfConnection = await ConnectionAsync(selfHost);
        ContainerResourceSnapshots.Set(new ContainerResourceSnapshot(selfConnection.Id, 1, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1),
            TimeSpan.Zero, 1,
            [new ContainerReading("c0", Environment.MachineName, "running", 150, 1, 0, 0, 0, null, null, null, DateTimeOffset.UtcNow)],
            [], new Dictionary<string, double>(), new HashSet<string>(), null));
        var refused = await actions.PrepareAsync(fix with { Kind = RemediationKind.StopContainer, TargetConnectionId = selfConnection.Id }, default);
        Assert.Contains("the container LabbyTwo itself runs in", refused.Refused);
        Assert.Null(refused.Run);
    }

    [Fact]
    public async Task Stop_and_pause_remediations_are_stored_as_themselves()
    {
        var store = Get<RemediationStore>();
        await store.SaveAsync(new Remediation { Trigger = "rule:a", Kind = RemediationKind.StopContainer, TargetConnectionId = "d", Container = "tdarr" });
        await store.SaveAsync(new Remediation { Trigger = "rule:b", Kind = RemediationKind.PauseContainer, TargetConnectionId = "d", Container = Remediation.BusiestByDisk });
        var all = await store.AllAsync();
        Assert.Equal(RemediationKind.StopContainer, all.Single(r => r.Trigger == "rule:a").Kind);
        var pause = all.Single(r => r.Trigger == "rule:b");
        Assert.Equal(RemediationKind.PauseContainer, pause.Kind);
        Assert.True(pause.IsComplete);
        Assert.Equal(ContainerAction.Pause, pause.ContainerVerb);
    }

    [Fact]
    public async Task The_busiest_card_lists_the_top_by_cpu_and_by_disk()
    {
        using var host = new FakeHost(6, TimeSpan.Zero);
        var connection = await ConnectionAsync(host);
        await TwoRoundsAsync(connection, host);

        await Renderer.RenderAsync<BusiestContainersCard>(new Dictionary<string, object?>
        {
            ["Context"] = new WidgetContext(new Widget { Type = "busiest-containers", Settings = new SettingsBag { ["count"] = "2" } }, connection),
        });
        var html = WebUtility.HtmlDecode(await Renderer.HtmlAsync());
        Assert.Contains("data-busiest-cpu=\"c5\"", html);
        Assert.Contains("data-busiest-cpu=\"c4\"", html);
        Assert.DoesNotContain("data-busiest-cpu=\"c3\"", html);
        Assert.Contains("20%", html);
        Assert.Contains("data-busiest-disk=\"c5\"", html);
        Assert.Contains("500 kB/s", html);
    }

    [Fact]
    public async Task The_resources_view_sorts_by_any_column()
    {
        using var host = new FakeHost(3, TimeSpan.Zero);
        var connection = await ConnectionAsync(host, top: "1");
        await TwoRoundsAsync(connection, host);

        await Renderer.RenderAsync<ContainerResourceTable>(new Dictionary<string, object?> { ["Docker"] = connection });
        var html = WebUtility.HtmlDecode(await Renderer.HtmlAsync());
        Assert.True(html.IndexOf("data-row=\"c2\"", StringComparison.Ordinal) < html.IndexOf("data-row=\"c0\"", StringComparison.Ordinal));
        Assert.Contains("8.0%", html);
        Assert.Contains("for 3 requests", html);
        Assert.Contains("1 kept in history", html);

        await Renderer.ClickAsync("Container");
        html = WebUtility.HtmlDecode(await Renderer.HtmlAsync());
        Assert.True(html.IndexOf("data-row=\"c0\"", StringComparison.Ordinal) < html.IndexOf("data-row=\"c2\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// What a round costs: forty running containers, the size of a busy home NAS, against a
    /// Docker that answers in 5 ms. Asserted loosely — this is a ceiling, not a benchmark —
    /// and printed, which is where the figures in the pull request came from.
    /// </summary>
    [Fact]
    public async Task A_round_of_forty_containers_is_cheap()
    {
        using var host = new FakeHost(40, TimeSpan.FromMilliseconds(5));
        var connection = await ConnectionAsync(host);
        var poller = Get<ContainerResources>();
        await poller.RoundAsync(connection, TimeSpan.FromMinutes(1), default); // warm: JIT, connections

        var rounds = 5;
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        var requests = 0;
        for (var i = 0; i < rounds; i++)
        {
            DockerContainers.ForgetRecentStats(host.Docker.Endpoint);
            requests += (await poller.RoundAsync(connection, TimeSpan.FromMinutes(1), default))!.Requests;
        }
        clock.Stop();
        var spent = Process.GetCurrentProcess().TotalProcessorTime - cpu;

        Assert.Equal(40 * rounds, requests);
        // Whole-process CPU includes the fake Docker's side of every request, so this is generous.
        Assert.True(spent < TimeSpan.FromSeconds(rounds * 2), $"{spent.TotalMilliseconds / rounds:0} ms of CPU per round");
        Console.WriteLine($"Container stats: {clock.Elapsed.TotalMilliseconds / rounds:0} ms and {spent.TotalMilliseconds / rounds:0} ms of process CPU per round of 40");
    }
}
