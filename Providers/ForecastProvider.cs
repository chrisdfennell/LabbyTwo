using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// Open-Meteo. A weather station tells you what is happening on your roof; this tells you
/// what is about to happen, which is the half a dashboard was missing — and it needs no
/// account, no key and no terms of service, which is why it is this one rather than any of
/// the alternatives.
/// </summary>
public sealed class ForecastProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "forecast";
    public string DisplayName => "Weather forecast";
    public string Icon => "🌦️";
    public string Category => "Sensors";
    public string Description => "Today's high and low, rain expected, and what it is doing now. No API key needed.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("latitude", "Latitude", FieldKind.Text, "39.7392", Required: true,
            Help: "Decimal degrees. The same pair the weather station tab uses."),

        new("longitude", "Longitude", FieldKind.Text, "-104.9903", Required: true),

        new("units", "Units", FieldKind.Select, Default: "auto", Options:
        [
            new SelectOption("auto", "Follow the app's setting"),
            new SelectOption("metric", "Celsius and millimetres"),
            new SelectOption("imperial", "Fahrenheit and inches"),
        ]),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("temp_now_c", "Now", "°C", 1),
        new("temp_high_c", "Today's high", "°C", 1),
        new("temp_low_c", "Today's low", "°C", 1),
        new("rain_chance_percent", "Chance of rain", "%", 0),
        new("rain_today_mm", "Rain expected", " mm", 1),
        new("wind_kph", "Wind", " km/h", 1),
        new("latency_ms", "Response time", " ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Freezing tonight", "temp_low_c", Comparison.Below, 0, ForMinutes: 60,
            Why: "Worth knowing the evening before rather than the morning after — pipes, plants, the car."),

        new("Rain on the way", "rain_chance_percent", Comparison.Above, 70, ForMinutes: 60,
            Why: "Pair it with a notification if you are the one who leaves washing out."),
    ];

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var latitude = connection.Settings.Get("latitude");
        var longitude = connection.Settings.Get("longitude");

        if (latitude.Length == 0 || longitude.Length == 0)
            return ProbeResult.Down(TimeSpan.Zero, "Needs a latitude and longitude.");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            // Always fetched in metric and converted for display by the usual machinery, so
            // one recorded history reads correctly whichever units the app is set to.
            var url = "https://api.open-meteo.com/v1/forecast" +
                      $"?latitude={Uri.EscapeDataString(latitude)}&longitude={Uri.EscapeDataString(longitude)}" +
                      "&current=temperature_2m,wind_speed_10m,precipitation" +
                      "&daily=temperature_2m_max,temperature_2m_min,precipitation_sum,precipitation_probability_max" +
                      "&forecast_days=2&timezone=auto";

            var http = httpFactory.CreateClient(ProviderHttp.ClientName);
            using var response = await http.GetAsync(url, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                // Open-Meteo explains itself properly, so pass its own words along.
                using var error = JsonDocument.Parse(body);
                var reason = error.RootElement.TryGetProperty("reason", out var text) ? text.GetString() : null;
                return ProbeResult.Down(stopwatch.Elapsed,
                    reason is { Length: > 0 } ? $"Open-Meteo said: {reason}" : $"HTTP {(int)response.StatusCode}");
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var metrics = new Dictionary<string, double> { ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds };

            if (root.TryGetProperty("current", out var current))
            {
                Copy(current, "temperature_2m", metrics, "temp_now_c");
                Copy(current, "wind_speed_10m", metrics, "wind_kph");
            }

            if (root.TryGetProperty("daily", out var daily))
            {
                Copy(First(daily, "temperature_2m_max"), metrics, "temp_high_c");
                Copy(First(daily, "temperature_2m_min"), metrics, "temp_low_c");
                Copy(First(daily, "precipitation_sum"), metrics, "rain_today_mm");
                Copy(First(daily, "precipitation_probability_max"), metrics, "rain_chance_percent");
            }

            var message = metrics.TryGetValue("temp_high_c", out var high) && metrics.TryGetValue("temp_low_c", out var low)
                ? $"{high:0.#}° / {low:0.#}°" +
                  (metrics.TryGetValue("rain_chance_percent", out var chance) && chance > 0
                      ? $", {chance:0}% rain"
                      : "")
                : "Connected";

            return ProbeResult.Up(stopwatch.Elapsed, message, metrics);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, "api.open-meteo.com"));
        }
    }

    /// <summary>Daily values arrive as parallel arrays; today is the first entry of each.</summary>
    private static double? First(JsonElement daily, string name) =>
        daily.TryGetProperty(name, out var series)
        && series.ValueKind == JsonValueKind.Array
        && series.GetArrayLength() > 0
        && series[0].ValueKind == JsonValueKind.Number
            ? series[0].GetDouble()
            : null;

    private static void Copy(double? value, Dictionary<string, double> metrics, string key)
    {
        if (value is { } number)
            metrics[key] = number;
    }

    private static void Copy(JsonElement element, string from, Dictionary<string, double> metrics, string to)
    {
        if (element.TryGetProperty(from, out var value) && value.ValueKind == JsonValueKind.Number)
            metrics[to] = value.GetDouble();
    }
}
