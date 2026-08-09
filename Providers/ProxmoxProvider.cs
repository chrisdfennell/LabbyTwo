using System.Diagnostics;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// Proxmox VE. Uses an API token rather than a login ticket: tokens do not expire, can be
/// scoped to read-only, and mean no password is stored to be replayed.
/// </summary>
public sealed class ProxmoxProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "proxmox";
    public string DisplayName => "Proxmox VE";
    public string Icon => "🖧";
    public string Category => "Virtualisation";
    public string Description => "Node CPU, memory and storage, plus how many guests are running.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "https://192.168.1.5:8006", Required: true),
        new("node", "Node name", FieldKind.Text, "pve", Required: true,
            Help: "The name in the left-hand tree, not the hostname."),
        new("token_id", "API token ID", FieldKind.Text, "monitor@pve!labby", Required: true,
            Help: "Datacenter → Permissions → API Tokens. Give it PVEAuditor and nothing more."),
        new("token_secret", "API token secret", FieldKind.Password, Required: true,
            Help: "Shown once when the token is created."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("cpu_percent", "CPU", "%", 1),
        new("ram_percent", "Memory", "%", 1),
        new("disk_percent", "Root filesystem", "%", 1),
        new("uptime_days", "Uptime", " days", 1),
        new("vms_running", "VMs running"),
        new("containers_running", "Containers running"),
        new("latency_ms", "Response time", "ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Root filesystem filling", "disk_percent", Comparison.Above, 85, ClearThreshold: 80, ForMinutes: 10,
            Why: "A full root on a Proxmox node stops guests starting."),
        new("Memory pressure", "ram_percent", Comparison.Above, 92, ClearThreshold: 85, ForMinutes: 15,
            Why: "Sustained, because a backup run briefly pins memory."),
    ];

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var baseUrl = connection.Settings.Get("url").TrimEnd('/');
        var node = connection.Settings.Get("node");
        if (baseUrl.Length == 0 || node.Length == 0)
            return ProbeResult.Down(TimeSpan.Zero, "Base URL and node name are both required.");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var status = await GetAsync(connection, $"nodes/{node}/status", ct);
            stopwatch.Stop();

            var data = status.RootElement.GetProperty("data");
            var metrics = new Dictionary<string, double> { ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds };

            if (data.TryGetProperty("cpu", out var cpu) && cpu.ValueKind == JsonValueKind.Number)
                metrics["cpu_percent"] = cpu.GetDouble() * 100;

            if (data.TryGetProperty("memory", out var memory) &&
                Number(memory, "total") is { } totalRam and > 0 && Number(memory, "used") is { } usedRam)
                metrics["ram_percent"] = usedRam / totalRam * 100;

            if (data.TryGetProperty("rootfs", out var rootfs) &&
                Number(rootfs, "total") is { } totalDisk and > 0 && Number(rootfs, "used") is { } usedDisk)
                metrics["disk_percent"] = usedDisk / totalDisk * 100;

            if (Number(data, "uptime") is { } uptime)
                metrics["uptime_days"] = uptime / 86400;

            var details = new Dictionary<string, string>();
            if (data.TryGetProperty("pveversion", out var version) && version.GetString() is { Length: > 0 } text)
                details["Version"] = text;

            // Guest counts are a second call, and a token scoped tightly enough to be
            // safe may not be allowed to list them. Losing the counts should not turn a
            // healthy node red.
            await TryCountGuestsAsync(connection, node, metrics, ct);

            var summary = metrics.TryGetValue("cpu_percent", out var cpuPercent)
                ? $"CPU {cpuPercent:0.#}%, memory {metrics.GetValueOrDefault("ram_percent"):0.#}%"
                : "Connected";

            return ProbeResult.Up(stopwatch.Elapsed, summary, metrics, details);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ex.GetBaseException().Message);
        }
    }

    private async Task TryCountGuestsAsync(
        Connection connection, string node, Dictionary<string, double> metrics, CancellationToken ct)
    {
        foreach (var (endpoint, metric) in new[] { ("qemu", "vms_running"), ("lxc", "containers_running") })
        {
            try
            {
                using var document = await GetAsync(connection, $"nodes/{node}/{endpoint}", ct);
                metrics[metric] = document.RootElement.GetProperty("data").EnumerateArray()
                    .Count(guest => guest.TryGetProperty("status", out var s) &&
                                    string.Equals(s.GetString(), "running", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                // Permission denied or an older API: the node status above still stands.
            }
        }
    }

    private async Task<JsonDocument> GetAsync(Connection connection, string path, CancellationToken ct)
    {
        var baseUrl = connection.Settings.Get("url").TrimEnd('/');
        var http = httpFactory.CreateClient(ProviderHttp.ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api2/json/{path}");

        // Proxmox's own scheme; "Bearer" is not accepted here.
        request.Headers.TryAddWithoutValidation("Authorization",
            $"PVEAPIToken={connection.Settings.Get("token_id")}={connection.Settings.Get("token_secret")}");

        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Proxmox rejected the API token. Check the token ID and secret, and that it has PVEAuditor.");
        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
}
