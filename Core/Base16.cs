using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace LabbyTwo.Core;

/// <summary>
/// Base16 colour schemes, turned into LabbyTwo themes.
///
/// Base16 is the lingua franca of editor and terminal themes: sixteen colours with fixed
/// jobs, published as hundreds of schemes. Accepting it means anybody's favourite scheme is
/// a paste away. Both shapes are read — the classic flat YAML (scheme, author, base00…) and
/// the newer tinted-theming one (name, author, variant, palette: {base00…}) — and, because
/// JSON is YAML, the same keys in JSON as well.
///
/// <para>The mapping:</para>
/// <list type="table">
/// <item><term>base00</term><description>ink (the page)</description></item>
/// <item><term>base01</term><description>panel (cards)</description></item>
/// <item><term>base02</term><description>panel-2, selection; edge is base02 blended a third of the way to base03</description></item>
/// <item><term>base03</term><description>placeholder, neutral</description></item>
/// <item><term>base04</term><description>muted (secondary text)</description></item>
/// <item><term>base05</term><description>text</description></item>
/// <item><term>base08</term><description>down, chart 7</description></item>
/// <item><term>base09</term><description>code, chart 5</description></item>
/// <item><term>base0A</term><description>warn, chart 2</description></item>
/// <item><term>base0B</term><description>up, chart 3</description></item>
/// <item><term>base0C</term><description>info, chart 6</description></item>
/// <item><term>base0D</term><description>accent, link (and so chart 1)</description></item>
/// <item><term>base0E</term><description>accent-2, chart 4</description></item>
/// <item><term>base0F</term><description>chart 8</description></item>
/// </list>
/// <para>
/// accent-ink is base00 or base07, whichever stands out more against base0D. A scheme makes
/// one variant only — light if base00 is a light colour, dark otherwise — because base16
/// describes one look, and inventing the other end of it would be guessing.
/// </para>
/// </summary>
public static class Base16
{
    public static readonly IReadOnlyList<string> Slots =
        ["base00", "base01", "base02", "base03", "base04", "base05", "base06", "base07",
         "base08", "base09", "base0A", "base0B", "base0C", "base0D", "base0E", "base0F"];

    /// <summary>A parsed scheme: its name, author and the sixteen colours.</summary>
    public sealed record Scheme(string Name, string Author, IReadOnlyDictionary<string, ThemeColor> Colours)
    {
        public ThemeColor this[string slot] => Colours[slot];

        /// <summary>Light when its background is: decided by luminance, not by what the file claims.</summary>
        public bool IsLight => Colours["base00"].IsLight;
    }

    /// <summary>Whether a paste looks like base16 at all, so the importer knows which reader to use.</summary>
    public static bool LooksLike(string? text) =>
        text is not null && text.Contains("base00", StringComparison.OrdinalIgnoreCase)
        && text.Contains("base0D", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses and maps in one step.</summary>
    public static ThemeJson.Result Import(string? text)
    {
        var (scheme, errors) = Parse(text);
        return scheme is null ? new ThemeJson.Result(null, errors) : new ThemeJson.Result(ToTheme(scheme), []);
    }

    public static (Scheme? Scheme, IReadOnlyList<string> Errors) Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, ["There is nothing to import."]);
        if (Encoding.UTF8.GetByteCount(text) > ThemeJson.MaxBytes)
            return (null, ["That is too big to be a colour scheme."]);

        Dictionary<object, object?>? root;
        try
        {
            // Into plain dictionaries and strings only — no type is ever chosen by the file.
            root = new DeserializerBuilder().Build().Deserialize<Dictionary<object, object?>>(text);
        }
        catch (YamlException)
        {
            return (null, ["That is not valid YAML or JSON."]);
        }

        if (root is null)
            return (null, ["That is empty."]);

        // The palette may be at the top (classic) or under "palette" (tinted-theming).
        var palette = root.TryGetValue("palette", out var nested) && nested is Dictionary<object, object?> inner ? inner : root;

        var errors = new List<string>();
        var colours = new Dictionary<string, ThemeColor>(StringComparer.Ordinal);
        foreach (var slot in Slots)
        {
            var value = Lookup(palette, slot);
            if (value is null)
            {
                errors.Add($"\"{slot}\" is missing.");
                continue;
            }

            var hex = value.Trim().TrimStart('#');
            if (hex.Length != 6 || !hex.All(Uri.IsHexDigit))
            {
                errors.Add($"\"{slot}\" is not a six-digit hex colour.");
                continue;
            }

            colours[slot] = ThemeColor.Parse("#" + hex);
        }

        if (errors.Count > 0)
            return (null, errors);

        var name = ThemeJson.CleanText(Lookup(root, "scheme") ?? Lookup(root, "name") ?? "");
        var author = ThemeJson.CleanText(Lookup(root, "author") ?? "");
        if (name.Length == 0)
            name = "Imported scheme";

        return (new Scheme(Clip(name, ThemeJson.MaxName), Clip(author, ThemeJson.MaxName), colours), []);
    }

    /// <summary>Slot names are matched without caring about case: base0a and base0A are both seen in the wild.</summary>
    private static string? Lookup(Dictionary<object, object?> map, string key)
    {
        foreach (var (k, v) in map)
        {
            if (k is string s && string.Equals(s, key, StringComparison.OrdinalIgnoreCase) && v is string or int or long or double)
                return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
        }
        return null;
    }

    private static string Clip(string text, int max) => text.Length > max ? text[..max] : text;

    /// <summary>The mapping in the summary above.</summary>
    public static Theme ToTheme(Scheme s)
    {
        var light = s.IsLight;
        var accent = s["base0D"];
        var accentInk = Contrast.Ratio(s["base00"], accent) >= Contrast.Ratio(s["base07"], accent) ? s["base00"] : s["base07"];
        var shadow = light ? s["base05"].WithAlpha(0.18) : new ThemeColor(0, 0, 0, 0.55);

        var tokens = new Dictionary<string, ThemeColor>(StringComparer.Ordinal)
        {
            ["ink"] = s["base00"],
            ["panel"] = s["base01"],
            ["panel-2"] = s["base02"],
            ["edge"] = s["base02"].Mix(s["base03"], 1d / 3),
            ["selection"] = s["base02"],
            ["placeholder"] = s["base03"],
            ["neutral"] = s["base03"],
            ["muted"] = s["base04"],
            ["text"] = s["base05"],
            ["accent"] = accent,
            ["accent-ink"] = accentInk,
            ["link"] = accent,
            ["accent-2"] = s["base0E"],
            ["up"] = s["base0B"],
            ["down"] = s["base08"],
            ["warn"] = s["base0A"],
            ["info"] = s["base0C"],
            ["code"] = s["base09"],
            ["chart-2"] = s["base0A"],
            ["chart-3"] = s["base0B"],
            ["chart-4"] = s["base0E"],
            ["chart-5"] = s["base09"],
            ["chart-6"] = s["base0C"],
            ["chart-7"] = s["base08"],
            ["chart-8"] = s["base0F"],
            ["shadow"] = shadow,
        };

        var variant = new ThemeVariant(tokens);
        return new Theme("", s.Name, s.Author, light ? null : variant, light ? variant : null,
            "Imported from a base16 scheme.");
    }
}
