using System.Collections.Concurrent;
using LabbyTwo.Core;

namespace LabbyTwo.Storage;

/// <summary>
/// Alerts that have fired and not cleared, as <see cref="Services.AlertService"/> tracks them for
/// delivery — see <see cref="FiringAlert"/>. Read once, then kept in memory and written
/// through: the table holds a row per thing currently broken, which is a handful, and the
/// alert pass asks about every one of them after every sweep.
/// </summary>
public sealed class FiringAlertStore(Db db)
{
    private readonly ConcurrentDictionary<string, FiringAlert> _entries = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile bool _loaded;

    /// <summary>Every entry, from memory. Empty until <see cref="LoadAsync"/> has run.</summary>
    public IReadOnlyCollection<FiringAlert> All => [.. _entries.Values];

    public FiringAlert? Get(string key) => _entries.TryGetValue(key, out var entry) ? entry : null;

    /// <summary>Reads the table the first time it is asked, and never again.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_loaded)
            return;

        await _lock.WaitAsync(ct);
        try
        {
            if (_loaded)
                return;

            await using var connection = await db.OpenAsync(ct);
            var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT key, rule_id, connection_id, since, clock_from, held_by, escalated_at,
                       escalations, escalated_to, title, body, link, value
                FROM firing_alerts
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var entry = new FiringAlert(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetString(2),
                    DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)),
                    DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4)),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(6)),
                    reader.GetInt32(7),
                    EscalationPolicy.ParseChannels(reader.GetString(8)),
                    reader.GetString(9),
                    reader.GetString(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.GetDouble(12));

                // TryAdd: anything written in memory before this finished is newer.
                _entries.TryAdd(entry.Key, entry);
            }
            _loaded = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(FiringAlert entry, CancellationToken ct = default)
    {
        _entries[entry.Key] = entry;

        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO firing_alerts
                (key, rule_id, connection_id, since, clock_from, held_by, escalated_at,
                 escalations, escalated_to, title, body, link, value)
            VALUES ($key, $rule, $conn, $since, $clock, $held, $escalated, $count, $to, $title, $body, $link, $value)
            ON CONFLICT(key) DO UPDATE SET
                rule_id = excluded.rule_id, connection_id = excluded.connection_id,
                since = excluded.since, clock_from = excluded.clock_from, held_by = excluded.held_by,
                escalated_at = excluded.escalated_at, escalations = excluded.escalations,
                escalated_to = excluded.escalated_to, title = excluded.title, body = excluded.body,
                link = excluded.link, value = excluded.value
            """;
        cmd.Parameters.AddWithValue("$key", entry.Key);
        cmd.Parameters.AddWithValue("$rule", (object?)entry.RuleId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$conn", entry.ConnectionId);
        cmd.Parameters.AddWithValue("$since", entry.Since.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$clock", entry.ClockFrom.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$held", (object?)entry.HeldBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$escalated", (object?)entry.EscalatedAt?.ToUnixTimeSeconds() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$count", entry.Escalations);
        cmd.Parameters.AddWithValue("$to", string.Join(',', entry.EscalatedTo));
        cmd.Parameters.AddWithValue("$title", entry.Title);
        cmd.Parameters.AddWithValue("$body", entry.Body);
        cmd.Parameters.AddWithValue("$link", (object?)entry.Link ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$value", double.IsFinite(entry.Value) ? entry.Value : 0);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        if (!_entries.TryRemove(key, out _))
            return;

        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM firing_alerts WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
