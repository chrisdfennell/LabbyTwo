using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace LabbyTwo.Tests;

/// <summary>
/// Queries that must never read the whole samples table, checked by their plan rather than
/// their timing. On a fast SSD a full scan of half a gigabyte is quick enough to pass any
/// timing budget; on NAS disks the same scan made the health page time out behind
/// Cloudflare. The plan says "SCAN samples" on every machine.
///
/// The history reads are held to more than "no scan": they must be answered from the
/// covering index alone. A plan that searches ix_samples_series without the word COVERING
/// goes back to the table for every row, and because rows are written a sweep at a time,
/// one series' rows are spread across the whole table — a week of one series was 80 MB of
/// reads for 20,000 numbers, on every chart, every sweep. That is not a scan, so only this
/// catches it.
/// </summary>
public sealed class QueryPlanTests
{
    /// <summary>
    /// The plan of <paramref name="sql"/> against the schema the app really makes — built by
    /// <see cref="Db"/> itself, every migration run — so a test can never pass against an
    /// index the app no longer has. Parameters are left unbound, which EXPLAIN allows.
    /// </summary>
    public static List<string> Plan(string sql) => WithSchema(connection =>
    {
        var explain = connection.CreateCommand();
        explain.CommandText = "EXPLAIN QUERY PLAN " + sql;
        var steps = new List<string>();
        using var reader = explain.ExecuteReader();
        while (reader.Read())
            steps.Add(reader.GetString(3));
        return steps;
    });

    private static T WithSchema<T>(Func<SqliteConnection, T> use)
    {
        var directory = TestHost.TempDirectory();
        using var services = new ServiceCollection().AddTestStorage(directory).BuildServiceProvider();
        try
        {
            services.GetRequiredService<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "test.db"),
                Pooling = false,
            }.ToString());
            connection.Open();
            return use(connection);
        }
        finally
        {
            using (var pooled = new SqliteConnection(services.GetRequiredService<Db>().ConnectionString))
                SqliteConnection.ClearPool(pooled);
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory should not fail a passing test.
            }
        }
    }

    /// <summary>Every step that reads samples reads the covering index and nothing else.</summary>
    private static void AssertIndexOnly(List<string> plan)
    {
        var reads = plan.Where(step => step.Contains(" samples ", StringComparison.Ordinal)
                                       || step.EndsWith(" samples", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(reads);
        Assert.All(reads, step => Assert.Contains("USING COVERING INDEX ix_samples_series", step, StringComparison.Ordinal));
        Assert.DoesNotContain(plan, step => step.StartsWith("SCAN samples", StringComparison.Ordinal));
    }

    [Fact]
    public void The_health_pages_sample_estimate_seeks_both_ends_rather_than_scanning()
    {
        var plan = Plan(SystemHealth.SampleEstimateSql);

        Assert.DoesNotContain(plan, step => step.StartsWith("SCAN samples", StringComparison.Ordinal));
        Assert.Equal(2, plan.Count(step => step.StartsWith("SEARCH samples", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_restores_last_sample_lookup_seeks_rather_than_scanning()
    {
        // The GROUP BY it replaced read every sample in the table, and held up monitoring
        // for sixteen minutes after each restart on a 580 MB install.
        var plan = Plan(HistoryStore.LastSampleSql);

        Assert.DoesNotContain(plan, step => step.StartsWith("SCAN samples", StringComparison.Ordinal));
        Assert.Contains(plan, step => step.StartsWith("SEARCH samples", StringComparison.Ordinal));
        AssertIndexOnly(plan);
    }

    [Fact]
    public void Min_and_max_together_in_one_select_would_scan_which_is_why_it_is_split()
    {
        // The trap itself, pinned down: if SQLite ever optimises this, the comment on
        // SampleEstimateSql can go, but until then nobody should "simplify" it back.
        Assert.Contains(Plan("SELECT MAX(rowid) - MIN(rowid) + 1 FROM samples"),
            step => step.StartsWith("SCAN samples", StringComparison.Ordinal));
    }

    [Fact]
    public void The_samples_index_carries_the_value_and_the_old_one_is_gone()
    {
        // Pinned from the schema side too: if the value ever left the index, every test
        // below would fail with a plan that looks almost right.
        var plan = Plan("SELECT value FROM samples WHERE connection_id = 'a' AND metric = 'b' AND ts > 0");
        Assert.Contains(plan, step => step.Contains("COVERING INDEX ix_samples_series", StringComparison.Ordinal));
        var indexes = WithSchema(connection =>
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'samples'";
            var names = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                names.Add(reader.GetString(0));
            return names;
        });
        // One index: a second on the same columns would cost every insert for nothing.
        Assert.Equal(["ix_samples_series"], indexes);
    }

    [Fact]
    public void A_chart_inside_the_raw_window_reads_only_the_index_and_in_order()
    {
        var plan = Plan(HistoryStore.RawSamplesSql);
        AssertIndexOnly(plan);
        // Already in time order from the index — no sort of a week of rows.
        Assert.DoesNotContain(plan, step => step.Contains("TEMP B-TREE", StringComparison.Ordinal));
    }

    [Fact]
    public void A_long_chart_forecast_or_baseline_reads_its_raw_hours_from_the_index_alone()
    {
        // Capacity forecasts (14 days) and unusual-value baselines (28 days) both come
        // through here for every series they look at: a week of raw rows each, summarised.
        AssertIndexOnly(Plan(HistoryStore.HourlyRawSql));
    }

    [Fact]
    public void The_latest_readings_never_go_back_to_the_table()
    {
        // It used to fetch each metric's newest row from the table by rowid — a random read
        // per metric, thirty of them for a weather station, on a disk busy with everything else.
        var plan = Plan(HistoryStore.LatestRawSql);
        AssertIndexOnly(plan);
        Assert.DoesNotContain(plan, step => step.Contains("SEARCH samples USING INTEGER PRIMARY KEY", StringComparison.Ordinal));
    }

    [Fact]
    public void The_metric_list_aggregates_and_energy_reads_stay_in_the_index()
    {
        AssertIndexOnly(Plan(HistoryStore.MetricsSql));
        AssertIndexOnly(Plan(HistoryStore.AggregateSql));
        AssertIndexOnly(Plan(HistoryStore.EnergyRawSql));
    }

    [Fact]
    public void The_newest_status_events_are_read_backwards_from_an_index_not_sorted()
    {
        // Asked every sweep by the changes card and the status tab, of a table kept for ever.
        var plan = Plan(HistoryStore.RecentEventsSql);
        Assert.DoesNotContain(plan, step => step.StartsWith("SCAN status_events", StringComparison.Ordinal)
                                            && !step.Contains("INDEX", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, step => step.Contains("TEMP B-TREE", StringComparison.Ordinal));

        var one = Plan(HistoryStore.RecentEventsForSql);
        Assert.Contains(one, step => step.Contains("ix_status_lookup", StringComparison.Ordinal));
        Assert.DoesNotContain(one, step => step.Contains("TEMP B-TREE", StringComparison.Ordinal));
    }

    [Fact]
    public void The_rollup_folds_an_hour_from_the_index_alone()
    {
        AssertIndexOnly(Plan(HistoryStore.FoldSql));
    }

    [Fact]
    public void A_scheduled_actions_history_is_one_range_of_its_index_read_backwards()
    {
        // Read for every action each time the page opens.
        var plan = Plan(ScheduledActionStore.RunsSql);
        Assert.Contains(plan, step => step.Contains("ix_scheduled_runs_action", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, step => step.StartsWith("SCAN scheduled_runs", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, step => step.Contains("TEMP B-TREE", StringComparison.Ordinal));
    }

    [Fact]
    public void Trimming_a_scheduled_actions_history_never_scans_the_table()
    {
        // After every run, of every action.
        var plan = Plan(ScheduledActionStore.TrimSql);
        Assert.Contains(plan, step => step.Contains("ix_scheduled_runs_action", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, step => step.StartsWith("SCAN scheduled_runs", StringComparison.Ordinal)
                                            && !step.Contains("INDEX", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, step => step.Contains("TEMP B-TREE", StringComparison.Ordinal));
    }

    [Fact]
    public void Moving_a_scheduled_action_on_is_a_primary_key_lookup()
    {
        // The only write a due action makes before it runs.
        var plan = Plan(ScheduledActionStore.SetCoveredSql);
        Assert.Contains(plan, step => step.StartsWith("SEARCH scheduled_actions USING PRIMARY KEY", StringComparison.Ordinal));
    }

    [Fact]
    public void A_chart_of_a_fixed_span_reads_only_the_index_bounded_at_both_ends()
    {
        // {{chart: … from= to=}} — last month in the monthly report. Raw, hourly on the fly,
        // and the stored summaries: each a range on its own key, never the table.
        var raw = Plan(HistoryStore.RawSamplesBetweenSql);
        AssertIndexOnly(raw);
        Assert.DoesNotContain(raw, step => step.Contains("TEMP B-TREE", StringComparison.Ordinal));
        Assert.Contains(raw, step => step.Contains("ts>? AND ts<?", StringComparison.Ordinal));

        AssertIndexOnly(Plan(HistoryStore.HourlyRawBetweenSql));

        var summaries = Plan(HistoryStore.SummariesBetweenSql);
        Assert.Contains(summaries, step => step.StartsWith("SEARCH samples_hourly", StringComparison.Ordinal)
                                          && step.Contains("hour_ts>? AND hour_ts<?", StringComparison.Ordinal));
        Assert.DoesNotContain(summaries, step => step.StartsWith("SCAN", StringComparison.Ordinal));
        Assert.DoesNotContain(summaries, step => step.Contains("TEMP B-TREE", StringComparison.Ordinal));
    }

    [Fact]
    public void A_months_uptime_is_two_range_reads_per_service_on_the_status_index()
    {
        // The monthly report (and the weekly summary) ask this of every connection. Status
        // events are kept for ever, so anything unbounded here grows for the life of the install.
        var prior = Plan(HistoryStore.StatusPriorSql);
        Assert.Contains(prior, step => step.StartsWith("SEARCH status_events USING", StringComparison.Ordinal)
                                      && step.Contains("ix_status_lookup", StringComparison.Ordinal));
        Assert.DoesNotContain(prior, step => step.Contains("TEMP B-TREE", StringComparison.Ordinal));

        var window = Plan(HistoryStore.StatusBetweenSql);
        Assert.Contains(window, step => step.StartsWith("SEARCH status_events USING", StringComparison.Ordinal)
                                       && step.Contains("ix_status_lookup", StringComparison.Ordinal)
                                       && step.Contains("ts>? AND ts<?", StringComparison.Ordinal));
        Assert.DoesNotContain(window, step => step.StartsWith("SCAN", StringComparison.Ordinal));
        Assert.DoesNotContain(window, step => step.Contains("TEMP B-TREE", StringComparison.Ordinal));
    }

    [Fact]
    public void A_months_incidents_and_feed_counts_are_ranges_on_their_time_indexes()
    {
        var incidents = Plan(IncidentStore.OverlappingSql);
        Assert.Contains(incidents, step => step.StartsWith("SEARCH incidents USING INDEX ix_incidents_started", StringComparison.Ordinal)
                                          && step.Contains("started_ts>? AND started_ts<?", StringComparison.Ordinal));
        Assert.DoesNotContain(incidents, step => step.StartsWith("SCAN", StringComparison.Ordinal));

        foreach (var kinds in new[] { 0, 2 })
        {
            var counts = Plan(ChangeStore.CountsSql(kinds));
            Assert.Contains(counts, step => step.StartsWith("SEARCH changes USING INDEX ix_changes_ts", StringComparison.Ordinal)
                                           && step.Contains("ts>? AND ts<?", StringComparison.Ordinal));
            Assert.DoesNotContain(counts, step => step.StartsWith("SCAN changes", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_storage_survey_seeks_and_counts_short_ranges_in_the_covering_index()
    {
        // The survey the storage page shows is built from these, per series, twice a day.
        // Listing the series hops through the index; each series' ends are two seeks — not
        // MIN and MAX side by side, which reads the whole range — and each sample window is
        // a range count that never leaves the index.
        AssertIndexOnly(Plan(HistoryStore.SeriesSql));

        var ends = Plan(HistoryStore.SurveyRawEndsSql);
        AssertIndexOnly(ends);
        Assert.Equal(2, ends.Count(step => step.StartsWith("SEARCH samples", StringComparison.Ordinal)));

        AssertIndexOnly(Plan(HistoryStore.SurveyRawCountSql));
    }

    [Fact]
    public void The_storage_survey_reads_the_summaries_by_their_key_too()
    {
        foreach (var sql in new[] { HistoryStore.HourlySeriesSql, HistoryStore.SurveyHourlyEndsSql, HistoryStore.SurveyHourlyCountSql })
        {
            var plan = Plan(sql);
            Assert.DoesNotContain(plan, step => step.StartsWith("SCAN samples_hourly", StringComparison.Ordinal));
            Assert.Contains(plan, step => step.StartsWith("SEARCH samples_hourly USING PRIMARY KEY", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_note_link_index_is_read_and_written_by_its_key()
    {
        // A save replaces one note's rows, and the directory writes back one row at a time:
        // each a seek on the primary key, never a scan of every link in every note.
        foreach (var sql in new[] { NotesStore.LinksFromSql, NotesStore.DeleteLinksSql, NotesStore.DeleteLinkSql, NotesStore.RememberSql })
        {
            var plan = Plan(sql);
            Assert.Contains(plan, step => step.StartsWith("SEARCH note_links USING PRIMARY KEY", StringComparison.Ordinal));
            Assert.DoesNotContain(plan, step => step.StartsWith("SCAN note_links", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Finding_notes_to_index_reads_only_the_partial_index()
    {
        // Checked on every read of the link directory, and empty on every install that has
        // been running a day: a walk of an empty index, not of every note's content.
        var plan = Plan(NotesStore.UnindexedSql);
        Assert.Contains(plan, step => step.Contains("USING INDEX ix_notes_unindexed", StringComparison.Ordinal));
    }

    [Fact]
    public void Note_templates_are_one_small_table_read_whole()
    {
        var plan = Plan(NoteTemplateStore.AllSql);
        Assert.Contains(plan, step => step.StartsWith("SCAN note_templates", StringComparison.Ordinal));
    }
}
