using System.Text.Json.Nodes;
using LabbyTwo.Core;

namespace LabbyTwo.Tests;

/// <summary>
/// Theme files and base16 schemes: what goes in, what comes out, and — mostly — what is
/// refused. A theme is data that ends up in a stylesheet, so the importer is a security
/// boundary, and it is tested like one.
/// </summary>
public class ThemeImportTests
{
    // ---- the LabbyTwo file format -------------------------------------------------------

    [Theory]
    [InlineData("labbytwo")]
    [InlineData("nord")]
    [InlineData("dracula")]
    [InlineData("high-contrast")]
    public void AnExportedThemeImportsBackExactly(string id)
    {
        var original = BuiltInThemes.Find(id)!;
        var result = ThemeJson.Import(ThemeJson.Export(original));

        Assert.True(result.Ok, string.Join("; ", result.Errors));
        var back = result.Theme!;
        Assert.Equal(original.Name, back.Name);
        Assert.Equal(original.Author, back.Author);
        Assert.Equal(original.ForcedMode, back.ForcedMode);
        Assert.Equal(ThemeCss.Render(original), ThemeCss.Render(back));
        // An import never carries an id: the store gives it one.
        Assert.Equal("", back.Id);
        Assert.False(back.BuiltIn);
    }

    [Fact]
    public void TheExportIsVersioned()
    {
        var json = JsonNode.Parse(ThemeJson.Export(BuiltInThemes.Nord))!.AsObject();
        Assert.Equal(1, (int)json["labbytwo-theme"]!);
        Assert.Equal("Nord", (string)json["name"]!);
        Assert.Equal("#2e3440", (string)json["dark"]!["ink"]!);
    }

    private static string Minimal(string extraTop = "", string extraDark = "", string version = "1")
    {
        var dark = BuiltInThemes.LabbyTwo.Dark!;
        var tokens = string.Join(",\n", ThemeTokens.RequiredNames.Select(n => $"\"{n}\": \"{dark[n]!.Value.Css}\""));
        return $$"""
            { "labbytwo-theme": {{version}}, "name": "Mine"{{extraTop}},
              "dark": { {{tokens}}{{extraDark}} } }
            """;
    }

    [Fact]
    public void TheSmallestValidFileImports()
    {
        var result = ThemeJson.Import(Minimal());
        Assert.True(result.Ok, string.Join("; ", result.Errors));
        Assert.Equal("dark", result.Theme!.ForcedMode);
    }

    /// <summary>Each of these is refused whole, with a reason — nothing half-imports.</summary>
    [Theory]
    [InlineData(", \"css\": \"body{display:none}\"", "", "\"css\" is not part of a theme file")]
    [InlineData("", ", \"background-image\": \"#fff\"", "not a theme token")]
    [InlineData("", ", \"link\": \"url(https://evil.example/x)\"", "\"link\" is not a colour")]
    [InlineData("", ", \"link\": \"#fff; background: red\"", "\"link\" is not a colour")]
    [InlineData("", ", \"link\": \"</style><script>alert(1)</script>\"", "\"link\" is not a colour")]
    [InlineData("", ", \"link\": \"var(--down)\"", "\"link\" is not a colour")]
    [InlineData("", ", \"link\": 12", "\"link\" is not a colour")]
    [InlineData("", ", \"link\": { \"a\": 1 }", "\"link\" is not a colour")]
    [InlineData(", \"author\": 5", "", "\"author\" must be text")]
    public void HostileOrMalformedFilesAreRejected(string top, string dark, string reason)
    {
        var result = ThemeJson.Import(Minimal(top, dark));
        Assert.False(result.Ok);
        Assert.Null(result.Theme);
        Assert.Contains(result.Errors, e => e.Contains(reason, StringComparison.Ordinal));
    }

    [Fact]
    public void AFileFromTheFutureIsRefused()
    {
        var result = ThemeJson.Import(Minimal(version: "2"));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("version 2", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingRequiredTokenIsNamed()
    {
        var json = Minimal().Replace("\"ink\": \"#0a0e13\",", "", StringComparison.Ordinal);
        var result = ThemeJson.Import(json);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("\"ink\" is missing", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1, 2, 3]")]
    [InlineData("\"a string\"")]
    [InlineData("{ \"name\": \"no version\", \"dark\": {} }")]
    [InlineData("{ \"labbytwo-theme\": 1, \"name\": \"no colours\" }")]
    [InlineData("{ \"labbytwo-theme\": 1, \"name\": \"\", \"dark\": {} }")]
    [InlineData("{ \"labbytwo-theme\": 1, \"labbytwo-theme\": 1, \"name\": \"dupe\", \"dark\": {} }")]
    public void NonThemesAreRefused(string text) => Assert.False(ThemeJson.Import(text).Ok);

    [Fact]
    public void AnEnormousFileIsRefusedBeforeItIsParsed()
    {
        var huge = Minimal(extraTop: $", \"description\": \"{new string('a', ThemeJson.MaxBytes)}\"");
        var result = ThemeJson.Import(huge);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("too big", StringComparison.Ordinal));
    }

    [Fact]
    public void DeeplyNestedJsonIsRefused()
    {
        var deep = new string('[', 200) + new string(']', 200);
        Assert.False(ThemeJson.Import(deep).Ok);
    }

    [Fact]
    public void ControlCharactersAreStrippedFromNames()
    {
        var result = ThemeJson.Import(Minimal().Replace("\"Mine\"", "\"Mi\\u0000ne\\n\"", StringComparison.Ordinal));
        Assert.True(result.Ok, string.Join("; ", result.Errors));
        Assert.Equal("Mine", result.Theme!.Name);
    }

    /// <summary>A key echoed back in an error is clipped, so a hostile file cannot fill the page with its own text.</summary>
    [Fact]
    public void ErrorsDoNotRepeatLongHostileKeys()
    {
        var result = ThemeJson.Import(Minimal($", \"{new string('x', 500)}\": 1"));
        Assert.False(result.Ok);
        Assert.All(result.Errors, e => Assert.True(e.Length < 120, e));
    }

    // ---- base16 -------------------------------------------------------------------------

    /// <summary>Tomorrow Night, by Chris Kempson — classic flat YAML.</summary>
    public const string TomorrowNight = """
        scheme: "Tomorrow Night"
        author: "Chris Kempson (http://chriskempson.com)"
        base00: "1d1f21"
        base01: "282a2e"
        base02: "373b41"
        base03: "969896"
        base04: "b4b7b4"
        base05: "c5c8c6"
        base06: "e0e0e0"
        base07: "ffffff"
        base08: "cc6666"
        base09: "de935f"
        base0A: "f0c674"
        base0B: "b5bd68"
        base0C: "8abeb7"
        base0D: "81a2be"
        base0E: "b294bb"
        base0F: "a3685a"
        """;

    /// <summary>Solarized Light, by Ethan Schoonover — the newer tinted-theming shape, with a palette block and hashes.</summary>
    public const string SolarizedLight = """
        system: "base16"
        name: "Solarized Light"
        author: "Ethan Schoonover (modified by aramisgithub)"
        variant: "light"
        palette:
          base00: "#fdf6e3"
          base01: "#eee8d5"
          base02: "#93a1a1"
          base03: "#839496"
          base04: "#657b83"
          base05: "#586e75"
          base06: "#073642"
          base07: "#002b36"
          base08: "#dc322f"
          base09: "#cb4b16"
          base0A: "#b58900"
          base0B: "#859900"
          base0C: "#2aa198"
          base0D: "#268bd2"
          base0E: "#6c71c4"
          base0F: "#d33682"
        """;

    /// <summary>Ocean, by Chris Kempson — as JSON, with lower-case slot letters.</summary>
    public const string OceanJson = """
        { "scheme": "Ocean", "author": "Chris Kempson",
          "base00": "2b303b", "base01": "343d46", "base02": "4f5b66", "base03": "65737e",
          "base04": "a7adba", "base05": "c0c5ce", "base06": "dfe1e8", "base07": "eff1f5",
          "base08": "bf616a", "base09": "d08770", "base0a": "ebcb8b", "base0b": "a3be8c",
          "base0c": "96b5b4", "base0d": "8fa1b3", "base0e": "b48ead", "base0f": "ab7967" }
        """;

    [Fact]
    public void ADarkSchemeMapsOntoTheTokens()
    {
        var result = ThemeJson.ImportAny(TomorrowNight);
        Assert.True(result.Ok, string.Join("; ", result.Errors));
        var theme = result.Theme!;

        Assert.Equal("Tomorrow Night", theme.Name);
        Assert.StartsWith("Chris Kempson", theme.Author);
        Assert.NotNull(theme.Dark);
        Assert.Null(theme.Light);

        var v = theme.Dark!;
        Assert.Equal(ThemeColor.Parse("#1d1f21"), v["ink"]);
        Assert.Equal(ThemeColor.Parse("#282a2e"), v["panel"]);
        Assert.Equal(ThemeColor.Parse("#373b41"), v["panel-2"]);
        Assert.Equal(ThemeColor.Parse("#c5c8c6"), v["text"]);
        Assert.Equal(ThemeColor.Parse("#b4b7b4"), v["muted"]);
        Assert.Equal(ThemeColor.Parse("#969896"), v["placeholder"]);
        Assert.Equal(ThemeColor.Parse("#81a2be"), v["accent"]);
        Assert.Equal(ThemeColor.Parse("#b5bd68"), v["up"]);
        Assert.Equal(ThemeColor.Parse("#cc6666"), v["down"]);
        Assert.Equal(ThemeColor.Parse("#f0c674"), v["warn"]);
        Assert.Equal(ThemeColor.Parse("#de935f"), v["code"]);
        Assert.Equal(ThemeColor.Parse("#b294bb"), v["accent-2"]);
        Assert.Equal(ThemeColor.Parse("#a3685a"), v["chart-8"]);
        // The border sits between base02 and base03.
        Assert.Equal(ThemeColor.Parse("#373b41").Mix(ThemeColor.Parse("#969896"), 1d / 3), v["edge"]);
        // Dark text on a light-blue button reads better than white.
        Assert.Equal(ThemeColor.Parse("#1d1f21"), v["accent-ink"]);
        Assert.Empty(v.Missing);
    }

    /// <summary>A light scheme becomes a light-only theme — decided by base00's luminance, not by the file's say-so.</summary>
    [Fact]
    public void ALightSchemeMakesOnlyALightEnd()
    {
        var result = ThemeJson.ImportAny(SolarizedLight);
        Assert.True(result.Ok, string.Join("; ", result.Errors));
        Assert.Null(result.Theme!.Dark);
        Assert.Equal("light", result.Theme.ForcedMode);
        Assert.Equal(ThemeColor.Parse("#fdf6e3"), result.Theme.Light!["ink"]);
        Assert.Equal(ThemeColor.Parse("#586e75"), result.Theme.Light!["text"]);
        Assert.Equal("Solarized Light", result.Theme.Name);
    }

    [Fact]
    public void JsonAndLowerCaseSlotsAreReadToo()
    {
        var result = ThemeJson.ImportAny(OceanJson);
        Assert.True(result.Ok, string.Join("; ", result.Errors));
        Assert.Equal("Ocean", result.Theme!.Name);
        Assert.Equal(ThemeColor.Parse("#ebcb8b"), result.Theme.Dark!["warn"]);
    }

    [Fact]
    public void AnImportedSchemeSurvivesAnExportRoundTrip()
    {
        var theme = ThemeJson.ImportAny(TomorrowNight).Theme!;
        var back = ThemeJson.Import(ThemeJson.Export(theme));
        Assert.True(back.Ok, string.Join("; ", back.Errors));
        Assert.Equal(ThemeCss.Render(theme), ThemeCss.Render(back.Theme!));
    }

    [Fact]
    public void ASchemeMissingASlotIsRefused()
    {
        var result = ThemeJson.ImportAny(TomorrowNight.Replace("base0E: \"b294bb\"", "", StringComparison.Ordinal));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("base0E", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("base08: \"cc6666\"", "base08: \"cc6666; background: url(x)\"")]
    [InlineData("base08: \"cc6666\"", "base08: \"red\"")]
    [InlineData("base08: \"cc6666\"", "base08: \"#cc66\"")]
    public void ASchemeWithANonColourIsRefused(string from, string to)
    {
        var result = ThemeJson.ImportAny(TomorrowNight.Replace(from, to, StringComparison.Ordinal));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("base08", StringComparison.Ordinal));
    }

    [Fact]
    public void YamlTagsCannotChooseATypeToBuild()
    {
        var hostile = "!!python/object:os.system\n" + TomorrowNight;
        Assert.False(ThemeJson.ImportAny(hostile).Ok);
    }

    [Fact]
    public void ASchemesNameIsTextAndClipped()
    {
        var result = ThemeJson.ImportAny(TomorrowNight.Replace("\"Tomorrow Night\"", $"\"{new string('n', 300)}\"", StringComparison.Ordinal));
        Assert.True(result.Ok);
        Assert.Equal(ThemeJson.MaxName, result.Theme!.Name.Length);
    }
}
