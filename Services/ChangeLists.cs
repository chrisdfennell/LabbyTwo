using System.Globalization;
using LabbyTwo.Core;

namespace LabbyTwo.Services;

/// <summary>What <c>{{changes: …}}</c> asks for, read and checked.</summary>
/// <param name="Kinds">Empty for every kind.</param>
/// <param name="Only">Connection ids from <c>only=</c>; null for every connection.</param>
public sealed record ChangeListOptions(TimeSpan Window, IReadOnlyList<string> Kinds, int Limit, HashSet<string>? Only);

/// <summary>What <c>{{incidents: …}}</c> asks for, read and checked.</summary>
public sealed record IncidentListOptions(bool OpenOnly, TimeSpan Window, int Limit);

/// <summary>
/// The options of the two history shortcodes, <c>{{changes}}</c> and <c>{{incidents}}</c>,
/// and the words their rows are drawn with. Pure, like <see cref="MarkdownLists"/>, so what
/// every option means is pinned by a test rather than by a runbook that has to be opened.
/// </summary>
public static class ChangeLists
{
    public static readonly TimeSpan DefaultChangeWindow = TimeSpan.FromHours(24);
    public const int DefaultChangeLimit = 10;

    public static readonly TimeSpan DefaultIncidentWindow = TimeSpan.FromDays(30);
    public const int DefaultIncidentLimit = 5;

    /// <summary>
    /// <c>{{changes}}</c>, <c>{{changes: containers}}</c>, <c>{{changes: kind="alerts, services" last=7d limit=20 only="NAS"}}</c>.
    /// The kind may be the positional part or <c>kind=</c>, several separated by commas.
    /// Null, with every problem found, when something written means nothing.
    /// </summary>
    public static ChangeListOptions? Changes(Shortcode code, IReadOnlyList<Connection> connections, out string? problem)
    {
        var problems = new List<string>();

        var window = ChangeWindow.Parse(code.Option("last"), DefaultChangeWindow, out var windowProblem);
        if (windowProblem is not null)
            problems.Add(windowProblem);

        var kinds = new List<string>();
        var written = string.Join(',', new[] { code.Part(0), code.Option("kind") }.Where(w => w.Length > 0));
        foreach (var word in written.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (word.Equals("all", StringComparison.OrdinalIgnoreCase))
                continue;
            if (ChangeKinds.Parse(word) is { } kind)
            {
                if (!kinds.Contains(kind))
                    kinds.Add(kind);
            }
            else
            {
                problems.Add($"“{word}” is not a kind of change. Use services, containers, alerts, certificates, dns, devices, updates or backups.");
            }
        }

        var limit = Limit(code, DefaultChangeLimit, 100, problems);
        var only = MarkdownLists.Only(code, connections, out var onlyProblem);
        if (onlyProblem is not null)
            problems.Add(onlyProblem);

        problem = problems.Count > 0 ? string.Join(" ", problems) : null;
        return window is { } span && problem is null ? new ChangeListOptions(span, kinds, limit, only) : null;
    }

    /// <summary>
    /// <c>{{incidents}}</c>, <c>{{incidents: open}}</c>, <c>{{incidents: last=90d limit=10}}</c>.
    /// "open" shows only what is still going on, however long ago it started; "all", the
    /// default, shows what started in the window and anything still open.
    /// </summary>
    public static IncidentListOptions? Incidents(Shortcode code, out string? problem)
    {
        var problems = new List<string>();
        var which = (code.Part(0) is { Length: > 0 } part ? part : code.Option("show", "all")).Trim().ToLowerInvariant();
        if (which is not ("all" or "open" or "recent"))
            problems.Add($"“{which}” is not something incidents can be. Use open or all.");

        var window = ChangeWindow.Parse(code.Option("last"), DefaultIncidentWindow, out var windowProblem);
        if (windowProblem is not null)
            problems.Add(windowProblem);

        var limit = Limit(code, DefaultIncidentLimit, 50, problems);

        problem = problems.Count > 0 ? string.Join(" ", problems) : null;
        return window is { } span && problem is null ? new IncidentListOptions(which == "open", span, limit) : null;
    }

    private static int Limit(Shortcode code, int fallback, int most, List<string> problems)
    {
        var written = code.Option("limit");
        if (written.Length == 0)
            return fallback;
        if (int.TryParse(written, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) && limit >= 1)
            return Math.Min(limit, most);
        problems.Add($"“{written}” is not a number of lines. Write limit=10.");
        return fallback;
    }

    /// <summary>
    /// The changes a list keeps once connections are narrowed. Kinds, the window and the
    /// limit are the query's job; <c>only=</c> is applied here, since it can name several
    /// connections and the feed's index is by time alone. A change with no connection —
    /// LabbyTwo's own update, a DNS answer — is left out when connections are named.
    /// </summary>
    public static IReadOnlyList<Change> Narrow(IEnumerable<Change> changes, HashSet<string>? only, int limit) =>
        [.. changes.Where(c => only is null || (c.ConnectionId is { } id && only.Contains(id))).Take(limit)];

    /// <summary>"Plex went down · QNAP NAS" — the connection's name beside a change, when it is not already in the title.</summary>
    public static string? Where(Change change, IReadOnlyList<Connection> connections)
    {
        if (change.ConnectionId is not { } id)
            return null;
        var name = connections.FirstOrDefault(c => c.Id == id)?.Name;
        return name is null || change.Title.Contains(name, StringComparison.OrdinalIgnoreCase) ? null : name;
    }

    /// <summary>
    /// When an incident happened and how long it took, as a line under its title: "Started
    /// 14:05 today, back after 12m", "Started 3 Sep 22:40, ongoing — 2h 5m so far".
    /// </summary>
    public static string IncidentWhen(Incident incident, DateTimeOffset now)
    {
        var started = incident.StartedAt.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var day = started.Date == today ? "today"
            : started.Date == today.AddDays(-1) ? "yesterday"
            : started.ToString("d MMM", CultureInfo.CurrentCulture);
        var when = $"Started {started:HH:mm} {day}";
        return incident.IsOpen
            ? $"{when}, ongoing — {Ago.Duration(incident.Duration(now))} so far"
            : $"{when}, back after {Ago.Duration(incident.Duration(now))}";
    }

    /// <summary>A member's own line: "down 12m", "fired, cleared after 4m", "still down".</summary>
    public static string MemberWhen(IncidentMember member, DateTimeOffset now)
    {
        var verb = member.Kind == ChangeKinds.Alert ? "firing" : "down";
        return member.UpAt is { } up
            ? $"{verb} for {Ago.Duration(up - member.DownAt)}"
            : $"still {verb}, {Ago.Duration(now - member.DownAt)} so far";
    }
}
