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
    public sealed record Renewal(
        string Id,
        string Title,
        string Category,
        DateOnly Due,
        int EveryDays,
        string Cost = "",
        string Url = "",
        string Notes = "")
    {
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
                    notes      TEXT NOT NULL DEFAULT ''
                )
                """;
            await create.ExecuteNonQueryAsync(ct);
            _ready = true;
        }

        return connection;
    }

    public async Task<IReadOnlyList<Renewal>> AllAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, title, category, due, every_days, cost, url, notes FROM plugin_renewals ORDER BY due, title";

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
                reader.GetString(7)));
        }

        return renewals;
    }

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
            INSERT INTO plugin_renewals (id, title, category, due, every_days, cost, url, notes)
            VALUES ($id, $title, $category, $due, $every, $cost, $url, $notes)
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
                cost = $cost, url = $url, notes = $notes
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
    }
}
