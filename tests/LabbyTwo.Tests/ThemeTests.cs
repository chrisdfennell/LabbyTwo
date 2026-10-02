using LabbyTwo.Core;

namespace LabbyTwo.Tests;

/// <summary>
/// The theme model: parsing colours, contrast, the built-in palettes, the CSS a theme
/// becomes, the file format and base16.
/// </summary>
public class ThemeTests
{
    // ---- colours ------------------------------------------------------------------------

    [Theory]
    [InlineData("#4da3ff", "#4da3ff")]
    [InlineData("#4DA3FF", "#4da3ff")]
    [InlineData("#fff", "#ffffff")]
    [InlineData("  #000  ", "#000000")]
    [InlineData("#00000080", "rgba(0, 0, 0, 0.502)")]
    [InlineData("#0008", "rgba(0, 0, 0, 0.533)")]
    [InlineData("rgb(255, 92, 108)", "#ff5c6c")]
    [InlineData("rgb(255 92 108)", "#ff5c6c")]
    [InlineData("rgba(0, 0, 0, .6)", "rgba(0, 0, 0, 0.6)")]
    [InlineData("rgb(0 0 0 / 50%)", "rgba(0, 0, 0, 0.5)")]
    [InlineData("rgb(100%, 0%, 0%)", "#ff0000")]
    [InlineData("hsl(0, 100%, 50%)", "#ff0000")]
    [InlineData("hsl(120deg 100% 25%)", "#008000")]
    [InlineData("hsla(240, 100%, 50%, 0.25)", "rgba(0, 0, 255, 0.25)")]
    public void ColoursAreParsedAndRewrittenInOneCanonicalForm(string input, string css)
        => Assert.Equal(css, ThemeColor.Parse(input).Css);

    /// <summary>
    /// The whole defence: anything that is not a plain colour fails to parse, so it never
    /// becomes a string that could reach a stylesheet.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("red")]
    [InlineData("transparent")]
    [InlineData("#12")]
    [InlineData("#12345")]
    [InlineData("#gggggg")]
    [InlineData("#fff; background: url(https://evil.example/x)")]
    [InlineData("#fff}html{display:none")]
    [InlineData("var(--accent)")]
    [InlineData("color-mix(in srgb, red 50%, blue)")]
    [InlineData("url(javascript:alert(1))")]
    [InlineData("expression(alert(1))")]
    [InlineData("rgb(0,0,0);x:y")]
    [InlineData("rgb(300, 0, 0)")]
    [InlineData("rgb(0, 0, 0, 2)")]
    [InlineData("rgb(0, 0)")]
    [InlineData("rgb(0, 0, 0 / 1)")]
    [InlineData("hsl(0, 100, 50)")]
    [InlineData("rgb(NaN, 0, 0)")]
    [InlineData("</style><script>alert(1)</script>")]
    public void AnythingElseIsNotAColour(string? input) => Assert.False(ThemeColor.IsValid(input));

    // ---- contrast -----------------------------------------------------------------------

    [Fact]
    public void BlackOnWhiteIsTwentyOneToOne()
        => Assert.Equal(21, Contrast.Ratio(ThemeColor.Parse("#000"), ThemeColor.Parse("#fff")), 3);

    [Fact]
    public void AColourAgainstItselfIsOneToOne()
        => Assert.Equal(1, Contrast.Ratio(ThemeColor.Parse("#4da3ff"), ThemeColor.Parse("#4da3ff")), 6);

    /// <summary>The textbook values: #777 on white just misses AA; #767676 is the lightest grey that passes.</summary>
    [Theory]
    [InlineData("#777777", "#ffffff", 4.48)]
    [InlineData("#767676", "#ffffff", 4.54)]
    [InlineData("#0000ff", "#ffffff", 8.59)]
    [InlineData("#ff0000", "#ffffff", 4.00)]
    public void KnownPairsHaveTheirPublishedRatios(string fg, string bg, double expected)
        => Assert.Equal(expected, Contrast.Ratio(ThemeColor.Parse(fg), ThemeColor.Parse(bg)), 2);

    [Fact]
    public void TheRatioIsTheSameEitherWayRound()
    {
        var a = ThemeColor.Parse("#35d07f");
        var b = ThemeColor.Parse("#121821");
        Assert.Equal(Contrast.Ratio(a, b), Contrast.Ratio(b, a), 10);
    }

    [Theory]
    [InlineData(7.0, Contrast.Use.Text, Contrast.Level.AAA)]
    [InlineData(6.99, Contrast.Use.Text, Contrast.Level.AA)]
    [InlineData(4.5, Contrast.Use.Text, Contrast.Level.AA)]
    [InlineData(4.49, Contrast.Use.Text, Contrast.Level.Fail)]
    [InlineData(3.0, Contrast.Use.Graphic, Contrast.Level.AA)]
    [InlineData(2.99, Contrast.Use.Graphic, Contrast.Level.Fail)]
    [InlineData(4.5, Contrast.Use.Graphic, Contrast.Level.AAA)]
    public void GradesFollowWcag(double ratio, Contrast.Use use, Contrast.Level level)
        => Assert.Equal(level, Contrast.Grade(ratio, use));

    /// <summary>Translucent text is judged as the blend the eye sees, not as the opaque colour.</summary>
    [Fact]
    public void TranslucentColoursAreCompositedFirst()
    {
        var white = ThemeColor.Parse("#ffffff");
        var black = ThemeColor.Parse("#000000");
        var halfBlack = black.WithAlpha(0.5);
        var ratio = Contrast.Ratio(halfBlack, white, white);
        Assert.InRange(ratio, 3.9, 4.1); // ≈ #808080 on white
    }

    [Fact]
    public void TheCheckUsesWhatAnUnsetTokenFallsBackTo()
    {
        // No link of its own, so the link pair is judged as the accent.
        var results = Contrast.Check(BuiltInThemes.LabbyTwo.Dark!);
        var link = results.Single(r => r.Pair.Foreground == "link");
        var accent = Contrast.Ratio(ThemeColor.Parse("#4da3ff"), ThemeColor.Parse("#121821"));
        Assert.Equal(accent, link.Ratio, 6);
    }

    // ---- the built-ins ------------------------------------------------------------------

    public static TheoryData<string> BuiltInIds()
    {
        var data = new TheoryData<string>();
        foreach (var theme in BuiltInThemes.All)
            data.Add(theme.Id);
        return data;
    }

    [Theory]
    [MemberData(nameof(BuiltInIds))]
    public void BodyTextPassesAAInEveryBuiltIn(string id)
    {
        foreach (var (dark, variant) in BuiltInThemes.Find(id)!.Variants)
        {
            foreach (var result in Contrast.Check(variant).Where(r => r.Pair.Foreground == "text"))
                Assert.True(result.PassesAA, $"{id} {(dark ? "dark" : "light")}: {result.Pair.Label} is {result.RatioText}");
        }
    }

    /// <summary>A keyboard user has to be able to see where they are, in every theme.</summary>
    [Theory]
    [MemberData(nameof(BuiltInIds))]
    public void TheFocusRingIsVisibleInEveryBuiltIn(string id)
    {
        foreach (var (dark, variant) in BuiltInThemes.Find(id)!.Variants)
        {
            var page = variant.Effective("ink")!.Value;
            foreach (var surface in new[] { "panel", "ink", "panel-2" })
            {
                var ratio = Contrast.Ratio(variant.Effective("focus")!.Value, variant.Effective(surface)!.Value, page);
                Assert.True(ratio >= 3, $"{id} {(dark ? "dark" : "light")}: focus on {surface} is {ratio:0.00}:1");
            }
        }
    }

    [Theory]
    [MemberData(nameof(BuiltInIds))]
    public void SecondaryTextIsReadableInEveryBuiltIn(string id)
    {
        foreach (var (dark, variant) in BuiltInThemes.Find(id)!.Variants)
        {
            var muted = Contrast.Check(variant).Single(r => r.Pair.Foreground == "muted");
            Assert.True(muted.Ratio >= 4.5, $"{id} {(dark ? "dark" : "light")}: muted is {muted.RatioText}");
        }
    }

    /// <summary>High contrast means it: every reading pair clears AAA, in both ends.</summary>
    [Fact]
    public void HighContrastPassesAAAForEveryTextPair()
    {
        foreach (var (dark, variant) in BuiltInThemes.HighContrast.Variants)
        {
            foreach (var result in Contrast.Check(variant))
                Assert.True(result.Level == Contrast.Level.AAA, $"{(dark ? "dark" : "light")}: {result.Pair.Label} is {result.RatioText}");
        }
    }

    [Fact]
    public void BuiltInIdsAreUniqueAndThereAreTwelve()
    {
        Assert.Equal(BuiltInThemes.All.Count, BuiltInThemes.All.Select(t => t.Id).Distinct().Count());
        Assert.Equal(12, BuiltInThemes.All.Count);
        Assert.All(BuiltInThemes.All, t => Assert.True(t.BuiltIn));
    }

    [Fact]
    public void DarkOnlyThemesSayWhichEndTheyForce()
    {
        Assert.Equal("dark", BuiltInThemes.Dracula.ForcedMode);
        Assert.Equal("dark", BuiltInThemes.OneDark.ForcedMode);
        Assert.Null(BuiltInThemes.Nord.ForcedMode);
        // Asked for its light end, a dark-only theme gives the one it has.
        Assert.Same(BuiltInThemes.Dracula.Dark, BuiltInThemes.Dracula.Variant(dark: false));
    }

    // ---- the CSS ------------------------------------------------------------------------

    [Fact]
    public void ATwoEndedThemeWritesBothEndsInTheThreeStates()
    {
        var css = ThemeCss.Render(BuiltInThemes.Nord);
        Assert.Contains(":root, :root[data-theme=\"dark\"] {", css);
        Assert.Contains("@media (prefers-color-scheme: light)", css);
        Assert.Contains(":root[data-theme=\"light\"] {", css);
        Assert.Contains("--ink: #2e3440;", css);
        Assert.Contains("--ink: #e5e9f0;", css);
    }

    [Fact]
    public void AOneEndedThemeIsThatEndEverywhere()
    {
        var css = ThemeCss.Render(BuiltInThemes.Dracula);
        Assert.Contains(":root[data-theme=\"light\"]", css);
        Assert.DoesNotContain("color-scheme: light;", css);
        Assert.Equal(2, css.Split("--ink: #21222c;").Length - 1);
    }

    /// <summary>
    /// The light block restates every token, so nothing the dark end set leaks into the
    /// light page through the shared :root selector.
    /// </summary>
    [Fact]
    public void EveryBlockDeclaresEveryToken()
    {
        var light = BuiltInThemes.LabbyTwo.Light!;
        var declarations = ThemeCss.Declarations(light, isDark: false);
        foreach (var token in ThemeTokens.All)
            Assert.Contains(token.Property + ":", declarations);
        Assert.Contains("--link: var(--accent);", declarations);
        Assert.Contains("--selection: initial;", declarations);
    }

    [Fact]
    public void OnlyAPureBlackPageKeepsARaisedCardsBorder()
    {
        Assert.Contains("--raised-edge: var(--edge);", ThemeCss.Declarations(BuiltInThemes.Black.Dark!, true));
        Assert.Contains("--raised-edge: transparent;", ThemeCss.Declarations(BuiltInThemes.LabbyTwo.Dark!, true));
    }

    /// <summary>A theme's words never reach its CSS — not the name, the author or the id.</summary>
    [Fact]
    public void NothingATypedIntoAThemeReachesItsCss()
    {
        var hostile = new Theme("user-</style><script>", "x</style><script>alert(1)</script>", "}html{display:none",
            BuiltInThemes.LabbyTwo.Dark, null, "/* */ url(evil)");
        var css = ThemeCss.Render(hostile);
        Assert.DoesNotContain("<", css);
        Assert.DoesNotContain("script", css);
        Assert.DoesNotContain("evil", css);
        Assert.DoesNotContain("display:none", css);
    }

    [Fact]
    public void TheHashFollowsTheCss()
    {
        var a = ThemeCss.Hash(ThemeCss.Render(BuiltInThemes.Nord));
        Assert.Equal(a, ThemeCss.Hash(ThemeCss.Render(BuiltInThemes.Nord)));
        Assert.NotEqual(a, ThemeCss.Hash(ThemeCss.Render(BuiltInThemes.Dracula)));
        Assert.Equal(12, a.Length);
    }

    [Fact]
    public void ChartColoursAreTokensAndComeRoundAgainMixed()
    {
        Assert.Equal("var(--chart-1)", ThemeTokens.ChartColour(0));
        Assert.Equal("var(--chart-8)", ThemeTokens.ChartColour(7));
        Assert.Equal("color-mix(in srgb, var(--chart-1) 60%, var(--text))", ThemeTokens.ChartColour(8));
    }

    [Fact]
    public void TheFirstChartLineFollowsTheAccentUnlessAThemeSaysOtherwise()
    {
        Assert.Equal(ThemeColor.Parse("#4da3ff"), BuiltInThemes.LabbyTwo.ChartPalette(dark: true)[0]);
        Assert.Equal(ThemeColor.Parse("#bd93f9"), BuiltInThemes.Dracula.ChartPalette(dark: true)[0]);
        Assert.Equal(ThemeColor.Parse("#ffb86c"), BuiltInThemes.Dracula.ChartPalette(dark: true)[1]);
    }
}
