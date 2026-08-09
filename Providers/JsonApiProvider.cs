using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// Any JSON endpoint, turned into metrics by dotted paths. This is the escape hatch that
/// keeps "add your everything" honest: a UPS daemon, a 3D printer, a solar inverter, a
/// weather API — anything that answers JSON becomes chartable without a provider of its own.
/// </summary>
public sealed class JsonApiProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "json";
    public string DisplayName => "JSON API";
    public string Icon => "🧬";
    public string Category => "General";
    public string Description => "Any endpoint returning JSON. Pull numbers out with dotted paths and they become tiles and charts like any other metric.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "URL", FieldKind.Url, "http://192.168.1.50:8080/api/stats", Required: true),
        new("metrics", "Metrics", FieldKind.Textarea,
            "temperature = sensors.cpu.temp\nqueue = jobs[0].pending",
            Required: true,
            Help: "One per line as name = path. Use dots for objects and [n] for array elements. " +
                  "Booleans count as 1 and 0; a numeric string is parsed."),
        new("status_path", "Status path", FieldKind.Text, "state",
            Help: "Optional. A field whose value decides up or down — true, \"ok\", \"online\", \"up\" and 1 all count as up. " +
                  "Leave blank to treat any successful response as up."),
        new("label_path", "Label path", FieldKind.Text,
            Help: "Optional. A field to show as the tile's message, e.g. version or state."),
        new("headers", "Extra headers", FieldKind.Textarea, "Authorization: Bearer …",
            Help: "Optional, one per line as Name: value."),
        new("timeout", "Timeout (seconds)", FieldKind.Number, Default: "10"),
    ];

    /// <summary>
    /// The metric set here is whatever the user typed into the Metrics box, so it is
    /// per-connection rather than per-provider. Declaring it puts those names in the
    /// widget editor's dropdown instead of leaving them to be retyped from memory.
    /// </summary>
    public IReadOnlyList<MetricSpec> MetricsFor(Connection connection) =>
    [
        MetricSpec.Fallback("latency_ms"),
        .. ParseMetricMap(connection.Settings.Get("metrics")).Select(m => MetricSpec.Fallback(m.Name)),
    ];

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var url = connection.Settings.Get("url");
        if (string.IsNullOrWhiteSpace(url))
            return ProbeResult.Down(TimeSpan.Zero, "No URL configured.");

        var timeout = TimeSpan.FromSeconds(Math.Clamp(connection.Settings.GetInt("timeout", 10), 1, 120));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var http = httpFactory.CreateClient(ProviderHttp.ClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            foreach (var line in connection.Settings.Get("headers").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = line.IndexOf(':');
                if (separator > 0)
                    request.Headers.TryAddWithoutValidation(line[..separator].Trim(), line[(separator + 1)..].Trim());
            }

            using var response = await http.SendAsync(request, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                stopwatch.Stop();
                return ProbeResult.Down(stopwatch.Elapsed, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            var payload = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            stopwatch.Stop();

            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            var metrics = new Dictionary<string, double> { ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds };
            var missing = new List<string>();

            foreach (var (name, path) in ParseMetricMap(connection.Settings.Get("metrics")))
            {
                if (Resolve(root, path) is { } element && AsNumber(element) is { } number)
                    metrics[name] = number;
                else
                    missing.Add(name);
            }

            // A path that stops matching is how these break — usually the API changed
            // shape. If every configured path missed, that is a failure, not a healthy
            // service; latency_ms is always present, so it alone means nothing resolved.
            if (missing.Count > 0 && metrics.Count == 1)
                return ProbeResult.Down(stopwatch.Elapsed,
                    $"Responded, but no configured path matched: {string.Join(", ", missing)}.");

            var statusPath = connection.Settings.Get("status_path");
            if (statusPath.Length > 0)
            {
                var element = Resolve(root, statusPath);
                if (element is null)
                    return ProbeResult.Down(stopwatch.Elapsed, $"Status path \"{statusPath}\" was not in the response.");
                if (!IsUp(element.Value))
                    return ProbeResult.Down(stopwatch.Elapsed, $"{statusPath} = {Describe(element.Value)}");
            }

            var label = connection.Settings.Get("label_path") is { Length: > 0 } labelPath
                ? Resolve(root, labelPath) is { } found ? Describe(found) : null
                : null;

            var message = label ?? (missing.Count > 0
                ? $"OK — {missing.Count} path(s) did not match"
                : "OK");

            return ProbeResult.Up(stopwatch.Elapsed, message, metrics);
        }
        catch (JsonException ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, $"The response was not valid JSON: {ex.Message}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, $"Timed out after {timeout.TotalSeconds:0}s.");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ex.GetBaseException().Message);
        }
    }

    /// <summary>Parses the "name = path" lines, ignoring blanks and #-comments.</summary>
    public static IEnumerable<(string Name, string Path)> ParseMetricMap(string raw)
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
            var path = trimmed[(separator + 1)..].Trim();
            if (name.Length > 0 && path.Length > 0)
                yield return (name, path);
        }
    }

    /// <summary>
    /// Walks a dotted path with optional [n] indexers. Deliberately tiny — this is not
    /// JSONPath, it is the 95% of shapes a home lab API actually returns.
    /// </summary>
    public static JsonElement? Resolve(JsonElement root, string path)
    {
        var current = root;
        foreach (var rawSegment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = rawSegment;

            // A segment may be "items[2]" or bare "items" or just "[2]".
            while (true)
            {
                var bracket = segment.IndexOf('[');
                var name = bracket < 0 ? segment : segment[..bracket];

                if (name.Length > 0)
                {
                    if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out var child))
                        return null;
                    current = child;
                }

                if (bracket < 0)
                    break;

                var close = segment.IndexOf(']', bracket);
                if (close < 0 || !int.TryParse(segment[(bracket + 1)..close], out var index))
                    return null;
                if (current.ValueKind != JsonValueKind.Array || index < 0 || index >= current.GetArrayLength())
                    return null;
                current = current[index];

                segment = segment[(close + 1)..];
                if (segment.Length == 0)
                    break;
            }
        }
        return current;
    }

    private static double? AsNumber(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        // Plenty of APIs quote their numbers; "42" is more useful charted than discarded.
        JsonValueKind.String when double.TryParse(element.GetString(), NumberStyles.Any,
            CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    private static bool IsUp(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => element.GetDouble() != 0,
        JsonValueKind.String => element.GetString()?.Trim().ToLowerInvariant()
            is "ok" or "up" or "online" or "healthy" or "running" or "active" or "true" or "enabled" or "1",
        _ => false,
    };

    private static string Describe(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.ToString(),
        _ => element.ValueKind.ToString().ToLowerInvariant(),
    };
}
