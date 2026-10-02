using LabbyTwo.Core;

namespace LabbyTwo.Storage;

/// <param name="SavedBy">The signed-in name, or empty on an install with no login.</param>
/// <param name="Note">Why it was saved, when it was not typed — "Restored the version from …".</param>
public sealed record CustomCssVersion(long Id, string Css, string SavedBy, DateTimeOffset SavedAt, string Note);

/// <summary>
/// The last <see cref="CustomCss.HistoryLimit"/> saves of the custom stylesheet, newest first.
///
/// Only the history lives here. The stylesheet in use is an app setting, so every page reads
/// it from the settings cache and never from this table; this is only read when somebody
/// opens Appearance and looks back. Kept short on purpose: it is a way back from a bad
/// edit, not an archive, and twenty copies of a 100 KB stylesheet is already two megabytes.
/// </summary>
public sealed class CustomCssStore(Db db)
{
    /// <summary>The history, newest first; public for the query-plan test.</summary>
    public const string ListSql = "SELECT id, css, saved_by, saved_at, note FROM custom_css_versions ORDER BY id DESC LIMIT $limit";

    public async Task<IReadOnlyList<CustomCssVersion>> ListAsync(CancellationToken ct = default)
    {
        var list = new List<CustomCssVersion>();
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = ListSql;
        cmd.Parameters.AddWithValue("$limit", CustomCss.HistoryLimit);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new CustomCssVersion(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)),
                reader.GetString(4)));
        }
        return list;
    }

    public async Task<CustomCssVersion?> FindAsync(long id, CancellationToken ct = default) =>
        (await ListAsync(ct)).FirstOrDefault(v => v.Id == id);

    /// <summary>Records a save and forgets whatever has fallen off the end, in one transaction.</summary>
    public async Task<long> AddAsync(string css, string savedBy, string note, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO custom_css_versions (css, saved_by, saved_at, note) VALUES ($css, $by, $at, $note);
            SELECT last_insert_rowid();
            """;
        insert.Parameters.AddWithValue("$css", css);
        insert.Parameters.AddWithValue("$by", savedBy);
        insert.Parameters.AddWithValue("$at", at.ToUnixTimeSeconds());
        insert.Parameters.AddWithValue("$note", note);
        var id = Convert.ToInt64(await insert.ExecuteScalarAsync(ct));

        var prune = connection.CreateCommand();
        prune.CommandText = """
            DELETE FROM custom_css_versions
            WHERE id NOT IN (SELECT id FROM custom_css_versions ORDER BY id DESC LIMIT $limit)
            """;
        prune.Parameters.AddWithValue("$limit", CustomCss.HistoryLimit);
        await prune.ExecuteNonQueryAsync(ct);

        await transaction.CommitAsync(ct);
        return id;
    }
}
