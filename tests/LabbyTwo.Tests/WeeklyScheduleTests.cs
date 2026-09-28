using LabbyTwo.Core;

namespace LabbyTwo.Tests;

/// <summary>
/// When the weekly summary is due. The failures worth fearing are all about time: sent an
/// hour off after the clocks change, sent twice because the process restarted, sent three
/// times after a long outage, or sent at 3am into somebody's quiet hours.
/// </summary>
public sealed class WeeklyScheduleTests
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

    private static WeeklySchedule Monday(int hour = 9, int minute = 0) =>
        new(true, DayOfWeek.Monday, new TimeOnly(hour, minute), []);

    /// <summary>A local wall-clock time in the test zone, as an instant.</summary>
    private static DateTimeOffset Local(int month, int day, int hour, int minute = 0) =>
        WeeklySchedule.At(new DateOnly(2026, month, day), new TimeOnly(hour, minute), Eastern);

    [Fact]
    public void NextIsTheComingWeekday()
    {
        // Wednesday 16 September; the next Monday is the 21st.
        var next = Monday().NextAfter(Local(9, 16, 12), Eastern);
        Assert.Equal(Local(9, 21, 9), next);
    }

    [Fact]
    public void OnTheDayBeforeTheTimeItIsToday()
    {
        Assert.Equal(Local(9, 21, 9), Monday().NextAfter(Local(9, 21, 8, 59), Eastern));
        Assert.Equal(Local(9, 28, 9), Monday().NextAfter(Local(9, 21, 9), Eastern));
    }

    [Fact]
    public void TheWallClockTimeHoldsAcrossTheAutumnChange()
    {
        // Clocks go back on Sunday 1 November. The Monday before and the Monday after are
        // both 09:00 local, which is 169 hours apart rather than 168.
        var before = Monday().NextAfter(Local(10, 25, 12), Eastern);
        var after = Monday().NextAfter(before, Eastern);

        Assert.Equal(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.FromHours(-5)), after);
        Assert.Equal(TimeSpan.FromHours(169), after - before);
    }

    [Fact]
    public void TheWallClockTimeHoldsAcrossTheSpringChange()
    {
        var before = Monday().NextAfter(Local(3, 1, 12), Eastern);
        var after = Monday().NextAfter(before, Eastern);

        Assert.Equal(new DateTimeOffset(2026, 3, 9, 9, 0, 0, TimeSpan.FromHours(-4)), after);
        Assert.Equal(TimeSpan.FromHours(167), after - before);
    }

    [Fact]
    public void ATimeTheClocksSkipIsSentJustAfterTheJump()
    {
        // 02:30 on Sunday 8 March does not exist; it becomes 03:30 daylight time.
        var schedule = new WeeklySchedule(true, DayOfWeek.Sunday, new TimeOnly(2, 30), []);
        var at = schedule.NextAfter(Local(3, 7, 12), Eastern);

        Assert.Equal(new DateTimeOffset(2026, 3, 8, 3, 30, 0, TimeSpan.FromHours(-4)), at);
    }

    [Fact]
    public void ATimeThatHappensTwiceIsSentTheFirstTime()
    {
        // 01:30 on Sunday 1 November happens at -4 and again at -5.
        var schedule = new WeeklySchedule(true, DayOfWeek.Sunday, new TimeOnly(1, 30), []);
        var at = schedule.NextAfter(Local(10, 31, 12), Eastern);

        Assert.Equal(new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.FromHours(-4)), at);

        // And the second 01:30, an hour later, is already covered by the first.
        var secondTime = new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.FromHours(-5));
        Assert.False(schedule.Decide(at, NoQuiet, secondTime, Eastern).SendNow);
    }

    [Fact]
    public void OffIsNeverDue()
    {
        var off = Monday() with { Enabled = false };
        var decision = off.Decide(Local(9, 1, 0), NoQuiet, Local(9, 21, 10), Eastern);

        Assert.False(decision.SendNow);
        Assert.Null(decision.NextAt);
    }

    [Fact]
    public void NotArmedIsNeverDue()
    {
        Assert.False(Monday().Decide(null, NoQuiet, Local(9, 21, 10), Eastern).SendNow);
    }

    [Fact]
    public void DueOnceTheTimePassesAndNotAgainOnceCovered()
    {
        var schedule = Monday();
        var armed = Local(9, 16, 12);

        Assert.False(schedule.Decide(armed, NoQuiet, Local(9, 21, 8, 59), Eastern).SendNow);

        var due = schedule.Decide(armed, NoQuiet, Local(9, 21, 9), Eastern);
        Assert.True(due.SendNow);

        // Sent at 09:00, which covers everything up to then. A restart a minute later reads
        // the same stored instant and finds nothing due.
        var sentAt = Local(9, 21, 9);
        var afterRestart = schedule.Decide(sentAt, NoQuiet, Local(9, 21, 9, 1), Eastern);
        Assert.False(afterRestart.SendNow);
        Assert.Equal(Local(9, 28, 9), afterRestart.NextAt);
    }

    [Fact]
    public void TurningItOnMidweekDoesNotSendLastMondaysStraightAway()
    {
        // Armed on Wednesday: last Monday's time is before the arming, so it is not owed.
        var decision = Monday().Decide(Local(9, 16, 12), NoQuiet, Local(9, 16, 12, 1), Eastern);

        Assert.False(decision.SendNow);
        Assert.Equal(Local(9, 21, 9), decision.NextAt);
    }

    [Fact]
    public void DownAtTheTimeCatchesUpOnceWhenItComesBack()
    {
        // Last sent on the 14th; the app was off over the 21st and starts again on the 22nd.
        var lastCovered = Local(9, 14, 9);
        var backUp = Local(9, 22, 18);

        Assert.True(Monday().Decide(lastCovered, NoQuiet, backUp, Eastern).SendNow);
        Assert.False(Monday().Decide(backUp, NoQuiet, backUp.AddMinutes(1), Eastern).SendNow);
    }

    [Fact]
    public void DownForSeveralWeeksCatchesUpOnceNotOncePerWeek()
    {
        var lastCovered = Local(8, 3, 9);
        var backUp = Local(9, 23, 12);

        Assert.True(Monday().Decide(lastCovered, NoQuiet, backUp, Eastern).SendNow);

        // Once sent, the three missed Mondays are all behind the covered instant.
        Assert.False(Monday().Decide(backUp, NoQuiet, backUp.AddMinutes(1), Eastern).SendNow);
        Assert.Equal(Local(9, 28, 9), Monday().Decide(backUp, NoQuiet, backUp.AddMinutes(1), Eastern).NextAt);
    }

    [Fact]
    public void ATimeInsideQuietHoursWaitsForThemToEnd()
    {
        var early = Monday(6, 0);
        var armed = Local(9, 16, 12);

        // Due at 06:00 but held, and the Settings page is told 07:00.
        Assert.Equal(Local(9, 21, 7), early.Decide(armed, Overnight, Local(9, 20, 12), Eastern).NextAt);

        var held = early.Decide(armed, Overnight, Local(9, 21, 6), Eastern);
        Assert.False(held.SendNow);
        Assert.Equal(Local(9, 21, 7), held.NextAt);

        Assert.False(early.Decide(armed, Overnight, Local(9, 21, 6, 59), Eastern).SendNow);
        Assert.True(early.Decide(armed, Overnight, Local(9, 21, 7), Eastern).SendNow);
    }

    [Fact]
    public void QuietHoursThatWrapMidnightEndTheNextMorning()
    {
        var late = new WeeklySchedule(true, DayOfWeek.Sunday, new TimeOnly(23, 30), []);
        var next = late.Decide(Local(9, 16, 12), Overnight, Local(9, 16, 12, 1), Eastern).NextAt;

        Assert.Equal(Local(9, 21, 7), next);
    }

    [Fact]
    public void QuietHoursHoldItEvenInDownOnlyMode()
    {
        // "Down only" lets outages through. A summary is not an outage.
        var held = Monday(3, 0).Decide(Local(9, 16, 12), Overnight, Local(9, 21, 3), Eastern);
        Assert.False(held.SendNow);
    }

    [Fact]
    public void SettingsRoundTrip()
    {
        var schedule = new WeeklySchedule(true, DayOfWeek.Friday, new TimeOnly(17, 45), ["a1", "b2"]);
        var bag = new SettingsBag(schedule.ToSettings());

        var read = WeeklySchedule.From(bag);
        Assert.Equal(schedule.Enabled, read.Enabled);
        Assert.Equal(schedule.Day, read.Day);
        Assert.Equal(schedule.Time, read.Time);
        Assert.Equal(schedule.Channels, read.Channels);
    }

    [Fact]
    public void NothingStoredMeansOffOnMondayMorning()
    {
        var read = WeeklySchedule.From(new SettingsBag());

        Assert.False(read.Enabled);
        Assert.Equal(DayOfWeek.Monday, read.Day);
        Assert.Equal(new TimeOnly(9, 0), read.Time);
        Assert.Empty(read.Channels);
    }

    [Fact]
    public void GarbageStoredFallsBackRatherThanThrowing()
    {
        var read = WeeklySchedule.From(new SettingsBag
        {
            [WeeklySchedule.EnabledKey] = "true",
            [WeeklySchedule.DayKey] = "Blursday",
            [WeeklySchedule.TimeKey] = "25:99",
        });

        Assert.True(read.Enabled);
        Assert.Equal(DayOfWeek.Monday, read.Day);
        Assert.Equal(new TimeOnly(9, 0), read.Time);
    }
}
