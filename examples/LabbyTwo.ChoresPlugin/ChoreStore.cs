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
    /// <param name="Assignee">Whose job it is. Empty means nobody in particular.</param>
    public sealed record Chore(
        string Id, string Title, DateOnly Due, int EveryDays, DateOnly? LastDone, string Assignee = "")
    {
        public bool IsAssigned => Assignee.Length > 0;

        /// <summary>True when this is theirs, or nobody's — an unclaimed chore is everyone's.</summary>
        public bool BelongsTo(string person) =>
            person.Length == 0
            || Assignee.Length == 0
            || Assignee.Equals(person, StringComparison.OrdinalIgnoreCase);

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
                    last_done  TEXT,
                    assignee   TEXT NOT NULL DEFAULT ''
                )
                """;
            await create.ExecuteNonQueryAsync(ct);

            await MigrateAsync(connection, ct);
            _ready = true;
        }

        return connection;
    }

    /// <summary>
    /// A plugin owns its schema, which means it owns changing it too — the host's
    /// migrations know nothing about this table. "assignee" arrived after the first
    /// release, so a table created by that version has to be caught up rather than
    /// recreated: dropping it to get a new column would throw away the list.
    /// </summary>
    private static async Task MigrateAsync(SqliteConnection connection, CancellationToken ct)
    {
        var columns = new List<string>();
        await using (var existing = connection.CreateCommand())
        {
            existing.CommandText = "SELECT name FROM pragma_table_info('plugin_chores')";
            await using var reader = await existing.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                columns.Add(reader.GetString(0));
        }

        if (columns.Contains("assignee"))
            return;

        await using var add = connection.CreateCommand();
        add.CommandText = "ALTER TABLE plugin_chores ADD COLUMN assignee TEXT NOT NULL DEFAULT ''";
        await add.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<Chore>> AllAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, title, due, every_days, last_done, assignee FROM plugin_chores ORDER BY due, title";

        var chores = new List<Chore>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            chores.Add(new Chore(
                reader.GetString(0),
                reader.GetString(1),
                DateOnly.Parse(reader.GetString(2)),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : DateOnly.Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? "" : reader.GetString(5)));
        }

        return chores;
    }

    public async Task AddAsync(
        string title, DateOnly due, int everyDays, string assignee = "", CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO plugin_chores (id, title, due, every_days, assignee) " +
            "VALUES ($id, $title, $due, $every, $who)";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("n")[..12]);
        command.Parameters.AddWithValue("$title", title.Trim());
        command.Parameters.AddWithValue("$due", due.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$every", Math.Max(0, everyDays));
        command.Parameters.AddWithValue("$who", await CanonicalAsync(connection, assignee, ct));
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Change a chore's title, due date or repeat in place. Assignee is left alone — that
    /// has its own control, and folding it in here would mean the edit form had to carry a
    /// people picker too. Whitespace-only titles are refused rather than saved blank.
    /// </summary>
    public async Task EditAsync(
        string id, string title, DateOnly due, int everyDays, CancellationToken ct = default)
    {
        title = title.Trim();
        if (title.Length == 0)
            return;

        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE plugin_chores SET title = $title, due = $due, every_days = $every WHERE id = $id";
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$due", due.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$every", Math.Max(0, everyDays));
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Hand a chore to somebody, or to nobody with an empty name.</summary>
    public async Task AssignAsync(string id, string assignee, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE plugin_chores SET assignee = $who WHERE id = $id";
        command.Parameters.AddWithValue("$who", await CanonicalAsync(connection, assignee, ct));
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Rename a person everywhere at once, case-insensitively. This is also how two spellings
    /// get merged — renaming "bob" to "Bob" sweeps every chore onto the one name — and how a
    /// person is cleared out, by renaming them to nothing.
    /// </summary>
    public async Task RenamePersonAsync(string from, string to, CancellationToken ct = default)
    {
        from = from.Trim();
        if (from.Length == 0)
            return;

        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE plugin_chores SET assignee = $to WHERE assignee = $from COLLATE NOCASE";
        command.Parameters.AddWithValue("$to", to.Trim());
        command.Parameters.AddWithValue("$from", from);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// The spelling a name should be stored under. If somebody with that name (ignoring case)
    /// already has a chore, reuse their exact spelling so "bob" typed today lands under the
    /// "Bob" already on the list, instead of splitting one person into two.
    /// </summary>
    private static async Task<string> CanonicalAsync(
        SqliteConnection connection, string raw, CancellationToken ct)
    {
        var name = raw.Trim();
        if (name.Length == 0)
            return "";

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT assignee FROM plugin_chores WHERE assignee = $who COLLATE NOCASE LIMIT 1";
        command.Parameters.AddWithValue("$who", name);
        return await command.ExecuteScalarAsync(ct) as string is { Length: > 0 } existing
            ? existing
            : name;
    }

    /// <summary>
    /// Everyone a chore has ever been given to, for the pickers. Derived rather than a
    /// table of people: a household is not worth a second table, and a name that stops
    /// being used stops being offered on its own.
    /// </summary>
    public async Task<IReadOnlyList<string>> PeopleAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT DISTINCT assignee FROM plugin_chores WHERE assignee <> '' ORDER BY assignee";

        var people = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            people.Add(reader.GetString(0));
        return people;
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
