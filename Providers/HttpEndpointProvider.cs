using System.Diagnostics;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// Named client used by every provider that just needs to make a request. Self-signed
/// certificates are the norm on a LAN, so a certificate problem must not read as "down".
/// </summary>
public static class ProviderHttp
{
    public const string ClientName = "provider";

    /// <summary>
    /// The same handler with no timeout, for downloads and uploads. Anything that streams
    /// a file should ask for this one and pass the request's CancellationToken.
    /// </summary>
    public const string TransferClientName = "provider-transfer";
}

/// <summary>
/// Any URL at all. This is the workhorse — a router admin page, a printer, some app with
/// no API — and it is what makes "add your everything" true without an integration for
/// each thing.
/// </summary>
public sealed class HttpEndpointProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "http";
    public string DisplayName => "Web service";
    public string Icon => "🌐";
    public string Category => "General";
    public string Description => "Any HTTP(S) URL — checked for a response, timed, and charted. Use this for anything without a dedicated integration.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "URL", FieldKind.Url, "http://192.168.1.10:8080", Required: true,
            Help: "Probed by the LabbyTwo server, so it needs to resolve from wherever LabbyTwo runs."),
        new("open_url", "Link opens", FieldKind.Url, "leave blank to use the URL above",
            Help: "Optional. Set this when the tile should open a different address than the one probed — a public hostname, say."),
        new("method", "Method", FieldKind.Select, Default: "GET", Options:
        [
            new SelectOption("GET", "GET"),
            new SelectOption("HEAD", "HEAD — lighter, but some apps reject it"),
        ]),
        new("expect_status", "Treat as up", FieldKind.Select, Default: "under500", Options:
        [
            new SelectOption("under500", "Any response below 500 (default — auth pages still count)"),
            new SelectOption("2xx", "Only 2xx"),
            new SelectOption("any", "Any response at all, even a 500"),
        ]),
        new("expect_text", "Body must contain", FieldKind.Text, Help: "Optional. Fails the probe when the response body lacks this text."),
        new("timeout", "Timeout (seconds)", FieldKind.Number, Default: "10") { Advanced = true },
        new("headers", "Extra headers", FieldKind.Textarea, "X-Api-Key: abc123",
            Help: "Optional, one per line as Name: value.") { Advanced = true },
    ];

    public IReadOnlyList<MetricSpec> Metrics => [new("latency_ms", "Response time", " ms")];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Answering slowly", "latency_ms", Comparison.Above, 5000, ClearThreshold: 2000, ForMinutes: 10,
            Why: "Up but crawling is the state nothing else catches — the probe succeeds, so the " +
                 "tile stays green while the thing is unusable."),
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
            var method = connection.Settings.Get("method", "GET").Equals("HEAD", StringComparison.OrdinalIgnoreCase)
                ? HttpMethod.Head
                : HttpMethod.Get;
            using var request = new HttpRequestMessage(method, url);
            foreach (var line in connection.Settings.Get("headers").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = line.IndexOf(':');
                if (separator > 0)
                    request.Headers.TryAddWithoutValidation(line[..separator].Trim(), line[(separator + 1)..].Trim());
            }

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            var code = (int)response.StatusCode;
            var ok = connection.Settings.Get("expect_status", "under500") switch
            {
                "2xx" => response.IsSuccessStatusCode,
                "any" => true,
                _ => code < 500,
            };

            if (ok && connection.Settings.Get("expect_text") is { Length: > 0 } needle)
            {
                var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                if (!body.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    stopwatch.Stop();
                    return ProbeResult.Down(stopwatch.Elapsed, $"HTTP {code} but the body did not contain \"{needle}\".");
                }
            }

            stopwatch.Stop();
            var metrics = new Dictionary<string, double> { ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds };
            return ok
                ? ProbeResult.Up(stopwatch.Elapsed, $"HTTP {code}", metrics)
                : ProbeResult.Down(stopwatch.Elapsed, $"HTTP {code} {response.ReasonPhrase}");
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
}
