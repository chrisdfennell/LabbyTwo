using System.Text.RegularExpressions;
using LabbyTwo.Core;

namespace LabbyTwo.Tests;

/// <summary>
/// Holds the stylesheets and components to the token system, so a theme really does
/// recolour everything.
///
/// A colour written straight into a rule — "#ff5c6c" rather than var(--down) — is a colour
/// no theme can reach: it stays that red in Nord, in Solarized and in High Contrast, and
/// nobody notices until somebody does. These tests make that a build failure instead. The
/// rule: colour literals live only in custom-property definitions (the tokens themselves),
/// in the built-in theme definitions, and on lines marked "allow-colour" with a reason —
/// for the very few colours that are genuinely fixed whatever the theme.
/// </summary>
public sealed partial class ThemeTokenAuditTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LabbyTwo.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not find the repository root.");
    }

    /// <summary>Every stylesheet the app ships: app.css, family.css and every component's scoped one.</summary>
    private static IEnumerable<string> StyleSheets() =>
        new[] { Path.Combine(Root, "wwwroot", "app.css"), Path.Combine(Root, "wwwroot", "family.css") }
            .Concat(Directory.EnumerateFiles(Path.Combine(Root, "Components"), "*.razor.css", SearchOption.AllDirectories));

    private static IEnumerable<string> CodeFiles() =>
        new[] { "Components", "Core", "Services", "Storage", "Providers" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(Root, d), "*.*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
            .Concat(Directory.EnumerateFiles(Path.Combine(Root, "wwwroot"), "*.js"));

    /// <summary>
    /// Code files allowed to hold colour literals, and why. Each is a place where a colour is
    /// data rather than styling.
    /// </summary>
    private static readonly Dictionary<string, string> CodeAllowList = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Core/BuiltInThemes.cs"] = "the built-in themes are the colours",
        ["Core/ThemeTokens.cs"] = "the optional tokens' fallbacks, mirrored in app.css",
        ["Core/ThemeColor.cs"] = "documentation of the accepted notations",
        ["Core/ThemeJson.cs"] = "the schema's example, in its doc comment",
        ["Core/Base16.cs"] = "the scheme format, in its doc comment",
        ["Storage/AppSettingsStore.cs"] = "the accent swatches are colours to pick from",
        ["Services/FaviconService.cs"] = "a placeholder icon served as an image file, where no stylesheet reaches",
    };

    // #rgb, #rgba, #rrggbb, #rrggbbaa as a whole word, and the colour functions.
    [GeneratedRegex(@"(?<![\w&])#(?:[0-9a-fA-F]{8}|[0-9a-fA-F]{6}|[0-9a-fA-F]{3,4})\b|\b(?:rgba?|hsla?)\(", RegexOptions.CultureInvariant)]
    private static partial Regex ColourLiteral();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CssComment();

    [GeneratedRegex(@"var\(\s*--([a-zA-Z0-9-]+)")]
    private static partial Regex VarUse();

    [GeneratedRegex(@"(?<![\w-])--([a-zA-Z0-9-]+)\s*:")]
    private static partial Regex VarDefinition();

    private static string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');

    /// <summary>
    /// The CSS with comments blanked out but lines kept where they were, so a hit can be
    /// reported with its line number and checked for an allow-colour marker.
    /// </summary>
    private static string[] CodeLines(string css) =>
        CssComment().Replace(css, m => new string([.. m.Value.Select(c => c == '\n' ? '\n' : ' ')])).Split('\n');

    [Fact]
    public void NoStylesheetHasAColourOutsideATokenDefinition()
    {
        var stray = new List<string>();
        foreach (var file in StyleSheets())
        {
            var original = File.ReadAllText(file).Split('\n');
            var code = CodeLines(File.ReadAllText(file));
            for (var i = 0; i < code.Length; i++)
            {
                var line = code[i];
                if (!ColourLiteral().IsMatch(line))
                    continue;

                // A custom property definition is a token: that is where colours belong.
                if (Regex.IsMatch(line, @"^\s*--[a-zA-Z0-9-]+\s*:"))
                    continue;

                var marked = original[i].Contains("allow-colour", StringComparison.Ordinal)
                             || (i > 0 && original[i - 1].Contains("allow-colour", StringComparison.Ordinal));
                if (!marked)
                    stray.Add($"{Relative(file)}:{i + 1}: {line.Trim()}");
            }
        }

        Assert.True(stray.Count == 0, "Colours outside a token — use a var(--token):\n" + string.Join("\n", stray));
    }

    /// <summary>The allow-list is for exceptions, and has to stay short enough to read.</summary>
    [Fact]
    public void TheCssAllowListIsShort()
    {
        var marked = StyleSheets().Sum(f => File.ReadAllLines(f).Count(l => l.Contains("allow-colour", StringComparison.Ordinal)));
        Assert.InRange(marked, 0, 5);
    }

    [Fact]
    public void NoComponentOrServiceHardCodesAColour()
    {
        var stray = new List<string>();
        foreach (var file in CodeFiles())
        {
            var relative = Relative(file);
            if (CodeAllowList.ContainsKey(relative))
                continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();
                // Comments explain colours; they do not paint any.
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("*", StringComparison.Ordinal)
                    || trimmed.StartsWith("@*", StringComparison.Ordinal))
                    continue;

                foreach (Match match in ColourLiteral().Matches(line))
                {
                    // Anchors and element ids ("#themes", "#wall-mode") look like short hex.
                    if (match.Value.StartsWith('#') && Regex.IsMatch(line, $@"(href|id|querySelector|getElementById)[^\n]*{Regex.Escape(match.Value)}"))
                        continue;
                    stray.Add($"{relative}:{i + 1}: {trimmed}");
                }
            }
        }

        Assert.True(stray.Count == 0, "Colours in code — use a var(--token) or a theme colour:\n" + string.Join("\n", stray.Distinct()));
    }

    /// <summary>
    /// Every var(--x) anything reads is defined somewhere — by a theme (the token catalogue),
    /// by a stylesheet, or by Bootstrap. A typo in a token name otherwise fails silently: the
    /// declaration is just dropped, and the element falls back to whatever it inherits.
    /// </summary>
    [Fact]
    public void EveryCustomPropertyReadIsDefined()
    {
        var defined = new HashSet<string>(StringComparer.Ordinal) { "raised-edge" };
        foreach (var token in ThemeTokens.All)
            defined.Add(token.Name);
        foreach (var file in StyleSheets())
            foreach (Match m in VarDefinition().Matches(CssComment().Replace(File.ReadAllText(file), "")))
                defined.Add(m.Groups[1].Value);
        // Set inline from code: --rows on a custom-page block, --accent and friends on <html>.
        foreach (var file in CodeFiles())
            foreach (Match m in VarDefinition().Matches(File.ReadAllText(file)))
                defined.Add(m.Groups[1].Value);
        var bootstrap = File.ReadAllText(Path.Combine(Root, "wwwroot", "lib", "bootstrap", "dist", "css", "bootstrap.min.css"));
        foreach (Match m in VarDefinition().Matches(bootstrap))
            defined.Add(m.Groups[1].Value);

        var undefined = new List<string>();
        foreach (var file in StyleSheets().Concat(CodeFiles()))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in VarUse().Matches(text))
            {
                var name = m.Groups[1].Value;
                // ChartColour builds --chart-N from an index; the catalogue has every N.
                if (!defined.Contains(name) && !name.EndsWith('-'))
                    undefined.Add($"{Relative(file)}: --{name}");
            }
        }

        Assert.True(undefined.Count == 0, "Read but never defined:\n" + string.Join("\n", undefined.Distinct()));
    }

    /// <summary>
    /// Every built-in theme gives every required token in every end it has. The optional
    /// ones always reach the page as well — ThemeCss writes their fallbacks — so between
    /// them every token the stylesheets read is defined by every theme.
    /// </summary>
    [Fact]
    public void EveryBuiltInThemeDefinesEveryRequiredToken()
    {
        foreach (var theme in BuiltInThemes.All)
        {
            foreach (var (dark, variant) in theme.Variants)
                Assert.True(variant.Missing.Count == 0, $"{theme.Id} ({(dark ? "dark" : "light")}) lacks {string.Join(", ", variant.Missing)}");

            var css = ThemeCss.Render(theme);
            foreach (var token in ThemeTokens.All)
                Assert.Contains(token.Property + ":", css);
        }
    }

    /// <summary>
    /// Every token a stylesheet reads that looks like a theme colour is in the catalogue —
    /// otherwise no theme could ever set it.
    /// </summary>
    [Fact]
    public void TheCatalogueCoversTheColourTokensTheStylesheetUses()
    {
        var app = File.ReadAllText(Path.Combine(Root, "wwwroot", "app.css"));
        foreach (var name in new[] { "ink", "panel", "panel-2", "edge", "text", "muted", "accent", "up", "down", "warn", "code", "link", "focus", "overlay", "tooltip-bg", "input-bg", "input-border", "accent-2" })
        {
            Assert.True(ThemeTokens.IsKnown(name), name);
            Assert.Contains($"var(--{name})", app);
        }
    }

    // ---- app.css's built-in defaults and the LabbyTwo theme ------------------------------

    private static Dictionary<string, ThemeColor> Block(string css, string selector)
    {
        var start = css.IndexOf(selector + " {", StringComparison.Ordinal);
        Assert.True(start >= 0, $"No block for {selector}");
        var open = css.IndexOf('{', start);
        var close = css.IndexOf('}', open);
        var result = new Dictionary<string, ThemeColor>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(css[(open + 1)..close], @"--([a-z0-9-]+)\s*:\s*([^;]+);"))
        {
            if (ThemeColor.TryParse(m.Groups[2].Value, out var colour))
                result[m.Groups[1].Value] = colour;
        }
        return result;
    }

    /// <summary>
    /// The default theme must look exactly as the app did before themes: app.css's own token
    /// values, which have not changed, are compared colour for colour with LabbyTwo's.
    /// </summary>
    [Fact]
    public void TheDefaultThemeIsExactlyTheStylesheetsOwnColours()
    {
        var css = File.ReadAllText(Path.Combine(Root, "wwwroot", "app.css"));

        var dark = Block(css, ":root");
        foreach (var name in ThemeTokens.RequiredNames)
            Assert.Equal(dark[name], BuiltInThemes.LabbyTwo.Dark![name]);

        var light = Block(css, ":root[data-theme=\"light\"]");
        foreach (var (name, colour) in light)
            Assert.Equal(colour, BuiltInThemes.LabbyTwo.Light![name]);

        // Tokens the light block does not restate are shared with dark — the accent.
        foreach (var name in ThemeTokens.RequiredNames.Where(n => !light.ContainsKey(n)))
            Assert.Equal(BuiltInThemes.LabbyTwo.Dark![name], BuiltInThemes.LabbyTwo.Light![name]);
    }

    /// <summary>Slate and True black were palettes before they were themes; their surfaces are unchanged.</summary>
    [Fact]
    public void SlateAndBlackKeepTheirOldSurfaces()
    {
        Assert.Equal(ThemeColor.Parse("#14181d"), BuiltInThemes.Slate.Dark!["ink"]);
        Assert.Equal(ThemeColor.Parse("#9aa7b6"), BuiltInThemes.Slate.Dark!["muted"]);
        Assert.Equal(ThemeColor.Parse("#000000"), BuiltInThemes.Black.Dark!["ink"]);
        Assert.Equal(ThemeColor.Parse("rgba(0,0,0,.9)"), BuiltInThemes.Black.Dark!["shadow"]);
        // The rest of a palette was always LabbyTwo's own.
        Assert.Equal(BuiltInThemes.LabbyTwo.Dark!["text"], BuiltInThemes.Slate.Dark!["text"]);
        Assert.Same(BuiltInThemes.LabbyTwo.Light, BuiltInThemes.Black.Light);
    }

    /// <summary>
    /// The optional tokens' fallbacks are written twice — in the catalogue, which the theme
    /// block is built from, and in app.css for a page without one. Same list, same values.
    /// </summary>
    [Fact]
    public void TheStylesheetsOptionalTokensMatchTheCatalogue()
    {
        var css = CssComment().Replace(File.ReadAllText(Path.Combine(Root, "wwwroot", "app.css")), "");
        foreach (var token in ThemeTokens.All.Where(t => !t.Required))
        {
            var match = Regex.Match(css, $@"(?<![\w-]){Regex.Escape(token.Property)}\s*:\s*([^;]+);");
            Assert.True(match.Success, $"app.css does not give {token.Property} a default");
            Assert.Equal(token.Fallback, match.Groups[1].Value.Trim());
        }
    }
}
