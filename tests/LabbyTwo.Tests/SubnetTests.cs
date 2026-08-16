using System.Net;
using LabbyTwo.LanScanPlugin;

namespace LabbyTwo.Tests;

/// <summary>
/// The address arithmetic behind the network scan.
///
/// Tested harder than the rest of that plugin because it is the only part that can do
/// damage. Everything else is a ping with a timeout; this decides *what gets pinged*, and
/// getting it wrong means sweeping a neighbour's range, hammering a broadcast address, or
/// walking sixty-five thousand hosts one at a time.
/// </summary>
public class SubnetTests
{
    [Fact]
    public void ASlash24IsTheUsableHostsWithoutNetworkOrBroadcast()
    {
        var hosts = Subnet.Hosts("192.168.1.0/24");

        Assert.Equal(254, hosts.Count);
        Assert.Equal(IPAddress.Parse("192.168.1.1"), hosts[0]);
        Assert.Equal(IPAddress.Parse("192.168.1.254"), hosts[^1]);

        // The two that are not devices.
        Assert.DoesNotContain(IPAddress.Parse("192.168.1.0"), hosts);
        Assert.DoesNotContain(IPAddress.Parse("192.168.1.255"), hosts);
    }

    /// <summary>
    /// The host bits are masked off, so "192.168.1.77/24" means the same range as
    /// "192.168.1.0/24" — which is what somebody typing their own address expects, and the
    /// alternative is a scan that silently starts in the middle.
    /// </summary>
    [Fact]
    public void TheAddressIsMaskedToItsNetwork()
    {
        Assert.Equal(Subnet.Hosts("192.168.1.0/24"), Subnet.Hosts("192.168.1.77/24"));
    }

    [Theory]
    [InlineData("192.168.1.0/30", 2)]
    [InlineData("192.168.1.0/29", 6)]
    [InlineData("192.168.1.0/24", 254)]
    [InlineData("10.0.0.0/22", 1022)]
    public void TheCountIsWhatTheMathsSays(string cidr, int expected)
        => Assert.Equal(expected, Subnet.Hosts(cidr).Count);

    /// <summary>
    /// A /31 and /32 have no network or broadcast address to leave out — a /32 being how you
    /// say "just this one machine".
    /// </summary>
    [Theory]
    [InlineData("192.168.1.50/32", 1)]
    [InlineData("192.168.1.50/31", 2)]
    public void SmallPrefixesKeepEveryAddress(string cidr, int expected)
        => Assert.Equal(expected, Subnet.Hosts(cidr).Count);

    [Fact]
    public void ABareAddressMeansThatOneHost()
    {
        var hosts = Subnet.Hosts("192.168.1.50");

        Assert.Single(hosts);
        Assert.Equal(IPAddress.Parse("192.168.1.50"), hosts[0]);
    }

    /// <summary>
    /// The guard rail. A /16 is sixty-five thousand addresses, which is somebody who typed
    /// the wrong number — refusing explains itself, obeying takes an hour and looks like an
    /// attack to every device on the way.
    /// </summary>
    [Theory]
    [InlineData("10.0.0.0/16")]
    [InlineData("10.0.0.0/8")]
    [InlineData("0.0.0.0/0")]
    public void AnAbsurdlyLargeRangeIsRefused(string cidr)
    {
        var refused = Assert.Throws<InvalidOperationException>(() => Subnet.Hosts(cidr));
        Assert.Contains("narrower", refused.Message);
    }

    [Theory]
    [InlineData("not an address")]
    [InlineData("192.168.1.0/33")]
    [InlineData("192.168.1.0/-1")]
    [InlineData("192.168.1.0/24/8")]
    [InlineData("")]
    [InlineData(null)]
    public void RubbishIsRejectedRatherThanGuessedAt(string? cidr)
        => Assert.False(Subnet.TryParse(cidr, out _, out _));

    /// <summary>
    /// IPv6 is refused rather than half-supported. A /64 is more addresses than exist in
    /// IPv4 altogether, so sweeping one is not a thing, and quietly accepting the syntax
    /// would produce a scan that finds nothing for reasons nobody could guess.
    /// </summary>
    [Fact]
    public void IPv6IsRefused()
        => Assert.False(Subnet.TryParse("fe80::/64", out _, out _));

    [Theory]
    [InlineData("192.168.1.0/24", 254)]
    [InlineData("192.168.1.0/25", 126)]
    [InlineData("192.168.1.50", 1)]
    public void CountAgreesWithWhatHostsProduces(string cidr, long expected)
        => Assert.Equal(expected, Subnet.Count(cidr));

    /// <summary>
    /// The top of the address space is where an off-by-one becomes an infinite loop: the
    /// counter wraps to zero and the walk starts again.
    /// </summary>
    [Fact]
    public void TheVeryTopOfTheAddressSpaceTerminates()
    {
        var hosts = Subnet.Hosts("255.255.255.252/30");

        Assert.Equal(2, hosts.Count);
        Assert.Equal(IPAddress.Parse("255.255.255.253"), hosts[0]);
    }
}
