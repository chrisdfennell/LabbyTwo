using LabbyTwo.Core;

namespace LabbyTwo.Tests;

/// <summary>
/// "Unusual for the time" on its own: the baseline maths, and the verdict a rule reaches
/// from a reading and a usual value. The engine that acts on the verdict is the one every
/// threshold rule goes through, and is tested against a real store in
/// <see cref="MetricAlertServiceTests"/>.
/// </summary>
public class UnusualRuleTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    /// <summary>Monday 1 June 2026, midnight UTC — so day offsets land on known weekdays.</summary>
    private static readonly DateTimeOffset Monday = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>One hourly point per day at <paramref name="hour"/>, stamped mid-hour as the history store does.</summary>
    private static IEnumerable<(DateTimeOffset, double)> Daily(int days, int hour, Func<DateTimeOffset, double> value) =>
        Enumerable.Range(0, days)
            .Select(day => Monday.AddDays(day).AddHours(hour).AddMinutes(30))
            .Select(at => (at, value(at)));

    private static bool IsWeekend(DateTimeOffset at) => at.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    // ---------- Robust statistics ----------

    [Fact]
    public void TheMedianIsTheMiddleValueOrTheMeanOfTheMiddleTwo()
    {
        Assert.Equal(3, MetricBaseline.Median([5, 1, 3]));
        Assert.Equal(2.5, MetricBaseline.Median([4, 1, 3, 2]));
        Assert.Equal(0, MetricBaseline.Median([]));
    }

    [Fact]
    public void TheSpreadIgnoresTheOneBadEvening()
    {
        // Deviations from 3 are 2, 1, 0, 1, 97: the median of those is 1, whatever the 100 was.
        Assert.Equal(1.4826, MetricBaseline.RobustSpread([1, 2, 3, 4, 100], 3), 4);
    }

    [Fact]
    public void WhenMostValuesAgreeTheMeanDeviationStandsInForAZeroMad()
    {
        // Four fives and a nine: the MAD is 0, but the series plainly does vary.
        var spread = MetricBaseline.RobustSpread([5, 5, 5, 5, 9], 5);
        Assert.Equal(0.8 * 1.2533, spread, 4);
    }

    // ---------- Slots ----------

    [Fact]
    public void WeekdaysAndWeekendsAreLearnedApart()
    {
        // Streams at 8pm: 2 on a weekday, 6 at the weekend. Four weeks of it.
        var baseline = MetricBaseline.Compute(Daily(28, 20, at => IsWeekend(at) ? 6 : 2), Utc);

        var wednesday = baseline.At(Monday.AddDays(2).AddHours(20).AddMinutes(10), Utc)!;
        Assert.Equal(2, wednesday.Median);
        Assert.Equal(UsualScope.Weekdays, wednesday.Scope);

        var saturday = baseline.At(Monday.AddDays(5).AddHours(20).AddMinutes(10), Utc)!;
        Assert.Equal(6, saturday.Median);
        Assert.Equal(UsualScope.Weekends, saturday.Scope);
        Assert.Equal("at this hour at the weekend", saturday.When);
    }

    [Fact]
    public void EachHourIsItsOwnSlot()
    {
        var evenings = Daily(10, 20, _ => 500);
        var nights = Daily(10, 3, _ => 900);
        var baseline = MetricBaseline.Compute(evenings.Concat(nights), Utc);

        Assert.Equal(500, baseline.At(Monday.AddDays(1).AddHours(20), Utc)!.Median);
        Assert.Equal(900, baseline.At(Monday.AddDays(1).AddHours(3), Utc)!.Median);
    }

    [Fact]
    public void FewerThanFiveDaysIsStillLearning()
    {
        var baseline = MetricBaseline.Compute(Daily(4, 20, _ => 500), Utc);

        Assert.True(baseline.IsLearning);
        Assert.Equal(1, baseline.DaysNeeded);
        Assert.Null(baseline.At(Monday.AddDays(4).AddHours(20), Utc));
    }

    [Fact]
    public void ManyReadingsInOneDayStillCountAsOneDay()
    {
        // Every hour for four days is 96 readings, and still only four evenings.
        var points = Enumerable.Range(0, 4 * 24).Select(h => (Monday.AddHours(h).AddMinutes(30), 500.0));
        var baseline = MetricBaseline.Compute(points, Utc);

        Assert.True(baseline.IsLearning);
        Assert.Equal(4, baseline.Days);
    }

    [Fact]
    public void FiveDaysIsEnough()
    {
        var baseline = MetricBaseline.Compute(Daily(5, 20, _ => 500), Utc);

        Assert.False(baseline.IsLearning);
        Assert.Equal(0, baseline.DaysNeeded);
        Assert.Equal(500, baseline.At(Monday.AddDays(5).AddHours(20), Utc)!.Median);
    }

    [Fact]
    public void AWeekendWithTooFewDaysBorrowsTheWholeWeeksHour()
    {
        // One week: five weekdays but only two weekend days at 8pm.
        var baseline = MetricBaseline.Compute(Daily(7, 20, at => IsWeekend(at) ? 600 : 500), Utc);

        var sunday = baseline.At(Monday.AddDays(6).AddHours(20), Utc)!;
        Assert.Equal(UsualScope.ThisHour, sunday.Scope);
        Assert.Equal(500, sunday.Median);
        Assert.Equal("at this hour", sunday.When);
    }

    [Fact]
    public void AnHourTheMetricIsNeverReportedFallsBackToTheWholeDay()
    {
        // A speed test every six hours: noon has no slot of its own, and never will.
        var points = new[] { 0, 6, 12, 18 }.SelectMany(hour => Daily(10, hour, _ => 500 + hour));
        var baseline = MetricBaseline.Compute(points, Utc);

        var usual = baseline.At(Monday.AddDays(3).AddHours(14), Utc)!;
        Assert.Equal(UsualScope.AnyTime, usual.Scope);
        Assert.Equal("", usual.When);
    }

    [Fact]
    public void ASteadyMetricIsNeverUnusualForMovingAFewPercent()
    {
        // 212 W every hour for a month has no spread at all; 214 W is not news.
        var baseline = MetricBaseline.Compute(Daily(28, 20, _ => 212), Utc);
        var usual = baseline.At(Monday.AddDays(2).AddHours(20), Utc)!;

        Assert.Equal(212 * 0.05, usual.Spread, 6);
        Assert.True(Math.Abs(usual.SpreadsFrom(214)) < 1);
    }

    [Fact]
    public void AnHourThatNeverVariedIsJudgedByHowMuchTheWholeDayVaries()
    {
        // No streams at 3am, ever; one to four in the evening.
        var nights = Daily(14, 3, _ => 0);
        var evenings = Daily(14, 20, at => at.Day % 4 + 1);
        var baseline = MetricBaseline.Compute(nights.Concat(evenings), Utc);

        var usual = baseline.At(Monday.AddDays(2).AddHours(3), Utc)!;
        Assert.Equal(0, usual.Median);
        Assert.True(usual.Spread > 0);
        Assert.True(double.IsFinite(usual.SpreadsFrom(1)));
    }

    [Fact]
    public void AnEmptyHistoryIsLearningWithEverythingStillToLearn()
    {
        var baseline = MetricBaseline.Compute([], Utc);
        Assert.True(baseline.IsLearning);
        Assert.Equal(MetricBaseline.MinDays, baseline.DaysNeeded);
    }

    // ---------- Verdicts ----------

    private static readonly Usual Usual500 = new(500, 50, 10, UsualScope.ThisHour);

    private static AlertRule Rule(UnusualBy by, Comparison comparison, double threshold, double? clear = null) => new()
    {
        Metric = "download_mbps",
        Kind = RuleKind.Unusual,
        UnusualBy = by,
        Comparison = comparison,
        Threshold = threshold,
        ClearThreshold = clear,
    };

    [Theory]
    [InlineData(240, Verdict.Breaching)]  // 48% of usual
    [InlineData(250, Verdict.Breaching)]  // exactly half
    [InlineData(300, Verdict.Holding)]    // 60%: better, but not yet back past two thirds of the way
    [InlineData(350, Verdict.Cleared)]    // 70%
    public void LessThanHalfItsUsual(double value, Verdict expected) =>
        Assert.Equal(expected, Rule(UnusualBy.Percent, Comparison.Below, 50).Judge(value, Usual500));

    [Theory]
    [InlineData(1000, Verdict.Breaching)]
    [InlineData(900, Verdict.Holding)]
    [InlineData(800, Verdict.Cleared)]
    public void MoreThanDoubleItsUsual(double value, Verdict expected) =>
        Assert.Equal(expected, Rule(UnusualBy.Percent, Comparison.Above, 200).Judge(value, Usual500));

    [Theory]
    [InlineData(1000, Verdict.Breaching)]
    [InlineData(250, Verdict.Breaching)]
    [InlineData(290, Verdict.Holding)]
    [InlineData(900, Verdict.Holding)]
    [InlineData(500, Verdict.Cleared)]
    public void DoubleOrHalfIsSymmetricAsARatio(double value, Verdict expected) =>
        Assert.Equal(expected, Rule(UnusualBy.Percent, Comparison.Either, 200).Judge(value, Usual500));

    [Theory]
    [InlineData(650, Verdict.Breaching)]  // three spreads above
    [InlineData(620, Verdict.Holding)]    // 2.4
    [InlineData(590, Verdict.Cleared)]    // 1.8, inside two
    [InlineData(100, Verdict.Cleared)]    // far below is not "above"
    public void WellAboveUsualBySpread(double value, Verdict expected) =>
        Assert.Equal(expected, Rule(UnusualBy.Spread, Comparison.Above, 3).Judge(value, Usual500));

    [Theory]
    [InlineData(350, Verdict.Breaching)]
    [InlineData(380, Verdict.Holding)]
    [InlineData(410, Verdict.Cleared)]
    public void WellBelowUsualBySpread(double value, Verdict expected) =>
        Assert.Equal(expected, Rule(UnusualBy.Spread, Comparison.Below, 3).Judge(value, Usual500));

    [Theory]
    [InlineData(650, Verdict.Breaching)]
    [InlineData(350, Verdict.Breaching)]
    [InlineData(500, Verdict.Cleared)]
    public void EitherWayBySpread(double value, Verdict expected) =>
        Assert.Equal(expected, Rule(UnusualBy.Spread, Comparison.Either, 3).Judge(value, Usual500));

    [Fact]
    public void AnExplicitClearBandReplacesTheDefault()
    {
        var rule = Rule(UnusualBy.Percent, Comparison.Below, 50, clear: 90);
        Assert.Equal(Verdict.Holding, rule.Judge(400, Usual500));
        Assert.Equal(Verdict.Cleared, rule.Judge(460, Usual500));
    }

    [Fact]
    public void NothingToCompareAgainstIsNoVerdictAtAll()
    {
        Assert.Null(Rule(UnusualBy.Percent, Comparison.Below, 50).Judge(10, null));

        // Half of a usual zero is still zero; a percentage cannot say anything here.
        var usuallyNothing = new Usual(0, 1, 10, UsualScope.ThisHour);
        Assert.Null(Rule(UnusualBy.Percent, Comparison.Above, 200).Judge(3, usuallyNothing));
        Assert.Equal(Verdict.Breaching, Rule(UnusualBy.Spread, Comparison.Above, 2).Judge(3, usuallyNothing));
    }

    [Fact]
    public void AThresholdRuleIgnoresUsualAltogether()
    {
        var rule = new AlertRule { Metric = "disk_percent", Comparison = Comparison.Above, Threshold = 90, ClearThreshold = 85 };

        Assert.Equal(Verdict.Breaching, rule.Judge(95, null));
        Assert.Equal(Verdict.Holding, rule.Judge(87, Usual500));
        Assert.Equal(Verdict.Cleared, rule.Judge(80, null));
    }

    // ---------- What can be saved ----------

    [Fact]
    public void ARuleThatCouldNeverFireOrNeverClearIsRefused()
    {
        Assert.NotNull(new AlertRule { Metric = "m", Comparison = Comparison.Either, Threshold = 1 }.Problem());
        Assert.NotNull(Rule(UnusualBy.Percent, Comparison.Below, 150).Problem());
        Assert.NotNull(Rule(UnusualBy.Percent, Comparison.Above, 50).Problem());
        Assert.NotNull(Rule(UnusualBy.Percent, Comparison.Either, 80).Problem());
        Assert.NotNull(Rule(UnusualBy.Percent, Comparison.Below, 50, clear: 40).Problem());
        Assert.NotNull(Rule(UnusualBy.Percent, Comparison.Above, 200, clear: 250).Problem());
        Assert.NotNull(Rule(UnusualBy.Spread, Comparison.Above, 3, clear: 4).Problem());
        Assert.NotNull(Rule(UnusualBy.Spread, Comparison.Above, 0).Problem());

        Assert.Null(Rule(UnusualBy.Percent, Comparison.Below, 50, clear: 70).Problem());
        Assert.Null(Rule(UnusualBy.Percent, Comparison.Either, 200).Problem());
        Assert.Null(Rule(UnusualBy.Spread, Comparison.Below, 4, clear: 2).Problem());
    }

    // ---------- Words ----------

    [Fact]
    public void TheConditionReadsAsASentence()
    {
        Assert.Equal("less than half its usual", Rule(UnusualBy.Percent, Comparison.Below, 50).UnusualPhrase());
        Assert.Equal("more than double its usual", Rule(UnusualBy.Percent, Comparison.Above, 200).UnusualPhrase());
        Assert.Equal("double or half its usual", Rule(UnusualBy.Percent, Comparison.Either, 200).UnusualPhrase());
        Assert.Equal("well above usual", Rule(UnusualBy.Spread, Comparison.Above, 4).UnusualPhrase());
        Assert.Equal("far below usual", Rule(UnusualBy.Spread, Comparison.Below, 6).UnusualPhrase());

        Assert.Equal("Internet · Download less than half its usual",
            Rule(UnusualBy.Percent, Comparison.Below, 50).Describe("Download", "Internet"));
    }

    [Fact]
    public void ASuggestionCarriesItsKindAndIsNotCoveredByAThreshold()
    {
        var suggestion = new SuggestedRule("Slow", "download_mbps", Comparison.Below, 50, ForMinutes: 30)
        {
            Kind = RuleKind.Unusual,
            UnusualBy = UnusualBy.Percent,
        };

        var rule = suggestion.ForConnection("line");
        Assert.Equal(RuleKind.Unusual, rule.Kind);
        Assert.Equal(UnusualBy.Percent, rule.UnusualBy);
        Assert.True(suggestion.IsCoveredBy(rule, "line"));

        var fixedLine = new AlertRule { Metric = "download_mbps", Comparison = Comparison.Below, Threshold = 100, ConnectionId = "line" };
        Assert.False(suggestion.IsCoveredBy(fixedLine, "line"));
    }

    [Theory]
    [InlineData("below", Comparison.Below)]
    [InlineData("either", Comparison.Either)]
    [InlineData("Above", Comparison.Above)]
    [InlineData("nonsense", Comparison.Above)]
    [InlineData(null, Comparison.Above)]
    public void AStoredComparisonReadsBack(string? stored, Comparison expected) =>
        Assert.Equal(expected, AlertRule.ParseComparison(stored));
}
