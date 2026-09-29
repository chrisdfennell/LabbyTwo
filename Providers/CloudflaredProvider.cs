using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// cloudflared itself, asked over its own metrics port rather than through Cloudflare's
/// API. No account ID, no token — just the container, which already knows better than
/// anyone whether it is connected: <c>/ready</c> answers 200 with how many edge
/// connections it holds, or 503 when it holds none, and <c>/metrics</c> says where they
/// land and how much traffic has come through.
///
/// A separate provider rather than a mode of <see cref="CloudflareProvider"/>, because the
/// two have nothing to fill in in common. One form with an account ID that is required
/// for one mode and meaningless for the other would be a form that lies about what it
/// needs. They share metric names instead — <c>connections</c>, <c>edge_locations</c> —
/// so the tunnel card and a rule written for one read the other unchanged, and running
/// both is fine: the API sees every tunnel from outside, this sees one connector from in.
///
/// The one thing this cannot see is a tunnel that cloudflared believes is up and
/// Cloudflare does not. That is rare; the API provider is the answer if it matters.
/// </summary>
public sealed class CloudflaredProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "cloudflared";
    public string DisplayName => "cloudflared (local)";
    public string Icon => "🌩️";
    public string Category => "Network";
    public string Description =>
        "Your tunnel, asked straight from the cloudflared container — connections, the data centres they reach, " +
        "and traffic through it. No Cloudflare API token needed.";

    /// <summary>
    /// The compose line, spelled out where it is needed. Nobody has the metrics port on by
    /// default, and "enable metrics" is not an instruction anyone can follow without
    /// looking it up.
    /// </summary>
    public const string SetupHelp =
        "cloudflared only serves this when started with a metrics address. In the cloudflared service in " +
        "docker-compose.yml, set command: tunnel --no-autoupdate --metrics 0.0.0.0:2000 run " +
        "(keep your --token or tunnel name), then docker compose up -d. " +
        "LabbyTwo has to be on the same Docker network as cloudflared to reach it by name — no port needs publishing.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Metrics address", FieldKind.Url, "http://cloudflared:2000", Required: true,
            Default: "http://cloudflared:2000",
            Help: SetupHelp),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("connections", "Active connectors"),
        new("edge_locations", "Edge locations"),
        new("requests_per_min", "Requests", "/min", 1),
        new("errors_per_min", "Request errors", "/min", 1),
        new("latency_ms", "Response time", " ms"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        // Below 1, not below 0 or "at most 0": comparisons are strict, and a healthy tunnel
        // holds four, so only zero breaches this. Three minutes lets cloudflared ride out a
        // routine reconnect, which it does on its own in seconds.
        new("The tunnel dropped", "connections", Comparison.Below, 1, ForMinutes: 3,
            Why: "cloudflared is running but has no connection to Cloudflare, so nothing outside can reach you."),

        new("Down to one connection", "connections", Comparison.Below, 2, ForMinutes: 15,
            Why: "cloudflared normally holds four. One left means the next blip is an outage."),

        // Above 0, because any error that lasts ten minutes deserves a look, and a
        // tunnel with no errors reports exactly zero.
        new("The tunnel is failing requests", "errors_per_min", Comparison.Above, 0, ForMinutes: 10,
            Why: "Requests are reaching the tunnel but not your service — usually a container that is down " +
                 "or an ingress rule pointing at the wrong port."),
    ];

    /// <summary>
    /// The last reading of the two traffic counters, per connection, so the next probe can
    /// turn a lifetime total into a rate. A counter since cloudflared started is useless to
    /// chart — it only ever climbs — and useless to alert on for the same reason.
    /// </summary>
    private readonly ConcurrentDictionary<string, Counters> _counters = new();

    public sealed record Counters(DateTimeOffset At, double Requests, double Errors);

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var baseUrl = BaseUrl(connection.Settings.Get("url"));
        if (baseUrl.Length == 0)
            return ProbeResult.Down(TimeSpan.Zero, "No metrics address configured. " + SetupHelp);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var http = httpFactory.CreateClient(ProviderHttp.ClientName);

            using var ready = await http.GetAsync($"{baseUrl}/ready", ct);
            var readyBody = await ready.Content.ReadAsStringAsync(ct);

            // 200 and 503 are both cloudflared answering. Anything else is something else on
            // that port — a 404 from a web server, say — and should not read as a tunnel.
            if (ready.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable))
            {
                stopwatch.Stop();
                return ProbeResult.Down(stopwatch.Elapsed,
                    $"{baseUrl} answered HTTP {(int)ready.StatusCode} to /ready — that does not look like cloudflared's " +
                    "metrics port. Check the port matches the --metrics address.");
            }

            var (readyConnections, connectorId) = ReadReady(readyBody);

            // /metrics is the richer half but not essential: /ready alone says whether the
            // tunnel is up, so a failure here costs detail, not the check.
            IReadOnlyList<PrometheusText.Sample> samples = [];
            try
            {
                using var scrape = await http.GetAsync($"{baseUrl}/metrics", ct);
                if (scrape.IsSuccessStatusCode)
                    samples = PrometheusText.Parse(await scrape.Content.ReadAsStringAsync(ct));
            }
            catch (HttpRequestException)
            {
            }

            stopwatch.Stop();
            return Read(connection.Id, ready.StatusCode == HttpStatusCode.OK, readyConnections, connectorId,
                samples, stopwatch.Elapsed, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, Explain(ex, baseUrl));
        }
    }

    /// <summary>
    /// The probe's result from what the two endpoints said. Separate from the HTTP so the
    /// reading — which is most of what can be wrong — is testable on its own.
    /// </summary>
    public ProbeResult Read(
        string connectionId, bool isReady, int? readyConnections, string connectorId,
        IReadOnlyList<PrometheusText.Sample> samples, TimeSpan elapsed, DateTimeOffset now)
    {
        // /ready is cloudflared's own verdict, so it wins; the HA gauge is the same number
        // from /metrics, for a version whose /ready body is not the JSON expected.
        var connections = readyConnections
            ?? (int?)PrometheusText.Sum(samples, "cloudflared_tunnel_ha_connections")
            ?? (isReady ? 1 : 0);

        // server_locations is 1 for where a connection is now and 0 for where it used to be.
        var edges = samples
            .Where(s => s.Name == "cloudflared_tunnel_server_locations" && s.Value > 0)
            .Select(s => s.Label("edge_location"))
            .ToList();

        var metrics = new Dictionary<string, double>
        {
            ["latency_ms"] = elapsed.TotalMilliseconds,
            ["connections"] = connections,
            ["edge_locations"] = CloudflareEdge.Count(edges),
        };

        if (PrometheusText.Sum(samples, "cloudflared_tunnel_total_requests") is { } requests)
        {
            var errors = PrometheusText.Sum(samples, "cloudflared_tunnel_request_errors") ?? 0;
            var current = new Counters(now, requests, errors);
            if (_counters.TryGetValue(connectionId, out var previous) && Rates(previous, current) is { } rates)
            {
                metrics["requests_per_min"] = rates.Requests;
                metrics["errors_per_min"] = rates.Errors;
            }
            _counters[connectionId] = current;
        }

        var details = new Dictionary<string, string>();
        var where = CloudflareEdge.Summarise(edges);
        details["Connections"] = where.Length > 0 ? $"{connections} · {where}" : connections.ToString();

        if (samples.FirstOrDefault(s => s.Name is "build_info" or "cloudflared_build_info" && s.Label("version").Length > 0)
            is { } build)
            details["cloudflared"] = build.Label("version");

        if (PrometheusText.Sum(samples, "process_start_time_seconds") is { } started and > 0)
        {
            var since = DateTimeOffset.FromUnixTimeMilliseconds((long)(started * 1000));
            metrics["uptime_days"] = Math.Max(0, (now - since).TotalDays);
            details["Started"] = Ago.Since(since, now);
        }

        if (connectorId.Length > 0)
            details["Connector"] = connectorId.Length > 8 ? connectorId[..8] : connectorId;

        if (!isReady || connections < 1)
        {
            // Down, but with the numbers: the readiness endpoint saying "not ready" is the
            // tunnel being down, and a tile showing green beside it would be the one lie
            // this integration exists to prevent. The metrics still go with it, so a rule
            // on connections sees the zero rather than no reading at all.
            return new ProbeResult(false,
                "cloudflared is running but not connected to Cloudflare — nothing outside can reach you.",
                elapsed, metrics, details);
        }

        var message = $"{connections} connection{(connections == 1 ? "" : "s")}" + (where.Length > 0 ? $" via {where}" : "");
        return ProbeResult.Up(elapsed, message, metrics, details);
    }

    /// <summary>
    /// Per-minute rates between two readings of the counters. Null when there is nothing
    /// honest to say: too little time between them to be a rate, or a counter that went
    /// backwards because cloudflared restarted and started counting from zero.
    /// </summary>
    public static (double Requests, double Errors)? Rates(Counters previous, Counters current)
    {
        var minutes = (current.At - previous.At).TotalMinutes;
        if (minutes < 1.0 / 60 || current.Requests < previous.Requests || current.Errors < previous.Errors)
            return null;

        return ((current.Requests - previous.Requests) / minutes, (current.Errors - previous.Errors) / minutes);
    }

    /// <summary>
    /// <c>{"status":200,"readyConnections":4,"connectorId":"…"}</c>. A 503 carries the same
    /// shape with zero; a body that is not JSON at all leaves the count unknown.
    /// </summary>
    public static (int? Connections, string ConnectorId) ReadReady(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, "");

            int? count = root.TryGetProperty("readyConnections", out var n) && n.ValueKind == JsonValueKind.Number
                ? n.GetInt32()
                : null;
            var id = root.TryGetProperty("connectorId", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? ""
                : "";
            return (count, id);
        }
        catch (JsonException)
        {
            return (null, "");
        }
    }

    /// <summary>
    /// What people paste: the base, or the /metrics or /ready URL they found in a guide.
    /// All three mean the same server.
    /// </summary>
    public static string BaseUrl(string raw)
    {
        var url = raw.Trim().TrimEnd('/');
        foreach (var suffix in new[] { "/metrics", "/ready" })
        {
            if (url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                url = url[..^suffix.Length];
        }
        if (url.Length > 0 && !url.Contains("://", StringComparison.Ordinal))
            url = "http://" + url;
        return url;
    }

    /// <summary>
    /// Refused and unresolvable are the two ways this goes wrong on a first try, and both
    /// have the same fix — the metrics address is off, or LabbyTwo is on another network —
    /// so say that rather than only the socket error.
    /// </summary>
    private static string Explain(Exception ex, string baseUrl)
    {
        var described = ProbeError.Describe(ex, baseUrl);
        var root = ex.GetBaseException();
        return root is System.Net.Sockets.SocketException
            ? $"{described} {SetupHelp}"
            : described;
    }
}
