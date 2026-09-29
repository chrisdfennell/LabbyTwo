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
    public ConditionResult Evaluate(RunbookCondition condition, IReadOnlyList<Connection> connections)
    {
        switch (condition.Test)
        {
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
                && Units.Convert(condition.Value, Units.Parse(condition.Unit)!, spec.Unit) is { } stored)
            {
                condition = condition with { Value = stored };
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
