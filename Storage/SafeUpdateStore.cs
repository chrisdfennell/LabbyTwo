using LabbyTwo.Core;
using Microsoft.Data.Sqlite;

namespace LabbyTwo.Storage;

/// <summary>
/// Safe updates: what each container ran before an update started from the Containers tab,
/// and where the watch after it has got to. Kept in the database rather than in memory
/// because a watch outlives a restart of LabbyTwo — the one thing that restarts most often
/// when containers are being updated — and a roll-back needs the old image id however long
/// ago the update was.
///
/// Small by construction: one row per update, <see cref="Keep"/> per container at most.
/// </summary>
public sealed class SafeUpdateStore(Db db)
{
    /// <summary>How many updates of one container are remembered.</summary>
    public const int Keep = 10;

    private const string Columns = """
        id, connection_id, container, container_id, reference, previous_image, previous_digest, rollback_tag,
        requested_ts, watch, watch_seconds, state, new_image, new_container_id, watch_from_ts, restart_baseline,
        ended_ts, reason
        """;

    /// <summary>The updates the watch job still has to look at, through the partial index that holds only those.</summary>
    public const string ActiveSql = $"""
        SELECT {Columns} FROM safe_updates
        WHERE state IN ('waiting', 'watching', 'rolling-back') ORDER BY id
        """;

    /// <summary>
    /// The newest update of each container on one host. Bare columns beside MAX(id) come
    /// from that row, and the index is walked in container order, so there is no sort.
    /// </summary>
    public const string LatestSql = $"""
        SELECT {Columns}, MAX(id) FROM safe_updates
        WHERE connection_id = $connection GROUP BY container
        """;

    /// <summary>Older updates of one container still holding a roll-back tag, to take it off.</summary>
    public const string TaggedSql = """
        SELECT id, rollback_tag FROM safe_updates
        WHERE connection_id = $connection AND container = $container AND id < $id AND rollback_tag <> ''
        """;

    public const string PruneSql = """
        DELETE FROM safe_updates
        WHERE connection_id = $connection AND container = $container AND id <= (
            SELECT id FROM safe_updates WHERE connection_id = $connection AND container = $container
            ORDER BY id DESC LIMIT 1 OFFSET $keep)
        """;

    /// <summary>
    /// Updates asked for in a window, oldest first — for the monthly report. The one read of
    /// this table that is not by host or by the active index, and it does not need either:
    /// the table holds <see cref="Keep"/> updates per container at most, a few hundred rows
    /// on the busiest install, and this runs once a month.
    /// </summary>
    public const string BetweenSql = $"""
        SELECT {Columns} FROM safe_updates
        WHERE requested_ts >= $from AND requested_ts < $to ORDER BY id
        """;

    public async Task<IReadOnlyList<SafeUpdate>> BetweenAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = BetweenSql;
        cmd.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$to", to.ToUnixTimeSeconds());
        return await ReadAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<SafeUpdate>> ActiveAsync(CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = ActiveSql;
        return await ReadAsync(cmd, ct);
    }

    /// <summary>The newest update of each container on one host, by container name.</summary>
    public async Task<IReadOnlyDictionary<string, SafeUpdate>> LatestAsync(string connectionId, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = LatestSql;
        cmd.Parameters.AddWithValue("$connection", connectionId);
        return (await ReadAsync(cmd, ct)).ToDictionary(u => u.Container, StringComparer.Ordinal);
    }

    public async Task<SafeUpdate?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM safe_updates WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return (await ReadAsync(cmd, ct)).FirstOrDefault();
    }

    /// <summary>Stores a new update, drops the oldest past <see cref="Keep"/>, and returns it with its id.</summary>
    public async Task<SafeUpdate> AddAsync(SafeUpdate update, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO safe_updates (connection_id, container, container_id, reference, previous_image, previous_digest,
                rollback_tag, requested_ts, watch, watch_seconds, state, new_image, new_container_id, watch_from_ts,
                restart_baseline, ended_ts, reason)
            VALUES ($connection, $container, $container_id, $reference, $previous_image, $previous_digest,
                $rollback_tag, $requested, $watch, $seconds, $state, $new_image, $new_container, $watch_from,
                $baseline, $ended, $reason)
            RETURNING id
            """;
        Bind(insert, update);
        var id = Convert.ToInt64(await insert.ExecuteScalarAsync(ct));

        var prune = connection.CreateCommand();
        prune.Transaction = transaction;
        prune.CommandText = PruneSql;
        prune.Parameters.AddWithValue("$connection", update.ConnectionId);
        prune.Parameters.AddWithValue("$container", update.Container);
        prune.Parameters.AddWithValue("$keep", Keep);
        await prune.ExecuteNonQueryAsync(ct);

        await transaction.CommitAsync(ct);
        return update with { Id = id };
    }

    /// <summary>Writes back everything that can change after an update is stored.</summary>
    public async Task SaveAsync(SafeUpdate update, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE safe_updates SET state = $state, new_image = $new_image, new_container_id = $new_container,
                watch_from_ts = $watch_from, restart_baseline = $baseline, ended_ts = $ended, reason = $reason,
                rollback_tag = $rollback_tag, previous_digest = $previous_digest
            WHERE id = $id
            """;
        Bind(cmd, update);
        cmd.Parameters.AddWithValue("$id", update.Id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Older updates of this container that still hold a roll-back tag: id and tag.</summary>
    public async Task<IReadOnlyList<(long Id, string Tag)>> OlderTagsAsync(SafeUpdate update, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = TaggedSql;
        cmd.Parameters.AddWithValue("$connection", update.ConnectionId);
        cmd.Parameters.AddWithValue("$container", update.Container);
        cmd.Parameters.AddWithValue("$id", update.Id);
        var list = new List<(long, string)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add((reader.GetInt64(0), reader.GetString(1)));
        return list;
    }

    /// <summary>Forgets an update's roll-back tag once the image behind it has been let go.</summary>
    public async Task ClearTagAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE safe_updates SET rollback_tag = '' WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void Bind(SqliteCommand cmd, SafeUpdate update)
    {
        cmd.Parameters.AddWithValue("$connection", update.ConnectionId);
        cmd.Parameters.AddWithValue("$container", update.Container);
        cmd.Parameters.AddWithValue("$container_id", update.ContainerId);
        cmd.Parameters.AddWithValue("$reference", update.Reference);
        cmd.Parameters.AddWithValue("$previous_image", update.PreviousImage);
        cmd.Parameters.AddWithValue("$previous_digest", update.PreviousDigest);
        cmd.Parameters.AddWithValue("$rollback_tag", update.RollbackTag);
        cmd.Parameters.AddWithValue("$requested", update.RequestedAt.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$watch", update.Watch ? 1 : 0);
        cmd.Parameters.AddWithValue("$seconds", (long)update.WatchFor.TotalSeconds);
        cmd.Parameters.AddWithValue("$state", SafeUpdate.Word(update.State));
        cmd.Parameters.AddWithValue("$new_image", update.NewImage);
        cmd.Parameters.AddWithValue("$new_container", update.NewContainerId);
        cmd.Parameters.AddWithValue("$watch_from", update.WatchFrom is { } from ? from.ToUnixTimeSeconds() : DBNull.Value);
        cmd.Parameters.AddWithValue("$baseline", update.RestartBaseline);
        cmd.Parameters.AddWithValue("$ended", update.EndedAt is { } ended ? ended.ToUnixTimeSeconds() : DBNull.Value);
        cmd.Parameters.AddWithValue("$reason", update.Reason);
    }

    private static async Task<IReadOnlyList<SafeUpdate>> ReadAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var list = new List<SafeUpdate>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new SafeUpdate(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(8)).ToLocalTime(),
                reader.GetInt64(9) != 0,
                TimeSpan.FromSeconds(reader.GetInt64(10)),
                SafeUpdate.Parse(reader.GetString(11)),
                reader.GetString(12),
                reader.GetString(13),
                reader.IsDBNull(14) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(14)).ToLocalTime(),
                reader.GetInt32(15),
                reader.IsDBNull(16) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(16)).ToLocalTime(),
                reader.GetString(17)));
        }
        return list;
    }
}
