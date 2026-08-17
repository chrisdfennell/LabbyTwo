using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LabbyTwo.LanScanPlugin;

/// <summary>
/// A NetBIOS node status query — the one way to ask a device what it is called that still
/// works from a bridged container.
///
/// <see cref="Arp"/> cannot cross a router and neither can mDNS, because both are layer 2:
/// the replies never reach a container whose only neighbour is its bridge gateway. NBSTAT
/// is ordinary unicast UDP to port 137, so it is routed like anything else and answers from
/// the far side of the LAN.
///
/// What comes back is the device's own name — which matters because reverse DNS is silent
/// on most home networks. A router hands out addresses without registering PTR records for
/// them, so a sweep of 254 addresses can find 29 devices and name two of them. Anything
/// running Windows or Samba answers this instead.
///
/// The reply also has room for the adapter's hardware address, and real Windows fills it
/// in. Samba does not — it sends six zero bytes — so a NAS answers with its name and no
/// MAC. That is why <see cref="Result.Mac"/> is empty rather than "00:00:00:00:00:00":
/// zeros are the absence of an answer, not an address.
/// </summary>
public static class Nbstat
{
    public sealed record Result(string Name, string Mac);

    /// <summary>
    /// The wildcard node status request. The name is "*" in NetBIOS' half-ASCII encoding —
    /// each byte split into two nibbles and added to 'A' — padded to sixteen characters,
    /// which is why it reads as CK followed by thirty A's rather than anything meaningful.
    /// </summary>
    private static readonly byte[] Query =
    [
        0x00, 0x00,             // transaction id
        0x00, 0x00,             // flags: a plain query
        0x00, 0x01,             // one question
        0x00, 0x00,             // no answers
        0x00, 0x00,             // no authority records
        0x00, 0x00,             // no additional records
        0x20,                   // the encoded name is 32 bytes long
        .. "CKAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"u8,
        0x00,                   // end of the name
        0x00, 0x21,             // type NBSTAT
        0x00, 0x01,             // class IN
    ];

    /// <summary>
    /// Asks one address. Returns null when nothing answers, which is the common case and
    /// not a failure — most things on a home network do not speak NetBIOS at all.
    /// </summary>
    public static async Task<Result?> AskAsync(IPAddress address, int timeoutMs, CancellationToken ct)
    {
        using var socket = new UdpClient(AddressFamily.InterNetwork);

        try
        {
            await socket.SendAsync(Query, new IPEndPoint(address, 137), ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Math.Clamp(timeoutMs, 100, 10_000));

            var reply = await socket.ReceiveAsync(timeout.Token);
            return Parse(reply.Buffer);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;    // nothing answered in time
        }
        catch (SocketException)
        {
            // An ICMP port-unreachable comes back as a socket error on Windows. It means
            // "nothing is listening", which is an answer rather than a problem.
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The response layout: a twelve-byte header, the echoed name (34 bytes), type, class,
    /// TTL and length — 56 bytes in all — then a count of names, then eighteen bytes each,
    /// then the adapter statistics, which open with the six-byte MAC.
    /// </summary>
    internal static Result? Parse(byte[] data)
    {
        const int NamesStart = 57;

        if (data.Length < NamesStart)
            return null;

        int count = data[56];
        if (count is 0 or > 100)
            return null;

        var offset = NamesStart;
        var name = "";

        for (var index = 0; index < count && offset + 18 <= data.Length; index++, offset += 18)
        {
            var entry = Encoding.Latin1.GetString(data, offset, 15).Trim();
            var suffix = data[offset + 15];
            var group = (data[offset + 16] & 0x80) != 0;

            // Suffix 0x00 on a unique name is the machine's own name. Everything else is a
            // service or a workgroup — 0x20 is file sharing, and a group entry is the
            // workgroup, which would label every machine in the house identically.
            if (name.Length == 0 && suffix == 0x00 && !group && entry.Length > 0
                && !entry.StartsWith("__", StringComparison.Ordinal))
                name = entry;
        }

        var mac = "";
        if (offset + 6 <= data.Length)
        {
            var bytes = data.AsSpan(offset, 6);

            // Samba answers with zeros. Reporting that as a hardware address would put a
            // device on the network that does not exist, and six zeros is a broadcast
            // address rather than anything's adapter.
            if (bytes.ToArray().Any(b => b != 0))
                mac = string.Join(':', bytes.ToArray().Select(b => b.ToString("x2")));
        }

        return name.Length == 0 && mac.Length == 0 ? null : new Result(name, mac);
    }
}
