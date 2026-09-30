using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace LabbyTwo.Storage;

/// <summary>
/// Which statements are slow, and which read the most from disk — switched on with
/// <c>Labby__SlowQueryMs</c> for an install that is slow somewhere nobody can reproduce.
///
/// It exists because the question "why is my NAS slow" could not be answered from a log.
/// A page taking a minute says nothing about which of the few hundred statements behind it
/// was the one, or whether it was the page at all rather than a background job holding the
/// disk; and a statement that reads a lot is not always a slow one on the machine that is
/// asked, because the pages it reads came from the operating system's cache. So two things
/// are recorded:
/// <list type="bullet">
/// <item>Every statement that takes longer than the threshold is logged as it finishes,
/// with how long it took, how many pages it had to read, and what called it.</item>
/// <item>Every few minutes (<see cref="LabbyOptions.SlowQuerySummaryMinutes"/>) the statements
/// that read the most pages in that time are logged as a table, with how many commits there
/// were, however quick each statement was. That is the one that finds a query run every
/// sweep that reads a hundred megabytes each time — which is fast on an SSD and is the whole
/// disk's bandwidth on a NAS.</item>
/// </list>
///
/// Measured with SQLite's own counters rather than a stopwatch around each call: the
/// profile hook reports each statement's own time when it finishes, and the connection's
/// page-cache misses since the previous statement are the pages it read — from the WAL,
/// the operating system's cache or the disk, which SQLite cannot tell apart, but on an
/// install whose database does not fit in memory it is the disk.
///
/// Off by default, and free when off: nothing is attached to any connection.
/// </summary>
public sealed partial class QueryLog
{
    /// <summary>
    /// A statement reading at least this many pages has its caller worked out even when it
    /// was quick, so the summary can say who asked. Four megabytes at SQLite's default page
    /// size; smaller reads are not what makes a disk busy, and walking the stack of every
    /// statement would be the slowest thing here.
    /// </summary>
    private const int CallerPages = 1024;

    private const int CacheMiss = 8; // SQLITE_DBSTATUS_CACHE_MISS

    private readonly ILogger _log;
    private readonly TimeSpan _threshold;
    private readonly TimeSpan _summaryEvery;
    private readonly ConditionalWeakTable<sqlite3, object> _attached = [];
    private readonly ConcurrentDictionary<(string Sql, string Caller), Totals> _totals = new();
    private readonly Stopwatch _sinceSummary = Stopwatch.StartNew();
    private int _pageSize = 4096;
    private int _summarising;
    private long _commits;

    public QueryLog(TimeSpan threshold, TimeSpan summaryEvery, ILogger log)
    {
        _threshold = threshold;
        _summaryEvery = summaryEvery;
        _log = log;
    }

    /// <summary>
    /// Hooks one connection. Pooled connections come back with the same native handle, so a
    /// handle already hooked is left alone rather than hooked again on every open.
    /// </summary>
    public void Attach(SqliteConnection connection)
    {
        if (connection.Handle is not { } handle)
            return;
        lock (_attached)
        {
            if (_attached.TryGetValue(handle, out _))
                return;
            _attached.Add(handle, this);
        }

        using (var size = connection.CreateCommand())
        {
            size.CommandText = "PRAGMA page_size";
            if (size.ExecuteScalar() is long bytes and > 0)
                _pageSize = (int)bytes;
        }
        // Zero the counter so the first statement is not charged for opening the file.
        raw.sqlite3_db_status(handle, CacheMiss, out _, out _, 1);
        raw.sqlite3_profile(handle, OnProfile, handle);
        // Every commit takes the one write lock and, unless synchronous is off, syncs the
        // file; how many there are is half of why a writer can wait.
        raw.sqlite3_commit_hook(handle, _ =>
        {
            Interlocked.Increment(ref _commits);
            return 0;
        }, null);
    }

    private void OnProfile(object state, string sql, long nanoseconds)
    {
        var handle = (sqlite3)state;
        // Reset as it is read: the misses since the last statement on this connection
        // finished are this statement's reads.
        raw.sqlite3_db_status(handle, CacheMiss, out var pages, out _, 1);
        var took = TimeSpan.FromTicks(nanoseconds / 100);
        var slow = _threshold > TimeSpan.Zero && took >= _threshold;

        var caller = slow || pages >= CallerPages ? Caller() : "";
        var text = Shorten(sql);
        _totals.AddOrUpdate((text, caller),
            _ => new Totals(1, pages, took),
            (_, t) => new Totals(t.Count + 1, t.Pages + pages, t.Took + took));

        if (slow)
        {
            _log.LogWarning("Slow query: {Milliseconds} ms, {Read} read, from {Caller}: {Sql}",
                (long)took.TotalMilliseconds, Size((long)pages * _pageSize), caller, text);
        }

        if (_summaryEvery > TimeSpan.Zero && _sinceSummary.Elapsed >= _summaryEvery && Interlocked.Exchange(ref _summarising, 1) == 0)
        {
            try
            {
                Summarise();
            }
            finally
            {
                Volatile.Write(ref _summarising, 0);
            }
        }
    }

    private void Summarise()
    {
        var over = _sinceSummary.Elapsed;
        _sinceSummary.Restart();
        var taken = _totals.ToArray();
        _totals.Clear();
        if (taken.Length == 0)
            return;

        var pages = taken.Sum(t => t.Value.Pages);
        var commits = Interlocked.Exchange(ref _commits, 0);
        var report = new StringBuilder();
        report.Append(CultureInfo.InvariantCulture,
            $"SQLite read {Size(pages * _pageSize)} in {taken.Sum(t => t.Value.Count):N0} statements and committed {commits:N0} times over the last {over.TotalMinutes:0.#} min. Heaviest:");
        foreach (var (key, totals) in taken.OrderByDescending(t => t.Value.Pages).ThenByDescending(t => t.Value.Took).Take(8))
        {
            report.Append(CultureInfo.InvariantCulture,
                $"\n  {Size(totals.Pages * _pageSize),9} {totals.Count,6}x {(long)totals.Took.TotalMilliseconds,8} ms  {(key.Caller.Length > 0 ? key.Caller + ": " : "")}{key.Sql}");
        }
        _log.LogInformation("{Report}", report.ToString());
    }

    /// <summary>
    /// The first few frames of our own code above the store, as "Store.Method &lt; Caller.Method".
    /// Async methods appear as their state machines, <c>&lt;SamplesAsync&gt;d__12</c> nested
    /// in the class, so the name is taken from there.
    /// </summary>
    private static string Caller()
    {
        var names = new List<string>(3);
        foreach (var frame in new StackTrace(2, false).GetFrames())
        {
            if (frame.GetMethod() is not { DeclaringType: { } type } method)
                continue;
            // A state machine (<SamplesAsync>d__12) or a lambda (<Load>b__0, perhaps inside
            // a <>c__DisplayClass) is named after the method it came from.
            var owner = type;
            var name = Unmangle(method.Name);
            while (owner.Name.StartsWith('<') && owner.DeclaringType is { } outer)
            {
                if (Unmangle(owner.Name) is { Length: > 0 } from && !method.Name.StartsWith('<'))
                    name = from;
                owner = outer;
            }
            if (owner.Namespace?.StartsWith("LabbyTwo", StringComparison.Ordinal) != true || owner == typeof(QueryLog))
                continue;
            var label = $"{owner.Name}.{name}";
            if (names.Count == 0 || names[^1] != label)
                names.Add(label);
            if (names.Count == 3)
                break;
        }
        return string.Join(" < ", names);

        static string Unmangle(string name) =>
            name.StartsWith('<') && name.IndexOf('>') is > 1 and var end ? name[1..end] : name;
    }

    /// <summary>One line, and not the whole of a long statement: enough to find it in the source.</summary>
    private static string Shorten(string sql)
    {
        var line = Whitespace().Replace(sql, " ").Trim();
        return line.Length <= 160 ? line : line[..157] + "...";
    }

    private static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.0} GB"),
        >= 1L << 20 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):0.0} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0} KB"),
    };

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private readonly record struct Totals(long Count, long Pages, TimeSpan Took);
}
