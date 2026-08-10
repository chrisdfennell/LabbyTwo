using LabbyTwo.Storage;
using Microsoft.Data.Sqlite;

namespace LabbyTwo.ChoresPlugin;

/// <summary>
/// A plugin that stores its own data rather than reading somebody else's API.
///
/// It uses the host's database — injected as <see cref="Db"/> — and owns one table in it.
/// That is deliberate: the same file means the chores are inside every backup and every
/// "Download database" without anyone thinking about it. LabbyTwo's own migrations know
/// nothing about this table, so the plugin creates it itself, idempotently, on first use.
///
/// The <c>plugin_</c> prefix is manners rather than a rule. Nothing stops a plugin from
/// naming a table <c>connections</c>; nothing good happens either.
/// </summary>
public sealed class ChoreStore(Db db)
{
    public sealed record Chore(string Id, string Title, DateOnly Due, int EveryDays, DateOnly? LastDone)
    {
        public bool IsOverdue(DateOnly today) => Due < today;
        public bool IsDueToday(DateOnly today) => Due == today;
        public bool Recurs => EveryDays > 0;

        public string DueLabel(DateOnly today) => (Due.DayNumber - today.DayNumber) switch
        {
            0 => "today",
            1 => "tomorrow",
            -1 => "yesterday",
            < 0 and var days => $"{-days} days ago",
            var days and <= 7 => $"in {days} days",
            _ => Due.ToString("ddd d MMM"),
        };
    }

    private bool _ready;

    /// <summary>
    /// Called before every operation rather than once at startup, because a plugin has no
    /// startup hook — it is discovered, and then simply used.
    /// </summary>
    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = await db.OpenAsync(ct);

        if (!_ready)
        {
            await using var create = connection.CreateCommand();
            create.CommandText =
                """
                CREATE TABLE IF NOT EXISTS plugin_chores (
                    id         TEXT PRIMARY KEY,
                    title      TEXT NOT NULL,
                    due        TEXT NOT NULL,
                    every_days INTEGER NOT NULL DEFAULT 0,
                    last_done  TEXT
                )
                """;
            await create.ExecuteNonQueryAsync(ct);
            _ready = true;
        }

        return connection;
    }

    public async Task<IReadOnlyList<Chore>> AllAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, due, every_days, last_done FROM plugin_chores ORDER BY due, title";

        var chores = new List<Chore>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            chores.Add(new Chore(
                reader.GetString(0),
                reader.GetString(1),
                DateOnly.Parse(reader.GetString(2)),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : DateOnly.Parse(reader.GetString(4))));
        }

        return chores;
    }

    public async Task AddAsync(string title, DateOnly due, int everyDays, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO plugin_chores (id, title, due, every_days) VALUES ($id, $title, $due, $every)";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("n")[..12]);
        command.Parameters.AddWithValue("$title", title.Trim());
        command.Parameters.AddWithValue("$due", due.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$every", Math.Max(0, everyDays));
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Ticking a chore off. A repeating one comes back with a new due date rather than
    /// disappearing — counted from today, not from when it was *supposed* to be done, or a
    /// chore you are three weeks late on stays three weeks late forever.
    /// </summary>
    public async Task CompleteAsync(Chore chore, DateOnly today, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        if (!chore.Recurs)
        {
            await using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM plugin_chores WHERE id = $id";
            delete.Parameters.AddWithValue("$id", chore.Id);
            await delete.ExecuteNonQueryAsync(ct);
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE plugin_chores SET due = $due, last_done = $done WHERE id = $id";
        command.Parameters.AddWithValue("$due", today.AddDays(chore.EveryDays).ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$done", today.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$id", chore.Id);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM plugin_chores WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(ct);
    }
}
