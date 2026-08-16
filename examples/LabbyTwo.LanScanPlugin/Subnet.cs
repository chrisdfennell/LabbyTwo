using System.Net;

namespace LabbyTwo.LanScanPlugin;

/// <summary>
/// The addresses a scan will actually try.
///
/// Its own type, and tested on its own, because this is where a scanner does damage: get
/// the arithmetic wrong and it sweeps somebody else's network, or its own gateway, or
/// sixty-five thousand addresses one at a time. Everything else in this plugin is a ping
/// with a timeout.
/// </summary>
public static class Subnet
{
    /// <summary>
    /// The most a single scan may cover. A /22 is a thousand addresses, which is already a
    /// generous home network and takes a few seconds; a /16 is sixty-five thousand and is
    /// somebody who typed the wrong number. Refusing is friendlier than obeying.
    /// </summary>
    public const int MaxAddresses = 1024;

    /// <summary>
    /// Expands "192.168.1.0/24" into the hosts inside it.
    ///
    /// Network and broadcast addresses are left out: neither is a device, and pinging a
    /// broadcast address is how you get one reply from every host at once and a confusing
    /// result. A /31 and /32 have no such addresses to drop, and are returned whole — a /32
    /// being a perfectly reasonable way to say "just this one".
    /// </summary>
    public static IReadOnlyList<IPAddress> Hosts(string cidr)
    {
        if (!TryParse(cidr, out var network, out var prefix))
            throw new FormatException($"“{cidr}” is not a subnet like 192.168.1.0/24.");

        var total = 1L << (32 - prefix);
        if (total > MaxAddresses)
        {
            throw new InvalidOperationException(
                $"/{prefix} is {total:N0} addresses. The most one scan will cover is {MaxAddresses:N0} — "
                + "use a narrower range.");
        }

        var start = ToUInt(network) & Mask(prefix);
        var end = start + (uint)(total - 1);

        // The usable range. Below a /31 there is no network or broadcast address to skip.
        var first = prefix >= 31 ? start : start + 1;
        var last = prefix >= 31 ? end : end - 1;

        var hosts = new List<IPAddress>();
        for (var address = first; address <= last && hosts.Count < MaxAddresses; address++)
        {
            hosts.Add(ToAddress(address));

            if (address == uint.MaxValue)
                break;   // the counter would wrap and the loop would never end
        }

        return hosts;
    }

    /// <summary>
    /// Accepts a CIDR, or a bare address which is taken to mean that one host. Somebody who
    /// types "192.168.1.50" means one machine, and refusing it to insist on "/32" is
    /// pedantry rather than safety.
    /// </summary>
    public static bool TryParse(string? cidr, out IPAddress network, out int prefix)
    {
        network = IPAddress.None;
        prefix = 32;

        if (string.IsNullOrWhiteSpace(cidr))
            return false;

        var parts = cidr.Trim().Split('/');
        if (parts.Length > 2)
            return false;

        if (!IPAddress.TryParse(parts[0], out var parsed)
            || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;   // IPv4 only: a /64 of IPv6 is not something anybody sweeps
        }

        network = parsed;

        if (parts.Length == 1)
            return true;

        return int.TryParse(parts[1], out prefix) && prefix is >= 0 and <= 32;
    }

    /// <summary>How many hosts a range covers, for saying so before it is run.</summary>
    public static long Count(string cidr) =>
        TryParse(cidr, out _, out var prefix)
            ? prefix >= 31 ? 1L << (32 - prefix) : Math.Max(0, (1L << (32 - prefix)) - 2)
            : 0;

    private static uint Mask(int prefix) => prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);

    private static uint ToUInt(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static IPAddress ToAddress(uint value) =>
        new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
}
