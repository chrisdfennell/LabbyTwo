using Microsoft.Data.Sqlite;

namespace LabbyTwo.Storage;

/// <summary>
/// One browser or phone that asked to be notified. The endpoint is the push service's
/// address for that one browser profile; p256dh and auth are the keys its messages are
/// encrypted to. The name is only for people — "Chris's phone" — and is what a Browser
/// push channel's device list matches on.
/// </summary>
public sealed record PushSubscription
{
    public string Id { get; init; } = Guid.NewGuid().ToString("n");
    public string Name { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public string P256dh { get; init; } = "";
    public string Auth { get; init; } = "";
    public string UserAgent { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSentAt { get; init; }
    public string LastError { get; init; } = "";
}

/// <summary>The push_subscriptions table. Small, rarely written, so no cache.</summary>
public sealed class PushSubscriptionStore(Db db)
{
    /// <summary>Raised after any change, so an open devices page redraws when a send removes a dead one.</summary>
    public event Action? Changed;

    public async Task<IReadOnlyList<PushSubscription>> AllAsync(CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, endpoint, p256dh, auth, user_agent, created_at, last_sent_at, last_error
            FROM push_subscriptions ORDER BY created_at
            """;
        var result = new List<PushSubscription>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new PushSubscription
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Endpoint = reader.GetString(2),
                P256dh = reader.GetString(3),
                Auth = reader.GetString(4),
                UserAgent = reader.GetString(5),
                CreatedAt = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(6)),
                LastSentAt = reader.IsDBNull(7) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(7)),
                LastError = reader.GetString(8),
            });
        }
        return result;
    }

    /// <summary>
    /// Adds a subscription, or refreshes the keys and name of one already stored for the same
    /// endpoint. Pressing the button twice on one phone must not make that phone buzz twice.
    /// </summary>
    public async Task<PushSubscription> SaveAsync(PushSubscription value, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO push_subscriptions (id, name, endpoint, p256dh, auth, user_agent, created_at, last_error)
            VALUES ($id, $name, $endpoint, $p256dh, $auth, $ua, $created, '')
            ON CONFLICT(endpoint) DO UPDATE SET
                name = excluded.name, p256dh = excluded.p256dh, auth = excluded.auth,
                user_agent = excluded.user_agent, last_error = ''
            RETURNING id
            """;
        cmd.Parameters.AddWithValue("$id", value.Id);
        cmd.Parameters.AddWithValue("$name", value.Name);
        cmd.Parameters.AddWithValue("$endpoint", value.Endpoint);
        cmd.Parameters.AddWithValue("$p256dh", value.P256dh);
        cmd.Parameters.AddWithValue("$auth", value.Auth);
        cmd.Parameters.AddWithValue("$ua", value.UserAgent);
        cmd.Parameters.AddWithValue("$created", value.CreatedAt.ToUnixTimeSeconds());
        var id = (string)(await cmd.ExecuteScalarAsync(ct))!;
        Changed?.Invoke();
        return value with { Id = id };
    }

    public Task RenameAsync(string id, string name, CancellationToken ct = default) =>
        ExecuteAsync("UPDATE push_subscriptions SET name = $a WHERE id = $id", id, name.Trim(), ct);

    public Task DeleteAsync(string id, CancellationToken ct = default) =>
        ExecuteAsync("DELETE FROM push_subscriptions WHERE id = $id", id, null, ct);

    public Task DeleteAllAsync(CancellationToken ct = default) =>
        ExecuteAsync("DELETE FROM push_subscriptions", null, null, ct);

    /// <summary>
    /// The push service moved a browser to a new endpoint (pushsubscriptionchange). Matched
    /// on the old endpoint, which only that browser and this server know, so a caller cannot
    /// use this to take over somebody else's row without already holding it.
    /// </summary>
    public async Task<bool> ReplaceEndpointAsync(string oldEndpoint, string endpoint, string p256dh, string auth, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE push_subscriptions SET endpoint = $new, p256dh = $p, auth = $a, last_error = ''
            WHERE endpoint = $old
            """;
        cmd.Parameters.AddWithValue("$old", oldEndpoint);
        cmd.Parameters.AddWithValue("$new", endpoint);
        cmd.Parameters.AddWithValue("$p", p256dh);
        cmd.Parameters.AddWithValue("$a", auth);
        var changed = await cmd.ExecuteNonQueryAsync(ct) > 0;
        if (changed)
            Changed?.Invoke();
        return changed;
    }

    /// <summary>What happened on the last send, shown beside the device.</summary>
    public async Task RecordAsync(string id, bool delivered, string error, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = delivered
            ? "UPDATE push_subscriptions SET last_sent_at = $now, last_error = '' WHERE id = $id"
            : "UPDATE push_subscriptions SET last_error = $error WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$error", error);
        await cmd.ExecuteNonQueryAsync(ct);
        Changed?.Invoke();
    }

    private async Task ExecuteAsync(string sql, string? id, string? argument, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", (object?)id ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$a", (object?)argument ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
        Changed?.Invoke();
    }
}
