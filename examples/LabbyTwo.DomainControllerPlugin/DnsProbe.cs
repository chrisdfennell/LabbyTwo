using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LabbyTwo.DomainControllerPlugin;

/// <summary>
/// A real DNS question, asked of the domain controller itself.
///
/// A port check on 53 proves something is listening; it does not prove the DC can still
/// answer for its own zone, which is the failure that matters. Active Directory finds
/// everything through DNS — a domain member locates a DC by looking up SRV records — so a
/// DC whose DNS service is running but whose zone is broken takes the domain down while
/// looking perfectly healthy from the outside.
///
/// The question asked is the domain's own SOA record, which every DC must be able to answer
/// authoritatively about, and which needs no credentials.
/// </summary>
public static class DnsProbe
{
    public sealed record Answer(bool Ok, bool Authoritative, int Records, double Milliseconds, string Detail);

    public static async Task<Answer> AskSoaAsync(
        string server, string domain, int timeoutMs, CancellationToken ct)
    {
        using var socket = new UdpClient(AddressFamily.InterNetwork);
        var started = DateTimeOffset.UtcNow;

        try
        {
            var query = Build(domain);
            await socket.SendAsync(query, new IPEndPoint(IPAddress.Parse(server), 53), ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Math.Clamp(timeoutMs, 200, 10_000));

            var reply = await socket.ReceiveAsync(timeout.Token);
            var elapsed = (DateTimeOffset.UtcNow - started).TotalMilliseconds;

            return Read(reply.Buffer, elapsed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new Answer(false, false, 0, 0, "no reply");
        }
        catch (Exception exception) when (exception is SocketException or FormatException)
        {
            return new Answer(false, false, 0, 0, exception.GetBaseException().Message);
        }
    }

    /// <summary>
    /// A minimal query: one question, no recursion. Recursion is deliberately off — asking
    /// the DC to go and find the answer elsewhere would let a broken zone pass by being
    /// forwarded, which is the exact thing this is checking for.
    /// </summary>
    internal static byte[] Build(string domain)
    {
        var packet = new List<byte>
        {
            0x4C, 0x32,     // transaction id; any value, and it comes back unchanged
            0x00, 0x00,     // flags: standard query, recursion not desired
            0x00, 0x01,     // one question
            0x00, 0x00,     // no answers
            0x00, 0x00,     // no authority records
            0x00, 0x00,     // no additional records
        };

        // A name is its labels, each preceded by its length, ending with a zero byte.
        foreach (var label in domain.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63)
                throw new FormatException($"“{label}” is not a usable part of a domain name.");

            packet.Add((byte)bytes.Length);
            packet.AddRange(bytes);
        }

        packet.Add(0x00);           // end of the name
        packet.AddRange([0x00, 0x06]);  // type SOA
        packet.AddRange([0x00, 0x01]);  // class IN

        return [.. packet];
    }

    /// <summary>
    /// Only the header is read. The answer's contents do not matter — that the DC answered
    /// authoritatively, without an error, about its own zone is the whole of the question.
    /// </summary>
    internal static Answer Read(byte[] reply, double elapsed)
    {
        if (reply.Length < 12)
            return new Answer(false, false, 0, elapsed, "truncated reply");

        var flags = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(2, 2));
        var answers = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(6, 2));
        var authority = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(8, 2));

        var code = flags & 0x000F;
        var authoritative = (flags & 0x0400) != 0;

        var detail = code switch
        {
            0 => "",
            2 => "the server failed the query",
            3 => "no such domain — this DC does not hold that zone",
            5 => "refused",
            _ => $"rcode {code}",
        };

        // An SOA usually comes back in the answer section, but a DC that is authoritative
        // may put it in the authority section instead. Either counts as knowing the zone.
        var records = answers + authority;

        return new Answer(code == 0 && records > 0, authoritative, records, elapsed, detail);
    }
}
