using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// Which screen a theme is being resolved for.
///
/// Each can wear the dashboard's theme (the default) or one of its own, with its own light/dark
/// mode — something calmer on the wall, a phone that follows the sun. See
/// <see cref="ThemeService.ThemeIdFor"/> and <see cref="ThemeService.ModeFor"/>.
/// </summary>
public enum ThemeSurface
{
    Dashboard,
    Wall,
    Phone,
    Family,
}

/// <summary>
/// A theme resolved into what a page needs: the CSS block for &lt;head&gt; and a fingerprint
/// of it. Built once per change, not per render.
/// </summary>
public sealed record ActiveTheme(Theme Theme, string Css, string Hash)
{
    public static ActiveTheme For(Theme theme)
    {
        var css = ThemeCss.Render(theme);
        return new ActiveTheme(theme, css, ThemeCss.Hash(css));
    }

    /// <summary>
    /// The light/dark mode as decided for this surface when it was resolved — including
    /// "follow the sun" and "on a schedule", which <see cref="Appearance.ThemeAttribute"/>
    /// alone cannot answer. Null on one built with <see cref="For"/> outside ThemeService,
    /// which then falls back to the plain setting.
    /// </summary>
    public ModeDecision? Decision { get; init; }

    /// <summary>
    /// The data-theme attribute: the theme's only end when it has one, otherwise whatever the
    /// mode came to — null for "follow the OS", which leaves it to prefers-color-scheme.
    /// The same answer the server stamps on a fresh page and ThemeSync pushes to an open one.
    /// </summary>
    public string? ModeAttribute(Appearance look) =>
        Theme.ForcedMode ?? (Decision is { } decision ? decision.Attribute : look.ThemeAttribute);

    /// <summary>The browser-chrome colour: the page colour of whichever end is showing (dark for "follow the OS").</summary>
    public string MetaColour(Appearance look) =>
        // ink is a required token, so every variant that exists has one.
        Theme.Variant(dark: ModeAttribute(look) != "light").Effective("ink")!.Value.Hex6;
}

/// <summary>
/// What a page that is already open needs to restyle itself: the theme block, the mode, the
/// accent override and the browser-chrome colour. Pushed by ThemeSync when any of it changes.
/// </summary>
public sealed record ThemeSnapshot(string Css, string Hash, string? Mode, string? Accent, string Meta);

/// <summary>
/// The active theme, held in memory.
///
/// Every page render asks for it — App.razor puts its CSS in &lt;head&gt; — so it must cost
/// nothing: the settings and the user themes are both in-memory caches already, and the
/// rendered CSS is kept here on top of them until either changes. A save anywhere in
/// settings or themes drops it and raises <see cref="Changed"/>, which is how open pages
/// (via ThemeSync) restyle without a reload.
///
/// <para>Phase 2 hooks, deliberately left as single places to change:</para>
/// <list type="bullet">
/// <item><see cref="ThemeIdFor"/> is the one place that maps settings to a theme id, per
/// <see cref="ThemeSurface"/> — add per-screen keys there.</item>
/// <item><see cref="SnapshotAsync"/> decides the mode pushed to open pages. For the automatic
/// modes each cached entry carries the instant its mode next changes and is re-decided (not
/// re-rendered) once that passes, so a fresh page is right to the second; ThemeScheduler calls
/// <see cref="Refresh"/> at the crossing so open pages follow. The CSS already carries both
/// ends, so switching is an attribute.</item>
/// </list>
/// </summary>
public sealed class ThemeService : IDisposable
{
    /// <summary>The setting that names the chosen theme.</summary>
    public const string ThemeIdKey = "theme_id";

    /// <summary>Per-screen overrides: "" (the default) means "the same as the dashboard".</summary>
    public const string WallThemeKey = "theme_wall_id";
    public const string WallModeKey = "theme_wall_mode";
    public const string PhoneThemeKey = "theme_phone_id";
    public const string PhoneModeKey = "theme_phone_mode";
    public const string FamilyThemeKey = "theme_family_id";
    public const string FamilyModeKey = "theme_family_mode";

    /// <summary>The screens that can be given their own look, in the order the settings page lists them.</summary>
    public static readonly ThemeSurface[] OwnSurfaces = [ThemeSurface.Wall, ThemeSurface.Phone, ThemeSurface.Family];

    private readonly AppSettingsStore _settings;
    private readonly ThemeStore _store;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private readonly Dictionary<ThemeSurface, ActiveTheme> _active = [];
    private long _version;

    public ThemeService(AppSettingsStore settings, ThemeStore store, TimeProvider? clock = null)
    {
        _settings = settings;
        _store = store;
        _clock = clock ?? TimeProvider.System;
        _settings.Changed += Refresh;
        _store.Changed += Refresh;
    }

    /// <summary>
    /// The zone the sun and the schedule are read in. The server's own, like every other
    /// schedule in the app; settable so tests can pin one with clock changes in it.
    /// </summary>
    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Local;

    /// <summary>Raised after anything that could change the look. Subscribers compare snapshots; this does not.</summary>
    public event Action? Changed;

    public void Dispose()
    {
        _settings.Changed -= Refresh;
        _store.Changed -= Refresh;
    }

    /// <summary>Drops the cached theme and tells open pages to check theirs.</summary>
    public void Refresh()
    {
        lock (_gate)
        {
            _version++;
            _active.Clear();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// The chosen theme's id from settings. A database from before themes existed has no
    /// theme_id but may have the old "dark shade" setting, which named Slate or True black —
    /// both are themes now, so that choice carries straight across without a migration.
    /// </summary>
    public static string ThemeIdFor(SettingsBag settings, ThemeSurface surface = ThemeSurface.Dashboard)
    {
        // A screen with a theme of its own uses it; one left blank wears the dashboard's.
        if (ThemeKeyFor(surface) is { } own && settings.Get(own, "") is { Length: > 0 } chosen)
            return chosen;

        var id = settings.Get(ThemeIdKey, "");
        if (id.Length > 0)
            return id;

        return settings.Get(Appearance.DarkPaletteKey, "") switch
        {
            "slate" => BuiltInThemes.Slate.Id,
            "black" => BuiltInThemes.Black.Id,
            _ => BuiltInThemes.DefaultId,
        };
    }

    /// <summary>The setting holding a screen's own theme id, or null for the dashboard (which is <see cref="ThemeIdKey"/>).</summary>
    public static string? ThemeKeyFor(ThemeSurface surface) => surface switch
    {
        ThemeSurface.Wall => WallThemeKey,
        ThemeSurface.Phone => PhoneThemeKey,
        ThemeSurface.Family => FamilyThemeKey,
        _ => null,
    };

    /// <summary>The setting holding a screen's own mode, or the dashboard's own "theme" key.</summary>
    public static string ModeKeyFor(ThemeSurface surface) => surface switch
    {
        ThemeSurface.Wall => WallModeKey,
        ThemeSurface.Phone => PhoneModeKey,
        ThemeSurface.Family => FamilyModeKey,
        _ => Appearance.ThemeKey,
    };

    /// <summary>
    /// The light/dark mode a screen uses: its own when it has one, else the dashboard's. A
    /// value this version does not know — from a newer one, or typed into the database — is
    /// "follow the device", the one choice that can never be wrong for anybody.
    /// </summary>
    public static string ModeFor(SettingsBag settings, ThemeSurface surface = ThemeSurface.Dashboard)
    {
        var own = surface == ThemeSurface.Dashboard ? "" : settings.Get(ModeKeyFor(surface), "");
        var mode = own.Length > 0 ? own : settings.Get(Appearance.ThemeKey, ThemeModes.Auto);
        return ThemeModes.IsKnown(mode) ? mode : ThemeModes.Auto;
    }

    /// <summary>What a screen's mode comes to at <paramref name="now"/>. Arithmetic only — the settings are already in memory.</summary>
    public static ModeDecision DecideMode(SettingsBag settings, ThemeSurface surface, TimeZoneInfo zone, DateTimeOffset now) =>
        ThemeSchedule.Decide(ModeFor(settings, surface), ThemeTimes.From(settings), HomeLocation.From(settings), zone, now);

    /// <summary>
    /// Which screen a request is for, from its path, so the server-rendered page wears the
    /// same theme ThemeSync will keep it in. /t/{tab}?wall=1 redirects to /wall, so these
    /// prefixes are the whole of it.
    /// </summary>
    public static ThemeSurface SurfaceForPath(string? path)
    {
        static bool Under(string path, string prefix) =>
            path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);

        path ??= "";
        return Under(path, "/wall") ? ThemeSurface.Wall
            : Under(path, "/m") ? ThemeSurface.Phone
            : Under(path, "/family") ? ThemeSurface.Family
            : ThemeSurface.Dashboard;
    }

    /// <summary>
    /// The soonest moment any screen's mode changes by itself, for ThemeScheduler. Null when
    /// nothing is on an automatic mode.
    /// </summary>
    public async Task<DateTimeOffset?> NextSwitchAsync(CancellationToken ct = default)
    {
        var settings = await _settings.AllAsync(ct);
        var now = _clock.GetUtcNow();
        DateTimeOffset? soonest = null;
        foreach (var surface in Enum.GetValues<ThemeSurface>())
        {
            if (DecideMode(settings, surface, Zone, now).NextSwitch is { } next && (soonest is null || next < soonest))
                soonest = next;
        }
        return soonest;
    }

    /// <summary>What a screen's mode is now, for the settings page.</summary>
    public async Task<ModeDecision> DecisionAsync(ThemeSurface surface = ThemeSurface.Dashboard, CancellationToken ct = default) =>
        DecideMode(await _settings.AllAsync(ct), surface, Zone, _clock.GetUtcNow());

    /// <summary>Now, by the clock this service was given — so the settings page and the tests agree with it.</summary>
    public DateTimeOffset Now => _clock.GetUtcNow();

    /// <summary>Built-ins first, then the user's own by name.</summary>
    public async Task<IReadOnlyList<Theme>> AllAsync(CancellationToken ct = default) =>
        [.. BuiltInThemes.All, .. await _store.AllAsync(ct)];

    public async Task<Theme?> FindAsync(string? id, CancellationToken ct = default)
    {
        if (BuiltInThemes.Find(id) is { } builtIn)
            return builtIn;
        return id is null ? null : await _store.FindAsync(id, ct);
    }

    /// <summary>
    /// The active theme for a surface. An id that no longer exists — a deleted user theme, a
    /// row that stopped validating — resolves to the default rather than to nothing.
    ///
    /// The cached entry is good until its mode's next switch. After that it is re-decided —
    /// arithmetic on settings already in memory — and its CSS kept, because the theme has
    /// not changed, only which end of it shows.
    /// </summary>
    public async Task<ActiveTheme> ActiveAsync(ThemeSurface surface = ThemeSurface.Dashboard, CancellationToken ct = default)
    {
        long version;
        ActiveTheme? stale = null;
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (_active.TryGetValue(surface, out var cached))
            {
                if (cached.Decision?.NextSwitch is not { } until || now < until)
                    return cached;
                stale = cached;
            }
            version = _version;
        }

        var settings = await _settings.AllAsync(ct);
        var decision = DecideMode(settings, surface, Zone, now);
        var id = ThemeIdFor(settings, surface);
        var active = stale is not null && stale.Theme.Id == id
            ? stale with { Decision = decision }
            : ActiveTheme.For(await ResolveAsync(settings, surface, id, ct)) with { Decision = decision };

        lock (_gate)
        {
            // Only kept if nothing changed while it was being built — see VersionedCache.
            if (version == _version)
                _active[surface] = active;
        }
        return active;
    }

    /// <summary>
    /// A screen whose own theme has been deleted falls back to the dashboard's — what it
    /// would wear had nobody chosen — and only then to the default.
    /// </summary>
    private async Task<Theme> ResolveAsync(SettingsBag settings, ThemeSurface surface, string id, CancellationToken ct)
    {
        if (await FindAsync(id, ct) is { } theme)
            return theme;
        if (surface != ThemeSurface.Dashboard && await FindAsync(ThemeIdFor(settings), ct) is { } dashboard)
            return dashboard;
        return BuiltInThemes.LabbyTwo;
    }

    /// <summary>Everything an open page needs to match the current settings.</summary>
    public async Task<ThemeSnapshot> SnapshotAsync(ThemeSurface surface = ThemeSurface.Dashboard, CancellationToken ct = default)
    {
        var look = Appearance.From(await _settings.AllAsync(ct));
        var active = await ActiveAsync(surface, ct);
        return new ThemeSnapshot(active.Css, active.Hash, active.ModeAttribute(look), look.AccentOverride, active.MetaColour(look));
    }

    /// <summary>
    /// Makes a theme the active one. The accent override is cleared at the same time: a theme
    /// comes with its own accent, and picking Dracula only to keep the blue you chose for the
    /// default would be the opposite of what the click meant.
    /// </summary>
    public Task ApplyAsync(string id, CancellationToken ct = default) =>
        _settings.SaveAsync(new Dictionary<string, string>
        {
            [ThemeIdKey] = id,
            [Appearance.AccentKey] = "",
        }, ct);
}
