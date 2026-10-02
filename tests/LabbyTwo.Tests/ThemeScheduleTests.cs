using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;

namespace LabbyTwo.Tests;

/// <summary>
/// The light/dark decision for "follow the sun" and "on a schedule": pure arithmetic on a
/// place, a zone and a clock, so the cases that only happen twice a year or north of the
/// Arctic Circle are pinned here rather than met on somebody's wall.
/// </summary>
public sealed class ThemeScheduleTests
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

    /// <summary>Central European: UTC+1, +2 from the last Sunday of March to the last of October.</summary>
    private static readonly TimeZoneInfo Central = TimeZoneInfo.CreateCustomTimeZone(
        "Test/Central", TimeSpan.FromHours(1), "Test Central", "Test Central", "Test Central Summer",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday)),
        ]);

    /// <summary>Sydney in its summer: well east of Greenwich, so its sunrise is the previous day in UTC.</summary>
    private static readonly TimeZoneInfo SydneySummer = TimeZoneInfo.CreateCustomTimeZone("Test/Sydney", TimeSpan.FromHours(11), "Sydney", "AEDT");

    private static readonly HomeLocation NewYork = new(40.7128, -74.0060, "New York");
    private static readonly HomeLocation Tromso = new(69.6492, 18.9553, "Tromsø");
    private static readonly HomeLocation Sydney = new(-33.8688, 151.2093, "Sydney");

    private static readonly ThemeTimes NoOffsets = ThemeTimes.Default;

    private static DateTimeOffset At(int year, int month, int day, int hour, int minute, TimeZoneInfo zone) =>
        WeeklySchedule.At(new DateOnly(year, month, day), new TimeOnly(hour, minute), zone);

    private static DateTime Local(DateTimeOffset at, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(at, zone).DateTime;

    private static ModeDecision Sun(HomeLocation home, DateTimeOffset now, TimeZoneInfo zone, ThemeTimes? times = null) =>
        ThemeSchedule.Decide(ThemeModes.Sun, times ?? NoOffsets, home, zone, now);

    private static ModeDecision Schedule(string from, string to, DateTimeOffset now, TimeZoneInfo zone) =>
        ThemeSchedule.Decide(ThemeModes.Schedule,
            NoOffsets with { LightFrom = TimeOnly.Parse(from), LightTo = TimeOnly.Parse(to) },
            HomeLocation.None, zone, now);

    // ---- the fixed modes ----------------------------------------------------------------

    [Theory]
    [InlineData(ThemeModes.Dark, "dark")]
    [InlineData(ThemeModes.Light, "light")]
    [InlineData(ThemeModes.Auto, null)]
    [InlineData("nonsense", null)]
    public void TheFixedModesNeverSwitch(string mode, string? attribute)
    {
        var decision = ThemeSchedule.Decide(mode, NoOffsets, NewYork, Eastern, At(2026, 6, 21, 12, 0, Eastern));
        Assert.Equal(attribute, decision.Attribute);
        Assert.Null(decision.NextSwitch);
        Assert.Null(decision.Problem);
    }

    /// <summary>With nowhere set there is no sun to follow: it says how to fix that and follows the device.</summary>
    [Fact]
    public void FollowingTheSunWithNoHomeFallsBackToTheDeviceAndSaysWhy()
    {
        var decision = Sun(HomeLocation.None, At(2026, 6, 21, 12, 0, Eastern), Eastern);
        Assert.Null(decision.Attribute);
        Assert.Null(decision.NextSwitch);
        Assert.Contains("Settings → Where you are", decision.Problem);
        Assert.Equal(decision.Problem, ThemeSchedule.Describe(decision, Eastern, DateTimeOffset.UtcNow));
    }

    // ---- the sun --------------------------------------------------------------------------

    [Fact]
    public void MiddayInSummerIsLightUntilSunset()
    {
        var decision = Sun(NewYork, At(2026, 6, 21, 12, 0, Eastern), Eastern);

        Assert.Equal("light", decision.Attribute);
        Assert.Equal("dark", decision.NextAttribute);
        Assert.Equal("sunset", decision.NextReason);
        // New York's midsummer sunset is 20:31 EDT.
        var sunset = Local(decision.NextSwitch!.Value, Eastern);
        Assert.Equal(new DateTime(2026, 6, 21), sunset.Date);
        Assert.InRange(sunset.TimeOfDay, new TimeSpan(20, 20, 0), new TimeSpan(20, 40, 0));
    }

    [Fact]
    public void LateEveningIsDarkUntilTomorrowsSunrise()
    {
        var decision = Sun(NewYork, At(2026, 6, 21, 23, 0, Eastern), Eastern);

        Assert.Equal("dark", decision.Attribute);
        Assert.Equal("sunrise", decision.NextReason);
        var sunrise = Local(decision.NextSwitch!.Value, Eastern);
        Assert.Equal(new DateTime(2026, 6, 22), sunrise.Date);
        Assert.InRange(sunrise.TimeOfDay, new TimeSpan(5, 15, 0), new TimeSpan(5, 35, 0));
    }

    [Fact]
    public void EarlyMorningIsDarkUntilTodaysSunrise()
    {
        var decision = Sun(NewYork, At(2026, 6, 21, 3, 0, Eastern), Eastern);
        Assert.Equal("dark", decision.Attribute);
        Assert.Equal(new DateTime(2026, 6, 21), Local(decision.NextSwitch!.Value, Eastern).Date);
    }

    /// <summary>"Dark half an hour before sunset" moves the switch exactly that far.</summary>
    [Fact]
    public void ASunsetOffsetMovesTheSwitchByExactlyThatMuch()
    {
        var now = At(2026, 6, 21, 12, 0, Eastern);
        var plain = Sun(NewYork, now, Eastern).NextSwitch!.Value;
        var early = Sun(NewYork, now, Eastern, NoOffsets with { SunsetOffsetMinutes = -30 }).NextSwitch!.Value;
        Assert.Equal(TimeSpan.FromMinutes(30), plain - early);
    }

    [Fact]
    public void ASunriseOffsetKeepsItDarkAfterTheSunIsUp()
    {
        var sunrise = Sun(NewYork, At(2026, 6, 21, 3, 0, Eastern), Eastern).NextSwitch!.Value;
        var times = NoOffsets with { SunriseOffsetMinutes = 20 };

        var justAfter = Sun(NewYork, sunrise.AddMinutes(10), Eastern, times);
        Assert.Equal("dark", justAfter.Attribute);
        Assert.Equal(sunrise.AddMinutes(20), justAfter.NextSwitch);

        Assert.Equal("light", Sun(NewYork, sunrise.AddMinutes(21), Eastern, times).Attribute);
    }

    /// <summary>The switch is a boundary: dark up to it, light from it.</summary>
    [Fact]
    public void TheSwitchItselfIsTheFirstMomentOfTheNewEnd()
    {
        var sunrise = Sun(NewYork, At(2026, 6, 21, 3, 0, Eastern), Eastern).NextSwitch!.Value;
        Assert.Equal("dark", Sun(NewYork, sunrise.AddSeconds(-1), Eastern).Attribute);
        Assert.Equal("light", Sun(NewYork, sunrise, Eastern).Attribute);
    }

    /// <summary>
    /// The morning the clocks go forward, sunrise is an hour later on the clock but about
    /// the same moment as the day before — the zone, not the sun, moved.
    /// </summary>
    [Fact]
    public void OnTheDayTheClocksGoForwardSunriseIsAnHourLaterOnTheClock()
    {
        var saturday = Sun(NewYork, At(2026, 3, 7, 1, 0, Eastern), Eastern).NextSwitch!.Value;
        var sunday = Sun(NewYork, At(2026, 3, 8, 1, 0, Eastern), Eastern).NextSwitch!.Value;

        Assert.InRange((sunday - saturday - TimeSpan.FromDays(1)).TotalMinutes, -3, 0);
        var clock = Local(sunday, Eastern).TimeOfDay - Local(saturday, Eastern).TimeOfDay;
        Assert.InRange(clock.TotalMinutes, 57, 60);
    }

    [Fact]
    public void OnTheDayTheClocksGoBackSunsetIsAnHourEarlierOnTheClock()
    {
        var saturday = Sun(NewYork, At(2026, 10, 31, 12, 0, Eastern), Eastern).NextSwitch!.Value;
        var sunday = Sun(NewYork, At(2026, 11, 1, 12, 0, Eastern), Eastern).NextSwitch!.Value;

        var clock = Local(saturday, Eastern).TimeOfDay - Local(sunday, Eastern).TimeOfDay;
        Assert.InRange(clock.TotalMinutes, 60, 63);
    }

    /// <summary>Far east of Greenwich the morning is the previous day in UTC; it still lands on the local day.</summary>
    [Fact]
    public void ASouthernSummerFarEastOfGreenwichLandsOnTheLocalDay()
    {
        var morning = Sun(Sydney, At(2026, 12, 21, 3, 0, SydneySummer), SydneySummer);
        Assert.Equal("dark", morning.Attribute);
        var sunrise = Local(morning.NextSwitch!.Value, SydneySummer);
        Assert.Equal(new DateTime(2026, 12, 21), sunrise.Date);
        Assert.InRange(sunrise.TimeOfDay, new TimeSpan(5, 30, 0), new TimeSpan(5, 55, 0));

        var noon = Sun(Sydney, At(2026, 12, 21, 12, 0, SydneySummer), SydneySummer);
        Assert.Equal("light", noon.Attribute);
        Assert.InRange(Local(noon.NextSwitch!.Value, SydneySummer).TimeOfDay, new TimeSpan(19, 55, 0), new TimeSpan(20, 15, 0));
    }

    /// <summary>
    /// A summer with no sunset: light, through midnight, and the next switch is the first
    /// real sunset weeks away — not a fake one at the end of the day.
    /// </summary>
    [Fact]
    public void APolarSummerIsLightUntilTheSunFirstSetsInJuly()
    {
        foreach (var hour in new[] { 12, 0, 23 })
        {
            var decision = Sun(Tromso, At(2026, 6, 21, hour, 30, Central), Central);
            Assert.Equal("light", decision.Attribute);
            Assert.Equal("sunset", decision.NextReason);
            var first = Local(decision.NextSwitch!.Value, Central);
            Assert.Equal(7, first.Month);
            Assert.InRange(first.Day, 15, 31);
        }
    }

    [Fact]
    public void APolarWinterIsDarkUntilTheSunComesBackInJanuary()
    {
        var decision = Sun(Tromso, At(2026, 12, 21, 12, 0, Central), Central);
        Assert.Equal("dark", decision.Attribute);
        Assert.Equal("sunrise", decision.NextReason);
        var back = Local(decision.NextSwitch!.Value, Central);
        Assert.Equal(2027, back.Year);
        Assert.Equal(1, back.Month);
        Assert.InRange(back.Day, 5, 25);

        // A switch weeks away is described with its date.
        Assert.Contains("on ", ThemeSchedule.Describe(decision, Central, At(2026, 12, 21, 12, 0, Central)));
    }

    /// <summary>A short day near the Arctic Circle that the offsets swallow whole stays dark.</summary>
    [Fact]
    public void OffsetsThatEatAShortWinterDayLeaveItDark()
    {
        var nearCircle = new HomeLocation(66.0, 18.0, "");
        var times = NoOffsets with { SunriseOffsetMinutes = 90, SunsetOffsetMinutes = -90 };

        var noon = Sun(nearCircle, At(2026, 12, 21, 12, 0, Central), Central, times);
        Assert.Equal("dark", noon.Attribute);
        Assert.NotNull(noon.NextSwitch);
        Assert.True(noon.NextSwitch > At(2026, 12, 22, 0, 0, Central));

        // Without them, the same moment is in the couple of hours of daylight it has.
        Assert.Equal("light", Sun(nearCircle, At(2026, 12, 21, 12, 0, Central), Central).Attribute);
    }

    // ---- the schedule -------------------------------------------------------------------

    [Fact]
    public void AScheduleIsLightInsideItsWindowAndDarkOutside()
    {
        var day = Schedule("07:00", "19:00", At(2026, 6, 1, 12, 0, Eastern), Eastern);
        Assert.Equal("light", day.Attribute);
        Assert.Equal(At(2026, 6, 1, 19, 0, Eastern), day.NextSwitch);
        Assert.Equal("schedule", day.NextReason);

        var night = Schedule("07:00", "19:00", At(2026, 6, 1, 19, 0, Eastern), Eastern);
        Assert.Equal("dark", night.Attribute);
        Assert.Equal(At(2026, 6, 2, 7, 0, Eastern), night.NextSwitch);
    }

    /// <summary>Light from 22:00 until 06:00, for somebody on nights.</summary>
    [Fact]
    public void AWindowThatRunsPastMidnightEndsTheNextMorning()
    {
        var late = Schedule("22:00", "06:00", At(2026, 6, 1, 23, 0, Eastern), Eastern);
        Assert.Equal("light", late.Attribute);
        Assert.Equal(At(2026, 6, 2, 6, 0, Eastern), late.NextSwitch);

        var small = Schedule("22:00", "06:00", At(2026, 6, 2, 3, 0, Eastern), Eastern);
        Assert.Equal("light", small.Attribute);
        Assert.Equal(At(2026, 6, 2, 6, 0, Eastern), small.NextSwitch);

        var noon = Schedule("22:00", "06:00", At(2026, 6, 2, 12, 0, Eastern), Eastern);
        Assert.Equal("dark", noon.Attribute);
        Assert.Equal(At(2026, 6, 2, 22, 0, Eastern), noon.NextSwitch);
    }

    /// <summary>07:00 means 07:00 on the wall clock either side of the change, so the instant moves an hour.</summary>
    [Fact]
    public void AScheduleKeepsItsClockTimeAcrossTheChange()
    {
        var before = Schedule("07:00", "19:00", At(2026, 3, 7, 1, 0, Eastern), Eastern).NextSwitch!.Value;
        var after = Schedule("07:00", "19:00", At(2026, 3, 8, 1, 0, Eastern), Eastern).NextSwitch!.Value;

        Assert.Equal(new DateTimeOffset(2026, 3, 7, 12, 0, 0, TimeSpan.Zero), before);
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 11, 0, 0, TimeSpan.Zero), after);
    }

    /// <summary>A time the clocks skip happens just after the jump; a time they repeat, the first time.</summary>
    [Fact]
    public void ASkippedOrRepeatedTimeIsTakenTheWayEveryOtherScheduleTakesIt()
    {
        var skipped = Schedule("02:30", "12:00", At(2026, 3, 8, 0, 30, Eastern), Eastern);
        Assert.Equal("dark", skipped.Attribute);
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 30, 0, TimeSpan.Zero), skipped.NextSwitch); // 03:30 EDT

        var repeated = Schedule("01:30", "12:00", At(2026, 11, 1, 0, 30, Eastern), Eastern);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), repeated.NextSwitch); // the EDT one
    }

    [Fact]
    public void TheSameTimeTwiceIsNoWindowAndStaysDark()
    {
        var decision = Schedule("07:00", "07:00", At(2026, 6, 1, 12, 0, Eastern), Eastern);
        Assert.Equal("dark", decision.Attribute);
        Assert.Null(decision.NextSwitch);
    }

    [Fact]
    public void TheDescriptionSaysWhatHappensNextAndWhy()
    {
        var now = At(2026, 6, 1, 12, 0, Eastern);
        Assert.Equal("Light now — switches to dark at 19:00.",
            ThemeSchedule.Describe(Schedule("07:00", "19:00", now, Eastern), Eastern, now));

        var evening = At(2026, 6, 1, 20, 0, Eastern);
        Assert.Equal("Dark now — switches to light tomorrow at 07:00.",
            ThemeSchedule.Describe(Schedule("07:00", "19:00", evening, Eastern), Eastern, evening));

        var sun = Sun(NewYork, now, Eastern);
        Assert.Matches(@"^Light now — switches to dark at 20:\d\d \(sunset\)\.$", ThemeSchedule.Describe(sun, Eastern, now));

        Assert.Equal("Dark now.", ThemeSchedule.Describe(new ModeDecision("dark"), Eastern, now));
    }

    // ---- settings -------------------------------------------------------------------------

    [Fact]
    public void TimesReadBackFromSettingsAndBadOnesAreTheDefaults()
    {
        var times = ThemeTimes.From(new SettingsBag
        {
            [ThemeTimes.SunriseOffsetKey] = "15",
            [ThemeTimes.SunsetOffsetKey] = "-9999",
            [ThemeTimes.LightFromKey] = "06:45",
            [ThemeTimes.LightToKey] = "teatime",
        });

        Assert.Equal(15, times.SunriseOffsetMinutes);
        Assert.Equal(-ThemeTimes.MaxOffsetMinutes, times.SunsetOffsetMinutes);
        Assert.Equal(new TimeOnly(6, 45), times.LightFrom);
        Assert.Equal(ThemeTimes.Default.LightTo, times.LightTo);
    }

    [Theory]
    [InlineData("/", ThemeSurface.Dashboard)]
    [InlineData("/t/home", ThemeSurface.Dashboard)]
    [InlineData("/wall", ThemeSurface.Wall)]
    [InlineData("/WALL", ThemeSurface.Wall)]
    [InlineData("/wallpaper", ThemeSurface.Dashboard)]
    [InlineData("/m", ThemeSurface.Phone)]
    [InlineData("/media", ThemeSurface.Dashboard)]
    [InlineData("/family/abc", ThemeSurface.Family)]
    [InlineData(null, ThemeSurface.Dashboard)]
    public void TheAddressSaysWhichScreenItIs(string? path, ThemeSurface surface) =>
        Assert.Equal(surface, ThemeService.SurfaceForPath(path));

    [Fact]
    public void AScreenWearsTheDashboardsThemeAndModeUntilGivenItsOwn()
    {
        var settings = new SettingsBag
        {
            [ThemeService.ThemeIdKey] = "nord",
            [Appearance.ThemeKey] = ThemeModes.Sun,
        };

        foreach (var surface in ThemeService.OwnSurfaces)
        {
            Assert.Equal("nord", ThemeService.ThemeIdFor(settings, surface));
            Assert.Equal(ThemeModes.Sun, ThemeService.ModeFor(settings, surface));
        }

        settings[ThemeService.WallThemeKey] = "black";
        settings[ThemeService.WallModeKey] = ThemeModes.Dark;
        settings[ThemeService.PhoneModeKey] = ThemeModes.Schedule;
        settings[ThemeService.FamilyModeKey] = "from-the-future";

        Assert.Equal("black", ThemeService.ThemeIdFor(settings, ThemeSurface.Wall));
        Assert.Equal(ThemeModes.Dark, ThemeService.ModeFor(settings, ThemeSurface.Wall));
        Assert.Equal("nord", ThemeService.ThemeIdFor(settings, ThemeSurface.Phone));
        Assert.Equal(ThemeModes.Schedule, ThemeService.ModeFor(settings, ThemeSurface.Phone));
        // A mode this version does not know follows the device rather than guessing.
        Assert.Equal(ThemeModes.Auto, ThemeService.ModeFor(settings, ThemeSurface.Family));
        // The dashboard is untouched by any of it.
        Assert.Equal("nord", ThemeService.ThemeIdFor(settings));
        Assert.Equal(ThemeModes.Sun, ThemeService.ModeFor(settings));
    }
}
