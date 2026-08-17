using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace LabbyTwo.DomainControllerPlugin;

/// <summary>
/// How far out a domain controller's clock is, asked over SNTP.
///
/// This is the single most valuable number about a domain controller, and the one nobody
/// watches. Kerberos refuses a ticket whose timestamp is more than five minutes from the
/// server's clock, so a DC that drifts stops authenticating — and the symptom is not "the
/// clock is wrong", it is logins failing across the whole domain for no visible reason. A
/// DC is also the time source for every machine joined to it, so it takes them with it.
///
/// No agent and no credentials: a domain controller is already an NTP server, so asking it
/// the time is a supported question rather than a trick.
/// </summary>
public static class Sntp
{
    /// <summary>Seconds between the NTP epoch (1900) and the Unix one (1970).</summary>
    private const long Epoch = 2_208_988_800;

    /// <summary>
    /// The offset in seconds: positive when the far end is ahead of us. Null when nothing
    /// answered, which is not a failure — plenty of machines do not serve time.
    /// </summary>
    public static async Task<double?> OffsetAsync(string host, int port, int timeoutMs, CancellationToken ct)
    {
        using var socket = new UdpClient(AddressFamily.InterNetwork);

        try
        {
            // Leap indicator 0, version 3, mode 3 (client). The rest of the 48 bytes is
            // zero, which is what a client is supposed to send.
            var request = new byte[48];
            request[0] = 0x1B;

            var before = DateTimeOffset.UtcNow;
            await socket.SendAsync(request, new IPEndPoint(IPAddress.Parse(host), port), ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Math.Clamp(timeoutMs, 200, 10_000));

            var reply = await socket.ReceiveAsync(timeout.Token);
            var after = DateTimeOffset.UtcNow;

            return Offset(reply.Buffer, before, after);
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or FormatException)
        {
            // A cancelled *outer* token is the app shutting down and has to propagate; the
            // inner one is only this probe giving up.
            if (exception is OperationCanceledException && ct.IsCancellationRequested)
                throw;

            return null;
        }
    }

    /// <summary>
    /// The arithmetic on its own, so it can be checked without a network. The reply's
    /// transmit timestamp is compared against the midpoint of when we asked and when the
    /// answer arrived, which cancels out most of the round trip.
    /// </summary>
    internal static double? Offset(byte[] reply, DateTimeOffset before, DateTimeOffset after)
    {
        if (reply.Length < 48)
            return null;

        // Bytes 40-47: transmit timestamp, four bytes of seconds then four of fraction.
        var seconds = BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(40, 4));
        var fraction = BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(44, 4));

        // Zero is what an unsynchronised server sends, and subtracting the epoch from it
        // would report the far end as being 70 years behind rather than as having no
        // opinion — which is a different and much louder fault than the real one.
        if (seconds == 0)
            return null;

        var theirs = (seconds - Epoch) + (fraction / (double)uint.MaxValue);
        var ours = (before.ToUnixTimeMilliseconds() + after.ToUnixTimeMilliseconds()) / 2000d;

        return theirs - ours;
    }
}
