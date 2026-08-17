using System.Buffers.Binary;
using System.Text;
using LabbyTwo.DomainControllerPlugin;

namespace LabbyTwo.Tests;

/// <summary>
/// The two protocol readers behind the domain controller checks. Both are pure functions of
/// a byte array, which is the only reason they can be tested at all — there is no way to
/// make a real domain controller drift its clock on demand.
///
/// The cases that matter are the ones that fail silently: an epoch confusion reports a
/// healthy DC as seventy years out, and an unsynchronised server sending zeros would do
/// exactly that if it were treated as a reading.
/// </summary>
public sealed class DomainControllerTests
{
    /// <summary>Seconds between 1900 and 1970, which is the whole trap in SNTP.</summary>
    private const long Epoch = 2_208_988_800;

    private static byte[] Reply(DateTimeOffset transmitted)
    {
        var reply = new byte[48];
        var seconds = (uint)(transmitted.ToUnixTimeSeconds() + Epoch);
        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(40, 4), seconds);

        // Fraction left at zero: whole seconds are enough to prove the arithmetic, and a
        // fraction would only add rounding to compare against.
        return reply;
    }

    [Fact]
    public void AClockInStepReadsAsNoOffset()
    {
        var now = DateTimeOffset.UtcNow;
        var offset = Sntp.Offset(Reply(now), now, now);

        Assert.NotNull(offset);

        // Within a second: the reply carries whole seconds, so anything tighter would be
        // testing the truncation rather than the maths.
        Assert.True(Math.Abs(offset!.Value) < 1, $"expected about zero, got {offset}");
    }

    [Fact]
    public void AClockThatIsAheadReadsPositive()
    {
        var now = DateTimeOffset.UtcNow;
        var offset = Sntp.Offset(Reply(now.AddSeconds(90)), now, now);

        Assert.NotNull(offset);
        Assert.InRange(offset!.Value, 89, 91);
    }

    [Fact]
    public void AClockThatIsBehindReadsNegative()
    {
        var now = DateTimeOffset.UtcNow;
        var offset = Sntp.Offset(Reply(now.AddSeconds(-400)), now, now);

        Assert.NotNull(offset);
        Assert.InRange(offset!.Value, -401, -399);
    }

    /// <summary>
    /// The one that would be worst to get wrong. A server with no time to give sends zeros,
    /// and subtracting the epoch from zero reports it as being seventy years behind — which
    /// would fire the Kerberos alert on a DC whose only fault is that it has just booted.
    /// </summary>
    [Fact]
    public void AServerWithNoTimeToGiveIsNotAReading()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.Null(Sntp.Offset(new byte[48], now, now));
    }

    [Fact]
    public void ATruncatedReplyIsNotAReading()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.Null(Sntp.Offset(new byte[20], now, now));
    }

    [Fact]
    public void TheDnsQuestionIsBuiltTheWayAResolverExpects()
    {
        var query = DnsProbe.Build("fennell.local");

        Assert.Equal(0x00, query[2]);       // no recursion desired: a forwarded answer would
        Assert.Equal(0x00, query[3]);       // hide the broken zone this is looking for
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(4, 2)));

        // Labels are length-prefixed and the name ends with a zero byte.
        var name = query.AsSpan(12).ToArray();
        Assert.Equal(7, name[0]);
        Assert.Equal("fennell", Encoding.ASCII.GetString(name, 1, 7));
        Assert.Equal(5, name[8]);
        Assert.Equal("local", Encoding.ASCII.GetString(name, 9, 5));
        Assert.Equal(0, name[14]);

        Assert.Equal(6, BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(15, 2)));  // SOA
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(17, 2)));  // IN
    }

    [Fact]
    public void ALabelTooLongForDnsIsRefusedRatherThanTruncated()
        => Assert.Throws<FormatException>(() => DnsProbe.Build(new string('a', 64) + ".local"));

    private static byte[] Header(int rcode, bool authoritative, int answers, int authority)
    {
        var reply = new byte[12];
        var flags = 0x8000 | rcode | (authoritative ? 0x0400 : 0);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2, 2), (ushort)flags);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(6, 2), (ushort)answers);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(8, 2), (ushort)authority);
        return reply;
    }

    [Fact]
    public void AnAuthoritativeAnswerCounts()
    {
        var answer = DnsProbe.Read(Header(0, authoritative: true, answers: 1, authority: 0), 12);

        Assert.True(answer.Ok);
        Assert.True(answer.Authoritative);
    }

    /// <summary>
    /// A domain controller often returns the SOA in the authority section rather than the
    /// answer section. Counting only answers would report a healthy DC as having lost its
    /// zone, which is the false alarm that gets an alert switched off.
    /// </summary>
    [Fact]
    public void AnSoaInTheAuthoritySectionStillCounts()
    {
        var answer = DnsProbe.Read(Header(0, authoritative: true, answers: 0, authority: 1), 12);

        Assert.True(answer.Ok);
    }

    [Fact]
    public void NoSuchDomainSaysWhichProblemItIs()
    {
        var answer = DnsProbe.Read(Header(3, authoritative: false, answers: 0, authority: 0), 12);

        Assert.False(answer.Ok);
        Assert.Contains("zone", answer.Detail);
    }

    [Fact]
    public void AnAnswerWithNoRecordsIsNotAnAnswer()
    {
        var answer = DnsProbe.Read(Header(0, authoritative: true, answers: 0, authority: 0), 12);

        Assert.False(answer.Ok);
    }
}
