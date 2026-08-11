using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// Cloudflare Tunnel. The failure worth catching is invisible from inside the house: the
/// tunnel dies, everything still answers perfectly on the LAN, and the only people who
/// notice are the ones trying to reach it from outside — usually you, from somewhere else,
/// at the worst moment.
/// </summary>
public sealed class CloudflareProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "cloudflare";
    public string DisplayName => "Cloudflare Tunnel";
    public string Icon => "🌩️";
    public string Category => "Network";
    public string Description =>
        "Whether your tunnels are up and how many connectors each has. Catches the outage nobody on the LAN can see.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("account_id", "Account ID", FieldKind.Text, Required: true,
            Help: "Cloudflare dashboard → Workers & Pages → the ID in the right-hand column, or the one in your URL."),

        new("api_token", "API token", FieldKind.Password, Required: true,
            Help: "My Profile → API Tokens → Create Token. It needs Account → Cloudflare Tunnel → Read and nothing else. " +
                  "Not the Global API Key — that one can do everything to everything."),

        new("tunnel", "Tunnel name", FieldKind.Text,
            Help: "Optional. Blank watches every tunnel on the account and reports the worst of them."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("tunnels", "Tunnels"),
        new("tunnels_healthy", "Tunnels healthy"),
        new("tunnels_down", "Tunnels down"),
        new("connections", "Active connectors"),
        new("latency_ms", "Response time", " ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("A tunnel is down", "tunnels_down", Comparison.Above, 0, ForMinutes: 5,
            Why: "Everything still works from inside the house, which is what makes this one worth being told about."),

        new("Down to one connector", "connections", Comparison.Below, 2, ForMinutes: 15,
            Why: "cloudflared normally holds four. One left means the next blip is an outage."),
    ];

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var account = connection.Settings.Get("account_id");
        var token = connection.Settings.Get("api_token");

        if (account.Length == 0 || token.Length == 0)
            return ProbeResult.Down(TimeSpan.Zero, "Needs an account ID and an API token.");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var http = httpFactory.CreateClient(ProviderHttp.ClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.cloudflare.com/client/v4/accounts/{Uri.EscapeDataString(account)}/cfd_tunnel?is_deleted=false");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            stopwatch.Stop();

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            // Cloudflare answers 200 with success=false and an errors array for most
            // mistakes, so the status code alone is not the check.
            if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
                return ProbeResult.Down(stopwatch.Elapsed, Explain(root, response.StatusCode));

            if (!root.TryGetProperty("result", out var tunnels) || tunnels.ValueKind != JsonValueKind.Array)
                return ProbeResult.Down(stopwatch.Elapsed, "No tunnels in the reply.");

            var wanted = connection.Settings.Get("tunnel");
            int total = 0, healthy = 0, down = 0, connectors = 0;
            var unhealthy = new List<string>();

            foreach (var tunnel in tunnels.EnumerateArray())
            {
                var name = tunnel.TryGetProperty("name", out var label) ? label.GetString() ?? "" : "";
                if (wanted.Length > 0 && !string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
                    continue;

                total++;

                // "healthy", "degraded", "down", or "inactive" for one never connected.
                var status = tunnel.TryGetProperty("status", out var state) ? state.GetString() ?? "" : "";
                if (status is "healthy")
                    healthy++;
                else
                {
                    down++;
                    if (name.Length > 0)
                        unhealthy.Add($"{name} ({status})");
                }

                if (tunnel.TryGetProperty("connections", out var live) && live.ValueKind == JsonValueKind.Array)
                    connectors += live.GetArrayLength();
            }

            if (total == 0)
                return ProbeResult.Down(stopwatch.Elapsed,
                    wanted.Length > 0 ? $"No tunnel called \"{wanted}\"." : "This account has no tunnels.");

            var metrics = new Dictionary<string, double>
            {
                ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds,
                ["tunnels"] = total,
                ["tunnels_healthy"] = healthy,
                ["tunnels_down"] = down,
                ["connections"] = connectors,
            };

            var message = down > 0
                ? $"{down} not healthy: {string.Join(", ", unhealthy.Take(3))}"
                : $"{healthy} healthy, {connectors} connector{(connectors == 1 ? "" : "s")}";

            return ProbeResult.Up(stopwatch.Elapsed, message, metrics);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, "api.cloudflare.com"));
        }
    }

    /// <summary>
    /// Cloudflare's error codes are numerous and mostly unhelpful; these two are the ones
    /// people actually hit, and both have a specific fix.
    /// </summary>
    private static string Explain(JsonElement root, System.Net.HttpStatusCode status)
    {
        var first = root.TryGetProperty("errors", out var errors)
            && errors.ValueKind == JsonValueKind.Array
            && errors.GetArrayLength() > 0
                ? errors[0]
                : default;

        var code = first.ValueKind == JsonValueKind.Object && first.TryGetProperty("code", out var value)
            && value.ValueKind == JsonValueKind.Number
                ? value.GetInt32()
                : 0;

        var message = first.ValueKind == JsonValueKind.Object && first.TryGetProperty("message", out var text)
            ? text.GetString() ?? ""
            : "";

        return code switch
        {
            10000 => "Cloudflare rejected the token. It needs Account → Cloudflare Tunnel → Read, " +
                     "and the account ID has to match the account the token was made on.",
            7003 => "That account ID does not look right — Cloudflare could not find it.",
            _ when (int)status == 403 => "Forbidden. The token is valid but lacks the Cloudflare Tunnel permission.",
            _ => message.Length > 0 ? $"Cloudflare said: {message}" : $"Cloudflare answered HTTP {(int)status}.",
        };
    }
}
