using LabbyTwo.Services;
using Microsoft.Data.Sqlite;

namespace LabbyTwo.Tests;

/// <summary>
/// Queries that must never read the whole samples table, checked by their plan rather than
/// their timing. On a fast SSD a full scan of half a gigabyte is quick enough to pass any
/// timing budget; on NAS disks the same scan made the health page time out behind
/// Cloudflare. The plan says "SCAN samples" on every machine.
/// </summary>
public sealed class QueryPlanTests
{
    private static List<string> Plan(string sql)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var create = connection.CreateCommand();
        create.CommandText =
            "CREATE TABLE samples (connection_id TEXT NOT NULL, metric TEXT NOT NULL, ts INTEGER NOT NULL, value REAL NOT NULL);" +
            "CREATE INDEX ix_samples_lookup ON samples (connection_id, metric, ts);";
        create.ExecuteNonQuery();

        var explain = connection.CreateCommand();
        explain.CommandText = "EXPLAIN QUERY PLAN " + sql;
        var steps = new List<string>();
        using var reader = explain.ExecuteReader();
        while (reader.Read())
            steps.Add(reader.GetString(3));
        return steps;
    }

    [Fact]
    public void The_health_pages_sample_estimate_seeks_both_ends_rather_than_scanning()
    {
        var plan = Plan(SystemHealth.SampleEstimateSql);

        Assert.DoesNotContain(plan, step => step.StartsWith("SCAN samples", StringComparison.Ordinal));
        Assert.Equal(2, plan.Count(step => step.StartsWith("SEARCH samples", StringComparison.Ordinal)));
    }

    [Fact]
    public void Min_and_max_together_in_one_select_would_scan_which_is_why_it_is_split()
    {
        // The trap itself, pinned down: if SQLite ever optimises this, the comment on
        // SampleEstimateSql can go, but until then nobody should "simplify" it back.
        Assert.Contains(Plan("SELECT MAX(rowid) - MIN(rowid) + 1 FROM samples"),
            step => step.StartsWith("SCAN samples", StringComparison.Ordinal));
    }
}
