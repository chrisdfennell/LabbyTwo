using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// Healthchecks — the dead man's switch. Everything else here notices a thing that is
/// broken; this notices a thing that has stopped happening, which is a different and
/// harder failure. A cron job that no longer runs looks exactly like a cron job with
/// nothing to do, and the backup providers next door can only report on backups that
/// actually started.
///
/// Works against healthchecks.io or a self-hosted instance; only the base URL differs.
/// </summary>
public sealed class HealthchecksProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "healthchecks";
    public string DisplayName => "Healthchecks";
    public string Icon => "⏱️";
    public string Category => "Monitoring";
    public string Description => "Scheduled jobs that have stopped checking in — late, down, or quietly paused.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, Default: "https://healthchecks.io", Required: true,
            Help: "Your own instance, or healthchecks.io. The /api path is added for you."),

        new("api_key", "API key", FieldKind.Password, Required: true,
            Help: "Project settings → API keys. A read-only key is enough and is the right one to use."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("checks", "Checks"),
        new("checks_down", "Checks down"),
        new("checks_late", "Checks late"),
        new("checks_paused", "Checks paused"),
        new("hours_since_ping", "Quietest check", " h", 1),
        new("latency_ms", "Response time", " ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("A scheduled job has stopped", "checks_down", Comparison.Above, 0, ForMinutes: 5,
            Why: "Healthchecks has already waited out the grace period before calling one down, " +
                 "so this needs no patience of its own."),
    ];

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var baseUrl = connection.Settings.Get("url", "https://healthchecks.io").TrimEnd('/');
        var key = connection.Settings.Get("api_key");

        if (key.Length == 0)
            return ProbeResult.Down(TimeSpan.Zero, "No API key configured.");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var http = httpFactory.CreateClient(ProviderHttp.ClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v3/checks/");
            request.Headers.TryAddWithoutValidation("X-Api-Key", key);

            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            stopwatch.Stop();

            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized)
                return ProbeResult.Down(stopwatch.Elapsed,
                    "Healthchecks refused the key. It is per project, and the read-only one is under " +
                    "the same settings page as the full one.");

            if (!response.IsSuccessStatusCode)
                return ProbeResult.Down(stopwatch.Elapsed, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("checks", out var checks)
                || checks.ValueKind != JsonValueKind.Array)
                return ProbeResult.Down(stopwatch.Elapsed, "No checks in the reply.");

            int total = 0, down = 0, late = 0, paused = 0;
            double quietest = 0;
            var failing = new List<string>();

            foreach (var check in checks.EnumerateArray())
            {
                total++;

                var name = check.TryGetProperty("name", out var label) ? label.GetString() ?? "" : "";
                var status = check.TryGetProperty("status", out var value) ? value.GetString() ?? "" : "";

                switch (status)
                {
                    case "down":
                        down++;
                        if (name.Length > 0)
                            failing.Add(name);
                        break;
                    case "grace":
                        // Late, but inside the grace period — Healthchecks has not given up
                        // yet, and neither should an alert.
                        late++;
                        break;
                    case "paused":
                        paused++;
                        break;
                }

                if (status != "paused" && When(check, "last_ping") is { } pinged)
                    quietest = Math.Max(quietest, (DateTimeOffset.UtcNow - pinged).TotalHours);
            }

            var metrics = new Dictionary<string, double>
            {
                ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds,
                ["checks"] = total,
                ["checks_down"] = down,
                ["checks_late"] = late,
                ["checks_paused"] = paused,
            };

            if (quietest > 0)
                metrics["hours_since_ping"] = quietest;

            var message = down > 0
                ? $"{down} down: {string.Join(", ", failing.Take(3))}"
                : late > 0
                    ? $"{late} late of {total}"
                    : $"{total} check{(total == 1 ? "" : "s")} healthy";

            return ProbeResult.Up(stopwatch.Elapsed, message, metrics);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("url")));
        }
    }

    private static DateTimeOffset? When(JsonElement check, string name) =>
        check.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
}
