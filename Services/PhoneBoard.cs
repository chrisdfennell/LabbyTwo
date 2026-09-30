using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>What a row on the phone view is about, which decides the actions it offers.</summary>
public enum PhoneRowKind
{
    Down,
    Alert,
    Incident,
    Backup,
    Family,
}

/// <summary>
/// One thing that needs attention, with everything its row says and does. Built by
/// <see cref="PhoneBoard.Build"/> from memory; the page only draws it.
/// </summary>
/// <param name="Key">Stable across redraws, for <c>@key</c>: a row that is still there is not rebuilt.</param>
/// <param name="Red">Red for broken, amber for worth a look.</param>
/// <param name="Title">What it is, first.</param>
/// <param name="Why">What is wrong with it, in the words it was reported in.</param>
/// <param name="For">How long: "for 12m".</param>
public sealed record PhoneRow(string Key, PhoneRowKind Kind, bool Red, string Title, string Why, string? For)
{
    /// <summary>The connection it is about, for its own action buttons and a silence.</summary>
    public Connection? Connection { get; init; }

    /// <summary>The alert's key in the delivery ledger, when it has one, for "I'm on it".</summary>
    public string? AlertKey { get; init; }

    public bool Acknowledged { get; init; }

    /// <summary>The container restart set up as this outage's self-healing, offered as a button.</summary>
    public Remediation? Restart { get; init; }

    /// <summary>The connection's runbook note, as a path inside LabbyTwo.</summary>
    public string? Runbook { get; init; }

    /// <summary>Where the full story is — the incident, the Backups page.</summary>
    public string? Link { get; init; }

    public string? LinkText { get; init; }

    /// <summary>"Probably: …" for an incident.</summary>
    public string? Probably { get; init; }

    /// <summary>The family report's id, for dismissing it.</summary>
    public long? ReportId { get; init; }

    /// <summary>When this connection's alerts are held until, if they are.</summary>
    public DateTimeOffset? SilencedUntil { get; init; }
}

/// <summary>One connection in the "everything else" list: a dot and a word.</summary>
/// <param name="State">"up", "down" or "unknown" — the <c>status-*</c> class.</param>
public sealed record PhoneDot(Connection Connection, string State, string Text);

/// <summary>The connections a tab shows, for the "everything else" list.</summary>
public sealed record PhoneGroup(string Title, string Icon, IReadOnlyList<PhoneDot> Items)
{
    public int Down => Items.Count(i => i.State == "down");
}

/// <summary>
/// Everything the phone view shows, worked out in one pass so the page can draw it without
/// asking anything. <see cref="Headline"/> is the line at the top; <see cref="Rows"/> is only
/// what is red or amber; <see cref="Groups"/> is the rest of the lab by tab.
/// </summary>
public sealed record PhoneModel(
    string Headline,
    bool AllFine,
    IReadOnlyList<PhoneRow> Rows,
    IReadOnlyList<PhoneGroup> Groups,
    int Monitored);

/// <summary>
/// What the phone view is built from, every part of it already in memory: the connection
/// and layout caches, the monitor's states, the evaluator's breaches, the backups as last
/// judged, the incidents the tracker holds, the reports the store last read.
/// </summary>
public sealed record PhoneInputs(
    IReadOnlyList<Connection> Connections,
    IReadOnlyList<Tab> Tabs,
    IReadOnlyList<Widget> Widgets,
    Func<Connection, bool> IsMonitored,
    Func<string, HealthMonitor.ProbeState?> State,
    IReadOnlyList<AlertLine> Alerts,
    IReadOnlyList<BackupRow>? Backups,
    IReadOnlyList<Incident>? Incidents,
    IReadOnlyDictionary<long, IReadOnlyList<ProbableCause>> Causes,
    IReadOnlyList<FamilyReport>? Reports,
    IReadOnlyList<Remediation> Remediations,
    Func<string, bool> HasDelivery,
    Func<string, bool> IsAcknowledged,
    DateTimeOffset Now);

/// <summary>
/// Turns what LabbyTwo holds in memory into the phone view. Pure — handed everything, it
/// asks nothing — so every state of the page (all fine, something down, blind, in
/// maintenance) is a test of this rather than of a phone.
/// </summary>
public static class PhoneBoard
{
    public static PhoneModel Build(PhoneInputs input)
    {
        var now = input.Now;
        var monitored = input.Connections.Where(input.IsMonitored).ToList();
        var byId = input.Connections.ToDictionary(c => c.Id);
        var rows = new List<PhoneRow>();

        var down = 0;
        var checking = 0;
        foreach (var connection in monitored)
        {
            var state = input.State(connection.Id);
            if (state?.IsUp is null)
            {
                checking++;
                continue;
            }
            if (state.IsUp == true)
                continue;

            down++;
            var why = state.Message.Length > 0 ? state.Message : "Not answering.";
            // The parent is the cause, and its own row says so; this one only needs to point at it.
            if (connection.DependsOn is { } parentId && byId.TryGetValue(parentId, out var parent)
                && input.State(parentId)?.IsUp == false)
            {
                why = $"Behind {parent.Name}, which is down too. {why}";
            }
            var key = FiringAlert.StatusKey(connection.Id);
            rows.Add(new PhoneRow($"down:{connection.Id}", PhoneRowKind.Down, true,
                $"{Name(connection)} is down", why, For(state.ChangedAt, now))
            {
                Connection = connection,
                AlertKey = input.HasDelivery(key) ? key : null,
                Acknowledged = input.IsAcknowledged(key),
                Restart = RestartFor(input.Remediations, Remediation.DownTrigger(connection.Id)),
                Runbook = PhoneView.RunbookFor(connection),
                SilencedUntil = connection.IsSilenced(now) ? connection.SilencedUntil : null,
            });
        }

        foreach (var line in input.Alerts)
        {
            var key = FiringAlert.RuleKey(line.RuleId, line.Connection.Id);
            var why = $"{line.Metric} is {line.Value} — {line.Limit}." + (line.MutedBy is { } window ? $" Muted by {window}." : "");
            rows.Add(new PhoneRow($"alert:{line.RuleId}:{line.Connection.Id}", PhoneRowKind.Alert, false,
                line.Name, why, For(line.Since, now))
            {
                Connection = line.Connection,
                AlertKey = input.HasDelivery(key) ? key : null,
                Acknowledged = input.IsAcknowledged(key),
                Restart = RestartFor(input.Remediations, Remediation.RuleTrigger(line.RuleId)),
                Runbook = PhoneView.RunbookFor(line.Connection),
                SilencedUntil = line.Connection.IsSilenced(now) ? line.Connection.SilencedUntil : null,
            });
        }

        foreach (var incident in input.Incidents ?? [])
        {
            if (!incident.IsOpen)
                continue;
            var still = incident.Members.Where(m => m.UpAt is null).Select(m => m.Name).Distinct().ToList();
            var why = still.Count == 0
                ? "Everything in it has recovered; it closes when it has been quiet for a while."
                : $"Still broken: {string.Join(", ", still)}.";
            rows.Add(new PhoneRow($"incident:{incident.Id}", PhoneRowKind.Incident, true,
                $"Incident: {incident.Title}", why, $"for {Ago.Duration(incident.Duration(now))}")
            {
                Probably = input.Causes.GetValueOrDefault(incident.Id) is { Count: > 0 } causes ? IncidentCauses.Headline(causes) : null,
                Link = $"incidents#incident-{incident.Id}",
                LinkText = "Timeline",
            });
        }

        foreach (var row in input.Backups ?? [])
        {
            if (row.Status.State == BackupState.Ok)
                continue;
            var (title, why) = row.Status.State switch
            {
                BackupState.Late => ($"The backup of {row.Item.Name} is late",
                    row.Status.LastSuccess is { } last ? $"The last one was {Ago.Since(last, now)}." : "It is overdue."),
                BackupState.Never => ($"{row.Item.Name} has never been backed up", "Its source answers, and has no backup to show."),
                _ => ($"The backup of {row.Item.Name} can't be checked", "Nothing can say when it last ran."),
            };
            if ((row.Status.Problem ?? row.Reading.Problem) is { } problem)
                why = $"{why} {Sentence(problem)}";
            rows.Add(new PhoneRow($"backup:{row.Item.Id}", PhoneRowKind.Backup, false, title, why,
                row.Status.DueBy is { } due && due < now ? $"due {Ago.Since(due, now)}" : null)
            {
                Connection = row.Item.ConnectionId is { } about ? byId.GetValueOrDefault(about) : null,
                Link = "backups",
                LinkText = "Backups",
            });
        }

        foreach (var report in input.Reports ?? [])
        {
            var who = report.Reporter.Length > 0 ? report.Reporter : "Somebody";
            rows.Add(new PhoneRow($"family:{report.Id}", PhoneRowKind.Family, false,
                $"{who} says {report.ItemName} is broken",
                report.Message.Length > 0 ? $"“{report.Message}”" : "No message.",
                Ago.Since(report.At, now))
            {
                Connection = report.ConnectionId is { } id ? byId.GetValueOrDefault(id) : null,
                ReportId = report.Id,
            });
        }

        var alerts = input.Alerts.Count;
        var other = rows.Count(r => r.Kind is PhoneRowKind.Incident or PhoneRowKind.Backup or PhoneRowKind.Family);
        var headline = PhoneView.Headline(monitored.Count, down, checking, alerts, other);
        return new PhoneModel(headline, rows.Count == 0 && checking == 0, rows, Groups(input, monitored), monitored.Count);
    }

    /// <summary>
    /// The lab by tab: each enabled tab with the monitored connections its cards are bound
    /// to, in the tab's order, then whatever is on no tab. A connection on two tabs is under
    /// both — it is where somebody would look for it.
    /// </summary>
    private static IReadOnlyList<PhoneGroup> Groups(PhoneInputs input, IReadOnlyList<Connection> monitored)
    {
        var groups = new List<PhoneGroup>();
        var placed = new HashSet<string>();
        var known = monitored.ToDictionary(c => c.Id);

        foreach (var tab in input.Tabs.Where(t => t.Enabled).OrderBy(t => t.Sort))
        {
            var items = input.Widgets
                .Where(w => w.TabId == tab.Id && w.ConnectionId is not null)
                .Select(w => known.GetValueOrDefault(w.ConnectionId!))
                .OfType<Connection>()
                .DistinctBy(c => c.Id)
                .OrderBy(c => c.Sort).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => Dot(c, input))
                .ToList();
            if (items.Count == 0)
                continue;
            placed.UnionWith(items.Select(i => i.Connection.Id));
            groups.Add(new PhoneGroup(tab.Name, tab.Icon, items));
        }

        var rest = monitored.Where(c => !placed.Contains(c.Id)).Select(c => Dot(c, input)).ToList();
        if (rest.Count > 0)
            groups.Add(new PhoneGroup(groups.Count == 0 ? "Everything monitored" : "Not on a tab", "", rest));
        return groups;
    }

    private static PhoneDot Dot(Connection connection, PhoneInputs input) => input.State(connection.Id) switch
    {
        { CantCheck: not null } => new PhoneDot(connection, "unknown", "can't check"),
        { IsUp: true } => new PhoneDot(connection, "up", "up"),
        { IsUp: false } => new PhoneDot(connection, "down", "down"),
        _ => new PhoneDot(connection, "unknown", "checking"),
    };

    /// <summary>A container restart set up for this trigger, if there is one worth a button.</summary>
    private static Remediation? RestartFor(IReadOnlyList<Remediation> remediations, string trigger) =>
        remediations.FirstOrDefault(r => r.Trigger == trigger && r.Kind == RemediationKind.RestartContainer && r.IsComplete);

    private static string Name(Connection connection) =>
        connection.Icon.Length > 0 ? $"{connection.Icon} {connection.Name}" : connection.Name;

    private static string? For(DateTimeOffset? since, DateTimeOffset now) =>
        since is { } at ? $"for {Ago.Duration(now - at)}" : null;

    private static string Sentence(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return "";
        trimmed = char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
        return trimmed.EndsWith('.') ? trimmed : trimmed + ".";
    }
}
