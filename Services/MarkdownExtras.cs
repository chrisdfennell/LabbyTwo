using System.Globalization;
using System.Text.RegularExpressions;
using LabbyTwo.Core;

namespace LabbyTwo.Services;

/// <summary>
/// The words <c>{{today}}</c>, <c>{{countdown: …}}</c> and <c>{{ago: …}}</c> are drawn as.
/// Pure, with "now" passed in, so every edge — midnight, tomorrow, a date already gone, a
/// format somebody made up — is pinned by a test rather than by waiting for the day.
/// </summary>
public static partial class LiveTime
{
    /// <summary>What <c>{{today}}</c> says with no format: "Tuesday, 29 September 2026".</summary>
    public const string DefaultFormat = "dddd, d MMMM yyyy";

    /// <summary>Formats with names, for the common cases nobody should have to spell out.</summary>
    public static readonly IReadOnlyDictionary<string, string> NamedFormats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["long"] = DefaultFormat,
        ["short"] = "d MMM yyyy",
        ["date"] = "d MMMM yyyy",
        ["iso"] = "yyyy-MM-dd",
        ["day"] = "dddd",
        ["time"] = "HH:mm",
        ["datetime"] = "d MMM yyyy HH:mm",
    };

    /// <summary>
    /// A custom format may use only the date and time letters and a few separators, or
    /// words in single quotes. Anything else — a backslash, a percent, "K", a format forty
    /// characters long — is refused rather than handed to .NET to interpret, because the
    /// letters .NET would read in a random word are not what whoever typed it meant.
    /// </summary>
    [GeneratedRegex(@"^(?:[dMyHhmstf]+|[ ,.\-/:]+|'[A-Za-z ,.]*')+$")]
    private static partial Regex SafeFormat();

    [GeneratedRegex(@"^\s*(\d{4})-(\d{1,2})-(\d{1,2})(?:[ T](\d{1,2}):(\d{2})(?::(\d{2}))?)?\s*$")]
    private static partial Regex Moment();

    /// <summary>
    /// The .NET format a <c>format=</c> option stands for, or null with the reason. Blank is
    /// the default; a name from <see cref="NamedFormats"/> is that format.
    /// </summary>
    public static string? Format(string? written, out string? problem)
    {
        problem = null;
        var format = (written ?? "").Trim();
        if (format.Length == 0)
            return DefaultFormat;
        if (NamedFormats.TryGetValue(format, out var named))
            return named;
        if (format.Length > 40 || !SafeFormat().IsMatch(format) || !format.Any(c => char.IsAsciiLetter(c) && c != '\''))
        {
            problem = $"“{format}” is not a date format. Use long, short, date, iso, day, time or datetime — or letters like dddd d MMMM yyyy HH:mm.";
            return null;
        }
        // One letter on its own is a standard format to .NET ("d" is the short date); the
        // percent sign makes it the custom one, which is what somebody writing "d" means.
        return format.Length == 1 ? "%" + format : format;
    }

    /// <summary>The date in the given (already checked) format.</summary>
    public static string Today(DateTimeOffset now, string format) => now.ToString(format, CultureInfo.CurrentCulture);

    /// <summary>
    /// Reads <c>2026-12-25</c> or <c>2026-12-25 18:00</c> — ISO order only, because 03/04
    /// is March to some readers and April to others, and a countdown that is a month out is
    /// worse than one that asks.
    /// </summary>
    public static bool TryParseMoment(string? text, out DateTime moment, out bool hasTime)
    {
        moment = default;
        hasTime = false;
        var match = Moment().Match(text ?? "");
        if (!match.Success)
            return false;
        int Part(int group) => match.Groups[group].Success ? int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture) : 0;
        try
        {
            moment = new DateTime(Part(1), Part(2), Part(3), Part(4), Part(5), Part(6), DateTimeKind.Unspecified);
        }
        catch (ArgumentOutOfRangeException)
        {
            // 2026-02-30, 25:00: a shape that looks right naming a moment that does not exist.
            return false;
        }
        hasTime = match.Groups[4].Success;
        return true;
    }

    /// <summary>
    /// How far off a date is, as a sentence ends: "today", "tomorrow", "in 87 days",
    /// "yesterday", "3 days ago". Counted in calendar days, not in 24-hour lumps, so the
    /// evening before is "tomorrow" rather than "today".
    /// </summary>
    public static string Days(DateTime target, DateTime now)
    {
        var days = (target.Date - now.Date).Days;
        return days switch
        {
            0 => "today",
            1 => "tomorrow",
            -1 => "yesterday",
            > 1 => $"in {days} days",
            _ => $"{-days} days ago",
        };
    }

    /// <summary>
    /// A countdown. With a time and less than a day to go it counts hours and minutes
    /// ("in 3 h 20 min", "25 min ago", "now"); otherwise it counts days as <see cref="Days"/> does.
    /// </summary>
    public static string Countdown(DateTime target, bool hasTime, DateTime now)
    {
        if (!hasTime)
            return Days(target, now);

        var left = target - now;
        var size = left.Duration();
        if (size >= TimeSpan.FromDays(1))
            return Days(target, now);
        if (size < TimeSpan.FromMinutes(1))
            return "now";

        var words = size.TotalHours < 1
            ? $"{(int)size.TotalMinutes} min"
            : size.Minutes == 0 || size.TotalHours >= 10
                ? $"{(int)size.TotalHours} h"
                : $"{(int)size.TotalHours} h {size.Minutes} min";
        return left > TimeSpan.Zero ? $"in {words}" : $"{words} ago";
    }

    /// <summary>
    /// How long since a connection was last probed: "12 s ago" while it is seconds, since that
    /// is the whole point of asking; "4 min ago", then the dashboard's usual "3h 5m ago".
    /// </summary>
    public static string Probed(DateTimeOffset at, DateTimeOffset now)
    {
        var elapsed = now - at;
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;
        if (elapsed.TotalSeconds < 60)
            return $"{(int)elapsed.TotalSeconds} s ago";
        if (elapsed.TotalHours < 1)
            return $"{(int)elapsed.TotalMinutes} min ago";
        return Ago.Since(at, now);
    }
}

/// <summary>
/// The window a <c>{{sparkline}}</c> covers: <c>24h</c>, <c>7d</c>, <c>2w</c> — from an hour
/// to thirty days, the range the history holds at a resolution worth drawing.
/// </summary>
public static partial class SparkWindow
{
    public static readonly TimeSpan Default = TimeSpan.FromHours(24);
    public static readonly TimeSpan Shortest = TimeSpan.FromHours(1);
    public static readonly TimeSpan Longest = TimeSpan.FromDays(30);

    [GeneratedRegex(@"^(\d{1,4})\s*(m|min|h|d|w)$", RegexOptions.IgnoreCase)]
    private static partial Regex Word();

    /// <summary>Whether <paramref name="text"/> is written like a window at all, in range or not.</summary>
    public static bool Looks(string text) => Word().IsMatch(text.Trim());

    /// <summary>The window written, or null with why: not a window, or outside an hour to thirty days.</summary>
    public static TimeSpan? Parse(string text, out string? problem)
    {
        problem = null;
        var match = Word().Match(text.Trim());
        if (!match.Success)
        {
            problem = $"“{text}” is not a window. Write it like 1h, 24h, 7d or 30d.";
            return null;
        }
        var count = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var window = match.Groups[2].Value.ToLowerInvariant() switch
        {
            "m" or "min" => TimeSpan.FromMinutes(count),
            "h" => TimeSpan.FromHours(count),
            "d" => TimeSpan.FromDays(count),
            _ => TimeSpan.FromDays(count * 7),
        };
        if (window < Shortest || window > Longest)
        {
            problem = $"A sparkline covers between 1h and 30d; “{text}” is outside that.";
            return null;
        }
        return window;
    }

    /// <summary>
    /// The metric and the window from the second part of <c>{{sparkline: NAS / cpu_percent 24h}}</c>:
    /// a last word that looks like a window is the window, everything before it the metric.
    /// </summary>
    public static (string Metric, string Window) Split(string part)
    {
        var text = part.Trim();
        var space = text.LastIndexOf(' ');
        if (space > 0 && Looks(text[(space + 1)..]))
            return (text[..space].TrimEnd(), text[(space + 1)..]);
        return Looks(text) ? ("", text) : (text, "");
    }

    /// <summary>
    /// At most <paramref name="points"/> values, each the average of its share of the series —
    /// a line five characters wide has no use for 2,880 readings.
    /// </summary>
    public static IReadOnlyList<double> Thin(IReadOnlyList<double> values, int points = 96)
    {
        if (values.Count <= points)
            return values;
        var thinned = new double[points];
        for (var i = 0; i < points; i++)
        {
            var from = (int)((long)i * values.Count / points);
            var to = (int)((long)(i + 1) * values.Count / points);
            var sum = 0d;
            for (var j = from; j < to; j++)
                sum += values[j];
            thinned[i] = sum / Math.Max(1, to - from);
        }
        return thinned;
    }

    /// <summary>"24 hours", "7 days" — the window in words, for a tooltip.</summary>
    public static string Describe(TimeSpan window) =>
        window.TotalHours > 48 && window.TotalDays % 1 == 0 ? $"{window.TotalDays:0} days"
        : window.TotalHours >= 1 && window.TotalHours % 1 == 0 ? $"{window.TotalHours:0} hour{(window.TotalHours == 1 ? "" : "s")}"
        : $"{window.TotalMinutes:0} minutes";
}

/// <summary>The status page's daily strip, shared so a runbook's bar and the status page agree on every colour.</summary>
public static class UptimeStrip
{
    public const int DefaultDays = 30;

    /// <summary>
    /// The colour of one day: green at 99.9% or better, amber down to 95%, red below, and
    /// hollow for a day with no history — a gap before monitoring began is not an outage.
    /// </summary>
    public static string DayClass(double? percent) => percent switch
    {
        null => "is-unknown",
        >= 99.9 => "is-good",
        >= 95 => "is-warn",
        _ => "is-bad",
    };

    /// <summary>The days asked for, kept to what the status page itself allows: a week to ninety days.</summary>
    public static int Days(string? written) =>
        Math.Clamp(int.TryParse(written, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) ? days : DefaultDays, 7, 90);
}

/// <summary>One line of <c>{{alerts}}</c>.</summary>
/// <param name="Name">The rule's own name, or its description when it has none.</param>
/// <param name="Value">The last reading, in the reader's units.</param>
/// <param name="Since">When the breach began; null when the evaluator does not know.</param>
/// <param name="MutedBy">The mute window holding its notification back right now, if any.</param>
public sealed record AlertLine(string RuleId, string Name, Connection Connection, string Metric, string Value, string Limit, DateTimeOffset? Since,
    string? MutedBy = null);

/// <summary>One line of <c>{{renewals}}</c>.</summary>
/// <param name="What">"certificate", "node key", "next renewal" — or empty for a count of overdue renewals.</param>
/// <param name="DaysLeft">Negative once it has expired.</param>
/// <param name="Overdue">How many renewals are already overdue, for the renewals list's own line.</param>
/// <param name="Detail">What the connection's last probe said, which for the renewals list names the item.</param>
public sealed record RenewalLine(Connection Connection, string What, double DaysLeft, int Overdue, string? Detail)
{
    public bool IsOverdue => Overdue > 0 || DaysLeft < 0;
}

/// <summary>
/// What the list shortcodes — <c>{{alerts}}</c>, <c>{{containers}}</c>, <c>{{renewals}}</c> —
/// show, worked out from what the app already holds in memory. Kept out of the components,
/// like <see cref="RunbookFacts"/>, so the rules are pinned by tests that hand in a state
/// rather than by a rule that has to be made to fire.
/// </summary>
public static class MarkdownLists
{
    /// <summary>How far back a countdown reading still counts, as the weekly summary reads it.</summary>
    public static readonly TimeSpan RenewalWindow = TimeSpan.FromDays(3);

    /// <summary>The filters <c>{{containers: …}}</c> takes, the same words the Containers tab's filter uses.</summary>
    /// <remarks>
    /// "busiest" is not a state but a ranking — the containers using the most CPU and disk,
    /// from the resource poller's last round in memory — and is drawn from those readings
    /// rather than the container list.
    /// </remarks>
    public static readonly IReadOnlyList<string> ContainerStates = ["all", "running", "stopped", "unhealthy", "paused", "restarting", "busiest"];

    /// <summary>
    /// Connections named in an <c>only="NAS, Plex"</c> option, and a note naming any that
    /// match nothing. Null when there is no such option: everything is wanted.
    /// </summary>
    public static HashSet<string>? Only(Shortcode code, IReadOnlyList<Connection> connections, out string? problem)
    {
        problem = null;
        if (code.Option("only") is not { Length: > 0 } only)
            return null;

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var name in only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ShortcodeLookup.Connection(connections, name) is { } found)
                ids.Add(found.Id);
            else
                missing.Add(name);
        }
        if (missing.Count > 0)
            problem = $"No connection called {string.Join(", ", missing.Select(m => $"“{m}”"))}.";
        return ids;
    }

    /// <summary>
    /// The alert rules firing now — threshold, unusual and forecast rules alike, since they
    /// are all the one evaluator — longest-firing first, as the Active alerts card reads them.
    /// A breach whose rule or connection has since been deleted is left out, not guessed at.
    ///
    /// The reading and the limit are both said in <paramref name="units"/>, the way the alert
    /// rules page shows them, so "is 140°F — above 60" can never appear: the rule was saved
    /// in the metric's own unit and is only ever converted for reading.
    /// </summary>
    public static IReadOnlyList<AlertLine> Alerts(
        IEnumerable<MetricAlertService.Breach> firing,
        IReadOnlyList<AlertRule> rules,
        IReadOnlyList<Connection> connections,
        Registry registry,
        HashSet<string>? only,
        Units.Preferences units,
        Func<string, string, string?>? mutedBy = null)
    {
        var lines = new List<AlertLine>();
        foreach (var breach in firing.Where(b => b.Firing))
        {
            if (only is not null && !only.Contains(breach.ConnectionId))
                continue;
            var rule = rules.FirstOrDefault(r => r.Id == breach.RuleId);
            var connection = connections.FirstOrDefault(c => c.Id == breach.ConnectionId);
            if (rule is null || connection is null)
                continue;

            var spec = registry.Metric(connection, rule.Metric);
            var limit = rule.IsUnusual ? rule.UnusualPhrase() : $"{rule.ComparisonWord} {Units.Format(spec, rule.Threshold, units)}";
            lines.Add(new AlertLine(rule.Id, rule.Describe(spec, connection.Name, units), connection, spec.Label,
                Units.Format(spec, breach.LastValue, units, spec.Decimals == 0 && Math.Abs(breach.LastValue) < 100 ? 1 : spec.Decimals),
                limit, breach.Since, mutedBy?.Invoke(rule.Id, connection.Id)));
        }
        return
        [
            .. lines
                .OrderBy(l => l.Since ?? DateTimeOffset.MaxValue)
                .ThenBy(l => l.Connection.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>The state filter written in <c>{{containers: stopped}}</c>, or null with why.</summary>
    public static string? ContainerFilter(Shortcode code, out string? problem)
    {
        problem = null;
        var written = (code.Part(0) is { Length: > 0 } part ? part : code.Option("show", "all")).Trim().ToLowerInvariant();
        if (ContainerStates.Contains(written))
            return written;
        problem = $"“{written}” is not something containers can be. Use {string.Join(", ", ContainerStates)}.";
        return null;
    }

    /// <summary>
    /// The containers a filter keeps — trouble first (unhealthy, restarting, then running,
    /// paused, stopped), then by name, which is the Containers tab's "state" order.
    /// </summary>
    public static IReadOnlyList<ContainerRow> Containers(IEnumerable<ContainerRow> rows, string filter) =>
        DockerContainers.Sort(rows.Where(r => DockerContainers.Matches(r, null, filter)), DockerContainers.SortBy.State);

    /// <summary>The dot a container gets: the service tile's colours, read for a container.</summary>
    public static string ContainerDot(ContainerRow row) =>
        row.Health == ContainerHealth.Unhealthy ? "status-down"
        : row.IsRunning ? "status-up"
        : row.IsRestarting || row.IsPaused ? "status-flapping"
        : row.ExitCode is { } code && code != 0 ? "status-down"
        : "status-unknown";

    /// <summary>
    /// The Docker connection a <c>{{containers}}</c> means: the one named in <c>connection=</c>,
    /// or the first enabled one, as the Containers tab picks it.
    /// </summary>
    public static Connection? Docker(Shortcode code, IReadOnlyList<Connection> connections, out string? problem)
    {
        problem = null;
        if (code.Option("connection") is { Length: > 0 } name)
        {
            var named = ShortcodeLookup.Connection(connections, name);
            if (named is null)
                problem = $"No connection called “{name}”.";
            else if (!string.Equals(named.Provider, "docker", StringComparison.OrdinalIgnoreCase))
                problem = $"{named.Name} is not a Docker connection.";
            return problem is null ? named : null;
        }
        var first = connections.FirstOrDefault(c => c.Enabled && string.Equals(c.Provider, "docker", StringComparison.OrdinalIgnoreCase));
        if (first is null)
            problem = "There is no Docker connection. Add one — the local socket, a named pipe or a socket proxy — under Connections.";
        return first;
    }

    /// <summary>
    /// What is due to expire, soonest first, within <paramref name="days"/>: every countdown
    /// metric the weekly summary reads (certificates, Tailscale keys, the renewals list's
    /// next item) plus the renewals list's overdue count. Read through metrics and the probe's
    /// message only, so the host knows nothing about the renewals plugin and a plugin that
    /// reports one of these names later is listed for free. Overdue and expired come first
    /// whatever the window, since those are the reason to look.
    /// </summary>
    public static IReadOnlyList<RenewalLine> Renewals(
        IEnumerable<Connection> connections,
        Func<string, IReadOnlyDictionary<string, double>> readings,
        Func<string, HealthMonitor.ProbeState?> state,
        int days,
        int limit,
        HashSet<string>? only = null)
    {
        var lines = new List<RenewalLine>();
        foreach (var connection in connections.Where(c => c.Enabled))
        {
            if (only is not null && !only.Contains(connection.Id))
                continue;
            var current = state(connection.Id);
            // The live probe first, then the newest stored reading — so a restart does not
            // empty the list until the next sweep.
            var latest = new Dictionary<string, double>(readings(connection.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in current?.Metrics ?? new Dictionary<string, double>())
                latest[key] = value;

            var detail = string.Equals(connection.Provider, "renewals", StringComparison.OrdinalIgnoreCase) && current?.Message is { Length: > 0 } message
                ? message
                : null;
            foreach (var expiry in WeeklySummaryGatherer.Expiries(connection, latest))
                lines.Add(new RenewalLine(connection, expiry.What, expiry.DaysLeft, expiry.Overdue, detail));
        }

        return
        [
            .. lines
                .Where(l => l.IsOverdue || l.DaysLeft <= days)
                .OrderBy(l => l.IsOverdue ? 0 : 1)
                .ThenBy(l => l.Overdue > 0 ? double.MinValue : l.DaysLeft)
                .ThenBy(l => l.Connection.Name, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, limit)),
        ];
    }

    /// <summary>"in 12 days", "today", "expired 3 days ago", "2 overdue" — how a renewal line reads.</summary>
    public static string RenewalWhen(RenewalLine line)
    {
        if (line.Overdue > 0)
            return $"{line.Overdue} overdue";
        // Whole days towards zero: 12.7 left is "in 12 days", half a day gone is "today".
        var whole = (int)Math.Truncate(line.DaysLeft);
        return whole switch
        {
            < -1 => $"expired {-whole} days ago",
            -1 => "expired yesterday",
            0 => line.DaysLeft < 0 ? "expired today" : "today",
            1 => "tomorrow",
            _ => $"in {whole} days",
        };
    }
}
