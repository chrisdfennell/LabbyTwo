using System.Diagnostics;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// An Ambient Weather personal weather station. Every reading the station sends becomes
/// a metric, so the chart widget can plot any of them without weather-specific code.
/// </summary>
public sealed class AmbientWeatherProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "ambient";
    public string DisplayName => "Ambient Weather station";
    public string Icon => "🌤️";
    public string Category => "Sensors";
    public string Description => "Live readings from an Ambient Weather station. Keys come from ambientweather.net/account.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("api_key", "API key", FieldKind.Password, Required: true),
        new("app_key", "Application key", FieldKind.Password, Required: true),
        new("device_mac", "Device MAC", FieldKind.Text,
            Help: "Optional — only needed when the account has more than one station."),
    ];

    // Stored in Celsius and the station's own imperial units for wind and rain; the
    // weather widget converts for display, but a chart of the raw metric still reads right.
    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("temp_outdoor_c", "Outdoor temperature", "°C", 1),
        new("temp_indoor_c", "Indoor temperature", "°C", 1),
        new("feels_like_c", "Feels like", "°C", 1),
        new("dew_point_c", "Dew point", "°C", 1),
        new("humidity", "Outdoor humidity", "%"),
        new("humidity_indoor", "Indoor humidity", "%"),
        new("wind_mph", "Wind", "mph", 1),
        new("gust_mph", "Wind gust", "mph", 1),
        new("wind_dir", "Wind direction", "°"),
        new("pressure_inhg", "Pressure", "inHg", 2),
        new("rain_in", "Rain today", "in", 2),
        new("solar_wm2", "Solar radiation", "W/m²"),
        new("uv_index", "UV index"),
        new("latency_ms", "Response time", "ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Frost", "temp_outdoor_c", Comparison.Below, 0, ClearThreshold: 2,
            Why: "Plants, pipes and the windscreen."),
        new("Hard freeze", "temp_outdoor_c", Comparison.Below, -6, ClearThreshold: -3,
            Why: "Cold enough to burst an unlagged pipe."),
        new("High wind", "gust_mph", Comparison.Above, 40, ClearThreshold: 30,
            Why: "Bins, trampolines and garden furniture."),
        new("Heat", "temp_outdoor_c", Comparison.Above, 32, ClearThreshold: 29, ForMinutes: 15,
            Why: "Sustained, so a thermometer in afternoon sun does not set it off."),
    ];

    /// <summary>The station's latest reading, kept so the weather widget renders without its own call.</summary>
    public sealed record Reading(IReadOnlyDictionary<string, double> Values, DateTimeOffset At, string Station);

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var reading = await ReadAsync(connection, ct);
            stopwatch.Stop();
            var metrics = new Dictionary<string, double>(reading.Values) { ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds };
            var summary = reading.Values.TryGetValue("temp_outdoor_c", out var temp)
                ? $"{CToF(temp):0.0}°F at {reading.Station}"
                : reading.Station;
            return ProbeResult.Up(stopwatch.Elapsed, summary, metrics);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ex.GetBaseException().Message);
        }
    }

    public async Task<Reading> ReadAsync(Connection connection, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(ProviderHttp.ClientName);
        var url = "https://rt.ambientweather.net/v1/devices" +
                  $"?applicationKey={Uri.EscapeDataString(connection.Settings.Get("app_key"))}" +
                  $"&apiKey={Uri.EscapeDataString(connection.Settings.Get("api_key"))}";

        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? "Ambient Weather rejected the keys."
                : $"Ambient Weather answered HTTP {(int)response.StatusCode}.");
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            throw new InvalidOperationException("The account has no weather stations.");

        var wanted = connection.Settings.Get("device_mac");
        var device = doc.RootElement.EnumerateArray().FirstOrDefault(d =>
            wanted.Length == 0 ||
            (d.TryGetProperty("macAddress", out var mac) && string.Equals(mac.GetString(), wanted, StringComparison.OrdinalIgnoreCase)));
        if (device.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"No station matched the MAC \"{wanted}\".");

        var station = device.TryGetProperty("info", out var info) && info.TryGetProperty("name", out var name)
            ? name.GetString() ?? "station"
            : "station";

        if (!device.TryGetProperty("lastData", out var last))
            throw new InvalidOperationException("The station has not reported any data yet.");

        var values = new Dictionary<string, double>();
        void Copy(string source, string metric, Func<double, double>? convert = null)
        {
            if (last.TryGetProperty(source, out var element) && element.ValueKind == JsonValueKind.Number)
                values[metric] = convert is null ? element.GetDouble() : convert(element.GetDouble());
        }

        // Ambient reports imperial; store Celsius so the metric names mean one thing
        // everywhere and the display layer converts.
        Copy("tempf", "temp_outdoor_c", FToC);
        Copy("tempinf", "temp_indoor_c", FToC);
        Copy("humidity", "humidity");
        Copy("humidityin", "humidity_indoor");
        Copy("windspeedmph", "wind_mph");
        Copy("windgustmph", "gust_mph");
        Copy("winddir", "wind_dir");
        Copy("baromrelin", "pressure_inhg");
        Copy("dailyrainin", "rain_in");
        Copy("solarradiation", "solar_wm2");
        Copy("uv", "uv_index");
        Copy("feelsLike", "feels_like_c", FToC);
        Copy("dewPoint", "dew_point_c", FToC);

        var at = last.TryGetProperty("dateutc", out var stamp) && stamp.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeMilliseconds(stamp.GetInt64()).ToLocalTime()
            : DateTimeOffset.Now;

        return new Reading(values, at, station);
    }

    public static double FToC(double f) => (f - 32) * 5 / 9;
    public static double CToF(double c) => c * 9 / 5 + 32;
}
