using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// Prometheus, as a way of getting at everything you already scrape. This is the highest
/// leverage integration in the app: one connection covers node_exporter, cAdvisor,
/// blackbox, smartctl_exporter and anything else in your scrape config, because the
/// numbers come from a query you write rather than from a shape this code knows.
///
/// Like the JSON API provider, the metrics are the user's rather than the code's — so
/// <see cref="MetricsFor"/> reads the query list, and the widget editor's dropdown offers
/// exactly what this connection produces.
/// </summary>
public sealed class PrometheusProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "prometheus";
    public string DisplayName => "Prometheus";
    public string Icon => "🔥";
    public string Category => "Monitoring";
    public string Description =>
        "Any number in Prometheus, from a query you write. One connection covers every exporter you scrape.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "http://192.168.1.20:9090", Required: true,
            Help: "The Prometheus server, or anything that speaks its query API — Thanos, Mimir, VictoriaMetrics."),

        new("queries", "Queries", FieldKind.Textarea, Required: true,
            Default: "cpu_percent = 100 - avg(rate(node_cpu_seconds_total{mode=\"idle\"}[5m])) * 100",
            Help: "One per line, as name = query. The name is what the metric is called here — keep it short " +
                  "and it charts like any other. Anything after a # is ignored, and a query returning several " +
                  "series takes the first, so aggregate in the query rather than here."),

        new("labels", "Labels", FieldKind.Textarea,
            Help: "Optional, one per line as name = Label · unit · decimals — say, " +
                  "cpu_percent = CPU · % · 1. Only affects how the number reads on a tile."),

        new("bearer", "Bearer token", FieldKind.Password,
            Help: "Optional. For a Prometheus behind an auth proxy or Grafana Cloud."),

        new("username", "Basic auth user", FieldKind.Text, Help: "Optional, if it is behind basic auth instead."),
        new("password", "Basic auth password", FieldKind.Password),
    ];

    public IReadOnlyList<MetricSpec> Metrics => [new("latency_ms", "Response time", " ms")];

    /// <summary>Whatever the user asked for, described however they labelled it.</summary>
    public IReadOnlyList<MetricSpec> MetricsFor(Connection connection)
    {
        var labels = ParseLabels(connection.Settings.Get("labels"));

        return
        [
            .. Metrics,
            .. ParseQueries(connection.Settings.Get("queries"))
                // Undescribed names still read well: Fallback humanises them and honours a
                // trailing unit, so "disk_free_gb" needs no label line at all.
                .Select(query => labels.TryGetValue(query.Name, out var described)
                    ? described
                    : MetricSpec.Fallback(query.Name)),
        ];
    }

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var baseUrl = connection.Settings.Get("url").TrimEnd('/');
        if (baseUrl.Length == 0)
            return ProbeResult.Down(TimeSpan.Zero, "No base URL configured.");

        var queries = ParseQueries(connection.Settings.Get("queries"));
        if (queries.Count == 0)
            return ProbeResult.Down(TimeSpan.Zero, "No queries configured — add one as name = query.");

        var stopwatch = Stopwatch.StartNew();
        var metrics = new Dictionary<string, double>();
        var failures = new List<string>();

        try
        {
            var http = httpFactory.CreateClient(ProviderHttp.ClientName);

            foreach (var (name, expression) in queries)
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get, $"{baseUrl}/api/v1/query?query={Uri.EscapeDataString(expression)}");
                Authorise(request, connection);

                using var response = await http.SendAsync(request, ct);
                var body = await response.Content.ReadAsStringAsync(ct);

                if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                {
                    stopwatch.Stop();
                    return ProbeResult.Down(stopwatch.Elapsed,
                        "Prometheus refused the credentials. Check the token, or the proxy in front of it.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    // A bad query is a 400 with the reason in the body, and the reason is
                    // the only useful thing to show — it names the character it choked on.
                    failures.Add($"{name}: {Reason(body) ?? $"HTTP {(int)response.StatusCode}"}");
                    continue;
                }

                if (Value(body) is { } value)
                    metrics[name] = value;
                else
                    failures.Add($"{name}: no data");
            }

            stopwatch.Stop();
            metrics["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds;

            // Every query failing means the connection is wrong; some failing is a query to
            // fix, and the rest of the numbers are still worth recording.
            if (metrics.Count == 1 && failures.Count > 0)
                return ProbeResult.Down(stopwatch.Elapsed, string.Join("; ", failures.Take(3)));

            var message = failures.Count == 0
                ? $"{queries.Count} quer{(queries.Count == 1 ? "y" : "ies")} answered"
                : $"{metrics.Count - 1} of {queries.Count} answered — {string.Join("; ", failures.Take(2))}";

            return ProbeResult.Up(stopwatch.Elapsed, message, metrics);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, connection.Settings.Get("url")));
        }
    }

    private static void Authorise(HttpRequestMessage request, Connection connection)
    {
        if (connection.Settings.Get("bearer") is { Length: > 0 } bearer)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }
        else if (connection.Settings.Get("username") is { Length: > 0 } user)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{connection.Settings.Get("password")}")));
        }
    }

    /// <summary>
    /// The scalar out of an instant query. Prometheus answers
    /// <c>{"data":{"resultType":"vector","result":[{"value":[ts,"1.23"]}]}}</c>, with the
    /// number as a string — and "NaN" for a query that matched but computed nothing.
    /// </summary>
    private static double? Value(string body)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("result", out var result))
            return null;

        var element = result.ValueKind switch
        {
            JsonValueKind.Array when result.GetArrayLength() > 0 => result[0],
            JsonValueKind.Object => result,
            _ => (JsonElement?)null,
        };

        if (element is not { } first)
            return null;

        // A vector carries "value": [timestamp, "1.23"]; a scalar is that pair directly.
        var pair = first.ValueKind == JsonValueKind.Object && first.TryGetProperty("value", out var value)
            ? value
            : first;

        if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() < 2)
            return null;

        var raw = pair[1].ValueKind == JsonValueKind.String ? pair[1].GetString() : pair[1].ToString();
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && !double.IsNaN(parsed)
            ? parsed
            : null;
    }

    private static string? Reason(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<(string Name, string Expression)> ParseQueries(string text)
    {
        var queries = new List<(string, string)>();

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
                continue;

            // Split on the first = only: a PromQL expression is full of them.
            var split = trimmed.IndexOf('=');
            if (split <= 0)
                continue;

            var name = trimmed[..split].Trim();
            var expression = trimmed[(split + 1)..].Trim();
            if (name.Length > 0 && expression.Length > 0)
                queries.Add((name, expression));
        }

        return queries;
    }

    /// <summary>Reads <c>cpu_percent = CPU · % · 1</c> into a <see cref="MetricSpec"/>.</summary>
    private static Dictionary<string, MetricSpec> ParseLabels(string text)
    {
        var labels = new Dictionary<string, MetricSpec>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            var split = trimmed.IndexOf('=');
            if (trimmed.Length == 0 || trimmed[0] == '#' || split <= 0)
                continue;

            var key = trimmed[..split].Trim();
            // Accept the middle dot people will copy from the help text, or a plain pipe.
            var parts = trimmed[(split + 1)..].Split(['·', '|'], StringSplitOptions.TrimEntries);
            if (key.Length == 0 || parts.Length == 0 || parts[0].Length == 0)
                continue;

            var decimals = parts.Length > 2 && int.TryParse(parts[2], out var places) ? places : 0;
            labels[key] = new MetricSpec(key, parts[0], parts.Length > 1 ? parts[1] : "", decimals);
        }

        return labels;
    }
}
