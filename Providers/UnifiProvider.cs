using System.Diagnostics;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// A UniFi controller. Two login shapes exist in the wild — the older self-hosted
/// controller and UniFi OS on a Dream Machine or Cloud Key — and the paths differ, so
/// this tries the UniFi OS one first and falls back rather than asking the user which
/// hardware they own.
/// </summary>
public sealed class UnifiProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "unifi";
    public string DisplayName => "UniFi controller";
    public string Icon => "📶";
    public string Category => "Network";
    public string Description => "Clients connected, access points up, and WAN throughput.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "https://192.168.1.1", Required: true,
            Help: "The controller, not an access point. Port 8443 for an older self-hosted controller."),
        new("username", "Username", FieldKind.Text, Required: true,
            Help: "A local account with read-only rights. A UniFi cloud account with MFA will not work."),
        new("password", "Password", FieldKind.Password, Required: true),
        new("site", "Site", FieldKind.Text, Default: "default",
            Help: "The site id in the controller URL, usually \"default\"."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("clients", "Clients connected"),
        new("clients_wired", "Wired clients"),
        new("clients_wireless", "Wireless clients"),
        new("devices_adopted", "Devices adopted"),
        new("devices_offline", "Devices offline"),

        new("download_mbps", "WAN download", " Mbps", 1),
        new("upload_mbps", "WAN upload", " Mbps", 1),
        new("latency_ms", "Response time", " ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("An access point has dropped off", "devices_offline", Comparison.Above, 0, ForMinutes: 10,
            Why: "The controller stays up while a switch or AP goes offline, so nothing else notices. " +
                 "Ten minutes' grace covers a firmware update rebooting one."),
    ];

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var baseUrl = connection.Settings.Get("url").TrimEnd('/');
        if (baseUrl.Length == 0)
            return ProbeResult.Down(TimeSpan.Zero, "No base URL configured.");

        var site = connection.Settings.Get("site", "default");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var http = httpFactory.CreateClient(ProviderHttp.ClientName);
            var (cookie, isUnifiOs) = await SignInAsync(http, connection, baseUrl, ct);
            var prefix = isUnifiOs ? "/proxy/network" : "";

            using var document = await GetAsync(http, $"{baseUrl}{prefix}/api/s/{site}/stat/health", cookie, ct);
            stopwatch.Stop();

            var metrics = new Dictionary<string, double> { ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds };
            var subsystems = document.RootElement.TryGetProperty("data", out var data) ? data : default;

            foreach (var subsystem in subsystems.ValueKind == JsonValueKind.Array ? subsystems.EnumerateArray() : [])
            {
                var name = subsystem.TryGetProperty("subsystem", out var s) ? s.GetString() : null;

                if (name is "wlan" or "lan")
                {
                    Add(metrics, "clients", Number(subsystem, "num_user") ?? 0);
                    Add(metrics, name == "wlan" ? "clients_wireless" : "clients_wired", Number(subsystem, "num_user") ?? 0);
                    Add(metrics, "devices_adopted", Number(subsystem, "num_adopted") ?? 0);
                    Add(metrics, "devices_offline", Number(subsystem, "num_disconnected") ?? 0);
                }
                else if (name == "www")
                {
                    // Reported in bytes per second.
                    const double toMbps = 8d / 1_000_000;
                    if (Number(subsystem, "rx_bytes-r") is { } rx)
                        metrics["download_mbps"] = rx * toMbps;
                    if (Number(subsystem, "tx_bytes-r") is { } tx)
                        metrics["upload_mbps"] = tx * toMbps;
                }
            }

            var offline = metrics.GetValueOrDefault("devices_offline");
            var message = $"{metrics.GetValueOrDefault("clients"):0} clients"
                          + (offline > 0 ? $", {offline:0} device(s) offline" : "");

            return ProbeResult.Up(stopwatch.Elapsed, message, metrics);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("url")));
        }
    }

    /// <summary>
    /// Logs in, returning the session cookie and which controller generation answered.
    /// UniFi OS lives at /api/auth/login; the classic controller at /api/login.
    /// </summary>
    private static async Task<(string Cookie, bool IsUnifiOs)> SignInAsync(
        HttpClient http, Connection connection, string baseUrl, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            username = connection.Settings.Get("username"),
            password = connection.Settings.Get("password"),
        });

        foreach (var (path, isUnifiOs) in new[] { ("/api/auth/login", true), ("/api/login", false) })
        {
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            using var response = await http.PostAsync($"{baseUrl}{path}", content, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                continue;   // wrong generation, try the other path

            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.BadRequest)
                throw new InvalidOperationException(
                    "UniFi rejected the username or password. A cloud account with MFA cannot be used — create a local read-only user.");

            response.EnsureSuccessStatusCode();

            if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
            {
                var jar = string.Join("; ", cookies.Select(c => c.Split(';')[0]));
                if (jar.Length > 0)
                    return (jar, isUnifiOs);
            }

            throw new InvalidOperationException("UniFi accepted the login but returned no session cookie.");
        }

        throw new InvalidOperationException("No UniFi login endpoint answered — check the URL and port.");
    }

    private static async Task<JsonDocument> GetAsync(HttpClient http, string url, string cookie, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Cookie", cookie);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    // wlan and lan each report their own client count, and the total is the sum.
    private static void Add(Dictionary<string, double> metrics, string key, double value) =>
        metrics[key] = metrics.GetValueOrDefault(key) + value;

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
}
