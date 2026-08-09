using LabbyTwo.Core;
using Microsoft.Data.Sqlite;

namespace LabbyTwo.Services.Import;

/// <summary>
/// Heimdall's <c>app.sqlite</c>. Heimdall has no config file and no JSON export — the
/// database is the configuration — so the importer reads the uploaded file directly.
/// Only the items table is touched, and only for reading.
/// </summary>
public sealed class HeimdallImporter : IDashboardImporter
{
    public string Key => "heimdall";
    public string DisplayName => "Heimdall";
    public string Icon => "🛡️";
    public string Description => "Heimdall's app.sqlite (from its config volume). Tags become cards, applications become links.";
    public IReadOnlyList<string> Extensions => [".sqlite", ".db", ".sqlite3"];

    // "SQLite format 3\0" — every SQLite file starts with it, and no YAML or HTML does.
    private static readonly byte[] Magic = "SQLite format 3\0"u8.ToArray();

    public bool CanHandle(ImportSource source) =>
        source.Content.Length > Magic.Length && source.Content.Take(Magic.Length).SequenceEqual(Magic);

    public ImportPlan Read(ImportSource source)
    {
        // Microsoft.Data.Sqlite opens files, not byte arrays, so the upload has to land on
        // disk. Read-only and deleted immediately after.
        var temp = Path.Combine(Path.GetTempPath(), $"heimdall-{Guid.NewGuid():N}.sqlite");
        try
        {
            File.WriteAllBytes(temp, source.Content);
            return ReadFrom(temp);
        }
        finally
        {
            // The connection is pooled, so the handle can outlive the using block and
            // Windows will refuse the delete. Clearing the pool first releases it.
            SqliteConnection.ClearAllPools();
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
                // A leftover temp file is not worth failing an otherwise good import over.
            }
        }
    }

    private static ImportPlan ReadFrom(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());

        try
        {
            connection.Open();
        }
        catch (SqliteException ex)
        {
            throw new FormatException($"That SQLite file could not be opened: {ex.Message}");
        }

        if (!HasTable(connection, "items"))
            throw new FormatException("That is a SQLite database, but not Heimdall's — it has no items table.");

        // Heimdall stores tags in the same table as applications, distinguished by type,
        // and links them through item_tag. Older versions have no type column at all, so
        // ask for what exists rather than assuming a schema version.
        var hasType = HasColumn(connection, "items", "type");
        var rows = new List<(int Id, string Title, string Url, int Type)>();

        var read = connection.CreateCommand();
        read.CommandText = hasType
            ? "SELECT id, title, url, COALESCE(type, 0) FROM items WHERE deleted_at IS NULL"
            : "SELECT id, title, url, 0 FROM items";

        try
        {
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((
                    reader.GetInt32(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                    reader.GetInt32(3)));
            }
        }
        catch (SqliteException ex)
        {
            throw new FormatException($"Heimdall's items table was not the expected shape: {ex.Message}");
        }

        // type 1 is a tag (a section), anything else is an application.
        var tags = rows.Where(r => r.Type == 1).ToDictionary(r => r.Id, r => r.Title);
        var apps = rows.Where(r => r.Type != 1 && r.Url.Length > 0).ToList();

        if (apps.Count == 0)
            throw new FormatException("No applications with URLs were found in that Heimdall database.");

        var membership = ReadMembership(connection);

        var tab = new ImportedTab("Heimdall", "🛡️");
        var grouped = apps
            .GroupBy(app => membership.TryGetValue(app.Id, out var tagId) && tags.TryGetValue(tagId, out var name)
                ? name
                : "Applications")
            .OrderBy(group => group.Key);

        foreach (var group in grouped)
        {
            var links = group
                .Select(app => new LinkRow("", app.Title.Length > 0 ? app.Title : app.Url, app.Url))
                .ToList();

            tab.Widgets.Add(new ImportedWidget(
                "links", group.Key, 3,
                new SettingsBag { ["links"] = LinkRow.Serialize(links) }));
        }

        var plan = new ImportPlan { Tabs = { tab } };
        plan.Notes.Add($"{apps.Count} applications in {tab.Widgets.Count} groups.");
        plan.Notes.Add(
            "Heimdall's enhanced apps store API keys for their live tiles. Those are not read — " +
            "add a connection under Connections for anything you want monitored.");
        return plan;
    }

    /// <summary>
    /// Which tag each application sits under. An app in several tags is put in the first,
    /// because a bookmark card is a list and duplicating a link across cards is noise.
    /// </summary>
    private static Dictionary<int, int> ReadMembership(SqliteConnection connection)
    {
        var membership = new Dictionary<int, int>();
        if (!HasTable(connection, "item_tag"))
            return membership;

        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT item_id, tag_id FROM item_tag";
        try
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var item = reader.GetInt32(0);
                if (!membership.ContainsKey(item))
                    membership[item] = reader.GetInt32(1);
            }
        }
        catch (SqliteException)
        {
            // Ungrouped links are still worth importing.
        }
        return membership;
    }

    private static bool HasTable(SqliteConnection connection, string name)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        cmd.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private static bool HasColumn(SqliteConnection connection, string table, string column)
    {
        var cmd = connection.CreateCommand();
        // PRAGMA does not take parameters; the table name here is ours, not a user's.
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
