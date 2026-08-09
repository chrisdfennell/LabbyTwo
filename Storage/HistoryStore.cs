using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace LabbyTwo.Storage;

/// <summary>
/// Probe history: numeric samples for charts, and up/down transitions for uptime.
/// Nothing here knows what a metric means, which is why any provider that reports one
/// gets charts and uptime for free.
/// </summary>
public sealed class HistoryStore(Db db, IOptions<LabbyOptions> options)
{
    public sealed record Sample(DateTimeOffset At, double Value);
    public sealed record StatusEvent(string ConnectionId, DateTimeOffset At, bool IsUp, string Message);
    /// <summary>
    /// <see cref="Since"/> is when monitoring actually began if that is later than the
    /// window start. The percentage is always measured from there, so a service added ten
    /// minutes ago does not get credited — or blamed — for the other 23 hours.
    /// </summary>
    public sealed record Uptime(double Percent, int Samples, DateTimeOffset? Since)
    {
        public bool IsPartial => Since is not null;
    }

    public async Task RecordAsync(string connectionId, IReadOnlyDictionary<string, double> metrics, CancellationToken ct)
    {
        if (metrics.Count == 0)
            return;
        await using var connection = await db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO samples (connection_id, metric, ts, value) VALUES ($c, $m, $t, $v)";
        var metric = cmd.Parameters.Add("$m", SqliteType.Text);
        var value = cmd.Parameters.Add("$v", SqliteType.Real);
        cmd.Parameters.AddWithValue("$c", connectionId);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        foreach (var (key, number) in metrics)
        {
            metric.Value = key;
            value.Value = number;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>Only called on a transition, so the table stays small enough to scan.</summary>
    public async Task RecordStatusAsync(string connectionId, bool isUp, string message, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO status_events (connection_id, ts, is_up, message) VALUES ($c, $t, $u, $m)";
        cmd.Parameters.AddWithValue("$c", connectionId);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$u", isUp ? 1 : 0);
        cmd.Parameters.AddWithValue("$m", message);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<Sample>> SamplesAsync(string connectionId, string metric, TimeSpan window, CancellationToken ct = default)
    {
        var since = DateTimeOffset.UtcNow.Subtract(window).ToUnixTimeSeconds();
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT ts, value FROM samples
            WHERE connection_id = $c AND metric = $m AND ts >= $since
            ORDER BY ts
            """;
        cmd.Parameters.AddWithValue("$c", connectionId);
        cmd.Parameters.AddWithValue("$m", metric);
        cmd.Parameters.AddWithValue("$since", since);
        var list = new List<Sample>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new Sample(DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)).ToLocalTime(), reader.GetDouble(1)));
        return list;
    }

    /// <summary>Which metrics a connection has actually reported — drives the chart widget's picker.</summary>
    /// <summary>
    /// The most recent recorded value of every metric on a connection. Cards that show a
    /// live reading use this as a fallback so they say something sensible immediately
    /// after a restart, instead of "waiting" until the next sweep lands.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, double>> LatestAsync(
        string connectionId, TimeSpan window, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        // Timestamps are whole seconds, so two probes in the same second tie — and with
        // GROUP BY plus MAX(ts) SQLite may then return either row. Ordering by rowid as
        // well makes "newest" mean the most recently inserted, deterministically.
        cmd.CommandText = """
            SELECT metric, value FROM (
                SELECT metric, value,
                       ROW_NUMBER() OVER (PARTITION BY metric ORDER BY ts DESC, rowid DESC) AS rank
                FROM samples
                WHERE connection_id = $id AND ts >= $since)
            WHERE rank = 1
            """;
        cmd.Parameters.AddWithValue("$id", connectionId);
        cmd.Parameters.AddWithValue("$since", DateTimeOffset.UtcNow.Subtract(window).ToUnixTimeSeconds());

        var latest = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            latest[reader.GetString(0)] = reader.GetDouble(1);
        return latest;
    }

    public async Task<IReadOnlyList<string>> MetricsAsync(string connectionId, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT metric FROM samples WHERE connection_id = $c ORDER BY metric";
        cmd.Parameters.AddWithValue("$c", connectionId);
        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(reader.GetString(0));
        return list;
    }

    /// <summary>
    /// Uptime over a window, computed from transitions: walk the events, add up the time
    /// spent up, and carry the state from before the window so a service that has been
    /// up for a month doesn't read as 0% for lack of events inside it.
    /// </summary>
    public async Task<Uptime> UptimeAsync(string connectionId, TimeSpan window, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var start = now.Subtract(window);
        await using var connection = await db.OpenAsync(ct);

        var priorCmd = connection.CreateCommand();
        priorCmd.CommandText = """
            SELECT is_up FROM status_events
            WHERE connection_id = $c AND ts < $start
            ORDER BY ts DESC LIMIT 1
            """;
        priorCmd.Parameters.AddWithValue("$c", connectionId);
        priorCmd.Parameters.AddWithValue("$start", start.ToUnixTimeSeconds());
        var prior = await priorCmd.ExecuteScalarAsync(ct);

        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT ts, is_up FROM status_events
            WHERE connection_id = $c AND ts >= $start
            ORDER BY ts
            """;
        cmd.Parameters.AddWithValue("$c", connectionId);
        cmd.Parameters.AddWithValue("$start", start.ToUnixTimeSeconds());

        var events = new List<(DateTimeOffset At, bool IsUp)>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                events.Add((DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)), reader.GetInt64(1) != 0));
        }

        if (prior is null && events.Count == 0)
            return new Uptime(0, 0, null);

        // A prior event means monitoring was already running when the window opened, so the
        // whole window counts. Otherwise it began at the first recorded event, and only the
        // time since then is measurable — counting the rest either way would be a guess.
        var measureFrom = prior is not null ? start : events[0].At;
        var state = prior is not null ? Convert.ToInt64(prior) != 0 : events[0].IsUp;

        var cursor = measureFrom;
        var upTime = TimeSpan.Zero;
        foreach (var (at, isUp) in events)
        {
            if (at > cursor)
            {
                if (state)
                    upTime += at - cursor;
                cursor = at;
            }
            state = isUp;
        }
        if (state && now > cursor)
            upTime += now - cursor;

        var measured = (now - measureFrom).TotalSeconds;
        if (measured <= 0)
            return new Uptime(100, events.Count, measureFrom.ToLocalTime());

        return new Uptime(
            Math.Clamp(upTime.TotalSeconds / measured * 100, 0, 100),
            events.Count,
            prior is null ? measureFrom.ToLocalTime() : null);
    }

    public sealed record Day(DateOnly Date, double? Percent);

    /// <summary>
    /// Per-day uptime for a bar strip. Reads the whole window once and walks it in memory —
    /// a query per day would be 30 round trips per service on every render.
    /// </summary>
    public async Task<IReadOnlyList<Day>> DailyUptimeAsync(string connectionId, int days, CancellationToken ct = default)
    {
        var now = DateTimeOffset.Now;
        var firstDay = DateOnly.FromDateTime(now.LocalDateTime.Date.AddDays(-(days - 1)));
        var windowStart = new DateTimeOffset(firstDay.ToDateTime(TimeOnly.MinValue), now.Offset);

        await using var connection = await db.OpenAsync(ct);

        var priorCmd = connection.CreateCommand();
        priorCmd.CommandText = """
            SELECT is_up, ts FROM status_events
            WHERE connection_id = $c AND ts < $start
            ORDER BY ts DESC LIMIT 1
            """;
        priorCmd.Parameters.AddWithValue("$c", connectionId);
        priorCmd.Parameters.AddWithValue("$start", windowStart.ToUnixTimeSeconds());

        bool? priorState = null;
        DateTimeOffset? monitoringSince = null;
        await using (var reader = await priorCmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                priorState = reader.GetInt64(0) != 0;
                monitoringSince = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)).ToLocalTime();
            }
        }

        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ts, is_up FROM status_events WHERE connection_id = $c AND ts >= $start ORDER BY ts";
        cmd.Parameters.AddWithValue("$c", connectionId);
        cmd.Parameters.AddWithValue("$start", windowStart.ToUnixTimeSeconds());

        var events = new List<(DateTimeOffset At, bool IsUp)>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                events.Add((DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)).ToLocalTime(), reader.GetInt64(1) != 0));
        }

        monitoringSince ??= events.Count > 0 ? events[0].At : null;

        var result = new List<Day>(days);
        var state = priorState ?? true;
        var cursor = 0;

        for (var i = 0; i < days; i++)
        {
            var date = firstDay.AddDays(i);
            var dayStart = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), now.Offset);
            var dayEnd = dayStart.AddDays(1);
            // Today is only partly elapsed; measuring it against a full 24h would show
            // every healthy service dropping through the day.
            var end = dayEnd > now ? now : dayEnd;

            // No data at all for this day — a gap before monitoring began reads as unknown,
            // not as an outage.
            if (monitoringSince is null || dayStart < monitoringSince && end <= monitoringSince)
            {
                result.Add(new Day(date, null));
                // Events cannot predate monitoringSince, so nothing to advance past.
                continue;
            }

            var measureFrom = dayStart < monitoringSince ? monitoringSince.Value : dayStart;
            var upTime = TimeSpan.Zero;
            var at = measureFrom;

            while (cursor < events.Count && events[cursor].At < end)
            {
                if (events[cursor].At > at)
                {
                    if (state)
                        upTime += events[cursor].At - at;
                    at = events[cursor].At;
                }
                state = events[cursor].IsUp;
                cursor++;
            }
            if (state && end > at)
                upTime += end - at;

            var total = (end - measureFrom).TotalSeconds;
            result.Add(new Day(date, total > 0 ? Math.Clamp(upTime.TotalSeconds / total * 100, 0, 100) : null));
        }

        return result;
    }

    public async Task<IReadOnlyList<StatusEvent>> RecentEventsAsync(string? connectionId, int limit, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = connectionId is null
            ? "SELECT connection_id, ts, is_up, message FROM status_events ORDER BY ts DESC LIMIT $limit"
            : "SELECT connection_id, ts, is_up, message FROM status_events WHERE connection_id = $c ORDER BY ts DESC LIMIT $limit";
        if (connectionId is not null)
            cmd.Parameters.AddWithValue("$c", connectionId);
        cmd.Parameters.AddWithValue("$limit", limit);
        var list = new List<StatusEvent>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new StatusEvent(
                reader.GetString(0),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)).ToLocalTime(),
                reader.GetInt64(2) != 0,
                reader.GetString(3)));
        }
        return list;
    }

    /// <summary>Drops samples past the retention window. Status events are kept — they are tiny and are the audit trail.</summary>
    public async Task PruneAsync(CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-options.Value.RetentionDays).ToUnixTimeSeconds();
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM samples WHERE ts < $cutoff";
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
