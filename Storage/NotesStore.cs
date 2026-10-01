using LabbyTwo.Core;
using Microsoft.Data.Sqlite;

namespace LabbyTwo.Storage;

/// <summary>
/// Markdown notes belonging to a notes tab, and what each of them said before.
///
/// Every save that changes a note first copies what it replaces into note_versions, in
/// the same transaction, so there is never a moment when an edit has landed and the text
/// it overwrote has not been kept. Deleting a note keeps it there too, which is what the
/// "Recently deleted" list restores from. Kept to <see cref="KeepPerNote"/> versions a note
/// (trimmed as each save writes, so it never grows past that) and to <see cref="KeepFor"/>
/// by age, and a deleted note's history goes <see cref="DeletedFor"/> after it was deleted —
/// both by <see cref="PruneAsync(DateTimeOffset, CancellationToken)"/>, a batch at a time.
///
/// Versions live in the database, so a backup holds them; a config export does not, since
/// it carries what the dashboard is, not what it used to be.
/// </summary>
public sealed class NotesStore(Db db)
{
    /// <param name="UpdatedAt">When it was last saved.</param>
    public sealed record Note(string Id, string TabId, string Title, string Content, int Sort, DateTimeOffset UpdatedAt)
    {
        /// <summary>Who last saved it; empty for a note written before anybody was recorded.</summary>
        public string UpdatedBy { get; init; } = "";
    }

    /// <summary>What a note said at one time.</summary>
    /// <param name="Content">Empty in a listing (<see cref="VersionsAsync"/>), which never reads the text.</param>
    /// <param name="Size">Characters of text, so a listing can say how big each was without reading it.</param>
    /// <param name="WrittenAt">When this text was saved — not when it was replaced.</param>
    /// <param name="WrittenBy">Who saved it; for a deleted note, who deleted it.</param>
    /// <param name="KeptAt">When it was replaced, or deleted.</param>
    /// <param name="Reason"><see cref="Edited"/> or <see cref="Deleted"/>.</param>
    public sealed record NoteVersion(
        long Id, string NoteId, string TabId, string Title, string Content, int Size,
        DateTimeOffset WrittenAt, string WrittenBy, DateTimeOffset KeptAt, string Reason)
    {
        public bool IsDeletion => Reason == Deleted;
    }

    public const string Edited = "edit";
    public const string Deleted = "deleted";

    /// <summary>Versions kept for each note; the oldest go as new ones are made.</summary>
    public const int KeepPerNote = 50;

    /// <summary>How long any version is kept.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(180);

    /// <summary>How long a deleted note can be brought back.</summary>
    public static readonly TimeSpan DeletedFor = TimeSpan.FromDays(30);

    private const int PruneBatch = 2000;

    private static Note ReadNote(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetInt32(4), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)).ToLocalTime())
        {
            UpdatedBy = reader.GetString(6),
        };

    public async Task<IReadOnlyList<Note>> ForTabAsync(string tabId, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, tab_id, title, content, sort, updated_at, updated_by FROM notes WHERE tab_id = $tab ORDER BY sort, updated_at DESC";
        cmd.Parameters.AddWithValue("$tab", tabId);
        var list = new List<Note>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(ReadNote(reader));
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
                list.Add(ReadNote(reader));
        }
        return list;
    }

    /// <summary>One note by its id; public for the query-plan test.</summary>
    public const string ByIdSql = "SELECT id, tab_id, title, content, sort, updated_at, updated_by FROM notes WHERE id = $id";

    public Task<string> SaveAsync(string? id, string tabId, string title, string content, CancellationToken ct = default) =>
        SaveAsync(id, tabId, title, content, "", ct);

    /// <summary>
    /// Writes a note, keeping what it said before as a version when this changes it.
    /// <paramref name="by"/> is who is saving, which the next version will say wrote it.
    /// </summary>
    public async Task<string> SaveAsync(string? id, string tabId, string title, string content, string by, CancellationToken ct = default)
    {
        id ??= Ids.New();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await using var connection = await db.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        var keep = connection.CreateCommand();
        keep.Transaction = transaction;
        keep.CommandText = KeepVersionSql;
        keep.Parameters.AddWithValue("$id", id);
        keep.Parameters.AddWithValue("$title", title);
        keep.Parameters.AddWithValue("$content", content);
        keep.Parameters.AddWithValue("$by", "");
        keep.Parameters.AddWithValue("$now", now);
        keep.Parameters.AddWithValue("$reason", Edited);
        var kept = await keep.ExecuteNonQueryAsync(ct) > 0;

        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO notes (id, tab_id, title, content, sort, updated_at, updated_by)
            VALUES ($id, $tab, $title, $content, 0, $now, $by)
            ON CONFLICT(id) DO UPDATE SET title = excluded.title, content = excluded.content,
                updated_at = excluded.updated_at, updated_by = excluded.updated_by
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$tab", tabId);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$content", content);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$by", by);
        await cmd.ExecuteNonQueryAsync(ct);

        if (kept)
        {
            var trim = connection.CreateCommand();
            trim.Transaction = transaction;
            trim.CommandText = TrimSql;
            trim.Parameters.AddWithValue("$id", id);
            trim.Parameters.AddWithValue("$keep", KeepPerNote);
            await trim.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return id;
    }

    /// <summary>
    /// Copies the note as it stands into note_versions — for an edit only when the edit
    /// changes it, so pressing Save twice, or a monthly report rewritten with the same
    /// words, is not a version. For a deletion, <c>$by</c> is who deleted it; for an edit it
    /// is empty and the version says who wrote the text it holds. Public for the query-plan test.
    /// </summary>
    public const string KeepVersionSql = """
        INSERT INTO note_versions (note_id, tab_id, title, content, size, written_at, written_by, kept_at, reason)
        SELECT id, tab_id, title, content, length(content), updated_at,
               CASE WHEN $by = '' THEN updated_by ELSE $by END, $now, $reason
        FROM notes
        WHERE id = $id AND ($reason <> 'edit' OR title IS NOT $title OR content IS NOT $content)
        """;

    /// <summary>
    /// Drops a note's versions beyond the newest <c>$keep</c>: the id of the first one too
    /// many is found by walking ix_note_versions_note backwards, and everything at or below
    /// it goes. Nothing at all while there are fewer.
    /// </summary>
    public const string TrimSql = """
        DELETE FROM note_versions WHERE note_id = $id AND id <= (
            SELECT id FROM note_versions WHERE note_id = $id ORDER BY id DESC LIMIT 1 OFFSET $keep)
        """;

    /// <summary>
    /// Puts a note back exactly as it was, its place in the order and its last-edited
    /// stamp included.
    ///
    /// <see cref="SaveAsync(string?, string, string, string, CancellationToken)"/> cannot do
    /// this and should not: it writes sort 0 and stamps <c>updated_at</c> with now, which is
    /// the truth for an edit. Restoring a notes tab through it would reshuffle every note
    /// into one heap and claim they had all just been written — which is a strange thing for
    /// an undo to do, since undoing is the one operation that is supposed to leave no trace.
    /// </summary>
    public async Task RestoreAsync(Note note, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO notes (id, tab_id, title, content, sort, updated_at, updated_by)
            VALUES ($id, $tab, $title, $content, $sort, $updated, $by)
            ON CONFLICT(id) DO UPDATE SET
                tab_id = excluded.tab_id, title = excluded.title, content = excluded.content,
                sort = excluded.sort, updated_at = excluded.updated_at, updated_by = excluded.updated_by
            """;
        cmd.Parameters.AddWithValue("$id", note.Id);
        cmd.Parameters.AddWithValue("$tab", note.TabId);
        cmd.Parameters.AddWithValue("$title", note.Title);
        cmd.Parameters.AddWithValue("$content", note.Content);
        cmd.Parameters.AddWithValue("$sort", note.Sort);
        cmd.Parameters.AddWithValue("$updated", note.UpdatedAt.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$by", note.UpdatedBy);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task DeleteAsync(string id, CancellationToken ct = default) => DeleteAsync(id, "", ct);

    /// <summary>
    /// Deletes a note, keeping it as a version first so it can be brought back from
    /// "Recently deleted" for <see cref="DeletedFor"/>. <paramref name="by"/> is who deleted it.
    /// </summary>
    public async Task DeleteAsync(string id, string by, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        var keep = connection.CreateCommand();
        keep.Transaction = transaction;
        keep.CommandText = KeepVersionSql;
        keep.Parameters.AddWithValue("$id", id);
        keep.Parameters.AddWithValue("$title", "");
        keep.Parameters.AddWithValue("$content", "");
        keep.Parameters.AddWithValue("$by", by);
        keep.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        keep.Parameters.AddWithValue("$reason", Deleted);
        await keep.ExecuteNonQueryAsync(ct);

        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "DELETE FROM notes WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);

        await transaction.CommitAsync(ct);
    }

    // ---------- history ----------

    /// <summary>A note's versions, newest first, without their text; public for the query-plan test.</summary>
    public const string VersionsSql = """
        SELECT id, note_id, tab_id, title, '', size, written_at, written_by, kept_at, reason
        FROM note_versions WHERE note_id = $id ORDER BY id DESC LIMIT $limit
        """;

    /// <summary>One version, text and all; public for the query-plan test.</summary>
    public const string VersionSql = """
        SELECT id, note_id, tab_id, title, content, size, written_at, written_by, kept_at, reason
        FROM note_versions WHERE id = $id
        """;

    /// <summary>
    /// The notes deleted from one tab recently enough to bring back, newest first, read
    /// through the partial index that holds only deletions. A note that has been brought
    /// back since is not one. Public for the query-plan test.
    /// </summary>
    public const string RecentlyDeletedSql = """
        SELECT v.id, v.note_id, v.tab_id, v.title, '', v.size, v.written_at, v.written_by, v.kept_at, v.reason
        FROM note_versions v
        WHERE v.tab_id = $tab AND v.reason = 'deleted' AND v.kept_at >= $since
          AND NOT EXISTS (SELECT 1 FROM notes n WHERE n.id = v.note_id)
        ORDER BY v.kept_at DESC LIMIT 200
        """;

    private static NoteVersion ReadVersion(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5),
        DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(6)).ToLocalTime(), reader.GetString(7),
        DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(8)).ToLocalTime(), reader.GetString(9));

    /// <summary>The note's earlier versions, newest first, without their text.</summary>
    public async Task<IReadOnlyList<NoteVersion>> VersionsAsync(string noteId, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = VersionsSql;
        cmd.Parameters.AddWithValue("$id", noteId);
        cmd.Parameters.AddWithValue("$limit", KeepPerNote + 1);
        var list = new List<NoteVersion>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(ReadVersion(reader));
        return list;
    }

    /// <summary>One version with its text, or null when it has been pruned since it was listed.</summary>
    public async Task<NoteVersion?> VersionAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = VersionSql;
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadVersion(reader) : null;
    }

    /// <summary>Notes deleted from this tab in the last <see cref="DeletedFor"/>, newest first, one per note.</summary>
    public Task<IReadOnlyList<NoteVersion>> RecentlyDeletedAsync(string tabId, CancellationToken ct = default) =>
        RecentlyDeletedAsync(tabId, DateTimeOffset.UtcNow, ct);

    /// <summary><see cref="RecentlyDeletedAsync(string, CancellationToken)"/> as if it were <paramref name="now"/>, for tests.</summary>
    public async Task<IReadOnlyList<NoteVersion>> RecentlyDeletedAsync(string tabId, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = RecentlyDeletedSql;
        cmd.Parameters.AddWithValue("$tab", tabId);
        cmd.Parameters.AddWithValue("$since", (now - DeletedFor).ToUnixTimeSeconds());
        var list = new List<NoteVersion>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var version = ReadVersion(reader);
            // Deleted, brought back and deleted again: the latest deletion is the one to offer.
            if (seen.Add(version.NoteId))
                list.Add(version);
        }
        return list;
    }

    /// <summary>
    /// Makes a version the note's text again — an earlier version of a note, or a deleted
    /// note brought back under its own id, so its history and its ticks come back with it.
    /// Goes through <see cref="SaveAsync(string?, string, string, string, string, CancellationToken)"/>,
    /// so whatever it replaces becomes a version in turn, and restoring is itself undoable.
    /// The restored text, or null when the version has been pruned since it was listed.
    /// </summary>
    public async Task<NoteVersion?> RestoreVersionAsync(long versionId, string by, CancellationToken ct = default)
    {
        if (await VersionAsync(versionId, ct) is not { } version)
            return null;
        await SaveAsync(version.NoteId, version.TabId, version.Title, version.Content, by, ct);
        return version;
    }

    // ---------- pruning ----------

    /// <summary>Versions older than <see cref="KeepFor"/>, a batch at a time along ix_note_versions_kept.</summary>
    public const string PruneOldSql = """
        DELETE FROM note_versions WHERE id IN (
            SELECT id FROM note_versions WHERE kept_at < $old ORDER BY kept_at LIMIT $batch)
        """;

    /// <summary>
    /// The history of notes that are gone and can no longer be brought back: no deletion of
    /// theirs within <see cref="DeletedFor"/>, and each version itself older than that — so
    /// the notes of a tab deleted a minute ago, which its undo can still put back, keep theirs.
    /// </summary>
    public const string PruneGoneSql = """
        DELETE FROM note_versions WHERE id IN (
            SELECT v.id FROM note_versions v
            WHERE v.kept_at < $gone
              AND NOT EXISTS (SELECT 1 FROM notes n WHERE n.id = v.note_id)
              AND NOT EXISTS (SELECT 1 FROM note_versions d
                              WHERE d.note_id = v.note_id AND d.reason = 'deleted' AND d.kept_at >= $gone)
            ORDER BY v.kept_at LIMIT $batch)
        """;

    /// <summary>Ticks on notes that are gone and have no history left to come back with. A small table, read whole.</summary>
    public const string PruneTicksSql = """
        DELETE FROM note_checks
        WHERE NOT EXISTS (SELECT 1 FROM notes n WHERE n.id = note_checks.note_id)
          AND NOT EXISTS (SELECT 1 FROM note_versions v WHERE v.note_id = note_checks.note_id)
        """;

    public Task<int> PruneAsync(CancellationToken ct = default) => PruneAsync(DateTimeOffset.UtcNow, ct);

    /// <summary>Drops old versions and the history of long-deleted notes, a batch at a time. How many versions went.</summary>
    public async Task<int> PruneAsync(DateTimeOffset now, CancellationToken ct)
    {
        var total = 0;
        await using var connection = await db.OpenAsync(ct);
        foreach (var (sql, name, cut) in new[]
                 {
                     (PruneOldSql, "$old", now - KeepFor),
                     (PruneGoneSql, "$gone", now - DeletedFor),
                 })
        {
            while (true)
            {
                var cmd = connection.CreateCommand();
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue(name, cut.ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("$batch", PruneBatch);
                var deleted = await cmd.ExecuteNonQueryAsync(ct);
                total += deleted;
                if (deleted < PruneBatch)
                    break;
            }
        }

        var ticks = connection.CreateCommand();
        ticks.CommandText = PruneTicksSql;
        await ticks.ExecuteNonQueryAsync(ct);
        return total;
    }
}
