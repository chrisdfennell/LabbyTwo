using LabbyTwo.Core;
using LabbyTwo.Storage;
using Microsoft.Extensions.Options;

namespace LabbyTwo.Services;

/// <summary>
/// Collects a month of what happened into a <see cref="MonthlyReportData"/> for
/// <see cref="MonthlyReport.Build"/> to write up.
///
/// The heaviest thing LabbyTwo reads, and it reads it once a month, so the rule is the one
/// the weekly summary follows, applied harder: every read is bounded by the month at both
/// ends and uses an index built for it (the query-plan tests hold each one to that), and
/// nothing reads <c>samples</c> except one aggregate per service for its average response
/// time — a range on the covering index, mostly over hourly summaries. Status events, kept
/// for ever, are two range reads per service; incidents one range on their start time; the
/// feed's alerts, backups and self-healing one grouped range between them.
///
/// It is also done in chunks. Each read opens, answers and closes on its own, and the
/// services are taken a few at a time with a pause between, so a sweep's writes and a page's
/// reads slot in between rather than waiting behind a month of history. And every part is
/// gathered on its own: one that fails loses its section, not the report.
///
/// Callers run this off the render thread — the job runs on the background runner, and the
/// Settings page's preview goes through <see cref="Offload"/>.
/// </summary>
public sealed class MonthlyReportGatherer(
    ConfigStore config,
    Registry registry,
    HistoryStore history,
    IncidentStore incidents,
    ChangeStore changes,
    SafeUpdateStore updates,
    ProbableCauses causes,
    IOptions<LabbyOptions> options,
    ILogger<MonthlyReportGatherer> log,
    PowerCosts? power = null,
    BackupProof? backups = null)
{
    /// <summary>Services read together before a pause.</summary>
    public const int Chunk = 8;

    /// <summary>The pause between chunks: long enough for a waiting writer to go first, short enough not to matter.</summary>
    public static readonly TimeSpan Breather = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// The most transitions read for one service in one month. Flapping every probe for a
    /// month is ~90,000; this describes any real month and caps the one that is not.
    /// </summary>
    public const int EventLimit = 5000;

    /// <summary>The most incidents read; far more than a month worth reading about has.</summary>
    public const int IncidentLimit = 300;

    /// <summary>The most self-healing entries read from the feed.</summary>
    public const int HealingLimit = 500;

    public async Task<MonthlyReportData> GatherAsync(DateOnly month, TimeZoneInfo zone, CancellationToken ct)
    {
        var (from, to) = MonthlySchedule.Span(month, zone);
        var all = await config.ConnectionsAsync(ct);
        var names = all.ToDictionary(c => c.Id, c => c.Name);
        var monitored = all
            .Where(c => c.Enabled && registry.Provider(c.Provider)?.IsMonitored != false)
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var services = new List<ServiceMonth>();
        for (var i = 0; i < monitored.Count; i += Chunk)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var connection in monitored.Skip(i).Take(Chunk))
            {
                if (await Safely($"the uptime of {connection.Name}", () => ServiceAsync(connection, from, to, ct)) is { } service)
                    services.Add(service);
            }
            if (i + Chunk < monitored.Count)
                await Task.Delay(Breather, ct);
        }

        var incidentLines = await Safely("the incidents", () => IncidentsAsync(from, to, ct)) ?? [];
        var counts = await Safely("the change feed", () => changes.CountsAsync(from, to,
            [ChangeKinds.Alert, ChangeKinds.Backup], ct)) ?? [];
        var healing = await Safely("self-healing", () => HealingAsync(from, to, ct)) ?? [];

        // The feed is pruned; a month older than it is kept is counted only from where it starts.
        var feedFrom = DateTimeOffset.Now - options.Value.ChangeRetention;
        return new MonthlyReportData
        {
            Month = month,
            From = from,
            To = to,
            Zone = zone,
            Services = services,
            Incidents = incidentLines,
            Alerts = Alerts(counts, names),
            Backups = await Safely("the backups", () => BackupsAsync(counts, to, ct)) ?? [],
            Power = await PowerAsync(to, zone, ct),
            Updates = await Safely("the safe updates", () => UpdatesAsync(from, to, names, ct)) ?? [],
            Healing = healing,
            FeedFrom = feedFrom > from ? feedFrom : null,
        };
    }

    private async Task<ServiceMonth> ServiceAsync(Connection connection, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var window = await history.StatusBetweenAsync(connection.Id, from, to, EventLimit, ct);
        var prior = window.Prior is { } p ? new StatusChange(p.At, p.IsUp, p.Message) : null;
        var events = window.Events.Select(e => new StatusChange(e.At, e.IsUp, e.Message)).ToList();

        // A truncated month ends at the last event read, not at the month's end: carrying
        // event five thousand's state on for the rest of the month would invent an outage or
        // erase one.
        var end = window.Truncated && events.Count > 0 ? events[^1].At : to;
        var month = ServiceMonth.Measure(connection.Name, connection.Id, prior, events, from, end) with { Truncated = window.Truncated };

        var latency = await history.AggregateAsync(connection.Id, "latency_ms", from, to, ct);
        return month with { AverageLatencyMs = latency?.Average };
    }

    private async Task<IReadOnlyList<IncidentLine>> IncidentsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var list = await incidents.OverlappingAsync(from, to, IncidentLimit, ct);
        if (list.Count == 0)
            return [];

        // A few at a time, like the services: each incident's causes read half an hour of
        // the feed around it.
        var explained = new Dictionary<long, IReadOnlyList<ProbableCause>>();
        for (var i = 0; i < list.Count; i += Chunk)
        {
            try
            {
                foreach (var (id, found) in await causes.ExplainAsync([.. list.Skip(i).Take(Chunk)], ct))
                    explained[id] = found;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Could not work out what caused some incidents for the monthly report");
            }
            if (i + Chunk < list.Count)
                await Task.Delay(Breather, ct);
        }

        return
        [
            .. list.Select(incident => new IncidentLine(
                incident.Title,
                incident.StartedAt,
                incident.EndedAt,
                incident.Members.Select(m => m.Key).Distinct().Count(),
                incident.Maintenance,
                explained.GetValueOrDefault(incident.Id) is { Count: > 0 } found ? IncidentCauses.Headline(found) : null)),
        ];
    }

    /// <summary>
    /// Each alert rule on each connection, and how often it fired. The rule's name is read
    /// from the newest title the feed recorded for it ("Alert fired: CPU pinned"), so a rule
    /// deleted since is still named as it was.
    /// </summary>
    public static IReadOnlyList<AlertCount> Alerts(IReadOnlyList<ChangeStore.Count> counts, IReadOnlyDictionary<string, string> names) =>
    [
        .. counts
            .Where(c => c.Kind == ChangeKinds.Alert && c.Action == ChangeActions.Firing)
            .Select(c => new AlertCount(
                c.Title.StartsWith("Alert fired: ", StringComparison.Ordinal) ? c.Title["Alert fired: ".Length..] : c.Title,
                c.ConnectionId is { } id ? names.GetValueOrDefault(id) : null,
                c.Times)),
    ];

    /// <summary>
    /// Each backup item's month from the feed: proven, late and restore-tested. Named from
    /// the Backups page as it is now, falling back to the feed's own words for an item
    /// since removed.
    /// </summary>
    private async Task<IReadOnlyList<BackupMonth>> BackupsAsync(IReadOnlyList<ChangeStore.Count> counts, DateTimeOffset to, CancellationToken ct)
    {
        var mine = counts.Where(c => c.Kind == ChangeKinds.Backup).ToList();
        if (mine.Count == 0)
            return [];

        var items = new Dictionary<string, string>(StringComparer.Ordinal);
        if (backups is not null)
        {
            foreach (var row in await backups.RowsAsync(to, ct))
                items[row.Item.Id] = row.Item.Name;
        }

        return
        [
            .. mine.GroupBy(c => c.Subject).Select(group =>
            {
                var proven = group.FirstOrDefault(c => c.Action == ChangeActions.Completed);
                var name = items.GetValueOrDefault(group.Key) ?? NameFromTitle(proven?.Title ?? group.First().Title);
                return new BackupMonth(
                    name,
                    proven?.Times ?? 0,
                    proven?.Last,
                    group.Where(c => c.Action == ChangeActions.Tested).Sum(c => c.Times),
                    group.Where(c => c.Action == ChangeActions.Late).Sum(c => c.Times));
            }),
        ];
    }

    /// <summary>"Nextcloud backed up" → "Nextcloud", for an item no longer on the page.</summary>
    private static string NameFromTitle(string title)
    {
        foreach (var (prefix, suffix) in new[] { ("", " backed up"), ("Restore of ", " tested"), ("The backup of ", " is late") })
        {
            if (title.StartsWith(prefix, StringComparison.Ordinal) && title.EndsWith(suffix, StringComparison.Ordinal)
                && title.Length > prefix.Length + suffix.Length)
                return title[prefix.Length..^suffix.Length];
        }
        return title;
    }

    private async Task<IReadOnlyList<HealingLine>> HealingAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        // ChangeQuery's end is inclusive to the second; the month's is exclusive.
        var feed = await changes.QueryAsync(new ChangeQuery(from, to.AddSeconds(-1), [ChangeKinds.Remediation], Limit: HealingLimit), ct);
        return [.. feed.Where(c => c.At < to).Select(c => new HealingLine(c.At, c.Action, c.Title))];
    }

    private async Task<IReadOnlyList<UpdateLine>> UpdatesAsync(
        DateTimeOffset from, DateTimeOffset to, IReadOnlyDictionary<string, string> names, CancellationToken ct) =>
    [
        .. (await updates.BetweenAsync(from, to, ct)).Select(u => new UpdateLine(
            u.Container, names.GetValueOrDefault(u.ConnectionId) ?? "a host no longer listed", u.State, u.RequestedAt, u.Reason)),
    ];

    /// <summary>
    /// The month's electricity from the Power page's own sums, worked out as if it were the
    /// last second of the month — so "this month" is that month, not the one just begun.
    /// </summary>
    private async Task<PowerMonth?> PowerAsync(DateTimeOffset to, TimeZoneInfo zone, CancellationToken ct)
    {
        if (power is null)
            return null;
        try
        {
            var snapshot = await power.GetAsync(to.AddSeconds(-1), zone, ct);
            if (snapshot.Reports.Count == 0)
                return null;
            return new PowerMonth(
                snapshot.Month.Kwh,
                snapshot.Month.Cost,
                snapshot.Tariff.Currency,
                [.. snapshot.Reports.Select(r => (r.Name, r.Month.Kwh, r.Month.Cost))]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not work out the month's electricity for the monthly report");
            return null;
        }
    }

    private async Task<T?> Safely<T>(string what, Func<Task<T>> read) where T : class
    {
        try
        {
            return await read();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not read {What} for the monthly report", what);
            return null;
        }
    }
}
