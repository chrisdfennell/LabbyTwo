using LabbyTwo.Core;

namespace LabbyTwo.Storage;

/// <summary>
/// A note somebody saved as a template: "New note from template…" starts a note with this
/// title and this Markdown, exactly as written — shortcodes and all, since a template of a
/// runbook is mostly shortcodes.
/// </summary>
public sealed record NoteTemplate(string Id, string Name, string Title, string Content, DateTimeOffset UpdatedAt);

/// <summary>
/// The user's own note templates. A handful of rows, read whole and held until one is
/// written, like <see cref="TemplateStore"/> holds the tab templates.
/// </summary>
public sealed class NoteTemplateStore(Db db)
{
    private readonly VersionedCache<List<NoteTemplate>> _cache = new();

    /// <summary>Every template, by name; public for the query-plan test.</summary>
    public const string AllSql = "SELECT id, name, title, content, updated_at FROM note_templates ORDER BY name COLLATE NOCASE";

    /// <summary>Raised after any write, so an open picker redraws.</summary>
    public event Action? Changed;

    public async Task<IReadOnlyList<NoteTemplate>> AllAsync(CancellationToken ct = default)
    {
        if (_cache.Value is { } cached)
            return cached;

        var version = _cache.Version;
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = AllSql;
        var list = new List<NoteTemplate>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new NoteTemplate(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4))));
        }
        return _cache.Store(list, version);
    }

    /// <summary>Writes a template, new or edited, and returns its id. A blank name is called after the title.</summary>
    public async Task<string> SaveAsync(string? id, string name, string title, string content, CancellationToken ct = default)
    {
        id ??= Ids.New();
        name = name.Trim() is { Length: > 0 } named ? named : title.Trim() is { Length: > 0 } titled ? titled : "Untitled template";
        await using (var connection = await db.OpenAsync(ct))
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO note_templates (id, name, title, content, updated_at)
                VALUES ($id, $name, $title, $content, $now)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name, title = excluded.title, content = excluded.content, updated_at = excluded.updated_at
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$title", title);
            cmd.Parameters.AddWithValue("$content", content);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        Invalidate();
        return id;
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using (var connection = await db.OpenAsync(ct))
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM note_templates WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        Invalidate();
    }

    private void Invalidate()
    {
        _cache.Invalidate();
        Changed?.Invoke();
    }
}
