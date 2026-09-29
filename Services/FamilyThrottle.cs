using System.Net;

namespace LabbyTwo.Services;

/// <summary>
/// Rate limits for the family status page, which is the one part of LabbyTwo anybody with
/// a link can reach without signing in.
///
/// Three budgets, each a sliding window:
/// <list type="bullet">
/// <item><b>Page loads per address</b> — generous, because a phone left on the page refreshes
/// once a minute by itself, but low enough that hammering it, or trying tokens, is pointless.
/// Wrong tokens count against it too.</item>
/// <item><b>Reports per address</b> — a few in a quarter of an hour and a few more in a day.
/// A person who finds two things broken can say so; a script cannot page the owner all night.</item>
/// <item><b>Reports from everybody</b> — the ceiling a botnet with a leaked link runs into,
/// since every report is a notification on the owner's phone. Past it, reports are refused
/// until the window moves on; the page still loads.</item>
/// </list>
///
/// Kept in memory, like <see cref="LoginThrottle"/> and for the same reasons: one process,
/// and a restart forgetting the counts costs an attacker a restart. Addresses are keyed the
/// same way — IPv6 by its /64 — and the table is bounded, so rotating addresses cannot grow it.
/// </summary>
public sealed class FamilyThrottle(TimeProvider? clock = null, FamilyThrottle.Limits? limits = null)
{
    /// <summary>The numbers, all in one place so a test can shrink them.</summary>
    public sealed record Limits
    {
        public int PageLoads { get; init; } = 60;
        public TimeSpan PageWindow { get; init; } = TimeSpan.FromMinutes(1);

        public int ReportsShort { get; init; } = 3;
        public TimeSpan ReportsShortWindow { get; init; } = TimeSpan.FromMinutes(15);

        public int ReportsDaily { get; init; } = 10;
        public TimeSpan ReportsDailyWindow { get; init; } = TimeSpan.FromDays(1);

        public int ReportsGlobal { get; init; } = 20;
        public TimeSpan ReportsGlobalWindow { get; init; } = TimeSpan.FromHours(1);

        public int MaxTracked { get; init; } = 10_000;
    }

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Limits _limits = limits ?? new Limits();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _pages = [];
    private readonly Dictionary<string, Queue<DateTimeOffset>> _reports = [];
    private readonly Queue<DateTimeOffset> _allReports = new();

    /// <summary>
    /// Counts one page load from <paramref name="client"/>. Returns how long to wait if it is
    /// over its budget, or null if it may go ahead.
    /// </summary>
    public TimeSpan? Page(IPAddress? client)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            var times = Times(_pages, LoginThrottle.KeyFor(client), now);
            Expire(times, now, _limits.PageWindow);
            if (times.Count >= _limits.PageLoads)
                return times.Peek() + _limits.PageWindow - now;
            times.Enqueue(now);
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="client"/> may send a report now, and if so counts it. The
    /// check and the count are one step, so a burst of simultaneous posts cannot all pass.
    /// </summary>
    public TimeSpan? Report(IPAddress? client)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            var times = Times(_reports, LoginThrottle.KeyFor(client), now);
            Expire(times, now, _limits.ReportsDailyWindow);
            Expire(_allReports, now, _limits.ReportsGlobalWindow);

            var recent = times.Where(t => now - t < _limits.ReportsShortWindow).ToList();
            if (recent.Count >= _limits.ReportsShort)
                return recent[0] + _limits.ReportsShortWindow - now;
            if (times.Count >= _limits.ReportsDaily)
                return times.Peek() + _limits.ReportsDailyWindow - now;
            if (_allReports.Count >= _limits.ReportsGlobal)
                return _allReports.Peek() + _limits.ReportsGlobalWindow - now;

            times.Enqueue(now);
            _allReports.Enqueue(now);
            return null;
        }
    }

    /// <summary>Addresses remembered across both tables. For tests of the bound.</summary>
    public int Tracked
    {
        get { lock (_gate) return _pages.Count + _reports.Count; }
    }

    private Queue<DateTimeOffset> Times(Dictionary<string, Queue<DateTimeOffset>> table, string key, DateTimeOffset now)
    {
        if (table.TryGetValue(key, out var times))
            return times;

        if (table.Count >= _limits.MaxTracked)
        {
            // Forget whoever has been quiet longest. An address with nothing inside its
            // window loses nothing by being forgotten; one mid-burst is the price of the bound.
            var quietest = table.MinBy(pair => pair.Value.Count == 0 ? DateTimeOffset.MinValue : pair.Value.Last()).Key;
            table.Remove(quietest);
        }

        times = new Queue<DateTimeOffset>();
        table[key] = times;
        return times;
    }

    private static void Expire(Queue<DateTimeOffset> times, DateTimeOffset now, TimeSpan window)
    {
        while (times.Count > 0 && now - times.Peek() >= window)
            times.Dequeue();
    }
}
