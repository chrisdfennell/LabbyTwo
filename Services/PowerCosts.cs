using LabbyTwo.Core;
using LabbyTwo.Storage;
using Microsoft.Extensions.Options;

namespace LabbyTwo.Services;

/// <summary>
/// What the lab's electricity costs, per plug and per service — the numbers behind the
/// Power page, the <c>{{power}}</c> shortcode and the weekly summary's power line.
///
/// Finds the power sources itself: every enabled connection's recorded metrics that are
/// power readings or energy counters (<see cref="PowerMetrics"/>), paired up so a plug's own
/// meter is preferred to integrating its watts. The user's choices — which count, what they
/// are called, what runs off them — come from <see cref="PowerSetup"/>, and the prices from
/// <see cref="PowerTariff"/>, both in the app settings.
///
/// Working it out means reading up to three months of each source: two index range reads
/// apiece (see <see cref="HistoryStore.EnergyInputAsync"/>) and the integration in memory.
/// That is cheap once and wasteful on every render, and the shortcode can sit on a wall
/// display redrawing every sweep, so the answer is kept for <see cref="CacheFor"/> and
/// shared by everyone who asks. A change to the settings drops it at once. Callers run
/// this off the render thread — SQLite is synchronous under its async API.
/// </summary>
public sealed class PowerCosts : IDisposable
{
    /// <summary>The longest chart the page offers, and so the furthest back anything reads.</summary>
    public const int ChartDays = 90;

    /// <summary>
    /// How long one answer is reused. Energy moves slowly — a 50 W plug uses under two
    /// watt-hours in two minutes — so nobody can tell a two-minute-old total from a new one.
    /// </summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(2);

    private readonly ConfigStore _config;
    private readonly Registry _registry;
    private readonly HistoryStore _history;
    private readonly AppSettingsStore _settings;
    private readonly IOptions<LabbyOptions> _options;
    private readonly ILogger<PowerCosts> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Snapshot? _cached;
    private int _version;

    public PowerCosts(
        ConfigStore config, Registry registry, HistoryStore history, AppSettingsStore settings,
        IOptions<LabbyOptions> options, ILogger<PowerCosts> log)
    {
        _config = config;
        _registry = registry;
        _history = history;
        _settings = settings;
        _options = options;
        _log = log;
        _settings.Changed += OnSettingsChanged;
        _config.Changed += Invalidate;
    }

    /// <summary>
    /// One power source as found: a connection, its power reading and/or its counter, and
    /// what the user has said about it.
    /// </summary>
    /// <param name="Scale">Turns one counter reading into kWh.</param>
    public sealed record Source(
        string Key, Connection Connection, string Name, MetricSpec? Power, MetricSpec? Counter, double Scale,
        PowerSourceSettings? Settings)
    {
        public bool Include => Settings?.Include ?? true;

        /// <summary>What the page says the numbers come from.</summary>
        public string Measures => (Power, Counter) switch
        {
            ({ } p, { } c) => $"{c.Label} (its own meter), else {p.Label}",
            ({ } p, null) => $"{p.Label}, integrated",
            (null, { } c) => $"{c.Label} (its own meter)",
            _ => "",
        };
    }

    /// <summary>Everything worked out at one moment.</summary>
    public sealed record Snapshot(
        DateTimeOffset At,
        PowerTariff Tariff,
        PowerSetup Setup,
        IReadOnlyList<Source> Sources,
        IReadOnlyList<PowerReport> Reports,
        PeriodEnergy Today,
        PeriodEnergy Month,
        PeriodEnergy Last30,
        PeriodEnergy LastWeek,
        double ProjectedMonthCost,
        double ProjectedMonthKwh,
        IReadOnlyList<DayEnergy> Days,
        IReadOnlyList<ServiceCost> Services)
    {
        /// <summary>The whole lab's figures, as the shortcode reads them.</summary>
        public PowerTotals Totals => new(Today, LastWeek, Month, Last30, ProjectedMonthCost, ProjectedMonthKwh);

        /// <summary>
        /// A source or a service by the name the shortcode was given: a source's own name,
        /// its connection's name, or a service somebody split a plug between.
        /// </summary>
        public PowerReport? Report(string name)
        {
            var wanted = name.Trim();
            return Reports.FirstOrDefault(r => string.Equals(r.Name, wanted, StringComparison.OrdinalIgnoreCase))
                   ?? Sources.Where(s => s.Include && string.Equals(s.Connection.Name.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                       .Select(s => Reports.FirstOrDefault(r => r.Key == s.Key)).FirstOrDefault(r => r is not null)
                   ?? Reports.FirstOrDefault(r => string.Equals(r.Key, wanted, StringComparison.Ordinal));
        }

        public ServiceCost? Service(string name) =>
            Services.FirstOrDefault(s => string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Raised, off the render thread, when the settings changed and the cached answer was dropped.</summary>
    public event Action? Changed;

    /// <summary>The answer as of now, from the cache when it is recent enough.</summary>
    public Task<Snapshot> GetAsync(CancellationToken ct = default) => GetAsync(DateTimeOffset.Now, TimeZoneInfo.Local, ct);

    /// <summary><see cref="GetAsync(CancellationToken)"/> at a chosen moment and zone — never cached, for tests and the weekly summary.</summary>
    public async Task<Snapshot> GetAsync(DateTimeOffset now, TimeZoneInfo zone, CancellationToken ct)
    {
        var live = zone == TimeZoneInfo.Local && Math.Abs((DateTimeOffset.Now - now).TotalSeconds) < 5;
        if (live && _cached is { } cached && now - cached.At < CacheFor)
            return cached;

        await _gate.WaitAsync(ct);
        try
        {
            if (live && _cached is { } again && now - again.At < CacheFor)
                return again;
            var version = _version;
            var snapshot = await BuildAsync(now, zone, ct);
            if (live && version == _version)
                _cached = snapshot;
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Every power source any enabled connection has recorded, with its saved settings.</summary>
    public async Task<IReadOnlyList<Source>> SourcesAsync(PowerSetup setup, CancellationToken ct)
    {
        var sources = new List<Source>();
        var connections = (await _config.ConnectionsAsync(ct))
            .Where(c => c.Enabled)
            .OrderBy(c => c.Sort).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var connection in connections)
        {
            ct.ThrowIfCancellationRequested();

            // What history holds, not only what the provider declares: a declared metric
            // that never arrives (a UPS that does not report its wattage) has nothing to
            // show, and a plugin's undeclared one does.
            IReadOnlyList<string> recorded;
            try
            {
                recorded = await _history.MetricsAsync(connection.Id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Could not list the metrics of {Connection} for the Power page", connection.Name);
                continue;
            }

            var pairs = PowerMetrics.Pair(recorded.Select(key => _registry.Metric(connection, key)));
            foreach (var (power, counter, scale) in pairs)
            {
                var metric = (power ?? counter)!.Key;
                var key = PowerSourceSettings.KeyFor(connection.Id, metric);
                var saved = setup.Find(key);
                var name = saved is { Name.Length: > 0 }
                    ? saved.Name
                    : pairs.Count == 1 ? connection.Name : $"{connection.Name} · {(power ?? counter)!.Label}";
                sources.Add(new Source(key, connection, name, power, counter, scale, saved));
            }
        }
        return sources;
    }

    private async Task<Snapshot> BuildAsync(DateTimeOffset now, TimeZoneInfo zone, CancellationToken ct)
    {
        var bag = await _settings.AllAsync(ct);
        var tariff = PowerTariff.From(bag);
        var setup = PowerSetup.From(bag);
        var sources = await SourcesAsync(setup, ct);

        var monthStart = PowerCost.StartOfMonth(now, zone);
        var chartStart = PowerCost.StartOfDay(PowerCost.LocalDate(now, zone).AddDays(-(ChartDays - 1)), zone);
        // An hour early, so a counter has a reading from before the window to count from.
        var from = (monthStart < chartStart ? monthStart : chartStart).AddHours(-1);

        var reports = new List<PowerReport>();
        foreach (var source in sources.Where(s => s.Include))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (hours, fromCounter) = await HoursAsync(source, from, now, ct);
                reports.Add(PowerCost.Report(source.Key, source.Name, hours, fromCounter, tariff, zone, now, ChartDays));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One series that cannot be read costs that one line, not the page.
                _log.LogWarning(ex, "Could not work out the energy used by {Source}", source.Name);
            }
        }

        var days = reports.SelectMany(r => r.Days)
            .GroupBy(d => d.Date)
            .OrderBy(g => g.Key)
            .Select(g => new DayEnergy(g.Key, g.Sum(d => d.Kwh), g.Sum(d => d.Cost)))
            .ToList();

        return new Snapshot(
            now, tariff, setup, sources, reports,
            PowerCost.Sum(reports.Select(r => r.Today)),
            PowerCost.Sum(reports.Select(r => r.Month)),
            PowerCost.Sum(reports.Select(r => r.Last30)),
            PowerCost.Sum(reports.Select(r => r.LastWeek)),
            reports.Sum(r => r.ProjectedMonthCost),
            reports.Sum(r => r.ProjectedMonthKwh),
            days,
            PowerCost.Attribute(reports, setup));
    }

    /// <summary>One source's energy per hour, from its counter where it has one and its watts otherwise.</summary>
    private async Task<(SortedDictionary<long, Energy.HourEnergy> Hours, bool FromCounter)> HoursAsync(
        Source source, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        SortedDictionary<long, Energy.HourEnergy>? fromPower = null, fromCounter = null;

        if (source.Power is { } power)
        {
            var (hours, raw) = await _history.EnergyInputAsync(source.Connection.Id, power.Key, from, to, ct);
            fromPower = Energy.FromPower(hours, raw, Energy.EstimateInterval(raw, ProbeInterval(source.Connection)));
        }
        if (source.Counter is { } counter)
        {
            var (hours, raw) = await _history.EnergyInputAsync(source.Connection.Id, counter.Key, from, to, ct);
            fromCounter = Energy.FromCounter(hours, raw, source.Scale);
        }

        var counted = fromCounter is { Count: > 0 } && fromCounter.Values.Any(h => h.CoveredSeconds > 0);
        return (fromCounter, fromPower) switch
        {
            ({ } c, { } p) => (Energy.Prefer(c, p), counted),
            ({ } c, null) => (c, counted),
            (null, { } p) => (p, false),
            _ => (new SortedDictionary<long, Energy.HourEnergy>(), false),
        };
    }

    /// <summary>How often the monitor asks this connection: the sweep, or the provider's own minimum if longer.</summary>
    private TimeSpan ProbeInterval(Connection connection)
    {
        var sweep = TimeSpan.FromSeconds(Math.Clamp(_options.Value.ProbeSeconds, 5, 3600));
        var minimum = _registry.Provider(connection.Provider)?.MinimumIntervalFor(connection) ?? TimeSpan.Zero;
        return minimum > sweep ? minimum : sweep;
    }

    /// <summary>Saves the prices and the per-source choices together, which drops the cached answer.</summary>
    public Task SaveAsync(PowerTariff tariff, PowerSetup setup, CancellationToken ct = default)
    {
        var values = tariff.ToSettings();
        foreach (var (key, value) in setup.ToSettings())
            values[key] = value;
        return _settings.SaveAsync(values, ct);
    }

    /// <summary>A connection added, renamed or removed: the next answer is worked out afresh.</summary>
    private void Invalidate()
    {
        Interlocked.Increment(ref _version);
        _cached = null;
    }

    /// <summary>
    /// Any app setting saved — not necessarily a price, but telling which would mean keeping
    /// the old values to compare, and a redraw of a few shortcodes costs nothing.
    /// </summary>
    private void OnSettingsChanged()
    {
        Invalidate();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _config.Changed -= Invalidate;
    }
}
