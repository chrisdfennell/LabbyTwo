using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Providers;

/// <summary>
/// Open-Meteo. A weather station tells you what is happening on your roof; this tells you
/// what is about to happen, which is the half a dashboard was missing — and it needs no
/// account, no key and no terms of service, which is why it is this one rather than any of
/// the alternatives.
/// </summary>
public sealed class ForecastProvider(IHttpClientFactory httpFactory, AppSettingsStore appSettings) : IConnectionProvider
{
    public string Type => "forecast";
    public string DisplayName => "Weather forecast";
    public string Icon => "🌦️";
    public string Category => "Sensors";
    public string Description => "The next few hours and the next couple of weeks — rain, wind, UV and snow. No API key needed.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("latitude", "Latitude", FieldKind.Text,
            Help: "Leave both blank to use the location set in Settings, which is usually what you want."),

        new("longitude", "Longitude", FieldKind.Text),

        new("days", "Days to forecast", FieldKind.Number, Default: "7",
            Help: "Up to 16. Open-Meteo's accuracy falls off a cliff after about a week, which is why " +
                  "this defaults to seven rather than the maximum."),
    ];

    /// <summary>One day of the forecast, in the app's canonical units — Celsius and inches.</summary>
    /// <param name="Code">A WMO weather code — 0 is clear, 95 upwards is thunder.</param>
    public sealed record Day(DateOnly Date, int Code)
    {
        public double High { get; init; }
        public double Low { get; init; }
        public double FeelsHigh { get; init; }
        public double FeelsLow { get; init; }
        public double RainInches { get; init; }
        public double SnowInches { get; init; }
        public double RainChance { get; init; }
        public double GustMph { get; init; }
        public double WindDirection { get; init; }
        public double UvIndex { get; init; }

        public string Icon => WeatherCode.Icon(Code);

        /// <summary>
        /// Today is worth naming; tomorrow is not — abbreviating it fits the column but
        /// reads as a man's name, and "Wed" beside "Today" is unambiguous anyway.
        /// </summary>
        public string Label(DateOnly today) =>
            Date == today ? "Today" : Date.ToString("ddd", CultureInfo.CurrentCulture);
    }

    /// <param name="At">Local to the forecast location, which is not necessarily local to the server.</param>
    public sealed record Hour(DateTime At, double TempC, double RainChance, double RainInches, int Code)
    {
        public string Icon => WeatherCode.Icon(Code);

        /// <summary>
        /// "3pm" rather than "15:00": narrow, and the way people say it. The format is
        /// "%h" and not "h" — a lone "h" is read as a standard format specifier, which
        /// there is no such thing as, and throws.
        /// </summary>
        public string Label => At.ToString("%h", CultureInfo.InvariantCulture)
                               + (At.Hour < 12 ? "am" : "pm");
    }

    // What a widget wants from a forecast is a week of days and a day of hours, and a
    // metric is one number — so they are kept here, on the singleton, and the cards read
    // them. Same arrangement the weather station and Plex providers use for data richer
    // than metrics can carry.
    private readonly ConcurrentDictionary<string, IReadOnlyList<Day>> _days = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<Hour>> _hours = new();

    /// <summary>The days from the last probe, or empty if this connection has not run yet.</summary>
    public IReadOnlyList<Day> Days(Connection connection) =>
        _days.TryGetValue(connection.Id, out var days) ? days : [];

    /// <summary>The hours from the last probe, starting at the hour it is there now.</summary>
    public IReadOnlyList<Hour> Hours(Connection connection) =>
        _hours.TryGetValue(connection.Id, out var hours) ? hours : [];

    // Canonical units match the weather station's, deliberately: the two sit on the same
    // page, and "18.5 km/h" beside "11.5 mph" is the same wind twice in two languages.
    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("temp_now_c", "Now", "°C", 1),
        new("temp_high_c", "Today's high", "°C", 1),
        new("temp_low_c", "Today's low", "°C", 1),
        new("temp_feels_high_c", "Feels like, high", "°C", 1),
        new("temp_feels_low_c", "Feels like, low", "°C", 1),
        new("rain_chance_percent", "Chance of rain", "%", 0),
        new("rain_today_in", "Rain expected", " in", 2),
        new("snow_today_in", "Snow expected", " in", 1),
        new("wind_mph", "Wind", " mph", 1),
        new("gust_max_mph", "Peak gust forecast", " mph", 1),
        new("wind_dir", "Wind direction", "°"),
        new("uv_index_max", "Peak UV index"),

        // Tomorrow gets its own three because that is the horizon you act on — putting the
        // washing out, covering the plants — and a rule can only watch a number.
        new("temp_high_tomorrow_c", "Tomorrow's high", "°C", 1),
        new("temp_low_tomorrow_c", "Tomorrow's low", "°C", 1),
        new("rain_chance_tomorrow_percent", "Tomorrow's chance of rain", "%", 0),

        new("latency_ms", "Response time", " ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Freezing tonight", "temp_low_c", Comparison.Below, 0, ForMinutes: 60,
            Why: "Worth knowing the evening before rather than the morning after — pipes, plants, the car."),

        new("Freezing tomorrow night", "temp_low_tomorrow_c", Comparison.Below, 0, ForMinutes: 60,
            Why: "A day's notice instead of an evening's, for anything that needs covering or draining."),

        new("Rain on the way", "rain_chance_percent", Comparison.Above, 70, ForMinutes: 60,
            Why: "Pair it with a notification if you are the one who leaves washing out."),

        new("Damaging gusts forecast", "gust_max_mph", Comparison.Above, 45, ForMinutes: 60,
            Why: "Bins, trampolines and garden furniture, before rather than after."),

        new("Snow forecast", "snow_today_in", Comparison.Above, 1, ForMinutes: 60,
            Why: "An inch is the point at which somebody has to move a car or find a shovel."),

        new("High UV", "uv_index_max", Comparison.Above, 8, ForMinutes: 60,
            Why: "Above 8 is a burn in under fifteen minutes for most people."),
    ];

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var home = HomeLocation.From(await appSettings.AllAsync(ct));
        if (home.Resolve(connection.Settings.Get("latitude"), connection.Settings.Get("longitude"))
            is not var (latitude, longitude))
        {
            return ProbeResult.Down(TimeSpan.Zero,
                "No coordinates: set a location in Settings, or give this connection its own.");
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var days = Math.Clamp(connection.Settings.GetInt("days", 7), 1, 16);

            // Always fetched in metric and converted for display by the usual machinery, so
            // one recorded history reads correctly whichever units the app is set to.
            var url = "https://api.open-meteo.com/v1/forecast" +
                      $"?latitude={HomeLocation.Format(latitude)}&longitude={HomeLocation.Format(longitude)}" +
                      "&current=temperature_2m,wind_speed_10m,precipitation" +
                      "&hourly=temperature_2m,precipitation_probability,precipitation,weather_code" +
                      "&daily=temperature_2m_max,temperature_2m_min,apparent_temperature_max," +
                      "apparent_temperature_min,precipitation_sum,precipitation_probability_max," +
                      "snowfall_sum,wind_gusts_10m_max,wind_direction_10m_dominant,uv_index_max,weather_code" +
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

            DateTime? now = null;
            if (root.TryGetProperty("current", out var current))
            {
                Copy(current, "temperature_2m", metrics, "temp_now_c");
                Copy(current, "wind_speed_10m", metrics, "wind_mph", KmhToMph);

                // The location's own clock. The server's may be in another timezone
                // entirely, and "the next twelve hours" has to mean the next twelve there.
                now = LocalTime(current, "time");
            }

            if (root.TryGetProperty("daily", out var daily))
            {
                var parsed = ReadDays(daily);
                _days[connection.Id] = parsed;

                // Today's numbers become metrics, so they chart and can carry an alert
                // rule; the whole week is kept for the card, which is the part a metric
                // cannot express.
                if (parsed.Count > 0)
                {
                    var today = parsed[0];
                    metrics["temp_high_c"] = today.High;
                    metrics["temp_low_c"] = today.Low;
                    metrics["temp_feels_high_c"] = today.FeelsHigh;
                    metrics["temp_feels_low_c"] = today.FeelsLow;
                    metrics["rain_chance_percent"] = today.RainChance;
                    metrics["rain_today_in"] = today.RainInches;
                    metrics["snow_today_in"] = today.SnowInches;
                    metrics["gust_max_mph"] = today.GustMph;
                    metrics["wind_dir"] = today.WindDirection;
                    metrics["uv_index_max"] = today.UvIndex;
                }

                if (parsed.Count > 1)
                {
                    metrics["temp_high_tomorrow_c"] = parsed[1].High;
                    metrics["temp_low_tomorrow_c"] = parsed[1].Low;
                    metrics["rain_chance_tomorrow_percent"] = parsed[1].RainChance;
                }
            }

            if (root.TryGetProperty("hourly", out var hourly))
                _hours[connection.Id] = ReadHours(hourly, now);

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
        var feelsHigh = Series(daily, "apparent_temperature_max");
        var feelsLow = Series(daily, "apparent_temperature_min");
        var rain = Series(daily, "precipitation_sum");
        var snow = Series(daily, "snowfall_sum");
        var chance = Series(daily, "precipitation_probability_max");
        var gusts = Series(daily, "wind_gusts_10m_max");
        var direction = Series(daily, "wind_direction_10m_dominant");
        var uv = Series(daily, "uv_index_max");
        var codes = Series(daily, "weather_code");

        var days = new List<Day>(dates.GetArrayLength());
        for (var i = 0; i < dates.GetArrayLength(); i++)
        {
            if (!DateOnly.TryParse(dates[i].GetString(), CultureInfo.InvariantCulture, out var date))
                continue;

            days.Add(new Day(date, (int)At(codes, i))
            {
                High = At(highs, i),
                Low = At(lows, i),
                FeelsHigh = At(feelsHigh, i),
                FeelsLow = At(feelsLow, i),
                RainInches = MmToInches(At(rain, i)),
                // Snowfall is the one field Open-Meteo answers in centimetres.
                SnowInches = CmToInches(At(snow, i)),
                RainChance = At(chance, i),
                GustMph = KmhToMph(At(gusts, i)),
                WindDirection = At(direction, i),
                UvIndex = At(uv, i),
            });
        }

        return days;
    }

    /// <summary>
    /// The hourly arrays cover whole days from midnight, so most of the first day is
    /// already in the past. <paramref name="now"/> is the location's own clock, from the
    /// response — using the server's would skip or repeat hours for anybody whose
    /// dashboard is not in the same timezone as their house.
    /// </summary>
    public static IReadOnlyList<Hour> ReadHours(JsonElement hourly, DateTime? now)
    {
        if (!hourly.TryGetProperty("time", out var times) || times.ValueKind != JsonValueKind.Array)
            return [];

        var temperatures = Series(hourly, "temperature_2m");
        var chance = Series(hourly, "precipitation_probability");
        var precipitation = Series(hourly, "precipitation");
        var codes = Series(hourly, "weather_code");

        var hours = new List<Hour>();
        for (var i = 0; i < times.GetArrayLength(); i++)
        {
            if (!DateTime.TryParse(times[i].GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces, out var at))
                continue;

            // The hour containing "now" still counts: at ten past three, three o'clock is
            // the hour you are in, not one you have missed.
            if (now is { } from && at < from.AddHours(-1))
                continue;

            hours.Add(new Hour(
                at,
                At(temperatures, i),
                At(chance, i),
                MmToInches(At(precipitation, i)),
                (int)At(codes, i)));
        }

        return hours;
    }

    private static DateTime? LocalTime(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)
            ? parsed
            : null;

    private static JsonElement? Series(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var series) && series.ValueKind == JsonValueKind.Array ? series : null;

    private static double At(JsonElement? series, int index) =>
        series is { } array && index < array.GetArrayLength() && array[index].ValueKind == JsonValueKind.Number
            ? array[index].GetDouble()
            : 0;

    private static void Copy(
        JsonElement element, string from, Dictionary<string, double> metrics, string to,
        Func<double, double>? convert = null)
    {
        if (element.TryGetProperty(from, out var value) && value.ValueKind == JsonValueKind.Number)
            metrics[to] = convert is null ? value.GetDouble() : convert(value.GetDouble());
    }

    // Open-Meteo answers in km/h, millimetres and centimetres; all three are stored the way
    // the rest of the app stores them and converted back at display time for anyone reading
    // in metric.
    private static double KmhToMph(double kmh) => kmh / 1.60934;
    private static double MmToInches(double mm) => mm / 25.4;
    private static double CmToInches(double cm) => cm / 2.54;
}

/// <summary>
/// WMO weather codes, grouped the way a person reads a forecast rather than the fifty-odd
/// distinctions the standard makes between kinds of drizzle. Shared, because the hourly
/// strip and the daily strip must not disagree about what code 71 looks like.
/// </summary>
public static class WeatherCode
{
    public static string Icon(int code) => code switch
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

    /// <summary>The words, for a tooltip. Grouped the same way as the icons.</summary>
    public static string Describe(int code) => code switch
    {
        0 => "Clear",
        1 or 2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        >= 51 and <= 57 => "Drizzle",
        >= 61 and <= 67 => "Rain",
        >= 71 and <= 77 => "Snow",
        >= 80 and <= 82 => "Showers",
        85 or 86 => "Snow showers",
        >= 95 and <= 99 => "Thunderstorms",
        _ => "Cloud",
    };
}
