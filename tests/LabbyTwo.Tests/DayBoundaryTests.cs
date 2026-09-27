using LabbyTwo.Storage;

namespace LabbyTwo.Tests;

/// <summary>
/// The uptime strip's days, in a zone that changes its clocks. Built as a custom zone rather
/// than looked up by name, so the test means the same thing on every machine that runs it.
/// </summary>
public sealed class DayBoundaryTests
{
    /// <summary>UTC-5 in winter, UTC-4 from the second Sunday of March to the first of November.</summary>
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.CreateCustomTimeZone(
        "Test/Eastern", TimeSpan.FromHours(-5), "Test Eastern", "Test Eastern",
        "Test Eastern Daylight",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday)),
        ]);

    [Fact]
    public void EachDayStartsWithItsOwnOffset()
    {
        // Either side of the March 2026 change (Sunday the 8th).
        Assert.Equal(TimeSpan.FromHours(-5), HistoryStore.StartOfDay(new DateOnly(2026, 3, 7), Eastern).Offset);
        Assert.Equal(TimeSpan.FromHours(-4), HistoryStore.StartOfDay(new DateOnly(2026, 3, 9), Eastern).Offset);
    }

    [Fact]
    public void TheDayOfAClockChangeIsNotTwentyFourHoursLong()
    {
        var spring = new DateOnly(2026, 3, 8);
        Assert.Equal(TimeSpan.FromHours(23),
            HistoryStore.StartOfDay(spring.AddDays(1), Eastern) - HistoryStore.StartOfDay(spring, Eastern));

        var autumn = new DateOnly(2026, 11, 1);
        Assert.Equal(TimeSpan.FromHours(25),
            HistoryStore.StartOfDay(autumn.AddDays(1), Eastern) - HistoryStore.StartOfDay(autumn, Eastern));
    }

    [Fact]
    public void MidnightIsMidnightLocallyOnBothSides()
    {
        foreach (var date in new[] { new DateOnly(2026, 1, 15), new DateOnly(2026, 7, 15) })
        {
            var start = HistoryStore.StartOfDay(date, Eastern);
            var local = TimeZoneInfo.ConvertTime(start, Eastern);
            Assert.Equal(date.ToDateTime(TimeOnly.MinValue), local.DateTime);
        }
    }
}
