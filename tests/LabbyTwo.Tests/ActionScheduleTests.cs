using LabbyTwo.Core;

namespace LabbyTwo.Tests;

/// <summary>
/// When a scheduled action runs. The failures worth fearing are all about time: a 02:30
/// restart lost for the night the clocks spring forward, a 01:30 restart done twice the night
/// they fall back, "every 15 minutes" drifting, a monthly job that skips February, a cron
/// expression that means something other than what was typed — and, after downtime, a burst
/// of every run that was missed.
/// </summary>
public sealed class ActionScheduleTests
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

    /// <summary>A local wall-clock time in the test zone, as an instant (2026).</summary>
    private static DateTimeOffset Local(int month, int day, int hour, int minute = 0) =>
        WeeklySchedule.At(new DateOnly(2026, month, day), new TimeOnly(hour, minute), Eastern);

    private static DateTimeOffset Utc(int month, int day, int hour, int minute = 0) =>
        new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    private static ActionSchedule Weekly(DayOfWeek[] days, params TimeOnly[] times) =>
        new() { Kind = ScheduleKind.Weekly, Days = days, Times = times };

    private static ActionSchedule Daily(params TimeOnly[] times) => Weekly([.. MuteWindow.Week], times);

    private static ActionSchedule Every(int minutes) => new() { Kind = ScheduleKind.Interval, IntervalMinutes = minutes };

    private static ActionSchedule Cron(string cron) => new() { Kind = ScheduleKind.Cron, Cron = cron };

    private static TimeOnly T(int hour, int minute = 0) => new(hour, minute);

    // ---------- Weekly ----------

    [Fact]
    public void SundaysAtFourIsTheComingSunday()
    {
        // Wednesday 16 September; the next Sunday is the 20th.
        var next = Weekly([DayOfWeek.Sunday], T(4)).NextAfter(Local(9, 16, 12), Eastern);
        Assert.Equal(Local(9, 20, 4), next);
    }

    [Fact]
    public void SeveralTimesADayTakeTheNextOneToday()
    {
        var schedule = Daily(T(4), T(16));
        Assert.Equal(Local(9, 16, 16), schedule.NextAfter(Local(9, 16, 5), Eastern));
        Assert.Equal(Local(9, 17, 4), schedule.NextAfter(Local(9, 16, 16), Eastern));
    }

    [Fact]
    public void TheLatestRunIsTheMostRecentPastOne()
    {
        var schedule = Weekly([DayOfWeek.Sunday], T(4));
        Assert.Equal(Local(9, 20, 4), schedule.LatestAtOrBefore(Local(9, 21, 9), Eastern));
        Assert.Equal(Local(9, 20, 4), schedule.LatestAtOrBefore(Local(9, 20, 4), Eastern));
    }

    [Fact]
    public void ATimeTheClocksSkipRunsJustAfterTheJumpRatherThanNotAtAll()
    {
        // 8 March 2026: 02:00 EST jumps to 03:00 EDT, so 02:30 never happens on the clock.
        var schedule = Daily(T(2, 30));
        var next = schedule.NextAfter(Local(3, 7, 12), Eastern);

        Assert.Equal(Utc(3, 8, 7, 30), next);                           // 03:30 EDT
        Assert.Equal(TimeSpan.FromHours(-4), TimeZoneInfo.ConvertTime(next!.Value, Eastern).Offset);
        // And the night after, back to 02:30 on the clock.
        Assert.Equal(Utc(3, 9, 6, 30), schedule.NextAfter(next.Value, Eastern));
    }

    [Fact]
    public void ATimeThatHappensTwiceRunsOnceTheFirstTime()
    {
        // 1 November 2026: 02:00 EDT falls back to 01:00 EST, so 01:30 happens twice.
        var schedule = Daily(T(1, 30));
        var runs = schedule.Upcoming(Local(10, 31, 12), Eastern, 2);

        Assert.Equal([Utc(11, 1, 5, 30), Utc(11, 2, 6, 30)], runs);    // 01:30 EDT, then tomorrow
    }

    // ---------- Intervals ----------

    [Fact]
    public void EveryFifteenMinutesIsRealMinutesThroughTheSpringJump()
    {
        // 01:50 EST, ten minutes before the clocks jump.
        var runs = Every(15).Upcoming(Utc(3, 8, 6, 50), Eastern, 4);

        Assert.Equal([Utc(3, 8, 7, 0), Utc(3, 8, 7, 15), Utc(3, 8, 7, 30), Utc(3, 8, 7, 45)], runs);
        Assert.All(runs.Zip(runs.Skip(1)), pair => Assert.Equal(TimeSpan.FromMinutes(15), pair.Second - pair.First));
    }

    [Fact]
    public void EveryFifteenMinutesDoesNotSkipTheRepeatedHour()
    {
        // 01:50 EDT; the hour 01:00–02:00 then happens again in EST.
        var runs = Every(15).Upcoming(Utc(11, 1, 5, 50), Eastern, 9);

        Assert.Equal(9, runs.Count);
        Assert.Equal(Utc(11, 1, 6, 0), runs[0]);
        Assert.Equal(Utc(11, 1, 8, 0), runs[^1]);                        // every quarter of both hours
    }

    [Fact]
    public void EverySixHoursCountsFromMidnight()
    {
        var runs = Every(6 * 60).Upcoming(Local(9, 16, 1), Eastern, 4);
        Assert.Equal([Local(9, 16, 6), Local(9, 16, 12), Local(9, 16, 18), Local(9, 17, 0)], runs);
    }

    [Fact]
    public void HourlyRunsOnceInTheRepeatedHour()
    {
        // 00:30 EDT on the night the clocks fall back. 01:00 happens twice; it runs the first.
        var runs = Every(60).Upcoming(Utc(11, 1, 4, 30), Eastern, 3);
        Assert.Equal([Utc(11, 1, 5, 0), Utc(11, 1, 7, 0), Utc(11, 1, 8, 0)], runs);
    }

    [Fact]
    public void ARestartNeverShiftsAnInterval()
    {
        // Wherever you ask from, the grid is the same.
        var fromA = Every(20).NextAfter(Utc(9, 16, 10, 1), Eastern);
        var fromB = Every(20).NextAfter(Utc(9, 16, 10, 19), Eastern);
        Assert.Equal(Utc(9, 16, 10, 20), fromA);
        Assert.Equal(fromA, fromB);
    }

    // ---------- Monthly ----------

    [Fact]
    public void TheThirtyFirstRunsOnTheLastDayOfAShorterMonth()
    {
        var schedule = new ActionSchedule { Kind = ScheduleKind.Monthly, DayOfMonth = 31, Times = [T(3)] };

        Assert.Equal(Local(1, 31, 3), schedule.NextAfter(Local(1, 2, 0), Eastern));
        Assert.Equal(Local(2, 28, 3), schedule.NextAfter(Local(2, 1, 0), Eastern));
        Assert.Equal(Local(4, 30, 3), schedule.NextAfter(Local(4, 1, 0), Eastern));
        Assert.Contains("last day", schedule.Describe());
    }

    [Fact]
    public void MonthlyOnTheFirst()
    {
        var schedule = new ActionSchedule { Kind = ScheduleKind.Monthly, DayOfMonth = 1, Times = [T(3, 30)] };
        Assert.Equal(Local(10, 1, 3, 30), schedule.NextAfter(Local(9, 16, 0), Eastern));
        Assert.Equal("On the 1st of every month at 03:30", schedule.Describe());
    }

    // ---------- Cron ----------

    [Fact]
    public void ACronExpressionRunsWhereTheSameWeeklyScheduleWould()
    {
        var cron = Cron("0 4 * * 0");
        var weekly = Weekly([DayOfWeek.Sunday], T(4));

        Assert.Equal(weekly.Upcoming(Local(9, 16, 12), Eastern, 5), cron.Upcoming(Local(9, 16, 12), Eastern, 5));
        Assert.Equal("Sundays at 04:00", cron.Describe());
    }

    [Theory]
    [InlineData("0 4 * * 0", "Sundays at 04:00")]
    [InlineData("0 4 * * 7", "Sundays at 04:00")]
    [InlineData("0 4 * * sun", "Sundays at 04:00")]
    [InlineData("*/15 * * * *", "Every day, every 15 minutes")]
    [InlineData("30 3 1,15 * *", "On the 1st and 15th of the month at 03:30")]
    [InlineData("0 2 * * 1-5", "Weekdays at 02:00")]
    [InlineData("0 9-17 * * 1-5", "Weekdays, every hour on the hour, between 09:00 and 17:59")]
    [InlineData("0 */6 * * *", "Every day at 00:00, 06:00, 12:00 and 18:00")]
    [InlineData("0 0 1 jan,jul *", "On the 1st of the month, in Jan and Jul at 00:00")]
    [InlineData("0 4 13 * 5", "On the 13th of the month, and on Fridays at 04:00")]
    public void ACronExpressionIsSaidBackInWords(string cron, string words)
    {
        Assert.Null(Cron(cron).Problem());
        Assert.Equal(words, Cron(cron).Describe());
    }

    [Fact]
    public void BothDayFieldsWrittenOutMeansEitherDay()
    {
        // Friday the 13th of November 2026: both. Friday 6th: a Friday. Tuesday 13 October: the 13th.
        var cron = CronExpression.Parse("0 4 13 * 5", out _)!;
        Assert.True(cron.Matches(new DateOnly(2026, 11, 6)));
        Assert.True(cron.Matches(new DateOnly(2026, 10, 13)));
        Assert.False(cron.Matches(new DateOnly(2026, 11, 7)));
    }

    [Theory]
    [InlineData("0 4 * *", "five parts")]
    [InlineData("61 * * * *", "outside")]
    [InlineData("*/0 * * * *", "step")]
    [InlineData("0 5-1 * * *", "backwards")]
    [InlineData("0 4 * * funday", "not a number")]
    [InlineData("0,2 * * * *", "2 minutes apart")]
    [InlineData("* * * * *", "1 minute apart")]
    [InlineData("0 0 31 2 *", "never")]
    [InlineData("", "Write a cron expression")]
    public void ABadCronExpressionSaysWhy(string cron, string why)
    {
        Assert.Contains(why, Cron(cron).Problem());
    }

    [Fact]
    public void ACronOnTheSpringJumpRunsJustAfterIt()
    {
        var next = Cron("30 2 * * *").NextAfter(Local(3, 7, 12), Eastern);
        Assert.Equal(Utc(3, 8, 7, 30), next);
    }

    // ---------- What can be saved ----------

    [Fact]
    public void NothingRunsMoreOftenThanEveryFiveMinutes()
    {
        Assert.Contains("too often", Every(4).Problem());
        Assert.Null(Every(5).Problem());
        Assert.Contains("3 minutes apart", Daily(T(4), T(4, 3)).Problem());
        // Across midnight too: 23:58 and 00:01 are three minutes apart on consecutive days.
        Assert.Contains("apart", Daily(T(23, 58), T(0, 1)).Problem());
        Assert.Contains("Pick at least one day", Weekly([], T(4)).Problem());
        Assert.Contains("at least one time", Daily().Problem());
    }

    [Fact]
    public void TimesAreStoredShortAndReadBackForgivingly()
    {
        Assert.Equal("04:00,16:30", ActionSchedule.StoredTimes([T(16, 30), T(4)]));
        Assert.Equal([T(4), T(16, 30)], ActionSchedule.ParseTimes(" 16:30, nonsense ,04:00,16:30"));
        Assert.Equal(ScheduleKind.Weekly, ActionSchedule.ParseKind("hourly-ish"));
        Assert.Equal(ScheduleKind.Cron, ActionSchedule.ParseKind("cron"));
    }

    // ---------- Missed runs ----------

    private static ScheduledAction Action(ActionSchedule schedule, DateTimeOffset covered) => new()
    {
        Name = "Restart plex",
        TargetConnectionId = "docker",
        Container = "plex",
        Schedule = schedule,
        CoveredUntil = covered,
    };

    [Fact]
    public void ARunMissedWithinTheGraceRunsOnceLate()
    {
        var action = Action(Weekly([DayOfWeek.Sunday], T(4)), Local(9, 20, 3));
        var decision = ScheduledRules.Decide(action, Local(9, 20, 4, 30), Eastern, TimeSpan.FromHours(1));

        Assert.Equal(ScheduledRules.Step.Run, decision.Step);
        Assert.Equal(Local(9, 20, 4), decision.Occurrence);
        Assert.True(decision.Late);
    }

    [Fact]
    public void ARunMissedLongerAgoThanTheGraceIsMissedNotRun()
    {
        var action = Action(Weekly([DayOfWeek.Sunday], T(4)), Local(9, 20, 3));
        var decision = ScheduledRules.Decide(action, Local(9, 20, 5, 30), Eastern, TimeSpan.FromHours(1));

        Assert.Equal(ScheduledRules.Step.Missed, decision.Step);
        Assert.Equal(Local(9, 20, 4), decision.Occurrence);
    }

    [Fact]
    public void AWeekOfMissedRunsIsOneRunNotSeven()
    {
        // Down for a week; back ten minutes after today's run was due.
        var action = Action(Daily(T(4)), Local(9, 13, 12));
        var decision = ScheduledRules.Decide(action, Local(9, 20, 4, 10), Eastern, TimeSpan.FromHours(1));

        Assert.Equal(ScheduledRules.Step.Run, decision.Step);
        Assert.Equal(Local(9, 20, 4), decision.Occurrence);
        Assert.True(decision.Late);
    }

    [Fact]
    public void OnTimeIsNotLateAndNotDueIsWaiting()
    {
        var action = Action(Daily(T(4)), Local(9, 19, 4));

        var onTime = ScheduledRules.Decide(action, Local(9, 20, 4, 0), Eastern, TimeSpan.FromHours(1));
        Assert.Equal(ScheduledRules.Step.Run, onTime.Step);
        Assert.False(onTime.Late);

        var early = ScheduledRules.Decide(action, Local(9, 20, 3, 59), Eastern, TimeSpan.FromHours(1));
        Assert.Equal(ScheduledRules.Step.Wait, early.Step);
        Assert.Equal(Local(9, 20, 4), early.Next);
    }

    [Fact]
    public void AnActionThatIsOffOrIncompleteNeverRuns()
    {
        var action = Action(Daily(T(4)), Local(9, 19, 4));
        Assert.Equal(ScheduledRules.Step.Wait, ScheduledRules.Decide(action with { Enabled = false }, Local(9, 20, 4), Eastern, TimeSpan.FromHours(1)).Step);
        Assert.Equal(ScheduledRules.Step.Wait, ScheduledRules.Decide(action with { Container = "" }, Local(9, 20, 4), Eastern, TimeSpan.FromHours(1)).Step);
    }
}
