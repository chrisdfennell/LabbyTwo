using System.Diagnostics;
using LabbyTwo.Core;

namespace LabbyTwo.WireGuardPlugin;

/// <summary>
/// WireGuard peers: who is connected, and how long since each one was heard from.
///
/// Tailscale and Gluetun are already covered; plain WireGuard was not, and it is what most
/// people actually run to get back into their own house. The reading that matters is the
/// last handshake — WireGuard is silent by design, so a peer is not "down", it has simply
/// not said anything for eleven minutes, and eleven minutes means the tunnel is dead
/// because a live one rekeys every two.
///
/// <para><b>How it reads them.</b> WireGuard has no API and no socket; the only interface
/// is the <c>wg</c> binary, which is not in LabbyTwo's container and should not be. So this
/// reads the output of <c>wg show all dump</c> from a file, which a one-line cron on the
/// host writes:</para>
///
/// <code>
/// * * * * * /usr/bin/wg show all dump &gt; /srv/labbytwo/wg-dump 2&gt;/dev/null
/// </code>
///
/// <para>Then mount that file into the container read-only. It keeps the privileged half on
/// the host where it belongs — nothing here runs a command, and a plugin that shelled out
/// as root to read a status page would be a poor trade for saving a cron line.</para>
/// </summary>
public sealed class WireGuardProvider : IConnectionProvider
{
    public const string ProviderType = "wireguard";

    public string Type => ProviderType;
    public string DisplayName => "WireGuard";
    public string Icon => "🔐";
    public string Category => "Network";

    public string Description =>
        "Peers on a WireGuard interface and how long since each was heard from, read from a "
        + "`wg show all dump` file written by the host.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("dump_path", "Dump file", FieldKind.Text, "/host/wg-dump", Required: true,
            Default: "/host/wg-dump",
            Help: "The path *inside LabbyTwo's container*. Write it on the host with "
                  + "`wg show all dump > /srv/labbytwo/wg-dump` from cron, and mount that file in read-only."),

        new("stale_minutes", "Call a peer offline after (minutes)", FieldKind.Number, Default: "5",
            Help: "A live tunnel rekeys about every two minutes, so five is generous. Raise it for peers "
                  + "that are phones — they go quiet when the screen is off and are not really gone."),

        new("names", "Name the peers", FieldKind.Textarea,
            "aBcD1234...= Chris's laptop\nEfGh5678...= Phone",
            Help: "One per line: the peer's public key, a space, then what to call it. Optional, and worth "
                  + "doing — without it every chart and alert is labelled with 44 characters of base64.")
        {
            Advanced = true,
        },
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("peers", "Peers configured"),
        new("peers_online", "Peers online"),
        new("rx_mb", "Received", " MB", 1),
        new("tx_mb", "Sent", " MB", 1),
    ];

    /// <summary>
    /// A metric per peer, named from the settings. The same pattern as the presence plugin,
    /// and for the same reason: which peers exist is the user's fact, not the code's, so
    /// the chart and alert pickers can only know them by reading the configuration.
    /// </summary>
    public IReadOnlyList<MetricSpec> MetricsFor(Connection connection) =>
    [
        .. Metrics,
        .. Names(connection.Settings.Get("names")).Values
            .Distinct()
            .Select(name => new MetricSpec(PeerKey(name), $"{name} — minutes since handshake", " min")),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Every peer has gone", "peers_online", Comparison.Below, 1, ForMinutes: 15,
            Why: "Nothing has been connected for a quarter of an hour. On a tunnel somebody uses daily this "
                 + "usually means the port forward moved or the dynamic address changed, not that everyone is out."),
    ];

    public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var path = connection.Settings.Get("dump_path", "/host/wg-dump");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (!File.Exists(path))
                return Task.FromResult(ProbeResult.Down(stopwatch.Elapsed,
                    $"No file at {path}. The host writes it with `wg show all dump`; check the mount and the cron job."));

            var text = File.ReadAllText(path);
            var written = File.GetLastWriteTime(path);
            stopwatch.Stop();

            var peers = Parse(text);
            var names = Names(connection.Settings.Get("names"));
            var stale = TimeSpan.FromMinutes(Math.Clamp(connection.Settings.GetInt("stale_minutes", 5), 1, 1440));
            var now = DateTimeOffset.Now;

            var metrics = new Dictionary<string, double>
            {
                ["peers"] = peers.Count,
                ["peers_online"] = peers.Count(p => p.Handshake is { } h && now - h < stale),
                ["rx_mb"] = peers.Sum(p => p.Received) / 1_000_000d,
                ["tx_mb"] = peers.Sum(p => p.Sent) / 1_000_000d,
            };

            foreach (var peer in peers)
            {
                if (!names.TryGetValue(peer.PublicKey, out var name))
                    continue;

                // Left out rather than reported as a huge number for a peer that has never
                // connected: "never" and "a very long time ago" are different facts, and
                // only one of them should move a chart.
                if (peer.Handshake is { } handshake)
                    metrics[PeerKey(name)] = (now - handshake).TotalMinutes;
            }

            // The dump is only as fresh as the cron that writes it. A stopped cron would
            // otherwise look exactly like every peer going offline at once, which is the
            // kind of false alarm that gets a dashboard ignored.
            var age = DateTimeOffset.Now - written;
            if (age > TimeSpan.FromMinutes(10))
                return Task.FromResult(ProbeResult.Down(stopwatch.Elapsed,
                    $"{path} was last written {age.TotalMinutes:0} minutes ago. The cron job writing it has stopped, "
                    + "so these readings are stale rather than the peers being gone."));

            var online = (int)metrics["peers_online"];
            return Task.FromResult(ProbeResult.Up(stopwatch.Elapsed,
                peers.Count == 0 ? "No peers configured." : $"{online} of {peers.Count} peers online",
                metrics));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return Task.FromResult(ProbeResult.Down(stopwatch.Elapsed, ex.GetBaseException().Message));
        }
    }

    internal sealed record Peer(string PublicKey, DateTimeOffset? Handshake, long Received, long Sent);

    /// <summary>
    /// <c>wg show all dump</c> is tab-separated with no header. The first line of each
    /// interface is the interface itself — four fields — and every line after it is a peer,
    /// with eight or nine depending on the version:
    /// <c>interface, public key, preshared key, endpoint, allowed ips, latest handshake,
    /// rx, tx, keepalive</c>. Counting fields is how the two are told apart, because the
    /// format has no other marker.
    /// </summary>
    internal static IReadOnlyList<Peer> Parse(string dump)
    {
        var peers = new List<Peer>();

        foreach (var line in dump.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split('\t');
            if (fields.Length < 8)
                continue; // the interface line, or something truncated

            var handshake = long.TryParse(fields[5], out var seconds) && seconds > 0
                ? DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime()
                : (DateTimeOffset?)null;

            peers.Add(new Peer(
                fields[1],
                handshake,
                long.TryParse(fields[6], out var rx) ? rx : 0,
                long.TryParse(fields[7], out var tx) ? tx : 0));
        }

        return peers;
    }

    /// <summary>
    /// "key name" per line. Split on the first run of whitespace, because that is the one
    /// separator a WireGuard key cannot contain — base64 keys end in an '=' pad, so
    /// splitting on '=' would cut a key in half or swallow the separator depending on which
    /// end you took it from. The name is everything after, spaces and all, so "Chris's
    /// laptop" needs no quoting.
    /// </summary>
    internal static Dictionary<string, string> Names(string raw)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var split = line.IndexOfAny([' ', '\t']);
            if (split <= 0)
                continue;

            var key = line[..split].Trim();

            // An '=' between the two is how people write a mapping, and it is not part of
            // either side. Dropping it costs nothing and accepts both spellings.
            var name = line[(split + 1)..].TrimStart().TrimStart('=').Trim();

            if (key.Length > 0 && name.Length > 0)
                names[key] = name;
        }

        return names;
    }

    /// <summary>A metric key has to survive being a chart setting, so a name is flattened.</summary>
    internal static string PeerKey(string name)
    {
        var key = new string([.. name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')]);
        return $"peer_{key.Trim('_')}_minutes";
    }
}
