namespace LabbyTwo.LanScanPlugin;

/// <summary>
/// Who made the thing, from the first half of its MAC address.
///
/// The first three bytes of a MAC are an Organisationally Unique Identifier assigned by the
/// IEEE, and turning one into "Espressif" is what makes a scan readable: a row saying
/// 192.168.1.94 is a number, and one saying "Espressif" is a smart plug you had forgotten
/// about.
///
/// **This is a curated list, not the register.** The full IEEE file is thirty-odd thousand
/// entries and several megabytes, which is a lot of assembly to carry so that a home network
/// can name a dozen devices — and it goes stale, so shipping it would mean either a stale
/// copy or a plugin that downloads one. What is here is the manufacturers that actually turn
/// up on a house network. Anything unrecognised is left blank rather than guessed at, and a
/// blank vendor is not a failure: it is the honest answer for a device nobody here has a
/// prefix for.
///
/// A locally administered address — the randomised MACs phones now use by default — is
/// reported as such, because "unknown vendor" and "deliberately anonymous" are different
/// facts and only one of them means you should go and look.
/// </summary>
public static class Oui
{
    /// <summary>The name for a MAC, or empty when there is nothing honest to say.</summary>
    public static string Vendor(string? mac)
    {
        if (mac is not { Length: >= 8 })
            return "";

        var prefix = mac[..8].ToLowerInvariant();

        if (Known.TryGetValue(prefix, out var vendor))
            return vendor;

        return IsRandomised(mac) ? "randomised" : "";
    }

    /// <summary>
    /// The second-least-significant bit of the first byte marks a locally administered
    /// address. Every modern phone sets it when it joins a network it does not trust, which
    /// on a home network is most of the visiting devices — and it is why a scan sees a new
    /// "device" every time a guest reconnects.
    /// </summary>
    public static bool IsRandomised(string mac) =>
        mac.Length >= 2
        && int.TryParse(mac[..2], System.Globalization.NumberStyles.HexNumber, null, out var first)
        && (first & 0b10) != 0;

    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        // Single-board computers and the things people put on a shelf
        ["b8:27:eb"] = "Raspberry Pi",
        ["dc:a6:32"] = "Raspberry Pi",
        ["e4:5f:01"] = "Raspberry Pi",
        ["28:cd:c1"] = "Raspberry Pi",
        ["d8:3a:dd"] = "Raspberry Pi",
        ["2c:cf:67"] = "Raspberry Pi",

        // The chips inside most cheap smart plugs, sensors and bulbs
        ["24:0a:c4"] = "Espressif",
        ["30:ae:a4"] = "Espressif",
        ["3c:61:05"] = "Espressif",
        ["7c:9e:bd"] = "Espressif",
        ["a4:cf:12"] = "Espressif",
        ["b4:e6:2d"] = "Espressif",
        ["cc:50:e3"] = "Espressif",
        ["ec:fa:bc"] = "Espressif",
        ["84:f3:eb"] = "Espressif",

        // Networking
        ["00:15:6d"] = "Ubiquiti",
        ["04:18:d6"] = "Ubiquiti",
        ["24:5a:4c"] = "Ubiquiti",
        ["78:8a:20"] = "Ubiquiti",
        ["b4:fb:e4"] = "Ubiquiti",
        ["e0:63:da"] = "Ubiquiti",
        ["fc:ec:da"] = "Ubiquiti",
        ["00:1d:7e"] = "Cisco-Linksys",
        ["14:cc:20"] = "TP-Link",
        ["50:c7:bf"] = "TP-Link",
        ["a4:2b:b0"] = "TP-Link",
        ["b0:be:76"] = "TP-Link",
        ["c0:06:c3"] = "TP-Link",
        ["00:1f:33"] = "Netgear",
        ["a0:40:a0"] = "Netgear",
        ["9c:3d:cf"] = "Netgear",
        ["00:14:bf"] = "Cisco-Linksys",
        ["2c:30:33"] = "Netgear",
        ["f8:1a:67"] = "TP-Link",

        // NAS and servers
        ["00:08:9b"] = "QNAP",
        ["24:5e:be"] = "QNAP",
        ["00:11:32"] = "Synology",
        ["90:09:d0"] = "Synology",
        ["00:25:90"] = "Supermicro",
        ["0c:c4:7a"] = "Supermicro",
        ["a0:36:9f"] = "Intel",
        ["00:1b:21"] = "Intel",
        ["3c:fd:fe"] = "Intel",
        ["94:c6:91"] = "Elitegroup",

        // Phones, tablets, laptops
        ["a4:83:e7"] = "Apple",
        ["ac:bc:32"] = "Apple",
        ["f0:18:98"] = "Apple",
        ["3c:15:c2"] = "Apple",
        ["68:ab:1e"] = "Apple",
        ["8c:85:90"] = "Apple",
        ["d0:81:7a"] = "Apple",
        ["00:1c:b3"] = "Apple",
        ["5c:f9:38"] = "Apple",
        ["00:12:47"] = "Samsung",
        ["1c:62:b8"] = "Samsung",
        ["78:1f:db"] = "Samsung",
        ["8c:77:12"] = "Samsung",
        ["c8:19:f7"] = "Samsung",
        ["00:26:37"] = "Samsung",

        // Media and home
        ["00:04:20"] = "Roku",
        ["b0:a7:37"] = "Roku",
        ["cc:6d:a0"] = "Roku",
        ["d8:31:34"] = "Roku",
        ["00:0e:58"] = "Sonos",
        ["5c:aa:fd"] = "Sonos",
        ["78:28:ca"] = "Sonos",
        ["b8:e9:37"] = "Sonos",
        ["00:17:88"] = "Philips Hue",
        ["ec:b5:fa"] = "Philips Hue",
        ["18:b4:30"] = "Nest",
        ["64:16:66"] = "Nest",
        ["f4:f5:d8"] = "Google",
        ["1c:f2:9a"] = "Google",
        ["30:fd:38"] = "Google",
        ["44:65:0d"] = "Amazon",
        ["68:37:e9"] = "Amazon",
        ["fc:65:de"] = "Amazon",
        ["b4:7c:9c"] = "Amazon",
        ["00:bb:3a"] = "Amazon",
        ["ac:63:be"] = "Amazon",

        // Printers and cameras
        ["00:00:48"] = "Epson",
        ["a4:5d:36"] = "HP",
        ["3c:d9:2b"] = "HP",
        ["00:80:77"] = "Brother",
        ["00:1a:1e"] = "Aruba",
        ["bc:ad:28"] = "Hikvision",
        ["4c:bd:8f"] = "Reolink",
        ["ec:71:db"] = "Reolink",

        // Virtualisation, which is what a lot of a home lab actually is
        ["00:50:56"] = "VMware",
        ["00:0c:29"] = "VMware",
        ["00:15:5d"] = "Hyper-V",
        ["52:54:00"] = "QEMU/KVM",
        ["02:42:ac"] = "Docker",
        ["08:00:27"] = "VirtualBox",
        ["bc:24:11"] = "Proxmox",
    };
}
