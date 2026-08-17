using LabbyTwo.Storage;
using Microsoft.Data.Sqlite;

namespace LabbyTwo.InboxPlugin;

/// <summary>
/// What came in. Rows in the host's own database, so they are inside every backup and the
/// retention sweep is a DELETE rather than a folder to manage.
/// </summary>
public sealed class InboxStore(Db db)
{
    public sealed record Event(
        long Id,
        string ConnectionId,
        string Source,
        string Level,
        string Title,
        string Body,
        DateTimeOffset Created)
    {
        public bool IsBad => Level.Equals("down", StringComparison.OrdinalIgnoreCase);
        public bool IsGood => Level.Equals("up", StringComparison.OrdinalIgnoreCase);

        public string Emoji => Level.ToLowerInvariant() switch
        {
            "down" => "🔴",
            "up" => "🟢",
            _ => "ℹ️",
        };
    }

    private bool _ready;

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = await db.OpenAsync(ct);

        if (!_ready)
        {
            await using var create = connection.CreateCommand();
            create.CommandText =
                """
                CREATE TABLE IF NOT EXISTS plugin_inbox (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    connection_id TEXT NOT NULL,
                    source        TEXT NOT NULL DEFAULT '',
                    level         TEXT NOT NULL DEFAULT 'info',
                    title         TEXT NOT NULL DEFAULT '',
                    body          TEXT NOT NULL DEFAULT '',
                    created       INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_plugin_inbox_lookup ON plugin_inbox (connection_id, created DESC);
                """;
            await create.ExecuteNonQueryAsync(ct);
            _ready = true;
        }

        return connection;
    }

    public async Task<long> AddAsync(
        string connectionId, string source, string level, string title, string body, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO plugin_inbox (connection_id, source, level, title, body, created)
            VALUES ($c, $s, $l, $t, $b, $n);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$c", connectionId);
        command.Parameters.AddWithValue("$s", source);
        command.Parameters.AddWithValue("$l", level);
        command.Parameters.AddWithValue("$t", title);
        command.Parameters.AddWithValue("$b", body);
        command.Parameters.AddWithValue("$n", DateTimeOffset.Now.ToUnixTimeSeconds());

        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    public async Task<IReadOnlyList<Event>> RecentAsync(string? connectionId, int limit, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, connection_id, source, level, title, body, created
            FROM plugin_inbox
            WHERE ($c IS NULL OR connection_id = $c)
            ORDER BY created DESC, id DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$c", (object?)connectionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        var events = new List<Event>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            events.Add(new Event(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(6)).ToLocalTime()));

        return events;
    }

    /// <summary>
    /// How many arrived in a window, and how long ago the last one was. Both are what the
    /// provider reports, and the second is the one that matters: a nightly job that stops
    /// reporting is exactly the failure nobody notices, because nothing happens.
    /// </summary>
    public sealed record Summary(int InWindow, DateTimeOffset? Last);

    public async Task<Summary> SummaryAsync(string connectionId, TimeSpan window, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                (SELECT COUNT(*) FROM plugin_inbox WHERE connection_id = $c AND created >= $since),
                (SELECT MAX(created) FROM plugin_inbox WHERE connection_id = $c)
            """;
        command.Parameters.AddWithValue("$c", connectionId);
        command.Parameters.AddWithValue("$since", DateTimeOffset.Now.Subtract(window).ToUnixTimeSeconds());

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new Summary(0, null);

        return new Summary(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)).ToLocalTime());
    }

    /// <summary>Drops anything older than the retention on its own connection. Returns how many went.</summary>
    public async Task<int> PurgeAsync(IReadOnlyDictionary<string, int> keepDaysByConnection, CancellationToken ct)
    {
        if (keepDaysByConnection.Count == 0)
            return 0;

        await using var connection = await OpenAsync(ct);
        var removed = 0;

        foreach (var (connectionId, days) in keepDaysByConnection)
        {
            // Zero means keep everything. A receiver somebody uses as a log wants that, and
            // silently deleting their history because the field was left at its default
            // would be the worst kind of tidy-up.
            if (days <= 0)
                continue;

            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM plugin_inbox WHERE connection_id = $c AND created < $before";
            command.Parameters.AddWithValue("$c", connectionId);
            command.Parameters.AddWithValue("$before", DateTimeOffset.Now.AddDays(-days).ToUnixTimeSeconds());
            removed += await command.ExecuteNonQueryAsync(ct);
        }

        return removed;
    }

    /// <summary>Rows belonging to connections that no longer exist, which nothing else would ever clear.</summary>
    public async Task<int> PurgeOrphansAsync(IReadOnlyCollection<string> liveConnectionIds, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();

        // Built rather than parameterised because the list is ids this process just read out
        // of its own database, and SQLite has no array parameter. They are still checked:
        // anything that is not a plain id is dropped before it reaches the text.
        var safe = liveConnectionIds
            .Where(id => id.Length > 0 && id.All(char.IsAsciiLetterOrDigit))
            .Select(id => $"'{id}'")
            .ToList();

        // Refusing to run is the only safe answer when the filter ate everything. "No live
        // connections" and "every id looked wrong" reach this line identically, and the
        // second one turning into an unqualified DELETE would throw away the history of
        // receivers that are still configured.
        if (safe.Count != liveConnectionIds.Count)
            return 0;

        command.CommandText = safe.Count == 0
            ? "DELETE FROM plugin_inbox"
            : $"DELETE FROM plugin_inbox WHERE connection_id NOT IN ({string.Join(",", safe)})";

        return await command.ExecuteNonQueryAsync(ct);
    }
}
