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
    /// <summary>
    /// One point in a series. A raw reading has <see cref="Min"/>, <see cref="Max"/> and
    /// <see cref="Value"/> all the same and a <see cref="Count"/> of one. A point from an
    /// hourly summary has the hour's average as its value and its extremes alongside, so a
    /// card reporting "the peak gust this month" can ask for the peak rather than the
    /// highest hourly average — which is lower, and not what the station recorded.
    /// </summary>
    public sealed record Sample(DateTimeOffset At, double Value)
    {
        public double Min { get; init; } = Value;
        public double Max { get; init; } = Value;
        public long Count { get; init; } = 1;
    }

    /// <summary>What one pass of <see cref="RollupAsync"/> did, for the log and the benchmark.</summary>
    /// <param name="LongestBatch">The longest any one transaction held the write lock.</param>
    /// <param name="Finished">False if it stopped at its time budget with hours still to do.</param>
    public sealed record RollupResult(int Batches, long HoursWritten, long RowsDeleted, TimeSpan LongestBatch, bool Finished);
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

    /// <summary>One metric's newest value and when it was recorded.</summary>
    public readonly record struct Reading(double Value, DateTimeOffset At);

    /// <summary>
    /// Raised after a batch of samples is committed, with the time they were stamped.
    /// <see cref="LabbyTwo.Services.LatestReadings"/> listens, so the newest value of every
    /// metric is in memory the moment it is written: cards read that, never the database,
    /// while a page is being drawn. Raised here rather than by the monitor so that anything
    /// that records samples keeps the cache current without having to know it exists.
    /// </summary>
    public event Action<string, IReadOnlyDictionary<string, double>, DateTimeOffset>? Recorded;

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
        var stamp = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$c", connectionId);
        cmd.Parameters.AddWithValue("$t", stamp.ToUnixTimeSeconds());
        foreach (var (key, number) in metrics)
        {
            metric.Value = key;
            value.Value = number;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);

        // After the commit, so nothing in memory claims a value the database rolled back.
        Recorded?.Invoke(connectionId, metrics, stamp);
    }

    /// <summary>
    /// Records a transition — and only a transition. The insert is conditional on the
    /// newest event for this connection saying something different, which is what keeps
    /// the promise the status page makes: every row here is a moment something actually
    /// changed. A caller that has lost track of the previous state (a restart, which
    /// leaves the monitor with nothing in memory) can call this freely rather than having
    /// to decide, and a service that was up before the restart and is up after it does
    /// not get a row saying it came up.
    /// </summary>
    /// <returns>True if a row was written, false if it repeated the current state.</returns>
    public async Task<bool> RecordStatusAsync(string connectionId, bool isUp, string message, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        // IS NOT rather than <> so the first event for a connection — where the subquery
        // is NULL — inserts instead of comparing against nothing and failing to.
        cmd.CommandText = """
            INSERT INTO status_events (connection_id, ts, is_up, message)
            SELECT $c, $t, $u, $m
            WHERE $u IS NOT (SELECT is_up FROM status_events
                             WHERE connection_id = $c
                             ORDER BY ts DESC, rowid DESC LIMIT 1)
            """;
        cmd.Parameters.AddWithValue("$c", connectionId);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$u", isUp ? 1 : 0);
        cmd.Parameters.AddWithValue("$m", message);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>
    /// The newest status event for every connection that has one — what the monitor knew
    /// before it was last stopped. Since the table holds only transitions, the timestamp
    /// on each is when that state began, which is what lets a recovery notice after a
    /// restart still say how long the outage lasted.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, StatusEvent>> LatestStatusAsync(CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT connection_id, ts, is_up, message FROM (
                SELECT connection_id, ts, is_up, message,
                       ROW_NUMBER() OVER (PARTITION BY connection_id ORDER BY ts DESC, rowid DESC) AS rank
                FROM status_events)
            WHERE rank = 1
            """;
        var latest = new Dictionary<string, StatusEvent>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            latest[reader.GetString(0)] = new StatusEvent(
                reader.GetString(0),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)).ToLocalTime(),
                reader.GetInt64(2) != 0,
                reader.GetString(3));
        }
        return latest;
    }

    /// <summary>
    /// When each connection last reported a number. The monitor uses it to decide what is
    /// due after a restart: a provider that declares a MinimumInterval because its
    /// upstream meters requests should not be asked again just because this process is
    /// new, and a restart loop would otherwise be a quota loop.
    ///
    /// Raw samples only, on purpose. A connection whose newest reading has already been
    /// rolled up last reported at least the raw retention ago — a day at the very least —
    /// which is past any sensible MinimumInterval, so it is due either way, and leaving it
    /// out of the answer says exactly that.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> LastSampleAtAsync(CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT connection_id, MAX(ts) FROM samples GROUP BY connection_id";
        var latest = new Dictionary<string, DateTimeOffset>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            latest[reader.GetString(0)] = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)).ToLocalTime();
        return latest;
    }

    /// <summary>
    /// A metric's history over a window, oldest first — what every chart draws.
    ///
    /// A window that fits inside the raw retention comes back exactly as recorded, one
    /// point per probe. A longer one comes back one point per hour, all the way through:
    /// the hours already summarised come from samples_hourly, and the recent ones are
    /// summarised here on the fly from the raw rows. Mixing the two resolutions in one
    /// series would be wrong rather than merely untidy — the charts space points evenly by
    /// index, not by time, so a month of hourly points followed by a week of 30-second ones
    /// would draw the month as a sliver at the left edge and the week as the whole chart.
    /// It is also the downsampling a long window wants anyway: a 30-day chart is 720
    /// points rather than 86,400, whatever the retention settings are.
    ///
    /// Which of the two to do is decided by the window and by the data: longer than the
    /// raw retention, or any summary inside it (the retention was raised after a rollup,
    /// say), is hourly. A window within the raw retention never meets a summary otherwise,
    /// because the rollup only summarises hours that ended before its own cut-off, and that
    /// cut-off is always at least the raw retention ago.
    ///
    /// Each hourly point is stamped at the middle of its hour — clamped, for an hour still
    /// in progress or cut by the window's start, to the readings it actually holds — so no
    /// point is ever in the future or before the window.
    /// </summary>
    public async Task<IReadOnlyList<Sample>> SamplesAsync(string connectionId, string metric, TimeSpan window, CancellationToken ct = default)
    {
        var since = DateTimeOffset.UtcNow.Subtract(window).ToUnixTimeSeconds();
        await using var connection = await db.OpenAsync(ct);

        var summaries = await SummariesAsync(connection, connectionId, metric, since, ct);
        if (summaries.Count == 0 && window <= options.Value.RawRetention)
            return await RawSamplesAsync(connection, connectionId, metric, since, ct);

        return await HourlySamplesAsync(connection, connectionId, metric, since, summaries, ct);
    }

    /// <summary>
    /// The hourly half of <see cref="SamplesAsync"/>: stored summaries merged with the raw
    /// rows summarised here, one point per hour.
    /// </summary>
    private static async Task<IReadOnlyList<Sample>> HourlySamplesAsync(
        SqliteConnection connection, string connectionId, string metric, long since, List<Bucket> summaries, CancellationToken ct)
    {
        // Grouped per hour in SQL, so a long window never brings a week of raw rows into
        // memory — one aggregate per hour. The (connection_id, metric, ts) index delivers
        // the range already in ts order, so the grouping streams rather than sorts.
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT ts / 3600 * 3600 AS hour, MIN(ts), MAX(ts), MIN(value), MAX(value), AVG(value), COUNT(*)
            FROM samples
            WHERE connection_id = $c AND metric = $m AND ts >= $since
            GROUP BY hour
            """;
        cmd.Parameters.AddWithValue("$c", connectionId);
        cmd.Parameters.AddWithValue("$m", metric);
        cmd.Parameters.AddWithValue("$since", since);

        var hours = new SortedDictionary<long, Bucket>();
        foreach (var summary in summaries)
            hours[summary.Hour] = summary;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var fresh = new Bucket(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
                    reader.GetDouble(3), reader.GetDouble(4), reader.GetDouble(5), reader.GetInt64(6));
                // An hour is in both only if raw rows landed in it after it was rolled up —
                // a restored backup, a clock that jumped back. Combined rather than drawn
                // twice at the same instant.
                hours[fresh.Hour] = hours.TryGetValue(fresh.Hour, out var summarised) ? summarised.Merge(fresh) : fresh;
            }
        }

        var list = new List<Sample>(hours.Count);
        foreach (var bucket in hours.Values)
        {
            var at = Math.Clamp(bucket.Hour + 1800, bucket.First, bucket.Last);
            list.Add(new Sample(DateTimeOffset.FromUnixTimeSeconds(at).ToLocalTime(), bucket.Avg)
            {
                Min = bucket.Min,
                Max = bucket.Max,
                Count = bucket.Count,
            });
        }
        return list;
    }

    /// <summary>
    /// One hour of one series. <see cref="First"/> and <see cref="Last"/> bound the
    /// readings it holds: exactly, for one summarised from raw rows here, and as the whole
    /// hour for a stored summary — which is complete and in the past, so its midpoint is
    /// always inside the window it was selected for.
    /// </summary>
    private readonly record struct Bucket(long Hour, long First, long Last, double Min, double Max, double Avg, long Count)
    {
        public Bucket Merge(Bucket other) => new(
            Hour,
            Math.Min(First, other.First),
            Math.Max(Last, other.Last),
            Math.Min(Min, other.Min),
            Math.Max(Max, other.Max),
            (Avg * Count + other.Avg * other.Count) / (Count + other.Count),
            Count + other.Count);
    }

    private static async Task<List<Bucket>> SummariesAsync(
        SqliteConnection connection, string connectionId, string metric, long since, CancellationToken ct)
    {
        var cmd = connection.CreateCommand();
        // An hour belongs to the window if its midpoint does, since that is where it is drawn.
        cmd.CommandText = """
            SELECT hour_ts, min, max, avg, count FROM samples_hourly
            WHERE connection_id = $c AND metric = $m AND hour_ts >= $since - 1800
            ORDER BY hour_ts
            """;
        cmd.Parameters.AddWithValue("$c", connectionId);
        cmd.Parameters.AddWithValue("$m", metric);
        cmd.Parameters.AddWithValue("$since", since);
        var list = new List<Bucket>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var hour = reader.GetInt64(0);
            list.Add(new Bucket(hour, hour, hour + 3599,
                reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetInt64(4)));
        }
        return list;
    }

    private static async Task<IReadOnlyList<Sample>> RawSamplesAsync(
        SqliteConnection connection, string connectionId, string metric, long since, CancellationToken ct)
    {
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

    /// <summary>
    /// The most recent recorded value of every metric on a connection. Cards that show a
    /// live reading use this as a fallback so they say something sensible immediately
    /// after a restart, instead of "waiting" until the next sweep lands.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, double>> LatestAsync(
        string connectionId, TimeSpan window, CancellationToken ct = default)
    {
        var latest = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (metric, reading) in await LatestReadingsAsync(connectionId, window, ct))
            latest[metric] = reading.Value;
        return latest;
    }

    /// <summary>
    /// <see cref="LatestAsync"/> with the time each value was recorded, which is what the
    /// in-memory cache needs to answer a narrower window later without asking again.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, Reading>> LatestReadingsAsync(
        string connectionId, TimeSpan window, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        // Timestamps are whole seconds, so two probes in the same second tie — and with
        // GROUP BY plus MAX(ts) SQLite may then return either row. Ordering by rowid as
        // well makes "newest" mean the most recently inserted, deterministically.
        //
        // Walked one metric at a time rather than ranked in one pass. The index is
        // (connection_id, metric, ts), so ranking had to read every sample the connection
        // had ever kept — a month of them, filtered to the window afterwards — and ten
        // cards do this while the page is being drawn. The recursive part hops from one
        // metric name to the next through the index, and each metric's newest value is
        // then a single seek from the end of its range: a handful of lookups, however
        // big the table has grown. The seek finds the row's rowid, and the join fetches
        // value and timestamp from that one row, so the two can never come from different
        // samples. Cards no longer run this while drawing — they read LatestReadings,
        // which runs it in the background — but it is still what warms that cache.
        cmd.CommandText = """
            WITH RECURSIVE metrics(metric) AS (
                SELECT MIN(metric) FROM samples WHERE connection_id = $id
                UNION ALL
                SELECT (SELECT MIN(metric) FROM samples WHERE connection_id = $id AND metric > metrics.metric)
                FROM metrics WHERE metrics.metric IS NOT NULL)
            SELECT samples.metric, samples.value, samples.ts
            FROM metrics
            JOIN samples ON samples.rowid =
                (SELECT rowid FROM samples
                 WHERE connection_id = $id AND metric = metrics.metric AND ts >= $since
                 ORDER BY ts DESC, rowid DESC LIMIT 1)
            WHERE metrics.metric IS NOT NULL
            """;
        cmd.Parameters.AddWithValue("$id", connectionId);
        cmd.Parameters.AddWithValue("$since", DateTimeOffset.UtcNow.Subtract(window).ToUnixTimeSeconds());

        var latest = new Dictionary<string, Reading>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                latest[reader.GetString(0)] = new Reading(
                    reader.GetDouble(1), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)));
            }
        }

        // Then the summaries, for a metric whose newest reading is older than the raw
        // window and so now lives only there — a speed test that stopped running a
        // fortnight ago still has a last result, and the card asking for thirty days
        // should still show it, marked as old. Each summary keeps its hour's newest
        // reading exactly, so this is the value and time that were really recorded, not
        // an average. The same hop through the key, and a seek to each metric's last
        // hour. A raw reading always wins: anything still raw is newer than anything
        // already summarised, bar the rows a restored backup might put back.
        var summaries = connection.CreateCommand();
        summaries.CommandText = """
            WITH RECURSIVE metrics(metric) AS (
                SELECT MIN(metric) FROM samples_hourly WHERE connection_id = $id
                UNION ALL
                SELECT (SELECT MIN(metric) FROM samples_hourly WHERE connection_id = $id AND metric > metrics.metric)
                FROM metrics WHERE metrics.metric IS NOT NULL)
            SELECT hourly.metric, hourly.last_value, hourly.last_ts
            FROM metrics
            JOIN samples_hourly AS hourly
              ON hourly.connection_id = $id AND hourly.metric = metrics.metric
             AND hourly.hour_ts = (SELECT MAX(hour_ts) FROM samples_hourly
                                   WHERE connection_id = $id AND metric = metrics.metric)
            WHERE metrics.metric IS NOT NULL AND hourly.last_ts >= $since
            """;
        summaries.Parameters.AddWithValue("$id", connectionId);
        summaries.Parameters.AddWithValue("$since", DateTimeOffset.UtcNow.Subtract(window).ToUnixTimeSeconds());
        await using (var reader = await summaries.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var reading = new Reading(reader.GetDouble(1), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)));
                if (!latest.TryGetValue(reader.GetString(0), out var raw) || raw.At < reading.At)
                    latest[reader.GetString(0)] = reading;
            }
        }
        return latest;
    }

    /// <summary>Which metrics a connection has actually reported — drives the chart widget's picker.</summary>
    public async Task<IReadOnlyList<string>> MetricsAsync(string connectionId, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        // Hopped through the index one name at a time rather than DISTINCT, which on this
        // index still reads every sample the connection has kept — a month of them, to
        // find a dozen names. The alert rules page asks this for every connection before
        // it can draw, and on a busy install that was long enough that the page never
        // opened. This is a lookup per metric, however big the table grows.
        //
        // Both tables, hopped the same way: a metric that stopped reporting before the raw
        // window still has a year of history to chart, so it must still be offered.
        cmd.CommandText = """
            WITH RECURSIVE
            raw(metric) AS (
                SELECT MIN(metric) FROM samples WHERE connection_id = $c
                UNION ALL
                SELECT (SELECT MIN(metric) FROM samples WHERE connection_id = $c AND metric > raw.metric)
                FROM raw WHERE raw.metric IS NOT NULL),
            hourly(metric) AS (
                SELECT MIN(metric) FROM samples_hourly WHERE connection_id = $c
                UNION ALL
                SELECT (SELECT MIN(metric) FROM samples_hourly WHERE connection_id = $c AND metric > hourly.metric)
                FROM hourly WHERE hourly.metric IS NOT NULL)
            SELECT metric FROM raw WHERE metric IS NOT NULL
            UNION
            SELECT metric FROM hourly WHERE metric IS NOT NULL
            ORDER BY metric
            """;
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

        // rowid breaks the tie here for the same reason it does when reading events back:
        // timestamps are whole seconds, so a service that drops and returns inside one
        // second leaves two rows SQLite may order either way. Getting it backwards on the
        // row just before the window means the whole window is measured from the wrong
        // state, which is a percentage that is wrong rather than a list in an odd order.
        var priorCmd = connection.CreateCommand();
        priorCmd.CommandText = """
            SELECT is_up FROM status_events
            WHERE connection_id = $c AND ts < $start
            ORDER BY ts DESC, rowid DESC LIMIT 1
            """;
        priorCmd.Parameters.AddWithValue("$c", connectionId);
        priorCmd.Parameters.AddWithValue("$start", start.ToUnixTimeSeconds());
        var prior = await priorCmd.ExecuteScalarAsync(ct);

        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT ts, is_up FROM status_events
            WHERE connection_id = $c AND ts >= $start
            ORDER BY ts, rowid
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
    /// Local midnight on <paramref name="date"/>, with the offset that day actually had.
    ///
    /// The strip used to stamp every day with today's offset, so in a window that crossed a
    /// clock change every day on the far side of it started an hour early or late — an
    /// outage just after midnight landed on the wrong bar. A midnight the clocks skip (a few
    /// zones change at midnight rather than at two) takes the offset from before the jump,
    /// which is where the day's first real moment is.
    /// </summary>
    public static DateTimeOffset StartOfDay(DateOnly date, TimeZoneInfo zone)
    {
        var midnight = date.ToDateTime(TimeOnly.MinValue);
        if (zone.IsInvalidTime(midnight))
            return new DateTimeOffset(midnight, zone.GetUtcOffset(midnight.AddHours(-1)));

        // Ambiguous only if the clocks fall back across midnight; the earlier of the two
        // readings is the start of the day.
        var offset = zone.IsAmbiguousTime(midnight)
            ? zone.GetAmbiguousTimeOffsets(midnight).Max()
            : zone.GetUtcOffset(midnight);
        return new DateTimeOffset(midnight, offset);
    }

    /// <summary>
    /// Per-day uptime for a bar strip. Reads the whole window once and walks it in memory —
    /// a query per day would be 30 round trips per service on every render.
    /// </summary>
    public async Task<IReadOnlyList<Day>> DailyUptimeAsync(string connectionId, int days, CancellationToken ct = default)
    {
        var now = DateTimeOffset.Now;
        var firstDay = DateOnly.FromDateTime(now.LocalDateTime.Date.AddDays(-(days - 1)));
        var windowStart = StartOfDay(firstDay, TimeZoneInfo.Local);

        await using var connection = await db.OpenAsync(ct);

        var priorCmd = connection.CreateCommand();
        priorCmd.CommandText = """
            SELECT is_up, ts FROM status_events
            WHERE connection_id = $c AND ts < $start
            ORDER BY ts DESC, rowid DESC LIMIT 1
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
        cmd.CommandText = "SELECT ts, is_up FROM status_events WHERE connection_id = $c AND ts >= $start ORDER BY ts, rowid";
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
            var dayStart = StartOfDay(date, TimeZoneInfo.Local);
            // The next midnight, not 24 hours on: the days either side of a clock change
            // are 23 and 25 hours long.
            var dayEnd = StartOfDay(date.AddDays(1), TimeZoneInfo.Local);
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
        // rowid breaks the tie. Timestamps are whole seconds, so a service that drops and
        // comes back inside one second gives SQLite two rows it may return in either
        // order — and "recovered, then went down" is the wrong way round to read on a
        // status page. Newest means most recently inserted, as it does in LatestAsync.
        cmd.CommandText = connectionId is null
            ? "SELECT connection_id, ts, is_up, message FROM status_events ORDER BY ts DESC, rowid DESC LIMIT $limit"
            : "SELECT connection_id, ts, is_up, message FROM status_events WHERE connection_id = $c ORDER BY ts DESC, rowid DESC LIMIT $limit";
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

    /// <summary>
    /// Drops hourly summaries past their retention. Status events are kept — they are tiny
    /// and are the audit trail.
    ///
    /// Raw samples are no longer deleted here: past the raw window they are summarised
    /// first, by <see cref="RollupAsync"/>, which deletes each hour's rows in the same
    /// transaction that writes its summary. Deleting them here as well would race it and
    /// lose whatever it had not reached yet. That also takes the one expensive statement
    /// out of the monitor's sweep, which calls this hourly and waits for it: "every raw row
    /// older than X" had no index to use and read the whole table, while this is a range
    /// on ix_samples_hourly_age holding one hour's worth of summaries.
    /// </summary>
    public async Task PruneAsync(CancellationToken ct) => await PruneAsync(DateTimeOffset.UtcNow, ct);

    /// <summary><see cref="PruneAsync(CancellationToken)"/> as if it were <paramref name="now"/>, for tests.</summary>
    public async Task PruneAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM samples_hourly WHERE hour_ts < $keep";
        cmd.Parameters.AddWithValue("$keep", HourlyHorizon(now));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private const long Hour = 3600;

    /// <summary>
    /// The span of time one rollup transaction covers, across every series at once.
    ///
    /// Time first, not series first, because of how the rows lie on disk. Samples are
    /// inserted a sweep at a time, so the table's pages hold every series' readings from
    /// the same few minutes side by side. Deleting one series' day at a time touched a page
    /// for nearly every row — and came back to the same page once for every other series —
    /// which on 7M rows over 80 series took 100 ms a batch and six and a half minutes in
    /// all. Deleting an hour of everything empties whole pages in one go: the same
    /// rollup took 44 seconds.
    ///
    /// An hour, because that keeps a transaction small however the install is set up: an
    /// hour of 80 series probed every 30 seconds is 9,600 rows and about 40 ms, nowhere
    /// near the 10-second busy timeout a probe's insert would give up at — where rolling up
    /// a month in one statement would hold the write lock for minutes.
    /// </summary>
    private const long SliceSpan = Hour;

    /// <summary>
    /// The most series one transaction folds, so an install with thousands of them still
    /// commits in bounded time; the rest of the slice goes in the next transaction.
    /// </summary>
    private const int MaxSeriesPerBatch = 200;

    /// <summary>
    /// The least time the rollup leaves between transactions, when a batch was quick. It
    /// otherwise pauses for as long as the batch took — see <see cref="RollupAsync(DateTimeOffset, TimeSpan?, CancellationToken)"/>.
    /// </summary>
    private static readonly TimeSpan MinimumPause = TimeSpan.FromMilliseconds(5);

    private static long FloorHour(long unixSeconds) => unixSeconds - (unixSeconds % Hour + Hour) % Hour;

    /// <summary>Raw samples before this are summarised and deleted. Always the start of an hour.</summary>
    private long RawHorizon(DateTimeOffset now) =>
        FloorHour(now.ToUnixTimeSeconds() - (long)options.Value.RawRetention.TotalSeconds);

    /// <summary>Summaries before this are deleted; raw rows before it are deleted without one.</summary>
    private long HourlyHorizon(DateTimeOffset now) =>
        FloorHour(now.ToUnixTimeSeconds() - (long)options.Value.HourlyRetention.TotalSeconds);

    /// <summary>
    /// Folds raw samples older than the raw retention into hourly summaries and deletes
    /// them, one hour of every series per transaction.
    ///
    /// Only whole hours: the cut-off is the start of the hour the raw window begins in, so
    /// an hour is never summarised while it could still gain readings, and the hour that is
    /// being recorded now is never touched.
    ///
    /// Safe to stop anywhere and to run again. Each batch writes its summaries and deletes
    /// the rows they came from in one transaction, so a crash or a restart leaves every
    /// hour either still raw or summarised with its rows gone, never both and never
    /// neither. A second run finds nothing left to fold and changes nothing. Summaries are
    /// merged into an existing row for the same hour rather than replacing it — which only
    /// matters if raw rows ever reappear for an hour already rolled up (a backup restored
    /// over a newer database), and then replacing would throw the first summary away.
    ///
    /// Between batches it pauses for as long as the batch took, so it holds the write lock
    /// at most half the time. Anything else waiting to write — the monitor recording a
    /// sweep — gets its turn within a batch or two, rather than finding the lock taken
    /// every time it retries. Readers never wait at all: the database is in WAL mode.
    /// </summary>
    /// <param name="budget">
    /// Stop after about this long and report <see cref="RollupResult.Finished"/> false. The
    /// first run on a database that kept a month of raw samples has three weeks of them to
    /// fold; spreading that over a few runs keeps one run from looking stuck.
    /// </param>
    public Task<RollupResult> RollupAsync(TimeSpan? budget = null, CancellationToken ct = default) =>
        RollupAsync(DateTimeOffset.UtcNow, budget, ct);

    /// <summary><see cref="RollupAsync(TimeSpan?, CancellationToken)"/> as if it were <paramref name="now"/>, for tests.</summary>
    public async Task<RollupResult> RollupAsync(DateTimeOffset now, TimeSpan? budget, CancellationToken ct)
    {
        var rawHorizon = RawHorizon(now);
        var hourlyHorizon = HourlyHorizon(now);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var longest = TimeSpan.Zero;
        int batches = 0;
        long hours = 0, deleted = 0;

        await using var connection = await db.OpenAsync(ct);

        // Each series with something to fold, and the oldest raw row it has — found by a
        // seek apiece, since "the oldest row in the table" has no index of its own.
        var pending = new List<Series>();
        foreach (var (connectionId, metric) in await SeriesAsync(connection, ct))
        {
            if (await OldestAsync(connection, connectionId, metric, 0, ct) is { } oldest && oldest < rawHorizon)
                pending.Add(new Series(connectionId, metric, oldest));
        }

        var fold = connection.CreateCommand();
        fold.CommandText = FoldSql;
        var delete = connection.CreateCommand();
        delete.CommandText = """
            DELETE FROM samples
            WHERE connection_id = $c AND metric = $m AND ts >= $from AND ts < $to
            """;
        foreach (var cmd in new[] { fold, delete })
        {
            cmd.Parameters.Add("$c", SqliteType.Text);
            cmd.Parameters.Add("$m", SqliteType.Text);
            cmd.Parameters.Add("$from", SqliteType.Integer);
            cmd.Parameters.Add("$to", SqliteType.Integer);
        }

        while (pending.Count > 0)
        {
            if (budget is { } limit && clock.Elapsed > limit)
                return new RollupResult(batches, hours, deleted, longest, Finished: false);

            // The oldest slice anybody has, so a gap in the history (a week the NAS was
            // off) is stepped over rather than walked an hour at a time.
            var from = FloorHour(pending.Min(series => series.Oldest));
            var to = Math.Min(from + SliceSpan, rawHorizon);
            var due = pending.Where(series => series.Oldest < to).Take(MaxSeriesPerBatch).ToList();

            var started = clock.Elapsed;
            await using (var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct))
            {
                fold.Transaction = delete.Transaction = tx;
                foreach (var series in due)
                {
                    // Past both retentions there is nothing to keep, so those rows are only
                    // deleted — this is what expires raw samples older than a summary of
                    // them would be kept for.
                    if (to > hourlyHorizon)
                    {
                        Bind(fold, series, Math.Max(from, hourlyHorizon), to);
                        hours += await fold.ExecuteNonQueryAsync(ct);
                    }

                    Bind(delete, series, from, to);
                    deleted += await delete.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
            }
            batches++;

            var took = clock.Elapsed - started;
            if (took > longest)
                longest = took;
            await Task.Delay(took > MinimumPause ? took : MinimumPause, ct);

            foreach (var series in due)
            {
                pending.Remove(series);
                if (await OldestAsync(connection, series.ConnectionId, series.Metric, to, ct) is { } next && next < rawHorizon)
                    pending.Add(series with { Oldest = next });
            }
        }

        return new RollupResult(batches, hours, deleted, longest, Finished: true);
    }

    private sealed record Series(string ConnectionId, string Metric, long Oldest);

    private static void Bind(SqliteCommand cmd, Series series, long from, long to)
    {
        cmd.Parameters["$c"].Value = series.ConnectionId;
        cmd.Parameters["$m"].Value = series.Metric;
        cmd.Parameters["$from"].Value = from;
        cmd.Parameters["$to"].Value = to;
    }

    /// <summary>
    /// One series' raw rows in [$from, $to), summarised per hour and merged into
    /// samples_hourly. The CTE first because an upsert's SELECT cannot carry one, and the
    /// WHERE on the outer SELECT is SQLite's documented way of keeping ON CONFLICT from
    /// being parsed as part of a join. last_value is a seek for the row the hour's newest
    /// timestamp belongs to — rowid breaking a tie, as it does in LatestReadingsAsync —
    /// rather than a bare column beside MAX(ts), which SQLite only resolves reliably when
    /// the query has a single min() or max().
    /// </summary>
    private const string FoldSql = """
        WITH hours AS (
            SELECT ts / 3600 * 3600 AS hour_ts, MIN(value) AS lo, MAX(value) AS hi, AVG(value) AS mean,
                   COUNT(*) AS n, MAX(ts) AS last_ts
            FROM samples
            WHERE connection_id = $c AND metric = $m AND ts >= $from AND ts < $to
            GROUP BY hour_ts)
        INSERT INTO samples_hourly (connection_id, metric, hour_ts, min, max, avg, count, last_ts, last_value)
        SELECT $c, $m, hour_ts, lo, hi, mean, n, last_ts,
               (SELECT value FROM samples
                WHERE connection_id = $c AND metric = $m AND ts = hours.last_ts
                ORDER BY rowid DESC LIMIT 1)
        FROM hours WHERE true
        ON CONFLICT (connection_id, metric, hour_ts) DO UPDATE SET
            min = MIN(samples_hourly.min, excluded.min),
            max = MAX(samples_hourly.max, excluded.max),
            avg = (samples_hourly.avg * samples_hourly.count + excluded.avg * excluded.count)
                  / (samples_hourly.count + excluded.count),
            count = samples_hourly.count + excluded.count,
            last_value = CASE WHEN excluded.last_ts >= samples_hourly.last_ts
                              THEN excluded.last_value ELSE samples_hourly.last_value END,
            last_ts = MAX(samples_hourly.last_ts, excluded.last_ts)
        """;

    /// <summary>
    /// Every (connection, metric) pair with raw samples, hopped through the index — first
    /// from one connection to the next, then through each one's metric names — so listing
    /// a few hundred series is a few hundred seeks rather than a pass over every row.
    /// </summary>
    private static async Task<List<(string ConnectionId, string Metric)>> SeriesAsync(
        SqliteConnection connection, CancellationToken ct)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            WITH RECURSIVE
            connections(c) AS (
                SELECT MIN(connection_id) FROM samples
                UNION ALL
                SELECT (SELECT MIN(connection_id) FROM samples WHERE connection_id > connections.c)
                FROM connections WHERE connections.c IS NOT NULL),
            series(c, m) AS (
                SELECT c, (SELECT MIN(metric) FROM samples WHERE connection_id = connections.c)
                FROM connections WHERE c IS NOT NULL
                UNION ALL
                SELECT c, (SELECT MIN(metric) FROM samples WHERE connection_id = series.c AND metric > series.m)
                FROM series WHERE m IS NOT NULL)
            SELECT c, m FROM series WHERE m IS NOT NULL
            """;
        var list = new List<(string, string)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add((reader.GetString(0), reader.GetString(1)));
        return list;
    }

    /// <summary>The oldest raw timestamp of one series at or after <paramref name="from"/> — a single seek.</summary>
    private static async Task<long?> OldestAsync(
        SqliteConnection connection, string connectionId, string metric, long from, CancellationToken ct)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT MIN(ts) FROM samples WHERE connection_id = $c AND metric = $m AND ts >= $from";
        cmd.Parameters.AddWithValue("$c", connectionId);
        cmd.Parameters.AddWithValue("$m", metric);
        cmd.Parameters.AddWithValue("$from", from);
        return await cmd.ExecuteScalarAsync(ct) is long oldest ? oldest : null;
    }

    /// <summary>
    /// A metric's history as one point per hour, whatever the window — the hourly half of
    /// <see cref="SamplesAsync"/> without the choice. For the unusual-value baselines, which
    /// compare hours of the day and so want hours even for a window inside the raw retention
    /// (somebody who keeps a month of raw rows would otherwise hand them 80,000 readings to
    /// average themselves). The same index range reads: a seek into the summaries and a
    /// grouped range over the raw rows, never a scan of the connection's history.
    /// </summary>
    public async Task<IReadOnlyList<Sample>> HourlyAsync(string connectionId, string metric, TimeSpan window, CancellationToken ct = default)
    {
        var since = DateTimeOffset.UtcNow.Subtract(window).ToUnixTimeSeconds();
        await using var connection = await db.OpenAsync(ct);
        var summaries = await SummariesAsync(connection, connectionId, metric, since, ct);
        return await HourlySamplesAsync(connection, connectionId, metric, since, summaries, ct);
    }
}
