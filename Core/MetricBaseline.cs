namespace LabbyTwo.Core;

/// <summary>What kind of test a rule applies to its metric.</summary>
public enum RuleKind
{
    /// <summary>A fixed line: "disk used above 90%". What every rule was before this existed.</summary>
    Threshold,

    /// <summary>
    /// Unusual for the time: "download is less than half what it usually is at this hour".
    /// For the numbers where no single line is right — a connection that is quick at 4am and
    /// slow at 8pm, a NAS that is warm in the afternoon — so a fixed threshold is either
    /// silent through a real fault or noisy every evening.
    /// </summary>
    Unusual,
}

/// <summary>How an <see cref="RuleKind.Unusual"/> rule measures "how far from usual".</summary>
public enum UnusualBy
{
    /// <summary>
    /// As a share of the usual value: "less than half", "more than double". The one people
    /// reach for, and the right one for things measured from zero — speeds, counts, watts.
    /// </summary>
    Percent,

    /// <summary>
    /// In multiples of how much the metric normally varies at that hour (a robust z-score).
    /// For things whose zero means nothing — a temperature, which is not "twice as hot" at
    /// 90°C as at 45°C — and for anything steady, where 10% off is already alarming.
    /// </summary>
    Spread,
}

/// <summary>Which slice of history a <see cref="Usual"/> came from, so a message can say so.</summary>
public enum UsualScope
{
    Weekdays,
    Weekends,
    ThisHour,
    AnyTime,
}

/// <summary>
/// What a metric usually reads at some moment: a robust centre and spread, and how many
/// days of history they rest on.
/// </summary>
/// <param name="Median">The middle of the readings — a bad evening among twenty does not move it.</param>
/// <param name="Spread">
/// How far readings normally stray from it, on the scale of a standard deviation, so
/// "three spreads out" means what "three sigma" means to anyone who has met one.
/// </param>
public sealed record Usual(double Median, double Spread, int Days, UsualScope Scope)
{
    /// <summary>
    /// Below this a usual value counts as zero, and "half of it" means nothing. A percentage
    /// rule on a metric that is usually zero is not judged at all rather than judged wrongly.
    /// </summary>
    public const double Negligible = 1e-9;

    /// <summary>The reading as a percentage of usual, or NaN when usual is zero or less.</summary>
    public double PercentOf(double value) => Median > Negligible ? value / Median * 100 : double.NaN;

    /// <summary>
    /// How many spreads the reading is from usual. A history that never varied at all has no
    /// spread, and then any change at all is infinitely unusual — which is the truth.
    /// </summary>
    public double SpreadsFrom(double value)
    {
        var distance = value - Median;
        if (Spread > 0)
            return distance / Spread;
        return distance == 0 ? 0 : distance > 0 ? double.PositiveInfinity : double.NegativeInfinity;
    }

    /// <summary>The end of "usually about 520 Mbps …" — empty when the whole day was used.</summary>
    public string When => Scope switch
    {
        UsualScope.Weekdays => "at this hour on a weekday",
        UsualScope.Weekends => "at this hour at the weekend",
        UsualScope.ThisHour => "at this hour",
        _ => "",
    };
}

/// <summary>
/// What is usual for one metric on one connection, hour by hour, worked out from its recent
/// history. Built by <c>MetricBaselines</c> in the background and looked up by the rule
/// engine, which only ever asks "what is usual right now".
///
/// The slots are hour of day, split into weekdays and weekends. Hour of week — 168 slots —
/// would say more about a Friday night, but with one reading per slot per week it takes
/// five weeks before a single slot means anything, and a month's history holds four. Hour
/// of day alone, 24 slots, is ready in days but calls every Saturday-morning stream
/// unusual. The split is the difference most home labs actually have — people are in on
/// weekends — at a price of 48 slots.
///
/// A slot is used only when it has readings from <see cref="MinDays"/> separate days,
/// since the median of three evenings is one bad evening away from nonsense. Until the
/// weekend slot has that many — three weekends — the plain hour-of-day slot stands in for
/// it, and until that has enough, the whole day does (see <see cref="At"/>).
/// </summary>
public sealed class MetricBaseline
{
    /// <summary>
    /// Distinct days of history a slot needs before it is trusted. Five: a median of five
    /// survives two odd days, and a working week fills a weekday slot.
    /// </summary>
    public const int MinDays = 5;

    /// <summary>
    /// How far back to look. Four weeks is enough for every slot to fill, and short enough
    /// that a faster broadband plan becomes "usual" within a fortnight rather than being an
    /// unusually good connection for months.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(28);

    /// <summary>
    /// Median absolute deviation to standard deviation, for normally distributed data. Makes
    /// <see cref="Usual.Spread"/> comparable with the sigma people already have a feel for.
    /// </summary>
    private const double MadScale = 1.4826;

    /// <summary>The same for the mean absolute deviation, used only where the MAD is zero.</summary>
    private const double MeanDeviationScale = 1.2533;

    /// <summary>
    /// The least spread, as a share of usual. A reading within a few percent of what it
    /// usually is is never unusual however steady the history was: a UPS load that has read
    /// 212 W every hour for a month has a spread of nothing, and 214 W is not news.
    /// </summary>
    private const double RelativeFloor = 0.05;

    private const int Weekday = 0, Weekend = 1, AnyDay = 2;

    private readonly Dictionary<(int Days, int Hour), Usual> _slots;
    private readonly Usual? _anyTime;

    private MetricBaseline(Dictionary<(int, int), Usual> slots, Usual? anyTime, int days)
    {
        _slots = slots;
        _anyTime = anyTime;
        Days = days;
    }

    /// <summary>A baseline with no history behind it.</summary>
    public static MetricBaseline Empty { get; } = new([], null, 0);

    /// <summary>Distinct days the history covers at all.</summary>
    public int Days { get; }

    /// <summary>Still learning: nothing about this metric can be called unusual yet.</summary>
    public bool IsLearning => Days < MinDays;

    /// <summary>How many more days of history before it can judge, for "learning — needs 3 more days".</summary>
    public int DaysNeeded => Math.Max(0, MinDays - Days);

    /// <summary>
    /// Builds the table from hourly averages. Each point is one hour of one day, stamped
    /// anywhere inside that hour — which is how the history store hands them out.
    /// </summary>
    public static MetricBaseline Compute(IEnumerable<(DateTimeOffset At, double Value)> hourly, TimeZoneInfo zone)
    {
        var buckets = new Dictionary<(int, int), List<(DateOnly Day, double Value)>>();
        var all = new List<(DateOnly Day, double Value)>();

        foreach (var (at, value) in hourly)
        {
            if (!double.IsFinite(value))
                continue;

            var local = TimeZoneInfo.ConvertTime(at, zone);
            var day = DateOnly.FromDateTime(local.DateTime);
            var kind = IsWeekend(local) ? Weekend : Weekday;

            Add((kind, local.Hour), day, value);
            Add((AnyDay, local.Hour), day, value);
            all.Add((day, value));
        }

        var days = all.Select(point => point.Day).Distinct().Count();
        if (all.Count == 0)
            return Empty;

        // The whole day's spread is what a slot falls back on when it never varied at all —
        // zero streams at 3am every night for a month says nothing about how big a jump from
        // zero is a big one, but how much it varies across the day does.
        var anyTime = Summarise(all, UsualScope.AnyTime, fallbackSpread: 0);

        var slots = new Dictionary<(int, int), Usual>();
        foreach (var (key, points) in buckets)
        {
            var slotDays = points.Select(point => point.Day).Distinct().Count();
            if (slotDays < MinDays)
                continue;

            var scope = key.Item1 switch
            {
                Weekday => UsualScope.Weekdays,
                Weekend => UsualScope.Weekends,
                _ => UsualScope.ThisHour,
            };
            slots[key] = Summarise(points, scope, anyTime.Spread);
        }

        return new MetricBaseline(slots, days >= MinDays ? anyTime : null, days);

        void Add((int, int) key, DateOnly day, double value)
        {
            if (!buckets.TryGetValue(key, out var list))
                buckets[key] = list = [];
            list.Add((day, value));
        }
    }

    /// <summary>
    /// What is usual at <paramref name="when"/>, or null while still learning. The most
    /// specific slot with enough history wins: this hour on this kind of day, then this hour
    /// on any day, then the whole day. The last is for a metric reported only some hours — a
    /// speed test every six hours leaves most hour slots empty for ever, and "usually about
    /// 520" is still worth saying when "at this hour" cannot be.
    /// </summary>
    public Usual? At(DateTimeOffset when, TimeZoneInfo zone)
    {
        if (IsLearning)
            return null;

        var local = TimeZoneInfo.ConvertTime(when, zone);
        if (_slots.TryGetValue((IsWeekend(local) ? Weekend : Weekday, local.Hour), out var specific))
            return specific;
        if (_slots.TryGetValue((AnyDay, local.Hour), out var hourly))
            return hourly;
        return _anyTime;
    }

    private static bool IsWeekend(DateTimeOffset local) =>
        local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    private static Usual Summarise(List<(DateOnly Day, double Value)> points, UsualScope scope, double fallbackSpread)
    {
        var values = points.Select(point => point.Value).ToList();
        var median = Median(values);
        var spread = RobustSpread(values, median);
        if (spread == 0)
            spread = fallbackSpread;
        spread = Math.Max(spread, Math.Abs(median) * RelativeFloor);
        return new Usual(median, spread, points.Select(point => point.Day).Distinct().Count(), scope);
    }

    /// <summary>The middle value; the mean of the middle two for an even count. Zero for none.</summary>
    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
            return 0;
        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    /// <summary>
    /// The scaled median absolute deviation: robust, so one evening the internet was down
    /// does not widen "normal" enough to hide the next one. When more than half the values
    /// are identical the MAD is zero however much the rest vary, and the scaled mean absolute
    /// deviation stands in — less robust, but not blind to the variation that is there.
    /// </summary>
    public static double RobustSpread(IReadOnlyList<double> values, double median)
    {
        if (values.Count == 0)
            return 0;
        var deviations = values.Select(value => Math.Abs(value - median)).ToList();
        var mad = Median(deviations) * MadScale;
        return mad > 0 ? mad : deviations.Average() * MeanDeviationScale;
    }
}

/// <summary>The outcome of one reading against one rule.</summary>
public enum Verdict
{
    Breaching,
    Cleared,

    /// <summary>Inside the hysteresis band: keep doing whatever it was doing.</summary>
    Holding,
}
