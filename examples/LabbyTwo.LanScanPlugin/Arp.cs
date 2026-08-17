using System.Net;

namespace LabbyTwo.LanScanPlugin;

/// <summary>
/// Hardware addresses, read out of an ARP table.
///
/// ARP only reaches machines on the same broadcast domain. A bridged container's only
/// neighbours are its bridge gateway and the other containers beside it, so a sweep of the
/// LAN gets replies routed *through* the gateway and the container's own cache learns
/// nothing about the machines that sent them. That is a fact about layer 2, not a
/// permission problem, and no amount of code inside the container changes it.
///
/// So there are two ways to have real addresses here, and this reads either:
///
/// <list type="bullet">
/// <item><b>Be on the LAN.</b> Host networking or a macvlan puts LabbyTwo on the same
/// segment and its own <c>/proc/net/arp</c> fills up by itself. This is what the previous
/// version of this dashboard did — same code, <c>network_mode: host</c> — and it is why it
/// showed MAC addresses. The cost is that container-name DNS stops working, which on a NAS
/// whose bridges will not forward between stacks is usually the thing you needed more.</item>
/// <item><b>Be told.</b> Something already on the LAN writes its table to a file, and
/// LabbyTwo reads it. A line of cron on the NAS costs nothing and keeps the container
/// exactly where it is:
/// <code>* * * * * /usr/sbin/arp -an &gt; /share/Container/labbytwo/arp-table 2&gt;/dev/null</code>
/// then mount that file in and point <c>arp_source</c> at it.</item>
/// </list>
///
/// Both formats are read, because the file you get depends on which command wrote it and
/// nobody should have to care: the kernel's own table is columns, and <c>arp -an</c> is
/// prose.
/// </summary>
public static class Arp
{
    /// <summary>The kernel's own table. Correct when LabbyTwo is on the LAN, empty otherwise.</summary>
    public const string DefaultPath = "/proc/net/arp";

    /// <summary>
    /// Address to MAC from whichever table is at <paramref name="path"/>. Empty when there
    /// is no file, which is the normal case on a bridge rather than a failure.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Table(string? path = null)
    {
        var source = string.IsNullOrWhiteSpace(path) ? DefaultPath : path.Trim();
        var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(source))
            return table;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(source);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return table;
        }

        foreach (var line in lines)
        {
            foreach (var (address, mac) in ReadLine(line))
                table[address] = mac.ToLowerInvariant();
        }

        return table;
    }

    /// <summary>
    /// One line, in whichever of the two shapes it is. Written as a yield rather than a
    /// nullable tuple so a header or a blank line is simply nothing, with no sentinel to
    /// check at the call site.
    /// </summary>
    private static IEnumerable<(string Address, string Mac)> ReadLine(string line)
    {
        var columns = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (columns.Length < 3)
            yield break;

        // /proc/net/arp:  192.168.86.1  0x1  0x2  b8:27:eb:11:22:33  *  eth0
        if (IPAddress.TryParse(columns[0], out _) && columns.Length >= 4 && IsRealMac(columns[3]))
        {
            yield return (columns[0], columns[3]);
            yield break;
        }

        // arp -an:  ? (192.168.86.1) at b8:27:eb:11:22:33 [ether] on eth0
        // Also covers `ip neigh`: 192.168.86.1 dev eth0 lladdr b8:27:eb:11:22:33 REACHABLE
        string? address = null;
        foreach (var column in columns)
        {
            var candidate = column.Trim('(', ')', ',');

            if (address is null && IPAddress.TryParse(candidate, out _))
            {
                address = candidate;
                continue;
            }

            if (address is not null && IsRealMac(candidate))
            {
                yield return (address, candidate);
                yield break;
            }
        }
    }

    /// <summary>
    /// Six colon- or dash-separated octets, and not the all-zero one. Zeros are an entry the
    /// kernel created and never resolved — the host did not answer — and reporting them as a
    /// hardware address would be inventing a device.
    /// </summary>
    internal static bool IsRealMac(string mac)
    {
        if (mac.Length != 17)
            return false;

        var parts = mac.Split(mac.Contains(':') ? ':' : '-');
        return parts.Length == 6
               && parts.All(part => part.Length == 2 && part.All(Uri.IsHexDigit))
               && parts.Any(part => part != "00");
    }
}
