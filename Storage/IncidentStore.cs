using LabbyTwo.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace LabbyTwo.Storage;

/// <summary>
/// Incidents and the services and rules involved in each. What makes something an
/// incident, and when one ends, is decided in <see cref="IncidentRules"/>; this only keeps
/// the answers.
///
/// Small on purpose. An incident is a row with a start, an end and a flag; its members are
/// a handful of rows under its id; its timeline is not stored at all but read from the
/// change feed for the incident's window. Every read is by id, by the open-incidents
/// partial index, or a range on the start time, newest first with a limit.
/// </summary>
public sealed class IncidentStore(Db db, IOptions<LabbyOptions> options)
{
    /// <summary>Raised after an incident is created or changed.</summary>
    public event Action<Incident>? Changed;

    /// <summary>
    /// Writes an incident and its members: inserted if it has no id yet, updated otherwise.
    /// Members are upserted by key, which is all an incident ever does to them — add one, or
    /// change when one went down or came back.
    /// </summary>
    public async Task<Incident> SaveAsync(Incident incident, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        var id = incident.Id;
        var cmd = connection.CreateCommand();
        if (id == 0)
        {
            cmd.CommandText = """
                INSERT INTO incidents (started_ts, ended_ts, last_ts, maintenance)
                VALUES ($started, $ended, $last, $maintenance) RETURNING id
                """;
        }
        else
        {
            cmd.CommandText = """
                UPDATE incidents SET started_ts = $started, ended_ts = $ended, last_ts = $last, maintenance = $maintenance
                WHERE id = $id
                """;
            cmd.Parameters.AddWithValue("$id", id);
        }
        cmd.Parameters.AddWithValue("$started", incident.StartedAt.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$ended", incident.EndedAt is { } ended ? ended.ToUnixTimeSeconds() : DBNull.Value);
        cmd.Parameters.AddWithValue("$last", incident.LastActivity.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$maintenance", incident.Maintenance ? 1 : 0);
        if (id == 0)
            id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        else
            await cmd.ExecuteNonQueryAsync(ct);

        var member = connection.CreateCommand();
        member.CommandText = """
            INSERT INTO incident_members (incident_id, member, kind, connection_id, name, down_ts, up_ts, seq)
            VALUES ($incident, $member, $kind, $connection, $name, $down, $up, $seq)
            ON CONFLICT (incident_id, member) DO UPDATE SET down_ts = excluded.down_ts, up_ts = excluded.up_ts
            """;
        var seq = member.Parameters.Add("$seq", SqliteType.Integer);
        member.Parameters.AddWithValue("$incident", id);
        var key = member.Parameters.Add("$member", SqliteType.Text);
        var kind = member.Parameters.Add("$kind", SqliteType.Text);
        var connectionId = member.Parameters.Add("$connection", SqliteType.Text);
        var name = member.Parameters.Add("$name", SqliteType.Text);
        var down = member.Parameters.Add("$down", SqliteType.Integer);
        var up = member.Parameters.Add("$up", SqliteType.Integer);
        foreach (var (m, index) in incident.Members.Select((m, i) => (m, i)))
        {
            seq.Value = index;
            key.Value = StoredKey(m);
            kind.Value = m.Kind;
            connectionId.Value = (object?)m.ConnectionId ?? DBNull.Value;
            name.Value = m.Name;
            down.Value = m.DownAt.ToUnixTimeSeconds();
            up.Value = m.UpAt is { } at ? at.ToUnixTimeSeconds() : DBNull.Value;
            await member.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);

        var saved = incident with { Id = id };
        Changed?.Invoke(saved);
        return saved;
    }

    /// <summary>Every incident still open — through the partial index, which holds nothing else.</summary>
    public const string OpenSql = """
        SELECT id, started_ts, ended_ts, last_ts, maintenance, writeup_note FROM incidents
        WHERE ended_ts IS NULL ORDER BY started_ts DESC LIMIT $limit
        """;

    /// <summary>Incidents that started in a window, newest first.</summary>
    public const string RecentSql = """
        SELECT id, started_ts, ended_ts, last_ts, maintenance, writeup_note FROM incidents
        WHERE started_ts >= $since ORDER BY started_ts DESC LIMIT $limit
        """;

    /// <summary>One incident's members, by its primary key.</summary>
    public const string MembersSql = """
        SELECT member, kind, connection_id, name, down_ts, up_ts FROM incident_members
        WHERE incident_id = $incident ORDER BY seq
        """;

    /// <summary>The open incidents, newest first.</summary>
    public async Task<IReadOnlyList<Incident>> OpenAsync(CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        return await ReadAsync(connection, OpenSql, cmd => cmd.Parameters.AddWithValue("$limit", 200), ct);
    }

    /// <summary>
    /// Incidents that started since <paramref name="since"/>, plus any still open that started
    /// before it — an outage that has gone on for a week is still the first thing to show.
    /// Newest first, at most <paramref name="limit"/>.
    /// </summary>
    public async Task<IReadOnlyList<Incident>> RecentAsync(DateTimeOffset since, int limit, bool openOnly = false, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        await using var connection = await db.OpenAsync(ct);
        var open = await ReadAsync(connection, OpenSql, cmd => cmd.Parameters.AddWithValue("$limit", limit), ct);
        if (openOnly)
            return open;

        var recent = await ReadAsync(connection, RecentSql, cmd =>
        {
            cmd.Parameters.AddWithValue("$since", since.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$limit", limit);
        }, ct);

        return
        [
            .. open.Concat(recent)
                .DistinctBy(i => i.Id)
                .OrderByDescending(i => i.StartedAt)
                .ThenByDescending(i => i.Id)
                .Take(limit),
        ];
    }

    /// <summary>
    /// Records the note written up about an incident, or forgets it with null. Its own
    /// statement, not part of <see cref="SaveAsync"/>: the tracker saves incidents from what
    /// it holds in memory, which knows nothing of write-ups, and must never clear one.
    /// </summary>
    public async Task SetWriteUpAsync(long id, string? noteId, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE incidents SET writeup_note = $note WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$note", (object?)noteId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
        if ((await ReadAsync(connection, ByIdSql, c => c.Parameters.AddWithValue("$id", id), ct)) is [var changed])
            Changed?.Invoke(changed);
    }

    /// <summary>One incident by its id.</summary>
    public const string ByIdSql = "SELECT id, started_ts, ended_ts, last_ts, maintenance, writeup_note FROM incidents WHERE id = $id";

    /// <summary>One incident, or null if there is no such id (or it has been pruned).</summary>
    public async Task<Incident?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var list = await ReadAsync(connection, ByIdSql,
            cmd => cmd.Parameters.AddWithValue("$id", id), ct);
        return list.Count > 0 ? list[0] : null;
    }

    private static async Task<IReadOnlyList<Incident>> ReadAsync(
        SqliteConnection connection, string sql, Action<SqliteCommand> bind, CancellationToken ct)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        bind(cmd);
        var rows = new List<(long Id, long Started, long? Ended, long Last, bool Maintenance, string? WriteUp)>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                rows.Add((reader.GetInt64(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    reader.GetInt64(3), reader.GetInt64(4) != 0, reader.IsDBNull(5) ? null : reader.GetString(5)));
            }
        }

        var members = connection.CreateCommand();
        members.CommandText = MembersSql;
        var incidentId = members.Parameters.Add("$incident", SqliteType.Integer);

        var list = new List<Incident>(rows.Count);
        foreach (var row in rows)
        {
            incidentId.Value = row.Id;
            var memberList = new List<IncidentMember>();
            await using (var reader = await members.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    var (memberKey, span) = ParseKey(reader.GetString(0));
                    memberList.Add(new IncidentMember(
                        memberKey,
                        reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.GetString(3),
                        Time(reader.GetInt64(4)),
                        reader.IsDBNull(5) ? null : Time(reader.GetInt64(5))) { Span = span });
                }
            }

            list.Add(new Incident(row.Id, Time(row.Started), row.Ended is { } ended ? Time(ended) : null,
                Time(row.Last), row.Maintenance, memberList) { WriteUpNoteId = row.WriteUp });
        }
        return list;
    }

    /// <summary>What a member row's key column holds.</summary>
    private const string SpanMark = "#span";

    /// <summary>
    /// The key a member is stored under. The first span of anything is stored under its own
    /// key, exactly as before spans existed, so every incident already recorded reads back
    /// unchanged; a later span gets its number on the end, which keeps the table's primary
    /// key — one row per incident and key — without a migration.
    /// </summary>
    public static string StoredKey(IncidentMember member) =>
        member.Span > 1 ? $"{member.Key}{SpanMark}{member.Span}" : member.Key;

    /// <summary>The member key and span a stored key stands for.</summary>
    public static (string Key, int Span) ParseKey(string stored)
    {
        var at = stored.LastIndexOf(SpanMark, StringComparison.Ordinal);
        return at > 0 && int.TryParse(stored.AsSpan(at + SpanMark.Length), System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture, out var span) && span > 1
            ? (stored[..at], span)
            : (stored, 1);
    }

    private static DateTimeOffset Time(long unixSeconds) => DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime();

    /// <summary>
    /// The deletes the prune runs: members first, found through the incidents' start index and
    /// removed by their own key, then the incidents. Only closed ones — an incident still open
    /// after a year is still news. Public for the query-plan test.
    /// </summary>
    public const string PruneMembersSql = """
        DELETE FROM incident_members WHERE incident_id IN (
            SELECT id FROM incidents WHERE started_ts < $keep AND ended_ts IS NOT NULL)
        """;

    public const string PruneIncidentsSql = "DELETE FROM incidents WHERE started_ts < $keep AND ended_ts IS NOT NULL";

    /// <summary>Drops closed incidents older than the retention. Returns how many went.</summary>
    public Task<int> PruneAsync(CancellationToken ct = default) => PruneAsync(DateTimeOffset.UtcNow, ct);

    /// <summary><see cref="PruneAsync(CancellationToken)"/> as if it were <paramref name="now"/>, for tests.</summary>
    public async Task<int> PruneAsync(DateTimeOffset now, CancellationToken ct)
    {
        var keep = (now - options.Value.IncidentRetention).ToUnixTimeSeconds();
        await using var connection = await db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        var members = connection.CreateCommand();
        members.CommandText = PruneMembersSql;
        members.Parameters.AddWithValue("$keep", keep);
        await members.ExecuteNonQueryAsync(ct);

        var incidents = connection.CreateCommand();
        incidents.CommandText = PruneIncidentsSql;
        incidents.Parameters.AddWithValue("$keep", keep);
        var deleted = await incidents.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
        return deleted;
    }
}
