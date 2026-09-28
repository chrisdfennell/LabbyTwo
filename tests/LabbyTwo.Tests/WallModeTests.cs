using LabbyTwo.Core;

namespace LabbyTwo.Tests;

/// <summary>
/// Wall mode's decisions: what a bookmarked URL means, which tab comes next, and when a
/// touched screen carries on. All of it runs on a device nobody is watching, so a wrong
/// answer here is a wall stuck on one tab, or skipping one, until somebody happens to notice.
/// </summary>
public class WallModeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private static Tab T(string slug, bool enabled = true) => new() { Slug = slug, Name = slug, Enabled = enabled };

    private static Dictionary<string, string> Q(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    // ---- options ----

    [Fact]
    public void NothingSavedGivesTheBuiltInDefaults()
    {
        var options = WallOptions.From(new SettingsBag());

        Assert.Empty(options.Tabs);
        Assert.Equal(60, options.Seconds);
        Assert.Equal(60, options.ResumeSeconds);
        Assert.True(options.Overlay);
        Assert.False(options.Nav);
        Assert.Null(options.At);
    }

    [Fact]
    public void SavedSettingsAreRead()
    {
        var options = WallOptions.From(new SettingsBag
        {
            [WallOptions.TabsKey] = "home, media,home",
            [WallOptions.SecondsKey] = "30",
            [WallOptions.ResumeKey] = "0",
            [WallOptions.OverlayKey] = "false",
            [WallOptions.NavKey] = "true",
        });

        Assert.Equal(["home", "media"], options.Tabs);
        Assert.Equal(30, options.Seconds);
        Assert.Equal(0, options.ResumeSeconds);
        Assert.False(options.Overlay);
        Assert.True(options.Nav);
    }

    [Fact]
    public void TheQueryOverridesWhatIsSaved()
    {
        var saved = WallOptions.Default with { Tabs = ["a"], Seconds = 90 };

        var options = saved.WithQuery(Q(("tabs", "b,c"), ("seconds", "3"), ("resume", "20"),
            ("clock", "0"), ("nav", "yes"), ("at", "c")));

        Assert.Equal(["b", "c"], options.Tabs);
        Assert.Equal(3, options.Seconds);
        Assert.Equal(20, options.ResumeSeconds);
        Assert.False(options.Overlay);
        Assert.True(options.Nav);
        Assert.Equal("c", options.At);
    }

    [Fact]
    public void QueryKeysAreNotCaseSensitive()
    {
        var options = WallOptions.Default.WithQuery(Q(("Seconds", "15"), ("OVERLAY", "off")));

        Assert.Equal(15, options.Seconds);
        Assert.False(options.Overlay);
    }

    /// <summary>A typo in a bookmark keeps the configured wall, not a built-in one.</summary>
    [Fact]
    public void UnreadableQueryValuesKeepTheSavedOnes()
    {
        var saved = WallOptions.Default with { Seconds = 45, Overlay = false };

        var options = saved.WithQuery(Q(("seconds", "sixty"), ("overlay", "maybe"), ("nav", "")));

        Assert.Equal(45, options.Seconds);
        Assert.False(options.Overlay);
        Assert.False(options.Nav);
    }

    [Theory]
    [InlineData(0, WallOptions.MinSeconds)]
    [InlineData(1, WallOptions.MinSeconds)]
    [InlineData(-5, WallOptions.MinSeconds)]
    [InlineData(3, 3)]
    [InlineData(600, 600)]
    [InlineData(int.MaxValue, WallOptions.MaxSeconds)]
    public void SecondsAreKeptToSomethingReadable(int asked, int expected)
        => Assert.Equal(expected, WallOptions.Default.WithQuery(Q(("seconds", asked.ToString()))).Seconds);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(1, WallOptions.MinSeconds)]
    [InlineData(120, 120)]
    [InlineData(999999, WallOptions.MaxResumeSeconds)]
    public void ZeroResumeMeansNeverPauseAndTheRestIsClamped(int asked, int expected)
        => Assert.Equal(expected, WallOptions.ClampResume(asked));

    [Fact]
    public void TheBookmarkUrlReproducesTheOptions()
    {
        var options = new WallOptions(["home", "media"], 30, 0, false, true);

        var url = options.ToUrl();
        var query = url["wall?".Length..].Split('&')
            .Select(p => p.Split('='))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

        Assert.StartsWith("wall?", url);
        Assert.Equal(options, WallOptions.Default.WithQuery(query) with { Tabs = options.Tabs });
        Assert.Equal(["home", "media"], WallOptions.Default.WithQuery(query).Tabs);
    }

    [Fact]
    public void WallOnATabShowsThatTabAndKeepsTheRestOfTheQuery()
    {
        Assert.Equal("wall?tabs=media&seconds=10", WallOptions.UrlForTab("media", Q(("wall", "1"), ("seconds", "10"))));
    }

    [Fact]
    public void WallOnATabLetsAnExplicitTabListWin()
    {
        Assert.Equal("wall?tabs=a%2Cb", WallOptions.UrlForTab("media", Q(("wall", "1"), ("tabs", "a,b"))));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("on", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("wall", false)]
    public void OnlyAClearYesTurnsWallModeOn(string? value, bool expected)
        => Assert.Equal(expected, WallOptions.IsOn(value));

    // ---- which tabs, and which next ----

    [Fact]
    public void NoChoiceMeansEveryEnabledTabInOrder()
    {
        var rotation = WallRotation.Resolve([T("a"), T("b", enabled: false), T("c")], []);

        Assert.Equal(["a", "c"], rotation.Select(t => t.Slug));
    }

    [Fact]
    public void AChoiceIsShownInTheOrderItWasWritten()
    {
        var rotation = WallRotation.Resolve([T("a"), T("b"), T("c")], ["c", "A"]);

        Assert.Equal(["c", "a"], rotation.Select(t => t.Slug));
    }

    /// <summary>Disabling a tab takes it off every wall, even one that names it.</summary>
    [Fact]
    public void DisabledAndMissingTabsAreSkipped()
    {
        var rotation = WallRotation.Resolve([T("a"), T("b", enabled: false), T("c")], ["b", "gone", "c", "c"]);

        Assert.Equal(["c"], rotation.Select(t => t.Slug));
    }

    /// <summary>A renamed tab should not leave a bookmarked wall blank.</summary>
    [Fact]
    public void WhenNoneOfTheChosenTabsExistEveryEnabledTabIsShown()
    {
        var rotation = WallRotation.Resolve([T("a"), T("b")], ["renamed"]);

        Assert.Equal(["a", "b"], rotation.Select(t => t.Slug));
    }

    [Fact]
    public void NoEnabledTabsIsAnEmptyRotation()
    {
        Assert.Empty(WallRotation.Resolve([T("a", enabled: false)], []));
        Assert.Equal(-1, WallRotation.Step(0, 0, 1));
        Assert.Equal(-1, WallRotation.Reposition([], "a", 0));
    }

    [Fact]
    public void AWallStartsWhereTheUrlSaysOrAtTheBeginning()
    {
        IReadOnlyList<Tab> rotation = [T("a"), T("b"), T("c")];

        Assert.Equal(1, WallRotation.Start(rotation, "b"));
        Assert.Equal(1, WallRotation.Start(rotation, " B "));
        Assert.Equal(0, WallRotation.Start(rotation, "gone"));
        Assert.Equal(0, WallRotation.Start(rotation, null));
    }

    [Theory]
    [InlineData(3, 0, 1, 1)]
    [InlineData(3, 2, 1, 0)]
    [InlineData(3, 0, -1, 2)]
    [InlineData(3, 1, -1, 0)]
    [InlineData(1, 0, 1, 0)]
    [InlineData(1, 0, -1, 0)]
    [InlineData(4, 1, 6, 3)]
    public void SteppingWrapsAtBothEnds(int count, int index, int delta, int expected)
        => Assert.Equal(expected, WallRotation.Step(count, index, delta));

    [Fact]
    public void ATabThatMovedIsFollowed()
    {
        Assert.Equal(0, WallRotation.Reposition([T("c"), T("a"), T("b")], "c", 2));
    }

    /// <summary>
    /// Deleting the tab on screen shows the one that took its place, not the start of the
    /// rotation, and deleting the last one pulls back inside the list.
    /// </summary>
    [Fact]
    public void ATabThatWentAwayLeavesTheWallInTheSamePlace()
    {
        Assert.Equal(1, WallRotation.Reposition([T("a"), T("c"), T("d")], "b", 1));
        Assert.Equal(1, WallRotation.Reposition([T("a"), T("b")], "c", 2));
    }

    // ---- when it moves on ----

    [Fact]
    public void ATabStaysUpForItsTurn()
    {
        var timer = new WallTimer(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30), T0);

        Assert.False(timer.Due(T0.AddSeconds(59)));
        Assert.True(timer.Due(T0.AddSeconds(60)));

        timer.Stepped(T0.AddSeconds(60));
        Assert.False(timer.Due(T0.AddSeconds(119)));
        Assert.True(timer.Due(T0.AddSeconds(120)));
    }

    [Fact]
    public void ATouchPausesAndTheTabGetsAFullTurnOnceLeftAlone()
    {
        var timer = new WallTimer(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30), T0);

        timer.Interacted(T0.AddSeconds(50));
        Assert.True(timer.IsPaused(T0.AddSeconds(79)));
        Assert.False(timer.Due(T0.AddSeconds(79)));

        // Idle again at 80; the tab on screen then has its own full minute.
        Assert.False(timer.IsPaused(T0.AddSeconds(80)));
        Assert.False(timer.Due(T0.AddSeconds(139)));
        Assert.True(timer.Due(T0.AddSeconds(140)));
    }

    [Fact]
    public void EveryTouchExtendsThePause()
    {
        var timer = new WallTimer(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), T0);

        timer.Interacted(T0);
        timer.Interacted(T0.AddSeconds(25));

        Assert.True(timer.IsPaused(T0.AddSeconds(54)));
        Assert.False(timer.IsPaused(T0.AddSeconds(55)));
        Assert.True(timer.Due(T0.AddSeconds(65)));
    }

    /// <summary>
    /// Stepping by hand during a pause does not end the pause: the person is still there,
    /// looking at the tab they chose.
    /// </summary>
    [Fact]
    public void SteppingWhilePausedWaitsForThePauseToEnd()
    {
        var timer = new WallTimer(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), T0);

        timer.Interacted(T0);
        timer.Stepped(T0.AddSeconds(5));

        Assert.False(timer.Due(T0.AddSeconds(35)));
        Assert.True(timer.Due(T0.AddSeconds(40)));
    }

    [Fact]
    public void WithResumeOffATouchChangesNothing()
    {
        var timer = new WallTimer(TimeSpan.FromSeconds(10), TimeSpan.Zero, T0);

        timer.Interacted(T0.AddSeconds(5));

        Assert.False(timer.IsPaused(T0.AddSeconds(6)));
        Assert.True(timer.Due(T0.AddSeconds(10)));
    }
}
