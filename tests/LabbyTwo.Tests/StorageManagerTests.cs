#pragma warning disable BL0006 // The interactive test renderer is a Renderer, which is the whole point of it.
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LabbyTwo.Tests;

/// <summary>
/// The storage manager: a connection's own raw retention and "don't record" honoured by the
/// writes, the charts and the rollup; the survey's estimates close to the truth without
/// reading it all; what a change would free worked out before it is saved; and compaction
/// giving the space back, the second time a little at a time.
/// </summary>
public sealed class StorageManagerTests : IAsyncDisposable
{
    private const long Hour = 3600;
    private const long Day = 86400;

    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private InteractiveRenderer? _renderer;

    public StorageManagerTests()
    {
        _services = TestHost.Build(_directory, services =>
        {
            services.AddSingleton<Offload>();
            services.AddSingleton<StorageManager>();
        });
        Db.EnsureSchemaAsync().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (_renderer is not null)
            await _renderer.DisposeAsync();
        TestHost.Teardown(_services, _directory);
    }

    private Db Db => _services.GetRequiredService<Db>();

    private HistoryStore History(int rawDays = 30, int hourlyDays = 365) =>
        new(Db, Options.Create(new LabbyOptions
        {
            DatabasePath = Path.Combine(_directory, "test.db"),
            RetentionDays = rawDays,
            HourlyRetentionDays = hourlyDays,
        }));

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private async Task InsertAsync(string connectionId, string metric, IEnumerable<long> times, double value = 1)
    {
        await using var connection = await Db.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO samples (connection_id, metric, ts, value) VALUES ($c, $m, $t, $v)";
        cmd.Parameters.AddWithValue("$c", connectionId);
        cmd.Parameters.AddWithValue("$m", metric);
        cmd.Parameters.AddWithValue("$v", value);
        var ts = cmd.Parameters.Add("$t", SqliteType.Integer);
        foreach (var t in times)
        {
            ts.Value = t;
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    private async Task InsertHourlyAsync(string connectionId, string metric, IEnumerable<long> hours)
    {
        await using var connection = await Db.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO samples_hourly (connection_id, metric, hour_ts, min, max, avg, count, last_ts, last_value)
            VALUES ($c, $m, $h, 1, 1, 1, 120, $h + 3570, 1)
            """;
        cmd.Parameters.AddWithValue("$c", connectionId);
        cmd.Parameters.AddWithValue("$m", metric);
        var h = cmd.Parameters.Add("$h", SqliteType.Integer);
        foreach (var hour in hours)
        {
            h.Value = hour;
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = await Db.OpenAsync();
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static IEnumerable<long> Every(long from, long to, long step)
    {
        for (var t = from; t < to; t += step)
            yield return t;
    }

    // ---------- The policy ----------

    [Fact]
    public async Task The_policy_is_kept_and_read_back_by_a_fresh_store()
    {
        var policy = HistoryPolicy.Default.WithRawDays("weather", 7).WithRecord("nas", "fan_rpm", false);
        await History().SavePolicyAsync(policy);

        var read = await History().PolicyAsync();
        Assert.Equal(7, read.RawDaysFor("weather"));
        Assert.Null(read.RawDaysFor("nas"));
        Assert.False(read.Records("nas", "fan_rpm"));
        Assert.True(read.Records("nas", "cpu_percent"));
        Assert.True(read.SameAs(policy));

        // Back to the defaults is an empty table, not a row saying "the default".
        await History().SavePolicyAsync(read.WithRawDays("weather", null).WithRecord("nas", "fan_rpm", true));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM history_policy"));
    }

    [Fact]
    public void The_horizon_is_the_connections_own_and_the_current_hour_for_a_metric_not_recorded()
    {
        var now = new DateTimeOffset(2026, 9, 30, 12, 34, 56, TimeSpan.Zero);
        var hour = HistoryPolicy.FloorHour(now.ToUnixTimeSeconds());
        var global = TimeSpan.FromDays(30);
        var policy = HistoryPolicy.Default.WithRawDays("weather", 7).WithRecord("nas", "fan_rpm", false);

        Assert.Equal(hour - 30 * Day, policy.RawHorizon("nas", "cpu_percent", now, global));
        Assert.Equal(hour - 7 * Day, policy.RawHorizon("weather", "temp_c", now, global));
        Assert.Equal(hour, policy.RawHorizon("nas", "fan_rpm", now, global));

        // Out of range is clamped rather than trusted: nothing keeps less than a day.
        Assert.Equal(1, HistoryPolicy.Default.WithRawDays("x", 0).RawDaysFor("x"));
        Assert.Equal(HistoryPolicy.MaxRawDays, HistoryPolicy.Default.WithRawDays("x", 99999).RawDaysFor("x"));
    }

    [Fact]
    public async Task A_metric_not_recorded_still_reaches_live_listeners_but_not_the_table()
    {
        var history = History();
        await history.SavePolicyAsync(HistoryPolicy.Default.WithRecord("nas", "fan_rpm", false));
        IReadOnlyDictionary<string, double>? heard = null;
        history.Recorded += (_, metrics, _) => heard = metrics;

        await history.RecordAsync("nas", new Dictionary<string, double> { ["cpu_percent"] = 12, ["fan_rpm"] = 900 }, default);

        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM samples WHERE metric = 'cpu_percent'"));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM samples WHERE metric = 'fan_rpm'"));
        Assert.Equal(900, heard!["fan_rpm"]);
        Assert.Equal(1, history.Writes.Status(DateTimeOffset.Now).Succeeded);
    }

    [Fact]
    public async Task While_compacting_readings_are_skipped_and_counted_rather_than_queued()
    {
        var history = History();
        var heard = 0;
        history.Recorded += (_, _, _) => heard++;
        history.Writes.Compacting = true;

        await history.RecordAsync("nas", new Dictionary<string, double> { ["a"] = 1, ["b"] = 2 }, default);

        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM samples"));
        Assert.Equal(2, history.Writes.Status(DateTimeOffset.Now).Dropped);
        Assert.Equal(1, heard);
    }

    [Fact]
    public async Task The_rollup_uses_each_connections_retention_and_folds_a_metric_no_longer_recorded()
    {
        var now = Now;
        var hour = HistoryPolicy.FloorHour(now);
        await InsertAsync("weather", "temp_c", [now - 10 * Day, now - 3 * Day]);
        await InsertAsync("nas", "cpu_percent", [now - 10 * Day, now - 3 * Day]);
        await InsertAsync("nas", "fan_rpm", [hour - 2 * Hour, hour - Hour, now]);

        var history = History(rawDays: 30);
        await history.SavePolicyAsync(HistoryPolicy.Default.WithRawDays("weather", 7).WithRecord("nas", "fan_rpm", false));
        var result = await history.RollupAsync(DateTimeOffset.FromUnixTimeSeconds(now), null, default);

        // The weather station keeps a week: its reading ten days ago is now an hourly summary.
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM samples WHERE connection_id = 'weather'"));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM samples_hourly WHERE connection_id = 'weather'"));
        // The NAS keeps the global thirty days: nothing of it was touched.
        Assert.Equal(2, await CountAsync("SELECT COUNT(*) FROM samples WHERE metric = 'cpu_percent'"));
        // The fan is no longer recorded: everything before this hour is folded, the hour in progress stays.
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM samples WHERE metric = 'fan_rpm'"));
        Assert.Equal(2, await CountAsync("SELECT COUNT(*) FROM samples_hourly WHERE metric = 'fan_rpm'"));
        Assert.Equal(3, result.RowsDeleted);
        Assert.NotNull(history.LastRollup);
    }

    [Fact]
    public async Task A_chart_longer_than_the_connections_retention_is_drawn_by_the_hour()
    {
        // Three readings in one hour, a day ago, and no summaries anywhere. Under the global
        // thirty days a five-day chart is raw — three points. With the connection keeping
        // two days, five days is longer than its raw window, so one point for the hour.
        var start = HistoryPolicy.FloorHour(Now - Day);
        await InsertAsync("weather", "temp_c", [start + 60, start + 120, start + 180]);

        Assert.Equal(3, (await History().SamplesAsync("weather", "temp_c", TimeSpan.FromDays(5))).Count);

        var history = History();
        await history.SavePolicyAsync(HistoryPolicy.Default.WithRawDays("weather", 2));
        var point = Assert.Single(await history.SamplesAsync("weather", "temp_c", TimeSpan.FromDays(5)));
        Assert.Equal(3, point.Count);
    }

    // ---------- What it frees ----------

    [Fact]
    public async Task The_rollup_reports_the_space_it_freed_inside_the_file()
    {
        var old = Now - 40 * Day;
        await InsertAsync("nas", "cpu_percent", Every(old, old + 3 * Day, 30));
        await InsertAsync("nas", "mem_percent", Every(old, old + 3 * Day, 30));

        var result = await History().RollupAsync();

        Assert.Equal(2 * 3 * 2880, result.RowsDeleted);
        Assert.True(result.FreedBytes > 500_000, $"freed {result.FreedBytes}");
    }

    [Fact]
    public void What_a_shorter_or_longer_retention_would_do_is_worked_out_from_the_survey()
    {
        var now = HistoryPolicy.FloorHour(Now);
        // Thirty days of a 30-second series.
        var series = new SeriesSize("abcdef012345", "temp_c", 30 * 2880, now - 30 * Day, now - 30, 2880, 0, null, null);
        var rawBytes = StorageMath.RawRowBytes("abcdef012345", "temp_c");
        var hourlyBytes = StorageMath.HourlyRowBytes("abcdef012345", "temp_c");

        // Down to seven days: 23 of the 30 days go, and 23 days of hourly summaries replace them.
        var shorter = StorageMath.Effect(series, now - 30 * Day, now - 7 * Day, now - 365 * Day);
        Assert.InRange(shorter.RowsRemoved, 23 * 2880 - 5, 23 * 2880 + 5);
        Assert.Equal(23 * 24, shorter.HourlyRowsAdded);
        Assert.InRange(shorter.BytesFreed, (long)(23 * 2880 * rawBytes - 23 * 24 * hourlyBytes) - 2000,
            (long)(23 * 2880 * rawBytes - 23 * 24 * hourlyBytes) + 2000);
        Assert.Equal(0, shorter.BytesGrowth);

        // Up to sixty: nothing freed, and thirty more days of it once they have filled up.
        var longer = StorageMath.Effect(series, now - 30 * Day, now - 60 * Day, now - 365 * Day);
        Assert.Equal(0, longer.BytesFreed);
        var grows = (long)(2880 * 30 * rawBytes - 30 * 24 * hourlyBytes);
        Assert.InRange(longer.BytesGrowth, grows - 2000, grows + 2000);

        // No change, no effect.
        Assert.Equal(StorageEffect.None, StorageMath.Effect(series, now - 30 * Day, now - 30 * Day, now - 365 * Day));
    }

    [Fact]
    public void Row_sizes_follow_the_length_of_the_names()
    {
        // The measured figure the constants came from: 70 series with 21.8 bytes of names
        // took 94.6 bytes a raw row and 122.6 an hourly one.
        Assert.InRange(StorageMath.RawRowBytes(22), 92, 97);
        Assert.InRange(StorageMath.HourlyRowBytes(22), 118, 126);
        Assert.True(StorageMath.RawRowBytes("a-much-longer-connection-id", "a_long_metric_name") > StorageMath.RawRowBytes("id", "m"));
    }

    // ---------- The survey ----------

    [Fact]
    public async Task The_survey_estimates_each_series_close_to_its_real_count_and_the_exact_mode_is_exact()
    {
        var now = Now;
        // Three days at 30 s with a twelve-hour gap in the middle — the NAS was off.
        var start = now - 3 * Day;
        await InsertAsync("nas", "cpu_percent", Every(start, start + Day + 6 * Hour, 30).Concat(Every(start + Day + 18 * Hour, now, 30)));
        // Hourly, like a speed test, for twenty days.
        await InsertAsync("speed", "download_mbps", Every(now - 20 * Day, now, Hour));
        // Short: ten readings a minute apart.
        await InsertAsync("new", "latency_ms", Every(now - 600, now, 60));
        // A month of summaries for the NAS.
        await InsertHourlyAsync("nas", "cpu_percent", Every(HistoryPolicy.FloorHour(now - 33 * Day), HistoryPolicy.FloorHour(now - 3 * Day), Hour));

        var history = History();
        var estimate = await history.SurveyAsync(DateTimeOffset.FromUnixTimeSeconds(now), TimeSpan.Zero);
        var exact = await history.SurveyAsync(DateTimeOffset.FromUnixTimeSeconds(now), TimeSpan.Zero, exact: true);

        Assert.Equal(3, estimate.Series.Count);
        foreach (var real in exact.Series)
        {
            var truth = await CountAsync($"SELECT COUNT(*) FROM samples WHERE connection_id = '{real.ConnectionId}' AND metric = '{real.Metric}'");
            Assert.Equal(truth, real.RawRows);
        }

        var nas = estimate.Series.Single(s => s.ConnectionId == "nas");
        var nasTruth = exact.Series.Single(s => s.ConnectionId == "nas");
        Assert.InRange(nas.RawRows, nasTruth.RawRows * 0.85, nasTruth.RawRows * 1.15);
        Assert.InRange(nas.RawPerDay, 2800, 2960);
        Assert.InRange(nas.HourlyRows, 30 * 24 * 0.98, 30 * 24 * 1.02);

        var speed = estimate.Series.Single(s => s.ConnectionId == "speed");
        Assert.InRange(speed.RawRows, 480 * 0.95, 480 * 1.05);
        Assert.InRange(speed.RawPerDay, 20, 28);

        // Shorter than the six windows together: counted outright.
        Assert.Equal(10, estimate.Series.Single(s => s.ConnectionId == "new").RawRows);
        Assert.True(estimate.PagesRead > 0);
    }

    [Fact]
    public void Sample_windows_spread_over_the_series_and_one_window_covers_a_short_one()
    {
        var windows = StorageMath.SampleWindows(0, 100 * Hour - 1, 3 * Hour);
        Assert.Equal(StorageMath.Windows, windows.Count);
        Assert.Equal(0, windows[0].From);
        Assert.Equal(100 * Hour, windows[^1].To);
        Assert.All(windows, w => Assert.Equal(3 * Hour, w.To - w.From));

        Assert.Equal([(5L, 1006L)], StorageMath.SampleWindows(5, 1005, 3 * Hour));
        Assert.Equal(12, StorageMath.EstimateRows(5, 1005, 3 * Hour, [12]));
        // 6 windows of 3 h, each with 360 readings, over 100 h: 12,000.
        Assert.Equal(12000, StorageMath.EstimateRows(0, 100 * Hour - 1, 3 * Hour, [360, 360, 360, 360, 360, 360]));
    }

    // ---------- Growth ----------

    [Fact]
    public void Growth_is_the_last_day_against_the_median_of_the_days_before()
    {
        var now = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        var points = new List<(long, long)>();
        for (var h = 8 * 24; h >= 0; h--)
        {
            var at = now.AddHours(-h).ToUnixTimeSeconds();
            // 10 MB a day until the last day, which adds 100 MB.
            var used = h >= 24 ? (8 * 24 - h) * (10L << 20) / 24 : 7 * (10L << 20) + (24 - h) * (100L << 20) / 24;
            points.Add((at, used));
        }

        var growth = StorageManager.GrowthOf(points, now);
        Assert.InRange(growth.LastDay!.Value, (100L << 20) - (1 << 20), (100L << 20) + (1 << 20));
        Assert.InRange(growth.UsualPerDay!.Value, (10L << 20) - (1 << 20), (10L << 20) + (1 << 20));
        Assert.Equal(7, growth.DaysKnown);

        // A day and a bit: the last day is known, "usual" is not yet.
        var young = StorageManager.GrowthOf(points.TakeLast(30).ToList(), now);
        Assert.NotNull(young.LastDay);
        Assert.Null(young.UsualPerDay);

        // Nothing recent: nothing claimed.
        Assert.Null(StorageManager.GrowthOf(points, now.AddHours(5)).LastDay);
    }

    // ---------- Compaction ----------

    [Fact]
    public async Task Compacting_gives_the_space_back_and_can_make_the_next_one_incremental()
    {
        var old = Now - 40 * Day;
        await InsertAsync("nas", "cpu_percent", Every(old, old + 10 * Day, 30));
        var history = History();
        await history.RollupAsync();
        var before = await history.FactsAsync();
        Assert.True(before.FreeBytes > 1_000_000);
        Assert.False(before.Incremental);

        var full = await history.CompactAsync(makeIncremental: true);
        var after = await history.FactsAsync();
        Assert.True(full.Full);
        Assert.True(full.BytesAfter < full.BytesBefore - 1_000_000, $"{full.BytesBefore} -> {full.BytesAfter}");
        Assert.True(after.Incremental);
        Assert.Equal(0, after.FreePages);
        Assert.False(history.Writes.Compacting);

        // The second time there is no VACUUM: free pages are handed back a step at a time.
        await InsertAsync("nas", "mem_percent", Every(old, old + 10 * Day, 30));
        await history.RollupAsync();
        Assert.True((await history.FactsAsync()).FreePages > 0);
        var quick = await history.CompactAsync(makeIncremental: false);
        Assert.False(quick.Full);
        Assert.Equal(0, (await history.FactsAsync()).FreePages);
    }

    [Fact]
    public void A_full_compaction_is_refused_without_room_for_it()
    {
        var facts = new DatabaseFacts("x", 600L << 20, 0, 4096, (600L << 20) / 4096, 0, 0, 800L << 20, 10L << 30, 10L << 30);
        Assert.Contains("needs about 1.2 GB", facts.FullCompactionProblem());
        Assert.Null((facts with { DiskFree = 2L << 30 }).FullCompactionProblem());
        Assert.Contains("temporary", (facts with { DiskFree = 2L << 30, TempFree = 100L << 20 }).FullCompactionProblem());
    }

    // ---------- The page ----------

    [Fact]
    public async Task The_storage_page_lists_what_the_survey_found_by_connection_name()
    {
        var config = _services.GetRequiredService<ConfigStore>();
        var nas = new Connection { Provider = "http", Name = "The NAS" };
        await config.SaveConnectionAsync(nas);
        var now = Now;
        await InsertAsync(nas.Id, "latency_ms", Every(now - Day, now, 30));

        await _services.GetRequiredService<StorageManager>().SurveyNowAsync();

        _renderer = new InteractiveRenderer(_services);
        await _renderer.RenderAsync<LabbyTwo.Components.Pages.StoragePage>(new Dictionary<string, object?>());
        var html = System.Net.WebUtility.HtmlDecode(await _renderer.WaitForAsync(h =>
            h.Contains("The NAS", StringComparison.Ordinal) && h.Contains("free of", StringComparison.Ordinal)));

        Assert.Contains("The NAS", html);
        Assert.Contains("What takes the space", html);
        Assert.Contains("Compact now", html);
        Assert.Contains("1 metric", html);
        Assert.DoesNotContain("would be freed", html);
    }
}
