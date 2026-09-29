using System.Collections.Concurrent;
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
///
/// This one asks Cloudflare's API, so it sees every tunnel on the account from anywhere.
/// <see cref="CloudflaredProvider"/> asks the cloudflared container itself and needs no
/// token; the two report the same metric names where they measure the same thing, so a
/// card or a rule written for one works for the other.
/// </summary>
public sealed class CloudflareProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    public string Type => "cloudflare";
    public string DisplayName => "Cloudflare Tunnel";
    public string Icon => "🌩️";
    public string Category => "Network";
    public string Description =>
        "Whether your tunnels are up, how many connectors each has and which Cloudflare data centres they reach. " +
        "Catches the outage nobody on the LAN can see.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("account_id", "Account ID", FieldKind.Text, Required: true,
            Help: "Cloudflare dashboard → Workers & Pages → the ID in the right-hand column, or the one in your URL."),

        new("api_token", "API token", FieldKind.Password, Required: true,
            Help: "My Profile → API Tokens → Create Token. It needs Account → Cloudflare Tunnel → Read and nothing else. " +
                  "Not the Global API Key — that one can do everything to everything. " +
                  "Would rather not make a token? Add \"cloudflared (local)\" instead, which asks the container."),

        new("tunnel", "Tunnel name", FieldKind.Text,
            Help: "Optional. Blank watches every tunnel on the account and reports the worst of them."),
    ];

    /// <summary>
    /// The one metric recorded per tunnel as well as in total, as
    /// <c>connections:&lt;tunnel&gt;</c>. The suffix is the same convention a NAS uses for
    /// its volumes, so the label and unit carry over without being declared again.
    /// </summary>
    private static readonly MetricSpec Connections = new("connections", "Active connectors");

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("tunnels", "Tunnels"),
        new("tunnels_healthy", "Tunnels healthy"),
        new("tunnels_down", "Tunnels down"),
        Connections,
        new("fewest_connections", "Fewest connectors on a tunnel"),
        new("edge_locations", "Edge locations"),
        new("reconnecting", "Connectors reconnecting"),
        new("connected_hours", "Since last reconnect", " h", 1),
        new("latency_ms", "Response time", " ms"),
    ];

    /// <summary>
    /// Tunnel names seen on each connection's last probe, so the widget editor's metric
    /// dropdown can offer "home connectors" rather than leaving someone to guess the key.
    /// </summary>
    private readonly ConcurrentDictionary<string, IReadOnlyList<(string Key, string Name)>> _tunnels = new();

    public IReadOnlyList<MetricSpec> MetricsFor(Connection connection) =>
        _tunnels.TryGetValue(connection.Id, out var tunnels)
            ? [.. Metrics, .. tunnels.Select(t => Connections with { Key = t.Key, Label = $"{t.Name} connectors" })]
            : Metrics;

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("A tunnel is down", "tunnels_down", Comparison.Above, 0, ForMinutes: 5,
            Why: "Everything still works from inside the house, which is what makes this one worth being told about."),

        // On the fewest rather than the total: with two tunnels, four connectors on one and
        // one on the other add up to five, and a rule on the sum would never notice.
        new("Down to one connector", "fewest_connections", Comparison.Below, 2, ForMinutes: 15,
            Why: "cloudflared normally holds four. One left means the next blip is an outage."),
    ];

    /// <summary>
    /// One of a tunnel's connections to Cloudflare's edge, as the API lists it. A connector
    /// (one cloudflared process) normally holds four, spread over two data centres.
    /// </summary>
    public sealed record EdgeConnection(string Colo, string Version, DateTimeOffset? OpenedAt, bool PendingReconnect, string ClientId);

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

            var now = DateTimeOffset.UtcNow;
            var wanted = connection.Settings.Get("tunnel");
            int total = 0, healthy = 0, down = 0, connectors = 0, reconnecting = 0;
            int? fewest = null;
            var unhealthy = new List<string>();
            var everyConnection = new List<EdgeConnection>();
            var metrics = new Dictionary<string, double>();
            var details = new Dictionary<string, string>();
            var perTunnel = new List<(string Name, int Count)>();

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

                var live = ReadConnections(tunnel);
                connectors += live.Count;
                reconnecting += live.Count(c => c.PendingReconnect);
                fewest = Math.Min(fewest ?? int.MaxValue, live.Count);
                everyConnection.AddRange(live);

                // The id survives a rename in the Cloudflare dashboard, but "connections:3f2a…"
                // means nothing in a rule. Tunnel names are unique per account, and renaming
                // one is rare enough that a fresh series is the right price for a readable key.
                if (name.Length > 0)
                {
                    perTunnel.Add((name, live.Count));
                    if (details.Count < MaxTunnelsDescribed)
                        details[name] = DescribeTunnel(status, live, now);
                }
            }

            if (total == 0)
                return ProbeResult.Down(stopwatch.Elapsed,
                    wanted.Length > 0 ? $"No tunnel called \"{wanted}\"." : "This account has no tunnels.");

            metrics["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds;
            metrics["tunnels"] = total;
            metrics["tunnels_healthy"] = healthy;
            metrics["tunnels_down"] = down;
            metrics["connections"] = connectors;
            metrics["fewest_connections"] = fewest ?? 0;
            metrics["edge_locations"] = CloudflareEdge.Count(everyConnection.Select(c => c.Colo));
            metrics["reconnecting"] = reconnecting;

            if (Newest(everyConnection) is { } newest)
                metrics["connected_hours"] = Math.Max(0, (now - newest).TotalHours);

            // Only worth a series each when there is more than one — with a single tunnel,
            // "connections:home" would be the total recorded twice.
            if (perTunnel.Count > 1)
            {
                var keyed = new List<(string, string)>();
                foreach (var (name, count) in perTunnel.Take(VolumeMetric.MaxPerConnection))
                {
                    var key = VolumeMetric.KeyFor(Connections.Key, name);
                    if (metrics.TryAdd(key, count))
                        keyed.Add((key, name));
                }
                _tunnels[connection.Id] = keyed;
            }
            else
            {
                _tunnels.TryRemove(connection.Id, out _);
            }

            var message = down > 0
                ? $"{down} not healthy: {string.Join(", ", unhealthy.Take(3))}"
                : $"{healthy} healthy, {connectors} connector{(connectors == 1 ? "" : "s")}" +
                  (CloudflareEdge.Summarise(everyConnection.Select(c => c.Colo)) is { Length: > 0 } where ? $" via {where}" : "");

            return ProbeResult.Up(stopwatch.Elapsed, message, metrics, details);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ProbeError.Describe(ex, "api.cloudflare.com"));
        }
    }

    /// <summary>
    /// Enough to read on a card. An account with dozens of tunnels is a business rather
    /// than a home lab, and a card listing all of them would be a page.
    /// </summary>
    private const int MaxTunnelsDescribed = 8;

    /// <summary>
    /// The <c>connections</c> array on one tunnel. Anything missing is left blank rather than
    /// failing the probe — a field Cloudflare stops sending should cost a detail, not the tunnel.
    /// </summary>
    public static IReadOnlyList<EdgeConnection> ReadConnections(JsonElement tunnel)
    {
        if (!tunnel.TryGetProperty("connections", out var live) || live.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<EdgeConnection>();
        foreach (var item in live.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            DateTimeOffset? opened = Text(item, "opened_at") is { Length: > 0 } stamp
                && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                    ? parsed
                    : null;

            var pending = item.TryGetProperty("is_pending_reconnect", out var flag) && flag.ValueKind == JsonValueKind.True;
            result.Add(new EdgeConnection(Text(item, "colo_name"), Text(item, "client_version"), opened, pending, Text(item, "client_id")));
        }
        return result;
    }

    /// <summary>
    /// One tunnel in a line: "healthy · 4 connectors · ATL ×2, DFW ×2 · cloudflared
    /// 2025.1.0 · connected 3d 4h ago". The time is since the <em>newest</em> connection
    /// opened, because that is the last time anything about the tunnel changed — four
    /// connections all three days old is a steady tunnel, and one of them ten minutes old
    /// is a tunnel that just blinked.
    /// </summary>
    public static string DescribeTunnel(string status, IReadOnlyList<EdgeConnection> connections, DateTimeOffset now)
    {
        var parts = new List<string>();
        if (status.Length > 0)
            parts.Add(status);

        parts.Add($"{connections.Count} connector{(connections.Count == 1 ? "" : "s")}");

        // Each cloudflared process has its own client id; more than one means replicas,
        // which is worth saying because it changes what "four connections" should be.
        var replicas = connections.Select(c => c.ClientId).Where(id => id.Length > 0).Distinct().Count();
        if (replicas > 1)
            parts.Add($"{replicas} replicas");

        if (CloudflareEdge.Summarise(connections.Select(c => c.Colo)) is { Length: > 0 } where)
            parts.Add(where);

        if (CloudflareEdge.Versions(connections.Select(c => c.Version)) is { Length: > 0 } version)
            parts.Add($"cloudflared {version}");

        var reconnecting = connections.Count(c => c.PendingReconnect);
        if (reconnecting > 0)
            parts.Add($"{reconnecting} reconnecting");

        if (Newest(connections) is { } newest)
            parts.Add($"connected {Ago.Since(newest, now)}");

        return string.Join(" · ", parts);
    }

    /// <summary>When the most recent of these connections opened, if any of them said.</summary>
    private static DateTimeOffset? Newest(IEnumerable<EdgeConnection> connections) =>
        connections.Select(c => c.OpenedAt).Where(at => at is not null).Max();

    private static string Text(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

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

/// <summary>
/// Cloudflare's data centres, as both the API and cloudflared name them. The API says
/// "DFW"; cloudflared's metrics say "dfw08", the eighth machine room in Dallas. What a
/// person wants to know is the city — whether the tunnel has a leg in two places, so
/// losing one is not an outage — so both are read down to the airport code.
/// </summary>
public static class CloudflareEdge
{
    /// <summary>"dfw08" and "DFW" both become "DFW". Blank for blank.</summary>
    public static string Colo(string? raw)
    {
        var trimmed = (raw ?? "").Trim();
        var letters = new string([.. trimmed.TakeWhile(char.IsAsciiLetter)]);
        return (letters.Length > 0 ? letters : trimmed).ToUpperInvariant();
    }

    /// <summary>How many different data centres, which is the redundancy that matters.</summary>
    public static int Count(IEnumerable<string?> raw) =>
        raw.Select(Colo).Where(c => c.Length > 0).Distinct().Count();

    /// <summary>
    /// "ATL ×2, DFW ×2" — each data centre once, with how many connections land there when
    /// it is more than one. Alphabetical, so the text does not reshuffle every probe.
    /// </summary>
    public static string Summarise(IEnumerable<string?> raw) => string.Join(", ",
        raw.Select(Colo)
            .Where(c => c.Length > 0)
            .GroupBy(c => c)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key));

    /// <summary>
    /// The cloudflared versions in play. Usually one; two means a replica was left behind
    /// on an old image, which is worth seeing.
    /// </summary>
    public static string Versions(IEnumerable<string?> raw) => string.Join(", ",
        raw.Select(v => (v ?? "").Trim()).Where(v => v.Length > 0).Distinct().Order(StringComparer.Ordinal));
}
