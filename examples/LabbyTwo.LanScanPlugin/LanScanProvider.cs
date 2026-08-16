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
/// desktop scanner gets vendor names. Host networking or a macvlan would give those back;
/// that is a deployment choice rather than something this can fix, so it is stated rather
/// than worked around.
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

            // Bounded rather than all at once. A thousand simultaneous pings is a burst your
            // own switch may drop, which shows up as devices that "went missing" and did not.
            using var limit = new SemaphoreSlim(64);

            var found = await Task.WhenAll(hosts.Select(async address =>
            {
                await limit.WaitAsync(ct);
                try
                {
                    return await ProbeOneAsync(address, timeout, ports, resolve, ct);
                }
                finally
                {
                    limit.Release();
                }
            }));

            var up = found.Where(device => device is not null).Select(device => device!).ToList();
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
    public sealed record Device(string Address, string Name, double Milliseconds, IReadOnlyList<int> OpenPorts)
    {
        public string Label => Name.Length > 0 ? Name : Address;
    }

    private static async Task<Device?> ProbeOneAsync(
        IPAddress address, int timeout, IReadOnlyList<int> ports, bool resolve, CancellationToken ct)
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

        return new Device(address.ToString(), name, elapsed, open);
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
