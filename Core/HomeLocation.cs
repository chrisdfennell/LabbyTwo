using System.Globalization;

namespace LabbyTwo.Core;

/// <summary>
/// Where the dashboard is. One place for it, because three things wanted the same pair of
/// numbers — the forecast, the radar and the weather tab — and each asked for them
/// separately, so a decimal typo in one of the three showed you somebody else's weather
/// and nothing said why.
///
/// Anything that needs coordinates should treat its own as an override and fall back here.
/// </summary>
/// <param name="Place">What the user typed, kept only to show them what they picked.</param>
public sealed record HomeLocation(double? Latitude, double? Longitude, string Place)
{
    public const string LatitudeKey = "home_lat";
    public const string LongitudeKey = "home_lon";
    public const string PlaceKey = "home_place";

    public static HomeLocation None => new(null, null, "");

    public static HomeLocation From(SettingsBag settings) => new(
        Number(settings.Get(LatitudeKey)),
        Number(settings.Get(LongitudeKey)),
        settings.Get(PlaceKey));

    public bool IsSet => Latitude is not null && Longitude is not null;

    /// <summary>What to show when there is nowhere set — deliberately not a guess at one.</summary>
    public string Describe() => !IsSet
        ? "No location set."
        : Place is { Length: > 0 } place
            ? $"{place} ({Format(Latitude)}, {Format(Longitude)})"
            : $"{Format(Latitude)}, {Format(Longitude)}";

    /// <summary>
    /// Written and read invariantly throughout. A machine set to a comma decimal separator
    /// would otherwise store "39,7392" and read it back as the number 397392.
    /// </summary>
    public static string Format(double? value) =>
        value?.ToString("0.####", CultureInfo.InvariantCulture) ?? "";

    public static double? Number(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// The coordinates to actually use, given whatever a connection or widget set for
    /// itself. Its own values win; blank means "wherever home is".
    /// </summary>
    public (double Latitude, double Longitude)? Resolve(string ownLatitude, string ownLongitude)
    {
        var latitude = Number(ownLatitude) ?? Latitude;
        var longitude = Number(ownLongitude) ?? Longitude;

        return latitude is { } lat && longitude is { } lon ? (lat, lon) : null;
    }
}
