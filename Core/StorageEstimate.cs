namespace LabbyTwo.Core;

/// <summary>
/// One series' share of the database, as the storage survey estimated it. Times are Unix
/// seconds, as stored.
/// </summary>
/// <param name="RawRows">Raw readings held, estimated (see <see cref="StorageMath.EstimateRows"/>).</param>
/// <param name="RawPerDay">Raw readings written in a day at the current rate; zero for a series that has stopped.</param>
/// <param name="HourlyRows">Hourly summaries held, estimated the same way.</param>
public sealed record SeriesSize(
    string ConnectionId,
    string Metric,
    long RawRows,
    long? RawFirst,
    long? RawLast,
    double RawPerDay,
    long HourlyRows,
    long? HourlyFirst,
    long? HourlyLast)
{
    public long RawBytes => (long)(RawRows * StorageMath.RawRowBytes(ConnectionId, Metric));
    public long HourlyBytes => (long)(HourlyRows * StorageMath.HourlyRowBytes(ConnectionId, Metric));
    public long Bytes => RawBytes + HourlyBytes;

    /// <summary>Bytes of raw readings written a day. Not growth: past the retention as much is removed as is written.</summary>
    public double RawBytesPerDay => RawPerDay * StorageMath.RawRowBytes(ConnectionId, Metric);
}

/// <summary>What the storage survey found, and what it cost to find it.</summary>
/// <param name="PagesRead">Database pages the survey had to fetch — from the disk or the operating system's cache.</param>
/// <param name="Exact">True when every series was counted row by row rather than estimated (only ever for tests and measurement).</param>
public sealed record StorageSurvey(
    DateTimeOffset At,
    TimeSpan Took,
    long PagesRead,
    int PageSize,
    IReadOnlyList<SeriesSize> Series,
    bool Exact = false)
{
    public long RawRows => Series.Sum(s => s.RawRows);
    public long HourlyRows => Series.Sum(s => s.HourlyRows);
    public long RawBytes => Series.Sum(s => s.RawBytes);
    public long HourlyBytes => Series.Sum(s => s.HourlyBytes);
}

/// <summary>What changing a series' retention, or no longer recording it, would do.</summary>
/// <param name="RowsRemoved">Raw readings the next tidy-up would summarise and delete.</param>
/// <param name="HourlyRowsAdded">Hourly summaries it would write in their place.</param>
/// <param name="BytesFreed">Space freed inside the file straight away (after the tidy-up); never negative.</param>
/// <param name="BytesGrowth">Space a longer retention will take as it fills up; never negative.</param>
public sealed record StorageEffect(long RowsRemoved, long HourlyRowsAdded, long BytesFreed, long BytesGrowth)
{
    public static readonly StorageEffect None = new(0, 0, 0, 0);

    public StorageEffect Plus(StorageEffect other) => new(
        RowsRemoved + other.RowsRemoved, HourlyRowsAdded + other.HourlyRowsAdded,
        BytesFreed + other.BytesFreed, BytesGrowth + other.BytesGrowth);
}

/// <summary>
/// The arithmetic behind the storage page: how big a row is, how many rows a series holds,
/// and what a retention change would free. Pure, so every number on that page can be
/// checked against a database whose contents are known.
///
/// <para><b>Why estimates, not counts.</b> Counting a series' rows means reading every
/// entry of it in the index. Across every series that is the whole index — on the install
/// that prompted this, 6.2 million rows and a couple of hundred megabytes, which on NAS
/// disks is minutes of reading and exactly the kind of load v1.10.0 taught us to keep off
/// the disk. So each series is measured by its ends and a few samples of its middle: the
/// first and last timestamps are one seek each, and the rows in a handful of short windows
/// spread across the range are small range counts in the covering index. Readings arrive at
/// a steady rate, so the rows in the samples, scaled up to the whole range, are the rows in
/// the range — within a few per cent, even across a gap when the NAS was off, because some
/// of the windows land in the gap too.</para>
///
/// <para><b>Why bytes are worked out rather than measured.</b> SQLite can report the pages
/// each table and index uses (the dbstat table), but the SQLite bundled here is built
/// without it, and it reads every page anyway. A row's size is known exactly from how
/// SQLite lays out a record — a header, the two names, the time, the value — in the table
/// and again in the covering index, and how full pages are left by the way LabbyTwo writes
/// was measured: see <see cref="RawFill"/>.</para>
/// </summary>
public static class StorageMath
{
    /// <summary>How many short windows of a series are counted to estimate it.</summary>
    public const int Windows = 6;

    /// <summary>
    /// How long each counted window of raw readings is. Three hours of a 30-second probe is
    /// 360 index entries — two or three pages — which is enough rows that one missed probe
    /// does not move the estimate, and few enough that six of them are nothing to read.
    /// </summary>
    public const long RawWindowSeconds = 3 * 3600;

    /// <summary>Each counted window of hourly summaries: a day, which is at most 24 rows.</summary>
    public const long HourlyWindowSeconds = 86400;

    /// <summary>
    /// How full SQLite leaves the pages of samples and its covering index when rows arrive a
    /// sweep at a time and the oldest hour is deleted as new ones come in. Measured by writing
    /// a week of 70 series that way (scratch benchmark, see the pull request that added this):
    /// 94.6 bytes a row in use against 90.3 once packed by VACUUM, so 0.96. The table fills its
    /// pages completely, since rowids only grow; each series' part of the index grows at its
    /// own end, which SQLite also splits without leaving much room.
    /// </summary>
    public const double RawFill = 0.96;

    /// <summary>The same for samples_hourly and its age index, written an hour of every series at a time: 0.88.</summary>
    public const double HourlyFill = 0.88;

    /// <summary>
    /// Bytes one raw reading takes, table and covering index together, for a series whose
    /// connection id and metric name are this long.
    ///
    /// The table's record: a header byte per column plus its length (5), both names, the
    /// time as a 4-byte integer (6 bytes after 2038), the value as an 8-byte float; then the
    /// cell's length and rowid as varints (4) and its 2-byte pointer. The index entry holds
    /// the same columns and the rowid in its record. Together, 2 × (the names) + 47, over
    /// how full the pages are.
    /// </summary>
    public static double RawRowBytes(int keyLength) => (2.0 * keyLength + 47) / RawFill;

    /// <inheritdoc cref="RawRowBytes(int)"/>
    public static double RawRowBytes(string connectionId, string metric) => RawRowBytes(KeyLength(connectionId, metric));

    /// <summary>
    /// Bytes one hourly summary takes: the row itself — the two names, the hour, three
    /// floats, the count, the last time and value, about the names + 54 — and its entry in
    /// ix_samples_hourly_age, which carries the hour and the whole primary key again, about
    /// the names + 10. Over how full the pages are.
    /// </summary>
    public static double HourlyRowBytes(int keyLength) => (2.0 * keyLength + 64) / HourlyFill;

    /// <inheritdoc cref="HourlyRowBytes(int)"/>
    public static double HourlyRowBytes(string connectionId, string metric) => HourlyRowBytes(KeyLength(connectionId, metric));

    private static int KeyLength(string connectionId, string metric) =>
        System.Text.Encoding.UTF8.GetByteCount(connectionId) + System.Text.Encoding.UTF8.GetByteCount(metric);

    /// <summary>
    /// Where to count, for a series running from <paramref name="first"/> to
    /// <paramref name="last"/>: <see cref="Windows"/> windows <paramref name="width"/> long,
    /// evenly spread, the first starting at the first reading and the last ending just after
    /// the newest. A series shorter than all of them together gets one window over the whole
    /// of it, which is then an exact count. Half-open, [from, to).
    /// </summary>
    public static IReadOnlyList<(long From, long To)> SampleWindows(long first, long last, long width)
    {
        var span = last - first + 1;
        if (span <= width * Windows)
            return [(first, last + 1)];

        var windows = new List<(long, long)>(Windows);
        for (var i = 0; i < Windows; i++)
        {
            var from = first + (span - width) * i / (Windows - 1);
            windows.Add((from, from + width));
        }
        return windows;
    }

    /// <summary>
    /// Rows in a series, from the counts in its <see cref="SampleWindows"/>. With one window
    /// the count is the answer; otherwise the rows per second seen in the windows, over the
    /// series' whole range.
    /// </summary>
    public static long EstimateRows(long first, long last, long width, IReadOnlyList<long> counts)
    {
        if (counts.Count == 1)
            return counts[0];
        var seen = counts.Sum();
        var span = last - first + 1;
        return (long)Math.Round(seen * (double)span / (width * counts.Count));
    }

    /// <summary>
    /// What moving a series' raw horizon from <paramref name="currentHorizon"/> to
    /// <paramref name="newHorizon"/> would do: raw rows before a horizon are summarised into
    /// hours and deleted. A later horizon frees space now (the rows, less the summaries
    /// written for them); an earlier one takes more as the extra days fill up.
    /// </summary>
    /// <param name="hourlyHorizon">Summaries are not written for hours before this; they are past their own retention.</param>
    public static StorageEffect Effect(SeriesSize series, long currentHorizon, long newHorizon, long hourlyHorizon)
    {
        var rawBytes = RawRowBytes(series.ConnectionId, series.Metric);
        var hourlyBytes = HourlyRowBytes(series.ConnectionId, series.Metric);

        if (newHorizon > currentHorizon)
        {
            if (series.RawRows == 0 || series.RawFirst is not { } first || series.RawLast is not { } last || newHorizon <= first)
                return StorageEffect.None;

            // Rows are spread evenly over [first, last], so the share before the new horizon
            // is the share of the time.
            var removed = newHorizon > last
                ? series.RawRows
                : (long)Math.Round(series.RawRows * (double)(newHorizon - first) / Math.Max(1, last - first + 1));
            var foldFrom = HistoryPolicy.FloorHour(Math.Max(first, hourlyHorizon));
            var foldTo = Math.Min(newHorizon, HistoryPolicy.FloorHour(last) + 3600);
            var hours = foldTo > foldFrom ? (foldTo - foldFrom) / 3600 : 0;
            var freed = (long)(removed * rawBytes - hours * hourlyBytes);
            return new StorageEffect(removed, hours, Math.Max(0, freed), 0);
        }

        if (newHorizon < currentHorizon && series.RawPerDay > 0)
        {
            // Kept longer: nothing is freed, and once the window has filled there are this
            // many more days of raw rows, less the summaries that no longer need writing.
            var days = (currentHorizon - newHorizon) / 86400.0;
            var growth = (long)(series.RawPerDay * days * rawBytes - days * 24 * hourlyBytes);
            return new StorageEffect(0, 0, 0, Math.Max(0, growth));
        }

        return StorageEffect.None;
    }
}
