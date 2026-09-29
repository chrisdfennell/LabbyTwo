using LabbyTwo.Core;

namespace LabbyTwo.Tests;

/// <summary>
/// When a mute window is open, and what it holds back. The failures worth fearing are the
/// same as for the weekly summary — a window an hour off after the clocks change, one that
/// forgets its second half after midnight — plus one of its own: muting more than was asked,
/// which is how somebody misses the alert that mattered.
/// </summary>
public sealed class MuteWindowTests
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

    /// <summary>A wall-clock time in the test zone, as an instant. September 2026: the 20th is a Sunday.</summary>
    private static DateTimeOffset Local(int month, int day, int hour, int minute = 0) =>
        WeeklySchedule.At(new DateOnly(2026, month, day), new TimeOnly(hour, minute), Eastern);

    private static MuteWindow Window(TimeOnly start, TimeOnly end, params DayOfWeek[] days) => new()
    {
        Name = "Test",
        Days = days,
        Start = start,
        End = end,
    };

    private static MuteWindow Scrub => Window(new TimeOnly(1, 0), new TimeOnly(5, 0), DayOfWeek.Sunday);

    [Fact]
    public void ItIsOpenFromItsStartUpToButNotIncludingItsEnd()
    {
        Assert.False(Scrub.IsActive(Local(9, 20, 0, 59), Eastern));
        Assert.True(Scrub.IsActive(Local(9, 20, 1, 0), Eastern));
        Assert.True(Scrub.IsActive(Local(9, 20, 4, 59), Eastern));
        Assert.False(Scrub.IsActive(Local(9, 20, 5, 0), Eastern));
    }

    [Fact]
    public void ItOnlyOpensOnItsDays()
    {
        Assert.False(Scrub.IsActive(Local(9, 21, 3), Eastern)); // Monday
        Assert.False(Scrub.IsActive(Local(9, 19, 3), Eastern)); // Saturday
        Assert.True(Scrub.IsActive(Local(9, 27, 3), Eastern));  // the next Sunday
    }

    [Fact]
    public void AWindowPastMidnightBelongsToTheDayItStarts()
    {
        // Friday 23:00 to 02:00: Saturday's small hours are Friday's window.
        var late = Window(new TimeOnly(23, 0), new TimeOnly(2, 0), DayOfWeek.Friday);

        Assert.True(late.IsActive(Local(9, 18, 23, 30), Eastern));  // Friday night
        Assert.True(late.IsActive(Local(9, 19, 1, 30), Eastern));   // Saturday morning, still Friday's
        Assert.False(late.IsActive(Local(9, 19, 2, 0), Eastern));   // ended
        Assert.False(late.IsActive(Local(9, 19, 23, 30), Eastern)); // Saturday night is not chosen
        Assert.False(late.IsActive(Local(9, 18, 1, 30), Eastern));  // Friday morning is Thursday's
    }

    [Fact]
    public void TheSameTimeAtBothEndsIsAWholeDay()
    {
        var day = Window(new TimeOnly(0, 0), new TimeOnly(0, 0), DayOfWeek.Wednesday);

        Assert.True(day.IsActive(Local(9, 16, 0, 0), Eastern));
        Assert.True(day.IsActive(Local(9, 16, 23, 59), Eastern));
        Assert.False(day.IsActive(Local(9, 17, 0, 0), Eastern));
        Assert.Equal("Wednesdays all day from 00:00", day.Describe());
    }

    [Fact]
    public void OnTheSpringNightTheWindowEndsWhenTheClockSaysSo()
    {
        // 8 March 2026: 02:00 becomes 03:00. 01:00–05:00 is three real hours, ending at
        // 05:00 daylight time, not at 06:00.
        var (start, end) = Scrub.Occurrence(new DateOnly(2026, 3, 8), Eastern);

        Assert.Equal(new DateTimeOffset(2026, 3, 8, 1, 0, 0, TimeSpan.FromHours(-5)), start);
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 5, 0, 0, TimeSpan.FromHours(-4)), end);
        Assert.Equal(TimeSpan.FromHours(3), end - start);
        Assert.True(Scrub.IsActive(new DateTimeOffset(2026, 3, 8, 4, 59, 0, TimeSpan.FromHours(-4)), Eastern));
        Assert.False(Scrub.IsActive(new DateTimeOffset(2026, 3, 8, 5, 0, 0, TimeSpan.FromHours(-4)), Eastern));
    }

    [Fact]
    public void AStartInTheSkippedHourOpensJustAfterTheJump()
    {
        var skipped = Window(new TimeOnly(2, 30), new TimeOnly(4, 0), DayOfWeek.Sunday);

        var (start, _) = skipped.Occurrence(new DateOnly(2026, 3, 8), Eastern);

        Assert.Equal(new DateTimeOffset(2026, 3, 8, 3, 30, 0, TimeSpan.FromHours(-4)), start);
    }

    [Fact]
    public void OnTheAutumnNightTheRepeatedHourIsInsideTheWindow()
    {
        // 1 November 2026: 02:00 goes back to 01:00. 01:00–05:00 is five real hours, and
        // both 01:30s are inside it.
        var (start, end) = Scrub.Occurrence(new DateOnly(2026, 11, 1), Eastern);

        Assert.Equal(TimeSpan.FromHours(5), end - start);
        Assert.True(Scrub.IsActive(new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.FromHours(-4)), Eastern));
        Assert.True(Scrub.IsActive(new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.FromHours(-5)), Eastern));
        Assert.False(Scrub.IsActive(new DateTimeOffset(2026, 11, 1, 5, 0, 0, TimeSpan.FromHours(-5)), Eastern));
    }

    [Fact]
    public void AWindowThatWrapsMidnightAcrossAClockChangeKeepsItsWallClockEnds()
    {
        // Saturday 7 March 23:00 standard time to Sunday 03:00 daylight time: three hours.
        var late = Window(new TimeOnly(23, 0), new TimeOnly(3, 0), DayOfWeek.Saturday);
        var (start, end) = late.Occurrence(new DateOnly(2026, 3, 7), Eastern);

        Assert.Equal(TimeSpan.FromHours(3), end - start);
        Assert.True(late.IsActive(new DateTimeOffset(2026, 3, 8, 2, 30, 0, TimeSpan.FromHours(-4)), Eastern));
    }

    [Fact]
    public void NextStartIsTheComingOpening()
    {
        Assert.Equal(Local(9, 20, 1), Scrub.NextStart(Local(9, 16, 12), Eastern));
        Assert.Equal(Local(9, 27, 1), Scrub.NextStart(Local(9, 20, 1), Eastern));
        Assert.Null((Scrub with { Enabled = false }).NextStart(Local(9, 16, 12), Eastern));
    }

    [Fact]
    public void AWindowThatIsOffNeverOpens()
    {
        Assert.False((Scrub with { Enabled = false }).IsActive(Local(9, 20, 3), Eastern));
    }

    // ---------- What it mutes ----------

    [Fact]
    public void EverythingMutesRulesAndDownAlerts()
    {
        var all = Scrub with { Scope = MuteScope.Everything };
        Assert.True(all.Covers("rule", "nas"));
        Assert.True(all.Covers(null, "nas"));
    }

    [Fact]
    public void AConnectionWindowMutesItsRulesAndItGoingDownButNothingElse()
    {
        var nas = Scrub with { Scope = MuteScope.Connections, Targets = ["nas"] };

        Assert.True(nas.Covers("any-rule", "nas"));
        Assert.True(nas.Covers(null, "nas"));
        Assert.False(nas.Covers("any-rule", "router"));
        Assert.False(nas.Covers(null, "router"));
    }

    [Fact]
    public void ARuleWindowMutesThatRuleOnEveryConnectionAndNeverADownAlert()
    {
        var cpu = Scrub with { Scope = MuteScope.Rules, Targets = ["cpu"] };

        Assert.True(cpu.Covers("cpu", "nas"));
        Assert.True(cpu.Covers("cpu", "router"));
        Assert.False(cpu.Covers("disk", "nas"));
        Assert.False(cpu.Covers(null, "nas"));
    }

    [Fact]
    public void MutingIsTheFirstOpenWindowThatCoversTheAlert()
    {
        var closed = Window(new TimeOnly(9, 0), new TimeOnly(10, 0), DayOfWeek.Sunday) with { Name = "Closed" };
        var other = Scrub with { Name = "Router only", Scope = MuteScope.Connections, Targets = ["router"] };
        var nas = Scrub with { Name = "NAS scrub", Scope = MuteScope.Connections, Targets = ["nas"] };
        var at = Local(9, 20, 2);

        Assert.Equal("NAS scrub", MuteWindow.Muting([closed, other, nas], "rule", "nas", at, Eastern)?.Name);
        Assert.Null(MuteWindow.Muting([closed, other, nas], "rule", "plex", at, Eastern));
        Assert.Null(MuteWindow.Muting([closed, other, nas], "rule", "nas", Local(9, 20, 6), Eastern));
    }

    // ---------- Saving ----------

    [Fact]
    public void AWindowThatWouldMuteNothingCannotBeSaved()
    {
        Assert.NotNull((Scrub with { Name = " " }).Problem());
        Assert.NotNull((Scrub with { Days = [] }).Problem());
        Assert.NotNull((Scrub with { Scope = MuteScope.Rules, Targets = [] }).Problem());
        Assert.Null((Scrub with { Scope = MuteScope.Rules, Targets = ["cpu"] }).Problem());
        Assert.Null(Scrub.Problem());
    }

    [Fact]
    public void DaysSurviveBeingStored()
    {
        var days = new[] { DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Friday };
        var stored = MuteWindow.StoredDays(days);

        Assert.Equal("mon,fri,sun", stored);
        Assert.Equal(days.OrderBy(d => d), MuteWindow.ParseDays(stored).OrderBy(d => d));
        Assert.Empty(MuteWindow.ParseDays("nonsense,,"));
    }

    [Fact]
    public void DaysAreSaidTheWayPeopleSayThem()
    {
        Assert.Equal("Every day", MuteWindow.DaysText(MuteWindow.Week.ToList()));
        Assert.Equal("Weekdays", MuteWindow.DaysText(MuteWindow.Weekdays.ToList()));
        Assert.Equal("Weekends", MuteWindow.DaysText([DayOfWeek.Sunday, DayOfWeek.Saturday]));
        Assert.Equal("Sundays", MuteWindow.DaysText([DayOfWeek.Sunday]));
        Assert.Equal("Mon, Wed, Sun", MuteWindow.DaysText([DayOfWeek.Sunday, DayOfWeek.Wednesday, DayOfWeek.Monday]));
        Assert.Equal("Fridays 23:00–02:00 (next day)",
            Window(new TimeOnly(23, 0), new TimeOnly(2, 0), DayOfWeek.Friday).Describe());
    }
}
