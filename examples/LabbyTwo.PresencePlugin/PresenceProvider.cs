using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using LabbyTwo.Core;

namespace LabbyTwo.PresencePlugin;

/// <summary>
/// Who is home. Pings a list of devices on every sweep and reports each one as its own
/// metric, so "was anyone in on Tuesday afternoon" becomes a chart rather than a guess.
///
/// This is the example of a provider whose metrics are decided by the user rather than the
/// code. <see cref="MetricsFor"/> reads the configured list, which is what puts each
/// device in the chart and alert pickers by name instead of leaving people to type
/// <c>home_kitchen_tablet</c> from memory.
/// </summary>
public sealed class PresenceProvider : IConnectionProvider
{
    public string Type => "presence";
    public string DisplayName => "Who's home";
    public string Icon => "🏠";
    public string Category => "Network";
    public string Description =>
        "Pings a list of devices and reports which are on the network, with history for each one.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("devices", "Devices", FieldKind.Textarea, "Chris = 192.168.1.31\nTV = 192.168.1.40", Required: true,
            Help: "One per line, as \"Name = address\". The address can be a hostname if this container can " +
                  "resolve it, but a phone is usually a reserved IP on your router."),

        new("timeout", "Timeout (seconds)", FieldKind.Number, Default: "3",
            Help: "Phones sleep their radios, so allow a little longer than you would for a server."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("devices_home", "Devices home"),
        new("devices_total", "Devices watched"),
    ];

    /// <summary>
    /// The metrics this *particular* connection reports, which only its settings can say.
    /// The base list is the same for everyone; the per-device ones are not.
    /// </summary>
    public IReadOnlyList<MetricSpec> MetricsFor(Connection connection) =>
    [
        .. Metrics,
        .. Parse(connection.Settings.Get("devices")).Select(d => new MetricSpec(d.Key, d.Name)),
    ];

    public sealed record Device(string Name, string Address)
    {
        /// <summary>
        /// A metric key has to survive being a column heading and a chart setting, so the
        /// display name is flattened: "Chris's iPhone" becomes "home_chris_s_iphone".
        /// </summary>
        public string Key
        {
            get
            {
                var builder = new StringBuilder("home_");
                foreach (var character in Name.ToLowerInvariant())
                    builder.Append(char.IsLetterOrDigit(character) ? character : '_');
                return builder.ToString().TrimEnd('_');
            }
        }
    }

    /// <summary>
    /// Shared with the widget so both agree on what the list means — including the
    /// blank-line and comment handling, which is the kind of thing that silently diverges
    /// when two places parse the same text.
    /// </summary>
    public static IReadOnlyList<Device> Parse(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return [];

        var devices = new List<Device>();
        foreach (var raw in configured.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            // "Name = address", but a bare address alone is a reasonable thing to type.
            var split = line.IndexOf('=');
            var name = split < 0 ? line : line[..split].Trim();
            var address = split < 0 ? line : line[(split + 1)..].Trim();

            if (address.Length > 0)
                devices.Add(new Device(name.Length > 0 ? name : address, address));
        }

        return devices;
    }

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var devices = Parse(connection.Settings.Get("devices"));
        if (devices.Count == 0)
            return ProbeResult.Down(TimeSpan.Zero, "No devices listed.");

        var timeout = Math.Clamp(connection.Settings.GetInt("timeout", 3), 1, 30) * 1000;
        var stopwatch = Stopwatch.StartNew();

        // All at once. In series, twelve sleeping phones at three seconds each would take
        // longer than the probe interval and the sweep would never finish.
        var results = await Task.WhenAll(devices.Select(async device =>
        {
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(device.Address, timeout);
                return (device, home: reply.Status == IPStatus.Success);
            }
            catch
            {
                // An unresolvable name is "not here" as far as this is concerned.
                return (device, home: false);
            }
        }));

        stopwatch.Stop();

        var metrics = new Dictionary<string, double>
        {
            ["devices_total"] = devices.Count,
            ["devices_home"] = results.Count(r => r.home),
            ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds,
        };

        foreach (var (device, home) in results)
            metrics[device.Key] = home ? 1 : 0;

        var here = results.Where(r => r.home).Select(r => r.device.Name).ToList();

        // Up means the check ran. Nobody being home is an answer, not a failure — treating
        // it as one would make the uptime figure mean "somebody was in", and would fire a
        // down-alert every time the house is empty.
        return ProbeResult.Up(
            stopwatch.Elapsed,
            here.Count == 0
                ? "Nobody home"
                : here.Count <= 3
                    ? $"{string.Join(", ", here)} home"
                    : $"{here.Count} of {devices.Count} home",
            metrics);
    }
}
