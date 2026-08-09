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
    private SettingsBag? _cache;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Raised after a save so the layout can restyle without a reload.</summary>
    public event Action? Changed;

    public async Task<SettingsBag> AllAsync(CancellationToken ct = default)
    {
        if (_cache is not null)
            return _cache;

        await _lock.WaitAsync(ct);
        try
        {
            if (_cache is not null)
                return _cache;

            await using var connection = await db.OpenAsync(ct);
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM app_settings";
            var bag = new SettingsBag();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                bag[reader.GetString(0)] = reader.GetString(1);
            return _cache = bag;
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
        _cache = null;
        Changed?.Invoke();
    }

    public Task SaveAsync(string key, string value, CancellationToken ct = default)
        => SaveAsync(new Dictionary<string, string> { [key] = value }, ct);
}

/// <summary>
/// The look of the app, resolved once per render into the CSS custom properties the
/// stylesheet reads. Kept as a record so a component can hold one without re-querying.
/// </summary>
public sealed record Appearance(string Theme, string Accent, string Density, string BrandName, string UnitSystem)
{
    public const string ThemeKey = "theme";
    public const string AccentKey = "accent";
    public const string DensityKey = "density";
    public const string BrandKey = "brand_name";
    public const string UnitsKey = "units";

    public static Appearance Default => new("system", "#4da3ff", "comfortable", "LabbyTwo", Core.Units.Imperial);

    public static Appearance From(SettingsBag settings) => new(
        settings.Get(ThemeKey, Default.Theme),
        settings.Get(AccentKey, Default.Accent),
        settings.Get(DensityKey, Default.Density),
        settings.Get(BrandKey, Default.BrandName),
        settings.Get(UnitsKey, Default.UnitSystem));

    /// <summary>
    /// "system" is deliberately absent from the attribute: with nothing stamped, the
    /// stylesheet's prefers-color-scheme rules decide, which is what following the OS means.
    /// </summary>
    public string? ThemeAttribute => Theme is "light" or "dark" ? Theme : null;

    /// <summary>Inline overrides for whatever the user picked, applied on the html element.</summary>
    public string StyleAttribute
    {
        get
        {
            var accent = IsValidColor(Accent) ? Accent : Default.Accent;
            var scale = Density == "compact" ? "0.86" : Density == "roomy" ? "1.12" : "1";
            return $"--accent: {accent}; --density: {scale};";
        }
    }

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
