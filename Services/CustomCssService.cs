using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// The custom stylesheet as pages need it, resolved once per change.
///
/// <see cref="Css"/> is the saved text; <see cref="Emitted"/> is what goes into
/// <c>&lt;style id="labby-custom"&gt;</c> — re-checked and escaped (see
/// <see cref="CustomCss.ForStyleElement"/>), or empty if the saved text no longer passes,
/// which only happens to a row edited by hand.
/// </summary>
public sealed record CustomCssState(string Css, bool Enabled, bool OnFamily, string Emitted)
{
    public static readonly CustomCssState Empty = new("", true, false, "");
}

/// <summary>
/// The owner's custom CSS: held in memory for every page render, saved with a history, and
/// announced to open pages when it changes.
///
/// The text is an app setting, so reading it costs what reading any setting costs —
/// nothing, once the settings cache is warm — and the checked, escaped copy is kept here on
/// top of that until a save drops it. <see cref="Changed"/> is how open pages pick a save up
/// without a reload (CustomCssSync); the history is only read from Appearance, through
/// <see cref="Offload"/>.
///
/// It is applied after the theme block, so the order in &lt;head&gt; is app.css, then the
/// theme, then this — the owner's rules win over both, and can use every theme token.
/// </summary>
public sealed class CustomCssService : IDisposable
{
    private readonly AppSettingsStore _settings;
    private readonly CustomCssStore _store;
    private readonly ILogger<CustomCssService> _log;
    private readonly VersionedCache<CustomCssState> _cache = new();
    private readonly TimeProvider _clock;

    public CustomCssService(AppSettingsStore settings, CustomCssStore store, ILogger<CustomCssService> log, TimeProvider? clock = null)
    {
        _settings = settings;
        _store = store;
        _log = log;
        _clock = clock ?? TimeProvider.System;
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>Raised after any settings save. Subscribers compare what they have; this does not.</summary>
    public event Action? Changed;

    public void Dispose() => _settings.Changed -= OnSettingsChanged;

    private void OnSettingsChanged()
    {
        _cache.Invalidate();
        Changed?.Invoke();
    }

    public async Task<CustomCssState> CurrentAsync(CancellationToken ct = default)
    {
        if (_cache.Value is { } cached)
            return cached;

        var version = _cache.Version;
        var settings = await _settings.AllAsync(ct);
        var css = settings.Get(CustomCss.CssKey, "");
        var check = CustomCss.Validate(css);
        if (!check.Ok)
            _log.LogWarning("The saved custom CSS no longer passes its checks and is left out of pages: {Reasons}", string.Join("; ", check.Errors));

        var state = new CustomCssState(
            css,
            settings.Get(CustomCss.EnabledKey, "1") != "0",
            settings.Get(CustomCss.FamilyKey, "0") == "1",
            check.Ok ? CustomCss.ForStyleElement(check.Css) : "");
        return _cache.Store(state, version);
    }

    /// <summary>
    /// What goes in a page's custom block: nothing in safe mode, with the switch off, or on
    /// the family page unless the owner asked for it there — the family page is for the rest
    /// of the house, and an experiment on the dashboard should not reach it by accident.
    /// </summary>
    public async Task<string> CssForAsync(ThemeSurface surface, bool safe, CancellationToken ct = default)
    {
        if (safe)
            return "";
        var state = await CurrentAsync(ct);
        if (!state.Enabled || (surface == ThemeSurface.Family && !state.OnFamily))
            return "";
        return state.Emitted;
    }

    public Task<IReadOnlyList<CustomCssVersion>> HistoryAsync(CancellationToken ct = default) => _store.ListAsync(ct);

    /// <summary>
    /// Checks and saves. Refused CSS is not saved and the check says why; CSS with only
    /// warnings is saved. Saving the same text twice records it once.
    /// </summary>
    public async Task<CustomCss.Check> SaveAsync(string? css, string savedBy, string note = "", CancellationToken ct = default)
    {
        var check = CustomCss.Validate(css);
        if (!check.Ok)
            return check;

        var history = await _store.ListAsync(ct);
        if (history.Count == 0 || history[0].Css != check.Css)
            await _store.AddAsync(check.Css, savedBy ?? "", note, _clock.GetUtcNow(), ct);

        await _settings.SaveAsync(CustomCss.CssKey, check.Css, ct);
        return check;
    }

    /// <summary>Saves an old version as the newest, so restoring is itself in the history and can be undone.</summary>
    public async Task<CustomCss.Check?> RestoreAsync(long id, string savedBy, CancellationToken ct = default)
    {
        if (await _store.FindAsync(id, ct) is not { } version)
            return null;
        var when = version.SavedAt.ToLocalTime().ToString("d MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return await SaveAsync(version.Css, savedBy, $"Restored the version from {when}", ct);
    }

    public Task SetEnabledAsync(bool on, CancellationToken ct = default) =>
        _settings.SaveAsync(CustomCss.EnabledKey, on ? "1" : "0", ct);

    public Task SetOnFamilyAsync(bool on, CancellationToken ct = default) =>
        _settings.SaveAsync(CustomCss.FamilyKey, on ? "1" : "0", ct);
}
