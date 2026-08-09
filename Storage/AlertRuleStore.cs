using LabbyTwo.Core;

namespace LabbyTwo.Storage;

/// <summary>
/// The threshold rules. Kept apart from <see cref="ConfigStore"/> because nothing else
/// reads them — the evaluator and one settings page — and because the three tables that
/// define the dashboard are a different concern from the ones that watch it.
/// </summary>
public sealed class AlertRuleStore(Db db)
{
    private List<AlertRule>? _cache;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public event Action? Changed;

    public async Task<IReadOnlyList<AlertRule>> AllAsync(CancellationToken ct = default)
    {
        if (_cache is not null)
            return _cache;

        await _lock.WaitAsync(ct);
        try
        {
            if (_cache is not null)
                return _cache;

            await using var connection = await db.OpenAsync(ct);
            var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT id, name, connection_id, metric, comparison, threshold, clear_threshold,
                       for_minutes, enabled
                FROM alert_rules ORDER BY name, metric
                """;
            var list = new List<AlertRule>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new AlertRule
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    ConnectionId = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Metric = reader.GetString(3),
                    Comparison = string.Equals(reader.GetString(4), "below", StringComparison.OrdinalIgnoreCase)
                        ? Comparison.Below
                        : Comparison.Above,
                    Threshold = reader.GetDouble(5),
                    ClearThreshold = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                    ForMinutes = reader.GetInt32(7),
                    Enabled = reader.GetInt64(8) != 0,
                });
            }
            return _cache = list;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<AlertRule?> GetAsync(string id, CancellationToken ct = default)
        => (await AllAsync(ct)).FirstOrDefault(r => r.Id == id);

    public async Task SaveAsync(AlertRule rule, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO alert_rules
                (id, name, connection_id, metric, comparison, threshold, clear_threshold, for_minutes, enabled)
            VALUES ($id, $name, $conn, $metric, $comparison, $threshold, $clear, $for, $enabled)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name, connection_id = excluded.connection_id,
                metric = excluded.metric, comparison = excluded.comparison,
                threshold = excluded.threshold, clear_threshold = excluded.clear_threshold,
                for_minutes = excluded.for_minutes, enabled = excluded.enabled
            """;
        cmd.Parameters.AddWithValue("$id", rule.Id);
        cmd.Parameters.AddWithValue("$name", rule.Name);
        cmd.Parameters.AddWithValue("$conn", (object?)rule.ConnectionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$metric", rule.Metric);
        cmd.Parameters.AddWithValue("$comparison", rule.Comparison == Comparison.Below ? "below" : "above");
        cmd.Parameters.AddWithValue("$threshold", rule.Threshold);
        cmd.Parameters.AddWithValue("$clear", (object?)rule.ClearThreshold ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$for", rule.ForMinutes);
        cmd.Parameters.AddWithValue("$enabled", rule.Enabled ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(ct);
        Invalidate();
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM alert_rules WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
        Invalidate();
    }

    private void Invalidate()
    {
        _cache = null;
        Changed?.Invoke();
    }
}
