using System.Security.Cryptography;
using System.Text;

namespace LabbyTwo.Core;

/// <summary>
/// Turns a <see cref="Theme"/> into the custom properties the stylesheet reads.
///
/// The output is built entirely from two things: the token names in
/// <see cref="ThemeTokens"/>, which are constants, and <see cref="ThemeColor.Css"/>, which
/// is rebuilt from four numbers. A theme's name, author or description never reaches it,
/// so there is nothing a theme can contain — imported, pasted or typed — that becomes CSS
/// other than a colour.
///
/// Every variant block declares <em>every</em> token: the theme's own value, or the
/// fallback from the catalogue for one it leaves unset. That matters when a theme has two
/// ends. The dark block's selector also matches a light page (it is plain :root, the base),
/// so a token the light end left out would otherwise quietly inherit the dark end's value.
/// </summary>
public static class ThemeCss
{
    /// <summary>
    /// The block for &lt;head&gt;. Same three-state structure as app.css — dark as the base,
    /// light under prefers-color-scheme unless dark was chosen, light when it was — so the
    /// dark/light/follow-the-OS setting keeps working with no script. A theme with one end
    /// is that end everywhere; the page is also stamped with its mode (see
    /// <see cref="Theme.ForcedMode"/>), which this does not rely on but Bootstrap does.
    /// </summary>
    public static string Render(Theme theme)
    {
        // Not even the id goes in a comment: a user theme's id comes from a database row, and
        // "nothing but token names and colours" is only a guarantee if it has no exceptions.
        var css = new StringBuilder();

        if (theme.Dark is { } dark && theme.Light is { } light)
        {
            Block(css, ":root, :root[data-theme=\"dark\"]", dark, isDark: true);
            css.Append("@media (prefers-color-scheme: light) {\n");
            Block(css, ":root:not([data-theme=\"dark\"])", light, isDark: false);
            css.Append("}\n");
            Block(css, ":root[data-theme=\"light\"]", light, isDark: false);
        }
        else
        {
            var only = theme.Variant(dark: true);
            Block(css, ":root, :root[data-theme=\"dark\"], :root[data-theme=\"light\"]", only, isDark: theme.Dark is not null);
            css.Append("@media (prefers-color-scheme: light) {\n");
            Block(css, ":root:not([data-theme=\"dark\"])", only, isDark: theme.Dark is not null);
            css.Append("}\n");
        }

        return css.ToString();
    }

    private static void Block(StringBuilder css, string selector, ThemeVariant variant, bool isDark)
    {
        css.Append(selector).Append(" { ").Append(Declarations(variant, isDark)).Append(" }\n");
    }

    /// <summary>
    /// One variant as a run of declarations, for a style block or a style attribute — the
    /// gallery's previews use the same string inline, so a tile is coloured by exactly what
    /// the page would be.
    /// </summary>
    public static string Declarations(ThemeVariant variant, bool isDark)
    {
        var css = new StringBuilder();
        css.Append("color-scheme: ").Append(isDark ? "dark" : "light").Append(';');

        foreach (var token in ThemeTokens.All)
        {
            var value = variant[token.Name]?.Css ?? token.Fallback;
            if (value is null)
                continue;
            css.Append(' ').Append(token.Property).Append(": ").Append(value).Append(';');
        }

        // A raised card's border. A shadow is a darkening of what is behind it, and nothing
        // is darker than black, so on a pure-black page the lift never renders and a raised
        // card would lose every edge it had. Any theme whose page is black keeps the border.
        var raisedEdge = variant.Effective("ink") is { } ink && ink.Luminance < 0.0005 ? "var(--edge)" : "transparent";
        css.Append(" --raised-edge: ").Append(raisedEdge).Append(';');

        return css.ToString();
    }

    /// <summary>A short, stable fingerprint of a rendered block, for cache keys and change detection.</summary>
    public static string Hash(string css) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(css)))[..12];
}
