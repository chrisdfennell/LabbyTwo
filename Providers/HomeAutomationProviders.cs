using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// Home Assistant. Rather than trying to model every integration it might have, this
/// pulls a list of entity ids the user names and turns each into a metric — the same
/// bargain the JSON API provider makes, and the only one that scales to a system whose
/// entities are different in every house.
/// </summary>
public sealed class HomeAssistantProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "homeassistant";
    public string DisplayName => "Home Assistant";
    public string Icon => "🏠";
    public string Category => "Home";
    public string Description => "Reachability, plus any entity's state as a metric — temperatures, power, battery levels.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "http://192.168.1.10:8123", Required: true),
        new("token", "Long-lived access token", FieldKind.Password, Required: true,
            Help: "Your profile page → Security → Long-lived access tokens, at the bottom."),
        new("entities", "Entities", FieldKind.Textarea,
            "office_temp = sensor.office_temperature\nsolar_watts = sensor.solar_power",
            Help: "One per line as name = entity_id. Numeric states become metrics; " +
                  "on/off, home/away and similar count as 1 and 0."),
    ];

    /// <summary>Whatever the user listed, since nobody's Home Assistant looks like anyone else's.</summary>
    public IReadOnlyList<MetricSpec> MetricsFor(Connection connection) =>
    [
        MetricSpec.Fallback("latency_ms"),
        .. ParseEntityMap(connection.Settings.Get("entities")).Select(e => MetricSpec.Fallback(e.Name)),
    ];

    public IReadOnlyList<MetricSpec> Metrics => [new("latency_ms", "Response time", " ms")];

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var baseUrl = connection.Settings.Get("url").TrimEnd('/');
        if (baseUrl.Length == 0)
            return ProbeResult.Down(TimeSpan.Zero, "No base URL configured.");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var http = httpFactory.CreateClient(ProviderHttp.ClientName);
            var token = connection.Settings.Get("token");

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

            using var response = await http.SendAsync(request, ct);
            stopwatch.Stop();

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return ProbeResult.Down(stopwatch.Elapsed, "Home Assistant rejected the access token.");
            if (!response.IsSuccessStatusCode)
                return ProbeResult.Down(stopwatch.Elapsed, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            var metrics = new Dictionary<string, double> { ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds };
            var missing = new List<string>();

            foreach (var (name, entityId) in ParseEntityMap(connection.Settings.Get("entities")))
            {
                var value = await TryReadEntityAsync(http, baseUrl, token, entityId, ct);
                if (value is { } number)
                    metrics[name] = number;
                else
                    missing.Add(entityId);
            }

            var message = missing.Count > 0
                ? $"Connected — {missing.Count} entity/entities not readable: {string.Join(", ", missing.Take(3))}"
                : metrics.Count > 1 ? $"Connected — {metrics.Count - 1} entity/entities read" : "Connected";

            return ProbeResult.Up(stopwatch.Elapsed, message, metrics);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("url")));
        }
    }

    private static async Task<double?> TryReadEntityAsync(
        HttpClient http, string baseUrl, string token, string entityId, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/states/{Uri.EscapeDataString(entityId)}");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return AsNumber(document.RootElement.TryGetProperty("state", out var state) ? state.GetString() : null);
        }
        catch
        {
            // One unreadable entity should not fail the others or the connection.
            return null;
        }
    }

    /// <summary>
    /// Home Assistant states are strings. Numeric ones parse; the common on/off vocabulary
    /// becomes 1 and 0 so a door sensor can be charted and alerted on like anything else.
    /// </summary>
    public static double? AsNumber(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
            return null;
        if (double.TryParse(state, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return number;

        return state.Trim().ToLowerInvariant() switch
        {
            "on" or "home" or "open" or "detected" or "locked" or "true" => 1,
            "off" or "not_home" or "away" or "closed" or "clear" or "unlocked" or "false" => 0,
            // "unavailable" and "unknown" are genuinely no reading, not zero.
            _ => null,
        };
    }

    /// <summary>Parses the "name = entity_id" lines, ignoring blanks and #-comments.</summary>
    public static IEnumerable<(string Name, string EntityId)> ParseEntityMap(string raw)
    {
        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;
            var separator = trimmed.IndexOf('=');
            if (separator <= 0)
                continue;
            var name = trimmed[..separator].Trim();
            var entity = trimmed[(separator + 1)..].Trim();
            if (name.Length > 0 && entity.Length > 0)
                yield return (name, entity);
        }
    }
}

/// <summary>
/// AdGuard Home. Same job as Pi-hole and often installed instead of it, so it reports the
/// same metric names — a chart or an alert rule built for one works on the other.
/// </summary>
public sealed class AdGuardProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "adguard";
    public string DisplayName => "AdGuard Home";
    public string Icon => "🛡️";
    public string Category => "Network";
    public string Description => "Queries and blocks today, block percentage, and whether protection is on.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "http://192.168.1.53:3000", Required: true),
        new("username", "Username", FieldKind.Text, Required: true),
        new("password", "Password", FieldKind.Password, Required: true),
    ];

    // Deliberately the same names Pi-hole reports.
    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("queries_today", "Queries today"),
        new("blocked_today", "Blocked today"),
        new("blocked_percent", "Blocked", "%", 1),
        new("blocking_enabled", "Protection enabled"),
        new("avg_process_ms", "Average processing time", " ms", 1),
        new("latency_ms", "Response time", " ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Blocking is switched off", "blocking_enabled", Comparison.Below, 1, ForMinutes: 30,
            Why: "Somebody paused it to get a site working and never turned it back on. Half an " +
                 "hour, because pausing it for a few minutes is a normal thing to do."),

        new("DNS is answering slowly", "avg_process_ms", Comparison.Above, 200, ClearThreshold: 100, ForMinutes: 15,
            Why: "Everything in the house feels broken when DNS is slow, and nothing points at DNS."),
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
            var credentials = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                $"{connection.Settings.Get("username")}:{connection.Settings.Get("password")}"));

            using var statsDocument = await GetAsync(http, baseUrl, credentials, "stats", ct);
            stopwatch.Stop();

            var stats = statsDocument.RootElement;
            var metrics = new Dictionary<string, double> { ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds };

            var queries = Number(stats, "num_dns_queries") ?? 0;
            var blocked = (Number(stats, "num_blocked_filtering") ?? 0)
                          + (Number(stats, "num_replaced_safebrowsing") ?? 0)
                          + (Number(stats, "num_replaced_parental") ?? 0);

            metrics["queries_today"] = queries;
            metrics["blocked_today"] = blocked;
            metrics["blocked_percent"] = queries > 0 ? blocked / queries * 100 : 0;

            if (Number(stats, "avg_processing_time") is { } average)
                metrics["avg_process_ms"] = average * 1000;

            var enabled = true;
            try
            {
                using var statusDocument = await GetAsync(http, baseUrl, credentials, "status", ct);
                if (statusDocument.RootElement.TryGetProperty("protection_enabled", out var flag))
                    enabled = flag.ValueKind != JsonValueKind.False;
            }
            catch
            {
                // Stats alone are still a working check.
            }
            metrics["blocking_enabled"] = enabled ? 1 : 0;

            var message = $"{metrics["blocked_percent"]:0.0}% blocked today";
            return enabled
                ? ProbeResult.Up(stopwatch.Elapsed, message, metrics)
                // Protection switched off is not a dead AdGuard, but it is the thing you
                // would want to know about, so it reads as a failure like Pi-hole's does.
                : new ProbeResult(false, $"Protection is disabled — {message}", stopwatch.Elapsed, metrics);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("url")));
        }
    }

    private static async Task<JsonDocument> GetAsync(
        HttpClient http, string baseUrl, string credentials, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/control/{path}");
        request.Headers.TryAddWithoutValidation("Authorization", $"Basic {credentials}");

        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new InvalidOperationException("AdGuard Home rejected the username or password.");
        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
}
