namespace LabbyTwo.Core;

/// <summary>
/// Turns recorded power readings into energy: how many kilowatt-hours went through a plug
/// in each hour. Everything the Power page, the <c>{{power}}</c> shortcode and the weekly
/// summary say about cost starts here, so the rules are in one pure place and pinned by
/// tests rather than spread across three callers that could each integrate differently.
///
/// Two kinds of input, because devices report two kinds of thing:
/// <list type="bullet">
/// <item><b>Power</b> (watts) — a reading of what it draws right now. Energy is the area
/// under that line, worked out with the trapezoid rule between neighbouring readings. It is
/// only as good as the readings are frequent, and it knows nothing about the time between
/// them — so a gap longer than <see cref="MaxGap"/> counts as no energy at all rather than
/// a straight line drawn across it. A plug that was unreachable for six hours may have been
/// off, or drawing double; either way, a number made up for those hours would be a guess
/// dressed as a measurement. The page reports how much of each period was covered instead,
/// so a month with a gap in it says so.</item>
/// <item><b>An energy counter</b> (kWh total) — the device's own running total, which
/// Shelly plugs and most energy monitors keep. Better than integrating watts whenever it is
/// there: it counts every second, including the ones LabbyTwo never saw, so a gap in the
/// readings loses nothing — the difference across it is exactly what was used. Counters do
/// go back to zero (a firmware update, a power cut on some models, a factory reset), so a
/// reading lower than the one before is a <b>reset</b>, not negative energy: what was used
/// since the reset is the new reading itself (see <see cref="FromCounter"/>).</item>
/// </list>
///
/// Old history is kept as hourly summaries (average, extremes, count, and the hour's last
/// reading) rather than every reading, so both kinds also work from those: an hour's
/// average watts times the part of the hour its readings covered, and a counter's last
/// reading of each hour as a point on the running total.
///
/// Times are Unix seconds throughout, and hours are UTC hour starts — the same keys
/// <c>samples_hourly</c> uses — so a local day, a time-of-use window or a month is decided
/// later, by whoever knows the zone.
/// </summary>
public static class Energy
{
    /// <summary>One recorded value: watts for a power reading, kWh for a counter.</summary>
    public readonly record struct Reading(long At, double Value);

    /// <summary>
    /// One hour of a series as <c>samples_hourly</c> keeps it. <see cref="LastAt"/> and
    /// <see cref="LastValue"/> are the newest raw reading in the hour.
    /// </summary>
    public readonly record struct HourSummary(
        long Hour, double Avg, double Min, double Max, long Count, long LastAt, double LastValue);

    /// <summary>
    /// The energy in one UTC hour, and how many of its seconds were actually measured — so a
    /// total can say "covering 92% of the month" rather than quietly being 8% low.
    /// </summary>
    public readonly record struct HourEnergy(double Kwh, double CoveredSeconds)
    {
        public HourEnergy Plus(HourEnergy other) =>
            new(Kwh + other.Kwh, Math.Min(HourSeconds, CoveredSeconds + other.CoveredSeconds));
    }

    public const long HourSeconds = 3600;

    /// <summary>
    /// The shortest gap between two power readings that is treated as "not measured" rather
    /// than bridged. Five minutes: long enough that a probe or two going missing on a busy
    /// sweep does not punch holes in the total, short enough that nothing an outage hides is
    /// invented.
    /// </summary>
    public static readonly TimeSpan MinimumGap = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The longest gap bridged with a straight line: three probe intervals, and never less
    /// than <see cref="MinimumGap"/>. Three, so a single missed probe on a slow connection is
    /// still bridged; anything longer is an outage, and contributes nothing.
    /// </summary>
    public static TimeSpan MaxGap(TimeSpan interval) =>
        TimeSpan.FromTicks(Math.Max(MinimumGap.Ticks, interval.Ticks * 3));

    /// <summary>
    /// How often a series is actually read, from its own recent readings: the median gap
    /// between neighbours. Used to tell how much of an hourly summary its readings covered.
    /// The configured probe interval is only the fallback, because a sweep that takes longer
    /// than the interval reads every connection less often than configured, and assuming the
    /// configured rate would make every hour look partly empty.
    /// </summary>
    public static TimeSpan EstimateInterval(IReadOnlyList<Reading> raw, TimeSpan fallback)
    {
        var gaps = new List<long>(Math.Max(0, raw.Count - 1));
        for (var i = 1; i < raw.Count; i++)
        {
            var gap = raw[i].At - raw[i - 1].At;
            if (gap > 0)
                gaps.Add(gap);
        }
        if (gaps.Count < 3)
            return fallback;
        gaps.Sort();
        return TimeSpan.FromSeconds(gaps[gaps.Count / 2]);
    }

    /// <summary>
    /// Energy per hour from power readings in watts.
    ///
    /// Hourly summaries count their average for the part of the hour their readings stand
    /// for — the number of readings times the probe interval, at most the whole hour — so an
    /// hour in which the plug answered for twenty minutes counts twenty minutes.
    ///
    /// Raw readings are joined pairwise by the trapezoid rule, split exactly at hour
    /// boundaries. A pair further apart than <see cref="MaxGap"/> is not joined: the time
    /// between them is left out, not filled in. A negative reading (a meter that also reports
    /// export) counts as zero — this is what the lab uses, not what it makes.
    /// </summary>
    /// <param name="interval">How often the series is read; see <see cref="EstimateInterval"/>.</param>
    public static SortedDictionary<long, HourEnergy> FromPower(
        IReadOnlyList<HourSummary> summaries, IReadOnlyList<Reading> raw, TimeSpan interval)
    {
        var hours = new SortedDictionary<long, HourEnergy>();
        var step = Math.Max(1, interval.TotalSeconds);

        foreach (var summary in summaries)
        {
            var covered = Math.Min(HourSeconds, summary.Count * step);
            var kwh = Math.Max(0, summary.Avg) * covered / HourSeconds / 1000;
            Add(hours, summary.Hour, new HourEnergy(kwh, covered));
        }

        var maxGap = (long)MaxGap(interval).TotalSeconds;
        for (var i = 1; i < raw.Count; i++)
        {
            var (a, b) = (raw[i - 1], raw[i]);
            var gap = b.At - a.At;
            if (gap <= 0 || gap > maxGap)
                continue;
            AddTrapezoid(hours, a.At, Math.Max(0, a.Value), b.At, Math.Max(0, b.Value));
        }

        return hours;
    }

    /// <summary>
    /// Energy per hour from a running total in kWh — the device's own meter.
    ///
    /// Each rise between neighbouring points is energy used, spread evenly over the time
    /// between them (which is what makes a counter better than watts: a six-hour gap in the
    /// readings still has its six hours of energy, because the device kept counting). An
    /// hourly summary contributes its last reading as a point.
    ///
    /// A fall is a reset. The energy since then is the new reading itself, since the
    /// counter started again from zero; and when the fall is inside a summarised hour, the
    /// hour's highest reading says how far the counter had climbed before it reset, so that
    /// part is counted too. A fall smaller than <see cref="ResetTolerance"/> is not a reset
    /// but rounding — some integrations report a total that wobbles in its last digit — and
    /// counts as nothing.
    /// </summary>
    /// <param name="scale">Multiplies each reading into kWh: 1 for a kWh counter, 0.001 for Wh.</param>
    public static SortedDictionary<long, HourEnergy> FromCounter(
        IReadOnlyList<HourSummary> summaries, IReadOnlyList<Reading> raw, double scale = 1)
    {
        var points = new List<(long At, double Value, double Max)>(summaries.Count + raw.Count);
        foreach (var summary in summaries)
            points.Add((summary.LastAt, summary.LastValue * scale, summary.Max * scale));
        foreach (var reading in raw)
            points.Add((reading.At, reading.Value * scale, reading.Value * scale));
        points.Sort((x, y) => x.At.CompareTo(y.At));

        var hours = new SortedDictionary<long, HourEnergy>();
        for (var i = 1; i < points.Count; i++)
        {
            var (p, q) = (points[i - 1], points[i]);
            var kwh = CounterDelta(p.Value, q.Value, q.Max);
            AddUniform(hours, p.At, q.At, kwh);
        }
        return hours;
    }

    /// <summary>
    /// A fall smaller than this is rounding, not a reset: 0.5% of the previous reading, and
    /// never less than a watt-hour.
    /// </summary>
    public static double ResetTolerance(double previous) => Math.Max(0.001, Math.Abs(previous) * 0.005);

    /// <summary>
    /// The energy between two readings of a counter. <paramref name="peak"/> is the highest
    /// the counter read in the interval ending at <paramref name="current"/> — the same as
    /// <paramref name="current"/> for a single reading, the hour's maximum for a summary.
    /// </summary>
    public static double CounterDelta(double previous, double current, double peak)
    {
        var delta = current - previous;
        if (delta >= 0)
            return delta;
        if (-delta <= ResetTolerance(previous))
            return 0;
        return Math.Max(0, peak - previous) + Math.Max(0, current);
    }

    /// <summary>
    /// The counter's hours where it has any, and the power readings' hours where it does
    /// not — so a plug whose counter only arrived with a firmware update still has its
    /// earlier months, measured the less exact way.
    /// </summary>
    public static SortedDictionary<long, HourEnergy> Prefer(
        SortedDictionary<long, HourEnergy> primary, SortedDictionary<long, HourEnergy> fallback)
    {
        var merged = new SortedDictionary<long, HourEnergy>(fallback);
        foreach (var (hour, energy) in primary)
        {
            if (energy.CoveredSeconds > 0)
                merged[hour] = energy;
        }
        return merged;
    }

    private static long FloorHour(long at) => at - ((at % HourSeconds) + HourSeconds) % HourSeconds;

    private static void Add(SortedDictionary<long, HourEnergy> hours, long hour, HourEnergy energy) =>
        hours[hour] = hours.TryGetValue(hour, out var existing) ? existing.Plus(energy) : energy;

    /// <summary>The area under a straight line from (t1, w1) to (t2, w2), cut at each hour.</summary>
    private static void AddTrapezoid(SortedDictionary<long, HourEnergy> hours, long t1, double w1, long t2, double w2)
    {
        double WattsAt(long t) => w1 + (w2 - w1) * (t - t1) / (double)(t2 - t1);

        var start = t1;
        while (start < t2)
        {
            var end = Math.Min(t2, FloorHour(start) + HourSeconds);
            var seconds = end - start;
            var wattHours = (WattsAt(start) + WattsAt(end)) / 2 * seconds / HourSeconds;
            Add(hours, FloorHour(start), new HourEnergy(wattHours / 1000, seconds));
            start = end;
        }
    }

    /// <summary><paramref name="kwh"/> spread evenly over [t1, t2], cut at each hour.</summary>
    private static void AddUniform(SortedDictionary<long, HourEnergy> hours, long t1, long t2, double kwh)
    {
        if (t2 <= t1)
        {
            if (kwh > 0)
                Add(hours, FloorHour(t2), new HourEnergy(kwh, 0));
            return;
        }

        var span = (double)(t2 - t1);
        var start = t1;
        while (start < t2)
        {
            var end = Math.Min(t2, FloorHour(start) + HourSeconds);
            Add(hours, FloorHour(start), new HourEnergy(kwh * (end - start) / span, end - start));
            start = end;
        }
    }
}
