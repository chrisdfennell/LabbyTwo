using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>The background block as a page needs it, with a fingerprint for change detection.</summary>
public sealed record ActiveBackdrop(string Css, string Hash)
{
    public static readonly ActiveBackdrop Empty = new("", ThemeCss.Hash(""));
}

/// <summary>
/// The background style block, held in memory.
///
/// Like <see cref="ThemeService"/>, it is asked for on every page render and must cost
/// nothing: it is built once from the settings (already cached) and the stored picture
/// (held in memory by <see cref="BackdropImageStore"/>), and kept until either changes. It
/// listens to the theme service rather than to settings directly because "only with this
/// theme" depends on which theme each screen is showing — and whatever makes that change,
/// a setting today or a schedule later, the theme service is where it is announced.
///
/// One block serves every screen: it scopes itself to each screen's root element (see
/// <see cref="BackdropCss.Roots"/>), so App.razor, which does not know which layout it is
/// wrapping, can emit the same string everywhere.
/// </summary>
public sealed class BackdropService : IDisposable
{
    private readonly AppSettingsStore _settings;
    private readonly BackdropImageStore _images;
    private readonly ThemeService _themes;
    private readonly Lock _gate = new();
    private ActiveBackdrop? _active;
    private long _version;

    public BackdropService(AppSettingsStore settings, BackdropImageStore images, ThemeService themes)
    {
        _settings = settings;
        _images = images;
        _themes = themes;
        _themes.Changed += Refresh;
        _images.Changed += Refresh;
    }

    /// <summary>Raised after anything that could change the background.</summary>
    public event Action? Changed;

    public void Dispose()
    {
        _themes.Changed -= Refresh;
        _images.Changed -= Refresh;
    }

    public void Refresh()
    {
        lock (_gate)
        {
            _version++;
            _active = null;
        }
        Changed?.Invoke();
    }

    public async Task<ActiveBackdrop> ActiveAsync(CancellationToken ct = default)
    {
        long version;
        lock (_gate)
        {
            if (_active is { } cached)
                return cached;
            version = _version;
        }

        var settings = await _settings.AllAsync(ct);
        var backdrop = Backdrop.From(settings);
        var css = BackdropCss.Render(backdrop, _images.Current(), ScreensFor(backdrop, settings));
        var active = css.Length == 0 ? ActiveBackdrop.Empty : new ActiveBackdrop(css, ThemeCss.Hash(css));

        lock (_gate)
        {
            if (version == _version)
                _active = active;
        }
        return active;
    }

    /// <summary>
    /// The screens whose theme allows the background: all of them, or only those showing the
    /// one theme it was chosen for.
    /// </summary>
    public static BackdropScreens ScreensFor(Backdrop backdrop, SettingsBag settings)
    {
        if (backdrop.ThemeOnly.Length == 0)
            return BackdropScreens.All;

        var screens = BackdropScreens.None;
        foreach (var (screen, surface) in Surfaces)
        {
            if (string.Equals(ThemeService.ThemeIdFor(settings, surface), backdrop.ThemeOnly, StringComparison.Ordinal))
                screens |= screen;
        }
        return screens;
    }

    private static readonly (BackdropScreens Screen, ThemeSurface Surface)[] Surfaces =
    [
        (BackdropScreens.Dashboard, ThemeSurface.Dashboard),
        (BackdropScreens.Wall, ThemeSurface.Wall),
        (BackdropScreens.Phone, ThemeSurface.Phone),
    ];

    /// <summary>Saves the whole record, which is what the Appearance page edits.</summary>
    public Task SaveAsync(Backdrop backdrop, CancellationToken ct = default) =>
        _settings.SaveAsync(backdrop.ToSettings(), ct);
}
