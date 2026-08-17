using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.LanScanPlugin;

/// <summary>
/// What is on the network, and — the part worth having on a dashboard — what was not there
/// last time.
///
/// A scan you look at is a table you read once. A scan that *remembers* turns into
/// "something appeared at 3am", which is an alert you can leave switched on: devices_new is
/// an ordinary metric, so a threshold rule of "above 0" needs no new alerting machinery.
///
/// **What it can see depends on where LabbyTwo runs.** From a bridged container it can route
/// to the LAN, so ping, reverse DNS and open ports all work. It is not on the same broadcast
/// domain, so ARP does not reach it and there are no MAC addresses — which is where a
/// desktop scanner gets vendor names. Host networking or a macvlan puts it on the LAN's own
/// segment and those appear; see <see cref="Arp"/>. Nothing is switched on for it: the
/// addresses are read if they are there and the column is empty if they are not, because a
/// setting for this would be a setting nobody could answer without knowing how their own
/// container is attached.
/// </summary>
public sealed class LanScanProvider(AppSettingsStore settings) : IConnectionProvider
{
    public string Type => "lanscan";
    public string DisplayName => "Network scan";
    public string Icon => "🛰️";
    public string Category => "Network";

    public string Description =>
        "Sweeps a subnet and reports what answered, what is new since last time, and what has gone.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("subnet", "Subnet", FieldKind.Text, "192.168.1.0/24", Required: true,
            Help: "In CIDR, or a single address. Swept from wherever LabbyTwo runs — inside a container "
                + $"that is the LAN it can route to. At most {Subnet.MaxAddresses:N0} addresses."),

        new("ports", "Ports to check", FieldKind.Text, "22,80,443",
            Help: "Optional, comma separated. A device that answers on one of these is reported as up "
                + "even when it ignores ping, which is most of Windows and anything with a firewall."),

        new("timeout", "Timeout (ms)", FieldKind.Number, Default: "500",
            Help: "Per address. A LAN answers in single-figure milliseconds; this only has to be long "
                + "enough for something asleep.")
        { Advanced = true },

        new("names", "Look up names", FieldKind.Bool, Default: "true",
            Help: "Reverse DNS for anything that answered. Turn it off if your resolver is slow — it is "
                + "the slowest part of a scan by a distance.")
        { Advanced = true },

        new("netbios", "Ask devices their name", FieldKind.Bool, Default: "true",
            Help: "A NetBIOS query on UDP 137, which unlike ARP is routed and so works from a container "
                + "on a bridge. Most home routers register no reverse-DNS names at all, so this is "
                + "usually what puts names in the list. Windows also returns its hardware address here; "
                + "Samba does not."),

        new("labels", "Name them yourself", FieldKind.Textarea,
            "192.168.86.45 Kitchen tablet\n192.168.86.52 Chris's laptop",
            Help: "One per line: address, a space, then what to call it. Wins over anything discovered, "
                + "because you know which one is the printer and the network does not."),

        new("arp_source", "ARP table", FieldKind.Text, Arp.DefaultPath, Default: Arp.DefaultPath,
            Help: "Where to read hardware addresses from. The default is this container's own table, "
                + "which is empty on a Docker bridge — ARP does not cross a router, so the addresses "
                + "never reach it. To have them anyway without moving LabbyTwo onto the LAN, let "
                + "something already on it write the table out on a timer and point this at that file. "
                + "On the NAS: * * * * * /usr/sbin/arp -an > /share/Container/labbytwo/arp-table "
                + "then mount it in read-only. Both that format and /proc/net/arp are read.")
        { Advanced = true },
    ];

    /// <summary>
    /// Sweeping a subnet is not something to do every thirty seconds. Every address gets a
    /// packet, and a scanner that never stops is indistinguishable from one somebody should
    /// be worried about — including to your own router.
    /// </summary>
    public TimeSpan MinimumInterval => TimeSpan.FromMinutes(15);

    public TimeSpan MinimumIntervalFor(Connection connection) =>
        TimeSpan.FromMinutes(Math.Clamp(connection.Settings.GetInt("every", 15), 1, 24 * 60));

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("devices_up", "Devices answering"),
        new("devices_new", "Devices never seen before"),
        new("devices_missing", "Known devices not answering"),
        new("scan_seconds", "Time to sweep", " s", 1),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Something new on the network", "devices_new", Comparison.Above, 0,
            Why: "An address that has never answered before. On a home network that is a guest, a new "
               + "gadget, or something you did not put there — all three are worth a look."),
    ];

    /// <summary>
    /// A scan on demand. The schedule is deliberately slow, so the button is how you ask
    /// "what is on here *now*" without waiting a quarter of an hour for the answer.
    /// </summary>
    public IReadOnlyList<ProviderAction> Actions =>
    [
        new("scan", "Scan now", "🛰️")
        {
            Description = "Sweeps the subnet now rather than waiting for the schedule.",
            // Worst case is having to press it again — which is exactly the case the
            // confirmation exists to skip.
            Confirms = false,
        },
    ];

    public async Task<ActionResult> RunActionAsync(
        Connection connection, ProviderAction action, SettingsBag input, CancellationToken ct)
    {
        if (action.Id != "scan")
            return ActionResult.Failed($"{DisplayName} does not know how to run “{action.Id}”.");

        var probe = await ProbeAsync(connection, ct);
        return probe.Ok ? ActionResult.Done(probe.Message) : ActionResult.Failed(probe.Message);
    }

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var hosts = Subnet.Hosts(connection.Settings.Get("subnet"));
            var timeout = Math.Clamp(connection.Settings.GetInt("timeout", 500), 50, 10_000);
            var ports = Ports(connection.Settings.Get("ports"));
            var resolve = connection.Settings.GetBool("names", true);
            var netbios = connection.Settings.GetBool("netbios", true);

            // Bounded rather than all at once. A thousand simultaneous pings is a burst your
            // own switch may drop, which shows up as devices that "went missing" and did not.
            using var limit = new SemaphoreSlim(64);

            var found = await Task.WhenAll(hosts.Select(async address =>
            {
                await limit.WaitAsync(ct);
                try
                {
                    return await ProbeOneAsync(address, timeout, ports, resolve, netbios, ct);
                }
                finally
                {
                    limit.Release();
                }
            }));

            var up = found.Where(device => device is not null).Select(device => device!).ToList();

            // After the sweep, never before: pinging a host is what puts it in the kernel's
            // ARP cache, so there is nothing to read until the addresses have been probed.
            // A table fed in from the LAN is already complete, but reading it here costs
            // nothing and keeps one code path.
            var macs = Arp.Table(connection.Settings.Get("arp_source"));
            if (macs.Count > 0)
            {
                // ARP wins over NetBIOS where both answered: it is the address the machine
                // is actually using on the wire, whereas the NetBIOS one is whatever the
                // adapter chose to report about itself.
                up = [.. up.Select(device => macs.TryGetValue(device.Address, out var mac)
                    ? device with { Mac = mac, Vendor = Oui.Vendor(mac) }
                    : device)];
            }

            // Last, so a name somebody typed beats every one that was discovered. They know
            // which one is the printer; the network does not.
            var labels = Labels(connection.Settings.Get("labels"));
            if (labels.Count > 0)
            {
                up = [.. up.Select(device => labels.TryGetValue(device.Address, out var label)
                    ? device with { Name = label }
                    : device)];
            }

            stopwatch.Stop();

            var known = await KnownAsync(connection, ct);
            var seen = up.Select(device => device.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // "New" only means anything once something has been recorded. On the very first
            // scan every device is new, which would fire the suggested rule for the whole
            // network and teach somebody to switch it off.
            var first = known.Count == 0;
            var appeared = first ? [] : up.Where(device => !known.Contains(device.Address)).ToList();
            var missing = known.Where(address => !seen.Contains(address)).ToList();

            await RememberAsync(connection, known.Union(seen, StringComparer.OrdinalIgnoreCase), up, ct);

            var summary = $"{up.Count} device{(up.Count == 1 ? "" : "s")} of {hosts.Count} addresses";
            if (appeared.Count > 0)
                summary += $" · {appeared.Count} new: {string.Join(", ", appeared.Take(3).Select(d => d.Label))}";
            else if (first)
                summary += " · first scan, so none are counted as new";

            // Said once in the message rather than as a warning, because it is a fact about
            // the deployment rather than a fault. Now that NetBIOS can bring some addresses
            // back on its own, "none at all" and "some" are worth telling apart — the first
            // means the ARP source is not set up, the second means it is working and only
            // the machines that do not answer NetBIOS are missing.
            var withMac = up.Count(device => device.Mac.Length > 0);
            if (up.Count > 0 && withMac == 0)
                summary += " · no hardware addresses — see the ARP table setting";
            else if (withMac < up.Count)
                summary += $" · {withMac} of {up.Count} with a hardware address";

            return ProbeResult.Up(stopwatch.Elapsed, summary, new Dictionary<string, double>
            {
                ["devices_up"] = up.Count,
                ["devices_new"] = appeared.Count,
                ["devices_missing"] = missing.Count,
                ["scan_seconds"] = Math.Round(stopwatch.Elapsed.TotalSeconds, 1),
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ex.GetBaseException().Message);
        }
    }

    /// <param name="Label">The name if there is one, the address otherwise — what a row should read.</param>
    /// <param name="Mac">Empty wherever ARP cannot reach, which is any bridged container.</param>
    public sealed record Device(
        string Address,
        string Name,
        double Milliseconds,
        IReadOnlyList<int> OpenPorts,
        string Mac = "",
        string Vendor = "")
    {
        public string Label => Name.Length > 0 ? Name : Address;

        /// <summary>
        /// What to put under the name. The vendor when there is one, because "Espressif" is
        /// the fact that identifies a device you had forgotten owning; the address otherwise,
        /// since a row has to say *something* about which machine it is.
        /// </summary>
        public string Detail => Vendor.Length > 0 ? $"{Address} · {Vendor}" : Address;
    }

    private static async Task<Device?> ProbeOneAsync(
        IPAddress address, int timeout, IReadOnlyList<int> ports, bool resolve, bool netbios, CancellationToken ct)
    {
        var answered = false;
        double elapsed = 0;

        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, timeout);
            if (reply.Status == IPStatus.Success)
            {
                answered = true;
                elapsed = reply.RoundtripTime;
            }
        }
        catch (Exception)
        {
            // ICMP is often refused outright inside a container without NET_RAW. That is not
            // a reason to call the whole subnet empty — the port check below still works.
        }

        var open = new List<int>();
        foreach (var port in ports)
        {
            if (await OpenAsync(address, port, timeout, ct))
            {
                open.Add(port);
                answered = true;
            }
        }

        if (!answered)
            return null;

        var name = "";
        if (resolve)
        {
            try
            {
                // The slowest part of a scan by a distance, and worth its own deadline: a
                // resolver that black-holes reverse lookups would otherwise stall the sweep.
                // WaitAsync rather than a linked token, because the Dns overload taking an
                // IPAddress does not accept one.
                var entry = await Dns.GetHostEntryAsync(address).WaitAsync(TimeSpan.FromSeconds(2), ct);
                name = entry.HostName is { Length: > 0 } host && host != address.ToString()
                    ? host.Split('.')[0]
                    : "";
            }
            catch (Exception)
            {
                // No PTR record is the normal case on a home network.
            }
        }

        // Asked second, and only when reverse DNS came back empty — which on most home
        // routers is nearly always. This is the step that turns a list of bare addresses
        // into a list of machines, and on Windows it also brings back the hardware address
        // that ARP could not reach from here.
        var mac = "";
        if (netbios)
        {
            var status = await Nbstat.AskAsync(address, Math.Min(timeout, 1_500), ct);
            if (status is not null)
            {
                if (name.Length == 0 && status.Name.Length > 0)
                    name = status.Name;

                mac = status.Mac;
            }
        }

        return new Device(address.ToString(), name, elapsed, open, mac,
            mac.Length > 0 ? Oui.Vendor(mac) : "");
    }

    private static async Task<bool> OpenAsync(IPAddress address, int port, int timeout, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(timeout);

            await client.ConnectAsync(address, port, window.Token);
            return client.Connected;
        }
        catch (Exception)
        {
            return false;   // refused, filtered, or timed out — all mean "not open"
        }
    }

    /// <summary>
    /// "address name" per line. Split on the first run of whitespace, which an address
    /// cannot contain, so the name keeps its spaces without needing quotes.
    /// </summary>
    internal static Dictionary<string, string> Labels(string raw)
    {
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var split = line.IndexOfAny([' ', '\t']);
            if (split <= 0)
                continue;

            var address = line[..split].Trim();

            // An '=' between the two is how people write a mapping; it belongs to neither
            // side. Accepting both spellings costs one call.
            var name = line[(split + 1)..].TrimStart().TrimStart('=').Trim();

            if (IPAddress.TryParse(address, out _) && name.Length > 0)
                labels[address] = name;
        }

        return labels;
    }

    private static IReadOnlyList<int> Ports(string raw) =>
    [
        .. raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var port) ? port : 0)
            .Where(port => port is > 0 and <= 65535)
            .Distinct()
            .Take(8),   // a scan is per address per port; eight is already 8,000 connections on a /24
    ];

    // ---- what was here last time -----------------------------------------------------

    /// <summary>
    /// Kept in app settings rather than in a table of its own.
    ///
    /// A plugin cannot add a table without a migration the host would have to know about,
    /// and this is a list of strings keyed by connection — which is exactly what a key-value
    /// store is for. It travels with the backup like everything else there.
    /// </summary>
    private static string KeyFor(Connection connection) => $"lanscan_known_{connection.Id}";

    private static string SeenKeyFor(Connection connection) => $"lanscan_seen_{connection.Id}";

    private async Task<HashSet<string>> KnownAsync(Connection connection, CancellationToken ct)
    {
        var stored = (await settings.AllAsync(ct)).Get(KeyFor(connection));
        if (stored.Length == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            return new HashSet<string>(
                JsonSerializer.Deserialize<string[]>(stored) ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>The last scan's results, so the card can draw without sweeping again.</summary>
    public async Task<IReadOnlyList<Device>> LastSeenAsync(Connection connection, CancellationToken ct = default)
    {
        var stored = (await settings.AllAsync(ct)).Get(SeenKeyFor(connection));
        if (stored.Length == 0)
            return [];

        try
        {
            return JsonSerializer.Deserialize<Device[]>(stored) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task RememberAsync(
        Connection connection, IEnumerable<string> known, IReadOnlyList<Device> seen, CancellationToken ct)
    {
        await settings.SaveAsync(new Dictionary<string, string>
        {
            // Capped, because this list only ever grows: a network where DHCP hands out a
            // different address every day would otherwise remember a thousand of them and
            // call none of them new.
            [KeyFor(connection)] = JsonSerializer.Serialize(known.Take(Subnet.MaxAddresses).ToArray()),
            [SeenKeyFor(connection)] = JsonSerializer.Serialize(seen),
        }, ct);
    }
}
