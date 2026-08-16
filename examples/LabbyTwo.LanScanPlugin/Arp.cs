using System.Net;

namespace LabbyTwo.LanScanPlugin;

/// <summary>
/// Hardware addresses, read out of the kernel's ARP cache after a sweep.
///
/// This is the half of a desktop scanner that a container usually cannot do, and the reason
/// is layer 2 rather than permissions. ARP only reaches machines on the same broadcast
/// domain. A bridged container's only neighbours are its bridge gateway and the other
/// containers on it, so a sweep of the LAN gets replies routed *through* the gateway and
/// the cache learns nothing about the machines that sent them.
///
/// So this is written to work when the deployment allows it and to return nothing when it
/// does not, rather than to be switched on by a setting somebody would have to understand.
/// Host networking or a macvlan puts LabbyTwo on the LAN's own segment and the addresses
/// appear; a bridge does not and they do not. Either way the scan still works — the vendor
/// column is simply empty, which is why nothing here is treated as an error.
///
/// Populated as a side effect of the sweep: pinging a host is what puts it in the cache in
/// the first place, so this is read *after* the addresses have been probed rather than
/// before.
/// </summary>
public static class Arp
{
    /// <summary>Linux exposes the cache as a text table. Nothing else here reads it.</summary>
    private const string ProcPath = "/proc/net/arp";

    /// <summary>
    /// Address to MAC, for whatever the kernel currently knows. Empty on any platform or
    /// deployment that cannot answer, which is the normal case rather than a failure.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Table()
    {
        var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!OperatingSystem.IsLinux() || !File.Exists(ProcPath))
            return table;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(ProcPath);
        }
        catch (IOException)
        {
            return table;
        }
        catch (UnauthorizedAccessException)
        {
            return table;
        }

        // IP address       HW type     Flags       HW address            Mask     Device
        // 192.168.1.1      0x1         0x2         b8:27:eb:11:22:33     *        eth0
        foreach (var line in lines.Skip(1))
        {
            var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 4)
                continue;

            var address = columns[0];
            var mac = columns[3];

            // 00:00:00:00:00:00 is an entry the kernel created and never resolved — the
            // host did not answer, and reporting zeros as its hardware address would be
            // inventing a device.
            if (!IPAddress.TryParse(address, out _) || !IsRealMac(mac))
                continue;

            table[address] = mac.ToLowerInvariant();
        }

        return table;
    }

    private static bool IsRealMac(string mac) =>
        mac.Length == 17
        && mac != "00:00:00:00:00:00"
        && mac.Split(':').Length == 6;
}
