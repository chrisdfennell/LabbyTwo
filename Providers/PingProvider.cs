using System.Net.NetworkInformation;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>ICMP ping — for boxes with no web UI at all (a switch, a printer, the gateway).</summary>
public sealed class PingProvider : IConnectionProvider
{
    public string Type => "ping";
    public string DisplayName => "Ping host";
    public string Icon => "📡";
    public string Category => "General";
    public string Description => "ICMP ping. For anything that answers a ping but has no web interface worth checking.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("host", "Hostname or IP", FieldKind.Text, "192.168.1.1", Required: true),
        new("timeout", "Timeout (seconds)", FieldKind.Number, Default: "4"),
    ];

    public IReadOnlyList<MetricSpec> Metrics => [new("rtt_ms", "Round-trip time", " ms", 1)];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Slow to answer", "rtt_ms", Comparison.Above, 150, ClearThreshold: 80, ForMinutes: 10,
            Why: "Still up, but something is wrong: a saturated line, a failing wireless link, or a " +
                 "host too busy to reply. Ten minutes, so one bad packet is not an alert."),
    ];

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var host = connection.Settings.Get("host");
        if (string.IsNullOrWhiteSpace(host))
            return ProbeResult.Down(TimeSpan.Zero, "No host configured.");

        var timeout = Math.Clamp(connection.Settings.GetInt("timeout", 4), 1, 30) * 1000;
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, timeout);
            var elapsed = TimeSpan.FromMilliseconds(reply.RoundtripTime);
            if (reply.Status != IPStatus.Success)
                return ProbeResult.Down(elapsed, $"Ping {reply.Status}.");
            return ProbeResult.Up(elapsed, $"{reply.RoundtripTime} ms",
                new Dictionary<string, double> { ["rtt_ms"] = reply.RoundtripTime });
        }
        catch (Exception ex)
        {
            return ProbeResult.Down(TimeSpan.Zero, ex.GetBaseException().Message);
        }
    }
}
