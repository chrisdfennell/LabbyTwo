using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// Uptime Kuma, read through its Prometheus endpoint. Kuma's own UI talks over Socket.IO,
/// which is no use to a poller — but <c>/metrics</c> is a plain text page listing every
/// monitor and its state, which is exactly what is wanted here.
///
/// The point of adding this to a dashboard that already pings things: Kuma knows about
/// checks LabbyTwo cannot make — keyword matches, certificate expiry, push monitors that
/// something else calls in on — and this pulls all of that in as numbers, so one tile can
/// say "everything Kuma watches is fine".
/// </summary>
public sealed class UptimeKumaProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "uptime-kuma";
    public string DisplayName => "Uptime Kuma";
    public string Icon => "🐨";
    public string Category => "Monitoring";
    public string Description =>
        "How many monitors are up, down or paused, and each one by name. Reads Kuma's /metrics endpoint.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "http://192.168.1.20:3001", Required: true,
            Help: "Just the host and port — /metrics is added for you."),

        new("api_key", "API key", FieldKind.Password, Required: true,
            Help: "Uptime Kuma → Profile → API Keys → Add API Key. Kuma sends it as HTTP basic auth " +
                  "with an empty username, which is what this does."),

        new("per_monitor", "A metric for each monitor", FieldKind.Bool, Default: "true",
            Help: "Off keeps only the totals. On lets you chart or alert on one particular monitor, " +
                  "which is what you want for the thing that matters more than the rest."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("monitors_up", "Monitors up"),
        new("monitors_down", "Monitors down"),
        new("monitors_paused", "Monitors paused"),
        new("monitors_total", "Monitors"),
        new("worst_response_ms", "Slowest monitor", " ms"),
        new("certs_expiring_days", "Nearest certificate expiry", " days"),
        new("latency_ms", "Response time", " ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Something Kuma watches is down", "monitors_down", Comparison.Above, 0, ForMinutes: 5,
            Why: "Five minutes, because Kuma has already applied its own retries before calling one down."),

        new("A certificate is nearly out", "certs_expiring_days", Comparison.Below, 14, ForMinutes: 60,
            Why: "Two weeks is enough to renew by hand on a Saturday. Kuma tracks this; nothing else here does."),
    ];

    /// <summary>
    /// Monitor names are the user's, not the code's, so the metric list has to come from
    /// what was last seen rather than a fixed table — the same trick the presence plugin
    /// uses. Without it a chart's metric dropdown would be blank for the interesting half.
    /// </summary>
    private readonly ConcurrentDictionary<string, IReadOnlyList<MetricSpec>> _discovered = new();

    public IReadOnlyList<MetricSpec> MetricsFor(Connection connection) =>
        _discovered.TryGetValue(connection.Id, out var seen) ? [.. Metrics, .. seen] : Metrics;

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var baseUrl = connection.Settings.Get("url").TrimEnd('/');
        if (baseUrl.Length == 0)
            return ProbeResult.Down(TimeSpan.Zero, "No base URL configured.");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var http = httpFactory.CreateClient(ProviderHttp.ClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/metrics");

            // Basic auth with no username: that is how Kuma documents its API key, and it
            // looks wrong enough that it is worth saying so here rather than in a bug report.
            var key = connection.Settings.Get("api_key");
            if (key.Length > 0)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($":{key}")));
            }

            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            stopwatch.Stop();

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return ProbeResult.Down(stopwatch.Elapsed,
                    "Kuma refused the API key. Profile → API Keys — and check it is not expired or disabled.");

            if (!response.IsSuccessStatusCode)
                return ProbeResult.Down(stopwatch.Elapsed, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            var samples = Parse(body);
            if (samples.Count == 0)
                return ProbeResult.Down(stopwatch.Elapsed,
                    "Kuma answered, but listed no monitors. Is this really an Uptime Kuma /metrics page?");

            var metrics = new Dictionary<string, double> { ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds };
            var perMonitor = connection.Settings.GetBool("per_monitor", true);
            var discovered = new List<MetricSpec>();

            var up = 0;
            var down = 0;
            var paused = 0;

            foreach (var sample in samples.Where(s => s.Name == "monitor_status"))
            {
                // 0 down, 1 up, 2 pending, 3 maintenance. Pending is a monitor mid-retry:
                // counting it as down would fire an alert Kuma itself has not decided on yet.
                switch ((int)sample.Value)
                {
                    case 1: up++; break;
                    case 0: down++; break;
                    default: paused++; break;
                }

                if (!perMonitor || sample.Monitor.Length == 0)
                    continue;

                var metricKey = $"monitor_{Slug(sample.Monitor)}";
                metrics[metricKey] = sample.Value == 1 ? 1 : 0;
                discovered.Add(new MetricSpec(metricKey, sample.Monitor));
            }

            metrics["monitors_up"] = up;
            metrics["monitors_down"] = down;
            metrics["monitors_paused"] = paused;
            metrics["monitors_total"] = up + down + paused;

            // Kuma reports seconds; everything here speaks milliseconds.
            var responses = samples.Where(s => s.Name == "monitor_response_seconds" && s.Value > 0).ToList();
            if (responses.Count > 0)
                metrics["worst_response_ms"] = responses.Max(s => s.Value) * 1000;

            var certificates = samples.Where(s => s.Name == "monitor_cert_days_remaining" && s.Value >= 0).ToList();
            if (certificates.Count > 0)
                metrics["certs_expiring_days"] = certificates.Min(s => s.Value);

            _discovered[connection.Id] = discovered;

            var message = down == 0
                ? $"{up} up{(paused > 0 ? $", {paused} paused" : "")}"
                : $"{down} down of {up + down + paused}: {string.Join(", ",
                    samples.Where(s => s.Name == "monitor_status" && s.Value == 0)
                        .Select(s => s.Monitor).Where(n => n.Length > 0).Take(3))}";

            // Deliberately still "up": Kuma is reachable and answering. Whether a monitor
            // being down is a problem is what the suggested alert rule is for — reporting
            // it as a failed probe would make Kuma's own uptime figure meaningless.
            return ProbeResult.Up(stopwatch.Elapsed, message, metrics);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("url")));
        }
    }

    private sealed record Sample(string Name, string Monitor, double Value);

    /// <summary>
    /// Enough of the Prometheus text format for this one page: a metric name, labels in
    /// braces, a value. Comments and anything unrecognised are skipped rather than fought
    /// with — a full parser would be a library, and this only ever reads Kuma's output.
    /// </summary>
    private static List<Sample> Parse(string body)
    {
        var samples = new List<Sample>();

        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            var brace = line.IndexOf('{');
            var close = line.LastIndexOf('}');
            if (brace < 0 || close < brace)
                continue;

            var name = line[..brace];
            if (!name.StartsWith("monitor_", StringComparison.Ordinal))
                continue;

            var tail = line[(close + 1)..].Trim();
            if (!double.TryParse(tail, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                continue;

            samples.Add(new Sample(name, Label(line[(brace + 1)..close], "monitor_name"), value));
        }

        return samples;
    }

    /// <summary>Pulls one label's value out of <c>a="1",monitor_name="Front door",b="2"</c>.</summary>
    private static string Label(string labels, string wanted)
    {
        var marker = $"{wanted}=\"";
        var start = labels.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return "";

        start += marker.Length;
        var end = labels.IndexOf('"', start);
        return end < 0 ? "" : labels[start..end].Replace("\\\"", "\"");
    }

    /// <summary>
    /// A monitor called "Nextcloud (external)" has to become a metric key that survives
    /// being typed into a widget, so anything that is not a letter or digit collapses to
    /// an underscore.
    /// </summary>
    private static string Slug(string name)
    {
        var slug = new StringBuilder(name.Length);
        foreach (var character in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
                slug.Append(character);
            else if (slug.Length > 0 && slug[^1] != '_')
                slug.Append('_');
        }
        return slug.ToString().Trim('_');
    }
}
