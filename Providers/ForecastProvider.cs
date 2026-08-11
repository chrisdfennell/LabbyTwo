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
    public string Description => "A week ahead — highs, lows and rain, day by day. No API key needed.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("latitude", "Latitude", FieldKind.Text, "39.7392", Required: true,
            Help: "Decimal degrees. The same pair the weather station tab uses."),

        new("longitude", "Longitude", FieldKind.Text, "-104.9903", Required: true),

        new("days", "Days to forecast", FieldKind.Number, Default: "7",
            Help: "Up to 16. Open-Meteo's accuracy falls off a cliff after about a week, which is why " +
                  "this defaults to seven rather than the maximum."),
    ];

    /// <summary>One day of the forecast, in the app's canonical units — Celsius and inches.</summary>
    /// <param name="Code">A WMO weather code — 0 is clear, 95 upwards is thunder.</param>
    public sealed record Day(
        DateOnly Date, double High, double Low, double RainInches, double RainChance, int Code)
    {
        /// <summary>
        /// WMO codes grouped the way a person reads a forecast, rather than the fifty-odd
        /// distinctions the standard makes between kinds of drizzle.
        /// </summary>
        public string Icon => Code switch
        {
            0 => "☀️",
            1 or 2 => "🌤️",
            3 => "☁️",
            45 or 48 => "🌫️",
            >= 51 and <= 57 => "🌦️",
            >= 61 and <= 67 => "🌧️",
            >= 71 and <= 77 => "🌨️",
            >= 80 and <= 82 => "🌦️",
            85 or 86 => "🌨️",
            >= 95 and <= 99 => "⛈️",
            // Codes stop at 99, so anything else is a number this did not expect.
            _ => "☁️",
        };

        /// <summary>
        /// Today is worth naming; tomorrow is not — abbreviating it fits the column but
        /// reads as a man's name, and "Wed" beside "Today" is unambiguous anyway.
        /// </summary>
        public string Label(DateOnly today) =>
            Date == today ? "Today" : Date.ToString("ddd", CultureInfo.CurrentCulture);
    }

    // The forecast a widget wants is a week of days, and a metric is one number — so the
    // days are kept here, on the singleton, and the card reads them. Same arrangement the
    // weather station and Plex providers use for data richer than metrics can carry.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<Day>> _days = new();

    /// <summary>The days from the last probe, or empty if this connection has not run yet.</summary>
    public IReadOnlyList<Day> Days(Connection connection) =>
        _days.TryGetValue(connection.Id, out var days) ? days : [];

    // Canonical units match the weather station's, deliberately: the two sit on the same
    // page, and "18.5 km/h" beside "11.5 mph" is the same wind twice in two languages.
    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("temp_now_c", "Now", "°C", 1),
        new("temp_high_c", "Today's high", "°C", 1),
        new("temp_low_c", "Today's low", "°C", 1),
        new("rain_chance_percent", "Chance of rain", "%", 0),
        new("rain_today_in", "Rain expected", " in", 2),
        new("wind_mph", "Wind", " mph", 1),
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
            var days = Math.Clamp(connection.Settings.GetInt("days", 7), 1, 16);

            var url = "https://api.open-meteo.com/v1/forecast" +
                      $"?latitude={Uri.EscapeDataString(latitude)}&longitude={Uri.EscapeDataString(longitude)}" +
                      "&current=temperature_2m,wind_speed_10m,precipitation" +
                      "&daily=temperature_2m_max,temperature_2m_min,precipitation_sum," +
                      "precipitation_probability_max,weather_code" +
                      $"&forecast_days={days}&timezone=auto";

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
                Copy(current, "wind_speed_10m", metrics, "wind_mph", KmhToMph);
            }

            if (root.TryGetProperty("daily", out var daily))
            {
                // Today's numbers become metrics, so they chart and can carry an alert
                // rule; the whole week is kept for the card, which is the part a metric
                // cannot express.
                Copy(First(daily, "temperature_2m_max"), metrics, "temp_high_c");
                Copy(First(daily, "temperature_2m_min"), metrics, "temp_low_c");
                Copy(First(daily, "precipitation_sum") is { } mm ? MmToInches(mm) : null, metrics, "rain_today_in");
                Copy(First(daily, "precipitation_probability_max"), metrics, "rain_chance_percent");

                _days[connection.Id] = ReadDays(daily);
            }

            var message = metrics.TryGetValue("temp_high_c", out var high) && metrics.TryGetValue("temp_low_c", out var low)
                ? $"{high:0.#}°C / {low:0.#}°C" +
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

    /// <summary>
    /// Open-Meteo returns each daily field as its own array, all the same length and all in
    /// the same order, so day <c>n</c> is index <c>n</c> of every one of them. A missing or
    /// short series just leaves that day's number at zero rather than losing the whole week.
    /// </summary>
    public static IReadOnlyList<Day> ReadDays(JsonElement daily)
    {
        if (!daily.TryGetProperty("time", out var dates) || dates.ValueKind != JsonValueKind.Array)
            return [];

        var highs = Series(daily, "temperature_2m_max");
        var lows = Series(daily, "temperature_2m_min");
        var rain = Series(daily, "precipitation_sum");
        var chance = Series(daily, "precipitation_probability_max");
        var codes = Series(daily, "weather_code");

        var days = new List<Day>(dates.GetArrayLength());
        for (var i = 0; i < dates.GetArrayLength(); i++)
        {
            if (!DateOnly.TryParse(dates[i].GetString(), CultureInfo.InvariantCulture, out var date))
                continue;

            days.Add(new Day(
                date, At(highs, i), At(lows, i), MmToInches(At(rain, i)), At(chance, i), (int)At(codes, i)));
        }

        return days;
    }

    private static JsonElement? Series(JsonElement daily, string name) =>
        daily.TryGetProperty(name, out var series) && series.ValueKind == JsonValueKind.Array ? series : null;

    private static double At(JsonElement? series, int index) =>
        series is { } array && index < array.GetArrayLength() && array[index].ValueKind == JsonValueKind.Number
            ? array[index].GetDouble()
            : 0;

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

    private static void Copy(
        JsonElement element, string from, Dictionary<string, double> metrics, string to,
        Func<double, double>? convert = null)
    {
        if (element.TryGetProperty(from, out var value) && value.ValueKind == JsonValueKind.Number)
            metrics[to] = convert is null ? value.GetDouble() : convert(value.GetDouble());
    }

    // Open-Meteo answers in km/h and millimetres; both are stored the way the rest of the
    // app stores them and converted back at display time for anyone reading in metric.
    private static double KmhToMph(double kmh) => kmh / 1.60934;
    private static double MmToInches(double mm) => mm / 25.4;
}
