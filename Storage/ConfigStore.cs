using LabbyTwo.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace LabbyTwo.Storage;

/// <summary>
/// Reads and writes the three tables that define the whole app. Everything is cached in
/// memory (a home lab has tens of rows, not thousands) and <see cref="Changed"/> fires on
/// every write so the nav and any open page re-render without a restart.
/// </summary>
public sealed class ConfigStore(Db db, IDataProtectionProvider protection, Registry registry, ILogger<ConfigStore> log)
{
    private readonly IDataProtector _protector = protection.CreateProtector("LabbyTwo.ConnectionSecrets");
    private readonly SemaphoreSlim _lock = new(1, 1);

    private List<Connection>? _connections;
    private List<Tab>? _tabs;
    private List<Widget>? _widgets;

    /// <summary>Raised after any mutation. Components subscribe to refresh themselves.</summary>
    public event Action? Changed;

    private void Invalidate()
    {
        _connections = null;
        _tabs = null;
        _widgets = null;
        Changed?.Invoke();
    }

    // ---------- Connections ----------

    public async Task<IReadOnlyList<Connection>> ConnectionsAsync(CancellationToken ct = default)
    {
        if (_connections is not null)
            return _connections;
        await _lock.WaitAsync(ct);
        try
        {
            await using var connection = await db.OpenAsync(ct);
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT id, provider, name, icon, enabled, sort, settings, alerts FROM connections ORDER BY sort, name";
            var list = new List<Connection>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new Connection
                {
                    Id = reader.GetString(0),
                    Provider = reader.GetString(1),
                    Name = reader.GetString(2),
                    Icon = reader.GetString(3),
                    Enabled = reader.GetInt64(4) != 0,
                    Sort = reader.GetInt32(5),
                    Settings = Decrypt(reader.GetString(1), SettingsBag.FromJson(reader.GetString(6))),
                    AlertsEnabled = reader.GetInt64(7) != 0,
                });
            }
            return _connections = list;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Connection?> ConnectionAsync(string? id, CancellationToken ct = default)
        => id is null ? null : (await ConnectionsAsync(ct)).FirstOrDefault(c => c.Id == id);

    public async Task SaveConnectionAsync(Connection value, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO connections (id, provider, name, icon, enabled, sort, settings, alerts)
            VALUES ($id, $provider, $name, $icon, $enabled, $sort, $settings, $alerts)
            ON CONFLICT(id) DO UPDATE SET
                provider = excluded.provider, name = excluded.name, icon = excluded.icon,
                enabled = excluded.enabled, sort = excluded.sort, settings = excluded.settings,
                alerts = excluded.alerts
            """;
        cmd.Parameters.AddWithValue("$id", value.Id);
        cmd.Parameters.AddWithValue("$provider", value.Provider);
        cmd.Parameters.AddWithValue("$name", value.Name);
        cmd.Parameters.AddWithValue("$icon", value.Icon);
        cmd.Parameters.AddWithValue("$enabled", value.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$sort", value.Sort);
        cmd.Parameters.AddWithValue("$settings", Encrypt(value.Provider, value.Settings).ToJson());
        cmd.Parameters.AddWithValue("$alerts", value.AlertsEnabled ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(ct);
        Invalidate();
    }

    public async Task DeleteConnectionAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        // Widgets bound to it lose the binding rather than vanishing, so the user sees
        // "connection missing" on the card instead of a silently emptier dashboard.
        cmd.CommandText = """
            DELETE FROM connections WHERE id = $id;
            UPDATE widgets SET connection_id = NULL WHERE connection_id = $id;
            DELETE FROM samples WHERE connection_id = $id;
            DELETE FROM status_events WHERE connection_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
        Invalidate();
    }

    // ---------- Tabs ----------

    public async Task<IReadOnlyList<Tab>> TabsAsync(CancellationToken ct = default)
    {
        if (_tabs is not null)
            return _tabs;
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, slug, name, icon, kind, sort, enabled, settings FROM tabs ORDER BY sort, name";
        var list = new List<Tab>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new Tab
            {
                Id = reader.GetString(0),
                Slug = reader.GetString(1),
                Name = reader.GetString(2),
                Icon = reader.GetString(3),
                Kind = reader.GetString(4),
                Sort = reader.GetInt32(5),
                Enabled = reader.GetInt64(6) != 0,
                Settings = SettingsBag.FromJson(reader.GetString(7)),
            });
        }
        return _tabs = list;
    }

    public async Task<Tab?> TabBySlugAsync(string slug, CancellationToken ct = default)
        => (await TabsAsync(ct)).FirstOrDefault(t => string.Equals(t.Slug, slug, StringComparison.OrdinalIgnoreCase));

    public async Task<Tab?> TabAsync(string id, CancellationToken ct = default)
        => (await TabsAsync(ct)).FirstOrDefault(t => t.Id == id);

    public async Task SaveTabAsync(Tab value, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tabs (id, slug, name, icon, kind, sort, enabled, settings)
            VALUES ($id, $slug, $name, $icon, $kind, $sort, $enabled, $settings)
            ON CONFLICT(id) DO UPDATE SET
                slug = excluded.slug, name = excluded.name, icon = excluded.icon,
                kind = excluded.kind, sort = excluded.sort, enabled = excluded.enabled,
                settings = excluded.settings
            """;
        cmd.Parameters.AddWithValue("$id", value.Id);
        cmd.Parameters.AddWithValue("$slug", value.Slug);
        cmd.Parameters.AddWithValue("$name", value.Name);
        cmd.Parameters.AddWithValue("$icon", value.Icon);
        cmd.Parameters.AddWithValue("$kind", value.Kind);
        cmd.Parameters.AddWithValue("$sort", value.Sort);
        cmd.Parameters.AddWithValue("$enabled", value.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$settings", value.Settings.ToJson());
        await cmd.ExecuteNonQueryAsync(ct);
        Invalidate();
    }

    public async Task DeleteTabAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM tabs WHERE id = $id;
            DELETE FROM widgets WHERE tab_id = $id;
            DELETE FROM notes WHERE tab_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
        Invalidate();
    }

    /// <summary>Moves a tab one place up or down in the nav.</summary>
    public async Task ReorderTabAsync(string id, int direction, CancellationToken ct = default)
    {
        var tabs = (await TabsAsync(ct)).OrderBy(t => t.Sort).ThenBy(t => t.Name).ToList();
        var index = tabs.FindIndex(t => t.Id == id);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= tabs.Count)
            return;
        (tabs[index], tabs[target]) = (tabs[target], tabs[index]);
        for (var i = 0; i < tabs.Count; i++)
            await SaveTabAsync(tabs[i] with { Sort = i }, ct);
    }

    /// <summary>Turns a display name into a URL slug that does not collide with an existing tab.</summary>
    public async Task<string> UniqueSlugAsync(string name, string? exceptId = null, CancellationToken ct = default)
    {
        var basis = new string([.. name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')])
            .Trim('-');
        while (basis.Contains("--"))
            basis = basis.Replace("--", "-");
        if (string.IsNullOrWhiteSpace(basis))
            basis = "tab";

        var tabs = await TabsAsync(ct);
        var candidate = basis;
        var suffix = 2;
        while (tabs.Any(t => t.Id != exceptId && string.Equals(t.Slug, candidate, StringComparison.OrdinalIgnoreCase)))
            candidate = $"{basis}-{suffix++}";
        return candidate;
    }

    // ---------- Widgets ----------

    public async Task<IReadOnlyList<Widget>> WidgetsAsync(CancellationToken ct = default)
    {
        if (_widgets is not null)
            return _widgets;
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, tab_id, type, title, connection_id, sort, width, settings FROM widgets ORDER BY sort";
        var list = new List<Widget>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new Widget
            {
                Id = reader.GetString(0),
                TabId = reader.GetString(1),
                Type = reader.GetString(2),
                Title = reader.GetString(3),
                ConnectionId = reader.IsDBNull(4) ? null : reader.GetString(4),
                Sort = reader.GetInt32(5),
                Width = reader.GetInt32(6),
                Settings = SettingsBag.FromJson(reader.GetString(7)),
            });
        }
        return _widgets = list;
    }

    public async Task<IReadOnlyList<Widget>> WidgetsForTabAsync(string tabId, CancellationToken ct = default)
        => [.. (await WidgetsAsync(ct)).Where(w => w.TabId == tabId).OrderBy(w => w.Sort)];

    public async Task SaveWidgetAsync(Widget value, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO widgets (id, tab_id, type, title, connection_id, sort, width, settings)
            VALUES ($id, $tab, $type, $title, $conn, $sort, $width, $settings)
            ON CONFLICT(id) DO UPDATE SET
                tab_id = excluded.tab_id, type = excluded.type, title = excluded.title,
                connection_id = excluded.connection_id, sort = excluded.sort,
                width = excluded.width, settings = excluded.settings
            """;
        cmd.Parameters.AddWithValue("$id", value.Id);
        cmd.Parameters.AddWithValue("$tab", value.TabId);
        cmd.Parameters.AddWithValue("$type", value.Type);
        cmd.Parameters.AddWithValue("$title", value.Title);
        cmd.Parameters.AddWithValue("$conn", (object?)value.ConnectionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sort", value.Sort);
        cmd.Parameters.AddWithValue("$width", value.Width);
        cmd.Parameters.AddWithValue("$settings", value.Settings.ToJson());
        await cmd.ExecuteNonQueryAsync(ct);
        Invalidate();
    }

    public async Task DeleteWidgetAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM widgets WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
        Invalidate();
    }

    public async Task ReorderWidgetAsync(string id, int direction, CancellationToken ct = default)
    {
        var widget = (await WidgetsAsync(ct)).FirstOrDefault(w => w.Id == id);
        if (widget is null)
            return;
        var siblings = (await WidgetsForTabAsync(widget.TabId, ct)).ToList();
        var index = siblings.FindIndex(w => w.Id == id);
        var target = index + direction;
        if (target < 0 || target >= siblings.Count)
            return;
        (siblings[index], siblings[target]) = (siblings[target], siblings[index]);
        for (var i = 0; i < siblings.Count; i++)
            await SaveWidgetAsync(siblings[i] with { Sort = i }, ct);
    }

    /// <summary>
    /// Moves one widget to sit immediately before another on the same tab, which is what
    /// a drag lands on. Renumbers the whole tab rather than nudging one row, so a layout
    /// dragged around for a while cannot drift into ties that reorder themselves later.
    /// </summary>
    public async Task MoveWidgetAsync(string id, string? beforeId, CancellationToken ct = default)
    {
        var widget = (await WidgetsAsync(ct)).FirstOrDefault(w => w.Id == id);
        if (widget is null || id == beforeId)
            return;

        var siblings = (await WidgetsForTabAsync(widget.TabId, ct)).ToList();
        siblings.RemoveAll(w => w.Id == id);

        // A null target means "the end", which is also where an unknown id lands rather
        // than the move being silently dropped.
        var index = beforeId is null ? siblings.Count : siblings.FindIndex(w => w.Id == beforeId);
        siblings.Insert(index < 0 ? siblings.Count : index, widget);

        for (var i = 0; i < siblings.Count; i++)
        {
            if (siblings[i].Sort != i)
                await SaveWidgetAsync(siblings[i] with { Sort = i }, ct);
        }
    }

    /// <summary>Next sort value for a tab, so a new widget lands at the end.</summary>
    public async Task<int> NextWidgetSortAsync(string tabId, CancellationToken ct = default)
        => (await WidgetsForTabAsync(tabId, ct)) is { Count: > 0 } existing ? existing.Max(w => w.Sort) + 1 : 0;

    // ---------- Secrets ----------

    // Password fields are encrypted with the app's data-protection key so a copied
    // database is not a pile of plaintext credentials. Keys live under the data volume,
    // so the DB and its keyring travel together.

    private SettingsBag Encrypt(string providerType, SettingsBag settings)
    {
        var provider = registry.Provider(providerType);
        if (provider is null)
            return settings;
        var result = settings.Clone();
        foreach (var field in provider.Fields.Where(f => f.IsSecret))
        {
            if (result.TryGetValue(field.Key, out var value) && value.Length > 0 && !value.StartsWith(SecretPrefix))
                result[field.Key] = SecretPrefix + _protector.Protect(value);
        }
        return result;
    }

    private SettingsBag Decrypt(string providerType, SettingsBag settings)
    {
        var provider = registry.Provider(providerType);
        if (provider is null)
            return settings;
        foreach (var field in provider.Fields.Where(f => f.IsSecret))
        {
            if (!settings.TryGetValue(field.Key, out var value) || !value.StartsWith(SecretPrefix))
                continue;
            try
            {
                settings[field.Key] = _protector.Unprotect(value[SecretPrefix.Length..]);
            }
            catch (Exception ex)
            {
                // A rotated or lost keyring shouldn't take the whole page down — the
                // connection simply fails to authenticate and the user re-enters it.
                log.LogWarning(ex, "Could not decrypt {Field} for a {Provider} connection", field.Key, providerType);
                settings[field.Key] = "";
            }
        }
        return settings;
    }

    private const string SecretPrefix = "enc:";
}
