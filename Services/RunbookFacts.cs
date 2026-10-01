using LabbyTwo.Core;

namespace LabbyTwo.Services;

/// <summary>Whether a condition holds, or why it cannot be decided.</summary>
/// <param name="Holds">Whether the "then" part shows. False whenever <paramref name="Problem"/> is set.</param>
/// <param name="Problem">A condition that names nothing: both parts are hidden and a "?" says why.</param>
public sealed record ConditionResult(bool Holds, string? Problem = null)
{
    public static ConditionResult Yes { get; } = new(true);
    public static ConditionResult No { get; } = new(false);
    public static ConditionResult Broken(string problem) => new(false, problem);
}

/// <summary>
/// Everything a condition may ask about beyond one connection's verdict, as the page holds
/// it right now: the names (connections, tabs, cards, alert rules) read once off the render
/// thread, and the state the app already keeps in memory — the evaluator's breaches, the
/// maintenance window, whether LabbyTwo can see, the backups as last judged, the open
/// incidents. A snapshot, built per decision, so one decision never sees two states.
///
/// Every part beyond the connections may be missing: null while it has not been read yet,
/// or on a page drawn by something that does not run that part of the app. A test whose
/// part is missing is false — "nothing to go on yet", like a connection still checking —
/// never a problem, since nothing about what was written is wrong.
/// </summary>
public sealed record RunbookContext(
    IReadOnlyList<Connection> Connections,
    IReadOnlyList<Tab>? Tabs = null,
    IReadOnlyList<Widget>? Widgets = null,
    IReadOnlyList<AlertRule>? Rules = null,
    IReadOnlyCollection<MetricAlertService.Breach>? Firing = null,
    bool Maintenance = false,
    bool Blind = false,
    IReadOnlyList<BackupRow>? Backups = null,
    IReadOnlyList<Incident>? Incidents = null);

/// <summary>One line of <c>{{down}}</c>.</summary>
/// <param name="State">Null for a connection not checked yet, listed only with <c>include="checking"</c>.</param>
/// <param name="Silenced">Its alerts are held until then — shown, since it is still down, but marked.</param>
public sealed record DownEntry(Connection Connection, HealthMonitor.ProbeState? State, DateTimeOffset? Silenced);

/// <summary>What <c>{{down}}</c> draws: the lines, and anything in its options that named nothing.</summary>
public sealed record DownList(IReadOnlyList<DownEntry> Entries, string? Problem);

/// <summary>
/// The questions a runbook asks of the lab — is the NAS down, is anything down, is the disk
/// over 90% — answered from memory only: the monitor's last verdicts, the latest readings,
/// the registry. Nothing here touches the database, because it is asked on the render
/// thread every time a sweep lands.
///
/// Built from delegates rather than the services themselves so the rules can be pinned by
/// tests that set a state directly, instead of by a probe that has to be made to fail.
/// </summary>
public sealed class RunbookFacts(
    Registry registry,
    Func<Connection, bool> isMonitored,
    Func<string, HealthMonitor.ProbeState?> state,
    Func<string, IReadOnlyDictionary<string, double>> stored)
{
    /// <summary>How far back a stored reading still counts — the metric tile's window, as {{metric: …}} uses.</summary>
    public static readonly TimeSpan StoredWindow = TimeSpan.FromHours(6);

    public RunbookFacts(Registry registry, HealthMonitor health, LatestReadings latest)
        : this(registry, health.IsMonitored, health.State, id => latest.Get(id, StoredWindow))
    {
    }

    /// <summary>
    /// Whether <paramref name="condition"/> holds for the connections in <paramref name="connections"/>.
    ///
    /// A connection still "checking" — never probed since the app started — is neither up
    /// nor down, so <c>down:</c> and <c>up:</c> are both false for it: a runbook should not
    /// tell you to go and power-cycle the NAS because the app restarted a second ago.
    /// </summary>
    public ConditionResult Evaluate(RunbookCondition condition, IReadOnlyList<Connection> connections) =>
        Evaluate(condition, new RunbookContext(connections));

    /// <summary>
    /// Whether a whole condition holds — tests joined with and, or and not — against what
    /// <paramref name="context"/> holds. Every test is asked (see <see cref="RunbookExpr.Decide"/>),
    /// so a test naming nothing is a problem whatever the others say.
    /// </summary>
    public ConditionResult Evaluate(RunbookExpr expression, RunbookContext context)
    {
        if (expression is RunbookAtom atom)
            return Evaluate(atom.Condition, context);
        var (holds, problem) = expression.Decide(test =>
        {
            var result = Evaluate(test, context);
            return (result.Holds, result.Problem);
        });
        return problem is not null ? ConditionResult.Broken(problem) : holds ? ConditionResult.Yes : ConditionResult.No;
    }

    /// <summary>One test, against what <paramref name="context"/> holds.</summary>
    public ConditionResult Evaluate(RunbookCondition condition, RunbookContext context)
    {
        var connections = context.Connections;
        switch (condition.Test)
        {
            case RunbookTest.Maintenance:
                return context.Maintenance ? ConditionResult.Yes : ConditionResult.No;

            case RunbookTest.Blind:
                return context.Blind ? ConditionResult.Yes : ConditionResult.No;

            case RunbookTest.IncidentOpen:
                return context.Incidents?.Any(i => i.IsOpen) == true ? ConditionResult.Yes : ConditionResult.No;

            case RunbookTest.AnyAlert:
                return context.Firing?.Any(b => b.Firing) == true ? ConditionResult.Yes : ConditionResult.No;

            case RunbookTest.Alert:
                return AlertFiring(condition.Connection, context);

            case RunbookTest.BackupLate:
                return BackupLate(condition.Connection, context);

            case RunbookTest.DownTab or RunbookTest.UpTab:
                return TabState(condition, context);

            case RunbookTest.AnyDown:
                return connections.Where(isMonitored).Any(c => state(c.Id)?.IsUp == false) ? ConditionResult.Yes : ConditionResult.No;

            case RunbookTest.AllUp:
                // Every one, and "checking" is not up: "all up" is a promise, and one not
                // yet kept while anything is unknown.
                return connections.Where(isMonitored).All(c => state(c.Id)?.IsUp == true) ? ConditionResult.Yes : ConditionResult.No;
        }

        if (ShortcodeLookup.Connection(connections, condition.Connection) is not { } connection)
            return ConditionResult.Broken($"No connection called “{condition.Connection}”.");

        // A paused connection is not probed, so whatever verdict it last had is history.
        var current = connection.Enabled ? state(connection.Id) : null;

        switch (condition.Test)
        {
            case RunbookTest.Down:
                return current?.IsUp == false ? ConditionResult.Yes : ConditionResult.No;
            case RunbookTest.Up:
                return current?.IsUp == true ? ConditionResult.Yes : ConditionResult.No;
            case RunbookTest.AlertOn:
                return context.Firing?.Any(b => b.Firing && b.ConnectionId == connection.Id) == true
                    ? ConditionResult.Yes
                    : ConditionResult.No;
        }

        // The live probe first, then the newest stored reading — the order {{metric: …}}
        // and the metric tile use, so a runbook never acts on a different number from the
        // one printed next to it.
        var live = current?.Metrics ?? new Dictionary<string, double>();
        var recorded = stored(connection.Id);
        if (ShortcodeLookup.MetricKey(registry, connection, condition.Metric, live.Keys.Concat(recorded.Keys)) is not { } key)
            return ConditionResult.Broken($"{connection.Name} has not reported a metric called “{condition.Metric}”.");

        var spec = registry.Metric(connection, key);
        if (condition.Unit.Length > 0 && !SameUnit(condition.Unit, spec.Unit))
        {
            // A number written in another unit of the same quantity — "> 122°F" on a metric
            // stored in °C — is turned into the stored unit once, here, and the comparison
            // runs on the stored reading as it always has. So "> 122°F" and "> 50°C" are the
            // same condition, and a runbook means the same thing whichever units the person
            // reading it has chosen. A bare number is still in the stored unit, as before.
            if (Units.ConvertsTo(spec.Unit, condition.Unit)
                && Units.Convert(condition.Value, Units.Parse(condition.Unit)!, spec.Unit) is { } stored
                && Units.Convert(condition.High, Units.Parse(condition.Unit)!, spec.Unit) is { } storedHigh)
            {
                condition = condition with { Value = stored, High = storedHigh };
            }
            else
            {
                return ConditionResult.Broken(!Units.IsConvertible(spec.Unit) && spec.Unit.Trim().Length > 0
                    ? $"{spec.Label} is compared in its own unit, {spec.Unit.Trim()}; write the number in that, or with no unit."
                    : Units.IsConvertible(spec.Unit)
                    ? $"{spec.Label} is in {spec.Unit.Trim()}; write the number in that, in another unit of the same kind, or with no unit to mean {spec.Unit.Trim()}."
                    : $"{spec.Label} has no unit; write the number on its own.");
            }
        }

        double? value = live.TryGetValue(key, out var now) ? now
            : recorded.TryGetValue(key, out var last) ? last
            : null;
        // No reading is not a problem with what was written, just nothing to go on yet.
        return value is { } v && condition.Compare(v) ? ConditionResult.Yes : ConditionResult.No;
    }

    /// <summary>
    /// An alert rule by the name it was given — or, for a rule nobody named, the name the
    /// Alerts page makes up for it ("NAS · Disk used above 90") — firing on any connection.
    /// A name no rule has is a problem, so a renamed rule is pointed out rather than
    /// quietly never firing; while the rules are still being read it is simply false.
    /// </summary>
    private ConditionResult AlertFiring(string name, RunbookContext context)
    {
        if (context.Rules is not { } rules)
            return ConditionResult.No;
        var wanted = name.Trim();
        var byId = context.Connections.ToDictionary(c => c.Id, StringComparer.Ordinal);

        bool Named(AlertRule rule, Connection? on)
        {
            if (string.Equals(rule.Name.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                return true;
            if (rule.Name.Trim().Length > 0)
                return false;
            var label = on is null ? rule.Metric : registry.Metric(on, rule.Metric).Label;
            return string.Equals(rule.Describe(label, on?.Name), wanted, StringComparison.OrdinalIgnoreCase);
        }

        var matching = rules
            .Where(r => Named(r, r.ConnectionId is { } id ? byId.GetValueOrDefault(id) : null))
            .Select(r => r.Id)
            .ToHashSet(StringComparer.Ordinal);
        // A rule on "any connection" is named after the connection it fires on.
        foreach (var breach in context.Firing ?? [])
        {
            if (breach.Firing && rules.FirstOrDefault(r => r.Id == breach.RuleId) is { } rule
                && byId.GetValueOrDefault(breach.ConnectionId) is { } on && Named(rule, on))
            {
                matching.Add(rule.Id);
            }
        }
        if (matching.Count == 0)
            return ConditionResult.Broken($"No alert rule called “{wanted}”. Give the rule that name on the Alerts page, or copy its name from there.");
        return context.Firing?.Any(b => b.Firing && matching.Contains(b.RuleId)) == true ? ConditionResult.Yes : ConditionResult.No;
    }

    /// <summary>
    /// Whether a backup — the one named, or any — is late, as the Backups page last judged
    /// it: overdue, or a source answering that there has never been one. One that cannot be
    /// checked at all is not "late", since nothing says it is; the Backups page lists it.
    /// </summary>
    private static ConditionResult BackupLate(string name, RunbookContext context)
    {
        if (context.Backups is not { } rows)
            return ConditionResult.No;
        static bool Late(BackupRow row) => row.Status.State is BackupState.Late or BackupState.Never;
        if (name.Trim().Length == 0)
            return rows.Any(Late) ? ConditionResult.Yes : ConditionResult.No;

        var wanted = name.Trim();
        var row = rows.FirstOrDefault(r => string.Equals(r.Item.Id, wanted, StringComparison.Ordinal))
                  ?? rows.FirstOrDefault(r => string.Equals(r.Item.Name.Trim(), wanted, StringComparison.OrdinalIgnoreCase));
        if (row is null)
            return ConditionResult.Broken($"No backup called “{wanted}” on the Backups page.");
        return Late(row) ? ConditionResult.Yes : ConditionResult.No;
    }

    /// <summary>
    /// <c>down: tab "Media"</c> and <c>up: tab "Media"</c>: the monitored connections whose
    /// cards are on that tab, judged as <c>any down</c> and <c>all up</c> judge the lab.
    /// </summary>
    private ConditionResult TabState(RunbookCondition condition, RunbookContext context)
    {
        if (LabStatus.FindTab(context.Tabs ?? [], condition.Connection) is not { } tab)
        {
            // Before tabs could be named here, "down: tab Media" meant a connection called
            // "tab Media". If there is one, that is still what it means.
            if (ShortcodeLookup.Connection(context.Connections, "tab " + condition.Connection) is { } old)
            {
                var test = condition.Test == RunbookTest.DownTab ? RunbookTest.Down : RunbookTest.Up;
                return Evaluate(condition with { Test = test, Connection = old.Id }, context);
            }
            return context.Tabs is null
                ? ConditionResult.No
                : ConditionResult.Broken($"No tab called “{condition.Connection}”.");
        }

        var on = LabStatus.OnTab(tab, context.Widgets ?? [], context.Connections).Where(isMonitored).ToList();
        if (condition.Test == RunbookTest.DownTab)
            return on.Any(c => state(c.Id)?.IsUp == false) ? ConditionResult.Yes : ConditionResult.No;
        return on.All(c => state(c.Id)?.IsUp == true) ? ConditionResult.Yes : ConditionResult.No;
    }

    private static bool SameUnit(string written, string unit) =>
        string.Equals(written.Replace(" ", ""), unit.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What is down right now, for <c>{{down}}</c>: the connections the dashboard's own
    /// counts consider — enabled, and of a kind that is monitored at all — whose last verdict
    /// is down. Longest down first, since in a chain of failures the first to go is usually
    /// the one to fix.
    ///
    /// A silenced connection is listed and marked, not hidden. Silencing holds its alerts
    /// (often because somebody just pressed Restart) but it is still down, and the dashboard
    /// counts it as down; a runbook saying "everything's up" beside a NAS that is visibly
    /// rebooting would be the one place in the app that lies. <c>silenced="hide"</c> leaves
    /// them out for a page that wants only the surprises.
    /// </summary>
    /// <param name="code">The <c>{{down}}</c> shortcode, for its options: <c>only="NAS, Plex"</c>,
    /// <c>include="checking"</c>, <c>silenced="hide"</c>.</param>
    public DownList Down(Shortcode code, IReadOnlyList<Connection> connections, DateTimeOffset now)
    {
        string? problem = null;
        IEnumerable<Connection> candidates = connections.Where(isMonitored);

        if (code.Option("only") is { Length: > 0 } only)
        {
            var named = new List<Connection>();
            var missing = new List<string>();
            foreach (var name in only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (ShortcodeLookup.Connection(connections, name) is { } found)
                    named.Add(found);
                else
                    missing.Add(name);
            }
            if (missing.Count > 0)
                problem = $"No connection called {string.Join(", ", missing.Select(m => $"“{m}”"))}.";
            var ids = named.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            candidates = candidates.Where(c => ids.Contains(c.Id));
        }

        var include = code.Option("include").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var withChecking = include.Contains("checking", StringComparer.OrdinalIgnoreCase);
        var hideSilenced = string.Equals(code.Option("silenced"), "hide", StringComparison.OrdinalIgnoreCase);

        var entries = new List<DownEntry>();
        foreach (var connection in candidates)
        {
            var current = state(connection.Id);
            var listed = current?.IsUp == false || (withChecking && current?.IsUp is null);
            if (!listed)
                continue;
            var silenced = connection.IsSilenced(now) ? connection.SilencedUntil : null;
            if (silenced is not null && hideSilenced)
                continue;
            entries.Add(new DownEntry(connection, current, silenced));
        }

        // Down before checking; longest down first; then by name, so the order is the same
        // on every screen and every sweep.
        var ordered = entries
            .OrderBy(e => e.State is null ? 1 : 0)
            .ThenBy(e => e.State?.ChangedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(e => e.Connection.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new DownList(ordered, problem);
    }
}
