using LabbyTwo.Storage;
using Microsoft.Data.Sqlite;

namespace LabbyTwo.RenewalsPlugin;

/// <summary>
/// Things that expire: domains, TLS certificates, subscriptions, warranties, the car's
/// MOT. One table in the host's database, the same arrangement the chores plugin uses, so
/// the list is inside every backup without anyone arranging that.
/// </summary>
public sealed class RenewalStore(Db db)
{
    /// <param name="EveryDays">0 for a one-off. 365 for a domain, 30 for a monthly bill.</param>
    /// <param name="Cost">Free text on purpose — "£12.99/yr", "$120", "" — because it is for reading, not summing.</param>
    /// <param name="TlsHost">
    /// Optional. When set, a background job reads the certificate this host presents and
    /// keeps <paramref name="Due"/> in step with it, so the date is observed rather than
    /// remembered.
    /// </param>
    public sealed record Renewal(
        string Id,
        string Title,
        string Category,
        DateOnly Due,
        int EveryDays,
        string Cost = "",
        string Url = "",
        string Notes = "",
        string TlsHost = "",
        string Issuer = "",
        DateTimeOffset? CheckedAt = null,
        string CheckError = "")
    {
        public bool IsWatched => TlsHost.Length > 0;

        /// <summary>True when the last check failed, which is worth showing on the row.</summary>
        public bool CheckFailed => CheckError.Length > 0;


        public bool Recurs => EveryDays > 0;

        public int DaysLeft(DateOnly today) => Due.DayNumber - today.DayNumber;

        public bool IsOverdue(DateOnly today) => Due < today;

        /// <summary>
        /// The next due date after renewing. Counted from the date it was *due*, not from
        /// today: a domain paid three days late still renews a year from its anniversary,
        /// and counting from today would walk the date forward a little every year until
        /// it drifted into a different month. Chores does the opposite, on purpose — see
        /// that plugin for why the two disagree.
        /// </summary>
        public DateOnly NextDue(DateOnly today)
        {
            if (!Recurs)
                return Due;

            var next = Due.AddDays(EveryDays);
            while (next <= today)
                next = next.AddDays(EveryDays);
            return next;
        }

        public string DueLabel(DateOnly today) => DaysLeft(today) switch
        {
            0 => "today",
            1 => "tomorrow",
            -1 => "yesterday",
            < 0 and var days => $"{-days} days ago",
            var days and <= 45 => $"in {days} days",
            _ => Due.ToString("d MMM yyyy"),
        };

        /// <summary>What the row should shout, if anything. Used for the colour only.</summary>
        public string Urgency(DateOnly today, int warnWithin) => DaysLeft(today) switch
        {
            < 0 => "overdue",
            var days when days <= warnWithin => "soon",
            _ => "fine",
        };
    }

    private bool _ready;

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = await db.OpenAsync(ct);

        if (!_ready)
        {
            await using var create = connection.CreateCommand();
            create.CommandText =
                """
                CREATE TABLE IF NOT EXISTS plugin_renewals (
                    id         TEXT PRIMARY KEY,
                    title      TEXT NOT NULL,
                    category   TEXT NOT NULL DEFAULT '',
                    due        TEXT NOT NULL,
                    every_days INTEGER NOT NULL DEFAULT 0,
                    cost       TEXT NOT NULL DEFAULT '',
                    url        TEXT NOT NULL DEFAULT '',
                    notes      TEXT NOT NULL DEFAULT '',
                    tls_host    TEXT NOT NULL DEFAULT '',
                    issuer      TEXT NOT NULL DEFAULT '',
                    checked_at  TEXT,
                    check_error TEXT NOT NULL DEFAULT ''
                )
                """;
            await create.ExecuteNonQueryAsync(ct);

            await MigrateAsync(connection, ct);
            _ready = true;
        }

        return connection;
    }

    /// <summary>
    /// The certificate columns arrived after the first release, so a table created by that
    /// version is caught up rather than recreated — dropping it to gain a column would
    /// throw away the list. Same reasoning as the chores plugin next door.
    /// </summary>
    private static async Task MigrateAsync(SqliteConnection connection, CancellationToken ct)
    {
        var columns = new List<string>();
        await using (var existing = connection.CreateCommand())
        {
            existing.CommandText = "SELECT name FROM pragma_table_info('plugin_renewals')";
            await using var reader = await existing.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                columns.Add(reader.GetString(0));
        }

        foreach (var (name, definition) in ((string Name, string Definition)[])
        [
            ("tls_host", "TEXT NOT NULL DEFAULT ''"),
            ("issuer", "TEXT NOT NULL DEFAULT ''"),
            ("checked_at", "TEXT"),
            ("check_error", "TEXT NOT NULL DEFAULT ''"),
        ])
        {
            if (columns.Contains(name))
                continue;

            await using var add = connection.CreateCommand();
            add.CommandText = $"ALTER TABLE plugin_renewals ADD COLUMN {name} {definition}";
            await add.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<IReadOnlyList<Renewal>> AllAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, title, category, due, every_days, cost, url, notes,
                   tls_host, issuer, checked_at, check_error
            FROM plugin_renewals
            ORDER BY due, title
            """;

        var renewals = new List<Renewal>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            renewals.Add(new Renewal(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                DateOnly.Parse(reader.GetString(3)),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                Moment(reader, 10),
                reader.GetString(11)));
        }

        return renewals;
    }

    /// <summary>
    /// A timestamp column that might be null, might be empty, and might be nonsense — a
    /// row hand-edited in a SQLite browser, or written by a version that stored it
    /// differently. Reading the list is how every page here starts, so one bad cell must
    /// not be able to take the whole thing down.
    /// </summary>
    private static DateTimeOffset? Moment(SqliteDataReader reader, int ordinal) =>
        !reader.IsDBNull(ordinal) && DateTimeOffset.TryParse(reader.GetString(ordinal), out var parsed)
            ? parsed
            : null;

    public async Task<IReadOnlyList<string>> CategoriesAsync(CancellationToken ct = default) =>
        [.. (await AllAsync(ct))
            .Select(renewal => renewal.Category)
            .Where(category => category.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(category => category, StringComparer.OrdinalIgnoreCase)];

    public async Task AddAsync(Renewal renewal, CancellationToken ct = default)
    {
        if (renewal.Title.Trim().Length == 0)
            return;

        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO plugin_renewals (id, title, category, due, every_days, cost, url, notes, tls_host)
            VALUES ($id, $title, $category, $due, $every, $cost, $url, $notes, $tls)
            """;
        Bind(command, renewal with { Id = Guid.NewGuid().ToString("n")[..12] });
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateAsync(Renewal renewal, CancellationToken ct = default)
    {
        if (renewal.Title.Trim().Length == 0)
            return;

        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE plugin_renewals
            SET title = $title, category = $category, due = $due, every_days = $every,
                cost = $cost, url = $url, notes = $notes, tls_host = $tls
            WHERE id = $id
            """;
        Bind(command, renewal);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Marks one as renewed. A recurring item moves to its next date; a one-off is done
    /// with, and deleting it is the honest thing rather than leaving a row that can never
    /// be anything but overdue.
    /// </summary>
    public async Task RenewAsync(Renewal renewal, DateOnly today, CancellationToken ct = default)
    {
        if (!renewal.Recurs)
        {
            await DeleteAsync(renewal.Id, ct);
            return;
        }

        await UpdateAsync(renewal with { Due = renewal.NextDue(today) }, ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM plugin_renewals WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void Bind(SqliteCommand command, Renewal renewal)
    {
        command.Parameters.AddWithValue("$id", renewal.Id);
        command.Parameters.AddWithValue("$title", renewal.Title.Trim());
        command.Parameters.AddWithValue("$category", renewal.Category.Trim());
        command.Parameters.AddWithValue("$due", renewal.Due.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$every", Math.Max(0, renewal.EveryDays));
        command.Parameters.AddWithValue("$cost", renewal.Cost.Trim());
        command.Parameters.AddWithValue("$url", renewal.Url.Trim());
        command.Parameters.AddWithValue("$notes", renewal.Notes.Trim());
        command.Parameters.AddWithValue("$tls", renewal.TlsHost.Trim());
    }

    /// <summary>
    /// Records what a certificate check found. The due date is taken from the certificate
    /// itself, which is the whole point: whatever renewed it — Caddy, acme.sh, the NAS's
    /// own web UI — the row follows along without anybody ticking anything off.
    /// </summary>
    public async Task RecordCheckAsync(
        string id, DateOnly? due, string issuer, string error, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();

        // A failed check leaves the last known date alone. Blanking it would turn "I cannot
        // reach the host" into "this expires today", which is a different and much louder
        // thing to say.
        command.CommandText = due is null
            ? """
              UPDATE plugin_renewals
              SET checked_at = $at, check_error = $error
              WHERE id = $id
              """
            : """
              UPDATE plugin_renewals
              SET due = $due, issuer = $issuer, checked_at = $at, check_error = $error
              WHERE id = $id
              """;

        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("o"));
        command.Parameters.AddWithValue("$error", error);
        if (due is { } date)
        {
            command.Parameters.AddWithValue("$due", date.ToString("yyyy-MM-dd"));
            command.Parameters.AddWithValue("$issuer", issuer);
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Checks one row's host and records the result. Shared by the background job and the
    /// Check button, so what the button proves is exactly what runs on the timer.
    /// </summary>
    public async Task CheckAsync(Renewal renewal, CancellationToken ct = default)
    {
        if (!renewal.IsWatched)
            return;

        try
        {
            var certificate = await TlsCertificate.ReadAsync(renewal.TlsHost, ct);
            await RecordCheckAsync(
                renewal.Id,
                DateOnly.FromDateTime(certificate.NotAfter.LocalDateTime),
                certificate.Issuer,
                "",
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await RecordCheckAsync(renewal.Id, null, "", ex.GetBaseException().Message, ct);
        }
    }
}
