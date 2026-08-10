using System.Diagnostics;
using System.Net;
using System.Text.Json;
using LabbyTwo.Core;
using LabbyTwo.Providers;

namespace LabbyTwo.SyncthingPlugin;

/// <summary>
/// Syncthing over its REST API — how many of your devices are actually connected, how
/// much has moved, and how long the daemon has been up.
///
/// This is the shape almost every HTTP provider takes, so it is worth reading as a
/// template: declare the fields, declare the metrics, make the round trips, and turn
/// whatever went wrong into a sentence someone can act on. There is no registration step;
/// the host finds this class by scanning the DLL.
/// </summary>
public sealed class SyncthingProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    // Stored on every connection row using this provider. Changing it after release
    // orphans existing connections, so pick it once. A prefix keeps it clear of anything
    // LabbyTwo might ship later under the obvious name.
    public string Type => "syncthing";

    public string DisplayName => "Syncthing";
    public string Icon => "🔄";
    public string Category => "Storage";
    public string Description =>
        "A Syncthing daemon — devices connected, data transferred, and how long it has been running.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "http://192.168.1.20:8384", Required: true,
            Help: "Reached by the LabbyTwo server, so it must resolve from wherever LabbyTwo runs."),

        // FieldKind.Password is encrypted at rest with the app's keyring and is never
        // rendered back to the browser, even to the person who typed it.
        new("api_key", "API key", FieldKind.Password, Required: true,
            Help: "Actions → Settings → General → API Key, inside Syncthing's own web UI."),
    ];

    // Only what is specific to Syncthing. latency_ms and uptime_days are already known to
    // the host, and anything undeclared still gets a readable label from its name.
    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("devices_connected", "Devices connected"),
        new("devices_total", "Devices configured"),
        new("received_gb", "Received", " GB", 1),
        new("sent_gb", "Sent", " GB", 1),
    ];

    /// <summary>
    /// Offered on the Alerts page, never created behind anyone's back. The person who
    /// wrote the integration knows which of its numbers actually matter; the user should
    /// not have to work that out from a list of metric names.
    /// </summary>
    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Nothing is syncing", "devices_connected", Comparison.Below, 1, ForMinutes: 30,
            Why: "No remote device has been connected for half an hour, so nothing is being backed up."),
    ];

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var status = await GetAsync(connection, "/rest/system/status", ct);
            using var connections = await GetAsync(connection, "/rest/system/connections", ct);

            var metrics = new Dictionary<string, double>();

            if (status.RootElement.TryGetProperty("uptime", out var uptime) &&
                uptime.ValueKind == JsonValueKind.Number)
                metrics["uptime_days"] = uptime.GetDouble() / 86400d;

            // "connections" maps device id -> state. A configured-but-offline device is
            // present with connected:false, which is exactly the distinction worth
            // charting: total tells you what you expect, connected tells you what you have.
            var total = 0;
            var online = 0;
            if (connections.RootElement.TryGetProperty("connections", out var devices) &&
                devices.ValueKind == JsonValueKind.Object)
            {
                foreach (var device in devices.EnumerateObject())
                {
                    total++;
                    if (device.Value.TryGetProperty("connected", out var flag) &&
                        flag.ValueKind == JsonValueKind.True)
                        online++;
                }
            }

            metrics["devices_total"] = total;
            metrics["devices_connected"] = online;

            const double gb = 1024d * 1024 * 1024;
            if (connections.RootElement.TryGetProperty("total", out var totals))
            {
                if (totals.TryGetProperty("inBytesTotal", out var inBytes) && inBytes.ValueKind == JsonValueKind.Number)
                    metrics["received_gb"] = inBytes.GetDouble() / gb;
                if (totals.TryGetProperty("outBytesTotal", out var outBytes) && outBytes.ValueKind == JsonValueKind.Number)
                    metrics["sent_gb"] = outBytes.GetDouble() / gb;
            }

            stopwatch.Stop();
            metrics["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds;

            // Details show on the connection's own page. Version belongs here rather than
            // in a metric: it is a fact, not a number to chart.
            var details = new Dictionary<string, string>();
            if (status.RootElement.TryGetProperty("myID", out var id) && id.GetString() is { Length: > 7 } deviceId)
                details["Device ID"] = deviceId[..7];

            return ProbeResult.Up(
                stopwatch.Elapsed,
                total == 0
                    ? "Running, with no remote devices configured"
                    : $"{online} of {total} device{(total == 1 ? "" : "s")} connected",
                metrics,
                details.Count > 0 ? details : null);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            // A probe must never throw. This message is what shows on the tile and in the
            // alert, so it should be the thing the reader needs to do something about —
            // ProbeError turns the common network failures into exactly that.
            return ProbeResult.Down(stopwatch.Elapsed, Explain(ex, connection));
        }
    }

    private static string Explain(Exception ex, Connection connection) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized } =>
            "Syncthing rejected the API key. Copy it again from Actions → Settings → General.",
        _ => ProbeError.Describe(ex, connection.Settings.Get("url")),
    };

    private async Task<JsonDocument> GetAsync(Connection connection, string path, CancellationToken ct)
    {
        var baseUrl = connection.Settings.Get("url").TrimEnd('/');
        if (baseUrl.Length == 0)
            throw new InvalidOperationException("No base URL configured.");

        // Use the host's factory rather than "new HttpClient()": it carries the shared
        // timeout and connection pooling, and a client per probe exhausts sockets.
        var http = httpFactory.CreateClient(ProviderHttp.ClientName);

        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
        request.Headers.TryAddWithoutValidation("X-API-Key", connection.Settings.Get("api_key"));

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
}
