using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LabbyTwo.Core;

namespace LabbyTwo.WakePlugin;

/// <summary>
/// A machine you would rather leave asleep. The desktop, the backup box, the games rig that
/// draws ninety watts doing nothing — things that should be off most of the time and on
/// within a minute of being wanted.
///
/// LabbyTwo could already send a magic packet, but only as a button hidden inside the QNAP
/// provider, so it worked for exactly one NAS and nothing else on the network. This is the
/// same packet with nothing else attached to it.
/// </summary>
public sealed class WakeProvider : IConnectionProvider
{
    public const string ProviderType = "wake";

    public string Type => ProviderType;
    public string DisplayName => "Wake on LAN";
    public string Icon => "⏰";
    public string Category => "Network";

    public string Description =>
        "Wakes a machine with a magic packet, and tells you whether it is awake. " +
        "Enable Wake on LAN in its BIOS and its network adapter first — nothing here can turn that on for it.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("mac", "MAC address", FieldKind.Text, "a8:a1:59:2f:c4:1e", Required: true,
            Help: "The wired adapter's. Wake on LAN over wi-fi needs hardware support that most laptops do not have."),

        new("host", "Address to check", FieldKind.Text, "192.168.86.40",
            Help: "Pinged each sweep to tell awake from asleep. Leave it blank and this connection becomes " +
                  "a button with no reading behind it, which still works — you just cannot chart it."),

        new("broadcast", "Broadcast address", FieldKind.Text, Default: "255.255.255.255",
            Help: "The packet is broadcast rather than addressed, because a sleeping machine has no address to " +
                  "send it to. If LabbyTwo is in a container on its own bridge network, the switch never sees " +
                  "255.255.255.255 — put your subnet's own broadcast here (192.168.86.255) and give the " +
                  "container host networking.")
        {
            Advanced = true,
        },

        new("port", "Port", FieldKind.Number, Default: "9",
            Help: "9 by convention, and 7 also works. Nothing listens on either — the adapter's firmware " +
                  "is watching the wire, not a socket.")
        {
            Advanced = true,
        },

        new("timeout", "Ping timeout (seconds)", FieldKind.Number, Default: "2") { Advanced = true },

        new("wake_at", "Wake at", FieldKind.Text, "02:00",
            Help: "Local time, 24-hour. Leave blank for a machine you only ever wake by hand. " +
                  "This is how the backup box is awake before the backup starts and asleep the rest of the day.")
        {
            Advanced = true,
        },

        new("wake_days", "On these days", FieldKind.Text, "mon,tue,wed,thu,fri",
            Help: "Comma separated, any of mon tue wed thu fri sat sun. Blank means every day.")
        {
            Advanced = true,
        },
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("awake", "Awake"),
    ];

    /// <summary>
    /// Nothing is suggested. The obvious rule — alert when it is not awake — is wrong for
    /// every machine this provider exists to manage: being asleep is the point. A rule
    /// worth having here is about a specific machine on a specific evening, which is the
    /// user's to write and not the integration's to guess.
    /// </summary>
    public IReadOnlyList<SuggestedRule> SuggestedRules => [];

    public IReadOnlyList<ProviderAction> Actions =>
    [
        new("wake", "Wake", "⏰")
        {
            Description = "Broadcasts a magic packet.",

            // Nothing is lost by waking a machine that is already awake, and a button you
            // have to confirm is a button you stop using from a phone.
            Confirms = false,
        },
    ];

    /// <summary>
    /// Reports whether it is awake. It never reports <c>Down</c>, and that is the whole
    /// point of this method rather than an oversight: a machine that is asleep is doing
    /// exactly what you asked of it, and calling that "down" would put a red tile on the
    /// dashboard, a dip in the uptime percentage and a line on the status page for every
    /// night the desktop was off. Up means "LabbyTwo knows the answer"; the answer itself
    /// is the <c>awake</c> metric.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        if (MacAddress(connection) is null)
            return ProbeResult.Down(TimeSpan.Zero,
                $"“{connection.Settings.Get("mac")}” is not a MAC address — six pairs of hex digits are needed.");

        var host = connection.Settings.Get("host");
        if (host.Length == 0)
            return ProbeResult.Up(TimeSpan.Zero, "Ready to wake. No address configured, so there is nothing to check.");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var ping = new Ping();
            var timeout = Math.Clamp(connection.Settings.GetInt("timeout", 2), 1, 30);
            var reply = await ping.SendPingAsync(host, TimeSpan.FromSeconds(timeout), cancellationToken: ct);
            stopwatch.Stop();

            var awake = reply.Status == IPStatus.Success;
            return ProbeResult.Up(stopwatch.Elapsed, awake ? "Awake" : "Asleep",
                new Dictionary<string, double>
                {
                    ["awake"] = awake ? 1 : 0,
                    // Only when it answered. A round-trip time recorded as zero for every
                    // hour the machine was off is a chart that lies about how fast it is.
                    ["rtt_ms"] = awake ? reply.RoundtripTime : double.NaN,
                }.Where(pair => !double.IsNaN(pair.Value)).ToDictionary());
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            // A name that does not resolve is a configuration problem rather than a sleeping
            // machine, so this one really is Down — the reading is unavailable, not zero.
            return ProbeResult.Down(stopwatch.Elapsed, ex.GetBaseException().Message);
        }
    }

    public async Task<ActionResult> RunActionAsync(
        Connection connection, ProviderAction action, SettingsBag input, CancellationToken ct)
    {
        if (action.Id != "wake")
            return ActionResult.Failed($"Wake on LAN does not know how to run “{action.Id}”.");

        if (MacAddress(connection) is not { } mac)
            return ActionResult.Failed("No usable MAC address on this connection.");

        try
        {
            await SendAsync(mac, connection.Settings.Get("broadcast", "255.255.255.255"),
                connection.Settings.GetInt("port", 9), ct);
        }
        catch (Exception ex)
        {
            return ActionResult.Failed(ex.GetBaseException().Message);
        }

        // Deliberately not "it is awake". Nothing acknowledges a magic packet — the adapter
        // wakes the machine and never answers — so the only honest report is that the
        // packet left.
        return ActionResult.Done("Magic packet sent. Give it a minute to boot.");
    }

    /// <summary>
    /// Six 0xFF bytes then the MAC sixteen times, broadcast rather than addressed. There is
    /// nothing to address it to: the machine is asleep and holds no lease, so it is the
    /// switch that has to carry this, not IP.
    /// </summary>
    internal static async Task SendAsync(byte[] mac, string broadcast, int port, CancellationToken ct)
    {
        var packet = new byte[6 + 16 * 6];
        packet.AsSpan(0, 6).Fill(0xFF);
        for (var repeat = 0; repeat < 16; repeat++)
            mac.CopyTo(packet, 6 + repeat * 6);

        var address = IPAddress.TryParse(broadcast, out var parsed) ? parsed : IPAddress.Broadcast;

        using var client = new UdpClient { EnableBroadcast = true };
        await client.SendAsync(packet, new IPEndPoint(address, port is > 0 and < 65536 ? port : 9), ct);
    }

    /// <summary>
    /// The configured MAC as six bytes, or null if there is not a usable one. Colons, dashes,
    /// dots or nothing at all — people paste it from wherever it was shown to them, and a
    /// form that only accepts one of those spellings is a form that gets abandoned.
    /// </summary>
    internal static byte[]? MacAddress(Connection connection)
    {
        var digits = new string([.. connection.Settings.Get("mac").Where(Uri.IsHexDigit)]);
        if (digits.Length != 12)
            return null;

        var mac = new byte[6];
        for (var index = 0; index < 6; index++)
            mac[index] = Convert.ToByte(digits.Substring(index * 2, 2), 16);

        return mac;
    }
}
