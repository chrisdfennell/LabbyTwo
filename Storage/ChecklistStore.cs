using System.Collections.Concurrent;
using LabbyTwo.Core;

namespace LabbyTwo.Storage;

/// <summary>
/// The ticks on task-list items (see <see cref="Checklists"/>) in anything with Markdown in
/// it — a note, a Markdown card on a dashboard, a Markdown block on a custom page — shared
/// by everybody looking at it.
///
/// Every method takes an owner key (see <see cref="ChecklistOwners"/>) rather than a note
/// id, so the three kinds of owner share one table, one cache and one query, and a card's
/// ticks can never land on a note that happens to have the same id.
///
/// Held in memory an owner at a time: the first box drawn for an owner reads its ticks
/// once — one primary-key range — and every box after it, on every page and every circuit,
/// reads the copy. A tick or a reset writes through and replaces the copy, then raises
/// <see cref="Changed"/>, so a box ticked on one phone is ticked on the wall display a
/// moment later without either asking the database again.
/// </summary>
public sealed class ChecklistStore(Db db)
{
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, ChecklistTick>> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyDictionary<string, ChecklistTick>>>> _loading = new(StringComparer.Ordinal);

    // One writer at a time, so a tick and a reset of the same owner cannot each replace the
    // cached copy with one that misses the other. Ticks are clicks; nobody queues behind it.
    private readonly SemaphoreSlim _write = new(1, 1);

    /// <summary>Raised after an owner's ticks change, with the owner key.</summary>
    public event Action<string>? Changed;

    /// <summary>The ticks of one owner, keyed by item; public for the query-plan test.</summary>
    public const string ForOwnerSql = "SELECT item_key, item_text, position, ticked_by, ticked_at FROM checklist_ticks WHERE owner = $owner";

    /// <summary>The owner's ticks if they have been read already — never touches the database.</summary>
    public bool TryCached(string owner, out IReadOnlyDictionary<string, ChecklistTick> ticks) =>
        _cache.TryGetValue(owner, out ticks!);

    /// <summary>
    /// The owner's ticks, read once and then held. Every caller asking for an owner that is
    /// still being read waits on the same read, so a checklist of thirty items drawn at once
    /// is one query, not thirty.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ChecklistTick>> TicksAsync(string owner, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(owner, out var cached))
            return cached;
        var load = _loading.GetOrAdd(owner, id => new Lazy<Task<IReadOnlyDictionary<string, ChecklistTick>>>(() => ReadAsync(id)));
        try
        {
            var ticks = await load.Value.WaitAsync(ct);
            _cache.TryAdd(owner, ticks);
            return _cache[owner];
        }
        finally
        {
            _loading.TryRemove(new KeyValuePair<string, Lazy<Task<IReadOnlyDictionary<string, ChecklistTick>>>>(owner, load));
        }
    }

    private async Task<IReadOnlyDictionary<string, ChecklistTick>> ReadAsync(string owner)
    {
        await using var connection = await db.OpenAsync();
        var cmd = connection.CreateCommand();
        cmd.CommandText = ForOwnerSql;
        cmd.Parameters.AddWithValue("$owner", owner);
        var ticks = new Dictionary<string, ChecklistTick>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var key = reader.GetString(0);
            ticks[key] = new ChecklistTick(key, reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4)));
        }
        return ticks;
    }

    /// <summary>Ticks or unticks one item, for everybody.</summary>
    public async Task SetAsync(string owner, ChecklistItem item, bool ticked, string by, DateTimeOffset at, CancellationToken ct = default)
    {
        await _write.WaitAsync(ct);
        try
        {
            var current = new Dictionary<string, ChecklistTick>(await TicksAsync(owner, ct), StringComparer.Ordinal);
            await using var connection = await db.OpenAsync(ct);
            var cmd = connection.CreateCommand();
            if (ticked)
            {
                cmd.CommandText = """
                    INSERT INTO checklist_ticks (owner, item_key, item_text, position, ticked_by, ticked_at)
                    VALUES ($owner, $key, $text, $position, $by, $at)
                    ON CONFLICT(owner, item_key) DO UPDATE SET
                        item_text = excluded.item_text, position = excluded.position,
                        ticked_by = excluded.ticked_by, ticked_at = excluded.ticked_at
                    """;
                cmd.Parameters.AddWithValue("$text", item.Text);
                cmd.Parameters.AddWithValue("$position", item.Position);
                cmd.Parameters.AddWithValue("$by", by);
                cmd.Parameters.AddWithValue("$at", at.ToUnixTimeSeconds());
                current[item.Key] = new ChecklistTick(item.Key, item.Text, item.Position, by, DateTimeOffset.FromUnixTimeSeconds(at.ToUnixTimeSeconds()));
            }
            else
            {
                cmd.CommandText = "DELETE FROM checklist_ticks WHERE owner = $owner AND item_key = $key";
                current.Remove(item.Key);
            }
            cmd.Parameters.AddWithValue("$owner", owner);
            cmd.Parameters.AddWithValue("$key", item.Key);
            await cmd.ExecuteNonQueryAsync(ct);
            _cache[owner] = current;
        }
        finally
        {
            _write.Release();
        }
        Changed?.Invoke(owner);
    }

    /// <summary>Unticks every item the owner has. How many were ticked.</summary>
    public async Task<int> ResetAsync(string owner, CancellationToken ct = default)
    {
        int removed;
        await _write.WaitAsync(ct);
        try
        {
            await using var connection = await db.OpenAsync(ct);
            var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM checklist_ticks WHERE owner = $owner";
            cmd.Parameters.AddWithValue("$owner", owner);
            removed = await cmd.ExecuteNonQueryAsync(ct);
            _cache[owner] = new Dictionary<string, ChecklistTick>(StringComparer.Ordinal);
        }
        finally
        {
            _write.Release();
        }
        Changed?.Invoke(owner);
        return removed;
    }

    /// <summary>
    /// After the owner's text changed: keeps the ticks <see cref="Checklists.Reconcile"/>
    /// says still belong, under their items' new keys, and drops the rest. Does nothing —
    /// not even a write — when every tick is where it was.
    /// </summary>
    public async Task ReconcileAsync(string owner, IReadOnlyList<ChecklistItem> items, CancellationToken ct = default)
    {
        var changed = false;
        await _write.WaitAsync(ct);
        try
        {
            var current = await TicksAsync(owner, ct);
            if (current.Count == 0)
                return;
            var kept = Checklists.Reconcile(items, current.Values.ToList());
            if (kept.Count == current.Count && kept.All(k => current.TryGetValue(k.Key, out var was)
                    && was.Text == k.Text && was.Position == k.Position))
                return;

            await using var connection = await db.OpenAsync(ct);
            await using var transaction = connection.BeginTransaction();
            var clear = connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM checklist_ticks WHERE owner = $owner";
            clear.Parameters.AddWithValue("$owner", owner);
            await clear.ExecuteNonQueryAsync(ct);
            foreach (var tick in kept)
            {
                var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT OR REPLACE INTO checklist_ticks (owner, item_key, item_text, position, ticked_by, ticked_at)
                    VALUES ($owner, $key, $text, $position, $by, $at)
                    """;
                insert.Parameters.AddWithValue("$owner", owner);
                insert.Parameters.AddWithValue("$key", tick.Key);
                insert.Parameters.AddWithValue("$text", tick.Text);
                insert.Parameters.AddWithValue("$position", tick.Position);
                insert.Parameters.AddWithValue("$by", tick.By);
                insert.Parameters.AddWithValue("$at", tick.At.ToUnixTimeSeconds());
                await insert.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
            _cache[owner] = kept.ToDictionary(k => k.Key, StringComparer.Ordinal);
            changed = true;
        }
        finally
        {
            _write.Release();
            if (changed)
                Changed?.Invoke(owner);
        }
    }
}
