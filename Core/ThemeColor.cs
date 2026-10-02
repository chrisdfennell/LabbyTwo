using System.Globalization;
using System.Text.RegularExpressions;

namespace LabbyTwo.Core;

/// <summary>
/// One colour in a theme, parsed.
///
/// Themes are data, and the only part of them that reaches a stylesheet is their colours —
/// so this is the gate. A value is accepted only if it parses as one of the three notations
/// people actually paste (hex, rgb(), hsl()), and what is written into CSS afterwards is
/// never the text that was pasted: it is <see cref="Css"/>, rebuilt here from the four
/// numbers. Anything that is not a colour — a semicolon, a brace, url(), var(), a named
/// colour, an expression — fails to parse and never gets as far as a string. That is a
/// stronger guarantee than escaping, which would be the wrong tool anyway: there is no
/// escape for "the declaration ends here" inside a value.
/// </summary>
public readonly partial record struct ThemeColor(byte R, byte G, byte B, double A = 1)
{
    /// <summary>
    /// The canonical form: #rrggbb when opaque, rgba(r, g, b, a) otherwise. rgba rather than
    /// eight-digit hex so an alpha of .18 stays .18 and not 46/255 — the default theme's
    /// shadows round-trip through here, and "looks the same as before" means the same.
    /// </summary>
    public string Css => A >= 1
        ? $"#{R:x2}{G:x2}{B:x2}"
        : string.Create(CultureInfo.InvariantCulture, $"rgba({R}, {G}, {B}, {Math.Round(A, 3):0.###})");

    /// <summary>The value an &lt;input type="color"&gt; can show: always six digits, alpha dropped.</summary>
    public string Hex6 => $"#{R:x2}{G:x2}{B:x2}";

    public override string ToString() => Css;

    /// <summary>
    /// WCAG 2.1 relative luminance — the sRGB channels linearised and weighted for how the
    /// eye responds to each. Alpha is ignored: composite over a background first
    /// (<see cref="Over"/>) if the colour is translucent.
    /// </summary>
    public double Luminance
    {
        get
        {
            static double Linear(byte channel)
            {
                var c = channel / 255d;
                return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Linear(R) + 0.7152 * Linear(G) + 0.0722 * Linear(B);
        }
    }

    /// <summary>Light enough that dark text belongs on it.</summary>
    public bool IsLight => Luminance > 0.179;

    /// <summary>This colour painted over <paramref name="background"/>, as the eye ends up seeing it.</summary>
    public ThemeColor Over(ThemeColor background)
    {
        if (A >= 1)
            return this;

        var alpha = A;
        byte Mix(byte top, byte bottom) => (byte)Math.Round(top * alpha + bottom * (1 - alpha));
        return new ThemeColor(Mix(R, background.R), Mix(G, background.G), Mix(B, background.B));
    }

    /// <summary>
    /// <paramref name="amount"/> of <paramref name="other"/> blended into this one — what CSS's
    /// color-mix(in srgb, …) does, for a mapping that needs a shade the palette does not have.
    /// </summary>
    public ThemeColor Mix(ThemeColor other, double amount)
    {
        byte Blend(byte a, byte b) => (byte)Math.Round(a + (b - a) * amount);
        return new ThemeColor(Blend(R, other.R), Blend(G, other.G), Blend(B, other.B), A + (other.A - A) * amount);
    }

    /// <summary>The same colour at a different opacity.</summary>
    public ThemeColor WithAlpha(double alpha) => this with { A = Math.Clamp(alpha, 0, 1) };

    /// <summary>Whether <paramref name="text"/> is a colour this class accepts.</summary>
    public static bool IsValid(string? text) => TryParse(text, out _);

    /// <summary>Parses a colour, throwing for anything else. For built-in palettes, which are code.</summary>
    public static ThemeColor Parse(string text) =>
        TryParse(text, out var colour) ? colour : throw new FormatException($"'{text}' is not a colour.");

    /// <summary>
    /// #rgb, #rgba, #rrggbb, #rrggbbaa, rgb()/rgba() and hsl()/hsla(), in either the comma or
    /// the space-and-slash syntax. Deliberately not named colours, var(), color-mix() or
    /// anything else CSS would accept: a theme has to mean the same thing on every screen
    /// it is opened on, and a colour that depends on something else does not.
    /// </summary>
    public static bool TryParse(string? text, out ThemeColor colour)
    {
        colour = default;
        if (text is null)
            return false;

        var value = text.Trim();
        if (value.Length is 0 or > 64)
            return false;

        if (value[0] == '#')
            return TryHex(value.AsSpan(1), out colour);

        var match = FunctionPattern().Match(value);
        if (!match.Success)
            return false;

        var name = match.Groups["name"].Value.ToLowerInvariant();
        var parts = SplitArguments(match.Groups["args"].Value);
        if (parts is null || parts.Count is < 3 or > 4)
            return false;

        if (!TryAlpha(parts.Count == 4 ? parts[3] : null, out var alpha))
            return false;

        if (name is "rgb" or "rgba")
        {
            if (!TryChannel(parts[0], out var r) || !TryChannel(parts[1], out var g) || !TryChannel(parts[2], out var b))
                return false;
            colour = new ThemeColor(r, g, b, alpha);
            return true;
        }

        // hsl / hsla
        if (!TryHue(parts[0], out var h) || !TryPercent(parts[1], out var s) || !TryPercent(parts[2], out var l))
            return false;

        var (hr, hg, hb) = FromHsl(h, s, l);
        colour = new ThemeColor(hr, hg, hb, alpha);
        return true;
    }

    private static bool TryHex(ReadOnlySpan<char> digits, out ThemeColor colour)
    {
        colour = default;
        foreach (var c in digits)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }

        static byte Pair(ReadOnlySpan<char> s) => byte.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        static byte Single(char c) => (byte)(Convert.ToInt32(c.ToString(), 16) * 17);

        switch (digits.Length)
        {
            case 3:
                colour = new ThemeColor(Single(digits[0]), Single(digits[1]), Single(digits[2]));
                return true;
            case 4:
                colour = new ThemeColor(Single(digits[0]), Single(digits[1]), Single(digits[2]), Single(digits[3]) / 255d);
                return true;
            case 6:
                colour = new ThemeColor(Pair(digits[..2]), Pair(digits[2..4]), Pair(digits[4..6]));
                return true;
            case 8:
                colour = new ThemeColor(Pair(digits[..2]), Pair(digits[2..4]), Pair(digits[4..6]), Pair(digits[6..8]) / 255d);
                return true;
            default:
                return false;
        }
    }

    /// <summary>"10, 20, 30" or "10 20 30 / .5" into its parts; null if it mixes the two.</summary>
    private static List<string>? SplitArguments(string args)
    {
        if (args.Contains(','))
        {
            if (args.Contains('/'))
                return null;
            return [.. args.Split(',').Select(p => p.Trim())];
        }

        var slash = args.Split('/');
        if (slash.Length > 2)
            return null;

        var parts = slash[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (slash.Length == 2)
            parts.Add(slash[1].Trim());
        return parts;
    }

    private static bool TryNumber(string text, out double number) =>
        double.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out number) && double.IsFinite(number);

    private static bool TryChannel(string text, out byte channel)
    {
        channel = 0;
        double value;
        if (text.EndsWith('%'))
        {
            if (!TryNumber(text[..^1], out var percent) || percent is < 0 or > 100)
                return false;
            value = percent * 2.55;
        }
        else if (!TryNumber(text, out value) || value is < 0 or > 255)
        {
            return false;
        }

        channel = (byte)Math.Round(value);
        return true;
    }

    private static bool TryAlpha(string? text, out double alpha)
    {
        alpha = 1;
        if (text is null)
            return true;

        if (text.EndsWith('%'))
        {
            if (!TryNumber(text[..^1], out var percent) || percent is < 0 or > 100)
                return false;
            alpha = percent / 100;
            return true;
        }

        return TryNumber(text, out alpha) && alpha is >= 0 and <= 1;
    }

    private static bool TryHue(string text, out double hue)
    {
        var bare = text.EndsWith("deg", StringComparison.OrdinalIgnoreCase) ? text[..^3] : text;
        if (!TryNumber(bare, out hue))
            return false;
        hue = ((hue % 360) + 360) % 360;
        return true;
    }

    private static bool TryPercent(string text, out double fraction)
    {
        fraction = 0;
        if (!text.EndsWith('%') || !TryNumber(text[..^1], out var percent) || percent is < 0 or > 100)
            return false;
        fraction = percent / 100;
        return true;
    }

    private static (byte R, byte G, byte B) FromHsl(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = (h / 60) switch
        {
            < 1 => (c, x, 0d),
            < 2 => (x, c, 0d),
            < 3 => (0d, c, x),
            < 4 => (0d, x, c),
            < 5 => (x, 0d, c),
            _ => (c, 0d, x),
        };

        static byte To(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return (To(r + m), To(g + m), To(b + m));
    }

    // Letters, digits, spaces, commas, dots, percent signs, minus signs, slashes and "deg" —
    // nothing that can close the function and start something else.
    [GeneratedRegex(@"^(?<name>rgba?|hsla?)\(\s*(?<args>[0-9.,%/\s+\-deg]+)\s*\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FunctionPattern();
}
