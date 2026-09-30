using LabbyTwo.Core;
using Microsoft.Data.Sqlite;

namespace LabbyTwo.Storage;

/// <summary>
/// The Backups page's list and its restore tests. What an item's state *means* is
/// <see cref="BackupSchedule"/>'s business, and asking the sources is
/// <see cref="Services.BackupProof"/>'s; this keeps the rows.
///
/// Settings and sweep state share a row but are written by different methods —
/// <see cref="SaveAsync"/> never touches what the sweep keeps, and
/// <see cref="SaveStateAsync"/> never touches what the editor wrote — so a sweep and an edit
/// landing together cannot undo each other.
/// </summary>
public sealed class BackupStore(Db db)
{
    /// <summary>Raised after anything here is written, so an open page or list reads again.</summary>
    public event Action? Changed;

    /// <summary>
    /// Every item with its latest restore test. The list is a handful of rows read whole;
    /// the two subqueries each seek ix_restore_tests_item for one item's newest test rather
    /// than grouping the whole table. Public so the query-plan test reads the statement the
    /// page runs.
    /// </summary>
    public const string ItemsSql = """
        SELECT id, name, connection_id, source, source_target, source_metric, frequency, grace_hours,
               alert_late, drill, position, created_ts, last_success_ts, last_success_by, last_state,
               late_announced, drill_reminded_ts,
               (SELECT MAX(t.ts) FROM restore_tests t WHERE t.item_id = backup_items.id),
               (SELECT t.who FROM restore_tests t WHERE t.item_id = backup_items.id ORDER BY t.ts DESC LIMIT 1)
        FROM backup_items ORDER BY position, name
        """;

    /// <summary>One item's restore tests, newest first. Public for the query-plan test.</summary>
    public const string TestsSql = """
        SELECT id, item_id, ts, who, notes FROM restore_tests
        WHERE item_id = $item ORDER BY ts DESC LIMIT $limit
        """;

    public async Task<IReadOnlyList<BackupItem>> AllAsync(CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = ItemsSql;
        var list = new List<BackupItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new BackupItem
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                ConnectionId = reader.IsDBNull(2) ? null : reader.GetString(2),
                Source = reader.GetString(3),
                SourceTarget = reader.GetString(4),
                SourceMetric = reader.GetString(5),
                Frequency = Enum.TryParse<BackupFrequency>(reader.GetString(6), true, out var frequency) ? frequency : BackupFrequency.Daily,
                GraceHours = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                AlertWhenLate = reader.GetInt64(8) != 0,
                Drill = Enum.TryParse<DrillCadence>(reader.GetString(9), true, out var drill) ? drill : DrillCadence.Quarterly,
                Position = reader.GetInt32(10),
                CreatedAt = Instant(reader, 11) ?? DateTimeOffset.UnixEpoch,
                LastSuccess = Instant(reader, 12),
                LastSuccessBy = reader.GetString(13),
                LastState = reader.GetString(14),
                LateAnnounced = reader.GetInt64(15) != 0,
                DrillRemindedFor = Instant(reader, 16),
                LastRestoreTest = Instant(reader, 17),
                LastRestoreTestBy = reader.IsDBNull(18) ? "" : reader.GetString(18),
            });
        }
        return list;
    }

    public async Task<BackupItem?> GetAsync(string id, CancellationToken ct = default) =>
        (await AllAsync(ct)).FirstOrDefault(i => i.Id == id);

    /// <summary>
    /// Adds an item or changes its settings. What the sweep keeps is left alone on an
    /// update — except that pointing an item at a different source forgets the date the old
    /// one proved, since that date was never evidence about the new one.
    /// </summary>
    public async Task SaveAsync(BackupItem item, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO backup_items (id, name, connection_id, source, source_target, source_metric, frequency,
                                      grace_hours, alert_late, drill, position, created_ts)
            VALUES ($id, $name, $connection, $source, $target, $metric, $frequency, $grace, $alert, $drill, $position, $created)
            ON CONFLICT (id) DO UPDATE SET
                last_success_ts = CASE WHEN backup_items.source = excluded.source
                                        AND backup_items.source_target = excluded.source_target
                                        AND backup_items.source_metric = excluded.source_metric
                                       THEN backup_items.last_success_ts ELSE NULL END,
                last_state = CASE WHEN backup_items.source = excluded.source
                                   AND backup_items.source_target = excluded.source_target
                                   AND backup_items.source_metric = excluded.source_metric
                                  THEN backup_items.last_state ELSE '' END,
                name = excluded.name, connection_id = excluded.connection_id, source = excluded.source,
                source_target = excluded.source_target, source_metric = excluded.source_metric,
                frequency = excluded.frequency, grace_hours = excluded.grace_hours,
                alert_late = excluded.alert_late, drill = excluded.drill, position = excluded.position
            """;
        cmd.Parameters.AddWithValue("$id", item.Id);
        cmd.Parameters.AddWithValue("$name", item.Name.Trim());
        cmd.Parameters.AddWithValue("$connection", item.ConnectionId is { Length: > 0 } c ? c : DBNull.Value);
        cmd.Parameters.AddWithValue("$source", item.Source);
        cmd.Parameters.AddWithValue("$target", item.Source is BackupSources.Metric or BackupSources.Offsite ? item.SourceTarget : "");
        cmd.Parameters.AddWithValue("$metric", item.Source == BackupSources.Metric ? item.SourceMetric.Trim() : "");
        cmd.Parameters.AddWithValue("$frequency", item.Frequency.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$grace", item.GraceHours is { } grace ? grace : DBNull.Value);
        cmd.Parameters.AddWithValue("$alert", item.AlertWhenLate ? 1 : 0);
        cmd.Parameters.AddWithValue("$drill", item.Drill.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$position", item.Position);
        cmd.Parameters.AddWithValue("$created", item.CreatedAt.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct);
        Changed?.Invoke();
    }

    /// <summary>What a sweep, or a "Mark backed up", learned about an item.</summary>
    public async Task SaveStateAsync(BackupItem item, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE backup_items SET last_success_ts = $last, last_success_by = $by, last_state = $state,
                                    late_announced = $announced, drill_reminded_ts = $reminded
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", item.Id);
        cmd.Parameters.AddWithValue("$last", item.LastSuccess is { } last ? last.ToUnixTimeSeconds() : DBNull.Value);
        cmd.Parameters.AddWithValue("$by", item.LastSuccessBy);
        cmd.Parameters.AddWithValue("$state", item.LastState);
        cmd.Parameters.AddWithValue("$announced", item.LateAnnounced ? 1 : 0);
        cmd.Parameters.AddWithValue("$reminded", item.DrillRemindedFor is { } due ? due.ToUnixTimeSeconds() : DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
        Changed?.Invoke();
    }

    /// <summary>Removes an item and its restore tests.</summary>
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM restore_tests WHERE item_id = $id;
            DELETE FROM backup_items WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
        Changed?.Invoke();
    }

    /// <summary>Records a restore test. Returns it with its id.</summary>
    public async Task<RestoreTest> AddTestAsync(RestoreTest test, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO restore_tests (item_id, ts, who, notes) VALUES ($item, $ts, $who, $notes) RETURNING id
            """;
        cmd.Parameters.AddWithValue("$item", test.ItemId);
        cmd.Parameters.AddWithValue("$ts", test.At.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$who", test.Who.Trim());
        cmd.Parameters.AddWithValue("$notes", test.Notes.Trim());
        var id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        Changed?.Invoke();
        return test with { Id = id };
    }

    /// <summary>One item's restore tests, newest first.</summary>
    public async Task<IReadOnlyList<RestoreTest>> TestsAsync(string itemId, int limit = 50, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = TestsSql;
        cmd.Parameters.AddWithValue("$item", itemId);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        var list = new List<RestoreTest>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new RestoreTest(reader.GetInt64(0), reader.GetString(1),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)).ToLocalTime(), reader.GetString(3), reader.GetString(4)));
        }
        return list;
    }

    private static DateTimeOffset? Instant(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(ordinal)).ToLocalTime();
}
