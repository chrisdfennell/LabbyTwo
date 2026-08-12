using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Providers;

/// <summary>
/// Air quality from Open-Meteo — no key, same as the forecast.
///
/// A separate connection rather than more numbers on the forecast: it is a different
/// question with a different answer. "Is it going to rain?" is planning; "can the kids play
/// outside?" is a decision you make in the next ten minutes, and on a smoke day in Colorado
/// it is the only weather number anybody looks at.
/// </summary>
public sealed class AirQualityProvider(IHttpClientFactory httpFactory, AppSettingsStore appSettings) : IConnectionProvider
{
    public string Type => "air-quality";
    public string DisplayName => "Air quality";
    public string Icon => "😷";
    public string Category => "Sensors";
    public string Description => "AQI, smoke particulates and ozone from Open-Meteo. No API key needed.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("latitude", "Latitude", FieldKind.Text,
            Help: "Leave both blank to use the location set in Settings."),

        new("longitude", "Longitude", FieldKind.Text),

        new("scale", "Index to use", FieldKind.Select, Default: "us", Options:
        [
            new SelectOption("us", "US AQI — the 0–500 scale"),
            new SelectOption("european", "European AQI — the 0–100+ scale"),
        ]),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("aqi", "Air quality index"),
        new("pm2_5", "PM2.5", " µg/m³", 1),
        new("pm10", "PM10", " µg/m³", 1),
        new("ozone", "Ozone", " µg/m³", 0),
        new("nitrogen_dioxide", "Nitrogen dioxide", " µg/m³", 0),
        new("carbon_monoxide", "Carbon monoxide", " µg/m³", 0),
        new("sulphur_dioxide", "Sulphur dioxide", " µg/m³", 0),
        new("dust", "Dust", " µg/m³", 0),
        new("uv_index", "UV index", "", 1),
        new("latency_ms", "Response time", " ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Unhealthy air", "aqi", Comparison.Above, 100, ClearThreshold: 80, ForMinutes: 30,
            Why: "Above 100 is the point at which the advice changes for children, older people and anyone with asthma."),

        new("Hazardous air", "aqi", Comparison.Above, 200, ClearThreshold: 150, ForMinutes: 15,
            Why: "Wildfire smoke territory — windows shut, and nobody outside."),

        new("Fine particulates", "pm2_5", Comparison.Above, 35, ClearThreshold: 25, ForMinutes: 30,
            Why: "The EPA's 24-hour limit. Smoke is mostly this, and it is what gets into your lungs."),
    ];

    /// <summary>
    /// The US AQI bands, in the EPA's own words. Anything above 300 is "hazardous", which
    /// is a word worth using unchanged rather than softening into "very poor".
    /// </summary>
    public static (string Label, string Css, string Advice) Band(double aqi) => aqi switch
    {
        <= 50 => ("Good", "is-good", "Nothing to think about."),
        <= 100 => ("Moderate", "is-ok", "Fine for most people; unusually sensitive people may notice it."),
        <= 150 => ("Unhealthy for sensitive groups", "is-warn",
            "Children, older people and anyone with asthma or a heart condition should take it easy outdoors."),
        <= 200 => ("Unhealthy", "is-bad", "Everyone should limit long or hard exertion outdoors."),
        <= 300 => ("Very unhealthy", "is-bad", "Stay indoors with the windows shut where you can."),
        _ => ("Hazardous", "is-bad", "Emergency conditions. Everyone should stay indoors."),
    };

    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, double>> _latest = new();

    /// <summary>The last reading, for a card that wants the pollutants and not just the index.</summary>
    public IReadOnlyDictionary<string, double> Latest(Connection connection) =>
        _latest.TryGetValue(connection.Id, out var values) ? values : new Dictionary<string, double>();

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var home = HomeLocation.From(await appSettings.AllAsync(ct));
        if (home.Resolve(connection.Settings.Get("latitude"), connection.Settings.Get("longitude"))
            is not var (latitude, longitude))
        {
            return ProbeResult.Down(TimeSpan.Zero,
                "No coordinates: set a location in Settings, or give this connection its own.");
        }

        var european = connection.Settings.Get("scale", "us") == "european";
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var url = "https://air-quality-api.open-meteo.com/v1/air-quality" +
                      $"?latitude={HomeLocation.Format(latitude)}&longitude={HomeLocation.Format(longitude)}" +
                      "&current=us_aqi,european_aqi,pm10,pm2_5,carbon_monoxide,nitrogen_dioxide," +
                      "sulphur_dioxide,ozone,dust,uv_index" +
                      "&timezone=auto";

            var http = httpFactory.CreateClient(ProviderHttp.ClientName);
            using var response = await http.GetAsync(url, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                using var error = JsonDocument.Parse(body);
                var reason = error.RootElement.TryGetProperty("reason", out var text) ? text.GetString() : null;
                return ProbeResult.Down(stopwatch.Elapsed,
                    reason is { Length: > 0 } ? $"Open-Meteo said: {reason}" : $"HTTP {(int)response.StatusCode}");
            }

            using var document = JsonDocument.Parse(body);
            var metrics = Read(document.RootElement, european);
            metrics["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds;
            _latest[connection.Id] = metrics;

            var message = metrics.TryGetValue("aqi", out var aqi)
                ? $"AQI {aqi:0} — {Band(aqi).Label}"
                : "Connected";

            return ProbeResult.Up(stopwatch.Elapsed, message, metrics);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, "air-quality-api.open-meteo.com"));
        }
    }

    /// <summary>
    /// Both indices are requested and one is kept as <c>aqi</c>, so a rule written against
    /// it keeps meaning the same thing — the two scales are not comparable, and silently
    /// recording whichever the user last picked would make history nonsense.
    /// </summary>
    public static Dictionary<string, double> Read(JsonElement root, bool european)
    {
        var metrics = new Dictionary<string, double>();
        if (!root.TryGetProperty("current", out var current))
            return metrics;

        foreach (var key in new[]
                 {
                     "pm10", "pm2_5", "carbon_monoxide", "nitrogen_dioxide",
                     "sulphur_dioxide", "ozone", "dust", "uv_index",
                 })
        {
            if (Number(current, key) is { } value)
                metrics[key] = value;
        }

        if (Number(current, european ? "european_aqi" : "us_aqi") is { } aqi)
            metrics["aqi"] = aqi;

        return metrics;
    }

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
}
