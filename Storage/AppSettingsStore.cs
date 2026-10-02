using LabbyTwo.Core;
using Microsoft.Data.Sqlite;

namespace LabbyTwo.Storage;

/// <summary>
/// App-level preferences — theme, accent, what the greeting says. Distinct from
/// <see cref="LabbyOptions"/>, which is deployment configuration set by whoever runs the
/// container; this is the stuff a user changes from the UI, so it lives in the database
/// and travels with a backup.
/// </summary>
public sealed class AppSettingsStore(Db db)
{
    // Versioned so a load cannot store settings from before a save that had already
    // invalidated them — see VersionedCache.
    private readonly VersionedCache<SettingsBag> _cache = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Raised after a save so the layout can restyle without a reload.</summary>
    public event Action? Changed;

    public async Task<SettingsBag> AllAsync(CancellationToken ct = default)
    {
        if (_cache.Value is { } cached)
            return cached;

        await _lock.WaitAsync(ct);
        try
        {
            if (_cache.Value is { } loaded)
                return loaded;

            var version = _cache.Version;
            await using var connection = await db.OpenAsync(ct);
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM app_settings";
            var bag = new SettingsBag();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                bag[reader.GetString(0)] = reader.GetString(1);
            return _cache.Store(bag, version);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string> GetAsync(string key, string fallback = "", CancellationToken ct = default)
        => (await AllAsync(ct)).Get(key, fallback);

    public async Task SaveAsync(IReadOnlyDictionary<string, string> values, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var (key, value) in values)
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO app_settings (key, value) VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value
                """;
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        _cache.Invalidate();
        Changed?.Invoke();
    }

    public Task SaveAsync(string key, string value, CancellationToken ct = default)
        => SaveAsync(new Dictionary<string, string> { [key] = value }, ct);
}

/// <summary>
/// The look of the app, resolved once per render into the CSS custom properties the
/// stylesheet reads. Kept as a record so a component can hold one without re-querying.
/// </summary>
/// <summary>
/// How the dashboard looks.
///
/// Every one of these is either a CSS custom property or an attribute on the html element —
/// see <see cref="StyleAttribute"/>. That is the whole reason there can be this many without
/// the stylesheet turning into a matrix of combinations: the page is written against the
/// tokens, and these set the tokens.
///
/// New properties are appended rather than inserted, and each has a matching key with a
/// default, so a database written by an older version reads back with the new ones at their
/// defaults rather than blank.
/// </summary>
public sealed record Appearance(
    string Theme,
    string Accent,
    string Density,
    string BrandName,
    string UnitSystem,
    string Radius,
    string Surface,
    // Read only to carry an old "dark shade" choice over to its theme — see ThemeService.ThemeIdFor.
    string DarkPalette,
    string TextScale,
    string Font,
    string FontFamily,
    string FontUrl,
    string WideScreens)
{
    public const string ThemeKey = "theme";
    public const string AccentKey = "accent";
    public const string DensityKey = "density";
    public const string BrandKey = "brand_name";
    public const string UnitsKey = "units";
    public const string RadiusKey = "radius";
    public const string SurfaceKey = "surface";
    public const string DarkPaletteKey = "dark_palette";
    public const string TextScaleKey = "text_scale";
    public const string FontKey = "font";
    public const string FontFamilyKey = "font_family";
    public const string FontUrlKey = "font_url";
    public const string WideScreensKey = "wide_screens";

    public static Appearance Default => new(
        "system", "", "comfortable", "LabbyTwo", Core.Units.Imperial,
        "rounded", "outlined", "midnight", "normal", "sans", "", "", "columns");

    public static Appearance From(SettingsBag settings) => new(
        settings.Get(ThemeKey, Default.Theme),
        settings.Get(AccentKey, Default.Accent),
        settings.Get(DensityKey, Default.Density),
        settings.Get(BrandKey, Default.BrandName),
        settings.Get(UnitsKey, Default.UnitSystem),
        settings.Get(RadiusKey, Default.Radius),
        settings.Get(SurfaceKey, Default.Surface),
        settings.Get(DarkPaletteKey, Default.DarkPalette),
        settings.Get(TextScaleKey, Default.TextScale),
        settings.Get(FontKey, Default.Font),
        settings.Get(FontFamilyKey, Default.FontFamily),
        settings.Get(FontUrlKey, Default.FontUrl),
        settings.Get(WideScreensKey, Default.WideScreens));

    /// <summary>
    /// The choices themselves, so the settings page renders from the same list the CSS is
    /// written against and the two cannot drift into offering something that does nothing.
    /// </summary>
    public static readonly (string Value, string Label, string Hint)[] Radii =
    [
        ("sharp", "Sharp", "Square corners, barely softened."),
        ("rounded", "Rounded", "The default."),
        ("soft", "Soft", "Generously rounded."),
    ];

    public static readonly (string Value, string Label, string Hint)[] Surfaces =
    [
        ("outlined", "Outlined", "A hairline border and no shadow. The default."),
        ("raised", "Raised", "No border — cards lift off the page with a soft shadow."),
        ("flat", "Flat", "No border and no shadow; cards are told apart by their fill alone."),
    ];

    public static readonly (string Value, string Label, string Hint)[] TextScales =
    [
        ("small", "Small", ""),
        ("normal", "Normal", ""),
        ("large", "Large", ""),
        ("huge", "Huge", "Readable from across a room."),
    ];

    public static readonly (string Value, string Label, string Hint)[] Fonts =
    [
        ("sans", "Sans", "The system's own interface font."),
        ("serif", "Serif", ""),
        ("mono", "Monospace", "Every figure the same width, so columns of numbers line up."),
        ("custom", "Custom", "Your own — a family name, an uploaded file, or a web font."),
    ];

    /// <summary>
    /// What a dashboard does with a screen wider than it was laid out for.
    ///
    /// Cards are sized in twelfths of the row, which is right on a laptop and wrong on an
    /// ultra-wide: stretched across 5000 pixels a quarter-width card is a banner, and capped
    /// at 1600 the page is a strip down the middle of the monitor. Adding columns as the row
    /// grows keeps a card about the size it was and fits more of them in, which is what a
    /// bigger screen is usually bought for — so that is the default, and the other two are
    /// there for somebody who wants the cards bigger or the page as it always was.
    /// </summary>
    public static readonly (string Value, string Label, string Hint)[] WideScreenModes =
    [
        ("columns", "More cards per row",
            "The dashboard uses the whole width, and the grid gains columns as it grows so each card keeps about the size it has on an ordinary monitor. The default."),
        ("stretch", "Stretch cards to fill",
            "The whole width, still twelve columns — the same layout as a laptop, every card wider."),
        ("centred", "Keep it centred",
            "A column 1600 pixels wide down the middle, with the rest of the screen left empty."),
    ];

    /// <summary>
    /// A CSS font-family list, and the only free text in this record that reaches a style
    /// attribute. It is checked rather than escaped, because escaping a value that lands
    /// inside a declaration is the wrong tool: a single semicolon ends the declaration and
    /// everything after it is a new one somebody else wrote.
    ///
    /// Letters, digits, spaces, commas, hyphens, underscores, full stops and quotes cover
    /// every real font stack — "Segoe UI", 'JetBrains Mono', Helvetica-Neue — and exclude
    /// the brackets and semicolons that make an injection.
    /// </summary>
    public static bool IsValidFontFamily(string? value) =>
        value is { Length: > 0 and <= 200 }
        && value.All(c => char.IsLetterOrDigit(c) || c is ' ' or ',' or '-' or '_' or '.' or '\'' or '"');

    /// <summary>
    /// A stylesheet URL for a hosted web font. https only, and absolute — a relative one
    /// would be resolved against this app and a javascript: one is not a stylesheet at all.
    ///
    /// This is the one setting in here that makes the dashboard depend on something outside
    /// the house. It is off unless somebody fills it in, and the page still draws without it.
    /// </summary>
    public static bool IsValidFontUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var url)
        && url.Scheme == Uri.UriSchemeHttps;

    /// <summary>
    /// Accent swatches. More than a handful, because this is the one setting people actually
    /// fiddle with, and picking from colours that were chosen to work against both palettes
    /// beats hunting in a colour wheel for one that does.
    /// </summary>
    public static readonly (string Value, string Name)[] AccentPresets =
    [
        ("#4da3ff", "Blue"),
        ("#2f6fed", "Deep blue"),
        ("#38bdf8", "Sky"),
        ("#22d3ee", "Cyan"),
        ("#2dd4bf", "Teal"),
        ("#35d07f", "Green"),
        ("#84cc16", "Lime"),
        ("#eab308", "Amber"),
        ("#f97316", "Orange"),
        ("#ef4444", "Red"),
        ("#f472b6", "Pink"),
        ("#a855f7", "Purple"),
        ("#8b5cf6", "Violet"),
        ("#94a3b8", "Grey"),
    ];

    /// <summary>
    /// "system" is deliberately absent from the attribute: with nothing stamped, the
    /// stylesheet's prefers-color-scheme rules decide, which is what following the OS means.
    /// </summary>
    public string? ThemeAttribute => Theme is "light" or "dark" ? Theme : null;

    /// <summary>Card treatment, for the same reason: it is a set of rules, not a value.</summary>
    public string? SurfaceAttribute =>
        Surfaces.Any(s => s.Value == Surface) && Surface != "outlined" ? Surface : null;

    /// <summary>
    /// The wide-screen treatment, as an attribute for the same reason as the surface: it
    /// switches rules on and off rather than setting a value. Absent for the default, and
    /// for anything unrecognised, so a stray value gets the default rather than nothing.
    /// </summary>
    public string? WideAttribute =>
        WideScreenModes.Any(m => m.Value == WideScreens) && WideScreens != "columns" ? WideScreens : null;

    /// <summary>
    /// Inline overrides for whatever the user picked, applied on the html element.
    ///
    /// Everything here is a number or a font stack this class chose from a fixed list — the
    /// one value that comes from a text box, the accent, is checked by
    /// <see cref="IsValidColor"/> first, because this string goes into a style attribute and
    /// anything unvalidated in it is an injection point rather than a colour.
    ///
    /// The accent is only here when somebody chose one over their theme's. Inline on the html
    /// element it outranks every rule in the theme block, which is exactly what an override
    /// is for; left out, the theme's own accent stands — per end, so a theme whose light
    /// accent is darker than its dark one keeps both.
    /// </summary>
    public string StyleAttribute
    {
        get
        {
            var accent = AccentOverride is { } chosen ? $"--accent: {chosen}; " : "";

            var density = Density switch { "compact" => "0.86", "roomy" => "1.12", _ => "1" };

            var radius = Radius switch { "sharp" => "0.25rem", "soft" => "1.25rem", _ => "0.75rem" };

            // Deliberately independent of density. Density is how much air there is; this is
            // how big the words are, and "compact and huge" — a dense wall display read from
            // the hallway — is a combination somebody genuinely wants.
            var text = TextScale switch
            {
                "small" => "0.92",
                "large" => "1.12",
                "huge" => "1.25",
                _ => "1",
            };

            // The three built-ins are system stacks, so the default install still downloads
            // nothing before a page can draw itself. "custom" is the opt-in that gives that
            // up, and only as far as whoever chose it asked for.
            var font = Font switch
            {
                "serif" => "Georgia, Cambria, 'Times New Roman', serif",
                "mono" => "ui-monospace, 'Cascadia Mono', Consolas, 'Liberation Mono', monospace",
                "custom" => CustomStack,
                _ => "system-ui, -apple-system, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif",
            };

            return $"{accent}--density: {density}; --radius: {radius}; "
                 + $"--text-scale: {text}; --font-body: {font};";
        }
    }

    /// <summary>
    /// What "custom" resolves to.
    ///
    /// The uploaded family goes first when there is one, with whatever was typed behind it
    /// and the system sans behind that — so an upload that fails to load, a family the device
    /// has never heard of and an empty box all end at something readable rather than at the
    /// browser's default serif.
    /// </summary>
    private string CustomStack
    {
        get
        {
            var typed = IsValidFontFamily(FontFamily) ? FontFamily.Trim() : "";
            var parts = new List<string>();

            if (HasUploadedFont)
                parts.Add($"'{FontStore.Family}'");
            if (typed.Length > 0)
                parts.Add(typed);

            parts.Add("system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif");
            return string.Join(", ", parts);
        }
    }

    /// <summary>
    /// Set by whoever builds this when a file is actually on disk. A property rather than a
    /// stored setting, because the file either exists or it does not and a setting saying
    /// otherwise is a setting that can be wrong.
    /// </summary>
    public bool HasUploadedFont { get; init; }

    /// <summary>The stylesheet to pull in, or null. Only ever set for the custom typeface.</summary>
    public string? WebFontUrl =>
        Font == "custom" && IsValidFontUrl(FontUrl) ? FontUrl : null;

    /// <summary>
    /// The accent somebody picked over their theme's, or null to use the theme's. Empty is
    /// "the theme's" by design — choosing a theme clears it — and anything stored that is not
    /// a plain hex colour is treated the same way rather than reaching the page.
    /// </summary>
    public string? AccentOverride => IsValidColor(Accent) ? Accent : null;

    /// <summary>
    /// Only #rgb and #rrggbb get through. The value goes into a style attribute, so
    /// anything else would be an injection point rather than a colour.
    /// </summary>
    public static bool IsValidColor(string? value) =>
        value is not null
        && (value.Length == 4 || value.Length == 7)
        && value[0] == '#'
        && value[1..].All(Uri.IsHexDigit);
}
