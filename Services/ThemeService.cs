using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// Which screen a theme is being resolved for.
///
/// Phase 1 gives them all the same theme. The parameter exists so that per-screen themes —
/// something calmer on the wall, something brighter on a phone in daylight — are a new
/// setting key and a line in <see cref="ThemeService.ThemeIdFor"/>, not a change to every
/// caller.
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
    /// The data-theme attribute: the theme's only end when it has one, otherwise whatever the
    /// user picked — null for "follow the OS", which leaves it to prefers-color-scheme.
    /// </summary>
    public string? ModeAttribute(Appearance look) => Theme.ForcedMode ?? look.ThemeAttribute;

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
/// <item><see cref="SnapshotAsync"/> decides the mode pushed to open pages — a sunrise/sunset
/// schedule only has to change what it returns and call <see cref="Refresh"/> when the sun
/// crosses the horizon; the CSS already carries both ends, so switching is an attribute.</item>
/// </list>
/// </summary>
public sealed class ThemeService : IDisposable
{
    /// <summary>The setting that names the chosen theme.</summary>
    public const string ThemeIdKey = "theme_id";

    private readonly AppSettingsStore _settings;
    private readonly ThemeStore _store;
    private readonly Lock _gate = new();
    private readonly Dictionary<ThemeSurface, ActiveTheme> _active = [];
    private long _version;

    public ThemeService(AppSettingsStore settings, ThemeStore store)
    {
        _settings = settings;
        _store = store;
        _settings.Changed += Refresh;
        _store.Changed += Refresh;
    }

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
        _ = surface; // One theme everywhere, for now.
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
    /// </summary>
    public async Task<ActiveTheme> ActiveAsync(ThemeSurface surface = ThemeSurface.Dashboard, CancellationToken ct = default)
    {
        long version;
        lock (_gate)
        {
            if (_active.TryGetValue(surface, out var cached))
                return cached;
            version = _version;
        }

        var settings = await _settings.AllAsync(ct);
        var theme = await FindAsync(ThemeIdFor(settings, surface), ct) ?? BuiltInThemes.LabbyTwo;
        var active = ActiveTheme.For(theme);

        lock (_gate)
        {
            // Only kept if nothing changed while it was being built — see VersionedCache.
            if (version == _version)
                _active[surface] = active;
        }
        return active;
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
