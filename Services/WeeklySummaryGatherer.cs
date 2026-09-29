using System.Text.Json;
using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// Collects a week of what happened into a <see cref="WeeklySummaryData"/> for
/// <see cref="WeeklySummary.Build"/> to phrase.
///
/// Everything here is bounded: per connection, two index range reads for its transitions,
/// one aggregate each for the metrics it cares about, and a seek per metric for the newest
/// readings. Nothing reads a table end to end, because status events are kept for ever and
/// a weekly job that scanned them would get a little slower every week for as long as the
/// install lived. The capacity forecasts are not recomputed at all — they are already in
/// memory, kept current by <see cref="CapacityForecasts"/>.
///
/// Each part is gathered on its own and a failure only loses that part: a summary that says
/// less is better than none, and a digest that arrived every week until one plugin's metric
/// threw would be a strange thing to lose.
///
/// Callers run this off the render thread. SQLite is synchronous under its async API, and
/// this is a few hundred queries on a big install.
/// </summary>
public sealed class WeeklySummaryGatherer(
    ConfigStore config,
    Registry registry,
    HistoryStore history,
    CapacityForecasts forecasts,
    UpdateChecker updates,
    AppSettingsStore settings,
    ILogger<WeeklySummaryGatherer> log,
    PowerCosts? power = null,
    BackupProof? backups = null)
{
    /// <summary>The connections, by id and name, as of the last scheduled summary — what "now watching" is measured against.</summary>
    public const string RosterKey = "weekly_summary_roster";

    /// <summary>The LabbyTwo version the last scheduled summary was sent from.</summary>
    public const string VersionKey = "weekly_summary_version";

    public static readonly TimeSpan Week = TimeSpan.FromDays(7);

    /// <summary>
    /// The most transitions read for one service in one week. A service flapping every
    /// probe for a week is ~20,000; this is enough to describe any real week and small
    /// enough that such a service costs no more than a normal one.
    /// </summary>
    private const int EventLimit = 2000;

    /// <summary>
    /// Metrics that are a countdown to a date, and what to call the thing that expires.
    /// Matched by key rather than by provider, so the certificate check, Uptime Kuma,
    /// Tailscale and the renewals plugin all land here without the host knowing any of
    /// them — and a plugin that reports one of these names later gets it for free.
    /// </summary>
    public static readonly IReadOnlyList<(string Metric, string What)> Countdowns =
    [
        ("cert_days_left", "certificate"),
        ("certs_expiring_days", "certificate"),
        ("key_expiry_days", "node key"),
        ("days_until_next", "next renewal"),
    ];

    /// <summary>The running version, overridable so a test can pretend to have been updated.</summary>
    public string Version { get; init; } = UpdateChecker.Installed;

    public async Task<WeeklySummaryData> GatherAsync(DateTimeOffset now, TimeZoneInfo zone, CancellationToken ct)
    {
        var from = now - Week;

        var monitored = (await config.ConnectionsAsync(ct))
            .Where(c => c.Enabled && registry.Provider(c.Provider)?.IsMonitored != false)
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var services = new List<ServiceWeek>();
        var expiries = new List<ExpiryLine>();
        var speed = new List<SpeedWeek>();

        foreach (var connection in monitored)
        {
            ct.ThrowIfCancellationRequested();

            if (await Safely(connection, "uptime", () => ServiceAsync(connection, from, now, ct)) is { } service)
                services.Add(service);

            var latest = await Safely(connection, "latest readings",
                () => history.LatestAsync(connection.Id, TimeSpan.FromDays(3), ct));
            if (latest is null)
                continue;

            expiries.AddRange(Expiries(connection, latest));

            if (latest.ContainsKey("download_mbps")
                && await Safely(connection, "speed tests", () => SpeedAsync(connection, from, now, ct)) is { } week)
            {
                speed.Add(week);
            }
        }

        var (added, removed) = await ChangesAsync(monitored, ct);
        var previousVersion = (await settings.GetAsync(VersionKey, "", ct)).Trim();
        var updated = previousVersion.Length > 0 && previousVersion != "dev" && Version != "dev"
                      && !string.Equals(previousVersion, Version, StringComparison.Ordinal);

        var backupLines = await BackupsAsync(now, ct);

        return new WeeklySummaryData
        {
            From = from,
            To = now,
            Zone = zone,
            Services = services,
            Capacity = Capacity(monitored),
            Expiries = expiries,
            Speed = speed,
            Power = await PowerAsync(now, zone, ct),
            Backups = backupLines,
            Added = added,
            Removed = removed,
            UpdatedFrom = updated ? previousVersion : null,
            UpdatedTo = updated ? Version : null,
            // Only what somebody has already asked GitHub about. The summary is not a reason
            // to phone home: LabbyTwo checks for updates when asked, and not otherwise.
            UpdateAvailable = updates.Last is { Behind: true, Latest: { Length: > 0 } latestVersion } ? latestVersion : null,
            Units = Units.Preferences.From(await settings.AllAsync(ct)),
        };
    }

    /// <summary>
    /// The week's electricity from the Power page's own sums — the last seven days, not
    /// cached, at this summary's moment and zone. Null when nothing reports power, and when
    /// it cannot be worked out: a summary without its power line is still worth sending.
    /// </summary>
    private async Task<PowerWeek?> PowerAsync(DateTimeOffset now, TimeZoneInfo zone, CancellationToken ct)
    {
        if (power is null)
            return null;
        try
        {
            var snapshot = await power.GetAsync(now, zone, ct);
            if (snapshot.Reports.Count == 0)
                return null;
            return new PowerWeek(
                snapshot.LastWeek.Kwh,
                snapshot.LastWeek.Cost,
                snapshot.Tariff.Currency,
                [.. snapshot.Reports.OrderByDescending(r => r.LastWeek.Cost).Select(r => (r.Name, r.LastWeek.Cost))],
                snapshot.ProjectedMonthCost + snapshot.Tariff.MonthlyFee);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not work out the week's electricity for the weekly summary");
            return null;
        }
    }

    /// <summary>
    /// The Backups page as it stands, judged at the moment of sending. Empty when nothing is
    /// listed, when this gatherer was built without the page (a test), or when it cannot be
    /// read — a lost section, not a lost summary.
    /// </summary>
    private async Task<IReadOnlyList<BackupLine>> BackupsAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (backups is null)
            return [];
        try
        {
            return
            [
                .. (await backups.RowsAsync(now, ct)).Select(r => new BackupLine(
                    r.Item.Name, r.Status.State, r.Status.LastSuccess, r.DrillOverdue, r.Item.LastRestoreTest)),
            ];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not read the backups for the weekly summary");
            return [];
        }
    }

    /// <summary>
    /// Records what this summary was measured against, so the next one's "what changed" is
    /// the difference since this one. Only after a scheduled send: a preview must not use up
    /// the news that a service was added.
    /// </summary>
    public async Task RememberAsync(CancellationToken ct)
    {
        var roster = (await config.ConnectionsAsync(ct))
            .Where(c => c.Enabled && registry.Provider(c.Provider)?.IsMonitored != false)
            .ToDictionary(c => c.Id, c => c.Name);

        await settings.SaveAsync(new Dictionary<string, string>
        {
            [RosterKey] = JsonSerializer.Serialize(roster),
            [VersionKey] = Version,
        }, ct);
    }

    private async Task<ServiceWeek> ServiceAsync(Connection connection, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var window = await history.StatusBetweenAsync(connection.Id, from, to, EventLimit, ct);
        if (window.Truncated)
            log.LogInformation("{Connection} changed state more than {Limit} times this week; the summary reads the first {Limit}",
                connection.Name, EventLimit, EventLimit);

        var prior = window.Prior is { } p ? new StatusChange(p.At, p.IsUp, p.Message) : null;
        var events = window.Events.Select(e => new StatusChange(e.At, e.IsUp, e.Message)).ToList();

        // A truncated week ends at the last event read, not at "now": carrying the state of
        // event two thousand on to the end of the week would invent an outage or erase one.
        var end = window.Truncated && events.Count > 0 ? events[^1].At : to;
        var week = ServiceWeek.Measure(connection.Name, prior, events, from, end);

        var latency = await history.AggregateAsync(connection.Id, "latency_ms", from, to, ct);
        return week with { AverageLatencyMs = latency?.Average };
    }

    public static IEnumerable<ExpiryLine> Expiries(Connection connection, IReadOnlyDictionary<string, double> latest)
    {
        foreach (var (metric, what) in Countdowns)
        {
            if (latest.TryGetValue(metric, out var days))
                yield return new ExpiryLine(connection.Name, what, days);
        }

        // "overdue" is too plain a name to trust from just anybody, so only the renewals
        // list's own count is read as one — through its metric, like everything else here,
        // so the host still knows nothing about the plugin.
        if (string.Equals(connection.Provider, "renewals", StringComparison.OrdinalIgnoreCase)
            && latest.TryGetValue("overdue", out var overdue) && overdue >= 1)
        {
            yield return new ExpiryLine(connection.Name, "", 0, (int)overdue);
        }
    }

    private async Task<SpeedWeek> SpeedAsync(Connection connection, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var download = await history.AggregateAsync(connection.Id, "download_mbps", from, to, ct);
        var upload = await history.AggregateAsync(connection.Id, "upload_mbps", from, to, ct);
        var ping = await history.AggregateAsync(connection.Id, "ping_ms", from, to, ct);

        // Both missing is a week with no tests in it; the builder leaves such a line out.
        return new SpeedWeek(connection.Name, download?.Average, download?.Min, upload?.Average, ping?.Average);
    }

    private IReadOnlyList<CapacityLine> Capacity(IReadOnlyList<Connection> monitored)
    {
        // Before the first pass after a restart there is nothing to say either way, and
        // "nothing is filling up" would be a claim nobody has checked.
        if (!forecasts.HasRun)
            return [];

        try
        {
            var byId = monitored.ToDictionary(c => c.Id);
            return
            [
                // Without the duplicates, so a NAS's only volume is not reported twice.
                .. CapacityForecasts.Representatives(forecasts.All)
                    .Where(e => byId.ContainsKey(e.ConnectionId))
                    .Select(e =>
                    {
                        var connection = byId[e.ConnectionId];
                        var spec = registry.Metric(connection, e.Metric);
                        return new CapacityLine(connection.Name, spec.Label, e.Forecast.Current,
                            e.Forecast.RatePerDay * 7, spec.Unit, spec.Decimals, e.Forecast);
                    }),
            ];
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not read the capacity forecasts for the weekly summary");
            return [];
        }
    }

    /// <summary>
    /// Connections added and removed since the last scheduled summary, by id — so renaming
    /// one is not reported as losing it and gaining another. The very first summary has
    /// nothing to compare with and says nothing, rather than announcing every connection
    /// as new.
    /// </summary>
    private async Task<(IReadOnlyList<string> Added, IReadOnlyList<string> Removed)> ChangesAsync(
        IReadOnlyList<Connection> monitored, CancellationToken ct)
    {
        try
        {
            var stored = await settings.GetAsync(RosterKey, "", ct);
            if (stored.Length == 0)
                return ([], []);

            var previous = JsonSerializer.Deserialize<Dictionary<string, string>>(stored) ?? [];
            var current = monitored.ToDictionary(c => c.Id, c => c.Name);

            return (
                [.. current.Where(c => !previous.ContainsKey(c.Key)).Select(c => c.Value)],
                [.. previous.Where(p => !current.ContainsKey(p.Key)).Select(p => p.Value).Order(StringComparer.OrdinalIgnoreCase)]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not work out what changed for the weekly summary");
            return ([], []);
        }
    }

    private async Task<T?> Safely<T>(Connection connection, string what, Func<Task<T>> read) where T : class
    {
        try
        {
            return await read();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not read the {What} of {Connection} for the weekly summary", what, connection.Name);
            return null;
        }
    }
}
