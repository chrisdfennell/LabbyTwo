using LabbyTwo.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace LabbyTwo.Storage;

public sealed class LabbyOptions
{
    public const string SectionName = "Labby";

    public string DatabasePath { get; set; } = "data/labbytwo.db";

    /// <summary>
    /// Scanned for extension DLLs at startup. Under the data volume by default, so
    /// installing a plugin does not mean rebuilding the image.
    /// </summary>
    public string PluginPath { get; set; } = "data/plugins";

    public AuthSettings Auth { get; set; } = new();
    public int ProbeSeconds { get; set; } = 30;
    public int FailuresBeforeDown { get; set; } = 2;
    public int RetentionDays { get; set; } = 30;

    public sealed class AuthSettings
    {
        public string Username { get; set; } = "labby";
        public string Password { get; set; } = "";
        public bool Enabled => !string.IsNullOrWhiteSpace(Password);
    }
}

/// <summary>
/// Owns the SQLite file: connection strings and the one-time schema creation every
/// store awaits before its first query.
/// </summary>
public sealed class Db
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _ready;

    public Db(IOptions<LabbyOptions> options, IHostEnvironment env)
    {
        _path = Path.GetFullPath(options.Value.DatabasePath, env.ContentRootPath);
    }

    public string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = _path,
        // Every store opens its own short-lived connection; WAL plus a busy timeout keeps
        // the background probe loop from colliding with a page render.
        Pooling = true,
        DefaultTimeout = 10,
    }.ToString();

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        if (_ready)
            return;
        await _lock.WaitAsync(ct);
        try
        {
            if (_ready)
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(ct);
            var cmd = connection.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA busy_timeout = 10000;

                CREATE TABLE IF NOT EXISTS connections (
                    id        TEXT PRIMARY KEY,
                    provider  TEXT NOT NULL,
                    name      TEXT NOT NULL,
                    icon      TEXT NOT NULL DEFAULT '',
                    enabled   INTEGER NOT NULL DEFAULT 1,
                    sort      INTEGER NOT NULL DEFAULT 0,
                    settings  TEXT NOT NULL DEFAULT '{}');

                CREATE TABLE IF NOT EXISTS tabs (
                    id       TEXT PRIMARY KEY,
                    slug     TEXT NOT NULL UNIQUE,
                    name     TEXT NOT NULL,
                    icon     TEXT NOT NULL DEFAULT '',
                    kind     TEXT NOT NULL DEFAULT 'grid',
                    sort     INTEGER NOT NULL DEFAULT 0,
                    enabled  INTEGER NOT NULL DEFAULT 1,
                    settings TEXT NOT NULL DEFAULT '{}');

                CREATE TABLE IF NOT EXISTS widgets (
                    id            TEXT PRIMARY KEY,
                    tab_id        TEXT NOT NULL,
                    type          TEXT NOT NULL,
                    title         TEXT NOT NULL DEFAULT '',
                    connection_id TEXT,
                    sort          INTEGER NOT NULL DEFAULT 0,
                    width         INTEGER NOT NULL DEFAULT 4,
                    settings      TEXT NOT NULL DEFAULT '{}');
                CREATE INDEX IF NOT EXISTS ix_widgets_tab ON widgets (tab_id, sort);

                -- One row per probe that changed nothing but the clock is wasteful, so
                -- samples hold metrics and status transitions hold up/down.
                CREATE TABLE IF NOT EXISTS samples (
                    connection_id TEXT NOT NULL,
                    metric        TEXT NOT NULL,
                    ts            INTEGER NOT NULL,
                    value         REAL NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_samples_lookup ON samples (connection_id, metric, ts);

                CREATE TABLE IF NOT EXISTS status_events (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    connection_id TEXT NOT NULL,
                    ts            INTEGER NOT NULL,
                    is_up         INTEGER NOT NULL,
                    message       TEXT NOT NULL DEFAULT '');
                CREATE INDEX IF NOT EXISTS ix_status_lookup ON status_events (connection_id, ts);

                CREATE TABLE IF NOT EXISTS notes (
                    id         TEXT PRIMARY KEY,
                    tab_id     TEXT NOT NULL,
                    title      TEXT NOT NULL DEFAULT '',
                    content    TEXT NOT NULL DEFAULT '',
                    sort       INTEGER NOT NULL DEFAULT 0,
                    updated_at INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_notes_tab ON notes (tab_id, sort);
                """;
            await cmd.ExecuteNonQueryAsync(ct);
            await MigrateAsync(connection, ct);
            _ready = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Anything that changes an existing table goes here, keyed off SQLite's own
    /// <c>user_version</c>. Creating tables is handled above with IF NOT EXISTS; this
    /// exists so a database already holding somebody's dashboard can move forward.
    /// </summary>
    private static readonly IReadOnlyList<string> Migrations =
    [
        // 1 — per-connection alert muting.
        "ALTER TABLE connections ADD COLUMN alerts INTEGER NOT NULL DEFAULT 1",

        // 2 — app-level preferences chosen from the UI (theme, accent, brand name).
        // CREATE IF NOT EXISTS above would cover a new database, but an existing one
        // never re-runs that block, so the table is created here too.
        """
        CREATE TABLE IF NOT EXISTS app_settings (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL DEFAULT '')
        """,

        // 3 — threshold alerting. connection_id is nullable: a null rule watches the
        // metric on every connection that reports it.
        """
        CREATE TABLE IF NOT EXISTS alert_rules (
            id              TEXT PRIMARY KEY,
            name            TEXT NOT NULL DEFAULT '',
            connection_id   TEXT,
            metric          TEXT NOT NULL,
            comparison      TEXT NOT NULL DEFAULT 'above',
            threshold       REAL NOT NULL,
            clear_threshold REAL,
            for_minutes     INTEGER NOT NULL DEFAULT 0,
            enabled         INTEGER NOT NULL DEFAULT 1)
        """,

        // 4 — Overseerr and Jellyseerr merged into Seerr, so the provider key changed.
        // Without this an existing connection would come back as "no provider named
        // overseerr is installed" and quietly stop being monitored.
        "UPDATE connections SET provider = 'seerr' WHERE provider = 'overseerr'",

        // 5 — what a connection sits behind. When the parent is down, the child's alert is
        // one fault reported twice, so it is suppressed.
        "ALTER TABLE connections ADD COLUMN depends_on TEXT",

        // 6 — "stop telling me about this until X", for the hour somebody spends restarting
        // the thing on purpose.
        "ALTER TABLE connections ADD COLUMN silenced_until TEXT",

        // 7 — which channel a rule speaks through. Null keeps the old behaviour: everything.
        "ALTER TABLE alert_rules ADD COLUMN channel_id TEXT",
    ];

    private static async Task MigrateAsync(SqliteConnection connection, CancellationToken ct)
    {
        var read = connection.CreateCommand();
        read.CommandText = "PRAGMA user_version";
        var current = Convert.ToInt32(await read.ExecuteScalarAsync(ct));

        for (var version = current; version < Migrations.Count; version++)
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText = Migrations[version];

            try
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1
                && ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
            {
                // The column is already there, so this migration has effectively run —
                // SQLite has no ADD COLUMN IF NOT EXISTS, and a database that got ahead of
                // its own version stamp (restored from a backup, or a version rewound by
                // hand) must not be stuck refusing to start for ever.
            }

            // user_version cannot be parameterised, and the value is ours, not a user's.
            var stamp = connection.CreateCommand();
            stamp.CommandText = $"PRAGMA user_version = {version + 1}";
            await stamp.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>Not named Path — that would shadow System.IO.Path for the rest of this class.</summary>
    public string FilePath => _path;

    public long SizeBytes
    {
        get
        {
            // WAL pages hold writes that have not been checkpointed yet, so the .db file
            // alone understates the real size.
            var total = 0L;
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var file = new FileInfo(_path + suffix);
                if (file.Exists)
                    total += file.Length;
            }
            return total;
        }
    }

    /// <summary>
    /// Writes a consistent copy while the app keeps running — SQLite's own backup API,
    /// rather than copying a file that is being written to.
    /// </summary>
    public async Task BackupToAsync(string destinationPath, CancellationToken ct = default)
    {
        await using var source = await OpenAsync(ct);
        await using var destination = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = destinationPath }.ToString());
        await destination.OpenAsync(ct);
        source.BackupDatabase(destination);
    }

    public async Task<bool> IsEmptyAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT (SELECT COUNT(*) FROM tabs) + (SELECT COUNT(*) FROM connections)";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) == 0;
    }
}
