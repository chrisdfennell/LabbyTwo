using LabbyTwo.Core;
using Microsoft.Data.Sqlite;

namespace LabbyTwo.Storage;

/// <summary>One stored version of a container's configuration.</summary>
/// <param name="ContainerId">The container it was read from. A later container with the
/// same settings keeps this version rather than writing a copy.</param>
public sealed record ContainerConfigVersion(long Id, string ContainerId, DateTimeOffset At, ContainerConfig Config);

/// <summary>
/// The config history: every version of every container's configuration, keyed by the
/// connection and the container's name, newest last. Written only when something changed,
/// and kept to <see cref="Keep"/> versions per container, so it stays a few hundred small
/// rows however long it runs.
/// </summary>
public sealed class ContainerConfigStore(Db db)
{
    /// <summary>How many versions of one container are kept.</summary>
    public const int Keep = 20;

    /// <summary>
    /// The newest version of each container on one host. SQLite's bare columns beside
    /// MAX(id) come from the row that holds the maximum, so this is one pass over the
    /// connection's range of the index — in container order, so there is no sort either.
    /// </summary>
    public const string LatestSql = """
        SELECT container, container_id, config, MAX(id) FROM container_configs
        WHERE connection_id = $connection GROUP BY container
        """;

    /// <summary>One container's versions, newest first.</summary>
    public const string HistorySql = """
        SELECT id, container_id, ts, config FROM container_configs
        WHERE connection_id = $connection AND container = $container
        ORDER BY id DESC LIMIT $limit
        """;

    /// <summary>Everything past the newest <see cref="Keep"/> of one container.</summary>
    public const string PruneSql = """
        DELETE FROM container_configs
        WHERE connection_id = $connection AND container = $container AND id <= (
            SELECT id FROM container_configs WHERE connection_id = $connection AND container = $container
            ORDER BY id DESC LIMIT 1 OFFSET $keep)
        """;

    /// <summary>What the newest version of each container is, by name: its id and configuration.</summary>
    public async Task<IReadOnlyDictionary<string, (long Id, string ContainerId, ContainerConfig? Config)>> LatestAsync(
        string connectionId, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = LatestSql;
        cmd.Parameters.AddWithValue("$connection", connectionId);

        var latest = new Dictionary<string, (long, string, ContainerConfig?)>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            latest[reader.GetString(0)] = (reader.GetInt64(3), reader.GetString(1), ContainerConfig.FromJson(reader.GetString(2)));
        return latest;
    }

    /// <summary>One container's versions, newest first.</summary>
    public async Task<IReadOnlyList<ContainerConfigVersion>> HistoryAsync(
        string connectionId, string container, int limit = Keep, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = HistorySql;
        cmd.Parameters.AddWithValue("$connection", connectionId);
        cmd.Parameters.AddWithValue("$container", container);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        var list = new List<ContainerConfigVersion>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (ContainerConfig.FromJson(reader.GetString(3)) is not { } config)
                continue;
            list.Add(new ContainerConfigVersion(reader.GetInt64(0), reader.GetString(1),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)).ToLocalTime(), config));
        }
        return list;
    }

    /// <summary>Stores a new version and drops any past the newest <see cref="Keep"/>. Returns its id.</summary>
    public async Task<long> AddAsync(
        string connectionId, string container, string containerId, DateTimeOffset at, ContainerConfig config,
        CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO container_configs (connection_id, container, container_id, ts, config)
            VALUES ($connection, $container, $id, $ts, $config) RETURNING id
            """;
        insert.Parameters.AddWithValue("$connection", connectionId);
        insert.Parameters.AddWithValue("$container", container);
        insert.Parameters.AddWithValue("$id", containerId);
        insert.Parameters.AddWithValue("$ts", at.ToUnixTimeSeconds());
        insert.Parameters.AddWithValue("$config", config.ToJson());
        var id = Convert.ToInt64(await insert.ExecuteScalarAsync(ct));

        var prune = connection.CreateCommand();
        prune.Transaction = transaction;
        prune.CommandText = PruneSql;
        prune.Parameters.AddWithValue("$connection", connectionId);
        prune.Parameters.AddWithValue("$container", container);
        prune.Parameters.AddWithValue("$keep", Keep);
        await prune.ExecuteNonQueryAsync(ct);

        await transaction.CommitAsync(ct);
        return id;
    }

    /// <summary>
    /// Moves a version on to a new container with the same settings — a plain recreate — so
    /// the next start of LabbyTwo does not inspect it again to find nothing new.
    /// </summary>
    public async Task TouchAsync(long id, string containerId, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE container_configs SET container_id = $container WHERE id = $id";
        cmd.Parameters.AddWithValue("$container", containerId);
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
