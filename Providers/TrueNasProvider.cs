using System.Diagnostics;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// TrueNAS SCALE or CORE over the v2.0 REST API. Pool capacity is the number people
/// actually want watching, so it is folded into the same probe rather than being a
/// separate thing to configure.
/// </summary>
public sealed class TrueNasProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "truenas";
    public string DisplayName => "TrueNAS";
    public string Icon => "🗄️";
    public string Category => "Storage";
    public string Description => "Version, uptime and how full the fullest pool is.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "https://192.168.1.6", Required: true),
        new("api_key", "API key", FieldKind.Password, Required: true,
            Help: "Settings → API Keys inside TrueNAS."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("disk_percent", "Fullest pool", "%", 1),
        new("pool_count", "Pools"),
        new("pools_degraded", "Pools not healthy"),
        new("uptime_days", "Uptime", " days", 1),
        new("latency_ms", "Response time", " ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Pool nearly full", "disk_percent", Comparison.Above, 80, ClearThreshold: 75, ForMinutes: 10,
            Why: "ZFS slows down badly past about 80%, well before it is actually full."),
        new("Pool not healthy", "pools_degraded", Comparison.Above, 0,
            Why: "A degraded or faulted pool is one more failure from data loss."),
    ];

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        if (connection.Settings.Get("url").Length == 0)
            return ProbeResult.Down(TimeSpan.Zero, "No base URL configured.");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var info = await GetAsync(connection, "system/info", ct);
            stopwatch.Stop();

            var root = info.RootElement;
            var metrics = new Dictionary<string, double> { ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds };
            var details = new Dictionary<string, string>();

            if (root.TryGetProperty("version", out var version) && version.GetString() is { Length: > 0 } text)
                details["Version"] = text;
            if (root.TryGetProperty("hostname", out var host) && host.GetString() is { Length: > 0 } hostname)
                details["Hostname"] = hostname;
            if (root.TryGetProperty("uptime_seconds", out var uptime) && uptime.ValueKind == JsonValueKind.Number)
                metrics["uptime_days"] = uptime.GetDouble() / 86400;

            var message = details.GetValueOrDefault("Version", "Connected");

            try
            {
                var (fullest, count, degraded, worst) = await PoolsAsync(connection, ct);
                if (count > 0)
                {
                    metrics["disk_percent"] = fullest;
                    metrics["pool_count"] = count;
                    metrics["pools_degraded"] = degraded;
                    message = degraded > 0
                        ? $"{worst} is not healthy — fullest pool {fullest:0.#}%"
                        : $"{count} pool(s), fullest {fullest:0.#}%";
                }
            }
            catch
            {
                // An API key without pool read access still gives a usable system check.
            }

            return ProbeResult.Up(stopwatch.Elapsed, message, metrics, details);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("url")));
        }
    }

    private async Task<(double Fullest, int Count, int Degraded, string Worst)> PoolsAsync(
        Connection connection, CancellationToken ct)
    {
        using var document = await GetAsync(connection, "pool", ct);

        double fullest = 0;
        var count = 0;
        var degraded = 0;
        var worst = "";

        foreach (var pool in document.RootElement.EnumerateArray())
        {
            count++;

            if (pool.TryGetProperty("status", out var status) &&
                status.GetString() is { Length: > 0 } state &&
                !string.Equals(state, "ONLINE", StringComparison.OrdinalIgnoreCase))
            {
                degraded++;
                worst = pool.TryGetProperty("name", out var name) ? name.GetString() ?? "A pool" : "A pool";
            }

            // topology totals are not in this payload; allocated/free on the root dataset are.
            if (pool.TryGetProperty("allocated", out var allocated) && allocated.ValueKind == JsonValueKind.Number &&
                pool.TryGetProperty("free", out var free) && free.ValueKind == JsonValueKind.Number)
            {
                var used = allocated.GetDouble();
                var total = used + free.GetDouble();
                if (total > 0)
                    fullest = Math.Max(fullest, used / total * 100);
            }
        }

        return (fullest, count, degraded, worst);
    }

    private async Task<JsonDocument> GetAsync(Connection connection, string path, CancellationToken ct)
    {
        var baseUrl = connection.Settings.Get("url").TrimEnd('/');
        var http = httpFactory.CreateClient(ProviderHttp.ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v2.0/{path}");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {connection.Settings.Get("api_key")}");

        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("TrueNAS rejected the API key.");
        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
}
