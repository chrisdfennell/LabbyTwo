using System.Diagnostics;
using System.Net;
using System.Text.Json;
using LabbyTwo.Core;
using LabbyTwo.Providers;

namespace LabbyTwo.GluetunPlugin;

/// <summary>
/// Gluetun's control server: whether the tunnel is actually up, which country it is
/// exiting from, and whether port forwarding got a port.
///
/// Worth monitoring because of how a VPN fails. When gluetun drops, the containers sharing
/// its network namespace do not go down — they go silent. qBittorrent still answers, still
/// says it is running, and simply stops moving bytes. Nothing on a dashboard shows that
/// unless something is watching the tunnel itself.
/// </summary>
public sealed class GluetunProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "gluetun";
    public string DisplayName => "Gluetun VPN";
    public string Icon => "🛡️";
    public string Category => "Network";
    public string Description =>
        "A Gluetun VPN container — tunnel state, the public IP and country it exits from, and the forwarded port.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Control server URL", FieldKind.Url, "http://gluetun:8000", Required: true,
            Help: "Gluetun's HTTP control server, port 8000 by default. It must be published or on a network " +
                  "LabbyTwo has joined — and note that containers inside gluetun's namespace share this address."),

        new("api_key", "API key", FieldKind.Password,
            Help: "Only needed on Gluetun 3.40 and later, and only if you configured authentication. " +
                  "Leave blank otherwise."),

        new("expect_country", "Expected country", FieldKind.Text, "Netherlands",
            Help: "Optional. If set, the probe fails when the exit country is anything else — which is how " +
                  "you find out the tunnel came back up somewhere you did not intend."),

        new("expect_port_forward", "Expect a forwarded port", FieldKind.Bool, Default: "false",
            Help: "Turn on if your provider forwards a port. A zero then counts as a failure rather than a fact."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("vpn_up", "Tunnel up"),
        new("forwarded_port", "Forwarded port"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        // Five minutes, not instant: gluetun reconnects on its own and a brief flap is
        // normal. Five minutes down means it is not coming back by itself.
        new("VPN tunnel is down", "vpn_up", Comparison.Below, 1, ForMinutes: 5,
            Why: "Anything sharing gluetun's network has no route out, and will fail quietly rather than loudly."),
    ];

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var status = await StatusAsync(connection, ct);
            var running = status.Equals("running", StringComparison.OrdinalIgnoreCase);

            using var ip = await GetAsync(connection, "/v1/publicip/ip", ct);
            var country = Text(ip.RootElement, "country");
            var city = Text(ip.RootElement, "city");
            var publicIp = Text(ip.RootElement, "public_ip");

            var port = await ForwardedPortAsync(connection, ct);

            stopwatch.Stop();

            var metrics = new Dictionary<string, double>
            {
                ["vpn_up"] = running ? 1 : 0,
                ["forwarded_port"] = port,
                ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds,
            };

            var details = new Dictionary<string, string>();
            if (publicIp.Length > 0) details["Public IP"] = publicIp;
            if (country.Length > 0) details["Exit country"] = city.Length > 0 ? $"{city}, {country}" : country;
            if (port > 0) details["Forwarded port"] = port.ToString();

            // Three ways this is "up but wrong", each reported as down because each one
            // means traffic is not going where you think it is.
            if (!running)
                return Fail(stopwatch, $"The tunnel is {status}.", metrics, details);

            var expected = connection.Settings.Get("expect_country");
            if (expected.Length > 0 && !country.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                return Fail(stopwatch,
                    country.Length == 0
                        ? $"Expected to exit in {expected}, but Gluetun did not report a country."
                        : $"Exiting in {country}, not {expected}.",
                    metrics, details);
            }

            if (connection.Settings.GetBool("expect_port_forward") && port <= 0)
                return Fail(stopwatch, "The tunnel is up but no port has been forwarded.", metrics, details);

            var where = country.Length > 0 ? $" via {country}" : "";
            var forwarded = port > 0 ? $", port {port}" : "";
            return ProbeResult.Up(stopwatch.Elapsed, $"Connected{where}{forwarded}", metrics, details);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, Explain(ex, connection));
        }
    }

    // A failed probe still carries its metrics, so vpn_up = 0 is recorded and chartable
    // rather than leaving a gap exactly when something went wrong.
    private static ProbeResult Fail(
        Stopwatch stopwatch, string message,
        Dictionary<string, double> metrics, Dictionary<string, string> details) =>
        new(false, message, stopwatch.Elapsed, metrics, details.Count > 0 ? details : null);

    /// <summary>
    /// Gluetun moved this endpoint. Newer builds answer /v1/vpn/status whatever the
    /// protocol; older ones only know the OpenVPN-specific path.
    /// </summary>
    private async Task<string> StatusAsync(Connection connection, CancellationToken ct)
    {
        foreach (var path in new[] { "/v1/vpn/status", "/v1/openvpn/status" })
        {
            try
            {
                using var doc = await GetAsync(connection, path, ct);
                if (Text(doc.RootElement, "status") is { Length: > 0 } status)
                    return status;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Try the older path.
            }
        }

        throw new InvalidOperationException(
            "Neither /v1/vpn/status nor /v1/openvpn/status answered. Is that Gluetun's control server?");
    }

    private async Task<int> ForwardedPortAsync(Connection connection, CancellationToken ct)
    {
        // The route is under /v1/openvpn/ even on a Wireguard tunnel, which reads like a
        // mistake and is not one. The bare path is tried second for older builds.
        foreach (var path in new[] { "/v1/openvpn/portforwarded", "/v1/portforwarded" })
        {
            try
            {
                using var doc = await GetAsync(connection, path, ct);
                if (doc.RootElement.TryGetProperty("port", out var port) && port.ValueKind == JsonValueKind.Number)
                    return port.GetInt32();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A provider without port forwarding answers with an error rather than a
                // zero, and that is a fact about the provider rather than a failed probe.
            }
        }

        return 0;
    }

    private static string Explain(Exception ex, Connection connection) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } =>
            "Gluetun rejected the request, so the control server is reachable but wants authentication. " +
            "Gluetun 3.40 and later configure this in /gluetun/auth/config.toml — give a role the routes " +
            "GET /v1/vpn/status, GET /v1/publicip/ip and GET /v1/openvpn/portforwarded, then either set " +
            "auth = \"none\" for them or paste the API key above.",
        _ => ProbeError.Describe(ex, connection.Settings.Get("url")),
    };

    private async Task<JsonDocument> GetAsync(Connection connection, string path, CancellationToken ct)
    {
        var baseUrl = connection.Settings.Get("url").TrimEnd('/');
        if (baseUrl.Length == 0)
            throw new InvalidOperationException("No control server URL configured.");

        var http = httpFactory.CreateClient(ProviderHttp.ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);

        if (connection.Settings.Get("api_key") is { Length: > 0 } key)
        {
            // Both, because Gluetun's own documentation uses X-API-Key while some builds
            // accept a bearer token, and sending the wrong one of the two looks exactly
            // like a wrong key. Neither header does harm when it is the unused one.
            request.Headers.TryAddWithoutValidation("X-API-Key", key);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
        }

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
