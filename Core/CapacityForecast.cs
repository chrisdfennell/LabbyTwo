namespace LabbyTwo.Core;

public enum ForecastState
{
    /// <summary>Too little history since the metric was last cleared out to say anything.</summary>
    NotEnoughHistory,

    /// <summary>Flat, falling, or rising too slowly to reach the limit in any horizon worth naming.</summary>
    NotFilling,

    /// <summary>Heading for the limit, with an estimate of when.</summary>
    Filling,

    /// <summary>Already at the limit.</summary>
    Full,
}

public enum ForecastConfidence
{
    Low,
    Medium,
    High,
}

/// <summary>
/// "Volume 1 is full in about seven weeks at the current rate" — a straight line fitted to a
/// capacity metric's recent history and followed to its limit.
///
/// A pure function of the samples, the limit and the clock, so every awkward shape a real
/// disk produces (a flat week, a big delete, one odd reading, a month of noise) can be
/// pinned by a test rather than discovered on somebody's NAS.
///
/// The method, and why each part is there:
/// <list type="bullet">
/// <item>Samples are first reduced to hourly medians. A probe every thirty seconds is forty
/// thousand points a fortnight, which the fit does not need, and a median per hour means
/// one odd reading cannot drag a bucket.</item>
/// <item>The fit starts after the last sudden, lasting drop. Deleting a few hundred
/// gigabytes is not the disk "filling at a negative rate": the rate that matters is the
/// one since then, and fitting across the drop would say the volume is emptying.</item>
/// <item>The slope is Theil–Sen — the median of the slopes between every pair of points —
/// rather than least squares, so a burst of writes or a stray reading moves the estimate
/// a little instead of pulling the whole line towards it.</item>
/// <item>A rise that is no bigger than the noise around the line is called flat. A trend
/// the data cannot tell apart from wobble is not a forecast, and projecting it to the
/// limit would print a date that means nothing.</item>
/// </list>
/// The answer is phrased coarsely on purpose — "about 6 weeks", never "43.2 days" — because
/// a fortnight's history cannot support more precision than that, and a number with a
/// decimal point in it reads as though it could.
/// </summary>
public sealed record CapacityForecast(
    ForecastState State,
    double Current,
    double? DaysLeft,
    double? RatePerDay,
    ForecastConfidence Confidence,
    double? RSquared,
    TimeSpan History,
    DateTimeOffset? FittedFrom)
{
    /// <summary>How far back the forecast looks. Long enough to see through a week's rhythm.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(14);

    /// <summary>Less history than this since the last clear-out and there is no forecast.</summary>
    public static readonly TimeSpan MinimumSpan = TimeSpan.FromDays(2);

    /// <summary>Distinct hours of readings needed, so two days with three samples do not count as two days.</summary>
    public const int MinimumPoints = 12;

    /// <summary>
    /// Further out than this is "not filling". Five years at the current rate is as good as
    /// never for a home lab — the disks will be replaced first — and a date that far out
    /// from two weeks of data is extrapolation, not a forecast.
    /// </summary>
    public const double HorizonDays = 5 * 365;

    /// <summary>
    /// A drop at least this fraction of the scale (the limit, or the current value when the
    /// limit is zero) counts as a clear-out. Two points of a percentage is a real delete on
    /// any volume; smaller dips are left to the robust fit to absorb.
    /// </summary>
    private const double DropFraction = 0.02;

    /// <summary>The value an alert rule sees: days left, zero when full, infinity when not filling, nothing when unknown.</summary>
    public double? AlertValue => State switch
    {
        ForecastState.Full => 0,
        ForecastState.Filling => DaysLeft,
        ForecastState.NotFilling => double.PositiveInfinity,
        _ => null,
    };

    /// <summary>Soonest first, then the ones that are fine, then the ones nobody can say yet.</summary>
    public double SortKey => State switch
    {
        ForecastState.Full => -1,
        ForecastState.Filling => DaysLeft ?? HorizonDays,
        ForecastState.NotFilling => HorizonDays + 1,
        _ => HorizonDays + 2,
    };

    /// <summary>"full in about 7 weeks", "not filling", "full now".</summary>
    public string Describe() => State switch
    {
        ForecastState.Full => "full now",
        ForecastState.Filling => "full " + Humanize(DaysLeft ?? 0),
        ForecastState.NotFilling => "not filling",
        _ => "not enough history yet",
    };

    /// <summary>A sentence on how far to trust it, for a tooltip.</summary>
    public string Explain()
    {
        var days = History.TotalDays;
        var span = days >= 1.5 ? $"{days:0} days" : $"{History.TotalHours:0} hours";
        return State switch
        {
            ForecastState.Full => "At its limit now.",
            ForecastState.NotEnoughHistory when FittedFrom is not null =>
                $"Cleared out recently, so there are only {span} of readings since. A forecast needs at least {MinimumSpan.TotalDays:0} days.",
            ForecastState.NotEnoughHistory =>
                $"Only {span} of readings so far. A forecast needs at least {MinimumSpan.TotalDays:0} days.",
            ForecastState.NotFilling => $"No steady rise over the last {span}.",
            _ => Confidence switch
            {
                ForecastConfidence.High => $"A steady trend over {span}.",
                ForecastConfidence.Medium => $"Based on {span} of readings; the rate has varied, so treat it as approximate.",
                _ => $"A rough guess: {span} of uneven readings, projected a long way ahead.",
            },
        };
    }

    /// <summary>
    /// A duration in words, rounded to the unit a person would use for it. "about 6 weeks"
    /// rather than "43.2 days" — see the class remarks.
    /// </summary>
    public static string Humanize(double days)
    {
        if (days < 1)
            return "within a day";
        if (days < 14)
        {
            var whole = Math.Max(1, (int)Math.Round(days));
            return whole == 1 ? "in about a day" : $"in about {whole} days";
        }
        if (days < 56)
            return $"in about {(int)Math.Round(days / 7)} weeks";
        if (days < 548)
            return $"in about {(int)Math.Round(days / 30.44)} months";

        var years = (int)Math.Round(days / 365.25);
        return years <= 1 ? "in about a year" : $"in about {years} years";
    }

    /// <summary>
    /// Forecasts one metric. <paramref name="samples"/> need not be sorted or evenly spaced.
    /// </summary>
    public static CapacityForecast Compute(
        IEnumerable<(DateTimeOffset At, double Value)> samples, CapacityLimit limit, DateTimeOffset now)
    {
        var ordered = samples.Where(s => double.IsFinite(s.Value)).OrderBy(s => s.At).ToList();
        if (ordered.Count == 0)
            return new(ForecastState.NotEnoughHistory, double.NaN, null, null, ForecastConfidence.Low, null, TimeSpan.Zero, null);

        var current = ordered[^1].Value;
        var history = ordered[^1].At - ordered[0].At;

        // Full is a fact about the latest reading and needs no history at all.
        if (limit.IsFull(current))
            return new(ForecastState.Full, current, 0, null, ForecastConfidence.High, null, history, null);

        // Everything below works as "rising towards full"; a metric that falls to its limit
        // is mirrored so there is one algorithm to get right rather than two.
        var sign = limit.Rising ? 1.0 : -1.0;
        var full = limit.Full * sign;
        var origin = ordered[0].At;

        var buckets = ordered
            .GroupBy(s => (long)Math.Floor((s.At - origin).TotalHours))
            .OrderBy(g => g.Key)
            .Select(g => new Point(
                g.Average(s => (s.At - origin).TotalDays),
                Median([.. g.Select(s => s.Value * sign)])))
            .ToList();

        var scale = Math.Max(Math.Abs(limit.Full), Math.Abs(current));
        var start = LastClearOut(buckets, DropFraction * scale);
        var fitted = buckets.Skip(start).ToList();
        DateTimeOffset? fittedFrom = start > 0 ? origin.AddDays(fitted[0].X) : null;

        var span = TimeSpan.FromDays(fitted[^1].X - fitted[0].X);
        if (fitted.Count < MinimumPoints || span < MinimumSpan)
            return new(ForecastState.NotEnoughHistory, current, null, null, ForecastConfidence.Low, null, span, fittedFrom);

        var slope = TheilSen(fitted);
        var intercept = Median([.. fitted.Select(p => p.Y - slope * p.X)]);

        var residuals = fitted.Select(p => p.Y - (intercept + slope * p.X)).ToList();
        var mean = fitted.Average(p => p.Y);
        var total = fitted.Sum(p => (p.Y - mean) * (p.Y - mean));
        var unexplained = residuals.Sum(r => r * r);
        double? rSquared = total > 0 ? Math.Clamp(1 - unexplained / total, 0, 1) : null;

        // The typical distance from the line, robustly: 1.4826 × the median absolute
        // residual estimates a standard deviation without one wild reading setting it.
        var noise = 1.4826 * Median([.. residuals.Select(Math.Abs)]);
        var rise = slope * span.TotalDays;

        if (slope <= 0 || rise <= 2 * noise)
            return new(ForecastState.NotFilling, current, null, slope * sign, ForecastConfidence.Medium, rSquared, span, fittedFrom);

        // From the latest hour's level rather than the fitted line's end: the line is the
        // rate, but where the metric is now is what was measured, not what was modelled.
        var level = fitted[^1].Y;
        var sinceLast = (now - origin).TotalDays - fitted[^1].X;
        var days = Math.Max(0, (full - level) / slope - Math.Max(0, sinceLast));

        if (days > HorizonDays)
            return new(ForecastState.NotFilling, current, null, slope * sign, ForecastConfidence.Medium, rSquared, span, fittedFrom);

        // How far past the data the line is being followed matters as much as how well it
        // fits: a perfect fortnight says little about next year.
        var reach = days / Math.Max(span.TotalDays, 1e-9);
        var fit = rSquared ?? 0;
        var confidence = fit >= 0.9 && reach <= 4 ? ForecastConfidence.High
            : fit >= 0.6 && reach <= 12 ? ForecastConfidence.Medium
            : ForecastConfidence.Low;

        return new(ForecastState.Filling, current, days, slope * sign, confidence, rSquared, span, fittedFrom);
    }

    private readonly record struct Point(double X, double Y);

    /// <summary>
    /// The index the fit should start from: just after the last drop that is both sudden and
    /// lasting, or zero if there was none. "Lasting" is judged with the median of five
    /// hours either side, so an hour or two of low readings — a probe that caught the NAS
    /// mid-scrub — is not taken for a clear-out, and a brief high before a normal hour is
    /// not taken for a drop from it. A drop in the last couple of hours has only those hours
    /// after it, and counts at once: that is the delete somebody has just done.
    /// </summary>
    private const int Neighbourhood = 5;

    private static int LastClearOut(List<Point> points, double threshold)
    {
        if (threshold <= 0 || points.Count < 2)
            return 0;

        // Differences between neighbouring hours, so a drop has to stand out from how
        // much this particular metric normally moves, not just from a fixed size.
        var steps = new List<double>(points.Count - 1);
        for (var i = 1; i < points.Count; i++)
            steps.Add(Math.Abs(points[i].Y - points[i - 1].Y));
        var needed = Math.Max(threshold, 10 * Median(steps));

        bool DropsAt(int i)
        {
            var from = Math.Max(0, i - Neighbourhood);
            var before = Median([.. points.Skip(from).Take(i - from).Select(p => p.Y)]);
            var after = Median([.. points.Skip(i).Take(Neighbourhood).Select(p => p.Y)]);
            return before - after >= needed;
        }

        for (var i = points.Count - 1; i >= 1; i--)
        {
            if (!DropsAt(i))
                continue;

            // The windows overlap, so one drop satisfies the test at a run of neighbouring
            // indices. Walk back to the start of the run and take the steepest single step
            // in it, which is where the drop actually happened.
            var first = i;
            while (first > 1 && DropsAt(first - 1))
                first--;

            var best = first;
            for (var j = first; j <= i; j++)
            {
                if (points[j - 1].Y - points[j].Y > points[best - 1].Y - points[best].Y)
                    best = j;
            }
            return best;
        }

        return 0;
    }

    /// <summary>The median of the slopes between every pair of points with distinct times.</summary>
    private static double TheilSen(List<Point> points)
    {
        var slopes = new List<double>(points.Count * (points.Count - 1) / 2);
        for (var i = 0; i < points.Count; i++)
        {
            for (var j = i + 1; j < points.Count; j++)
            {
                var dx = points[j].X - points[i].X;
                if (dx > 1e-9)
                    slopes.Add((points[j].Y - points[i].Y) / dx);
            }
        }
        return slopes.Count == 0 ? 0 : Median(slopes);
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
            return 0;
        values.Sort();
        var middle = values.Count / 2;
        return values.Count % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2;
    }
}

/// <summary>
/// The derived metric a forecast is published as, so an alert rule can say "days until
/// full below 30" with the same machinery as "disk used above 90". It is never recorded:
/// the samples table holds measurements, and a forecast is an opinion about them.
/// </summary>
public static class CapacityMetric
{
    /// <summary>The prefix on a derived key: <c>days_until_full:disk_percent</c>.</summary>
    public const string Prefix = "days_until_full:";

    public static string KeyFor(string metric) => Prefix + metric;

    /// <summary>Whether a metric key is a forecast, and of which metric.</summary>
    public static bool TryParse(string? key, out string metric)
    {
        if (key is not null && key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && key.Length > Prefix.Length)
        {
            metric = key[Prefix.Length..];
            return true;
        }
        metric = "";
        return false;
    }

    /// <summary>How the derived metric is labelled and formatted, from the metric it forecasts.</summary>
    public static MetricSpec SpecFor(MetricSpec measured) =>
        new(KeyFor(measured.Key), $"{measured.Label}: days until full", " days", 0);

    /// <summary>
    /// The suggested rule a provider offers for a capacity metric: warn a month ahead.
    /// Held for an hour and cleared at 45 days, because a forecast moves with every burst of
    /// writes and one that crosses the line and back all afternoon is not news.
    /// </summary>
    public static SuggestedRule FullWithin(string name, string metric, int days = 30) =>
        new(name, KeyFor(metric), Comparison.Below, days, ClearThreshold: days * 1.5, ForMinutes: 60,
            Why: "A month's warning at the current rate — time to order a disk rather than find out when writes fail.");
}
