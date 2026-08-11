using LabbyTwo.Storage;
using Microsoft.Data.Sqlite;

namespace LabbyTwo.DropPlugin;

/// <summary>
/// A shared shelf. Files and pasted text land here from one device and are picked up from
/// another — the thing everyone improvises with email drafts and chat messages to
/// themselves.
///
/// Metadata goes in the host's database so it is inside every backup; the file bytes go in
/// a folder beside it, because a few gigabytes of holiday video has no business being in
/// a SQLite row that the dashboard reads on every page load.
/// </summary>
public sealed class DropStore(Db db)
{
    public sealed record Drop(
        string Id,
        string Name,
        string Kind,
        string Text,
        long Size,
        string ContentType,
        DateTimeOffset Created,
        DateTimeOffset? Expires)
    {
        public bool IsText => Kind == "text";

        public bool HasExpired(DateTimeOffset now) => Expires is { } when && when <= now;

        public string ExpiryLabel(DateTimeOffset now)
        {
            if (Expires is not { } when)
                return "kept";

            var left = when - now;
            return left.TotalSeconds <= 0 ? "expired"
                : left.TotalHours < 1 ? $"{left.TotalMinutes:0} min left"
                : left.TotalHours < 48 ? $"{left.TotalHours:0} h left"
                : $"{left.TotalDays:0} days left";
        }

        public string SizeLabel
        {
            get
            {
                string[] units = ["B", "KB", "MB", "GB"];
                double size = Size;
                var unit = 0;
                while (size >= 1024 && unit < units.Length - 1)
                {
                    size /= 1024;
                    unit++;
                }
                return unit == 0 ? $"{Size} B" : $"{size:0.#} {units[unit]}";
            }
        }
    }

    /// <summary>Beside the database, so the data volume holds everything and one backup covers it.</summary>
    public string Folder => Path.Combine(Path.GetDirectoryName(db.FilePath)!, "drop");

    public string PathFor(string id) => Path.Combine(Folder, id);

    private bool _ready;

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = await db.OpenAsync(ct);

        if (!_ready)
        {
            Directory.CreateDirectory(Folder);

            await using var create = connection.CreateCommand();
            create.CommandText =
                """
                CREATE TABLE IF NOT EXISTS plugin_drops (
                    id           TEXT PRIMARY KEY,
                    name         TEXT NOT NULL,
                    kind         TEXT NOT NULL,
                    text         TEXT NOT NULL DEFAULT '',
                    size         INTEGER NOT NULL DEFAULT 0,
                    content_type TEXT NOT NULL DEFAULT '',
                    created      TEXT NOT NULL,
                    expires      TEXT
                )
                """;
            await create.ExecuteNonQueryAsync(ct);
            _ready = true;
        }

        return connection;
    }

    public async Task<IReadOnlyList<Drop>> AllAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, name, kind, text, size, content_type, created, expires FROM plugin_drops ORDER BY created DESC";

        var drops = new List<Drop>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            drops.Add(new Drop(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetString(5),
                DateTimeOffset.Parse(reader.GetString(6)),
                reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7))));
        }

        return drops;
    }

    public async Task<Drop?> FindAsync(string id, CancellationToken ct = default) =>
        (await AllAsync(ct)).FirstOrDefault(drop => drop.Id == id);

    public async Task<Drop> AddFileAsync(
        string name, string contentType, Stream content, TimeSpan? keepFor, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        var id = Guid.NewGuid().ToString("n");
        var path = PathFor(id);

        long size;
        await using (var file = File.Create(path))
        {
            await content.CopyToAsync(file, ct);
            size = file.Length;
        }

        var drop = new Drop(id, SafeName(name), "file", "", size, contentType,
            DateTimeOffset.Now, keepFor is { } span ? DateTimeOffset.Now + span : null);

        try
        {
            await InsertAsync(connection, drop, ct);
        }
        catch (Exception)
        {
            // Never leave bytes on disk with no row pointing at them: nothing would ever
            // clean them up, and the folder would grow for ever with no way to tell why.
            TryDelete(path);
            throw;
        }

        return drop;
    }

    public async Task<Drop> AddTextAsync(string text, TimeSpan? keepFor, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        var trimmed = text.Trim();
        var firstLine = trimmed.Split('\n')[0].Trim();
        var name = firstLine.Length <= 40 ? firstLine : firstLine[..40] + "…";

        var drop = new Drop(
            Guid.NewGuid().ToString("n"),
            name.Length == 0 ? "(empty)" : name,
            "text",
            trimmed,
            trimmed.Length,
            "text/plain",
            DateTimeOffset.Now,
            keepFor is { } span ? DateTimeOffset.Now + span : null);

        await InsertAsync(connection, drop, ct);
        return drop;
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM plugin_drops WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(ct);

        TryDelete(PathFor(id));
    }

    /// <summary>
    /// Removes everything past its expiry, and any file with no row left. Called by the
    /// background job — the reason this plugin needed one, since nothing else was ever
    /// going to notice that a file from three weeks ago is still sitting there.
    /// </summary>
    public async Task<int> PurgeAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.Now;
        var expired = (await AllAsync(ct)).Where(drop => drop.HasExpired(now)).ToList();

        foreach (var drop in expired)
            await DeleteAsync(drop.Id, ct);

        // Orphans: a crash between writing bytes and writing the row, or a database
        // restored from a backup that predates the file.
        var known = (await AllAsync(ct)).Select(drop => drop.Id).ToHashSet(StringComparer.Ordinal);
        var orphans = 0;

        if (Directory.Exists(Folder))
        {
            foreach (var path in Directory.EnumerateFiles(Folder))
            {
                if (known.Contains(Path.GetFileName(path)))
                    continue;

                // Give a file being written right now a wide berth.
                if (File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddMinutes(-10))
                    continue;

                TryDelete(path);
                orphans++;
            }
        }

        return expired.Count + orphans;
    }

    private static async Task InsertAsync(SqliteConnection connection, Drop drop, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO plugin_drops (id, name, kind, text, size, content_type, created, expires)
            VALUES ($id, $name, $kind, $text, $size, $type, $created, $expires)
            """;
        command.Parameters.AddWithValue("$id", drop.Id);
        command.Parameters.AddWithValue("$name", drop.Name);
        command.Parameters.AddWithValue("$kind", drop.Kind);
        command.Parameters.AddWithValue("$text", drop.Text);
        command.Parameters.AddWithValue("$size", drop.Size);
        command.Parameters.AddWithValue("$type", drop.ContentType);
        command.Parameters.AddWithValue("$created", drop.Created.ToString("o"));
        command.Parameters.AddWithValue("$expires", (object?)drop.Expires?.ToString("o") ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// The name is only ever shown and offered as a download filename — the bytes live
    /// under a generated id — but a name with a slash in it would still be a nasty
    /// surprise in a Content-Disposition header.
    /// </summary>
    private static string SafeName(string name)
    {
        var trimmed = Path.GetFileName(name.Trim());
        return trimmed.Length == 0 ? "file" : trimmed;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Locked by a download in flight; the next purge gets it.
        }
    }
}
