using LabbyTwo.Core;

namespace LabbyTwo.Storage;

/// <summary>Markdown notes belonging to a notes tab.</summary>
public sealed class NotesStore(Db db)
{
    public sealed record Note(string Id, string TabId, string Title, string Content, int Sort, DateTimeOffset UpdatedAt);

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

    public async Task<string> SaveAsync(string? id, string tabId, string title, string content, CancellationToken ct = default)
    {
        id ??= Ids.New();
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO notes (id, tab_id, title, content, sort, updated_at)
            VALUES ($id, $tab, $title, $content, 0, $now)
            ON CONFLICT(id) DO UPDATE SET title = excluded.title, content = excluded.content, updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$tab", tabId);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$content", content);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct);
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
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO notes (id, tab_id, title, content, sort, updated_at)
            VALUES ($id, $tab, $title, $content, $sort, $updated)
            ON CONFLICT(id) DO UPDATE SET
                tab_id = excluded.tab_id, title = excluded.title, content = excluded.content,
                sort = excluded.sort, updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$id", note.Id);
        cmd.Parameters.AddWithValue("$tab", note.TabId);
        cmd.Parameters.AddWithValue("$title", note.Title);
        cmd.Parameters.AddWithValue("$content", note.Content);
        cmd.Parameters.AddWithValue("$sort", note.Sort);
        cmd.Parameters.AddWithValue("$updated", note.UpdatedAt.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM notes WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
