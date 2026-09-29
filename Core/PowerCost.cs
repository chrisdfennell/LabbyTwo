using System.Text.Json;

namespace LabbyTwo.Core;

/// <summary>
/// Which metrics are power, and which are energy counters.
///
/// Decided from what the provider declared — the unit first ("W", "kWh"), the key second —
/// so a Shelly plug, a UPS, a Home Assistant sensor called <c>rack_watts</c> and a plugin
/// nobody here has heard of are all found the same way, and no list of providers needs
/// keeping.
/// </summary>
public static class PowerMetrics
{
    private static readonly string[] PowerKeys = ["watts", "power", "power_w", "load_watts", "apower"];
    private static readonly string[] PowerSuffixes = ["_watts", "_power_w", "_w"];

    /// <summary>Whether a metric is a reading of power in watts.</summary>
    public static bool IsPower(MetricSpec spec)
    {
        if (spec.Unit.Trim() == "W")
            return true;
        var key = spec.Key;
        return PowerKeys.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
               || PowerSuffixes.Any(s => key.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// What turns one of this metric's readings into kWh when it is a running energy total —
    /// 1 for kWh, 0.001 for Wh — or null when it is not one.
    /// </summary>
    public static double? CounterScale(MetricSpec spec)
    {
        var unit = spec.Unit.Trim();
        if (string.Equals(unit, "kWh", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (string.Equals(unit, "Wh", StringComparison.OrdinalIgnoreCase))
            return 0.001;
        if (unit.Length > 0)
            return null;
        if (spec.Key.EndsWith("kwh", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (spec.Key.EndsWith("_wh", StringComparison.OrdinalIgnoreCase))
            return 0.001;
        return null;
    }

    /// <summary>
    /// The part of a key that names the thing measured: <c>office_watts</c> and
    /// <c>office_kwh</c> are both "office", which is how a power reading and a counter on a
    /// connection that reports several of each are paired up.
    /// </summary>
    public static string Stem(string key)
    {
        var stem = key.ToLowerInvariant();
        foreach (var suffix in new[] { "energy_kwh", "_energy", "_kwh", "kwh", "_wh", "_watts", "watts", "_power_w", "_power", "power", "_w" })
        {
            if (stem.EndsWith(suffix, StringComparison.Ordinal))
                return stem[..^suffix.Length].TrimEnd('_');
        }
        return stem;
    }

    /// <summary>
    /// The power sources one connection offers: each power reading, with the counter that
    /// measures the same thing when there is one, and each counter with no power reading
    /// beside it on its own.
    ///
    /// A counter belongs to a power reading when their <see cref="Stem"/>s match, or when the
    /// connection reports exactly one of each — a Shelly's <c>watts</c> and
    /// <c>energy_kwh</c>, which share no stem but could hardly mean anything else.
    /// </summary>
    public static IReadOnlyList<(MetricSpec? Power, MetricSpec? Counter, double Scale)> Pair(IEnumerable<MetricSpec> metrics)
    {
        var list = metrics.DistinctBy(m => m.Key, StringComparer.OrdinalIgnoreCase).ToList();
        var power = list.Where(IsPower).ToList();
        var counters = list.Where(m => !IsPower(m) && CounterScale(m) is not null).ToList();
        var result = new List<(MetricSpec?, MetricSpec?, double)>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var watts in power)
        {
            var counter = power.Count == 1 && counters.Count == 1
                ? counters[0]
                : counters.FirstOrDefault(c => !used.Contains(c.Key) && Stem(c.Key) == Stem(watts.Key));
            if (counter is not null)
                used.Add(counter.Key);
            result.Add((watts, counter, counter is null ? 1 : CounterScale(counter)!.Value));
        }
        foreach (var counter in counters.Where(c => !used.Contains(c.Key)))
            result.Add((null, counter, CounterScale(counter)!.Value));
        return result;
    }
}

/// <summary>What is plugged into a power source, and how much of it each thing takes.</summary>
/// <param name="Weight">Only read when the source is split by weight; a share of the total weights.</param>
public sealed record ServiceShare(string Name, double Weight = 1)
{
    /// <summary>
    /// Reads the one-line form the Power page's settings use: names separated by commas,
    /// each optionally with a weight — <c>NAS=2, Plex, Frigate=3</c>. Weighted when any
    /// weight was written (a name with none then weighs 1); split evenly otherwise. Written
    /// this way because "what's on this plug" is a short list typed once, and a grid of
    /// rows with add and remove buttons would be more form than the job needs.
    /// </summary>
    public static (IReadOnlyList<ServiceShare> Services, bool Weighted) Parse(string? text)
    {
        var list = new List<ServiceShare>();
        var weighted = false;
        foreach (var item in (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = item.LastIndexOf('=');
            if (equals > 0 && double.TryParse(item[(equals + 1)..].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var weight) && weight >= 0 && double.IsFinite(weight))
            {
                var name = item[..equals].Trim();
                if (name.Length == 0)
                    continue;
                list.Add(new ServiceShare(name, weight));
                weighted = true;
            }
            else
            {
                list.Add(new ServiceShare(item));
            }
        }
        return (list, weighted);
    }

    /// <summary>The list written back in the form <see cref="Parse"/> reads.</summary>
    public static string Format(IReadOnlyList<ServiceShare> services, bool weighted) =>
        string.Join(", ", services.Select(s => weighted
            ? $"{s.Name}={s.Weight.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}"
            : s.Name));
}

/// <summary>
/// The user's choices about one power source: whether it counts, what to call it, and
/// what runs off it.
/// </summary>
/// <param name="Key">"connection id/metric" — the power reading's metric, or the counter's when there is no power reading.</param>
public sealed record PowerSourceSettings(
    string Key, string Name, bool Include, IReadOnlyList<ServiceShare> Services, bool Weighted)
{
    public static string KeyFor(string connectionId, string metric) => $"{connectionId}/{metric}";
}

/// <summary>
/// Every saved <see cref="PowerSourceSettings"/>, as one JSON setting. A source nobody has
/// touched has no entry and is counted, under its connection's name: a new plug should show
/// up on the Power page without a trip to its settings first.
/// </summary>
public sealed record PowerSetup(IReadOnlyList<PowerSourceSettings> Sources)
{
    public const string SourcesKey = "power_sources";

    public static PowerSetup Empty { get; } = new([]);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public PowerSourceSettings? Find(string key) =>
        Sources.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.Ordinal));

    public static PowerSetup From(SettingsBag settings)
    {
        var json = settings.Get(SourcesKey);
        if (json.Length == 0)
            return Empty;
        try
        {
            var list = JsonSerializer.Deserialize<List<PowerSourceSettings>>(json, Json) ?? [];
            return new PowerSetup([.. list
                .Where(s => !string.IsNullOrWhiteSpace(s.Key))
                .Select(s => s with
                {
                    Name = s.Name?.Trim() ?? "",
                    Services = [.. (s.Services ?? []).Where(x => !string.IsNullOrWhiteSpace(x.Name))
                        .Select(x => x with { Name = x.Name.Trim(), Weight = double.IsFinite(x.Weight) ? Math.Max(0, x.Weight) : 1 })],
                })]);
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    public Dictionary<string, string> ToSettings() => new()
    {
        [SourcesKey] = JsonSerializer.Serialize(Sources, Json),
    };
}

/// <summary>
/// Energy and cost over one period. <see cref="Coverage"/> is the share of the period that
/// was actually measured (0–1); <see cref="AverageWatts"/> is over the measured part only,
/// so an evening the plug was unreachable does not drag the average down.
/// </summary>
public sealed record PeriodEnergy(double Kwh, double Cost, double CoveredHours, double Hours)
{
    public static PeriodEnergy Zero { get; } = new(0, 0, 0, 0);

    public double? AverageWatts => CoveredHours >= 0.01 ? Kwh * 1000 / CoveredHours : null;

    public double Coverage => Hours > 0 ? Math.Clamp(CoveredHours / Hours, 0, 1) : 0;
}

/// <summary>One local day's energy and cost, for the chart.</summary>
public sealed record DayEnergy(DateOnly Date, double Kwh, double Cost);

/// <summary>Everything the Power page shows about one source.</summary>
/// <param name="FromCounter">True when the device's own meter supplied at least some of it.</param>
public sealed record PowerReport(
    string Key,
    string Name,
    PeriodEnergy Today,
    PeriodEnergy Month,
    PeriodEnergy Last30,
    PeriodEnergy LastWeek,
    double ProjectedMonthCost,
    double ProjectedMonthKwh,
    IReadOnlyList<DayEnergy> Days,
    bool FromCounter);

/// <summary>A service's estimated share of the cost of the plugs it runs from.</summary>
public sealed record ServiceCost(
    string Name, double MonthCost, double MonthKwh, double ProjectedMonthCost, double ProjectedMonthKwh, IReadOnlyList<string> Sources);

/// <summary>
/// Kilowatt-hours per hour, turned into what people ask: what did today cost, what will
/// this month come to, what does Plex cost me. Pure — the clock and the zone are passed in —
/// so month ends, clock changes and time-of-use windows across midnight are pinned by tests.
/// </summary>
public static class PowerCost
{
    /// <summary>How far back the projection looks for "the rate things are running at".</summary>
    public static readonly TimeSpan ProjectionBasis = TimeSpan.FromDays(7);

    /// <summary>Local midnight on a date, with the offset that day actually had (see HistoryStore.StartOfDay).</summary>
    public static DateTimeOffset StartOfDay(DateOnly date, TimeZoneInfo zone)
    {
        var midnight = date.ToDateTime(TimeOnly.MinValue);
        if (zone.IsInvalidTime(midnight))
            return new DateTimeOffset(midnight, zone.GetUtcOffset(midnight.AddHours(-1)));
        var offset = zone.IsAmbiguousTime(midnight)
            ? zone.GetAmbiguousTimeOffsets(midnight).Max()
            : zone.GetUtcOffset(midnight);
        return new DateTimeOffset(midnight, offset);
    }

    public static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    /// <summary>The first instant of the local month <paramref name="now"/> is in.</summary>
    public static DateTimeOffset StartOfMonth(DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = LocalDate(now, zone);
        return StartOfDay(new DateOnly(today.Year, today.Month, 1), zone);
    }

    /// <summary>The first instant of the next local month.</summary>
    public static DateTimeOffset EndOfMonth(DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = LocalDate(now, zone);
        return StartOfDay(new DateOnly(today.Year, today.Month, 1).AddMonths(1), zone);
    }

    /// <summary>
    /// Energy and cost over [<paramref name="from"/>, <paramref name="to"/>). An hour counts
    /// when it starts inside the window, so the hour in progress is included with whatever
    /// it has so far. Each hour is priced at its own rate, which is what makes a time-of-use
    /// tariff come out right.
    /// </summary>
    public static PeriodEnergy Period(
        IReadOnlyDictionary<long, Energy.HourEnergy> hours, PowerTariff tariff, TimeZoneInfo zone,
        DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from)
            return PeriodEnergy.Zero;
        var first = FloorHour(from.ToUnixTimeSeconds());
        var last = to.ToUnixTimeSeconds();
        double kwh = 0, cost = 0, covered = 0;
        foreach (var (hour, energy) in hours)
        {
            if (hour < first || hour >= last)
                continue;
            kwh += energy.Kwh;
            cost += energy.Kwh * tariff.PriceForHour(hour, zone);
            covered += energy.CoveredSeconds;
        }
        return new PeriodEnergy(kwh, cost, covered / 3600, (to - from).TotalHours);
    }

    /// <summary>
    /// Each local day from <paramref name="first"/> to <paramref name="last"/>, oldest first,
    /// with zeros for days nothing was measured. An hour belongs to the day its middle falls
    /// in, which only matters in a zone half an hour off UTC.
    /// </summary>
    public static IReadOnlyList<DayEnergy> Daily(
        IReadOnlyDictionary<long, Energy.HourEnergy> hours, PowerTariff tariff, TimeZoneInfo zone,
        DateOnly first, DateOnly last)
    {
        var days = new SortedDictionary<DateOnly, (double Kwh, double Cost)>();
        for (var day = first; day <= last; day = day.AddDays(1))
            days[day] = (0, 0);
        foreach (var (hour, energy) in hours)
        {
            var date = LocalDate(DateTimeOffset.FromUnixTimeSeconds(hour + 1800), zone);
            if (!days.TryGetValue(date, out var sum))
                continue;
            days[date] = (sum.Kwh + energy.Kwh, sum.Cost + energy.Kwh * tariff.PriceForHour(hour, zone));
        }
        return [.. days.Select(d => new DayEnergy(d.Key, d.Value.Kwh, d.Value.Cost))];
    }

    /// <summary>
    /// What the month will come to: what it has cost so far, plus the rest of the month at
    /// the rate of the last week — cost per measured hour, so a week with a gap in it is not
    /// taken for a week of lower use. The last week rather than the month so far, because on
    /// the 2nd "so far" is one day, and a Sunday is not a month. With no week to go on, the
    /// month so far is the rate; with neither, the month so far is all there is.
    /// </summary>
    public static (double Cost, double Kwh) Project(
        PeriodEnergy monthSoFar, PeriodEnergy recent, DateTimeOffset now, DateTimeOffset monthEnd)
    {
        var remaining = Math.Max(0, (monthEnd - now).TotalHours);
        var basis = recent.CoveredHours >= 1 ? recent : monthSoFar.CoveredHours >= 1 ? monthSoFar : null;
        if (basis is null)
            return (monthSoFar.Cost, monthSoFar.Kwh);
        return (monthSoFar.Cost + basis.Cost / basis.CoveredHours * remaining,
                monthSoFar.Kwh + basis.Kwh / basis.CoveredHours * remaining);
    }

    /// <summary>Everything the page shows about one source, <paramref name="days"/> days of chart included.</summary>
    public static PowerReport Report(
        string key, string name, IReadOnlyDictionary<long, Energy.HourEnergy> hours, bool fromCounter,
        PowerTariff tariff, TimeZoneInfo zone, DateTimeOffset now, int days)
    {
        var today = LocalDate(now, zone);
        var monthStart = StartOfMonth(now, zone);
        var monthEnd = EndOfMonth(now, zone);

        var month = Period(hours, tariff, zone, monthStart, now);
        var week = Period(hours, tariff, zone, now - ProjectionBasis, now);
        var (projectedCost, projectedKwh) = Project(month, week, now, monthEnd);

        return new PowerReport(
            key, name,
            Period(hours, tariff, zone, StartOfDay(today, zone), now),
            month,
            Period(hours, tariff, zone, now.AddDays(-30), now),
            week,
            projectedCost, projectedKwh,
            Daily(hours, tariff, zone, today.AddDays(-(days - 1)), today),
            fromCounter);
    }

    /// <summary>
    /// Adds several sources together, for the "all of it" row. Coverage is kept from the
    /// best-covered source, since the sum of several plugs' hours is not an amount of time;
    /// the average watts of a total are the sum of each one's average, not an average of
    /// averages.
    /// </summary>
    public static PeriodEnergy Sum(IEnumerable<PeriodEnergy> periods)
    {
        var list = periods.ToList();
        if (list.Count == 0)
            return PeriodEnergy.Zero;
        var watts = list.Sum(p => p.AverageWatts ?? 0);
        var hours = list.Max(p => p.Hours);
        // Chosen so AverageWatts reads back as the sum of the parts.
        var kwh = list.Sum(p => p.Kwh);
        var covered = watts > 0 ? kwh * 1000 / watts : 0;
        return new PeriodEnergy(kwh, list.Sum(p => p.Cost), covered, hours);
    }

    /// <summary>
    /// Each service's share of the plugs it is on. Evenly, or by weight when a source says
    /// so (a weight of zero takes nothing; all zeros falls back to even). A service on two
    /// plugs gets a share of each, added up under one name.
    ///
    /// An estimate and presented as one: a plug knows what it delivered, not which process
    /// on the machine behind it asked for it.
    /// </summary>
    public static IReadOnlyList<ServiceCost> Attribute(IReadOnlyList<PowerReport> reports, PowerSetup setup)
    {
        var byName = new Dictionary<string, Tally>(StringComparer.OrdinalIgnoreCase);

        foreach (var report in reports)
        {
            if (setup.Find(report.Key) is not { Services.Count: > 0 } source)
                continue;

            var weights = source.Services.Select(s => source.Weighted ? Math.Max(0, s.Weight) : 1).ToList();
            var total = weights.Sum();
            if (total <= 0)
            {
                weights = [.. source.Services.Select(_ => 1d)];
                total = weights.Count;
            }

            for (var i = 0; i < source.Services.Count; i++)
            {
                var share = weights[i] / total;
                var name = source.Services[i].Name;
                if (!byName.TryGetValue(name, out var sum))
                    byName[name] = sum = new Tally(name);
                sum.Sources.Add(report.Name);
                sum.Cost += report.Month.Cost * share;
                sum.Kwh += report.Month.Kwh * share;
                sum.Projected += report.ProjectedMonthCost * share;
                sum.ProjectedKwh += report.ProjectedMonthKwh * share;
            }
        }

        return [.. byName.Values
            .Select(s => new ServiceCost(s.Name, s.Cost, s.Kwh, s.Projected, s.ProjectedKwh, [.. s.Sources.Distinct()]))
            .OrderByDescending(s => s.MonthCost)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private sealed class Tally(string name)
    {
        public string Name { get; } = name;
        public double Cost { get; set; }
        public double Kwh { get; set; }
        public double Projected { get; set; }
        public double ProjectedKwh { get; set; }
        public List<string> Sources { get; } = [];
    }

    private static long FloorHour(long at) => at - ((at % 3600) + 3600) % 3600;
}
