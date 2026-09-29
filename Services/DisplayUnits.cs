using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// The units the dashboard is read in right now, kept in memory for everything that draws a
/// reading.
///
/// Most of what draws a number does it synchronously on the render thread — a sentence in a
/// runbook, a tile, a gauge — and none of it can wait on the settings table to find out
/// whether 17.6 should say °C or °F. So the choice is read once, at startup, and again
/// whenever a setting is saved; anything that shows a reading reads <see cref="Current"/>
/// and redraws on <see cref="Changed"/>, which is what makes switching to Celsius on the
/// Appearance page change every card already open rather than only the next page loaded.
/// </summary>
public sealed class DisplayUnits : IDisposable
{
    private readonly AppSettingsStore _settings;
    private volatile Units.Preferences? _current;
    private int _loading;

    public DisplayUnits(AppSettingsStore settings)
    {
        _settings = settings;
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>Raised, off the render thread, when the choice actually changed.</summary>
    public event Action? Changed;

    /// <summary>
    /// The current choice. Before the first read has finished this is the default and a read
    /// is started — the app loads it before serving anything, so only a test or a component
    /// built outside the app ever sees that, and it gets <see cref="Changed"/> when the real
    /// one lands.
    /// </summary>
    public Units.Preferences Current
    {
        get
        {
            if (_current is { } current)
                return current;
            if (Interlocked.Exchange(ref _loading, 1) == 0)
                _ = RefreshAsync();
            return Units.Preferences.Default;
        }
    }

    /// <summary>Reads the choice from the settings, raising <see cref="Changed"/> if it moved.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        Units.Preferences next;
        try
        {
            next = Units.Preferences.From(await _settings.AllAsync(ct));
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // A settings table that cannot be read is not a reason for every card on the page
            // to throw; they keep drawing in whatever units they last had.
            Interlocked.Exchange(ref _loading, 0);
            return;
        }

        var previous = _current;
        _current = next;
        if (previous != next)
            Changed?.Invoke();
    }

    private void OnSettingsChanged() => _ = RefreshAsync();

    public void Dispose() => _settings.Changed -= OnSettingsChanged;
}
