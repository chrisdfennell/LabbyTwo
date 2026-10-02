using System.Text.RegularExpressions;
using LabbyTwo.Core;

namespace LabbyTwo.Tests;

/// <summary>
/// Backgrounds and frosted glass without a server: the settings parser, the CSS builder and
/// what it refuses to write, the upload checks and metadata stripping, and the readability
/// estimate.
/// </summary>
public sealed partial class BackdropTests
{
    private static Backdrop Read(params (string Key, string Value)[] values)
    {
        var bag = new SettingsBag();
        foreach (var (key, value) in values)
            bag[key] = value;
        return Backdrop.From(bag);
    }

    private static readonly BackdropImage Picture = new("0123456789abcdef", ".jpg", 1920, 1080, 123_456);

    // ---- settings ------------------------------------------------------------------------

    [Fact]
    public void ByDefaultThereIsNoBackgroundAndNoRule()
    {
        var backdrop = Backdrop.From(new SettingsBag());
        Assert.Equal(BackdropKind.None, backdrop.Kind);
        Assert.False(backdrop.Glass);
        Assert.Equal("", BackdropCss.Render(backdrop, null, BackdropScreens.All));
    }

    [Fact]
    public void SettingsRoundTrip()
    {
        var chosen = Backdrop.Default with
        {
            Kind = BackdropKind.Gradient,
            Stops = [BackdropColour.OfToken("accent"), BackdropColour.Of(ThemeColor.Parse("#123456")), BackdropColour.OfToken("ink")],
            Angle = 45,
            Dim = 40,
            DimWith = BackdropDimWith.Overlay,
            Screens = BackdropScreens.Phone | BackdropScreens.Wall,
            ThemeOnly = "nord",
            Glass = true,
            GlassOpacity = 60,
            GlassBlur = 20,
            GlassSaturation = 180,
            GlassOnPhone = true,
        };

        var back = Backdrop.From(new SettingsBag(chosen.ToSettings()));
        Assert.Equal(chosen with { Stops = back.Stops }, back);
        Assert.Equal(chosen.Stops, back.Stops);
    }

    /// <summary>Whatever is in the database, what comes out is one of a fixed set of things.</summary>
    [Theory]
    [InlineData("backdrop_kind", "image; } body { display:none")]
    [InlineData("backdrop_kind", "3")]
    [InlineData("backdrop_fit", "cover;background:url(//evil)")]
    [InlineData("backdrop_position", "1,2")]
    [InlineData("backdrop_dim_with", "overlay)")]
    public void AnUnknownChoiceIsTheDefault(string key, string value)
    {
        var read = Read((key, value));
        Assert.Equal(Backdrop.Default, read with { Stops = Backdrop.Default.Stops });
    }

    [Theory]
    [InlineData("token:accent;}body{background:red")]
    [InlineData("url(https://evil.example/x.png),#fff")]
    [InlineData("#12345g,#ffffff")]
    [InlineData("red,blue")]
    [InlineData("rgb(1,2,3),#ffffff")]
    [InlineData("token:nope,#ffffff")]
    [InlineData("#111111")]
    [InlineData("#111111,#222222,#333333,#444444")]
    [InlineData("var(--x),#ffffff")]
    public void BadGradientStopsFallBackToTheDefault(string stops)
    {
        Assert.Null(Backdrop.ParseStops(stops));
        Assert.Equal(Backdrop.Default.Stops, Read((Backdrop.StopsKey, stops)).Stops);
    }

    [Fact]
    public void NumbersAreClamped()
    {
        var read = Read(
            (Backdrop.AngleKey, "999"), (Backdrop.DimKey, "-5"), (Backdrop.BlurKey, "500"),
            (Backdrop.GlassOpacityKey, "1"), (Backdrop.GlassBlurKey, "1000"), (Backdrop.GlassSaturationKey, "5"));
        Assert.Equal(359, read.Angle);
        Assert.Equal(0, read.Dim);
        Assert.Equal(Backdrop.MaxBlur, read.Blur);
        Assert.Equal(Backdrop.MinGlassOpacity, read.GlassOpacity);
        Assert.Equal(Backdrop.MaxGlassBlur, read.GlassBlur);
        Assert.Equal(Backdrop.MinSaturation, read.GlassSaturation);

        // Not a number at all: the default.
        Assert.Equal(Backdrop.Default.Dim, Read((Backdrop.DimKey, "30%; color: red")).Dim);
    }

    [Fact]
    public void ScreensAndThemeIdsAreChecked()
    {
        Assert.Equal(BackdropScreens.Dashboard | BackdropScreens.Phone, Read((Backdrop.ScreensKey, "dashboard, evil;, PHONE")).Screens);
        Assert.Equal("", Read((Backdrop.ThemeOnlyKey, "nord;}")).ThemeOnly);
        Assert.Equal("user-abc123", Read((Backdrop.ThemeOnlyKey, "user-abc123")).ThemeOnly);
    }

    // ---- the builder -----------------------------------------------------------------------

    [Fact]
    public void AGradientIsDrawnFromTheThemesOwnColours()
    {
        var css = BackdropCss.Render(Backdrop.Default with { Kind = BackdropKind.Gradient, Dim = 0 }, null, BackdropScreens.All);

        Assert.Contains("body > .app-shell::before", css);
        Assert.Contains("body > .wall::before", css);
        Assert.DoesNotContain(".phone-shell", css); // not one of the default screens
        Assert.Contains("linear-gradient(180deg, var(--panel-2), var(--ink))", css);
        Assert.Contains("position: fixed", css);
        Assert.Contains("z-index: -1", css);
        Assert.Contains("isolation: isolate", css);
    }

    [Fact]
    public void ACustomColourIsRebuiltNotCopied()
    {
        var backdrop = Read((Backdrop.KindKey, "solid"), (Backdrop.SolidKey, "#ABC"), (Backdrop.DimKey, "0"));
        Assert.Contains("background-color: #aabbcc;", BackdropCss.Render(backdrop, null, BackdropScreens.All));
    }

    [Fact]
    public void ThePictureUrlIsTheServersOwn()
    {
        var css = BackdropCss.Render(Backdrop.Default with { Kind = BackdropKind.Image, Blur = 10 }, Picture, BackdropScreens.All);
        Assert.Contains("url(\"/backdrop/0123456789abcdef.jpg\")", css);
        Assert.Contains("filter: blur(10px)", css);
        Assert.Contains("inset: -20px", css); // bleed past the edges, twice the blur
        Assert.Contains("background-size: 100% 100%, cover", css);
    }

    [Theory]
    [InlineData("../../etc/passwd", ".jpg")]
    [InlineData("0123456789ABCDEF", ".jpg")]
    [InlineData("0123456789abcdef", ".svg")]
    [InlineData("0123456789abcdef\") } body { x: url(\"", ".jpg")]
    public void APictureThatIsNotOursDrawsNothing(string hash, string extension)
    {
        var image = new BackdropImage(hash, extension, 10, 10, 10);
        Assert.False(image.IsValid);
        Assert.Equal("", BackdropCss.Render(Backdrop.Default with { Kind = BackdropKind.Image }, image, BackdropScreens.All));
        Assert.Equal("", BackdropCss.LayerDeclarations(Backdrop.Default with { Kind = BackdropKind.Image }, image));
    }

    [Fact]
    public void APictureChosenWithNoPictureUploadedDrawsNothing() =>
        Assert.Equal("", BackdropCss.Render(Backdrop.Default with { Kind = BackdropKind.Image }, null, BackdropScreens.All));

    [Fact]
    public void ScreensAreIntersectedWithWhatTheCallerAllows()
    {
        var backdrop = Backdrop.Default with { Kind = BackdropKind.Solid, Screens = BackdropScreens.All };
        var css = BackdropCss.Render(backdrop, null, BackdropScreens.Phone);
        Assert.Contains("body > .phone-shell::before", css);
        Assert.DoesNotContain(".app-shell", css);
        Assert.Equal("", BackdropCss.Render(backdrop, null, BackdropScreens.None));
    }

    [Fact]
    public void GlassOnlyWhereTheBrowserCanBlurAndNeverWhenTransparencyIsReduced()
    {
        var css = BackdropCss.Render(Backdrop.Default with { Kind = BackdropKind.Solid, Dim = 0, Glass = true, GlassOpacity = 65 }, null, BackdropScreens.All);

        var supports = css.IndexOf("@supports ((-webkit-backdrop-filter: blur(1px)) or (backdrop-filter: blur(1px)))", StringComparison.Ordinal);
        var glass = css.IndexOf("color-mix(in srgb, var(--panel) 65%, transparent)", StringComparison.Ordinal);
        var reduced = css.IndexOf("@media (prefers-reduced-transparency: reduce)", StringComparison.Ordinal);
        Assert.True(supports >= 0 && glass > supports && reduced > glass, css);

        // The translucent fill appears nowhere outside the @supports block.
        Assert.Single(Regex.Matches(css, "transparent\\)"));
        Assert.Contains("backdrop-filter: blur(14px) saturate(150%)", css);
        Assert.Contains("-webkit-backdrop-filter: blur(14px) saturate(150%)", css);
        Assert.Contains(BackdropCss.SolidDeclarations, css[reduced..]);
        Assert.Contains("wall-in-glass", css);
    }

    [Fact]
    public void ThePhoneKeepsSolidCardsUnlessAskedFor()
    {
        var on = Backdrop.Default with { Kind = BackdropKind.Solid, Glass = true, Screens = BackdropScreens.All };
        Assert.DoesNotContain(".phone-status", BackdropCss.Render(on, null, BackdropScreens.All));
        Assert.Contains("body > .phone-shell::before", BackdropCss.Render(on, null, BackdropScreens.All));
        Assert.Contains(".phone-status", BackdropCss.Render(on with { GlassOnPhone = true }, null, BackdropScreens.All));

        // Only the phone, glass not on the phone: no glass rules at all.
        Assert.DoesNotContain("@supports", BackdropCss.Render(on, null, BackdropScreens.Phone));
    }

    [GeneratedRegex(@"([a-z-]+)\s*:\s*[^;{}]+;")]
    private static partial Regex Declaration();

    private static readonly HashSet<string> AllowedProperties =
    [
        "isolation", "content", "position", "inset", "z-index", "pointer-events", "background-color", "background-image",
        "background-size", "background-repeat", "background-position", "filter", "-webkit-backdrop-filter",
        "backdrop-filter", "animation-name",
    ];

    /// <summary>
    /// Every setting at its most hostile, in every combination of kind and glass: the output
    /// only ever sets the known properties, has balanced braces and contains none of the text
    /// that was stored.
    /// </summary>
    [Fact]
    public void HostileSettingsNeverReachTheCss()
    {
        const string evil = "red;}*{display:none}/*</style><script>alert(1)</script>";
        var keys = new[]
        {
            Backdrop.SolidKey, Backdrop.StopsKey, Backdrop.AngleKey, Backdrop.FitKey, Backdrop.PositionKey,
            Backdrop.DimKey, Backdrop.DimWithKey, Backdrop.BlurKey, Backdrop.ScreensKey, Backdrop.ThemeOnlyKey,
            Backdrop.GlassOpacityKey, Backdrop.GlassBlurKey, Backdrop.GlassSaturationKey, Backdrop.GlassKey, Backdrop.GlassOnPhoneKey,
        };

        foreach (var kind in new[] { "solid", "gradient", "image" })
        {
            foreach (var glass in new[] { "true", "false" })
            {
                var bag = new SettingsBag();
                foreach (var key in keys)
                    bag[key] = evil;
                bag[Backdrop.KindKey] = kind;
                bag[Backdrop.GlassKey] = glass;
                bag[Backdrop.ScreensKey] = "dashboard,wall,phone";
                bag[Backdrop.GlassOnPhoneKey] = "true";

                var css = BackdropCss.Render(Backdrop.From(bag), Picture, BackdropScreens.All);
                Assert.NotEqual("", css);
                Assert.DoesNotContain("display:none", css);
                Assert.DoesNotContain("script", css);
                Assert.DoesNotContain("</style", css);
                Assert.DoesNotContain("/*", css);
                Assert.Equal(css.Count(c => c == '{'), css.Count(c => c == '}'));

                foreach (Match m in Declaration().Matches(css))
                    Assert.Contains(m.Groups[1].Value, AllowedProperties);
            }
        }
    }

    [Fact]
    public void ThePreviewUsesTheSameDeclarations()
    {
        var backdrop = Backdrop.Default with { Kind = BackdropKind.Image, Blur = 6 };
        var style = BackdropCss.PreviewLayerStyle(backdrop, Picture);
        Assert.StartsWith("inset: -12px; ", style);
        Assert.EndsWith(BackdropCss.LayerDeclarations(backdrop, Picture), style);
        Assert.Equal("", BackdropCss.PreviewLayerStyle(Backdrop.Default, null));
    }

    [Fact]
    public void ThePresetsAreThemeColoursNotLiterals()
    {
        foreach (var preset in Backdrop.Presets)
        {
            Assert.InRange(preset.Stops.Count, Backdrop.MinStops, Backdrop.MaxStops);
            Assert.All(preset.Stops, s => Assert.NotNull(s.Token));
        }
    }

    // ---- uploads ---------------------------------------------------------------------------

    [Fact]
    public void TheFormatComesFromTheBytes()
    {
        Assert.Equal(("png", ".png", "image/png", 64, 48), Tuple(BackdropImageFile.Inspect(BackdropTestImages.Png())));
        Assert.Equal(("jpeg", ".jpg", "image/jpeg", 640, 480), Tuple(BackdropImageFile.Inspect(BackdropTestImages.Jpeg())));
        Assert.Equal(("webp", ".webp", "image/webp", 320, 200), Tuple(BackdropImageFile.Inspect(BackdropTestImages.WebP())));

        static (string, string, string, int, int) Tuple(BackdropImageFile.Info i) => (i.Format, i.Extension, i.ContentType, i.Width, i.Height);
    }

    [Theory]
    [InlineData("<?xml version=\"1.0\"?><svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>", "SVG")]
    [InlineData("<svg onload=\"alert(1)\"/>", "SVG")]
    [InlineData("GIF89a......", "GIF")]
    [InlineData("<html><body>hello</body></html>", "not a JPEG, PNG or WebP")]
    [InlineData("MZ\u0090\0executable", "not a JPEG, PNG or WebP")]
    public void OnlyThreeFormatsGetIn(string content, string reason)
    {
        var ex = Assert.Throws<InvalidDataException>(() => BackdropImageFile.Inspect(System.Text.Encoding.UTF8.GetBytes(content)));
        Assert.Contains(reason, ex.Message);
    }

    [Fact]
    public void DamagedAndOversizedFilesAreRefused()
    {
        Assert.Throws<InvalidDataException>(() => BackdropImageFile.Inspect([]));
        Assert.Contains("incomplete", Assert.Throws<InvalidDataException>(() => BackdropImageFile.Inspect(BackdropTestImages.Png(complete: false))).Message);
        Assert.Contains("no picture size", Assert.Throws<InvalidDataException>(() => BackdropImageFile.Inspect(BackdropTestImages.Jpeg(withFrame: false))).Message);

        // A small file that claims to be enormous: refused before anything would decode it.
        Assert.Contains("megapixels", Assert.Throws<InvalidDataException>(() => BackdropImageFile.Inspect(BackdropTestImages.Png(10_000, 10_000))).Message);
        Assert.Contains("megapixels", Assert.Throws<InvalidDataException>(() => BackdropImageFile.Inspect(BackdropTestImages.Png(20_000, 10))).Message);

        var big = new byte[BackdropImageFile.MaxBytes + 1];
        BackdropTestImages.Png().CopyTo(big, 0);
        Assert.Contains("limit", Assert.Throws<InvalidDataException>(() => BackdropImageFile.Inspect(big)).Message);
    }

    [Fact]
    public void AJpegLosesItsMetadataButKeepsItsOrientation()
    {
        var original = BackdropTestImages.Jpeg(orientation: 6);
        Assert.True(BackdropTestImages.Contains(original, BackdropTestImages.Secret));

        var info = BackdropImageFile.Inspect(original);
        var stripped = BackdropImageFile.StripMetadata(original, info);

        Assert.False(BackdropTestImages.Contains(stripped, BackdropTestImages.Secret));
        Assert.False(BackdropTestImages.Contains(stripped, "xmpmeta"));
        Assert.True(BackdropTestImages.Contains(stripped, "ICC_PROFILE")); // colour stays
        Assert.True(BackdropTestImages.Contains(stripped, "JFIF"));
        Assert.Equal(info, BackdropImageFile.Inspect(stripped));

        // The minimal EXIF block is there, straight after JFIF, and says 6.
        var exif = stripped.AsSpan().IndexOf(BackdropImageFile.MinimalExif(6));
        Assert.True(exif > 0);
        Assert.Equal(6, BackdropImageFile.ExifOrientation(stripped.AsSpan(exif + 4, 30)));

        // The picture data after start-of-scan is untouched.
        Assert.True(stripped.AsSpan().EndsWith(original.AsSpan(original.AsSpan().IndexOf((byte[])[0xFF, 0xDA]))));
    }

    [Fact]
    public void AnUprightJpegGetsNoExifAtAll()
    {
        var stripped = BackdropImageFile.StripMetadata(BackdropTestImages.Jpeg(orientation: 1), BackdropImageFile.Inspect(BackdropTestImages.Jpeg(orientation: 1)));
        Assert.False(BackdropTestImages.Contains(stripped, "Exif"));
        var none = BackdropTestImages.Jpeg(orientation: 0);
        Assert.False(BackdropTestImages.Contains(BackdropImageFile.StripMetadata(none, BackdropImageFile.Inspect(none)), "Exif"));
    }

    [Fact]
    public void APngLosesItsTextChunks()
    {
        var original = BackdropTestImages.Png();
        var stripped = BackdropImageFile.StripMetadata(original, BackdropImageFile.Inspect(original));
        Assert.False(BackdropTestImages.Contains(stripped, BackdropTestImages.Secret));
        Assert.False(BackdropTestImages.Contains(stripped, "tEXt"));
        Assert.True(BackdropTestImages.Contains(stripped, "IDAT"));
        Assert.True(BackdropTestImages.Contains(stripped, "IEND"));
        Assert.Equal(64, BackdropImageFile.Inspect(stripped).Width);
    }

    [Fact]
    public void AWebPLosesItsMetadataAndSaysSo()
    {
        var original = BackdropTestImages.WebP();
        var stripped = BackdropImageFile.StripMetadata(original, BackdropImageFile.Inspect(original));

        Assert.False(BackdropTestImages.Contains(stripped, BackdropTestImages.Secret));
        Assert.False(BackdropTestImages.Contains(stripped, "EXIF"));
        Assert.Equal((uint)(stripped.Length - 8), System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(stripped.AsSpan(4)));

        // VP8X no longer promises EXIF or XMP chunks.
        Assert.Equal(0, stripped[12 + 8] & (0x08 | 0x04));
        Assert.Equal((320, 200), (BackdropImageFile.Inspect(stripped).Width, BackdropImageFile.Inspect(stripped).Height));
    }

    // ---- readability -----------------------------------------------------------------------

    private static ThemeVariant Dark => BuiltInThemes.LabbyTwo.Dark!;

    [Fact]
    public void ABrightPictureUndimmedFailsAndDimmingFixesIt()
    {
        var white = new[] { new ThemeColor(255, 255, 255) };
        var bright = Backdrop.Default with { Kind = BackdropKind.Image, Dim = 0 };

        var results = BackdropContrast.Check(bright, Dark, white);
        Assert.Contains(results, r => r.Pair.Label == "Headings on the background" && !r.PassesAA);
        Assert.DoesNotContain(results, r => r.Pair.Background == "glass"); // glass off: cards are solid

        var dimmed = BackdropContrast.Check(bright with { Dim = 90 }, Dark, white);
        Assert.All(dimmed.Where(r => r.Pair.Foreground == "text"), r => Assert.True(r.PassesAA, $"{r.Pair.Label} {r.RatioText}"));
    }

    [Fact]
    public void TheWorstSampleIsWhatCounts()
    {
        var mostlyDark = Enumerable.Repeat(new ThemeColor(10, 10, 10), 15).Append(new ThemeColor(255, 255, 255)).ToList();
        var backdrop = Backdrop.Default with { Kind = BackdropKind.Image, Dim = 0 };
        var text = BackdropContrast.Check(backdrop, Dark, mostlyDark).Single(r => r.Pair.Label == "Headings on the background");
        var onWhite = Contrast.Ratio(Dark.Effective("text")!.Value, new ThemeColor(255, 255, 255));
        Assert.Equal(onWhite, text.Ratio, 6);
    }

    [Fact]
    public void AMoreSolidGlassReadsBetter()
    {
        var white = new[] { new ThemeColor(255, 255, 255) };
        var thin = Backdrop.Default with { Kind = BackdropKind.Image, Dim = 0, Glass = true, GlassOpacity = Backdrop.MinGlassOpacity };
        var thick = thin with { GlassOpacity = Backdrop.MaxGlassOpacity };

        double OnGlass(Backdrop b) => BackdropContrast.Check(b, Dark, white).Single(r => r.Pair.Label == "Text on a glass card").Ratio;
        Assert.True(OnGlass(thick) > OnGlass(thin));
        Assert.False(BackdropContrast.Check(thin, Dark, white).Single(r => r.Pair.Label == "Text on a glass card").PassesAA);
        Assert.True(BackdropContrast.Check(thick, Dark, white).Single(r => r.Pair.Label == "Text on a glass card").PassesAA);
    }

    /// <summary>The estimate composites exactly as the CSS does: color-mix with transparent multiplies the alpha.</summary>
    [Fact]
    public void TheDimMatchesTheCss()
    {
        var behind = new ThemeColor(200, 100, 50);
        var page = Dark.Effective("ink")!.Value;
        var overlay = Dark.Effective("overlay")!.Value;

        var toPage = BackdropContrast.Dimmed(behind, Backdrop.Default with { Dim = 40 }, Dark);
        Assert.Equal(page.WithAlpha(0.4).Over(behind), toPage);

        var toOverlay = BackdropContrast.Dimmed(behind, Backdrop.Default with { Dim = 40, DimWith = BackdropDimWith.Overlay }, Dark);
        Assert.Equal(overlay.WithAlpha(overlay.A * 0.4).Over(behind), toOverlay);
    }

    [Fact]
    public void AGradientIsJudgedAtItsStopsAndAPictureWithoutSamplesNotAtAll()
    {
        var gradient = Backdrop.Default with { Kind = BackdropKind.Gradient };
        Assert.Equal(2, BackdropContrast.Behind(gradient, Dark, []).Count);
        Assert.Empty(BackdropContrast.Check(Backdrop.Default with { Kind = BackdropKind.Image }, Dark, []));
        Assert.Empty(BackdropContrast.Check(Backdrop.Default, Dark, []));

        // The default gradient is the theme's own surfaces: it reads as well as the theme does.
        Assert.All(BackdropContrast.Check(gradient, Dark, []), r => Assert.True(r.PassesAA, r.Pair.Label));
    }
}
