using System.Runtime.CompilerServices;

namespace LabbyTwo.Storage;

/// <summary>
/// A "something's broken" report sent from the family status page.
/// </summary>
/// <param name="ItemName">What the family picked, as the page named it then — or "Something else".</param>
/// <param name="Message">What they typed. Plain text, already cleaned; never rendered as anything else.</param>
/// <param name="Reporter">The name they gave, or empty.</param>
public sealed record FamilyReport(
    long Id,
    DateTimeOffset At,
    string ItemId,
    string? ConnectionId,
    string ItemName,
    string Message,
    string Reporter);

/// <summary>
/// The reports waiting for the owner. Dismissing one deletes it — the change feed keeps
/// the history, so there is nothing left for a dismissed row to be for.
///
/// Small by construction: the page's rate limits allow a handful an hour, and
/// <see cref="MaxKept"/> is a ceiling beneath even that, so a forgotten install with a
/// leaked link cannot fill a disk one report at a time.
/// </summary>
public sealed class FamilyReportStore(Db db)
{
    /// <summary>Past this many waiting, the oldest go as new ones arrive.</summary>
    public const int MaxKept = 200;

    /// <summary>Raised after anything is added or dismissed, for the nav badge and the settings list.</summary>
    public event Action? Changed;

    public async Task<FamilyReport> AddAsync(FamilyReport report, CancellationToken ct = default)
    {
        long id;
        await using (var connection = await db.OpenAsync(ct))
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO family_reports (ts, item_id, connection_id, item_name, message, reporter)
                VALUES ($ts, $item, $connection, $name, $message, $reporter)
                RETURNING id
                """;
            cmd.Parameters.AddWithValue("$ts", report.At.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$item", report.ItemId);
            cmd.Parameters.AddWithValue("$connection", (object?)report.ConnectionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$name", report.ItemName);
            cmd.Parameters.AddWithValue("$message", report.Message);
            cmd.Parameters.AddWithValue("$reporter", report.Reporter);
            id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));

            var trim = connection.CreateCommand();
            trim.CommandText = """
                DELETE FROM family_reports WHERE id NOT IN (
                    SELECT id FROM family_reports ORDER BY ts DESC, id DESC LIMIT $keep)
                """;
            trim.Parameters.AddWithValue("$keep", MaxKept);
            await trim.ExecuteNonQueryAsync(ct);
        }

        Raise();
        return report with { Id = id };
    }

    /// <summary>The waiting reports, newest first.</summary>
    public async Task<IReadOnlyList<FamilyReport>> ListAsync(int limit = 100, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, ts, item_id, connection_id, item_name, message, reporter
            FROM family_reports ORDER BY ts DESC, id DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, MaxKept));
        var list = new List<FamilyReport>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new FamilyReport(
                reader.GetInt64(0),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)).ToLocalTime(),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6)));
        }
        return list;
    }

    private readonly VersionedCache<StrongBox<int>> _count = new();

    /// <summary>
    /// How many are waiting, if it has been read since the last change: what the nav badge
    /// shows while a page is drawn, so drawing never waits for the database. Null until
    /// <see cref="CountAsync"/> has run once, and again after every add or dismissal.
    /// </summary>
    public int? KnownCount => _count.Value?.Value;

    /// <summary>
    /// How many are waiting. Read once and then kept until something is added or dismissed,
    /// because the nav asks on every page and the answer changes a few times a week.
    /// </summary>
    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        if (_count.Value is { } known)
            return known.Value;
        var version = _count.Version;
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM family_reports";
        return _count.Store(new StrongBox<int>(Convert.ToInt32(await cmd.ExecuteScalarAsync(ct))), version).Value;
    }

    private void Raise()
    {
        _count.Invalidate();
        Changed?.Invoke();
    }

    public async Task DismissAsync(long id, CancellationToken ct = default)
    {
        await using (var connection = await db.OpenAsync(ct))
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM family_reports WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        Raise();
    }

    public async Task DismissAllAsync(CancellationToken ct = default)
    {
        await using (var connection = await db.OpenAsync(ct))
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM family_reports";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        Raise();
    }
}
