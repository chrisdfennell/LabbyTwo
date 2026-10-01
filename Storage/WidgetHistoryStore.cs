using System.Text.Json;
using LabbyTwo.Core;
using Microsoft.Data.Sqlite;

namespace LabbyTwo.Storage;

/// <summary>
/// What a Markdown card said before — on a dashboard, or as a Markdown block on a custom
/// page, which is the same widget row (see <see cref="PageBlocks"/>).
///
/// The notes' history (<see cref="NotesStore"/>) for cards, with the same rules: every save
/// that changes the card's text first copies what it replaces into widget_versions, in the
/// same transaction as the write (<see cref="KeepAsync"/>, called by
/// <see cref="ConfigStore"/>, which owns the write); deleting a card keeps it there too, the
/// whole card, so "Recently deleted" can put it back; kept to <see cref="KeepPer"/> versions
/// a card and <see cref="KeepFor"/> by age, and a deleted card's history goes
/// <see cref="DeletedFor"/> after it was deleted.
///
/// A sibling table rather than more rows in note_versions: a card is one setting inside a
/// row of a cached table, with its own undo, duplicate and export, and the "is it gone yet"
/// question for pruning is asked of widgets, not notes. Sharing a table would mean every
/// note query and every note prune learning to step round cards. The pieces that are the
/// same — the history view, the line diff, the caps — are shared instead.
///
/// Only the text is versioned. Moving, resizing and retitling a card are layout, saved a
/// dozen times in a drag, and none of them is a version: <see cref="KeepVersionSql"/>
/// compares the text and nothing else.
/// </summary>
public sealed class WidgetHistoryStore(Db db, ConfigStore config)
{
    /// <summary>The widget type whose text is versioned: "Text / Markdown".</summary>
    public const string MarkdownType = "markdown";

    /// <summary>The setting the Markdown is kept in.</summary>
    public const string ContentKey = "content";

    public const string Edited = NotesStore.Edited;
    public const string Deleted = NotesStore.Deleted;

    /// <summary>Versions kept for each card — the same as for a note.</summary>
    public const int KeepPer = NotesStore.KeepPerNote;

    public static readonly TimeSpan KeepFor = NotesStore.KeepFor;
    public static readonly TimeSpan DeletedFor = NotesStore.DeletedFor;

    private const int PruneBatch = 2000;

    /// <summary>Whether this widget's saves keep versions of its text.</summary>
    public static bool Keeps(Widget widget) => widget.Type == MarkdownType;

    /// <summary>What a card's text was at one time.</summary>
    /// <param name="Content">Empty in a listing, which never reads the text.</param>
    /// <param name="WrittenAt">When this text was saved, or the epoch when that was before history was kept.</param>
    /// <param name="WrittenBy">Who saved it, when known.</param>
    /// <param name="KeptAt">When it was replaced, or the card deleted.</param>
    /// <param name="KeptBy">Who replaced it, or deleted the card.</param>
    /// <param name="Card">For a deletion, the whole card as JSON, to put it back from; otherwise empty.</param>
    public sealed record WidgetVersion(
        long Id, string WidgetId, string TabId, string Title, string Content, int Size,
        DateTimeOffset WrittenAt, string WrittenBy, DateTimeOffset KeptAt, string KeptBy, string Reason, string Card)
    {
        public bool IsDeletion => Reason == Deleted;

        /// <summary>False for text saved before anything was recorded about it.</summary>
        public bool WrittenKnown => WrittenAt.ToUnixTimeSeconds() > 0;
    }

    // ---------- keeping ----------

    /// <summary>
    /// Copies the card's text as it stands into widget_versions — for an edit only when the
    /// edit changes the text, so a resize, a move or pressing Save twice is not a version.
    /// Who wrote the text it holds is whoever replaced the one before it, which the newest
    /// edit already recorded (kept_by), so the widgets table needs no column for it. A
    /// deletion keeps the whole card too. Public for the query-plan test.
    /// </summary>
    public const string KeepVersionSql = """
        INSERT INTO widget_versions (widget_id, tab_id, title, content, size, written_at, written_by, kept_at, kept_by, reason, card)
        SELECT w.id, w.tab_id, w.title, ifnull(json_extract(w.settings, '$.content'), ''),
               length(ifnull(json_extract(w.settings, '$.content'), '')),
               ifnull(e.kept_at, 0), ifnull(e.kept_by, ''), $now, $by, $reason,
               CASE WHEN $reason = 'deleted'
                    THEN json_object('type', w.type, 'title', w.title, 'connection_id', w.connection_id,
                                     'sort', w.sort, 'width', w.width, 'height', w.height, 'settings', json(w.settings))
                    ELSE '' END
        FROM widgets w
        LEFT JOIN (SELECT kept_at, kept_by FROM widget_versions
                   WHERE widget_id = $id AND reason = 'edit' ORDER BY id DESC LIMIT 1) e ON 1
        WHERE w.id = $id AND w.type = 'markdown'
          AND ($reason <> 'edit' OR ifnull(json_extract(w.settings, '$.content'), '') IS NOT $content)
        """;

    /// <summary>Drops a card's versions beyond the newest <c>$keep</c>, as <see cref="NotesStore.TrimSql"/> does for a note.</summary>
    public const string TrimSql = """
        DELETE FROM widget_versions WHERE widget_id = $id AND id <= (
            SELECT id FROM widget_versions WHERE widget_id = $id ORDER BY id DESC LIMIT 1 OFFSET $keep)
        """;

    /// <summary>
    /// Inside the transaction writing <paramref name="widget"/> (or deleting it, with
    /// <paramref name="reason"/> <see cref="Deleted"/>), before the write: keeps what the
    /// card says now when this changes it, and trims the card to <see cref="KeepPer"/>.
    /// Whether a version was kept — that is, whether the text changed.
    /// </summary>
    internal static async Task<bool> KeepAsync(SqliteConnection connection, SqliteTransaction transaction,
        string widgetId, string content, string by, string reason, CancellationToken ct)
    {
        var keep = connection.CreateCommand();
        keep.Transaction = transaction;
        keep.CommandText = KeepVersionSql;
        keep.Parameters.AddWithValue("$id", widgetId);
        keep.Parameters.AddWithValue("$content", content);
        keep.Parameters.AddWithValue("$by", by);
        keep.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        keep.Parameters.AddWithValue("$reason", reason);
        if (await keep.ExecuteNonQueryAsync(ct) == 0)
            return false;

        var trim = connection.CreateCommand();
        trim.Transaction = transaction;
        trim.CommandText = TrimSql;
        trim.Parameters.AddWithValue("$id", widgetId);
        trim.Parameters.AddWithValue("$keep", KeepPer);
        await trim.ExecuteNonQueryAsync(ct);
        return true;
    }

    // ---------- reading ----------

    /// <summary>A card's versions, newest first, without their text; public for the query-plan test.</summary>
    public const string VersionsSql = """
        SELECT id, widget_id, tab_id, title, '', size, written_at, written_by, kept_at, kept_by, reason, ''
        FROM widget_versions WHERE widget_id = $id ORDER BY id DESC LIMIT $limit
        """;

    /// <summary>One version, text and all; public for the query-plan test.</summary>
    public const string VersionSql = """
        SELECT id, widget_id, tab_id, title, content, size, written_at, written_by, kept_at, kept_by, reason, card
        FROM widget_versions WHERE id = $id
        """;

    /// <summary>
    /// The cards deleted from one tab recently enough to bring back, newest first, through
    /// the partial index that holds only deletions. A card that is back — restored, or its
    /// delete undone — is not one. Public for the query-plan test.
    /// </summary>
    public const string RecentlyDeletedSql = """
        SELECT v.id, v.widget_id, v.tab_id, v.title, '', v.size, v.written_at, v.written_by, v.kept_at, v.kept_by, v.reason, ''
        FROM widget_versions v
        WHERE v.tab_id = $tab AND v.reason = 'deleted' AND v.kept_at >= $since
          AND NOT EXISTS (SELECT 1 FROM widgets w WHERE w.id = v.widget_id)
        ORDER BY v.kept_at DESC LIMIT 200
        """;

    private static WidgetVersion ReadVersion(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5),
        DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(6)).ToLocalTime(), reader.GetString(7),
        DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(8)).ToLocalTime(), reader.GetString(9), reader.GetString(10),
        reader.GetString(11));

    /// <summary>The card's earlier versions, newest first, without their text.</summary>
    public async Task<IReadOnlyList<WidgetVersion>> VersionsAsync(string widgetId, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = VersionsSql;
        cmd.Parameters.AddWithValue("$id", widgetId);
        cmd.Parameters.AddWithValue("$limit", KeepPer + 1);
        var list = new List<WidgetVersion>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(ReadVersion(reader));
        return list;
    }

    /// <summary>One version with its text, or null when it has been pruned since it was listed.</summary>
    public async Task<WidgetVersion?> VersionAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = VersionSql;
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadVersion(reader) : null;
    }

    /// <summary>Cards deleted from this tab in the last <see cref="DeletedFor"/>, newest first, one per card.</summary>
    public Task<IReadOnlyList<WidgetVersion>> RecentlyDeletedAsync(string tabId, CancellationToken ct = default) =>
        RecentlyDeletedAsync(tabId, DateTimeOffset.UtcNow, ct);

    /// <summary><see cref="RecentlyDeletedAsync(string, CancellationToken)"/> as if it were <paramref name="now"/>, for tests.</summary>
    public async Task<IReadOnlyList<WidgetVersion>> RecentlyDeletedAsync(string tabId, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = RecentlyDeletedSql;
        cmd.Parameters.AddWithValue("$tab", tabId);
        cmd.Parameters.AddWithValue("$since", (now - DeletedFor).ToUnixTimeSeconds());
        var list = new List<WidgetVersion>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var version = ReadVersion(reader);
            if (seen.Add(version.WidgetId))
                list.Add(version);
        }
        return list;
    }

    // ---------- restoring ----------

    /// <summary>
    /// Makes a version the card's text again, through <see cref="ConfigStore.SaveWidgetAsync(Widget, string, CancellationToken)"/>
    /// — so what it replaces becomes a version in turn and restoring is itself undoable, and
    /// the ticks are carried over as for any edit. Only the text: the card keeps the title,
    /// size and place it has now, since those were never part of its history.
    ///
    /// A deletion brings the whole card back under its own id — its history and its ticks
    /// with it — on its tab, in the place it was. Null when the
    /// version has been pruned since it was listed, or its card is gone and the version is
    /// not a deletion, so there is nothing to put the text back into.
    /// </summary>
    public async Task<Widget?> RestoreVersionAsync(long versionId, string by, CancellationToken ct = default)
    {
        if (await VersionAsync(versionId, ct) is not { } version)
            return null;

        var current = (await config.WidgetsAsync(ct)).FirstOrDefault(w => w.Id == version.WidgetId);
        Widget restored;
        if (current is not null)
        {
            var settings = current.Settings.Clone();
            settings[ContentKey] = version.Content;
            restored = current with { Settings = settings };
        }
        else if (version.IsDeletion && Card(version) is { } card)
        {
            restored = card;
        }
        else
        {
            return null;
        }

        await config.SaveWidgetAsync(restored, by, ct);
        return restored;
    }

    /// <summary>The card a deletion kept, rebuilt; null if what was kept cannot be read.</summary>
    private static Widget? Card(WidgetVersion version)
    {
        try
        {
            using var json = JsonDocument.Parse(version.Card);
            var root = json.RootElement;
            return new Widget
            {
                Id = version.WidgetId,
                TabId = version.TabId,
                Type = root.GetProperty("type").GetString() ?? MarkdownType,
                Title = root.GetProperty("title").GetString() ?? "",
                ConnectionId = root.TryGetProperty("connection_id", out var connection) && connection.ValueKind == JsonValueKind.String
                    ? connection.GetString() : null,
                Sort = root.GetProperty("sort").GetInt32(),
                Width = root.GetProperty("width").GetInt32(),
                Height = root.GetProperty("height").GetInt32(),
                Settings = SettingsBag.FromJson(root.GetProperty("settings").GetRawText()),
            };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    // ---------- pruning ----------

    /// <summary>Versions older than <see cref="KeepFor"/>, a batch at a time along ix_widget_versions_kept.</summary>
    public const string PruneOldSql = """
        DELETE FROM widget_versions WHERE id IN (
            SELECT id FROM widget_versions WHERE kept_at < $old ORDER BY kept_at LIMIT $batch)
        """;

    /// <summary>
    /// The history of cards that are gone and can no longer be brought back: no deletion of
    /// theirs within <see cref="DeletedFor"/>, and each version itself older than that — so
    /// the cards of a tab deleted a minute ago, which its undo can still put back, keep theirs.
    /// </summary>
    public const string PruneGoneSql = """
        DELETE FROM widget_versions WHERE id IN (
            SELECT v.id FROM widget_versions v
            WHERE v.kept_at < $gone
              AND NOT EXISTS (SELECT 1 FROM widgets w WHERE w.id = v.widget_id)
              AND NOT EXISTS (SELECT 1 FROM widget_versions d
                              WHERE d.widget_id = v.widget_id AND d.reason = 'deleted' AND d.kept_at >= $gone)
            ORDER BY v.kept_at LIMIT $batch)
        """;

    /// <summary>
    /// Ticks on cards that are gone and have no history left to come back with: the
    /// "widget:" range of checklist_ticks, along its primary key.
    /// </summary>
    public const string PruneTicksSql = """
        DELETE FROM checklist_ticks
        WHERE owner >= 'widget:' AND owner < 'widget;'
          AND NOT EXISTS (SELECT 1 FROM widgets w WHERE w.id = substr(checklist_ticks.owner, 8))
          AND NOT EXISTS (SELECT 1 FROM widget_versions v WHERE v.widget_id = substr(checklist_ticks.owner, 8))
        """;

    public Task<int> PruneAsync(CancellationToken ct = default) => PruneAsync(DateTimeOffset.UtcNow, ct);

    /// <summary>Drops old versions and the history of long-deleted cards, a batch at a time. How many versions went.</summary>
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
