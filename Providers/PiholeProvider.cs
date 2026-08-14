using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// Pi-hole. Talks to the v5 <c>admin/api.php</c> summary, which v6 still serves, so one
/// implementation covers both without asking which version you run.
/// </summary>
public sealed class PiholeProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "pihole";
    public string DisplayName => "Pi-hole";
    public string Icon => "🕳️";
    public string Category => "Network";
    public string Description => "Queries handled and blocked today, block percentage, and whether blocking is enabled.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "http://192.168.1.53", Required: true,
            Help: "Just the host — the /admin/api.php part is added for you."),
        new("token", "API token", FieldKind.Password,
            Help: "Optional for the summary. Settings → API/Web interface → Show API token."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("queries_today", "Queries today"),
        new("blocked_today", "Blocked today"),
        new("blocked_percent", "Blocked", "%", 1),
        new("blocklist_size", "Domains on blocklist"),
        new("clients", "Unique clients"),
        new("blocking_enabled", "Blocking enabled"),
        new("latency_ms", "Response time", " ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Blocking switched off", "blocking_enabled", Comparison.Below, 1, ForMinutes: 30,
            Why: "Half an hour, because \"disable for 10 minutes\" is a thing people do on purpose."),
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
            var url = $"{baseUrl}/admin/api.php?summaryRaw";
            if (connection.Settings.Get("token") is { Length: > 0 } token)
                url += $"&auth={Uri.EscapeDataString(token)}";

            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                stopwatch.Stop();
                return ProbeResult.Down(stopwatch.Elapsed, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            var payload = await response.Content.ReadAsStringAsync(ct);
            stopwatch.Stop();

            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            // An unauthenticated request to a locked-down Pi-hole answers 200 with "[]".
            if (root.ValueKind != JsonValueKind.Object)
                return ProbeResult.Down(stopwatch.Elapsed, "Pi-hole returned no data — it probably wants an API token.");

            var metrics = new Dictionary<string, double> { ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds };
            void Copy(string source, string metric)
            {
                if (Number(root, source) is { } value)
                    metrics[metric] = value;
            }

            Copy("dns_queries_today", "queries_today");
            Copy("ads_blocked_today", "blocked_today");
            Copy("ads_percentage_today", "blocked_percent");
            Copy("domains_being_blocked", "blocklist_size");
            Copy("unique_clients", "clients");

            var enabled = root.TryGetProperty("status", out var status)
                && string.Equals(status.GetString(), "enabled", StringComparison.OrdinalIgnoreCase);
            metrics["blocking_enabled"] = enabled ? 1 : 0;

            var message = metrics.TryGetValue("blocked_percent", out var percent)
                ? $"{percent:0.0}% blocked today"
                : "Connected";

            // Blocking switched off is not a dead Pi-hole, but it is the thing you would
            // want to notice, so it reads as down rather than hiding in a metric.
            return enabled
                ? ProbeResult.Up(stopwatch.Elapsed, message, metrics)
                : ProbeResult.Down(stopwatch.Elapsed, "Blocking is disabled.");
        }
        catch (JsonException)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, "That URL did not return Pi-hole's API JSON — check the host.");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("url")));
        }
    }

    // ---------- Controls ----------

    /// <summary>
    /// The two buttons anybody actually wants on a Pi-hole. Disabling blocking is the one
    /// thing a household does to it, and doing it from here beats the alternative — which
    /// is finding the admin page on a phone while somebody complains that a site is broken.
    /// </summary>
    public IReadOnlyList<ProviderAction> Actions =>
    [
        new("disable", "Pause blocking", "⏸️")
        {
            Description = "Stops blocking for a while, then resumes on its own.",
            Fields =
            [
                new("minutes", "For how long", FieldKind.Select, Default: "5", Options:
                [
                    new SelectOption("1", "1 minute"),
                    new SelectOption("5", "5 minutes"),
                    new SelectOption("30", "30 minutes"),
                    new SelectOption("0", "Until I turn it back on"),
                ]),
            ],
            // No confirmation: it is reversible, it expires by itself, and the whole value
            // of the button is that it is faster than the admin page.
            Confirms = false,
        },
        new("enable", "Resume blocking", "▶️") { Confirms = false },
    ];

    /// <summary>
    /// Both of these need the API token even though the summary does not, so the buttons
    /// stay hidden until there is one rather than failing at the moment they are pressed.
    /// </summary>
    public IReadOnlyList<ProviderAction> ActionsFor(Connection connection) =>
        connection.Settings.Get("token") is { Length: > 0 } ? Actions : [];

    public async Task<ActionResult> RunActionAsync(
        Connection connection, ProviderAction action, SettingsBag input, CancellationToken ct)
    {
        var baseUrl = connection.Settings.Get("url").TrimEnd('/');
        var token = connection.Settings.Get("token");
        if (baseUrl.Length == 0 || token.Length == 0)
            return ActionResult.Failed("This needs both a base URL and an API token.");

        var minutes = Math.Clamp(input.GetInt("minutes", 5), 0, 24 * 60);
        var (query, message) = action.Id switch
        {
            // Zero seconds means indefinitely to Pi-hole, which is also what the option says.
            "disable" => ($"disable={minutes * 60}",
                minutes == 0 ? "Blocking is off until you turn it back on." : $"Blocking is off for {minutes} minutes."),
            "enable" => ("enable", "Blocking is back on."),
            _ => ("", ""),
        };

        if (query.Length == 0)
            return ActionResult.Failed($"No Pi-hole action called “{action.Id}”.");

        var http = httpFactory.CreateClient(ProviderHttp.ClientName);
        using var response = await http.GetAsync(
            $"{baseUrl}/admin/api.php?{query}&auth={Uri.EscapeDataString(token)}", ct);

        if (!response.IsSuccessStatusCode)
            return ActionResult.Failed($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

        // A bad token is answered with 200 and an empty array rather than an error, so the
        // request looks fine and blocking carries on regardless.
        var payload = await response.Content.ReadAsStringAsync(ct);
        return payload.Contains("status", StringComparison.OrdinalIgnoreCase)
            ? ActionResult.Done(message)
            : ActionResult.Failed("Pi-hole ignored that — the API token is probably wrong.");
    }

    /// <summary>Pi-hole quotes some numbers and formats others with thousands separators.</summary>
    private static double? Number(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element))
            return null;
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.String when double.TryParse(element.GetString(), NumberStyles.Any,
                CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }
}
