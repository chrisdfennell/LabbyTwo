using System.Collections.Concurrent;
using LabbyTwo.Core;

namespace LabbyTwo.Storage;

/// <summary>
/// The ticks on notes' task-list items (see <see cref="Checklists"/>), shared by everybody
/// looking at the note.
///
/// Held in memory a note at a time: the first box drawn for a note reads that note's ticks
/// once — one primary-key range — and every box after it, on every page and every circuit,
/// reads the copy. A tick or a reset writes through and replaces the copy, then raises
/// <see cref="Changed"/>, so a box ticked on one phone is ticked on the wall display a
/// moment later without either asking the database again.
/// </summary>
public sealed class ChecklistStore(Db db)
{
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, ChecklistTick>> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyDictionary<string, ChecklistTick>>>> _loading = new(StringComparer.Ordinal);

    // One writer at a time, so a tick and a reset of the same note cannot each replace the
    // cached copy with one that misses the other. Ticks are clicks; nobody queues behind it.
    private readonly SemaphoreSlim _write = new(1, 1);

    /// <summary>Raised after a note's ticks change, with the note's id.</summary>
    public event Action<string>? Changed;

    /// <summary>The ticks of one note, keyed by item; public for the query-plan test.</summary>
    public const string ForNoteSql = "SELECT item_key, item_text, position, ticked_by, ticked_at FROM note_checks WHERE note_id = $note";

    /// <summary>The note's ticks if they have been read already — never touches the database.</summary>
    public bool TryCached(string noteId, out IReadOnlyDictionary<string, ChecklistTick> ticks) =>
        _cache.TryGetValue(noteId, out ticks!);

    /// <summary>
    /// The note's ticks, read once and then held. Every caller asking for a note that is
    /// still being read waits on the same read, so a checklist of thirty items drawn at once
    /// is one query, not thirty.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ChecklistTick>> TicksAsync(string noteId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(noteId, out var cached))
            return cached;
        var load = _loading.GetOrAdd(noteId, id => new Lazy<Task<IReadOnlyDictionary<string, ChecklistTick>>>(() => ReadAsync(id)));
        try
        {
            var ticks = await load.Value.WaitAsync(ct);
            _cache.TryAdd(noteId, ticks);
            return _cache[noteId];
        }
        finally
        {
            _loading.TryRemove(new KeyValuePair<string, Lazy<Task<IReadOnlyDictionary<string, ChecklistTick>>>>(noteId, load));
        }
    }

    private async Task<IReadOnlyDictionary<string, ChecklistTick>> ReadAsync(string noteId)
    {
        await using var connection = await db.OpenAsync();
        var cmd = connection.CreateCommand();
        cmd.CommandText = ForNoteSql;
        cmd.Parameters.AddWithValue("$note", noteId);
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
    public async Task SetAsync(string noteId, ChecklistItem item, bool ticked, string by, DateTimeOffset at, CancellationToken ct = default)
    {
        await _write.WaitAsync(ct);
        try
        {
            var current = new Dictionary<string, ChecklistTick>(await TicksAsync(noteId, ct), StringComparer.Ordinal);
            await using var connection = await db.OpenAsync(ct);
            var cmd = connection.CreateCommand();
            if (ticked)
            {
                cmd.CommandText = """
                    INSERT INTO note_checks (note_id, item_key, item_text, position, ticked_by, ticked_at)
                    VALUES ($note, $key, $text, $position, $by, $at)
                    ON CONFLICT(note_id, item_key) DO UPDATE SET
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
                cmd.CommandText = "DELETE FROM note_checks WHERE note_id = $note AND item_key = $key";
                current.Remove(item.Key);
            }
            cmd.Parameters.AddWithValue("$note", noteId);
            cmd.Parameters.AddWithValue("$key", item.Key);
            await cmd.ExecuteNonQueryAsync(ct);
            _cache[noteId] = current;
        }
        finally
        {
            _write.Release();
        }
        Changed?.Invoke(noteId);
    }

    /// <summary>Unticks every item in the note. How many were ticked.</summary>
    public async Task<int> ResetAsync(string noteId, CancellationToken ct = default)
    {
        int removed;
        await _write.WaitAsync(ct);
        try
        {
            await using var connection = await db.OpenAsync(ct);
            var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM note_checks WHERE note_id = $note";
            cmd.Parameters.AddWithValue("$note", noteId);
            removed = await cmd.ExecuteNonQueryAsync(ct);
            _cache[noteId] = new Dictionary<string, ChecklistTick>(StringComparer.Ordinal);
        }
        finally
        {
            _write.Release();
        }
        Changed?.Invoke(noteId);
        return removed;
    }

    /// <summary>
    /// After the note's text changed: keeps the ticks <see cref="Checklists.Reconcile"/>
    /// says still belong, under their items' new keys, and drops the rest. Does nothing —
    /// not even a write — when every tick is where it was.
    /// </summary>
    public async Task ReconcileAsync(string noteId, IReadOnlyList<ChecklistItem> items, CancellationToken ct = default)
    {
        var changed = false;
        await _write.WaitAsync(ct);
        try
        {
            var current = await TicksAsync(noteId, ct);
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
            clear.CommandText = "DELETE FROM note_checks WHERE note_id = $note";
            clear.Parameters.AddWithValue("$note", noteId);
            await clear.ExecuteNonQueryAsync(ct);
            foreach (var tick in kept)
            {
                var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT OR REPLACE INTO note_checks (note_id, item_key, item_text, position, ticked_by, ticked_at)
                    VALUES ($note, $key, $text, $position, $by, $at)
                    """;
                insert.Parameters.AddWithValue("$note", noteId);
                insert.Parameters.AddWithValue("$key", tick.Key);
                insert.Parameters.AddWithValue("$text", tick.Text);
                insert.Parameters.AddWithValue("$position", tick.Position);
                insert.Parameters.AddWithValue("$by", tick.By);
                insert.Parameters.AddWithValue("$at", tick.At.ToUnixTimeSeconds());
                await insert.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
            _cache[noteId] = kept.ToDictionary(k => k.Key, StringComparer.Ordinal);
            changed = true;
        }
        finally
        {
            _write.Release();
            if (changed)
                Changed?.Invoke(noteId);
        }
    }
}
