using System.Diagnostics;
using System.Globalization;
using LabbyTwo.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace LabbyTwo.Tests;

/// <summary>
/// Times the history queries the dashboard and the settings pages make, against the big
/// database scripts/perf-bigdb.sh builds, and writes the numbers for that script to judge.
/// Skipped in an ordinary test run, which has no such database.
///
/// Here rather than behind an endpoint because a page cannot always show a slow query any
/// more. The alert rules page asks for every connection's metric names; on a runner's fast
/// disk with a warm cache, the SELECT DISTINCT that never finished on a NAS costs a third
/// of a second across the whole page, which is lost in the time the page takes anyway —
/// yet it is a hundred times what the fixed query costs, and it grows with the table. Timed
/// on its own, the difference is too large for any runner to hide. Nothing here asserts:
/// the budgets live in the script, beside the page budgets, in one table.
///
/// It also runs the history rollup, which in the app first runs a quarter of an hour after
/// startup and cannot be started from outside. The script seeds one day more of raw
/// samples than the retention keeps — an install whose rollup has fallen a day behind,
/// which is what an upgrade or a week with the NAS off looks like — and this folds it.
/// </summary>
public sealed class BigDatabaseTimings
{
    private static readonly string? Database = Environment.GetEnvironmentVariable("LABBY_PERF_DB");
    private static readonly string? Output = Environment.GetEnvironmentVariable("LABBY_PERF_TIMINGS");

    [BigDatabaseFact]
    public async Task Time_the_history_queries_on_a_big_database()
    {
        var directory = TestHost.TempDirectory();
        var rawDays = int.TryParse(Environment.GetEnvironmentVariable("LABBY_PERF_RAW_DAYS"), out var days) ? days : 7;
        var services = new ServiceCollection()
            .AddTestStorage(directory, options =>
            {
                options.DatabasePath = Database!;
                options.RetentionDays = rawDays;
            })
            .AddSingleton<HistoryStore>()
            .BuildServiceProvider();
        var lines = new List<string>();

        try
        {
            var history = services.GetRequiredService<HistoryStore>();
            var connections = await ConnectionIdsAsync(services.GetRequiredService<Db>());
            Assert.NotEmpty(connections);

            var month = TimeSpan.FromDays(30);
            await TimeAsync(lines, "metrics", connections, id => history.MetricsAsync(id));
            await TimeAsync(lines, "latest", connections, id => history.LatestReadingsAsync(id, TimeSpan.FromDays(1)));
            await TimeAsync(lines, "chart30", connections, id => history.SamplesAsync(id, "cpu_percent", month));
            // What the monitor reads before it can start watching anything after a restart.
            // It was a full-table GROUP BY, and on a 580 MB install it held monitoring up for
            // sixteen minutes; every page budget here passed, because pages never asked it.
            {
                var restoreClock = Stopwatch.StartNew();
                await history.LastSampleAtAsync(connections);
                restoreClock.Stop();
                lines.Add(Line("restore", restoreClock.Elapsed, $"last sample time for {connections.Count} connections"));
            }
            await TimeAsync(lines, "uptime30", connections, async id =>
            {
                await history.UptimeAsync(id, month);
                await history.DailyUptimeAsync(id, 30);
            });

            // Once, and last: it changes the database the other timings read.
            var clock = Stopwatch.StartNew();
            var result = await history.RollupAsync();
            clock.Stop();
            lines.Add(Line("rollup", clock.Elapsed,
                $"{result.RowsDeleted} raw rows into {result.HoursWritten} hourly rows in {result.Batches} batches"));
            lines.Add(Line("rollup-batch", result.LongestBatch, "the longest the write lock was held"));
        }
        finally
        {
            File.WriteAllLines(Output ?? Path.Combine(directory, "timings.txt"), lines);
            TestHost.Teardown(services, directory);
        }
    }

    /// <summary>
    /// The query once for every connection, as a page does, three times over; the fastest
    /// pass is what is reported. The first pass pays for JIT and a cold page cache, and a
    /// runner's neighbours can stall any one pass, but neither makes all three slow — while
    /// a query that reads the whole table is slow every time.
    /// </summary>
    private static async Task TimeAsync(List<string> lines, string name, IReadOnlyList<string> connections, Func<string, Task> query)
    {
        var best = TimeSpan.MaxValue;
        for (var pass = 0; pass < 3; pass++)
        {
            var clock = Stopwatch.StartNew();
            foreach (var id in connections)
                await query(id);
            clock.Stop();
            if (clock.Elapsed < best)
                best = clock.Elapsed;
        }
        lines.Add(Line(name, best, $"{connections.Count} connections, fastest of 3"));
    }

    private static string Line(string name, TimeSpan took, string detail) =>
        string.Create(CultureInfo.InvariantCulture, $"{name}|{(long)took.TotalMilliseconds}|{detail}");

    private static async Task<IReadOnlyList<string>> ConnectionIdsAsync(Db db)
    {
        await using var connection = await db.OpenAsync();
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM connections ORDER BY sort";
        var ids = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            ids.Add(reader.GetString(0));
        return ids;
    }

    /// <summary>A fact that runs only when perf-bigdb.sh has said where its database is.</summary>
    private sealed class BigDatabaseFactAttribute : FactAttribute
    {
        public BigDatabaseFactAttribute()
        {
            if (string.IsNullOrEmpty(Database))
                Skip = "Needs LABBY_PERF_DB: run scripts/perf-bigdb.sh, which builds the database and sets it.";
        }
    }
}
