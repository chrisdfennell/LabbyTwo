using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Tests;

/// <summary>
/// The appearance record, and mostly the two free-text fields in it.
///
/// Everything else here is chosen from a fixed list, so it cannot be anything the code did
/// not offer. The font family and the web font URL are typed, and both end up somewhere that
/// makes a bad value more than a cosmetic problem: the family lands inside a style attribute,
/// and the URL becomes a stylesheet the page loads.
/// </summary>
public class AppearanceFontTests
{
    [Theory]
    [InlineData("Inter")]
    [InlineData("Inter, Segoe UI, sans-serif")]
    [InlineData("'JetBrains Mono', monospace")]
    [InlineData("\"Segoe UI\", Helvetica-Neue, Arial")]
    [InlineData("Noto Sans JP")]
    public void RealFontStacksAreAccepted(string family)
        => Assert.True(Appearance.IsValidFontFamily(family));

    /// <summary>
    /// The reason this is a check and not an escape. A style attribute is a list of
    /// declarations; a semicolon ends the one we are writing and starts one the user is,
    /// and url() and expression() are how that gets interesting.
    /// </summary>
    [Theory]
    [InlineData("Inter; background: url(https://evil.example/x)")]
    [InlineData("Inter}html{display:none")]
    [InlineData("expression(alert(1))")]
    [InlineData("url(data:text/css,x)")]
    [InlineData("Inter<script>")]
    [InlineData("")]
    public void AnythingThatCouldEndTheDeclarationIsRejected(string family)
        => Assert.False(Appearance.IsValidFontFamily(family));

    [Fact]
    public void AnAbsurdlyLongFamilyIsRejected()
        => Assert.False(Appearance.IsValidFontFamily(new string('a', 500)));

    [Theory]
    [InlineData("https://fonts.googleapis.com/css2?family=Inter&display=swap")]
    [InlineData("https://example.com/fonts.css")]
    public void AnHttpsStylesheetIsAccepted(string url)
        => Assert.True(Appearance.IsValidFontUrl(url));

    /// <summary>
    /// http is refused as well as the obviously hostile schemes: a dashboard served over
    /// https that pulls a stylesheet over http gets it blocked as mixed content anyway, so
    /// accepting it would only produce a setting that silently does nothing.
    /// </summary>
    [Theory]
    [InlineData("http://fonts.example.com/x.css")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/css,body{}")]
    [InlineData("//fonts.googleapis.com/css")]
    [InlineData("fonts.googleapis.com/css")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElseIsRefused(string? url)
        => Assert.False(Appearance.IsValidFontUrl(url));

    [Fact]
    public void TheWebFontIsOnlyPulledInForTheCustomTypeface()
    {
        var url = "https://fonts.googleapis.com/css2?family=Inter";

        Assert.Null((Appearance.Default with { Font = "sans", FontUrl = url }).WebFontUrl);
        Assert.Equal(url, (Appearance.Default with { Font = "custom", FontUrl = url }).WebFontUrl);
    }

    /// <summary>
    /// A rejected family must not reach the style attribute by another door — the record is
    /// what builds that string, so it filters again rather than trusting the page to have
    /// checked.
    /// </summary>
    [Fact]
    public void AnInvalidFamilyIsLeftOutOfTheStyleAttribute()
    {
        var look = Appearance.Default with
        {
            Font = "custom",
            FontFamily = "Inter; background: url(https://evil.example/x)",
        };

        Assert.DoesNotContain("evil.example", look.StyleAttribute);
        Assert.DoesNotContain("url(", look.StyleAttribute);
    }

    [Fact]
    public void CustomFallsBackToTheSystemStackWithNothingSet()
    {
        var look = Appearance.Default with { Font = "custom" };

        Assert.Contains("system-ui", look.StyleAttribute);
    }

    [Fact]
    public void AnUploadedFontIsPreferredOverTheTypedFamily()
    {
        var look = Appearance.Default with
        {
            Font = "custom",
            FontFamily = "Inter",
            HasUploadedFont = true,
        };

        var style = look.StyleAttribute;
        Assert.Contains(FontStore.Family, style);
        // Both are present, in that order, so a file that fails to load lands on the typed
        // family rather than straight back on the system stack.
        Assert.True(style.IndexOf(FontStore.Family, StringComparison.Ordinal)
                    < style.IndexOf("Inter", StringComparison.Ordinal));
    }

    /// <summary>
    /// The whole point of appending rather than inserting. A database written before any of
    /// this existed has none of these keys, and must read back as the default look rather
    /// than as a page with no font and no radius.
    /// </summary>
    [Fact]
    public void SettingsFromAnOlderVersionReadBackAsTheDefaults()
    {
        var old = new SettingsBag { ["theme"] = "dark", ["accent"] = "#35d07f" };

        var look = Appearance.From(old);

        Assert.Equal("dark", look.Theme);
        Assert.Equal(Appearance.Default.Radius, look.Radius);
        Assert.Equal(Appearance.Default.Surface, look.Surface);
        Assert.Equal(Appearance.Default.DarkPalette, look.DarkPalette);
        Assert.Equal(Appearance.Default.TextScale, look.TextScale);
        Assert.Equal(Appearance.Default.Font, look.Font);
    }
}
