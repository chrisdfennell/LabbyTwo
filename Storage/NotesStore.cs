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

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM notes WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
