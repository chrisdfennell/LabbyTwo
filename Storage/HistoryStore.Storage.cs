using System.Diagnostics;
using LabbyTwo.Core;
using Microsoft.Data.Sqlite;

namespace LabbyTwo.Storage;

/// <summary>What the database file looks like from the outside and from its header — nothing here reads a table.</summary>
/// <param name="AutoVacuum">0 none, 1 full, 2 incremental — SQLite's own numbers.</param>
/// <param name="DiskFree">Free space on the volume the database is on, or null when the system would not say.</param>
/// <param name="TempFree">Free space where SQLite writes temporary files — where VACUUM builds its copy.</param>
public sealed record DatabaseFacts(
    string Path,
    long FileBytes,
    long WalBytes,
    int PageSize,
    long Pages,
    long FreePages,
    int AutoVacuum,
    long? DiskFree,
    long? DiskTotal,
    long? TempFree)
{
    /// <summary>Bytes of the file holding data. The rest of it is free pages, reused before the file grows.</summary>
    public long UsedBytes => (Pages - FreePages) * PageSize;

    /// <summary>Bytes of free pages inside the file: what a compaction would give back to the disk.</summary>
    public long FreeBytes => FreePages * PageSize;

    public bool Incremental => AutoVacuum == 2;

    /// <summary>
    /// Why a full compaction should not start, or null. VACUUM writes a complete copy of the
    /// data to a temporary file, then writes it all again through the write-ahead log beside
    /// the database: about the used size twice over. Running out of disk half way is the
    /// one way it can go badly, so it is refused rather than attempted.
    /// </summary>
    public string? FullCompactionProblem()
    {
        if (DiskFree is { } free && free < UsedBytes * 2)
            return $"It needs about {StorageFormat.Bytes(UsedBytes * 2)} free next to the database while it runs, " +
                   $"and there is {StorageFormat.Bytes(free)}.";
        if (TempFree is { } temp && temp < UsedBytes)
            return $"It needs about {StorageFormat.Bytes(UsedBytes)} free for its temporary copy, and the temporary " +
                   $"folder has {StorageFormat.Bytes(temp)}.";
        return null;
    }
}

/// <summary>What a compaction did.</summary>
/// <param name="Full">True for a VACUUM; false for returning free pages a few at a time.</param>
/// <param name="Dropped">Readings not written to history while it held the database.</param>
public sealed record CompactResult(bool Full, long BytesBefore, long BytesAfter, TimeSpan Took, long Dropped)
{
    public long Saved => Math.Max(0, BytesBefore - BytesAfter);
}

/// <summary>"580 MB", in the units a person reads.</summary>
public static class StorageFormat
{
    public static string Bytes(double value)
    {
        var sign = value < 0 ? "-" : "";
        value = Math.Abs(value);
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{sign}{value:0} B" : value >= 100 ? $"{sign}{value:0} {units[unit]}" : $"{sign}{value:0.#} {units[unit]}";
    }

    /// <summary>"6.2 million", "48,000" — a row count as somebody would say it.</summary>
    public static string Rows(long rows) => rows switch
    {
        >= 1_000_000 => $"{rows / 1_000_000.0:0.#} million",
        >= 10_000 => $"{rows / 1000.0:0}k",
        _ => $"{rows:N0}",
    };
}

public sealed partial class HistoryStore
{
    private volatile HistoryPolicy? _policy;
    private readonly SemaphoreSlim _policyLoad = new(1, 1);

    /// <summary>Raised after the policy is saved.</summary>
    public event Action? PolicyChanged;

    /// <summary>
    /// What history is kept, where it differs from the global settings. Read from the
    /// database once and held: every sweep asks, for every connection that reported numbers,
    /// and must never wait on a query to decide what to write. If the read fails the
    /// defaults are used for now and it is tried again next time, since recording everything
    /// for a sweep is the harmless mistake.
    /// </summary>
    public async Task<HistoryPolicy> PolicyAsync(CancellationToken ct = default)
    {
        if (_policy is { } policy)
            return policy;
        // After a failed read, the defaults for a minute rather than a query on every write.
        if (Environment.TickCount64 < Volatile.Read(ref _policyRetryAt))
            return HistoryPolicy.Default;

        await _policyLoad.WaitAsync(ct);
        try
        {
            if (_policy is { } loaded)
                return loaded;

            await using var connection = await db.OpenAsync(ct);
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT connection_id, metric, raw_days, record FROM history_policy";
            var entries = new List<HistoryPolicyEntry>();
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    entries.Add(new HistoryPolicyEntry(reader.GetString(0), reader.GetString(1),
                        reader.IsDBNull(2) ? null : (int)reader.GetInt64(2), reader.GetInt64(3) != 0));
                }
            }
            return _policy = new HistoryPolicy(entries);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Volatile.Write(ref _policyRetryAt, Environment.TickCount64 + 60_000);
            return HistoryPolicy.Default;
        }
        finally
        {
            _policyLoad.Release();
        }
    }

    private long _policyRetryAt;

    /// <summary>Replaces the policy. Nothing is deleted here: the next rollup applies it, a batch at a time.</summary>
    public async Task SavePolicyAsync(HistoryPolicy policy, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var clear = connection.CreateCommand();
        clear.CommandText = "DELETE FROM history_policy";
        await clear.ExecuteNonQueryAsync(ct);

        var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO history_policy (connection_id, metric, raw_days, record) VALUES ($c, $m, $d, $r)";
        var c = insert.Parameters.Add("$c", SqliteType.Text);
        var m = insert.Parameters.Add("$m", SqliteType.Text);
        var d = insert.Parameters.Add("$d", SqliteType.Integer);
        var r = insert.Parameters.Add("$r", SqliteType.Integer);
        foreach (var entry in policy.Entries)
        {
            c.Value = entry.ConnectionId;
            m.Value = entry.Metric;
            d.Value = (object?)entry.RawDays ?? DBNull.Value;
            r.Value = entry.Record ? 1 : 0;
            await insert.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);

        _policy = new HistoryPolicy(policy.Entries);
        PolicyChanged?.Invoke();
    }

    // ---------- The survey ----------

    /// <summary>Every series in the summaries, hopped through their primary key as <see cref="SeriesSql"/> hops the index.</summary>
    public const string HourlySeriesSql = """
        WITH RECURSIVE
        connections(c) AS (
            SELECT MIN(connection_id) FROM samples_hourly
            UNION ALL
            SELECT (SELECT MIN(connection_id) FROM samples_hourly WHERE connection_id > connections.c)
            FROM connections WHERE connections.c IS NOT NULL),
        series(c, m) AS (
            SELECT c, (SELECT MIN(metric) FROM samples_hourly WHERE connection_id = connections.c)
            FROM connections WHERE c IS NOT NULL
            UNION ALL
            SELECT c, (SELECT MIN(metric) FROM samples_hourly WHERE connection_id = series.c AND metric > series.m)
            FROM series WHERE m IS NOT NULL)
        SELECT c, m FROM series WHERE m IS NOT NULL
        """;

    /// <summary>
    /// A raw series' first and last reading: two seeks. Two sub-selects rather than MIN and MAX
    /// side by side, which SQLite answers by reading the whole range (see
    /// <see cref="Services.SystemHealth.SampleEstimateSql"/>).
    /// </summary>
    public const string SurveyRawEndsSql = """
        SELECT (SELECT MIN(ts) FROM samples WHERE connection_id = $c AND metric = $m),
               (SELECT MAX(ts) FROM samples WHERE connection_id = $c AND metric = $m)
        """;

    /// <summary>Rows of one raw series in one window: a short range of the covering index.</summary>
    public const string SurveyRawCountSql =
        "SELECT COUNT(*) FROM samples WHERE connection_id = $c AND metric = $m AND ts >= $from AND ts < $to";

    /// <inheritdoc cref="SurveyRawEndsSql"/>
    public const string SurveyHourlyEndsSql = """
        SELECT (SELECT MIN(hour_ts) FROM samples_hourly WHERE connection_id = $c AND metric = $m),
               (SELECT MAX(hour_ts) FROM samples_hourly WHERE connection_id = $c AND metric = $m)
        """;

    /// <inheritdoc cref="SurveyRawCountSql"/>
    public const string SurveyHourlyCountSql =
        "SELECT COUNT(*) FROM samples_hourly WHERE connection_id = $c AND metric = $m AND hour_ts >= $from AND hour_ts < $to";

    private const int CacheMiss = 8; // SQLITE_DBSTATUS_CACHE_MISS

    /// <summary>
    /// How big each series is — rows and bytes, raw and hourly — and how fast it grows,
    /// estimated from a handful of index seeks per series rather than counted (see
    /// <see cref="StorageMath"/> for why). Built for a background job: it works one series at
    /// a time on a connection of its own, pausing <paramref name="pause"/> between them so
    /// that whatever else wants the disk gets it, and it only ever reads.
    /// </summary>
    /// <param name="exact">Count every row instead. Reads the whole index; for measuring the estimate, never for the app.</param>
    public async Task<StorageSurvey> SurveyAsync(DateTimeOffset now, TimeSpan pause, bool exact = false, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        await using var connection = await db.OpenPrivateAsync(ct);
        var handle = connection.Handle!;
        SQLitePCL.raw.sqlite3_db_status(handle, CacheMiss, out _, out _, 1);

        var raw = await SeriesAsync(connection, ct);
        var hourly = await ListSeriesAsync(connection, HourlySeriesSql, ct);
        var keys = raw.Concat(hourly).Distinct().OrderBy(k => k.ConnectionId, StringComparer.Ordinal)
            .ThenBy(k => k.Metric, StringComparer.Ordinal).ToList();
        var rawSet = raw.ToHashSet();
        var hourlySet = hourly.ToHashSet();

        var rawEnds = Command(connection, SurveyRawEndsSql, withRange: false);
        var rawCount = Command(connection, SurveyRawCountSql, withRange: true);
        var hourlyEnds = Command(connection, SurveyHourlyEndsSql, withRange: false);
        var hourlyCount = Command(connection, SurveyHourlyCountSql, withRange: true);
        var nowSeconds = now.ToUnixTimeSeconds();

        var series = new List<SeriesSize>(keys.Count);
        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();

            long rawRows = 0, hourlyRows = 0;
            long? rawFirst = null, rawLast = null, hourlyFirst = null, hourlyLast = null;
            double perDay = 0;

            if (rawSet.Contains(key) && await EndsAsync(rawEnds, key, ct) is ({ } first, { } last))
            {
                (rawFirst, rawLast) = (first, last);
                IReadOnlyList<(long, long)> windows = exact
                    ? [(first, last + 1)]
                    : StorageMath.SampleWindows(first, last, StorageMath.RawWindowSeconds);
                var counts = await CountsAsync(rawCount, key, windows, ct);
                rawRows = StorageMath.EstimateRows(first, last, StorageMath.RawWindowSeconds, counts);

                // The rate now, from the newest window — which ends at the newest reading —
                // and nothing for a series that has not reported for a day: it has stopped.
                if (last >= nowSeconds - 86400)
                {
                    var newest = windows.Count > 1
                        ? counts[^1]
                        : await CountsAsync(rawCount, key, [(Math.Max(first, last + 1 - StorageMath.RawWindowSeconds), last + 1)], ct) is [var n] ? n : 0;
                    var span = Math.Min(StorageMath.RawWindowSeconds, Math.Max(3600, last + 1 - first));
                    perDay = newest * 86400.0 / span;
                }
            }

            if (hourlySet.Contains(key) && await EndsAsync(hourlyEnds, key, ct) is ({ } hFirst, { } hLast))
            {
                (hourlyFirst, hourlyLast) = (hFirst, hLast);
                IReadOnlyList<(long, long)> windows = exact
                    ? [(hFirst, hLast + 1)]
                    : StorageMath.SampleWindows(hFirst, hLast, StorageMath.HourlyWindowSeconds);
                hourlyRows = StorageMath.EstimateRows(hFirst, hLast, StorageMath.HourlyWindowSeconds,
                    await CountsAsync(hourlyCount, key, windows, ct));
            }

            series.Add(new SeriesSize(key.ConnectionId, key.Metric, rawRows, rawFirst, rawLast, perDay, hourlyRows, hourlyFirst, hourlyLast));
            if (pause > TimeSpan.Zero)
                await Task.Delay(pause, ct);
        }

        SQLitePCL.raw.sqlite3_db_status(handle, CacheMiss, out var pages, out _, 0);
        var pageSize = (int)await ScalarAsync(connection, "PRAGMA page_size", ct);
        return new StorageSurvey(now, clock.Elapsed, pages, pageSize, series, exact);
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, bool withRange)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add("$c", SqliteType.Text);
        cmd.Parameters.Add("$m", SqliteType.Text);
        if (withRange)
        {
            cmd.Parameters.Add("$from", SqliteType.Integer);
            cmd.Parameters.Add("$to", SqliteType.Integer);
        }
        return cmd;
    }

    private static async Task<(long? First, long? Last)> EndsAsync(SqliteCommand cmd, (string ConnectionId, string Metric) key, CancellationToken ct)
    {
        cmd.Parameters["$c"].Value = key.ConnectionId;
        cmd.Parameters["$m"].Value = key.Metric;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.IsDBNull(0) || reader.IsDBNull(1))
            return (null, null);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task<List<long>> CountsAsync(
        SqliteCommand cmd, (string ConnectionId, string Metric) key, IReadOnlyList<(long From, long To)> windows, CancellationToken ct)
    {
        cmd.Parameters["$c"].Value = key.ConnectionId;
        cmd.Parameters["$m"].Value = key.Metric;
        var counts = new List<long>(windows.Count);
        foreach (var (from, to) in windows)
        {
            cmd.Parameters["$from"].Value = from;
            cmd.Parameters["$to"].Value = to;
            counts.Add(Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)));
        }
        return counts;
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    // ---------- The file ----------

    /// <summary>
    /// The file's size, its free pages and whether it can be compacted a little at a time.
    /// Every figure is from the file system or the database header — PRAGMA page_count and
    /// freelist_count read one page — so this is safe to ask whenever a page opens.
    /// </summary>
    public async Task<DatabaseFacts> FactsAsync(CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var pageSize = (int)await ScalarAsync(connection, "PRAGMA page_size", ct);
        var pages = await ScalarAsync(connection, "PRAGMA page_count", ct);
        var free = await ScalarAsync(connection, "PRAGMA freelist_count", ct);
        var autoVacuum = (int)await ScalarAsync(connection, "PRAGMA auto_vacuum", ct);

        var path = db.FilePath;
        var (diskFree, diskTotal) = Space(Path.GetDirectoryName(path)!);
        var (tempFree, _) = Space(Path.GetTempPath());
        return new DatabaseFacts(path, Length(path), Length(path + "-wal"), pageSize, pages, free, autoVacuum,
            diskFree, diskTotal, tempFree);
    }

    /// <summary>Free and total bytes on the volume holding <paramref name="directory"/>, or nulls if the system will not say.</summary>
    public static (long? Free, long? Total) Space(string directory)
    {
        try
        {
            // On Linux any path on the volume will do — it is asked with statvfs — which is
            // what makes this the data volume inside a container rather than the root.
            var drive = new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(Path.GetFullPath(directory))! : directory);
            return (drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, null);
        }
    }

    private static long Length(string path) => new FileInfo(path) is { Exists: true } info ? info.Length : 0;

    /// <summary>Free pages and the page size: two header reads, taken around a rollup to say what it freed.</summary>
    private async Task<(long Free, int PageSize)> FreePagesAsync(CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        return (await ScalarAsync(connection, "PRAGMA freelist_count", ct), (int)await ScalarAsync(connection, "PRAGMA page_size", ct));
    }

    // ---------- Compaction ----------

    /// <summary>Pages handed back per step of an incremental compaction: 8 MB at 4 KB a page.</summary>
    private const int IncrementalStep = 2048;

    /// <summary>
    /// Gives the database's free pages back to the disk.
    ///
    /// <para>If the file is set up for it (auto_vacuum = INCREMENTAL), a step at a time: each
    /// <c>PRAGMA incremental_vacuum</c> moves a few megabytes and commits, taking its turn
    /// with the monitor's writes like a rollup batch, so nothing waits long.</para>
    ///
    /// <para>Otherwise with VACUUM, which rewrites the whole file and holds it while it does:
    /// minutes on a NAS for a few hundred megabytes. Readers carry on (WAL), but nothing can
    /// be written, so for its duration the monitor's readings are skipped rather than queued
    /// (<see cref="WriteHealth.Compacting"/>) — a gap in the charts rather than a frozen
    /// dashboard. With <paramref name="makeIncremental"/> it also switches the file to
    /// incremental, which only a VACUUM can do, so every compaction after it is the quick
    /// kind. Never started automatically; the storage page asks, and says all of this.</para>
    /// </summary>
    public async Task<CompactResult> CompactAsync(bool makeIncremental, Action<string>? progress = null, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        var before = await FactsAsync(ct);
        var droppedBefore = Writes.Status(DateTimeOffset.Now).Dropped;

        if (before.Incremental)
        {
            await using var connection = await db.OpenAsync(ct);
            var step = connection.CreateCommand();
            step.CommandText = $"PRAGMA incremental_vacuum({IncrementalStep})";
            var left = before.FreePages;
            while (left > 0)
            {
                ct.ThrowIfCancellationRequested();
                var started = clock.Elapsed;
                await _writes.WaitAsync(ct);
                try
                {
                    await step.ExecuteNonQueryAsync(ct);
                }
                finally
                {
                    _writes.Release();
                }
                var remaining = await ScalarAsync(connection, "PRAGMA freelist_count", ct);
                if (remaining >= left)
                    break; // nothing moved: another writer is using the free pages as fast
                left = remaining;
                progress?.Invoke($"{StorageFormat.Bytes(left * before.PageSize)} still to give back");
                var took = clock.Elapsed - started;
                await Task.Delay(took > TimeSpan.FromMilliseconds(5) ? took : TimeSpan.FromMilliseconds(5), ct);
            }
            await CheckpointAsync(connection, ct);
        }
        else
        {
            Writes.Compacting = true;
            await _writes.WaitAsync(ct);
            try
            {
                progress?.Invoke("Rewriting the database");
                await using var connection = await db.OpenPrivateAsync(ct);
                var cmd = connection.CreateCommand();
                cmd.CommandTimeout = 0;
                cmd.CommandText = makeIncremental ? "PRAGMA auto_vacuum = INCREMENTAL; VACUUM;" : "VACUUM;";
                await cmd.ExecuteNonQueryAsync(ct);
                progress?.Invoke("Tidying the write-ahead log");
                await CheckpointAsync(connection, ct);
            }
            finally
            {
                Writes.Compacting = false;
                _writes.Release();
            }
        }

        var after = await FactsAsync(ct);
        return new CompactResult(!before.Incremental, before.FileBytes + before.WalBytes, after.FileBytes + after.WalBytes,
            clock.Elapsed, Writes.Status(DateTimeOffset.Now).Dropped - droppedBefore);
    }

    /// <summary>
    /// Folds the write-ahead log back into the database and truncates it: after a VACUUM the
    /// log holds a copy of the whole file. Best effort — a reader in the middle of something
    /// keeps part of it, and the next automatic checkpoint gets the rest.
    /// </summary>
    private static async Task CheckpointAsync(SqliteConnection connection, CancellationToken ct)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
