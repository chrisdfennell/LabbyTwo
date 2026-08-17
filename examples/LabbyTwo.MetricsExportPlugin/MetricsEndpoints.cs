using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LabbyTwo.MetricsExportPlugin;

/// <summary>
/// The other direction. LabbyTwo has read Prometheus since the beginning and published
/// nothing, which meant anything you wanted to keep — a year of NAS temperatures, the
/// evening the tunnel kept dropping — had to be measured a second time by a second
/// exporter that duplicated the credentials and the polling.
///
/// This is the same numbers, in the format Prometheus already speaks. Every provider gets
/// it for free, including one that arrives in a plugin years from now, because nothing here
/// knows what any metric means.
/// </summary>
public sealed class MetricsEndpoints(
    ConfigStore config,
    HealthMonitor health,
    Registry registry) : IEndpointExtension
{
    public const string RouteKey = "metrics";

    public string Key => RouteKey;

    /// <summary>
    /// Prometheus does not log in. It can send a bearer token and nothing else, so the
    /// route has to answer without a session and the token has to be the whole of the
    /// authorisation — which is exactly the case this override exists for.
    /// </summary>
    public bool RequiresAuthorization => false;

    /// <summary>
    /// One route, at the group's own path. Mapping both "" and "/" looks like belt and
    /// braces for the scrape config somebody typed without the trailing slash, and is
    /// actually two registrations of the identical pattern — which routing reports as an
    /// ambiguous match and turns every scrape into a 500. The empty path already answers
    /// both spellings.
    /// </summary>
    public void Map(IEndpointRouteBuilder routes) => routes.MapGet("", ScrapeAsync);

    private async Task<IResult> ScrapeAsync(HttpContext context, CancellationToken ct)
    {
        var exporters = (await config.ConnectionsAsync(ct))
            .Where(c => c.Provider == MetricsExportProvider.ProviderType && c.Enabled)
            .ToList();

        if (exporters.Count == 0)
            return Results.Text("No enabled Prometheus export connection is configured.", "text/plain", statusCode: 503);

        var presented = TokenFrom(context);
        var exporter = exporters.FirstOrDefault(e => Matches(e.Settings.Get("token"), presented));
        if (exporter is null)
        {
            // 401 with the challenge header, not 404. A scrape that is failing because of
            // the token should say so in Prometheus' own error column rather than looking
            // like a URL somebody typed wrong.
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return Results.Text("Bad or missing scrape token.", "text/plain", statusCode: 401);
        }

        var prefix = Sanitise(exporter.Settings.Get("prefix", "labby"));
        var includeDisabled = exporter.Settings.GetBool("include_disabled");

        var connections = (await config.ConnectionsAsync(ct))
            .Where(c => c.Provider != MetricsExportProvider.ProviderType)
            .Where(c => includeDisabled || c.Enabled)
            .Where(c => registry.Provider(c.Provider)?.IsMonitored != false)
            .ToList();

        var now = DateTimeOffset.Now;
        var body = Render(connections, prefix, now);

        // The version parameter is what tells Prometheus this is the text exposition
        // format rather than something it should sniff.
        return Results.Text(body, "text/plain; version=0.0.4", Encoding.UTF8);
    }

    /// <summary>
    /// The whole exposition, built as one string. Everything is small — a few dozen
    /// connections with a handful of metrics each — so streaming it would be complexity
    /// bought with nothing.
    /// </summary>
    private string Render(IReadOnlyList<Connection> connections, string prefix, DateTimeOffset now)
    {
        // Grouped by metric name, because the format requires it: every sample of a series
        // has to sit together under a single HELP and TYPE, and interleaving them is the
        // one way to make Prometheus reject an otherwise valid scrape.
        var families = new Dictionary<string, Family>(StringComparer.Ordinal);

        Family FamilyFor(string name, string help) =>
            families.TryGetValue(name, out var existing)
                ? existing
                : families[name] = new Family(name, help);

        foreach (var connection in connections)
        {
            var state = health.State(connection.Id);
            var labels = Labels(connection);

            // The monitor's verdict, not the last probe's. Those are not the same thing:
            // LabbyTwo tolerates FailuresBeforeDown failures in a row before it calls
            // something down, so a single dropped packet is still a 1 here. Exporting the
            // raw probe instead would give Prometheus a different opinion from the tile
            // next to it, and two dashboards disagreeing about whether the NAS is up is
            // worse than either answer on its own.
            FamilyFor($"{prefix}_up",
                    "1 when LabbyTwo considers this connection up, 0 when down. "
                    + "Absent until it has been probed once, and tolerant of the first failures the way the dashboard is.")
                .Add(labels, state?.IsUp switch { true => 1, false => 0, null => double.NaN });

            if (state is null)
                continue;

            FamilyFor($"{prefix}_probe_duration_seconds", "How long the last probe took.")
                .Add(labels, state.Duration.TotalSeconds);

            FamilyFor($"{prefix}_probe_age_seconds", "How long ago the last probe ran.")
                .Add(labels, (now - state.At).TotalSeconds);

            FamilyFor($"{prefix}_consecutive_failures", "Failed probes in a row, back to zero on the first success.")
                .Add(labels, state.ConsecutiveFailures);

            foreach (var (key, value) in state.Metrics)
            {
                var spec = registry.Metric(connection, key);
                var name = $"{prefix}_{Sanitise(key)}";

                // The provider's own label as the HELP text. It is written for a human
                // reading a tile, which is the same audience as a Grafana field picker.
                var help = spec.Unit is { Length: > 0 } unit
                    ? $"{spec.Label} ({unit.Trim()})"
                    : spec.Label;

                FamilyFor(name, help).Add(labels, value);
            }
        }

        var text = new StringBuilder();
        foreach (var family in families.Values.OrderBy(f => f.Name, StringComparer.Ordinal))
            family.WriteTo(text);

        return text.ToString();
    }

    /// <summary>
    /// The labels every series carries. The id is in there as well as the name because a
    /// name is the thing most likely to be edited, and a rename should not silently start
    /// a new series and orphan a year of history.
    /// </summary>
    private string Labels(Connection connection) =>
        $"connection=\"{Escape(connection.Name)}\","
        + $"provider=\"{Escape(connection.Provider)}\","
        + $"id=\"{Escape(connection.Id)}\"";

    private sealed class Family(string name, string help)
    {
        private readonly List<(string Labels, double Value)> _samples = [];

        public string Name => name;

        public void Add(string labels, double value) => _samples.Add((labels, value));

        public void WriteTo(StringBuilder text)
        {
            // NaN is how the loop above says "no reading yet". Prometheus accepts NaN as a
            // value, but a NaN in a graph is worse than an absent point, so drop them —
            // and drop the whole family if that leaves nothing, rather than emitting a
            // HELP for a series with no samples.
            var real = _samples.Where(s => !double.IsNaN(s.Value)).ToList();
            if (real.Count == 0)
                return;

            text.Append("# HELP ").Append(name).Append(' ').Append(EscapeHelp(help)).Append('\n');
            text.Append("# TYPE ").Append(name).Append(" gauge\n");

            foreach (var (labels, value) in real)
            {
                text.Append(name).Append('{').Append(labels).Append("} ")
                    // R, not the default: a temperature of 41.5 must not reach Prometheus
                    // as 41.5000000000001, and a culture with a decimal comma must not
                    // reach it at all.
                    .Append(value.ToString("R", CultureInfo.InvariantCulture))
                    .Append('\n');
            }
        }
    }

    /// <summary>
    /// Prometheus metric names are <c>[a-zA-Z_:][a-zA-Z0-9_:]*</c>. Connection names are
    /// whatever somebody typed, and a metric key from the JSON API provider is whatever was
    /// in the JSON — so anything outside that becomes an underscore rather than an
    /// unparseable scrape.
    /// </summary>
    private static string Sanitise(string raw)
    {
        if (raw.Length == 0)
            return "_";

        var clean = new StringBuilder(raw.Length);
        foreach (var character in raw)
            clean.Append(char.IsAsciiLetterOrDigit(character) || character is '_' or ':' ? character : '_');

        // A leading digit is legal in a label but not in a name.
        if (char.IsAsciiDigit(clean[0]))
            clean.Insert(0, '_');

        return clean.ToString();
    }

    /// <summary>Label values take a backslash, a quote and a newline; nothing else is special.</summary>
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

    /// <summary>HELP runs to the end of the line, so only a backslash and a newline matter.</summary>
    private static string EscapeHelp(string value) =>
        value.Replace("\\", "\\\\").Replace("\n", " ");

    /// <summary>
    /// Bearer header first, query string second. Prometheus can send either, but a token in
    /// a URL ends up in access logs and browser history, so the header is the one to reach
    /// for and the query is there for the curl that proves it works.
    /// </summary>
    private static string TokenFrom(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return header["Bearer ".Length..].Trim();

        return context.Request.Query["token"].ToString();
    }

    /// <summary>
    /// Fixed-time, because a token compared with <c>==</c> leaks its length and then its
    /// prefix to anyone patient enough to time the answers. The endpoint is deliberately
    /// reachable without a login, which is exactly when that stops being theoretical.
    /// </summary>
    private static bool Matches(string expected, string presented) =>
        expected.Length > 0
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(presented));
}
