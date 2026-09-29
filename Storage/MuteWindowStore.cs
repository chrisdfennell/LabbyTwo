using LabbyTwo.Core;

namespace LabbyTwo.Storage;

/// <summary>
/// The named mute windows. A table of their own rather than an app setting: there can be
/// any number, each is edited on its own, and a backup should carry them the way it
/// carries alert rules.
/// </summary>
public sealed class MuteWindowStore(Db db)
{
    // Versioned so a load cannot store windows from before a save that had already
    // invalidated them — see VersionedCache.
    private readonly VersionedCache<List<MuteWindow>> _cache = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IReadOnlyList<MuteWindow> _last = [];

    public event Action? Changed;

    /// <summary>
    /// The windows as last read, without touching the database. For a page that has to say
    /// "muted by …" while it draws: the alert pass reads the store after every sweep, so
    /// this is at most one sweep behind, and a save refreshes it straight away. Empty only
    /// before the first read, when nothing has had the chance to be muted yet.
    /// </summary>
    public IReadOnlyList<MuteWindow> Current => _cache.Value ?? _last;

    public async Task<IReadOnlyList<MuteWindow>> AllAsync(CancellationToken ct = default)
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
            cmd.CommandText = """
                SELECT id, name, days, start_time, end_time, scope, targets, enabled
                FROM mute_windows ORDER BY name, id
                """;
            var list = new List<MuteWindow>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new MuteWindow
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    Days = MuteWindow.ParseDays(reader.GetString(2)),
                    Start = MuteWindow.ParseTime(reader.GetString(3)),
                    End = MuteWindow.ParseTime(reader.GetString(4)),
                    Scope = MuteWindow.ParseScope(reader.GetString(5)),
                    Targets = EscalationPolicy.ParseChannels(reader.GetString(6)),
                    Enabled = reader.GetInt64(7) != 0,
                });
            }
            _last = list;
            return _cache.Store(list, version);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(MuteWindow window, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO mute_windows (id, name, days, start_time, end_time, scope, targets, enabled)
            VALUES ($id, $name, $days, $start, $end, $scope, $targets, $enabled)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name, days = excluded.days, start_time = excluded.start_time,
                end_time = excluded.end_time, scope = excluded.scope, targets = excluded.targets,
                enabled = excluded.enabled
            """;
        cmd.Parameters.AddWithValue("$id", window.Id);
        cmd.Parameters.AddWithValue("$name", window.Name.Trim());
        cmd.Parameters.AddWithValue("$days", MuteWindow.StoredDays(window.Days));
        cmd.Parameters.AddWithValue("$start", MuteWindow.StoredTime(window.Start));
        cmd.Parameters.AddWithValue("$end", MuteWindow.StoredTime(window.End));
        cmd.Parameters.AddWithValue("$scope", MuteWindow.StoredScope(window.Scope));
        cmd.Parameters.AddWithValue("$targets", window.Scope == MuteScope.Everything ? "" : string.Join(',', window.Targets));
        cmd.Parameters.AddWithValue("$enabled", window.Enabled ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(ct);
        await ReloadAsync(ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM mute_windows WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
        await ReloadAsync(ct);
    }

    /// <summary>
    /// Reads the list again before announcing the change, so <see cref="Current"/> is
    /// already right for whatever redraws on <see cref="Changed"/>.
    /// </summary>
    private async Task ReloadAsync(CancellationToken ct)
    {
        _cache.Invalidate();
        await AllAsync(ct);
        Changed?.Invoke();
    }
}
