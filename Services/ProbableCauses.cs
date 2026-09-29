using System.Collections.Concurrent;
using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// Gathers what <see cref="IncidentCauses"/> needs — the feed around an incident, the
/// connections and what they sit behind, the capacity forecasts — and asks it. The rules
/// themselves are pure and live in Core; this is only the reading.
///
/// A closed incident's causes are kept in memory once worked out: nothing about it will
/// change, and the Incidents page and every <c>{{incidents}}</c> would otherwise read its
/// half hour of the feed again on every redraw. An open one is worked out each time.
///
/// It also offers the down notification a cause, if one can be found in
/// <see cref="NotificationBudget"/>. <see cref="AlertService"/> does not depend on this —
/// it is handed <see cref="ExplainDownAsync"/> when this starts — so the alerting keeps
/// working, and keeps being testable, without the change feed.
/// </summary>
public sealed class ProbableCauses(
    ChangeStore changes,
    IncidentStore incidents,
    ConfigStore config,
    Registry registry,
    AlertRuleStore rules,
    CapacityForecasts forecasts,
    AlertService alerts,
    ILogger<ProbableCauses> log) : IHostedService
{
    /// <summary>
    /// How long a down notification waits for its cause. A notification that arrives late
    /// because it was being explained is worse than one that arrives bare.
    /// </summary>
    public static readonly TimeSpan NotificationBudget = TimeSpan.FromSeconds(2);

    /// <summary>The most changes read around one incident — far more than a half hour ever holds.</summary>
    private const int ChangeLimit = 500;

    /// <summary>Closed incidents remembered, before the memory is simply cleared and started again.</summary>
    private const int CacheLimit = 500;

    private readonly ConcurrentDictionary<long, (DateTimeOffset LastActivity, IReadOnlyList<ProbableCause> Causes)> _closed = new();

    public Task StartAsync(CancellationToken ct)
    {
        alerts.ExplainDown = ExplainDownAsync;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        alerts.ExplainDown = null;
        return Task.CompletedTask;
    }

    /// <summary>The likely causes of one incident, surest first.</summary>
    public async Task<IReadOnlyList<ProbableCause>> ExplainAsync(Incident incident, CancellationToken ct = default)
    {
        var all = await ExplainAsync([incident], ct);
        return all.GetValueOrDefault(incident.Id, []);
    }

    /// <summary>The likely causes of each incident, by id. The connections and rules are read once for all of them.</summary>
    public async Task<IReadOnlyDictionary<long, IReadOnlyList<ProbableCause>>> ExplainAsync(
        IReadOnlyList<Incident> list, CancellationToken ct = default)
    {
        var result = new Dictionary<long, IReadOnlyList<ProbableCause>>();
        var todo = new List<Incident>();
        foreach (var incident in list)
        {
            if (!incident.IsOpen && _closed.TryGetValue(incident.Id, out var known) && known.LastActivity == incident.LastActivity)
                result[incident.Id] = known.Causes;
            else
                todo.Add(incident);
        }
        if (todo.Count == 0)
            return result;

        var connections = await config.ConnectionsAsync(ct);
        var lab = LabFrom(connections);
        var ruleList = await rules.AllAsync(ct);
        var now = DateTimeOffset.Now;
        foreach (var incident in todo)
        {
            var feed = await changes.QueryAsync(new ChangeQuery(
                IncidentRules.TimelineFrom(incident), IncidentRules.TimelineTo(incident, now), Limit: ChangeLimit), ct);
            var causes = IncidentCauses.Explain(incident, feed, lab, Forecasts(incident, ruleList, connections));
            result[incident.Id] = causes;

            if (!incident.IsOpen)
            {
                if (_closed.Count >= CacheLimit)
                    _closed.Clear();
                _closed[incident.Id] = (incident.LastActivity, causes);
            }
        }
        return result;
    }

    /// <summary>
    /// A cause for a service that has just gone down, for its notification: worked out as if
    /// it had joined whatever incident is open, since the tracker may not have added it yet.
    /// Null when there is nothing worth saying, or it could not be found in time.
    /// </summary>
    public async Task<string?> ExplainDownAsync(Connection connection, DateTimeOffset at, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(NotificationBudget);
        try
        {
            var open = await incidents.OpenAsync(budget.Token);
            var joined = open.Where(i => at - i.LastActivity <= IncidentRules.JoinWindow).OrderByDescending(i => i.LastActivity).FirstOrDefault();
            var key = IncidentMember.StatusKey(connection.Id);
            var member = new IncidentMember(key, ChangeKinds.Status, connection.Id, connection.Name, at, null);
            var incident = joined is null
                ? new Incident(0, at, null, at, false, [member])
                : joined with { Members = [.. joined.Members.Where(m => m.Key != key), member] };

            var feed = await changes.QueryAsync(new ChangeQuery(IncidentRules.TimelineFrom(incident), at, Limit: ChangeLimit), budget.Token);
            var causes = IncidentCauses.Explain(incident, feed, await LabAsync(budget.Token));

            // Only what concerns this service, and only if it is more than a hunch: a
            // notification is read in a second, and "maybe DNS" is not worth that second.
            var cause = causes.FirstOrDefault(c => c.Confidence >= CauseConfidence.Medium && Mentions(c, connection));
            return cause?.Sentence;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogDebug("No cause for {Connection} going down within {Budget}", connection.Name, NotificationBudget);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogDebug(ex, "Could not work out why {Connection} went down", connection.Name);
            return null;
        }
    }

    /// <summary>Whether a cause is about this connection, rather than about another part of the same incident.</summary>
    private static bool Mentions(ProbableCause cause, Connection connection) =>
        cause.Kind is CauseKinds.SelfUpdate or CauseKinds.Host
        || cause.Evidence.Any(c => c.ConnectionId == connection.Id)
        || cause.Sentence.Contains(connection.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>The connections as the rules see them. The config store caches them, so this is normally no read.</summary>
    public async Task<CauseLab> LabAsync(CancellationToken ct = default) =>
        LabFrom(await config.ConnectionsAsync(ct));

    /// <summary>The lab from a list of connections, each with the hosts its settings point at.</summary>
    public static CauseLab LabFrom(IEnumerable<Connection> connections) =>
        new(connections.Select(c => new CauseConnection(
            c.Id, c.Name, c.Provider, c.DependsOn,
            [.. c.Settings.Values.Select(ContainerSafety.HostOf).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)])));

    /// <summary>
    /// The capacity forecast behind each alert member that watches a capacity metric — its
    /// own, or the measured one under a <c>days_until_full:</c> rule.
    /// </summary>
    private Dictionary<string, CauseForecast> Forecasts(Incident incident, IReadOnlyList<AlertRule> ruleList, IReadOnlyList<Connection> connections)
    {
        var found = new Dictionary<string, CauseForecast>();
        foreach (var member in incident.Members.Where(m => m.Kind == ChangeKinds.Alert && m.ConnectionId is not null))
        {
            var parts = member.Key.Split(':', 3);
            if (parts.Length != 3 || ruleList.FirstOrDefault(r => r.Id == parts[1]) is not { } rule)
                continue;

            var metric = CapacityMetric.TryParse(rule.Metric, out var measured) ? measured : rule.Metric;
            if (forecasts.Soonest(member.ConnectionId!, metric) is not { } entry)
                continue;

            var connection = connections.FirstOrDefault(c => c.Id == member.ConnectionId);
            var label = registry.MetricsFor(connection).FirstOrDefault(m => string.Equals(m.Key, metric, StringComparison.OrdinalIgnoreCase))?.Label
                        ?? metric;
            found[member.Key] = new CauseForecast(
                connection is null ? label : $"{label} on {connection.Name}", entry.Forecast);
        }
        return found;
    }
}
