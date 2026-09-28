namespace LabbyTwo.Storage;

/// <summary>
/// A tab somebody built once and wants to start from again. The content is a shared-tab
/// file (see <c>ShareTransfer</c>) kept as text: that format already answers what a copy
/// of a tab needs — no ids, connections named rather than pointed at, no credentials — so
/// a template is that file with a name, an icon and a sentence about what it is for.
/// </summary>
public sealed record TabTemplate
{
    public string Id { get; init; } = Core.Ids.New();
    public string Name { get; init; } = "";
    public string Icon { get; init; } = "";
    public string Description { get; init; } = "";

    /// <summary>The captured tab, as a shared-tab JSON file.</summary>
    public string Content { get; init; } = "{}";

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Reads and writes <c>tab_templates</c>. Its own table rather than rows in app_settings:
/// a template is a record with several fields that gets listed, renamed and deleted one at
/// a time, and packing that into key/value pairs would mean parsing every setting in the
/// app to find them.
/// </summary>
public sealed class TemplateStore(Db db)
{
    private readonly VersionedCache<List<TabTemplate>> _cache = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Raised after any write, so the picker and the list redraw.</summary>
    public event Action? Changed;

    public async Task<IReadOnlyList<TabTemplate>> AllAsync(CancellationToken ct = default)
    {
        if (_cache.Value is { } cached)
            return cached;

        await _lock.WaitAsync(ct);
        try
        {
            if (_cache.Value is { } loaded)
                return loaded;

            var version = _cache.Version;
            await using var connection = await db.OpenAsync(ct);
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT id, name, icon, description, content, created_at FROM tab_templates ORDER BY name COLLATE NOCASE";
            var list = new List<TabTemplate>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new TabTemplate
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    Icon = reader.GetString(2),
                    Description = reader.GetString(3),
                    Content = reader.GetString(4),
                    CreatedAt = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)),
                });
            }
            return _cache.Store(list, version);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<TabTemplate?> GetAsync(string id, CancellationToken ct = default)
        => (await AllAsync(ct)).FirstOrDefault(t => t.Id == id);

    public async Task SaveAsync(TabTemplate value, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tab_templates (id, name, icon, description, content, created_at)
            VALUES ($id, $name, $icon, $description, $content, $created)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name, icon = excluded.icon, description = excluded.description,
                content = excluded.content, created_at = excluded.created_at
            """;
        cmd.Parameters.AddWithValue("$id", value.Id);
        cmd.Parameters.AddWithValue("$name", value.Name);
        cmd.Parameters.AddWithValue("$icon", value.Icon);
        cmd.Parameters.AddWithValue("$description", value.Description);
        cmd.Parameters.AddWithValue("$content", value.Content);
        cmd.Parameters.AddWithValue("$created", value.CreatedAt.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct);
        Invalidate();
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM tab_templates WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
        Invalidate();
    }

    private void Invalidate()
    {
        _cache.Invalidate();
        Changed?.Invoke();
    }
}
