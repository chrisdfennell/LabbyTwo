using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// A Shelly plug or relay, read straight off the device. No cloud, no account, no MQTT
/// broker in the middle — Shellies answer a plain HTTP GET on the LAN, which makes power
/// the one homelab number that is genuinely easy to get and that nothing here reported.
///
/// Handles both generations: Gen1 answers <c>/status</c>, Gen2 and later speak RPC at
/// <c>/rpc/Shelly.GetStatus</c>. Tried in that order, so one connection type covers a
/// drawer of devices bought years apart.
/// </summary>
public sealed class ShellyProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "shelly";
    public string DisplayName => "Shelly plug";
    public string Icon => "🔌";
    public string Category => "Power";
    public string Description => "Power draw, energy used and temperature from a Shelly plug or relay on your LAN.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Address", FieldKind.Url, "http://192.168.1.70", Required: true,
            Help: "Just the device's address. Both Shelly generations are tried, so it does not matter which you have."),

        new("username", "Username", FieldKind.Text, Help: "Only if you set a password on the device."),
        new("password", "Password", FieldKind.Password),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("watts", "Power", " W", 1),
        new("energy_kwh", "Energy used", " kWh", 2),
        new("voltage", "Voltage", " V", 1),
        new("temp_c", "Temperature", "°C", 1),
        new("relay_on", "Switched on"),
        new("latency_ms", "Response time", " ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Drawing nothing", "watts", Comparison.Below, 1, ForMinutes: 15,
            Why: "For a plug that should always be pulling something — a freezer, a server. " +
                 "Zero watts means whatever is plugged in has stopped, which nothing else would tell you."),

        new("Running hot", "temp_c", Comparison.Above, 70, ClearThreshold: 60, ForMinutes: 10,
            Why: "A plug that is warm is a plug near its limit. This is the one that starts fires."),
    ];

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var baseUrl = connection.Settings.Get("url").TrimEnd('/');
        if (baseUrl.Length == 0)
            return ProbeResult.Down(TimeSpan.Zero, "No address configured.");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var http = httpFactory.CreateClient(ProviderHttp.ClientName);
            var metrics = new Dictionary<string, double>();

            // Gen2 first: it is what anything bought recently is, and its 404 on Gen1 is
            // cheap. Gen1's /status exists on some Gen2 firmware too but reports less.
            using var document = await TryAsync(http, connection, $"{baseUrl}/rpc/Shelly.GetStatus", ct)
                ?? await TryAsync(http, connection, $"{baseUrl}/status", ct)
                ?? throw new InvalidOperationException(
                    "Neither /rpc/Shelly.GetStatus nor /status answered. Is this a Shelly?");

            var root = document.RootElement;
            stopwatch.Stop();

            // Gen2 puts a switch under "switch:0"; Gen1 uses a "meters" array beside "relays".
            if (root.TryGetProperty("switch:0", out var gen2))
            {
                Copy(gen2, "apower", metrics, "watts");
                Copy(gen2, "voltage", metrics, "voltage");

                if (gen2.TryGetProperty("aenergy", out var energy) && Number(energy, "total") is { } total)
                    metrics["energy_kwh"] = total / 1000;

                if (gen2.TryGetProperty("temperature", out var temperature))
                    Copy(temperature, "tC", metrics, "temp_c");

                if (gen2.TryGetProperty("output", out var output))
                    metrics["relay_on"] = output.ValueKind == JsonValueKind.True ? 1 : 0;
            }
            else
            {
                if (root.TryGetProperty("meters", out var meters)
                    && meters.ValueKind == JsonValueKind.Array && meters.GetArrayLength() > 0)
                {
                    Copy(meters[0], "power", metrics, "watts");

                    // Gen1 counts in watt-minutes, which is a unit nobody wants to read.
                    if (Number(meters[0], "total") is { } minutes)
                        metrics["energy_kwh"] = minutes / 60 / 1000;
                }

                if (root.TryGetProperty("relays", out var relays)
                    && relays.ValueKind == JsonValueKind.Array && relays.GetArrayLength() > 0
                    && relays[0].TryGetProperty("ison", out var ison))
                    metrics["relay_on"] = ison.ValueKind == JsonValueKind.True ? 1 : 0;

                Copy(root, "temperature", metrics, "temp_c");
                if (root.TryGetProperty("tmp", out var tmp))
                    Copy(tmp, "tC", metrics, "temp_c");
            }

            metrics["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds;

            var message = metrics.TryGetValue("watts", out var watts)
                ? $"{watts:0.#} W{(metrics.TryGetValue("relay_on", out var on) && on == 0 ? " · switched off" : "")}"
                : "Connected";

            return ProbeResult.Up(stopwatch.Elapsed, message, metrics);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("url")));
        }
    }

    /// <summary>Returns null for a 404 so the caller can try the other generation's path.</summary>
    private static async Task<JsonDocument?> TryAsync(
        HttpClient http, Connection connection, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (connection.Settings.Get("username") is { Length: > 0 } user)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                    $"{user}:{connection.Settings.Get("password")}")));
        }

        using var response = await http.SendAsync(request, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("The Shelly wants a username and password.");

        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static void Copy(JsonElement element, string from, Dictionary<string, double> metrics, string to)
    {
        if (Number(element, from) is { } value)
            metrics[to] = value;
    }

    private static double? Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }
}
