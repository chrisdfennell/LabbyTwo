using LabbyTwo.Core;
using LabbyTwo.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace LabbyTwo.Tests;

/// <summary>
/// The forecast against the shapes real disks actually make. Each series is built by hand,
/// hourly or finer, ending at a fixed "now", so what the fit sees is exactly what the test
/// says it sees.
/// </summary>
public class CapacityForecastTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-06-15T12:00:00Z");

    /// <summary>A reading every <paramref name="step"/>, for <paramref name="days"/> up to now.</summary>
    private static List<(DateTimeOffset At, double Value)> Series(
        double days, Func<double, double> valueAtDay, TimeSpan? step = null)
    {
        var every = step ?? TimeSpan.FromMinutes(30);
        var start = Now - TimeSpan.FromDays(days);
        var list = new List<(DateTimeOffset, double)>();
        for (var at = start; at <= Now; at += every)
            list.Add((at, valueAtDay((at - start).TotalDays)));
        return list;
    }

    private static CapacityForecast Percent(IEnumerable<(DateTimeOffset, double)> samples) =>
        CapacityForecast.Compute(samples, CapacityLimit.Percent, Now);

    [Fact]
    public void AFlatVolumeIsNotFilling()
    {
        var forecast = Percent(Series(14, _ => 62));

        Assert.Equal(ForecastState.NotFilling, forecast.State);
        Assert.Equal("not filling", forecast.Describe());
        Assert.Equal(double.PositiveInfinity, forecast.AlertValue);
    }

    [Fact]
    public void ASteadyRiseIsProjectedToTheLimit()
    {
        // 50% a fortnight ago, one point a day: 64% now, 36 days to go.
        var forecast = Percent(Series(14, day => 50 + day));

        Assert.Equal(ForecastState.Filling, forecast.State);
        Assert.InRange(forecast.DaysLeft!.Value, 35, 37);
        Assert.InRange(forecast.RatePerDay!.Value, 0.95, 1.05);
        Assert.Equal(ForecastConfidence.High, forecast.Confidence);
        Assert.Equal("full in about 5 weeks", forecast.Describe());
    }

    [Fact]
    public void NoiseAroundARiseStillFindsTheRise()
    {
        // The same rise with a wobble of a few points either way — the NAS's own caches,
        // snapshots coming and going — and the odd wild reading besides.
        var random = new Random(42);
        var forecast = Percent(Series(14, day =>
        {
            var wobble = (random.NextDouble() - 0.5) * 6;
            var wild = random.NextDouble() < 0.01 ? 25 : 0;
            return 50 + day + wobble + wild;
        }));

        Assert.Equal(ForecastState.Filling, forecast.State);
        Assert.InRange(forecast.DaysLeft!.Value, 30, 43);
        Assert.InRange(forecast.RatePerDay!.Value, 0.85, 1.15);
    }

    [Fact]
    public void NoiseWithNoRiseIsNotCalledARise()
    {
        var random = new Random(7);
        var forecast = Percent(Series(14, _ => 70 + (random.NextDouble() - 0.5) * 4));

        Assert.Equal(ForecastState.NotFilling, forecast.State);
    }

    [Fact]
    public void AClearOutMidWindowForecastsFromAfterIt()
    {
        // Filling fast for a week, a big delete, then filling at a gentler rate. The rate
        // that matters is the one since the delete — fitting across it would say the volume
        // is emptying.
        var forecast = Percent(Series(14, day => day < 7 ? 60 + day * 3 : 50 + (day - 7) * 1));

        Assert.Equal(ForecastState.Filling, forecast.State);
        Assert.NotNull(forecast.FittedFrom);
        Assert.InRange(forecast.FittedFrom!.Value, Now.AddDays(-7.2), Now.AddDays(-6.8));
        Assert.InRange(forecast.RatePerDay!.Value, 0.9, 1.1);
        Assert.InRange(forecast.DaysLeft!.Value, 41, 45);
    }

    [Fact]
    public void AClearOutYesterdayLeavesTooLittleToGoOn()
    {
        var forecast = Percent(Series(14, day => day < 13 ? 60 + day : 40));

        Assert.Equal(ForecastState.NotEnoughHistory, forecast.State);
        Assert.NotNull(forecast.FittedFrom);
        Assert.Contains("Cleared out recently", forecast.Explain());
        Assert.Null(forecast.AlertValue);
    }

    [Fact]
    public void OneLowReadingIsNotAClearOut()
    {
        // A probe that caught the NAS mid-scrub. One bad hour must not throw away twelve
        // days of history.
        var forecast = Percent(Series(14, day => day is > 10 and < 10.05 ? 20 : 50 + day));

        Assert.Equal(ForecastState.Filling, forecast.State);
        Assert.Null(forecast.FittedFrom);
        Assert.InRange(forecast.DaysLeft!.Value, 35, 37);
    }

    [Fact]
    public void TooShortAHistoryIsSaidSo()
    {
        var forecast = Percent(Series(1, day => 50 + day * 5));

        Assert.Equal(ForecastState.NotEnoughHistory, forecast.State);
        Assert.Equal("not enough history yet", forecast.Describe());
        Assert.Null(forecast.FittedFrom);
    }

    [Fact]
    public void AFewReadingsSpreadOverDaysAreNotEnoughEither()
    {
        var samples = new[] { (Now.AddDays(-5), 50.0), (Now.AddDays(-3), 55.0), (Now, 60.0) };

        Assert.Equal(ForecastState.NotEnoughHistory, Percent(samples).State);
    }

    [Fact]
    public void NoReadingsAtAllIsNotEnoughHistory()
    {
        Assert.Equal(ForecastState.NotEnoughHistory, Percent([]).State);
    }

    [Fact]
    public void AVolumeAlreadyAtTheLimitIsFullNow()
    {
        // Full needs no history: one reading of 100 is enough to say so.
        var forecast = Percent([(Now, 100.0)]);

        Assert.Equal(ForecastState.Full, forecast.State);
        Assert.Equal("full now", forecast.Describe());
        Assert.Equal(0, forecast.AlertValue);
    }

    [Fact]
    public void AFallingVolumeIsNotFilling()
    {
        var forecast = Percent(Series(14, day => 90 - day * 2));

        Assert.Equal(ForecastState.NotFilling, forecast.State);
        Assert.True(forecast.RatePerDay < 0);
    }

    [Fact]
    public void ARiseTooSlowToMatterIsNotFilling()
    {
        // A thousandth of a point a day from 50% is over a century away.
        var forecast = Percent(Series(14, day => 50 + day * 0.001));

        Assert.Equal(ForecastState.NotFilling, forecast.State);
    }

    [Fact]
    public void FreeSpaceRunningDownIsForecastToZero()
    {
        // The download disk: 500 GB free, losing ten a day, fifty days to go.
        var forecast = CapacityForecast.Compute(
            Series(14, day => 640 - day * 10), CapacityLimit.RunsOutAtZero, Now);

        Assert.Equal(ForecastState.Filling, forecast.State);
        Assert.InRange(forecast.DaysLeft!.Value, 49, 51);
        Assert.InRange(forecast.RatePerDay!.Value, -10.5, -9.5);
        Assert.Equal("full in about 7 weeks", forecast.Describe());
    }

    [Fact]
    public void FreeSpaceAtZeroIsFullNow()
    {
        var forecast = CapacityForecast.Compute([(Now, 0.0)], CapacityLimit.RunsOutAtZero, Now);

        Assert.Equal(ForecastState.Full, forecast.State);
    }

    [Fact]
    public void FreeSpaceGrowingIsNotFilling()
    {
        var forecast = CapacityForecast.Compute(
            Series(14, day => 100 + day * 10), CapacityLimit.RunsOutAtZero, Now);

        Assert.Equal(ForecastState.NotFilling, forecast.State);
    }

    [Fact]
    public void SamplesNeedNotArriveInOrder()
    {
        var samples = Series(14, day => 50 + day);
        var shuffled = samples.OrderBy(_ => Guid.NewGuid()).ToList();

        Assert.Equal(Percent(samples).DaysLeft, Percent(shuffled).DaysLeft);
    }

    [Fact]
    public void AFastFillOfAFortnightIsNotTrustedForYears()
    {
        // A perfect line, but followed forty times past the data it came from.
        var forecast = Percent(Series(3, day => 10 + day * 0.07, TimeSpan.FromMinutes(10)));

        Assert.Equal(ForecastState.Filling, forecast.State);
        Assert.Equal(ForecastConfidence.Low, forecast.Confidence);
    }

    [Theory]
    [InlineData(0.4, "within a day")]
    [InlineData(1.2, "in about a day")]
    [InlineData(5.4, "in about 5 days")]
    [InlineData(13.4, "in about 13 days")]
    [InlineData(43.2, "in about 6 weeks")]
    [InlineData(47, "in about 7 weeks")]
    [InlineData(90, "in about 3 months")]
    [InlineData(400, "in about 13 months")]
    [InlineData(500, "in about 16 months")]
    [InlineData(600, "in about 2 years")]
    [InlineData(1500, "in about 4 years")]
    public void DurationsAreSaidTheWayAPersonWouldSayThem(double days, string expected)
    {
        Assert.Equal(expected, CapacityForecast.Humanize(days));
    }

    [Fact]
    public void AForecastNeverClaimsDecimalPrecision()
    {
        for (var days = 0.1; days < CapacityForecast.HorizonDays; days *= 1.37)
            Assert.DoesNotMatch(@"\d[.,]\d", CapacityForecast.Humanize(days));
    }

    [Fact]
    public void SoonestSortsFirst()
    {
        var full = Percent([(Now, 100.0)]);
        var soon = Percent(Series(14, day => 80 + day));
        var later = Percent(Series(14, day => 30 + day));
        var flat = Percent(Series(14, _ => 50));
        var unknown = Percent([(Now, 50.0)]);

        var sorted = new[] { unknown, flat, later, soon, full }.OrderBy(f => f.SortKey).ToList();

        Assert.Equal([full, soon, later, flat, unknown], sorted);
    }

    // ---------- The derived metric ----------

    /// <summary>The real registry, with every built-in provider. Nothing here opens the database.</summary>
    private static Registry Registry()
    {
        var directory = TestHost.TempDirectory();
        var services = TestHost.Build(directory);
        try
        {
            return services.GetRequiredService<Registry>();
        }
        finally
        {
            TestHost.Teardown(services, directory);
        }
    }

    [Fact]
    public void TheDerivedKeyNamesTheMetricItForecasts()
    {
        var key = CapacityMetric.KeyFor("disk_percent");

        Assert.True(CapacityMetric.TryParse(key, out var measured));
        Assert.Equal("disk_percent", measured);
        Assert.False(CapacityMetric.TryParse("disk_percent", out _));
        Assert.False(CapacityMetric.TryParse(CapacityMetric.Prefix, out _));
    }

    [Fact]
    public void TheRegistryLabelsAForecastAfterWhatItForecasts()
    {
        var registry = Registry();
        var nas = new Connection { Provider = "qnap", Name = "NAS" };

        var spec = registry.Metric(nas, CapacityMetric.KeyFor("disk_percent"));

        Assert.Equal("Fullest volume: days until full", spec.Label);
        Assert.Equal(" days", spec.Unit);
    }

    [Fact]
    public void VolumeUsageIsACapacityAndCpuIsNot()
    {
        var registry = Registry();
        var nas = new Connection { Provider = "qnap", Name = "NAS" };

        Assert.Equal(CapacityLimit.Percent, registry.CapacityOf(nas, "disk_percent"));
        Assert.Null(registry.CapacityOf(nas, "cpu_percent"));
        Assert.Null(registry.CapacityOf(nas, CapacityMetric.KeyFor("disk_percent")));
    }

    [Fact]
    public void AWellKnownCapacityCountsEvenFromAProviderThatDidNotSaySo()
    {
        // A JSON API connection declares its metrics by name only, as does every plugin
        // written before capacity existed; disk_percent still means a disk filling.
        var registry = Registry();

        Assert.NotNull(registry.CapacityOf(new Connection { Provider = "json" }, "disk_percent"));
        Assert.NotNull(registry.CapacityOf(null, "disk_percent"));
    }

    [Fact]
    public void TheNasProvidersSuggestAMonthsWarning()
    {
        var suggested = new QnapProvider(null!, null!).SuggestedRules
            .Single(rule => CapacityMetric.TryParse(rule.Metric, out _));

        Assert.Equal(Comparison.Below, suggested.Comparison);
        Assert.Equal(30, suggested.Threshold);
        Assert.True(suggested.ClearThreshold > suggested.Threshold);
    }
}
