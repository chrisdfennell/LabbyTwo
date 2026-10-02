using System.Globalization;

namespace LabbyTwo.Core;

/// <summary>What is behind the cards.</summary>
public enum BackdropKind { None, Solid, Gradient, Image }

/// <summary>How a picture fills the screen.</summary>
public enum BackdropFit { Cover, Contain, Tile }

/// <summary>Which part of a picture stays in view when the screen's shape crops it.</summary>
public enum BackdropPosition { Center, Top, Bottom, Left, Right }

/// <summary>
/// What the dim washes the background towards. The page colour by default, because that is
/// the colour the text was chosen to be read against — dimming towards it can only help, in
/// a light theme as much as a dark one. The overlay (the shade behind a dialog, usually a
/// translucent black) darkens instead, which suits a dark theme over a bright photo.
/// </summary>
public enum BackdropDimWith { Page, Overlay }

/// <summary>
/// The screens a background is drawn on. Separate from per-screen <em>themes</em> on
/// purpose: a photo that is lovely on the wall can be clutter on a phone, whatever colours
/// either uses.
/// </summary>
[Flags]
public enum BackdropScreens
{
    None = 0,
    Dashboard = 1,
    Wall = 2,
    Phone = 4,
    All = Dashboard | Wall | Phone,
}

/// <summary>
/// One colour in a background: either one of the theme's own tokens, so the background
/// follows the theme (and both its ends), or a fixed colour somebody picked.
///
/// What reaches CSS is <see cref="Css"/>: a var() of the token for a token name that is in
/// the catalogue, or <see cref="ThemeColor.Css"/>, rebuilt from four numbers. The text it
/// was parsed from never does.
/// </summary>
public readonly record struct BackdropColour
{
    private BackdropColour(string? token, ThemeColor? colour)
    {
        Token = token;
        Colour = colour;
    }

    /// <summary>A token name from <see cref="ThemeTokens"/>, or null for a fixed colour.</summary>
    public string? Token { get; }

    /// <summary>The fixed colour, or null for a token.</summary>
    public ThemeColor? Colour { get; }

    public static BackdropColour OfToken(string name) =>
        ThemeTokens.IsKnown(name) ? new(name, null) : throw new ArgumentException($"There is no colour called {name}.", nameof(name));

    public static BackdropColour Of(ThemeColor colour) => new(null, colour.WithAlpha(1));

    /// <summary>
    /// "token:accent" or a plain #rgb / #rrggbb. Nothing else: the stored setting is only ever
    /// written by the Appearance page, which offers exactly these two, so anything that is not
    /// one of them came from somewhere else and is refused rather than interpreted.
    /// </summary>
    public static bool TryParse(string? text, out BackdropColour colour)
    {
        colour = default;
        var value = text?.Trim() ?? "";
        if (value.StartsWith("token:", StringComparison.Ordinal))
        {
            var name = value["token:".Length..];
            if (!ThemeTokens.IsKnown(name))
                return false;
            colour = new BackdropColour(name, null);
            return true;
        }

        if (!IsHex(value) || !ThemeColor.TryParse(value, out var parsed))
            return false;
        colour = Of(parsed);
        return true;
    }

    private static bool IsHex(string value) =>
        value.Length is 4 or 7 && value[0] == '#' && value[1..].All(char.IsAsciiHexDigit);

    public string Css => Token is { } token ? $"var(--{token})" : (Colour ?? default).Css;

    /// <summary>How it is written back into the setting.</summary>
    public string Stored => Token is { } token ? "token:" + token : (Colour ?? default).Hex6;

    /// <summary>What it is in one end of a theme, for the contrast estimate. Translucent tokens are left translucent.</summary>
    public ThemeColor? Resolve(ThemeVariant variant) => Token is { } token ? variant.Effective(token) : Colour;

    public override string ToString() => Stored;
}

/// <summary>
/// The background and frosted-glass settings, read from app settings.
///
/// A record of its own rather than more fields on <see cref="Appearance"/>: everything here
/// is drawn by <see cref="BackdropCss"/> into its own style block, and none of it is a
/// custom property on &lt;html&gt;. Every value is parsed into an enum, a clamped number
/// or a <see cref="BackdropColour"/> here, so whatever is in the database, what the builder
/// sees is already one of a fixed set of things.
///
/// The keys all start backdrop_ or glass_, so they cannot collide with the per-screen theme
/// keys being added alongside.
/// </summary>
public sealed record Backdrop(
    BackdropKind Kind,
    BackdropColour Solid,
    IReadOnlyList<BackdropColour> Stops,
    int Angle,
    BackdropFit Fit,
    BackdropPosition Position,
    int Dim,
    BackdropDimWith DimWith,
    int Blur,
    BackdropScreens Screens,
    string ThemeOnly,
    bool Glass,
    int GlassOpacity,
    int GlassBlur,
    int GlassSaturation,
    bool GlassOnPhone)
{
    public const string KindKey = "backdrop_kind";
    public const string SolidKey = "backdrop_solid";
    public const string StopsKey = "backdrop_stops";
    public const string AngleKey = "backdrop_angle";
    public const string FitKey = "backdrop_fit";
    public const string PositionKey = "backdrop_position";
    public const string DimKey = "backdrop_dim";
    public const string DimWithKey = "backdrop_dim_with";
    public const string BlurKey = "backdrop_blur";
    public const string ScreensKey = "backdrop_screens";
    public const string ThemeOnlyKey = "backdrop_theme";
    public const string GlassKey = "glass";
    public const string GlassOpacityKey = "glass_opacity";
    public const string GlassBlurKey = "glass_blur";
    public const string GlassSaturationKey = "glass_saturation";
    public const string GlassOnPhoneKey = "glass_phone";

    /// <summary>Past 90% the background is not dimmed, it is gone; that is what "None" is for.</summary>
    public const int MaxDim = 90;

    /// <summary>
    /// Blur is drawn once, on a layer that never moves, so its cost does not grow with the
    /// page — but past this the picture is a smear and might as well be a gradient.
    /// </summary>
    public const int MaxBlur = 40;

    /// <summary>
    /// How opaque a glass card's fill is. Not below 30%: under that the card is a pane of
    /// clear glass over a photo and no amount of blur keeps the text on it readable.
    /// </summary>
    public const int MinGlassOpacity = 30;
    public const int MaxGlassOpacity = 95;
    public const int MaxGlassBlur = 40;
    public const int MinSaturation = 100;
    public const int MaxSaturation = 200;

    /// <summary>A gradient has two or three colours: more is a rainbow, fewer is a solid.</summary>
    public const int MinStops = 2;
    public const int MaxStops = 3;

    /// <summary>A gradient that starts from the theme's own colours, so it suits whichever theme is on.</summary>
    public sealed record GradientPreset(string Id, string Name, int Angle, IReadOnlyList<BackdropColour> Stops);

    /// <summary>
    /// The presets are token references, not colours: "Accent" is this theme's accent fading
    /// into its second accent, in Nord as much as in Dracula, and in the light end as much as
    /// the dark one.
    /// </summary>
    public static readonly IReadOnlyList<GradientPreset> Presets =
    [
        new("depth", "Depth", 180, [BackdropColour.OfToken("panel-2"), BackdropColour.OfToken("ink")]),
        new("dusk", "Dusk", 160, [BackdropColour.OfToken("ink"), BackdropColour.OfToken("accent-2")]),
        new("accent", "Accent", 135, [BackdropColour.OfToken("accent"), BackdropColour.OfToken("accent-2")]),
        new("aurora", "Aurora", 135, [BackdropColour.OfToken("chart-3"), BackdropColour.OfToken("chart-1"), BackdropColour.OfToken("chart-4")]),
        new("sunset", "Sunset", 160, [BackdropColour.OfToken("chart-5"), BackdropColour.OfToken("chart-7"), BackdropColour.OfToken("chart-4")]),
    ];

    /// <summary>The theme colours a solid background or a gradient stop can be, in the order offered.</summary>
    public static readonly IReadOnlyList<string> ThemeColours =
        ["ink", "panel", "panel-2", "accent", "accent-2", "up", "down", "warn", "info", "chart-1", "chart-2", "chart-3", "chart-4", "chart-5", "chart-6", "chart-7", "chart-8"];

    public static readonly (BackdropFit Value, string Label, string Hint)[] Fits =
    [
        (BackdropFit.Cover, "Fill the screen", "Cropped to fit — the usual choice for a photo."),
        (BackdropFit.Contain, "Show all of it", "The whole picture, with the page colour round it."),
        (BackdropFit.Tile, "Tile", "Repeated at its own size, for a pattern."),
    ];

    public static readonly (BackdropPosition Value, string Label)[] Positions =
    [
        (BackdropPosition.Center, "Centre"),
        (BackdropPosition.Top, "Top"),
        (BackdropPosition.Bottom, "Bottom"),
        (BackdropPosition.Left, "Left"),
        (BackdropPosition.Right, "Right"),
    ];

    public static Backdrop Default { get; } = new(
        BackdropKind.None,
        BackdropColour.OfToken("panel-2"),
        Presets[0].Stops,
        Presets[0].Angle,
        BackdropFit.Cover,
        BackdropPosition.Center,
        Dim: 30,
        BackdropDimWith.Page,
        Blur: 0,
        BackdropScreens.Dashboard | BackdropScreens.Wall,
        ThemeOnly: "",
        Glass: false,
        GlassOpacity: 72,
        GlassBlur: 14,
        GlassSaturation: 150,
        GlassOnPhone: false);

    /// <summary>Whether anything is drawn at all — the picture case also needs a picture, which the caller knows.</summary>
    public bool IsOn => Kind != BackdropKind.None && Screens != BackdropScreens.None;

    public static Backdrop From(SettingsBag settings)
    {
        var d = Default;
        return new Backdrop(
            Enum<BackdropKind>(settings, KindKey, d.Kind),
            BackdropColour.TryParse(settings.Get(SolidKey), out var solid) ? solid : d.Solid,
            ParseStops(settings.Get(StopsKey)) ?? d.Stops,
            Math.Clamp(settings.GetInt(AngleKey, d.Angle), 0, 359),
            Enum<BackdropFit>(settings, FitKey, d.Fit),
            Enum<BackdropPosition>(settings, PositionKey, d.Position),
            Math.Clamp(settings.GetInt(DimKey, d.Dim), 0, MaxDim),
            Enum<BackdropDimWith>(settings, DimWithKey, d.DimWith),
            Math.Clamp(settings.GetInt(BlurKey, d.Blur), 0, MaxBlur),
            ParseScreens(settings.Get(ScreensKey, Format(d.Screens))),
            ValidThemeId(settings.Get(ThemeOnlyKey)) ? settings.Get(ThemeOnlyKey) : "",
            Flag(settings, GlassKey, d.Glass),
            Math.Clamp(settings.GetInt(GlassOpacityKey, d.GlassOpacity), MinGlassOpacity, MaxGlassOpacity),
            Math.Clamp(settings.GetInt(GlassBlurKey, d.GlassBlur), 0, MaxGlassBlur),
            Math.Clamp(settings.GetInt(GlassSaturationKey, d.GlassSaturation), MinSaturation, MaxSaturation),
            Flag(settings, GlassOnPhoneKey, d.GlassOnPhone));
    }

    /// <summary>Every key with its value, for saving the whole record at once.</summary>
    public Dictionary<string, string> ToSettings() => new()
    {
        [KindKey] = Name(Kind),
        [SolidKey] = Solid.Stored,
        [StopsKey] = string.Join(',', Stops.Select(s => s.Stored)),
        [AngleKey] = Angle.ToString(CultureInfo.InvariantCulture),
        [FitKey] = Name(Fit),
        [PositionKey] = Name(Position),
        [DimKey] = Dim.ToString(CultureInfo.InvariantCulture),
        [DimWithKey] = Name(DimWith),
        [BlurKey] = Blur.ToString(CultureInfo.InvariantCulture),
        [ScreensKey] = Format(Screens),
        [ThemeOnlyKey] = ThemeOnly,
        [GlassKey] = Glass ? "true" : "false",
        [GlassOpacityKey] = GlassOpacity.ToString(CultureInfo.InvariantCulture),
        [GlassBlurKey] = GlassBlur.ToString(CultureInfo.InvariantCulture),
        [GlassSaturationKey] = GlassSaturation.ToString(CultureInfo.InvariantCulture),
        [GlassOnPhoneKey] = GlassOnPhone ? "true" : "false",
    };

    /// <summary>"token:panel-2,#112233" — two or three valid colours, or null for anything else.</summary>
    public static IReadOnlyList<BackdropColour>? ParseStops(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var parts = text.Split(',');
        if (parts.Length is < MinStops or > MaxStops)
            return null;

        var stops = new List<BackdropColour>(parts.Length);
        foreach (var part in parts)
        {
            if (!BackdropColour.TryParse(part, out var stop))
                return null;
            stops.Add(stop);
        }
        return stops;
    }

    /// <summary>"dashboard,wall". Unknown names are ignored; an empty list is no screens.</summary>
    public static BackdropScreens ParseScreens(string? text)
    {
        var screens = BackdropScreens.None;
        foreach (var part in (text ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            screens |= part.ToLowerInvariant() switch
            {
                "dashboard" => BackdropScreens.Dashboard,
                "wall" => BackdropScreens.Wall,
                "phone" => BackdropScreens.Phone,
                _ => BackdropScreens.None,
            };
        }
        return screens;
    }

    public static string Format(BackdropScreens screens) => string.Join(',',
        new[] { (BackdropScreens.Dashboard, "dashboard"), (BackdropScreens.Wall, "wall"), (BackdropScreens.Phone, "phone") }
            .Where(s => screens.HasFlag(s.Item1)).Select(s => s.Item2));

    /// <summary>A theme id as the theme store makes them: letters, digits and hyphens. Only ever compared, never drawn.</summary>
    private static bool ValidThemeId(string id) =>
        id.Length is > 0 and <= 80 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    public static string Name<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();

    private static T Enum<T>(SettingsBag settings, string key, T fallback) where T : struct, Enum
    {
        var text = settings.Get(key);
        // Names only: Enum.TryParse would also take "3", or "1,2" for a flags enum.
        return text.Length > 0 && text.All(char.IsAsciiLetter) && System.Enum.TryParse<T>(text, ignoreCase: true, out var parsed)
               && System.Enum.IsDefined(parsed)
            ? parsed
            : fallback;
    }

    private static bool Flag(SettingsBag settings, string key, bool fallback) =>
        settings.Get(key) switch
        {
            "true" => true,
            "false" => false,
            _ => fallback,
        };
}
