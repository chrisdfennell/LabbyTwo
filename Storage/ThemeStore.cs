using LabbyTwo.Core;

namespace LabbyTwo.Storage;

/// <summary>
/// Themes the user made. A handful of rows, read whole and held in memory until one is
/// written — the same shape as <see cref="NoteTemplateStore"/> — so resolving the active
/// theme on a page render never touches the database.
///
/// Each row is the theme's export JSON (<see cref="ThemeJson"/>), and every row is put back
/// through the importer's validation when it is read. A row that fails — edited by hand,
/// written by a future version — is skipped rather than trusted, and the page falls back to
/// the default theme instead of drawing with half a palette.
/// </summary>
public sealed class ThemeStore(Db db, ILogger<ThemeStore> log)
{
    public const string IdPrefix = "user-";

    /// <summary>Every user theme, by name; public for the query-plan test.</summary>
    public const string AllSql = "SELECT id, json FROM user_themes ORDER BY name COLLATE NOCASE";

    private readonly VersionedCache<List<Theme>> _cache = new();

    /// <summary>Raised after any write, so the active theme is re-resolved and open galleries redraw.</summary>
    public event Action? Changed;

    public async Task<IReadOnlyList<Theme>> AllAsync(CancellationToken ct = default)
    {
        if (_cache.Value is { } cached)
            return cached;

        var version = _cache.Version;
        var list = new List<Theme>();
        await using (var connection = await db.OpenAsync(ct))
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText = AllSql;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetString(0);
                var parsed = ThemeJson.Import(reader.GetString(1));
                if (parsed.Theme is { } theme)
                    list.Add(theme with { Id = id });
                else
                    log.LogWarning("Saved theme {Id} no longer validates and is ignored: {Reasons}", id, string.Join("; ", parsed.Errors));
            }
        }
        return _cache.Store(list, version);
    }

    public async Task<Theme?> FindAsync(string id, CancellationToken ct = default) =>
        (await AllAsync(ct)).FirstOrDefault(t => t.Id == id);

    /// <summary>
    /// Saves a theme and returns its id. A theme without one — new, imported, customised from
    /// a built-in — gets a fresh id; only an id that already belongs to a user theme is
    /// overwritten, so nothing can be saved over a built-in.
    /// </summary>
    public async Task<string> SaveAsync(Theme theme, CancellationToken ct = default)
    {
        var id = theme.Id.StartsWith(IdPrefix, StringComparison.Ordinal) && theme.Id.Length <= 40
            ? theme.Id
            : IdPrefix + Ids.New();
        var name = ThemeJson.CleanText(theme.Name) is { Length: > 0 } named ? named : "Untitled theme";
        if (name.Length > ThemeJson.MaxName)
            name = name[..ThemeJson.MaxName];
        var json = ThemeJson.Export(theme with { Name = name, BuiltIn = false });

        await using (var connection = await db.OpenAsync(ct))
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO user_themes (id, name, json, updated_at) VALUES ($id, $name, $json, $now)
                ON CONFLICT(id) DO UPDATE SET name = excluded.name, json = excluded.json, updated_at = excluded.updated_at
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$json", json);
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
            cmd.CommandText = "DELETE FROM user_themes WHERE id = $id";
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
