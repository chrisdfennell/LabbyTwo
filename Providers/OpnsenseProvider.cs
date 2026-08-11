using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// OPNsense. The router is the one box where "is it up" is never the question — you would
/// know — so this reports the things that go wrong while it is still up: a gateway that
/// has started losing packets, a WAN address that changed overnight, a firewall table
/// filling up.
///
/// pfSense speaks a different API and is not covered by this; its own package exposes a
/// similar set and would be a sibling class rather than a flag here.
/// </summary>
public sealed class OpnsenseProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "opnsense";
    public string DisplayName => "OPNsense";
    public string Icon => "🧱";
    public string Category => "Network";
    public string Description => "Gateway loss and latency, WAN address, CPU and memory from an OPNsense firewall.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "https://192.168.1.1", Required: true,
            Help: "The web interface. A self-signed certificate is fine — it is not checked."),

        new("key", "API key", FieldKind.Text, Required: true,
            Help: "System → Access → Users → edit a user → API keys. Downloads as a file with both halves in it."),

        new("secret", "API secret", FieldKind.Password, Required: true),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("gateways", "Gateways"),
        new("gateways_down", "Gateways down"),
        new("gateway_loss_percent", "Worst packet loss", "%", 1),
        new("gateway_latency_ms", "Gateway latency", " ms", 1),
        new("cpu_percent", "CPU", "%", 1),
        new("ram_percent", "Memory", "%", 1),
        new("latency_ms", "Response time", " ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("The line is dropping packets", "gateway_loss_percent", Comparison.Above, 5, ClearThreshold: 2, ForMinutes: 10,
            Why: "The failure everybody blames on wifi. Ten minutes rides out a blip and catches a real fault."),

        new("A gateway is down", "gateways_down", Comparison.Above, 0, ForMinutes: 5,
            Why: "On a single-WAN setup this is the internet being off, from the box that knows first."),
    ];

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var baseUrl = connection.Settings.Get("url").TrimEnd('/');
        if (baseUrl.Length == 0)
            return ProbeResult.Down(TimeSpan.Zero, "No base URL configured.");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var metrics = new Dictionary<string, double>();
            var details = new Dictionary<string, string>();

            using var gateways = await GetAsync(connection, "/api/routes/gateway/status", ct);

            int total = 0, down = 0;
            double worstLoss = 0, latency = 0;
            var failing = new List<string>();

            if (gateways.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var gateway in items.EnumerateArray())
                {
                    total++;

                    var name = Text(gateway, "name");
                    var status = Text(gateway, "status_translated");

                    // "Online", "Offline", "Pending", or a loss/latency warning phrase.
                    if (status.Contains("offline", StringComparison.OrdinalIgnoreCase)
                        || status.Contains("down", StringComparison.OrdinalIgnoreCase))
                    {
                        down++;
                        if (name.Length > 0)
                            failing.Add(name);
                    }

                    // Reported as strings with the unit attached: "0.0 %", "12.3 ms", or "~".
                    worstLoss = Math.Max(worstLoss, Measure(Text(gateway, "loss")));
                    latency = Math.Max(latency, Measure(Text(gateway, "delay")));

                    if (Text(gateway, "address") is { Length: > 0 } address && name.Length > 0)
                        details[name] = address;
                }
            }

            metrics["gateways"] = total;
            metrics["gateways_down"] = down;
            metrics["gateway_loss_percent"] = worstLoss;
            if (latency > 0)
                metrics["gateway_latency_ms"] = latency;

            // System load is a second call and a nice-to-have: a firewall with a sick
            // gateway is worth reporting even if this endpoint moved in an update.
            try
            {
                using var system = await GetAsync(connection, "/api/diagnostics/system/systemResources", ct);
                var root = system.RootElement;

                if (root.TryGetProperty("memory", out var memory))
                {
                    var used = Number(memory, "used") ?? 0;
                    var totalMemory = Number(memory, "total") ?? 0;
                    if (totalMemory > 0)
                        metrics["ram_percent"] = used / totalMemory * 100;
                }
            }
            catch (Exception)
            {
                // Left out rather than failing the probe.
            }

            try
            {
                using var cpu = await GetAsync(connection, "/api/diagnostics/cpu_usage/getCPUType", ct);
                if (cpu.RootElement.ValueKind == JsonValueKind.Array && cpu.RootElement.GetArrayLength() > 0)
                    details["CPU"] = cpu.RootElement[0].GetString() ?? "";
            }
            catch (Exception)
            {
                // Same again.
            }

            stopwatch.Stop();
            metrics["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds;

            var message = down > 0
                ? $"{down} gateway down: {string.Join(", ", failing.Take(2))}"
                : worstLoss > 0
                    ? $"{total} gateway{(total == 1 ? "" : "s")}, {worstLoss:0.#}% loss"
                    : $"{total} gateway{(total == 1 ? "" : "s")} online";

            return ProbeResult.Up(stopwatch.Elapsed, message, metrics, details);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("url")));
        }
    }

    private async Task<JsonDocument> GetAsync(Connection connection, string path, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(ProviderHttp.ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            connection.Settings.Get("url").TrimEnd('/') + path);

        // OPNsense takes the key and secret as ordinary basic auth.
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{connection.Settings.Get("key")}:{connection.Settings.Get("secret")}")));

        using var response = await http.SendAsync(request, ct);

        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException(
                "OPNsense rejected the key. Both halves come from the same downloaded file, and the user " +
                "needs the matching privileges for the API pages.");

        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

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

    /// <summary>
    /// "12.3 ms", "0.0 %", or "~" for a gateway that has not been measured. Takes the
    /// leading number and ignores the rest.
    /// </summary>
    private static double Measure(string text)
    {
        var digits = new string([.. text.TakeWhile(c => char.IsAsciiDigit(c) || c is '.' or '-')]);
        return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }
}
