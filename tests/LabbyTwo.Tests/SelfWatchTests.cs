using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabbyTwo.Tests;

/// <summary>
/// LabbyTwo telling you about itself: each check's thresholds, both ways; the ledger that
/// makes it one notice when a problem starts and one when it ends, however much the number
/// wobbles in between; holds by quiet hours, maintenance and mute windows; turning a check
/// off; and a restart that neither repeats an old notice nor forgets to close it.
/// </summary>
public sealed class SelfWatchTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(30);

    private readonly string _directory = TestHost.TempDirectory();
    private readonly RecordingChannel _channel = new();
    private readonly ServiceProvider _services;

    private sealed class RecordingChannel : IAlertChannel
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

    public SelfWatchTests()
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        services.AddTestStorage(_directory);
        services.AddSingleton<IConnectionProvider>(_channel);
        services.AddSingleton<IEnumerable<IWidgetType>>([]);
        services.AddSingleton<IEnumerable<ITabKind>>([]);
        services.AddSingleton<Registry>();
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<AlertRuleStore>();
        services.AddSingleton<HistoryStore>();
        services.AddSingleton<HealthMonitor>();
        services.AddSingleton<AlertService>();
        services.AddSingleton<BackgroundJobRunner>();
        services.AddSingleton<StorageManager>();
        services.AddSingleton<SelfWatch>();
        _services = services.BuildServiceProvider();
        Get<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();
        Get<AlertService>().Zone = TimeZoneInfo.Utc;
        Get<SelfWatch>().Zone = TimeZoneInfo.Utc;
        Get<ConfigStore>().SaveConnectionAsync(new Connection { Provider = "recording", Name = "Phone" }).GetAwaiter().GetResult();
    }

    public void Dispose() => TestHost.Teardown(_services, _directory);

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    // ---------- Facts ----------

    private static MonitorStatus Monitor(DateTimeOffset now, IReadOnlyList<TimeSpan>? sweeps = null, DateTimeOffset? running = null) =>
        new(T0.AddHours(-1), Period, RestoreOutcome.Completed, null, null, null, 100,
            running, now.AddSeconds(-5), now, TimeSpan.FromSeconds(5), 4, null, [], [])
        {
            RecentSweeps = sweeps ?? [.. Enumerable.Repeat(TimeSpan.FromSeconds(3), 10)],
        };

    private static SelfFacts Facts(
        DateTimeOffset now,
        MonitorStatus? monitor = null,
        IReadOnlyList<JobRun>? jobs = null,
        WriteHealthStatus? writes = null,
        GrowthFacts? growth = null,
        DiskFacts? disk = null,
        SlowQueryFacts? slow = null) =>
        new(now, TimeSpan.FromHours(1), monitor ?? Monitor(now), jobs ?? [], writes ?? WriteHealthStatus.Unknown, growth, disk, slow,
            new Dictionary<string, string> { ["plex"] = "Plex" });

    private static SelfReading Reading(SelfFacts facts, string key, params string[] firing) =>
        SelfWatchRules.Evaluate(facts, firing.ToHashSet()).Single(r => r.Key == key);

    private static IReadOnlyList<TimeSpan> Sweeps(int late) =>
        [.. Enumerable.Repeat(TimeSpan.FromSeconds(45), late), .. Enumerable.Repeat(TimeSpan.FromSeconds(4), 10 - late)];

    private static readonly DiskFacts Roomy = new(40L << 30, 100L << 30, 500L << 20);

    // ---------- The rules ----------

    [Fact]
    public void Sweeps_are_late_at_five_of_ten_and_stay_late_down_to_three()
    {
        Assert.Equal(SelfState.Holds, Reading(Facts(T0, Monitor(T0, Sweeps(5))), "sweeps-late").State);
        Assert.Equal(SelfState.Clear, Reading(Facts(T0, Monitor(T0, Sweeps(4))), "sweeps-late").State);
        Assert.Equal(SelfState.Holds, Reading(Facts(T0, Monitor(T0, Sweeps(3))), "sweeps-late", "sweeps-late").State);
        Assert.Equal(SelfState.Clear, Reading(Facts(T0, Monitor(T0, Sweeps(2))), "sweeps-late", "sweeps-late").State);
        // Nine sweeps is not enough to say — just after a restart.
        Assert.Equal(SelfState.Unknown, Reading(Facts(T0, Monitor(T0, [.. Sweeps(9).Take(9)])), "sweeps-late").State);

        var late = Reading(Facts(T0, Monitor(T0, Sweeps(6))), "sweeps-late");
        Assert.Contains("6 of the last 10 rounds of checks took longer than the 30 s", late.Body);
        Assert.Contains("the slowest took 45 s", late.Body);
    }

    [Fact]
    public void A_stuck_sweep_says_what_it_is_waiting_on_and_a_stopped_monitor_is_stuck_too()
    {
        var stuck = Monitor(T0) with
        {
            CurrentSweepStarted = T0.AddSeconds(-75),
            InFlight = [new ProbeInFlight("plex", T0.AddSeconds(-75))],
        };
        var reading = Reading(Facts(T0, stuck), "sweep-stuck");
        Assert.Equal(SelfState.Holds, reading.State);
        Assert.Contains("running for 75 s", reading.Body);
        Assert.Contains("still waiting on Plex", reading.Body);

        var stopped = Monitor(T0) with { LastSweepFinished = T0.AddMinutes(-3) };
        Assert.Equal("LabbyTwo's monitor has stopped", Reading(Facts(T0, stopped), "sweep-stuck").Title);

        Assert.Equal(SelfState.Clear, Reading(Facts(T0), "sweep-stuck").State);
    }

    [Fact]
    public void A_job_is_failing_after_three_runs_in_a_row_or_two_for_an_hourly_one()
    {
        JobRun Run(int failures, TimeSpan interval, bool ok = false) =>
            new("tidy", T0, TimeSpan.FromSeconds(1), ok, ok ? "OK" : "disk I/O error") { Interval = interval, ConsecutiveFailures = failures };

        Assert.Equal(SelfState.Clear, Reading(Facts(T0, jobs: [Run(2, TimeSpan.FromMinutes(5))]), "job:tidy").State);
        Assert.Equal(SelfState.Holds, Reading(Facts(T0, jobs: [Run(3, TimeSpan.FromMinutes(5))]), "job:tidy").State);
        Assert.Equal(SelfState.Holds, Reading(Facts(T0, jobs: [Run(2, TimeSpan.FromHours(1))]), "job:tidy").State);
        Assert.Equal(SelfState.Clear, Reading(Facts(T0, jobs: [Run(0, TimeSpan.FromMinutes(5), ok: true)]), "job:tidy", "job:tidy").State);
        Assert.Equal(SelfState.Unknown, Reading(Facts(T0, jobs: [new JobRun("tidy", null, TimeSpan.Zero, true, "Not run yet")]), "job:tidy").State);

        Assert.Contains("failed 3 times in a row", Reading(Facts(T0, jobs: [Run(3, TimeSpan.FromMinutes(5))]), "job:tidy").Body);
        Assert.Contains("disk I/O error", Reading(Facts(T0, jobs: [Run(3, TimeSpan.FromMinutes(5))]), "job:tidy").Body);
    }

    [Fact]
    public void Failed_writes_in_three_of_ten_minutes_start_it_and_ten_clean_minutes_end_it()
    {
        WriteHealthStatus Writes(int minutes) => new(minutes, minutes * 20, 40, "database is locked", T0, T0, 0, false);

        Assert.Equal(SelfState.Clear, Reading(Facts(T0, writes: Writes(2)), "db-writes").State);
        Assert.Equal(SelfState.Holds, Reading(Facts(T0, writes: Writes(3)), "db-writes").State);
        Assert.Equal(SelfState.Holds, Reading(Facts(T0, writes: Writes(1)), "db-writes", "db-writes").State);
        Assert.Equal(SelfState.Clear, Reading(Facts(T0, writes: Writes(0)), "db-writes", "db-writes").State);
        // A compaction holds the database on purpose, and the storage page said so.
        Assert.Equal(SelfState.Unknown, Reading(Facts(T0, writes: Writes(5) with { Compacting = true }), "db-writes").State);

        Assert.Contains("\"database is locked\"", Reading(Facts(T0, writes: Writes(3)), "db-writes").Body);
    }

    [Fact]
    public void The_disk_is_nearly_full_below_a_gigabyte_or_five_percent_and_has_room_again_above_both_with_margin()
    {
        DiskFacts Free(double gb, double totalGb = 100) => new((long)(gb * (1L << 30)), (long)(totalGb * (1L << 30)), 500L << 20);

        Assert.Equal(SelfState.Holds, Reading(Facts(T0, disk: Free(0.9, 10)), "disk").State);
        Assert.Equal(SelfState.Holds, Reading(Facts(T0, disk: Free(4)), "disk").State);
        Assert.Equal(SelfState.Clear, Reading(Facts(T0, disk: Free(6)), "disk").State);
        // Once reported, 6% is not yet enough to call it over.
        Assert.Equal(SelfState.Holds, Reading(Facts(T0, disk: Free(6)), "disk", "disk").State);
        Assert.Equal(SelfState.Clear, Reading(Facts(T0, disk: Free(8)), "disk", "disk").State);
        Assert.Equal(SelfState.Unknown, Reading(Facts(T0), "disk").State);

        Assert.Contains("4 GB free of 100 GB (4%)", Reading(Facts(T0, disk: Free(4)), "disk").Body);
    }

    [Fact]
    public void Growth_is_news_at_three_times_the_usual_day_and_never_under_fifty_megabytes()
    {
        GrowthFacts Grew(long mb, long usualMb) => new(mb << 20, usualMb << 20, 7);

        Assert.Equal(SelfState.Holds, Reading(Facts(T0, growth: Grew(200, 20), disk: Roomy), "db-growth").State);
        Assert.Equal(SelfState.Clear, Reading(Facts(T0, growth: Grew(50, 20)), "db-growth").State);
        // Barely grows usually, so 40 MB is eight times usual — and still not news.
        Assert.Equal(SelfState.Clear, Reading(Facts(T0, growth: Grew(40, 1)), "db-growth").State);
        Assert.Equal(SelfState.Holds, Reading(Facts(T0, growth: Grew(45, 20)), "db-growth", "db-growth").State);
        Assert.Equal(SelfState.Unknown, Reading(Facts(T0, growth: new GrowthFacts(200L << 20, null, 1)), "db-growth").State);

        var body = Reading(Facts(T0, growth: Grew(200, 20), disk: Roomy), "db-growth").Body;
        Assert.Contains("200 MB in the last day", body);
        Assert.Contains("about 20 MB a day", body);
        Assert.Contains("lasts about 204 days", body);
    }

    [Fact]
    public void The_slow_query_budget_is_fifty_statements_or_a_minute_in_ten_minutes_and_needs_the_log()
    {
        SlowQueryFacts Slow(int count, int seconds) =>
            new(count, TimeSpan.FromSeconds(seconds), TimeSpan.FromMilliseconds(200), TimeSpan.FromMinutes(10));

        Assert.Equal(SelfState.Unknown, Reading(Facts(T0), "slow-queries").State);
        Assert.Equal(SelfState.Clear, Reading(Facts(T0, slow: Slow(10, 5)), "slow-queries").State);
        Assert.Equal(SelfState.Holds, Reading(Facts(T0, slow: Slow(50, 12)), "slow-queries").State);
        Assert.Equal(SelfState.Holds, Reading(Facts(T0, slow: Slow(8, 70)), "slow-queries").State);
        Assert.Equal(SelfState.Holds, Reading(Facts(T0, slow: Slow(30, 5)), "slow-queries", "slow-queries").State);
        Assert.Equal(SelfState.Clear, Reading(Facts(T0, slow: Slow(20, 5)), "slow-queries", "slow-queries").State);
    }

    // ---------- The ledger ----------

    private static SelfReading Disk(SelfState state) =>
        new("disk", SelfCheck.DiskSpace, state, SystemHealth.Level.Bad, "Disk nearly full", "3% free", "Disk has room", "20% free");

    private static SelfReading Late(SelfState state) =>
        new("sweeps-late", SelfCheck.SweepsLate, state, SystemHealth.Level.Warn, "Late", "6 of 10", "On time", "1 of 10");

    [Fact]
    public void An_alert_starts_only_once_it_has_held_for_its_sustain_time()
    {
        var ledger = new SelfWatchLedger();
        ledger.Step(T0, [Disk(SelfState.Holds)], _ => true);
        ledger.Step(T0.AddMinutes(4), [Disk(SelfState.Holds)], _ => true);
        Assert.Empty(ledger.OwedStarts);

        ledger.Step(T0.AddMinutes(5), [Disk(SelfState.Holds)], _ => true);
        Assert.Equal("disk", Assert.Single(ledger.OwedStarts).Key);

        // Suspected and gone again before then is forgotten, with nothing owed.
        var brief = new SelfWatchLedger();
        brief.Step(T0, [Disk(SelfState.Holds)], _ => true);
        brief.Step(T0.AddMinutes(2), [Disk(SelfState.Clear)], _ => true);
        Assert.Empty(brief.Alerts);
        Assert.Empty(brief.OwedClears);
    }

    [Fact]
    public void A_number_hovering_at_the_threshold_is_one_alert_and_one_all_clear()
    {
        var ledger = new SelfWatchLedger();
        ledger.Step(T0, [Late(SelfState.Holds)], _ => true);
        ledger.Delivered("sweeps-late");

        // Clear, late again two minutes later, clear again: still the one alert.
        ledger.Step(T0.AddMinutes(1), [Late(SelfState.Clear)], _ => true);
        ledger.Step(T0.AddMinutes(3), [Late(SelfState.Holds)], _ => true);
        ledger.Step(T0.AddMinutes(4), [Late(SelfState.Clear)], _ => true);
        ledger.Step(T0.AddMinutes(8), [Late(SelfState.Clear)], _ => true);
        Assert.Empty(ledger.OwedStarts);
        Assert.Empty(ledger.OwedClears);

        // Five minutes clear: over.
        ledger.Step(T0.AddMinutes(9), [Late(SelfState.Clear)], _ => true);
        var clear = Assert.Single(ledger.OwedClears);
        Assert.Equal("On time", clear.Title);
        Assert.Contains("It lasted 9m", clear.Body);
        Assert.Empty(ledger.Alerts);
    }

    [Fact]
    public void An_alert_nobody_was_told_about_ends_without_a_word_and_unknown_changes_nothing()
    {
        var ledger = new SelfWatchLedger();
        ledger.Step(T0, [Late(SelfState.Holds)], _ => true);
        ledger.Step(T0.AddMinutes(1), [Late(SelfState.Unknown)], _ => true);
        Assert.Single(ledger.OwedStarts);

        ledger.Step(T0.AddMinutes(2), [Late(SelfState.Clear)], _ => true);
        ledger.Step(T0.AddMinutes(8), [Late(SelfState.Clear)], _ => true);
        Assert.Empty(ledger.Alerts);
        Assert.Empty(ledger.OwedClears);
    }

    [Fact]
    public void Back_before_its_end_was_announced_it_carries_on_as_already_announced()
    {
        var ledger = new SelfWatchLedger();
        ledger.Step(T0, [Late(SelfState.Holds)], _ => true);
        ledger.Delivered("sweeps-late");
        ledger.Step(T0.AddMinutes(1), [Late(SelfState.Clear)], _ => true);
        ledger.Step(T0.AddMinutes(7), [Late(SelfState.Clear)], _ => true);
        Assert.Single(ledger.OwedClears);

        // Quiet hours held that all-clear, and now it is late again.
        ledger.Step(T0.AddMinutes(8), [Late(SelfState.Holds)], _ => true);
        Assert.Empty(ledger.OwedClears);
        Assert.Empty(ledger.OwedStarts);
        Assert.True(Assert.Single(ledger.Alerts).Notified);
    }

    [Fact]
    public void Turning_a_check_off_forgets_it_silently()
    {
        var ledger = new SelfWatchLedger();
        ledger.Step(T0, [Late(SelfState.Holds)], _ => true);
        ledger.Delivered("sweeps-late");
        ledger.Step(T0.AddMinutes(1), [Late(SelfState.Holds)], check => check != SelfCheck.SweepsLate);
        Assert.Empty(ledger.Alerts);
        Assert.Empty(ledger.OwedClears);
    }

    [Fact]
    public void Across_a_restart_an_announced_alert_is_neither_repeated_nor_forgotten()
    {
        var before = new SelfWatchLedger();
        before.Step(T0, [Late(SelfState.Holds)], _ => true);
        before.Delivered("sweeps-late");
        var saved = before.Save();

        var after = new SelfWatchLedger();
        after.Load(saved);
        // Straight after a restart there are not ten sweeps to judge.
        after.Step(T0.AddMinutes(10), [Late(SelfState.Unknown)], _ => true);
        Assert.Empty(after.OwedStarts);
        Assert.Single(after.Alerts);

        after.Step(T0.AddMinutes(20), [Late(SelfState.Clear)], _ => true);
        after.Step(T0.AddMinutes(26), [Late(SelfState.Clear)], _ => true);
        Assert.Single(after.OwedClears);

        // Nonsense is a fresh start, not a crash.
        after.Load("{not json");
        Assert.Empty(after.Alerts);
    }

    // ---------- The service ----------

    [Fact]
    public async Task Held_by_quiet_hours_then_sent_once_when_they_end_and_once_when_it_clears()
    {
        await Get<AppSettingsStore>().SaveAsync(new Dictionary<string, string>
        {
            [AlertPolicy.FromKey] = "11:00",
            [AlertPolicy.ToKey] = "13:00",
            [AlertPolicy.ModeKey] = AlertPolicy.DownOnly,
        });
        var watch = Get<SelfWatch>();
        var full = new DiskFacts(500L << 20, 100L << 30, 400L << 20);

        await watch.PassAsync(T0, Facts(T0, disk: full), default);
        await watch.PassAsync(T0.AddMinutes(6), Facts(T0.AddMinutes(6), disk: full), default);
        Assert.Empty(_channel.Sent);
        Assert.True(Assert.Single(watch.Current).Firing);

        // 13:01: quiet hours are over and it is still nearly full.
        await watch.PassAsync(T0.AddMinutes(61), Facts(T0.AddMinutes(61), disk: full), default);
        await watch.PassAsync(T0.AddMinutes(62), Facts(T0.AddMinutes(62), disk: full), default);
        var sent = Assert.Single(_channel.Sent);
        Assert.Equal(AlertLevel.Down, sent.Level);
        Assert.Equal("LabbyTwo's disk is nearly full", sent.Title);
        Assert.Equal("labbytwo:disk", sent.Tag);

        // Space freed; ten minutes later it is over.
        await watch.PassAsync(T0.AddMinutes(70), Facts(T0.AddMinutes(70), disk: Roomy), default);
        await watch.PassAsync(T0.AddMinutes(80), Facts(T0.AddMinutes(80), disk: Roomy), default);
        Assert.Equal(2, _channel.Sent.Count);
        Assert.Equal(AlertLevel.Up, _channel.Sent[1].Level);
        Assert.Equal("LabbyTwo's disk has room again", _channel.Sent[1].Title);
    }

    [Fact]
    public async Task Maintenance_and_a_mute_window_over_everything_hold_it()
    {
        var watch = Get<SelfWatch>();
        var settings = Get<AppSettingsStore>();
        await settings.SaveAsync(Maintenance.Key, Maintenance.Indefinite);
        Assert.Equal("all alerts are silenced", await watch.HeldAsync(T0, await settings.AllAsync(), default));

        await settings.SaveAsync(Maintenance.Key, Maintenance.Cleared);
        await Get<MuteWindowStore>().SaveAsync(new MuteWindow
        {
            Name = "Scrub", Days = MuteWindow.Week, Start = new TimeOnly(11, 0), End = new TimeOnly(14, 0),
        });
        Assert.Equal("muted by Scrub", await watch.HeldAsync(T0, await settings.AllAsync(), default));
        Assert.Null(await watch.HeldAsync(T0.AddHours(3), await settings.AllAsync(), default));
    }

    [Fact]
    public async Task A_check_turned_off_in_settings_says_nothing()
    {
        var watch = Get<SelfWatch>();
        await Get<AppSettingsStore>().SaveAsync(SelfWatch.OffKey, "disk,slow-queries");
        var off = await watch.DisabledAsync();
        Assert.Equal([SelfCheck.DiskSpace, SelfCheck.SlowQueries], off.OrderBy(c => c));

        var full = new DiskFacts(500L << 20, 100L << 30, 400L << 20);
        await watch.PassAsync(T0, Facts(T0, disk: full), default);
        await watch.PassAsync(T0.AddMinutes(10), Facts(T0.AddMinutes(10), disk: full), default);
        Assert.Empty(_channel.Sent);
        Assert.Empty(watch.Current);
    }

    [Fact]
    public async Task What_was_announced_is_kept_so_a_new_process_sends_the_all_clear()
    {
        var writes = new WriteHealthStatus(4, 80, 10, "database is locked", T0, T0, 0, false);
        await Get<SelfWatch>().PassAsync(T0, Facts(T0, writes: writes), default);
        Assert.Equal("LabbyTwo can't save readings", Assert.Single(_channel.Sent).Title);

        // A new process: the same database, a new watcher with nothing in memory.
        var next = ActivatorUtilities.CreateInstance<SelfWatch>(_services);
        next.Zone = TimeZoneInfo.Utc;
        await next.PassAsync(T0.AddMinutes(20), Facts(T0.AddMinutes(20), writes: WriteHealthStatus.Unknown), default);
        Assert.Equal(2, _channel.Sent.Count);
        Assert.Equal("LabbyTwo is saving readings again", _channel.Sent[1].Title);
    }

    // ---------- The facts it keeps ----------

    [Fact]
    public void Write_health_counts_the_minutes_with_a_failure_in_the_last_ten()
    {
        var health = new WriteHealth();
        health.Failed(T0.AddMinutes(-15), new SqliteException("database is locked", 5));
        health.Failed(T0.AddMinutes(-3), new SqliteException("database is locked", 5));
        health.Failed(T0.AddMinutes(-3).AddSeconds(10), new SqliteException("database is locked", 5));
        health.Failed(T0.AddMinutes(-1), new SqliteException("database is locked", 5));
        health.Succeeded(T0);

        var status = health.Status(T0);
        Assert.Equal(2, status.FailedMinutes);
        Assert.Equal(3, status.Failures);
        Assert.Equal(1, status.Succeeded);
        Assert.Equal("database is locked", status.LastError);
    }

    [Fact]
    public void The_query_log_counts_slow_statements_for_the_budget()
    {
        var log = new QueryLog(TimeSpan.FromMilliseconds(1), TimeSpan.Zero, NullLogger.Instance);
        var path = Path.Combine(_directory, "slow.db");
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        log.Attach(connection);
        var cmd = connection.CreateCommand();
        cmd.CommandText = "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 3000000) SELECT SUM(i) FROM n";
        cmd.ExecuteScalar();

        var (count, total) = log.SlowSince(DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.True(count >= 1);
        Assert.True(total > TimeSpan.Zero);
        Assert.Equal(0, log.SlowSince(DateTimeOffset.UtcNow.AddMinutes(1)).Count);
    }
}
