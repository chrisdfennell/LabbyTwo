using System.Diagnostics;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// Scrutiny — SMART data for every disk, collected somewhere it can be looked at. This
/// pairs with the NAS providers rather than duplicating them: a NAS says the volume is
/// 80% full, Scrutiny says one of the disks under it has reallocated sectors and is about
/// to take the volume with it.
/// </summary>
public sealed class ScrutinyProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "scrutiny";
    public string DisplayName => "Scrutiny (SMART)";
    public string Icon => "🩺";
    public string Category => "Storage";
    public string Description => "Disk health from SMART — how many drives are failing, the hottest one, and their age.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "http://192.168.1.62:8080", Required: true,
            Help: "Scrutiny's web interface. It has no authentication of its own."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("disks", "Disks"),
        new("disks_failing", "Disks failing"),
        new("disks_warning", "Disks with warnings"),
        new("hottest_disk_c", "Hottest disk", "°C", 1),
        new("oldest_disk_years", "Oldest disk", " years", 1),
        new("latency_ms", "Response time", " ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("A disk is failing", "disks_failing", Comparison.Above, 0, ForMinutes: 5,
            Why: "SMART has decided this drive is going. This is the alert you want to arrive before the array does."),

        new("Disks running hot", "hottest_disk_c", Comparison.Above, 50, ClearThreshold: 45, ForMinutes: 30,
            Why: "Sustained heat is what turns a healthy drive into a failing one. Usually a dead fan."),
    ];

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var baseUrl = connection.Settings.Get("url").TrimEnd('/');
        if (baseUrl.Length == 0)
            return ProbeResult.Down(TimeSpan.Zero, "No base URL configured.");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var http = httpFactory.CreateClient(ProviderHttp.ClientName);
            using var response = await http.GetAsync($"{baseUrl}/api/summary", ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
                return ProbeResult.Down(stopwatch.Elapsed, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("summary", out var summary)
                || summary.ValueKind != JsonValueKind.Object)
                return ProbeResult.Down(stopwatch.Elapsed, "No summary in the reply — is this Scrutiny?");

            var metrics = new Dictionary<string, double> { ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds };

            int disks = 0, failing = 0, warning = 0;
            double hottest = 0, oldestHours = 0;
            var names = new List<string>();

            foreach (var entry in summary.EnumerateObject())
            {
                disks++;

                if (entry.Value.TryGetProperty("device", out var device))
                {
                    // device_status is a bit field: 0 passed, 1 failed by SMART itself,
                    // 2 failed by Scrutiny's own thresholds, 4 warning.
                    var status = (int)(Number(device, "device_status") ?? 0);
                    if ((status & 3) != 0)
                    {
                        failing++;
                        if (device.TryGetProperty("device_name", out var name) && name.GetString() is { } text)
                            names.Add(text);
                    }
                    else if (status != 0)
                    {
                        warning++;
                    }
                }

                if (entry.Value.TryGetProperty("smart", out var smart))
                {
                    if (Number(smart, "temp") is { } temperature)
                        hottest = Math.Max(hottest, temperature);
                    if (Number(smart, "power_on_hours") is { } hours)
                        oldestHours = Math.Max(oldestHours, hours);
                }
            }

            metrics["disks"] = disks;
            metrics["disks_failing"] = failing;
            metrics["disks_warning"] = warning;
            if (hottest > 0)
                metrics["hottest_disk_c"] = hottest;
            if (oldestHours > 0)
                metrics["oldest_disk_years"] = oldestHours / 24 / 365;

            var message = failing > 0
                ? $"{failing} disk{(failing == 1 ? "" : "s")} failing: {string.Join(", ", names.Take(3))}"
                : $"{disks} disk{(disks == 1 ? "" : "s")} healthy{(hottest > 0 ? $", hottest {hottest:0}°C" : "")}";

            // A failing disk is not a failed probe: Scrutiny is answering perfectly well,
            // and the alert rule above is what should be shouting, not the tile going red.
            return ProbeResult.Up(stopwatch.Elapsed, message, metrics);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("url")));
        }
    }

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
}
