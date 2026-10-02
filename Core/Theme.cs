using System.Text.RegularExpressions;

namespace LabbyTwo.Core;

/// <summary>
/// One end of a theme — its dark or its light colours — as token name to colour.
///
/// Holds parsed <see cref="ThemeColor"/>s rather than strings, so a variant that exists is a
/// variant whose every value is already known to be a colour; nothing downstream has to
/// validate again, and nothing downstream can be handed text to paste into CSS.
/// </summary>
public sealed partial class ThemeVariant
{
    private readonly Dictionary<string, ThemeColor> _tokens;

    public ThemeVariant(IReadOnlyDictionary<string, ThemeColor> tokens)
    {
        foreach (var name in tokens.Keys)
        {
            if (!ThemeTokens.IsKnown(name))
                throw new ArgumentException($"'{name}' is not a theme token.", nameof(tokens));
        }
        _tokens = new Dictionary<string, ThemeColor>(tokens, StringComparer.Ordinal);
    }

    /// <summary>For built-in palettes, which are code: throws on a bad name or colour.</summary>
    public static ThemeVariant Of(params (string Name, string Value)[] tokens) =>
        new(tokens.ToDictionary(t => t.Name, t => ThemeColor.Parse(t.Value), StringComparer.Ordinal));

    /// <summary>The tokens this variant sets itself, in catalogue order.</summary>
    public IReadOnlyDictionary<string, ThemeColor> Tokens => _tokens;

    public ThemeColor? this[string name] => _tokens.TryGetValue(name, out var colour) ? colour : null;

    /// <summary>A copy with some tokens replaced or added — how Slate is LabbyTwo with other surfaces.</summary>
    public ThemeVariant With(params (string Name, string Value)[] changes)
    {
        var copy = new Dictionary<string, ThemeColor>(_tokens, StringComparer.Ordinal);
        foreach (var (name, value) in changes)
            copy[name] = ThemeColor.Parse(value);
        return new ThemeVariant(copy);
    }

    /// <summary>A copy with one token set, or removed when <paramref name="colour"/> is null.</summary>
    public ThemeVariant With(string name, ThemeColor? colour)
    {
        var copy = new Dictionary<string, ThemeColor>(_tokens, StringComparer.Ordinal);
        if (colour is { } c)
            copy[name] = c;
        else
            copy.Remove(name);
        return new ThemeVariant(copy);
    }

    /// <summary>Required tokens this variant does not give. Empty for a usable variant.</summary>
    public IReadOnlyList<string> Missing => [.. ThemeTokens.RequiredNames.Where(n => !_tokens.ContainsKey(n))];

    /// <summary>
    /// What a token actually comes out as on screen: its own value, or the fallback app.css
    /// gives it followed through — --link to --accent, --up-soft to a 14% --up. Used by the
    /// contrast checker and the previews, so they judge the colour a viewer will really see
    /// rather than an empty slot. Null only for --selection left to the browser.
    /// </summary>
    public ThemeColor? Effective(string name) => Effective(name, depth: 0);

    private ThemeColor? Effective(string name, int depth)
    {
        if (_tokens.TryGetValue(name, out var own))
            return own;
        if (depth > 8 || ThemeTokens.Find(name)?.Fallback is not { } fallback)
            return null;

        if (VarPattern().Match(fallback) is { Success: true } reference)
            return Effective(reference.Groups[1].Value, depth + 1);

        if (MixPattern().Match(fallback) is { Success: true } mix
            && Effective(mix.Groups[1].Value, depth + 1) is { } mixed)
            return mixed.WithAlpha(int.Parse(mix.Groups[2].Value) / 100d);

        return ThemeColor.TryParse(fallback, out var literal) ? literal : null;
    }

    [GeneratedRegex(@"^var\(--([a-z0-9-]+)\)$")]
    private static partial Regex VarPattern();

    [GeneratedRegex(@"^color-mix\(in srgb, var\(--([a-z0-9-]+)\) (\d+)%, transparent\)$")]
    private static partial Regex MixPattern();
}

/// <summary>
/// A theme: a name, who made it, and a dark and a light set of colours.
///
/// Either set may be missing — Dracula has no light version, and pretending otherwise would
/// mean inventing one. A theme with one variant is used for both: the dark/light setting
/// still exists, but there is only one thing for it to choose, and the page is stamped with
/// that variant's mode so Bootstrap's own pieces (the close button, select arrows) match.
///
/// Deliberately colours only. Anything else a future theme might carry — a background
/// picture, frosted cards — belongs beside this record, not in the token sets, so a theme
/// stays something that can be validated completely and shared without risk.
/// </summary>
/// <param name="Id">Stable, lower-case. Built-ins use plain words; user themes "user-" plus a random id.</param>
public sealed record Theme(
    string Id,
    string Name,
    string Author,
    ThemeVariant? Dark,
    ThemeVariant? Light,
    string Description = "",
    bool BuiltIn = false)
{
    /// <summary>"dark" or "light" when only that variant exists, so the page has to be it; else null.</summary>
    public string? ForcedMode => Dark is null ? "light" : Light is null ? "dark" : null;

    /// <summary>The variant for a dark or light page, falling back to the one there is.</summary>
    public ThemeVariant Variant(bool dark) => (dark ? Dark ?? Light : Light ?? Dark)
        ?? throw new InvalidOperationException($"Theme '{Id}' has no colours at all.");

    /// <summary>The variants that exist, dark first.</summary>
    public IEnumerable<(bool Dark, ThemeVariant Variant)> Variants
    {
        get
        {
            if (Dark is not null)
                yield return (true, Dark);
            if (Light is not null)
                yield return (false, Light);
        }
    }

    /// <summary>The accent the theme ships with, for the dark end where there is one.</summary>
    public ThemeColor Accent => Variant(dark: true).Effective("accent")!.Value; // required, so always there

    /// <summary>The chart palette, series 1 to 8, as they come out for the given end.</summary>
    public IReadOnlyList<ThemeColor> ChartPalette(bool dark)
    {
        var variant = Variant(dark);
        return [.. Enumerable.Range(1, ThemeTokens.ChartSeries)
            .Select(i => variant.Effective($"chart-{i}") ?? variant.Effective("accent")!.Value)];
    }

    /// <summary>"Dark and light", "Dark only" or "Light only", for a gallery tile.</summary>
    public string Coverage => ForcedMode switch
    {
        "dark" => "Dark only",
        "light" => "Light only",
        _ => "Dark and light",
    };
}
