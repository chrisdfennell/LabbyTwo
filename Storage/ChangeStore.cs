using System.Text;
using LabbyTwo.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace LabbyTwo.Storage;

/// <summary>
/// The "what changed" feed, and what each change detector saw last.
///
/// Anything may record a change — the monitor's status transitions, the container watcher,
/// the alert evaluator, a plugin — and nothing that records one needs to know who reads
/// them. <see cref="Recorded"/> is raised after each write, so an open feed, an incident
/// being built and a runbook's <c>{{changes}}</c> all hear about it at once.
///
/// Cheap by construction, because the database on a NAS is large and busy: a change is
/// one small insert, written only when something actually changed; every read is a range
/// on the ts index, newest first, with a limit; and the table is kept to
/// <see cref="LabbyOptions.ChangeRetention"/> by <see cref="PruneAsync(DateTimeOffset, CancellationToken)"/>.
/// </summary>
public sealed class ChangeStore(Db db, IOptions<LabbyOptions> options)
{
    /// <summary>Raised after a change is committed, with its id filled in.</summary>
    public event Action<Change>? Recorded;

    /// <summary>
    /// Stores a change and tells whoever is listening. Throws if the write fails — callers
    /// that must not stop for a lost row (a detector in the middle of a sweep) catch it.
    /// </summary>
    public async Task<Change> RecordAsync(Change change, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO changes (ts, kind, action, connection_id, subject, title, detail)
            VALUES ($ts, $kind, $action, $connection, $subject, $title, $detail)
            RETURNING id
            """;
        cmd.Parameters.AddWithValue("$ts", change.At.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$kind", change.Kind);
        cmd.Parameters.AddWithValue("$action", change.Action);
        cmd.Parameters.AddWithValue("$connection", (object?)change.ConnectionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$subject", change.Subject);
        cmd.Parameters.AddWithValue("$title", change.Title);
        cmd.Parameters.AddWithValue("$detail", change.Detail);
        var id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));

        var stored = change with { Id = id };
        Recorded?.Invoke(stored);
        return stored;
    }

    /// <summary>
    /// The SQL behind <see cref="QueryAsync"/>, for a query with <paramref name="kinds"/>
    /// kinds named and a connection or not. Public so the query-plan test reads exactly the
    /// statement the feed runs.
    ///
    /// A range on ix_changes_ts walked backwards, stopping at the limit. Kind and connection
    /// are checked on the rows the range yields rather than given indexes of their own: the
    /// table is a few thousand rows a month, and every extra index is another write on a
    /// disk that is already busy. ORDER BY ts, id matches the index — which holds the rowid
    /// after ts — so there is no sort either.
    /// </summary>
    public static string QuerySql(int kinds, bool connection)
    {
        var sql = new StringBuilder("""
            SELECT id, ts, kind, action, connection_id, subject, title, detail FROM changes
            WHERE ts >= $from AND ts < $to
            """);
        if (kinds > 0)
            sql.Append(" AND kind IN (").Append(string.Join(", ", Enumerable.Range(0, kinds).Select(i => $"$k{i}"))).Append(')');
        if (connection)
            sql.Append(" AND connection_id = $connection");
        sql.Append(" ORDER BY ts DESC, id DESC LIMIT $limit");
        return sql.ToString();
    }

    /// <summary>The changes a query asks for, newest first.</summary>
    public async Task<IReadOnlyList<Change>> QueryAsync(ChangeQuery query, CancellationToken ct = default)
    {
        var kinds = query.Kinds?.Where(k => k.Length > 0).Distinct().ToList() ?? [];
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = QuerySql(kinds.Count, query.ConnectionId is { Length: > 0 });
        cmd.Parameters.AddWithValue("$from", query.From.ToUnixTimeSeconds());
        // Exclusive, and a second added so "up to now" includes what was written this second.
        cmd.Parameters.AddWithValue("$to", query.To.ToUnixTimeSeconds() + 1);
        for (var i = 0; i < kinds.Count; i++)
            cmd.Parameters.AddWithValue($"$k{i}", kinds[i]);
        if (query.ConnectionId is { Length: > 0 } id)
            cmd.Parameters.AddWithValue("$connection", id);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 5000));

        var list = new List<Change>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new Change(
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)).ToLocalTime(),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7))
            {
                Id = reader.GetInt64(0),
            });
        }
        return list;
    }

    /// <summary>
    /// How often each thing happened in a window: one row per kind, action, connection and
    /// subject, with a count and the newest title and time. What the monthly report reads to
    /// say which alerts fired most and how many backups were proven, in one statement rather
    /// than reading every row back.
    ///
    /// A range on ix_changes_ts, the same as the feed's, bounded at both ends; the grouping
    /// is done over the few thousand rows a month holds. The kinds are checked on the rows
    /// the range yields, like the feed's own filter.
    /// </summary>
    public static string CountsSql(int kinds)
    {
        var sql = new StringBuilder("""
            SELECT kind, action, connection_id, subject, COUNT(*), MAX(ts), MAX(title) FROM changes
            WHERE ts >= $from AND ts < $to
            """);
        if (kinds > 0)
            sql.Append(" AND kind IN (").Append(string.Join(", ", Enumerable.Range(0, kinds).Select(i => $"$k{i}"))).Append(')');
        sql.Append(" GROUP BY kind, action, connection_id, subject");
        return sql.ToString();
    }

    /// <summary>One group of <see cref="CountsSql"/>: what happened, to what, how often, and the newest time and title.</summary>
    public sealed record Count(string Kind, string Action, string? ConnectionId, string Subject, int Times, DateTimeOffset Last, string Title);

    /// <summary>The changes in [<paramref name="from"/>, <paramref name="to"/>) of these kinds, counted by what and to what.</summary>
    public async Task<IReadOnlyList<Count>> CountsAsync(
        DateTimeOffset from, DateTimeOffset to, IReadOnlyCollection<string> kinds, CancellationToken ct = default)
    {
        var wanted = kinds.Where(k => k.Length > 0).Distinct().ToList();
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = CountsSql(wanted.Count);
        cmd.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$to", to.ToUnixTimeSeconds());
        for (var i = 0; i < wanted.Count; i++)
            cmd.Parameters.AddWithValue($"$k{i}", wanted[i]);

        var list = new List<Count>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new Count(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)).ToLocalTime(),
                reader.GetString(6)));
        }
        return list;
    }

    /// <summary>What a detector last saw for <paramref name="key"/>, or null if it has never looked.</summary>
    public async Task<string?> BaselineAsync(string key, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM change_baselines WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>
    /// Remembers what a detector saw. Callers write only when it differs from what they read,
    /// so a quiet sweep writes nothing.
    /// </summary>
    public async Task SetBaselineAsync(string key, string value, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO change_baselines (key, value, updated_at) VALUES ($key, $value, $at)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The most rows one delete removes, so the first prune after an upgrade never holds the write lock for long.</summary>
    private const int PruneBatch = 5000;

    /// <summary>The delete the prune runs in batches; public for the query-plan test.</summary>
    public const string PruneSql = """
        DELETE FROM changes WHERE id IN (
            SELECT id FROM changes WHERE ts < $keep ORDER BY ts LIMIT $batch)
        """;

    /// <summary>Drops changes older than the retention. Returns how many went.</summary>
    public Task<int> PruneAsync(CancellationToken ct = default) => PruneAsync(DateTimeOffset.UtcNow, ct);

    /// <summary><see cref="PruneAsync(CancellationToken)"/> as if it were <paramref name="now"/>, for tests.</summary>
    public async Task<int> PruneAsync(DateTimeOffset now, CancellationToken ct)
    {
        var keep = (now - options.Value.ChangeRetention).ToUnixTimeSeconds();
        var total = 0;
        await using var connection = await db.OpenAsync(ct);
        while (true)
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText = PruneSql;
            cmd.Parameters.AddWithValue("$keep", keep);
            cmd.Parameters.AddWithValue("$batch", PruneBatch);
            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            total += deleted;
            if (deleted < PruneBatch)
                return total;
        }
    }
}
