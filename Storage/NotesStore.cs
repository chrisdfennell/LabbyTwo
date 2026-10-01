using LabbyTwo.Core;
using Microsoft.Data.Sqlite;

namespace LabbyTwo.Storage;

/// <summary>
/// Markdown notes belonging to a notes tab, and the index of the <c>[[links]]</c> between
/// them.
///
/// The link index is written here, in the same transaction as the note, rather than by
/// whoever happens to save one: a note is saved from the editor, by an incident's write-up,
/// by the monthly report and by undo, and an index that only the editor kept up to date
/// would be wrong about every note the app wrote itself.
/// </summary>
public sealed class NotesStore(Db db)
{
    public sealed record Note(string Id, string TabId, string Title, string Content, int Sort, DateTimeOffset UpdatedAt);

    /// <summary>A note without its content: what the link directory needs, and all it reads.</summary>
    public sealed record Heading(string Id, string TabId, string Title);

    /// <summary>
    /// Raised after any note is written or deleted, so the link directory reads again.
    /// Not raised for <see cref="RememberTargetsAsync"/>, which is the directory writing
    /// down what it already knows.
    /// </summary>
    public event Action? Changed;

    public async Task<IReadOnlyList<Note>> ForTabAsync(string tabId, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, tab_id, title, content, sort, updated_at FROM notes WHERE tab_id = $tab ORDER BY sort, updated_at DESC";
        cmd.Parameters.AddWithValue("$tab", tabId);
        var list = new List<Note>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new Note(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt32(4), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)).ToLocalTime()));
        }
        return list;
    }

    /// <summary>
    /// The notes with these ids, whichever tab they are on — for the incidents that link to
    /// their write-ups. Each is a primary-key lookup; ids that are gone are simply missing.
    /// </summary>
    public async Task<IReadOnlyList<Note>> ByIdsAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        var distinct = ids.Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 0)
            return [];

        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = ByIdSql;
        var id = cmd.Parameters.Add("$id", SqliteType.Text);
        var list = new List<Note>();
        foreach (var each in distinct)
        {
            id.Value = each;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                list.Add(new Note(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetInt32(4), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)).ToLocalTime()));
            }
        }
        return list;
    }

    /// <summary>One note by its id; public for the query-plan test.</summary>
    public const string ByIdSql = "SELECT id, tab_id, title, content, sort, updated_at FROM notes WHERE id = $id";

    public async Task<string> SaveAsync(string? id, string tabId, string title, string content, CancellationToken ct = default)
    {
        id ??= Ids.New();
        await using (var connection = await db.OpenAsync(ct))
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT INTO notes (id, tab_id, title, content, sort, updated_at, links_indexed)
                VALUES ($id, $tab, $title, $content, 0, $now, 1)
                ON CONFLICT(id) DO UPDATE SET title = excluded.title, content = excluded.content,
                    updated_at = excluded.updated_at, links_indexed = 1
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$tab", tabId);
            cmd.Parameters.AddWithValue("$title", title);
            cmd.Parameters.AddWithValue("$content", content);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync(ct);
            await WriteLinksAsync(connection, transaction, id, content, ct);
            await transaction.CommitAsync(ct);
        }
        Changed?.Invoke();
        return id;
    }

    /// <summary>
    /// Puts a note back exactly as it was, its place in the order and its last-edited
    /// stamp included.
    ///
    /// <see cref="SaveAsync"/> cannot do this and should not: it writes sort 0 and stamps
    /// <c>updated_at</c> with now, which is the truth for an edit. Restoring a notes tab
    /// through it would reshuffle every note into one heap and claim they had all just
    /// been written — which is a strange thing for an undo to do, since undoing is the one
    /// operation that is supposed to leave no trace.
    /// </summary>
    public async Task RestoreAsync(Note note, CancellationToken ct = default)
    {
        await using (var connection = await db.OpenAsync(ct))
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT INTO notes (id, tab_id, title, content, sort, updated_at, links_indexed)
                VALUES ($id, $tab, $title, $content, $sort, $updated, 1)
                ON CONFLICT(id) DO UPDATE SET
                    tab_id = excluded.tab_id, title = excluded.title, content = excluded.content,
                    sort = excluded.sort, updated_at = excluded.updated_at, links_indexed = 1
                """;
            cmd.Parameters.AddWithValue("$id", note.Id);
            cmd.Parameters.AddWithValue("$tab", note.TabId);
            cmd.Parameters.AddWithValue("$title", note.Title);
            cmd.Parameters.AddWithValue("$content", note.Content);
            cmd.Parameters.AddWithValue("$sort", note.Sort);
            cmd.Parameters.AddWithValue("$updated", note.UpdatedAt.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync(ct);
            await WriteLinksAsync(connection, transaction, note.Id, note.Content, ct);
            await transaction.CommitAsync(ct);
        }
        Changed?.Invoke();
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using (var connection = await db.OpenAsync(ct))
        {
            var cmd = connection.CreateCommand();
            // The links this note made go with it. Links *to* it stay: they become "missing
            // note" links, which is the truth, and find it again if it is undone.
            cmd.CommandText = "DELETE FROM notes WHERE id = $id; " + DeleteLinksSql;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$from", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        Changed?.Invoke();
    }

    // ---- the link index

    /// <summary>The targets one note's index rows hold; public for the query-plan test.</summary>
    public const string LinksFromSql = "SELECT target FROM note_links WHERE from_id = $from";

    /// <summary>Every index row a note made; public for the query-plan test.</summary>
    public const string DeleteLinksSql = "DELETE FROM note_links WHERE from_id = $from";

    /// <summary>One index row; public for the query-plan test.</summary>
    public const string DeleteLinkSql = "DELETE FROM note_links WHERE from_id = $from AND target = $target";

    /// <summary>The note one index row last resolved to; public for the query-plan test.</summary>
    public const string RememberSql = "UPDATE note_links SET to_id = $to WHERE from_id = $from AND target = $target";

    /// <summary>The notes written before the index was, through the partial index; public for the query-plan test.</summary>
    public const string UnindexedSql = "SELECT id, content FROM notes WHERE links_indexed = 0";

    /// <summary>
    /// Every note's title and page, and every row of the link index — all the link
    /// directory needs, and no note's content. A scan of each, which for a table of notes
    /// somebody wrote by hand is a few hundred rows.
    /// </summary>
    public async Task<(IReadOnlyList<Heading> Notes, IReadOnlyList<NoteLinkRow> Links)> DirectoryAsync(CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var notes = new List<Heading>();
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, tab_id, title FROM notes";
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                notes.Add(new Heading(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        var links = new List<NoteLinkRow>();
        var read = connection.CreateCommand();
        read.CommandText = "SELECT from_id, target, to_id FROM note_links";
        await using (var reader = await read.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                links.Add(new NoteLinkRow(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }
        return (notes, links);
    }

    /// <summary>
    /// Indexes the links of every note written before there was an index (or restored from
    /// a backup made before it), and says how many. Nothing to do — the usual case — is one
    /// look at an empty partial index. Does not raise <see cref="Changed"/>: it is called by
    /// the directory, which is about to read what it wrote.
    /// </summary>
    public async Task<int> IndexMissingAsync(CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var waiting = new List<(string Id, string Content)>();
        var cmd = connection.CreateCommand();
        cmd.CommandText = UnindexedSql;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                waiting.Add((reader.GetString(0), reader.GetString(1)));
        }
        if (waiting.Count == 0)
            return 0;

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        var mark = connection.CreateCommand();
        mark.Transaction = transaction;
        mark.CommandText = "UPDATE notes SET links_indexed = 1 WHERE id = $id";
        var id = mark.Parameters.Add("$id", SqliteType.Text);
        foreach (var note in waiting)
        {
            await WriteLinksAsync(connection, transaction, note.Id, note.Content, ct);
            id.Value = note.Id;
            await mark.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return waiting.Count;
    }

    /// <summary>
    /// Writes down which note each of these links resolves to now (see
    /// <see cref="NoteGraph.Corrections"/>), and drops the rows of notes that no longer
    /// exist. So that on the day a note is renamed, every link that found it the day before
    /// still does.
    /// </summary>
    public async Task RememberTargetsAsync(IReadOnlyCollection<NoteLinkRow> rows, IReadOnlyCollection<string> orphans, CancellationToken ct = default)
    {
        if (rows.Count == 0 && orphans.Count == 0)
            return;
        await using var connection = await db.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = RememberSql;
        var to = update.Parameters.Add("$to", SqliteType.Text);
        var from = update.Parameters.Add("$from", SqliteType.Text);
        var target = update.Parameters.Add("$target", SqliteType.Text);
        foreach (var row in rows)
        {
            to.Value = (object?)row.ToId ?? DBNull.Value;
            from.Value = row.FromId;
            target.Value = row.Target;
            await update.ExecuteNonQueryAsync(ct);
        }

        var forget = connection.CreateCommand();
        forget.Transaction = transaction;
        forget.CommandText = DeleteLinksSql;
        var orphan = forget.Parameters.Add("$from", SqliteType.Text);
        foreach (var each in orphans)
        {
            orphan.Value = each;
            await forget.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// Makes the index rows for note <paramref name="id"/> match the links in
    /// <paramref name="content"/>: new targets added, gone ones removed, and the ones still
    /// there left alone — so the note each one last resolved to is not forgotten just
    /// because the note linking to it was edited.
    /// </summary>
    private static async Task WriteLinksAsync(SqliteConnection connection, SqliteTransaction transaction, string id, string content, CancellationToken ct)
    {
        var wanted = NoteLinks.Targets(content).ToHashSet(StringComparer.Ordinal);

        var had = new HashSet<string>(StringComparer.Ordinal);
        var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = LinksFromSql;
        read.Parameters.AddWithValue("$from", id);
        await using (var reader = await read.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                had.Add(reader.GetString(0));
        }

        if (had.SetEquals(wanted))
            return;

        var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = DeleteLinkSql;
        delete.Parameters.AddWithValue("$from", id);
        var gone = delete.Parameters.Add("$target", SqliteType.Text);
        foreach (var target in had.Except(wanted))
        {
            gone.Value = target;
            await delete.ExecuteNonQueryAsync(ct);
        }

        var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT OR IGNORE INTO note_links (from_id, target) VALUES ($from, $target)";
        insert.Parameters.AddWithValue("$from", id);
        var added = insert.Parameters.Add("$target", SqliteType.Text);
        foreach (var target in wanted.Except(had))
        {
            added.Value = target;
            await insert.ExecuteNonQueryAsync(ct);
        }
    }
}
