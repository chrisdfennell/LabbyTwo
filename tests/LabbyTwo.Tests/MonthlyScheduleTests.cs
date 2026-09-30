using LabbyTwo.Core;

namespace LabbyTwo.Tests;

/// <summary>
/// When the monthly report is made, and which month it is about. The failures worth fearing
/// are the weekly summary's plus the calendar's own: an hour off after the clocks change, a
/// second report after a restart, three after a long outage, a 31st that never comes in
/// February, and a report dated by UTC that is about the wrong month in the lab's own zone.
/// </summary>
public sealed class MonthlyScheduleTests
{
    /// <summary>UTC-5 in winter, UTC-4 from the second Sunday of March to the first of November.</summary>
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.CreateCustomTimeZone(
        "Test/Eastern", TimeSpan.FromHours(-5), "Test Eastern", "Test Eastern", "Test Eastern Daylight",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday)),
        ]);

    private static readonly AlertPolicy NoQuiet = AlertPolicy.Default;
    private static readonly AlertPolicy Overnight = new(new TimeOnly(23, 0), new TimeOnly(7, 0), AlertPolicy.DownOnly);

    private static MonthlySchedule On(int day, int hour = 8, int minute = 0, bool send = false) =>
        new(true, day, new TimeOnly(hour, minute), send, []);

    private static DateTimeOffset Local(int month, int day, int hour, int minute = 0, int year = 2026) =>
        WeeklySchedule.At(new DateOnly(year, month, day), new TimeOnly(hour, minute), Eastern);

    [Fact]
    public void NextIsTheComingFirst()
    {
        Assert.Equal(Local(10, 1, 8), On(1).NextAfter(Local(9, 16, 12), Eastern));
        Assert.Equal(Local(10, 1, 8), On(1).NextAfter(Local(10, 1, 7, 59), Eastern));
        Assert.Equal(Local(11, 1, 8), On(1).NextAfter(Local(10, 1, 8), Eastern));
        Assert.Equal(Local(1, 1, 8, year: 2027), On(1).NextAfter(Local(12, 5, 8), Eastern));
    }

    [Fact]
    public void The31stIsTheLastDayOfAShortMonth()
    {
        var end = On(31, 20);
        Assert.Equal(Local(2, 28, 20), end.NextAfter(Local(2, 1, 0), Eastern));
        Assert.Equal(Local(4, 30, 20), end.NextAfter(Local(4, 1, 0), Eastern));
        Assert.Equal(Local(2, 29, 20, year: 2028), end.In(new DateOnly(2028, 2, 1), Eastern));
        Assert.Equal(Local(1, 31, 20), end.LatestAtOrBefore(Local(2, 27, 0), Eastern));
    }

    [Fact]
    public void ATimeTheClocksSkipIsTakenJustAfterTheJump()
    {
        // 8 March 2026 is the second Sunday; 02:30 does not exist that morning.
        var at = On(8, 2, 30).In(new DateOnly(2026, 3, 1), Eastern);
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 3, 30, 0, TimeSpan.FromHours(-4)), at);
    }

    [Fact]
    public void ATimeThatHappensTwiceIsTheFirst()
    {
        // 1 November 2026 is the first Sunday; 01:30 happens in daylight time, then again.
        var at = On(1, 1, 30).In(new DateOnly(2026, 11, 1), Eastern);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.FromHours(-4)), at);
    }

    [Fact]
    public void TheWallClockTimeHoldsAcrossAClockChange()
    {
        // The clocks go back at 02:00 on 1 November, six hours before November's report: it
        // is still at 08:00 on the wall, a month and an hour after October's.
        var october = On(1).NextAfter(Local(9, 20, 0), Eastern);
        var november = On(1).NextAfter(october, Eastern);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.FromHours(-4)), october);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.FromHours(-5)), november);
        Assert.Equal(TimeSpan.FromDays(31) + TimeSpan.FromHours(1), november - october);
        var december = On(1).NextAfter(november, Eastern);
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 8, 0, 0, TimeSpan.FromHours(-5)), december);
        Assert.Equal(TimeSpan.FromDays(30), december - november);
    }

    [Fact]
    public void AMonthIsItsLocalMidnightsApart()
    {
        var (from, to) = MonthlySchedule.Span(new DateOnly(2026, 11, 1), Eastern);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(-4)), from);
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.FromHours(-5)), to);
        Assert.Equal(TimeSpan.FromDays(30) + TimeSpan.FromHours(1), to - from);

        var (march, april) = MonthlySchedule.Span(new DateOnly(2026, 3, 15), Eastern);
        Assert.Equal(TimeSpan.FromDays(31) - TimeSpan.FromHours(1), april - march);
    }

    [Fact]
    public void TheReportIsAboutTheMonthBeforeInTheLabsOwnZone()
    {
        Assert.Equal(new DateOnly(2026, 9, 1), MonthlySchedule.ReportedMonth(Local(10, 1, 8), Eastern));
        Assert.Equal(new DateOnly(2026, 12, 1), MonthlySchedule.ReportedMonth(Local(1, 1, 0, 5, year: 2027), Eastern));

        // Two in the morning on 1 October in UTC is still the evening of 30 September in
        // the lab: a report made then is August's, not September's.
        var utc = new DateTimeOffset(2026, 10, 1, 2, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 8, 1), MonthlySchedule.ReportedMonth(utc, Eastern));
        Assert.Equal(new DateOnly(2026, 9, 1), MonthlySchedule.ReportedMonth(utc, TimeZoneInfo.Utc));
        Assert.Equal("September 2026", MonthlySchedule.Name(new DateOnly(2026, 9, 1)));
    }

    [Fact]
    public void OffIsNeverDue()
    {
        var decision = (On(1) with { Enabled = false }).Decide(Local(9, 1, 0), NoQuiet, Local(10, 2, 0), Eastern);
        Assert.False(decision.MakeNow);
        Assert.Null(decision.NextAt);
    }

    [Fact]
    public void DueOnceThenWaitingForTheNextMonth()
    {
        var schedule = On(1);
        var covered = Local(9, 15, 12);

        Assert.False(schedule.Decide(covered, NoQuiet, Local(10, 1, 7, 59), Eastern).MakeNow);
        Assert.True(schedule.Decide(covered, NoQuiet, Local(10, 1, 8), Eastern).MakeNow);

        // Made at 08:00 and covered to then: a restart a minute later makes nothing more.
        var after = schedule.Decide(Local(10, 1, 8), NoQuiet, Local(10, 1, 8, 1), Eastern);
        Assert.False(after.MakeNow);
        Assert.Equal(Local(11, 1, 8), after.NextAt);
    }

    [Fact]
    public void ThreeMonthsDownMakesOneReportOfTheMonthJustGone()
    {
        var schedule = On(1);
        var covered = Local(6, 1, 8);
        var back = Local(9, 10, 12);

        var decision = schedule.Decide(covered, NoQuiet, back, Eastern);
        Assert.True(decision.MakeNow);
        Assert.Equal(new DateOnly(2026, 8, 1), MonthlySchedule.ReportedMonth(back, Eastern));
        Assert.False(schedule.Decide(back, NoQuiet, back.AddMinutes(1), Eastern).MakeNow);
    }

    [Fact]
    public void QuietHoursHoldOnlyAReportThatIsAlsoSent()
    {
        var covered = Local(9, 15, 12);
        var night = Local(10, 1, 3);

        // A note at 3am wakes nobody.
        Assert.True(On(1, 2).Decide(covered, Overnight, night, Eastern).MakeNow);

        var held = On(1, 2, send: true).Decide(covered, Overnight, night, Eastern);
        Assert.False(held.MakeNow);
        Assert.Equal(Local(10, 1, 7), held.NextAt);
        Assert.True(On(1, 2, send: true).Decide(covered, Overnight, Local(10, 1, 7), Eastern).MakeNow);
    }

    [Fact]
    public void NotArmedIsNeverDue() =>
        Assert.False(On(1).Decide(null, NoQuiet, Local(10, 2, 0), Eastern).MakeNow);

    [Fact]
    public void SettingsRoundTrip()
    {
        var schedule = new MonthlySchedule(true, 31, new TimeOnly(6, 45), true, ["a", "b"]);
        var bag = new SettingsBag();
        foreach (var (key, value) in schedule.ToSettings())
            bag[key] = value;
        var read = MonthlySchedule.From(bag);

        Assert.Equal(schedule.Enabled, read.Enabled);
        Assert.Equal(31, read.Day);
        Assert.Equal(new TimeOnly(6, 45), read.Time);
        Assert.True(read.Send);
        Assert.Equal(["a", "b"], read.Channels);

        Assert.Equal(MonthlySchedule.Default, MonthlySchedule.From(new SettingsBag()) with { Channels = MonthlySchedule.Default.Channels });
        Assert.Equal(31, MonthlySchedule.From(new SettingsBag { [MonthlySchedule.DayKey] = "99" }).Day);
    }
}
