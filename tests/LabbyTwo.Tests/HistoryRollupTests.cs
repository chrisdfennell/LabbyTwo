using LabbyTwo.Core;
using LabbyTwo.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace LabbyTwo.Tests;

/// <summary>
/// Raw samples folded into hourly summaries past the raw retention, and every read that
/// used to see only raw samples seeing both. What matters: nothing is lost or counted
/// twice, the rollup can be run again or interrupted without changing the answer, and a
/// chart or a card asking for a long window still gets a continuous series and a real
/// latest value.
/// </summary>
public sealed class HistoryRollupTests : IDisposable
{
    private const long Hour = 3600;

    /// <summary>A fixed "now" for the tests that only exercise the rollup, so hour boundaries are known.</summary>
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 34, 56, TimeSpan.Zero);

    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;

    public HistoryRollupTests() => _services = TestHost.ReadyHost(_directory);

    public void Dispose() => TestHost.Teardown(_services, _directory);

    private Db Db => _services.GetRequiredService<Db>();

    /// <summary>The store with the defaults — seven days raw, a year of summaries — unless told otherwise.</summary>
    private HistoryStore History(int rawDays = 7, int hourlyDays = 365) =>
        new(Db, Options.Create(new LabbyOptions
        {
            DatabasePath = Path.Combine(_directory, "test.db"),
            RetentionDays = rawDays,
            HourlyRetentionDays = hourlyDays,
        }));

    private async Task InsertAsync(string connectionId, string metric, params (long Ts, double Value)[] rows)
    {
        await using var connection = await Db.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO samples (connection_id, metric, ts, value) VALUES ($c, $m, $t, $v)";
        cmd.Parameters.AddWithValue("$c", connectionId);
        cmd.Parameters.AddWithValue("$m", metric);
        var ts = cmd.Parameters.Add("$t", SqliteType.Integer);
        var value = cmd.Parameters.Add("$v", SqliteType.Real);
        foreach (var row in rows)
        {
            ts.Value = row.Ts;
            value.Value = row.Value;
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    private sealed record Summary(string ConnectionId, string Metric, long HourTs, double Min, double Max, double Avg, long Count, long LastTs, double LastValue);

    private async Task<List<Summary>> SummariesAsync()
    {
        await using var connection = await Db.OpenAsync();
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT connection_id, metric, hour_ts, min, max, avg, count, last_ts, last_value
            FROM samples_hourly ORDER BY connection_id, metric, hour_ts
            """;
        var list = new List<Summary>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new Summary(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetDouble(3),
                reader.GetDouble(4), reader.GetDouble(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetDouble(8)));
        }
        return list;
    }

    private async Task<List<long>> RawTimestampsAsync(string connectionId, string metric)
    {
        await using var connection = await Db.OpenAsync();
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ts FROM samples WHERE connection_id = $c AND metric = $m ORDER BY ts";
        cmd.Parameters.AddWithValue("$c", connectionId);
        cmd.Parameters.AddWithValue("$m", metric);
        var list = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add(reader.GetInt64(0));
        return list;
    }

    private static long HourOf(DateTimeOffset at) => at.ToUnixTimeSeconds() / Hour * Hour;

    // ---------- The rollup ----------

    [Fact]
    public async Task EachCompleteHourBecomesOneRowWithItsMinMaxAverageCountAndLastReading()
    {
        var hour = HourOf(Now.AddDays(-10));
        await InsertAsync("nas", "temp_c", (hour, 1), (hour + 600, 2), (hour + 1200, 3), (hour + 3000, 6));
        await InsertAsync("nas", "temp_c", (hour + Hour + 5, 10), (hour + Hour + 3599, 20));

        await History().RollupAsync(Now, null, default);

        var rows = await SummariesAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(new Summary("nas", "temp_c", hour, 1, 6, 3, 4, hour + 3000, 6), rows[0]);
        Assert.Equal(new Summary("nas", "temp_c", hour + Hour, 10, 20, 15, 2, hour + Hour + 3599, 20), rows[1]);
        Assert.Empty(await RawTimestampsAsync("nas", "temp_c"));
    }

    [Fact]
    public async Task TwoReadingsInTheSameSecondKeepTheOneWrittenLastAsTheHoursLastValue()
    {
        // The rule LatestReadingsAsync uses: timestamps are whole seconds, so "newest"
        // means most recently inserted.
        var hour = HourOf(Now.AddDays(-10));
        await InsertAsync("nas", "temp_c", (hour + 100, 5), (hour + 100, 7));

        await History().RollupAsync(Now, null, default);

        Assert.Equal(7, Assert.Single(await SummariesAsync()).LastValue);
    }

    [Fact]
    public async Task OnlyHoursThatEndedBeforeTheRawWindowAreTouched()
    {
        // Raw window seven days: the cut-off is the start of the hour seven days ago, so
        // that hour stays raw in full even though part of it is older than seven days.
        var cutoff = HourOf(Now.AddDays(-7));
        var current = HourOf(Now);
        await InsertAsync("nas", "temp_c",
            (cutoff - 1, 1),                       // last second of a complete, old hour
            (cutoff, 2), (cutoff + 1800, 3),       // the hour the window begins in
            (current, 4), (Now.ToUnixTimeSeconds(), 5)); // the hour being recorded now

        await History().RollupAsync(Now, null, default);

        var summary = Assert.Single(await SummariesAsync());
        Assert.Equal(cutoff - Hour, summary.HourTs);
        Assert.Equal(1, summary.Count);
        Assert.Equal([cutoff, cutoff + 1800, current, Now.ToUnixTimeSeconds()], await RawTimestampsAsync("nas", "temp_c"));
    }

    [Fact]
    public async Task RunningItAgainChangesNothing()
    {
        var start = HourOf(Now.AddDays(-9));
        var rows = Enumerable.Range(0, 9 * 24 * 12).Select(i => (start + i * 300L, (double)(i % 17))).ToArray();
        await InsertAsync("nas", "temp_c", rows);
        await InsertAsync("nas", "fan_rpm", rows);
        await InsertAsync("router", "latency_ms", rows);

        var history = History();
        var first = await history.RollupAsync(Now, null, default);
        var summaries = await SummariesAsync();
        var raw = await RawTimestampsAsync("nas", "temp_c");

        var second = await history.RollupAsync(Now, null, default);

        Assert.True(first.Batches > 0);
        Assert.Equal(0, second.Batches);
        Assert.Equal(summaries, await SummariesAsync());
        Assert.Equal(raw, await RawTimestampsAsync("nas", "temp_c"));
    }

    [Fact]
    public async Task EveryReadingIsEitherStillRawOrCountedInExactlyOneSummary()
    {
        var start = Now.AddDays(-9).ToUnixTimeSeconds();
        var rows = Enumerable.Range(0, 9 * 24 * 60).Select(i => (start + i * 60L, (double)i)).ToArray();
        await InsertAsync("nas", "temp_c", rows);

        var result = await History().RollupAsync(Now, null, default);

        var summaries = await SummariesAsync();
        var raw = await RawTimestampsAsync("nas", "temp_c");
        Assert.Equal(rows.Length, raw.Count + summaries.Sum(s => s.Count));
        Assert.Equal(summaries.Sum(s => s.Count), result.RowsDeleted);
        Assert.Equal(summaries.Count, result.HoursWritten);
        // And the average of the whole is unchanged by folding part of it.
        var total = raw.Sum(ts => (double)((ts - start) / 60)) + summaries.Sum(s => s.Avg * s.Count);
        Assert.Equal(rows.Sum(r => r.Item2), total, precision: 3);
    }

    [Fact]
    public async Task ABudgetStopsBetweenBatchesAndTheNextRunCarriesOn()
    {
        var start = HourOf(Now.AddDays(-9));
        var rows = Enumerable.Range(0, 9 * 24).Select(i => (start + i * Hour, (double)i)).ToArray();
        await InsertAsync("nas", "temp_c", rows);
        var history = History();

        var partial = await history.RollupAsync(Now, TimeSpan.Zero, default);
        Assert.False(partial.Finished);

        // However it was split, the end state is the same as doing it in one go.
        var rest = await history.RollupAsync(Now, null, default);
        Assert.True(rest.Finished);
        var summaries = await SummariesAsync();
        Assert.Equal(rows.Length, summaries.Sum(s => s.Count) + (await RawTimestampsAsync("nas", "temp_c")).Count);
        Assert.Equal(summaries.Select(s => s.HourTs).Distinct().Count(), summaries.Count);
    }

    [Fact]
    public async Task RawRowsReappearingForASummarisedHourAreMergedIntoItNotReplacingIt()
    {
        var hour = HourOf(Now.AddDays(-10));
        await InsertAsync("nas", "temp_c", (hour + 10, 4), (hour + 20, 8));
        var history = History();
        await history.RollupAsync(Now, null, default);

        // As if a backup had put some of that hour's rows back.
        await InsertAsync("nas", "temp_c", (hour + 30, 2), (hour + 5, 30));
        await history.RollupAsync(Now, null, default);

        var summary = Assert.Single(await SummariesAsync());
        Assert.Equal(2, summary.Min);
        Assert.Equal(30, summary.Max);
        Assert.Equal(4, summary.Count);
        Assert.Equal(11, summary.Avg, precision: 9);
        Assert.Equal(hour + 30, summary.LastTs);
        Assert.Equal(2, summary.LastValue);
    }

    [Fact]
    public async Task TheBackgroundJobIsDiscoveredAndDoesTheRollup()
    {
        // Discovered like any plugin's job, so the runner schedules it without anybody
        // registering it — and it is not the monitor's sweep that waits for it.
        var catalog = new ServiceCollection().AddModules(typeof(Registry).Assembly,
            Path.Combine(_directory, "no-plugins"), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        Assert.Contains(catalog.Modules, module => module.Jobs.Contains(nameof(Services.HistoryRollupJob)));

        var job = new Services.HistoryRollupJob(_services.GetRequiredService<HistoryStore>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Services.HistoryRollupJob>.Instance);
        await InsertAsync("nas", "temp_c", (HourOf(DateTimeOffset.UtcNow.AddDays(-10)) + 60, 5));

        await job.RunAsync(default);

        Assert.Equal(5, Assert.Single(await SummariesAsync()).Avg);
        Assert.Empty(await RawTimestampsAsync("nas", "temp_c"));
    }

    // ---------- Retention ----------

    [Fact]
    public async Task SummariesPastTheirRetentionArePrunedAndNewerOnesKept()
    {
        var history = History(rawDays: 7, hourlyDays: 30);
        var old = HourOf(Now.AddDays(-31));
        var kept = HourOf(Now.AddDays(-29));
        await InsertAsync("nas", "temp_c", (old + 60, 1), (kept + 60, 2));

        // The rollup itself does not write a summary it would have to prune: the row
        // older than both retentions is deleted without one.
        await history.RollupAsync(Now, null, default);
        Assert.Equal([kept], (await SummariesAsync()).Select(s => s.HourTs));
        Assert.Empty(await RawTimestampsAsync("nas", "temp_c"));

        // Time passes, and the kept one expires too.
        await history.PruneAsync(Now.AddDays(2), default);
        Assert.Empty(await SummariesAsync());
    }

    [Fact]
    public void RetentionsAreClampedSoNothingExpiresBeforeWhatItSummarises()
    {
        var zero = new LabbyOptions { RetentionDays = 0, HourlyRetentionDays = 0 };
        Assert.Equal(TimeSpan.FromDays(1), zero.RawRetention);
        Assert.Equal(TimeSpan.FromDays(1), zero.HourlyRetention);

        var longRaw = new LabbyOptions { RetentionDays = 400, HourlyRetentionDays = 365 };
        Assert.Equal(TimeSpan.FromDays(400), longRaw.HourlyRetention);

        var defaults = new LabbyOptions();
        Assert.Equal(TimeSpan.FromDays(7), defaults.RawRetention);
        Assert.Equal(TimeSpan.FromDays(365), defaults.HourlyRetention);
    }

    // ---------- Reading it back ----------

    [Fact]
    public async Task ALongWindowIsOneContinuousHourlySeriesAcrossBothTables()
    {
        var now = DateTimeOffset.UtcNow;
        var start = HourOf(now.AddDays(-10));
        var end = now.ToUnixTimeSeconds();
        var rows = new List<(long, double)>();
        for (var ts = start; ts <= end; ts += 600)
            rows.Add((ts, ts / Hour % 24));   // the same value all hour, so each hour's average is known
        await InsertAsync("nas", "temp_c", [.. rows]);
        await InsertAsync("nas", "temp_c", (start + 10, 100));   // one spike, in a summarised hour

        var history = History();
        await history.RollupAsync(null, default);
        Assert.NotEmpty(await SummariesAsync());

        var series = await history.SamplesAsync("nas", "temp_c", TimeSpan.FromDays(30));

        // One point per hour from the first to the one in progress, no gaps, no repeats.
        var expectedHours = (HourOf(now) - start) / Hour + 1;
        Assert.Equal(expectedHours, series.Count);
        Assert.All(series.Zip(series.Skip(1)), pair => Assert.True(pair.Second.At > pair.First.At));
        Assert.All(series.Zip(series.Skip(1)), pair =>
            Assert.True(pair.Second.At - pair.First.At <= TimeSpan.FromHours(1)));
        Assert.True(series[^1].At <= DateTimeOffset.Now);

        // Summarised hours and on-the-fly hours alike carry the hour's own figures.
        Assert.Equal(100, series[0].Max);
        Assert.Equal(start / Hour % 24, series[0].Min);
        Assert.Equal(7, series[0].Count);
        var recent = series[^3];
        Assert.Equal((recent.At.ToUnixTimeSeconds() / Hour) % 24, recent.Value);
        Assert.Equal(6, recent.Count);
    }

    [Fact]
    public async Task AWindowInsideTheRawRetentionStillReturnsEveryReading()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var rows = Enumerable.Range(0, 24 * 60).Select(i => (now - i * 60L, (double)i)).ToArray();
        await InsertAsync("nas", "temp_c", rows);
        await InsertAsync("nas", "temp_c", (HourOf(DateTimeOffset.UtcNow.AddDays(-9)), 1));
        var history = History();
        await history.RollupAsync(null, default);

        var day = await history.SamplesAsync("nas", "temp_c", TimeSpan.FromHours(24));

        Assert.Equal(24 * 60, day.Count);
        Assert.All(day, sample => Assert.Equal(1, sample.Count));
    }

    [Fact]
    public async Task MetricsAndTheLatestReadingSurviveTheirRawRowsBeingRolledUp()
    {
        var now = DateTimeOffset.UtcNow;
        // A speed test that stopped nine days ago: every one of its rows is past the raw
        // window, so after the rollup it exists only as summaries.
        var stopped = now.AddDays(-9).ToUnixTimeSeconds();
        await InsertAsync("speed", "download_mbps", (stopped - 7200, 400), (stopped - 30, 900), (stopped, 500));
        await InsertAsync("speed", "latency_ms", (now.ToUnixTimeSeconds() - 60, 3));

        var history = History();
        await history.RollupAsync(null, default);
        Assert.Empty(await RawTimestampsAsync("speed", "download_mbps"));

        Assert.Equal(["download_mbps", "latency_ms"], await history.MetricsAsync("speed"));

        var month = await history.LatestReadingsAsync("speed", TimeSpan.FromDays(30));
        // The reading that was really last, at the time it was really taken — not the
        // hour's average.
        Assert.Equal(500, month["download_mbps"].Value);
        Assert.Equal(stopped, month["download_mbps"].At.ToUnixTimeSeconds());
        Assert.Equal(3, month["latency_ms"].Value);

        // And a window that ends after it still leaves it out.
        var week = await history.LatestAsync("speed", TimeSpan.FromDays(7));
        Assert.False(week.ContainsKey("download_mbps"));
        Assert.True(week.ContainsKey("latency_ms"));
    }

    [Fact]
    public async Task DeletingAConnectionTakesItsSummariesWithIt()
    {
        var connection = new Connection { Provider = "http", Name = "NAS" };
        var config = _services.GetRequiredService<ConfigStore>();
        await config.SaveConnectionAsync(connection);
        await InsertAsync(connection.Id, "temp_c", (HourOf(DateTimeOffset.UtcNow.AddDays(-10)), 1));
        await History().RollupAsync(null, default);
        Assert.NotEmpty(await SummariesAsync());

        await config.DeleteConnectionAsync(connection.Id);

        Assert.Empty(await SummariesAsync());
    }

    // ---------- Upgrading ----------

    [Fact]
    public async Task AnExistingDatabaseGainsTheTableAndItsSamplesCanBeRolledUp()
    {
        // A database as the previous release left it: at migration 8, no summary table,
        // and ten days of samples.
        await using (var connection = await Db.OpenAsync())
        {
            var downgrade = connection.CreateCommand();
            downgrade.CommandText = """
                DROP TABLE samples_hourly;
                PRAGMA user_version = 8;
                """;
            await downgrade.ExecuteNonQueryAsync();
        }
        var start = HourOf(DateTimeOffset.UtcNow.AddDays(-10));
        var rows = Enumerable.Range(0, 10 * 24).Select(i => (start + i * Hour + 60, (double)i)).ToArray();
        await InsertAsync("nas", "temp_c", rows);

        var reopened = new Db(Options.Create(new LabbyOptions { DatabasePath = Path.Combine(_directory, "test.db") }),
            _services.GetRequiredService<IHostEnvironment>());
        await reopened.EnsureSchemaAsync();

        await using (var check = await reopened.OpenAsync())
        {
            var version = check.CreateCommand();
            version.CommandText = "PRAGMA user_version";
            Assert.Equal(10L, (long)(await version.ExecuteScalarAsync())!);
        }

        var history = new HistoryStore(reopened, Options.Create(new LabbyOptions()));
        Assert.Equal(10 * 24, (await history.SamplesAsync("nas", "temp_c", TimeSpan.FromDays(11))).Count);

        await history.RollupAsync(null, default);

        var after = await history.SamplesAsync("nas", "temp_c", TimeSpan.FromDays(11));
        Assert.Equal(10 * 24, after.Count);
        Assert.Equal(rows.Select(r => r.Item2), after.Select(s => s.Value));
        Assert.True((await SummariesAsync()).Count >= 2 * 24);
    }
}
