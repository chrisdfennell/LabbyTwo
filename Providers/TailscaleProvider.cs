using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// Tailscale. Two things worth watching, and the second is the one nobody thinks about:
/// which machines are actually on the tailnet, and how long until a node's key expires.
/// Key expiry is a scheduled outage you agreed to six months ago — the machine drops off
/// while you are away from home, which is exactly when you wanted to reach it.
/// </summary>
public sealed class TailscaleProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "tailscale";
    public string DisplayName => "Tailscale";
    public string Icon => "🔗";
    public string Category => "Network";
    public string Description => "Devices on your tailnet, how many are online, and the nearest key expiry.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("api_key", "API access token", FieldKind.Password, Required: true,
            Help: "login.tailscale.com → Settings → Keys → Generate access token. Read-only is enough. " +
                  "These expire after 90 days at most, which this will tell you about by going down."),

        new("tailnet", "Tailnet", FieldKind.Text, Default: "-",
            Help: "\"-\" means the tailnet the token belongs to, which is what you want unless you have several."),

        new("offline_minutes", "Call a device offline after (minutes)", FieldKind.Number, Default: "5",
            Help: "Tailscale reports when a device was last seen rather than a live status, so this is the " +
                  "line between \"asleep\" and \"gone\"."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("devices", "Devices"),
        new("devices_online", "Devices online"),
        new("devices_offline", "Devices offline"),
        new("key_expiry_days", "Nearest key expiry", " days", 1),
        new("updates_available", "Devices with updates"),
        new("latency_ms", "Response time", " ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("A node key is about to expire", "key_expiry_days", Comparison.Below, 7, ForMinutes: 60,
            Why: "Once it goes, that machine is off the tailnet until somebody re-authenticates it in person."),
    ];

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var key = connection.Settings.Get("api_key");
        if (key.Length == 0)
            return ProbeResult.Down(TimeSpan.Zero, "No access token configured.");

        var tailnet = connection.Settings.Get("tailnet", "-");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var http = httpFactory.CreateClient(ProviderHttp.ClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.tailscale.com/api/v2/tailnet/{Uri.EscapeDataString(tailnet)}/devices");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            stopwatch.Stop();

            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized)
                return ProbeResult.Down(stopwatch.Elapsed,
                    "Tailscale refused the token. They expire — generate a new one under Settings → Keys.");

            if (!response.IsSuccessStatusCode)
                return ProbeResult.Down(stopwatch.Elapsed, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("devices", out var devices)
                || devices.ValueKind != JsonValueKind.Array)
                return ProbeResult.Down(stopwatch.Elapsed, "No device list in the reply.");

            var cutoff = DateTimeOffset.UtcNow.AddMinutes(
                -Math.Max(1, connection.Settings.GetInt("offline_minutes", 5)));

            int total = 0, online = 0, updates = 0;
            double? soonestExpiry = null;
            var missing = new List<string>();

            foreach (var device in devices.EnumerateArray())
            {
                total++;

                var name = device.TryGetProperty("hostname", out var host) ? host.GetString() ?? "" : "";

                if (When(device, "lastSeen") is { } seen && seen >= cutoff)
                    online++;
                else if (name.Length > 0)
                    missing.Add(name);

                // A device with key expiry disabled reports an expiry date anyway; it just
                // never arrives. Counting it would show a permanent false alarm.
                var expiryDisabled = device.TryGetProperty("keyExpiryDisabled", out var disabled)
                    && disabled.ValueKind == JsonValueKind.True;

                if (!expiryDisabled && When(device, "expires") is { } expires)
                {
                    var days = (expires - DateTimeOffset.UtcNow).TotalDays;
                    if (days >= 0 && (soonestExpiry is null || days < soonestExpiry))
                        soonestExpiry = days;
                }

                if (device.TryGetProperty("updateAvailable", out var update) && update.ValueKind == JsonValueKind.True)
                    updates++;
            }

            var metrics = new Dictionary<string, double>
            {
                ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds,
                ["devices"] = total,
                ["devices_online"] = online,
                ["devices_offline"] = total - online,
                ["updates_available"] = updates,
            };

            if (soonestExpiry is { } soonest)
                metrics["key_expiry_days"] = soonest;

            var message = $"{online} of {total} online" +
                          (missing.Count > 0 && missing.Count <= 3 ? $" — away: {string.Join(", ", missing)}" : "");

            return ProbeResult.Up(stopwatch.Elapsed, message, metrics);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, "api.tailscale.com"));
        }
    }

    private static DateTimeOffset? When(JsonElement device, string name) =>
        device.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
}
