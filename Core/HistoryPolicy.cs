namespace LabbyTwo.Core;

/// <summary>
/// One choice about a connection's history: how many days of raw readings to keep for the
/// whole connection (<see cref="Metric"/> empty), or whether to record one metric at all.
/// </summary>
/// <param name="Metric">Empty for a choice about the whole connection.</param>
/// <param name="RawDays">Days of raw readings to keep, or null for the global setting. Only on a connection-wide entry.</param>
/// <param name="Record">False means the metric's live value still shows, but nothing more of it is written to history.</param>
public sealed record HistoryPolicyEntry(string ConnectionId, string Metric, int? RawDays, bool Record);

/// <summary>
/// What history is kept, where it differs from the global settings: a weather station that
/// only needs a week of raw readings beside a NAS that keeps a month, and the odd noisy
/// metric that nobody has ever charted.
///
/// Held in memory by <see cref="Storage.HistoryStore"/> once read, because it is consulted
/// on every write of every sweep, and a sweep must never wait on the database to decide
/// what to write. It is a handful of rows, read once and replaced whole when saved.
///
/// Why raw retention is per connection rather than per metric: raw rows are what cost
/// space, and they cost it per connection in practice — a weather station reporting thirty
/// numbers every thirty seconds is most of a database, a ping is almost none of it. Per
/// metric is one more thing to understand for little gain, while "stop recording this one"
/// covers the metric that really is noise.
/// </summary>
public sealed class HistoryPolicy
{
    /// <summary>The longest raw retention a connection may ask for, ten years: past that it is a typo.</summary>
    public const int MaxRawDays = 3650;

    public static readonly HistoryPolicy Default = new([]);

    private readonly Dictionary<string, int> _rawDays = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unrecorded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _connectionsWithUnrecorded = new(StringComparer.Ordinal);

    public HistoryPolicy(IEnumerable<HistoryPolicyEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Metric.Length == 0)
            {
                if (entry.RawDays is { } days)
                    _rawDays[entry.ConnectionId] = Math.Clamp(days, 1, MaxRawDays);
            }
            else if (!entry.Record)
            {
                _unrecorded.Add(Key(entry.ConnectionId, entry.Metric));
                _connectionsWithUnrecorded.Add(entry.ConnectionId);
            }
        }
        Entries =
        [
            .. _rawDays.Select(pair => new HistoryPolicyEntry(pair.Key, "", pair.Value, true)),
            .. _unrecorded.Select(key => key.Split('\n') is [var c, var m] ? new HistoryPolicyEntry(c, m, null, false) : null)
                .OfType<HistoryPolicyEntry>(),
        ];
    }

    /// <summary>Every choice, normalised: one entry per connection override and one per metric not recorded.</summary>
    public IReadOnlyList<HistoryPolicyEntry> Entries { get; }

    /// <summary>The connection's own raw retention in days, or null when it follows the global one.</summary>
    public int? RawDaysFor(string connectionId) => _rawDays.TryGetValue(connectionId, out var days) ? days : null;

    /// <summary>How long raw readings are kept for this connection.</summary>
    public TimeSpan RawRetentionFor(string connectionId, TimeSpan global) =>
        RawDaysFor(connectionId) is { } days ? TimeSpan.FromDays(days) : global;

    /// <summary>Whether this metric's readings are written to history. True unless somebody said otherwise.</summary>
    public bool Records(string connectionId, string metric) =>
        !_connectionsWithUnrecorded.Contains(connectionId) || !_unrecorded.Contains(Key(connectionId, metric));

    /// <summary>True when some metric on this connection is not recorded: the fast way past the check for everything else.</summary>
    public bool HasUnrecorded(string connectionId) => _connectionsWithUnrecorded.Contains(connectionId);

    /// <summary>
    /// Raw rows of this series older than the returned time are due to be summarised into
    /// hours and deleted. Always the start of an hour. For a metric that is not recorded any
    /// more that is the current hour: whatever it did record is folded into hourly summaries
    /// straight away — so a chart of its past still draws — and the raw rows go.
    /// </summary>
    public long RawHorizon(string connectionId, string metric, DateTimeOffset now, TimeSpan global)
    {
        var seconds = now.ToUnixTimeSeconds();
        if (!Records(connectionId, metric))
            return FloorHour(seconds);
        return FloorHour(seconds - (long)RawRetentionFor(connectionId, global).TotalSeconds);
    }

    /// <summary>This policy with a connection's raw retention changed; null days goes back to the global setting.</summary>
    public HistoryPolicy WithRawDays(string connectionId, int? days) => new(
    [
        .. Entries.Where(e => !(e.ConnectionId == connectionId && e.Metric.Length == 0)),
        .. days is { } d ? [new HistoryPolicyEntry(connectionId, "", d, true)] : Array.Empty<HistoryPolicyEntry>(),
    ]);

    /// <summary>This policy with one metric recorded or not.</summary>
    public HistoryPolicy WithRecord(string connectionId, string metric, bool record) => new(
    [
        .. Entries.Where(e => !(e.ConnectionId == connectionId && e.Metric == metric)),
        .. record ? Array.Empty<HistoryPolicyEntry>() : [new HistoryPolicyEntry(connectionId, metric, null, false)],
    ]);

    /// <summary>Whether two policies say the same thing, for "you have unsaved changes".</summary>
    public bool SameAs(HistoryPolicy other) =>
        _unrecorded.SetEquals(other._unrecorded)
        && _rawDays.Count == other._rawDays.Count
        && _rawDays.All(pair => other._rawDays.TryGetValue(pair.Key, out var days) && days == pair.Value);

    public static long FloorHour(long unixSeconds) => unixSeconds - (unixSeconds % 3600 + 3600) % 3600;

    private static string Key(string connectionId, string metric) => connectionId + "\n" + metric;
}
