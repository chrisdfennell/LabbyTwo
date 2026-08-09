using System.Globalization;

namespace LabbyTwo.Core;

public enum RadarKind
{
    /// <summary>A still image or animated GIF the card refreshes on a timer.</summary>
    Image,

    /// <summary>Somebody else's interactive map, in an iframe. It animates itself.</summary>
    Embed,
}

/// <summary>
/// Where a radar picture comes from. Like <see cref="SearchEngine"/>, this is a list of
/// URL templates rather than an extension point: every source is "put the coordinates in
/// this URL", and none of them needs code. Adding one is a line here.
/// </summary>
/// <param name="Coverage">Said plainly, because half of these are one country only.</param>
/// <param name="NeedsStation">True for sources addressed by radar site rather than by coordinates.</param>
public sealed record RadarSource(
    string Key,
    string DisplayName,
    RadarKind Kind,
    string UrlTemplate,
    string Coverage,
    bool NeedsStation = false,
    string Attribution = "")
{
    public const string CustomImageKey = "custom-image";
    public const string CustomEmbedKey = "custom-embed";

    public static readonly IReadOnlyList<RadarSource> All =
    [
        new("rainviewer", "RainViewer", RadarKind.Embed,
            "https://www.rainviewer.com/map.html?loc={lat},{lon},{zoom}&oFa=0&oC=0&oU=0&oCS=1&oF=0&oAP=1&c=3&o=83&lm=1&layer=radar&sm=1&sc=1",
            "Worldwide", Attribution: "RainViewer"),

        new("windy", "Windy", RadarKind.Embed,
            "https://embed.windy.com/embed2.html?lat={lat}&lon={lon}&zoom={zoom}&level=surface&overlay=radar&menu=&message=&marker=true&calendar=now&pressure=&type=map&location=coordinates&detailLat={lat}&detailLon={lon}&metricWind=default&metricTemp=default&radarRange=-1",
            "Worldwide", Attribution: "Windy.com"),

        // NOAA addresses radar by site code, not by coordinates — hence NeedsStation.
        new("nws", "NOAA / NWS (US)", RadarKind.Image,
            "https://radar.weather.gov/ridge/standard/{station}_loop.gif",
            "United States", NeedsStation: true, Attribution: "NOAA / National Weather Service"),

        new("nws-conus", "NOAA — whole US", RadarKind.Image,
            "https://radar.weather.gov/ridge/standard/CONUS_loop.gif",
            "United States", Attribution: "NOAA / National Weather Service"),

        new("rainviewer-uk", "RainViewer — Europe", RadarKind.Embed,
            "https://www.rainviewer.com/map.html?loc={lat},{lon},{zoom}&oFa=0&oC=0&oU=0&oCS=1&oF=0&oAP=1&c=3&o=83&lm=1&layer=radar&sm=1&sc=1",
            "Worldwide, centred on your coordinates", Attribution: "RainViewer"),

        new(CustomImageKey, "Custom — image URL", RadarKind.Image, "", "Wherever you point it"),
        new(CustomEmbedKey, "Custom — embedded page", RadarKind.Embed, "", "Wherever you point it"),
    ];

    public static IReadOnlyList<SelectOption> Options =>
        [.. All.Select(source => new SelectOption(source.Key, $"{source.DisplayName} — {source.Coverage}"))];

    public static RadarSource Resolve(string? key) =>
        All.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public bool IsCustom => Key is CustomImageKey or CustomEmbedKey;

    /// <summary>
    /// Fills in the template. Coordinates are formatted invariantly on purpose — a comma
    /// decimal separator would silently produce a URL pointing somewhere else entirely.
    /// </summary>
    public string BuildUrl(double latitude, double longitude, int zoom, string station, string customUrl)
    {
        var template = IsCustom ? customUrl.Trim() : UrlTemplate;
        if (template.Length == 0)
            return "";

        return template
            .Replace("{lat}", latitude.ToString("0.####", CultureInfo.InvariantCulture))
            .Replace("{lon}", longitude.ToString("0.####", CultureInfo.InvariantCulture))
            .Replace("{zoom}", Math.Clamp(zoom, 1, 15).ToString(CultureInfo.InvariantCulture))
            .Replace("{station}", Uri.EscapeDataString(station.Trim().ToUpperInvariant()));
    }
}
