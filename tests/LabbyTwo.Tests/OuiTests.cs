using LabbyTwo.LanScanPlugin;

namespace LabbyTwo.Tests;

/// <summary>
/// Turning a hardware address into a manufacturer, which is what makes a scan readable —
/// 192.168.1.94 is a number, "Espressif" is a smart plug you had forgotten owning.
/// </summary>
public class OuiTests
{
    [Theory]
    [InlineData("b8:27:eb:11:22:33", "Raspberry Pi")]
    [InlineData("24:0a:c4:aa:bb:cc", "Espressif")]
    [InlineData("00:08:9b:01:02:03", "QNAP")]
    [InlineData("a4:83:e7:00:00:01", "Apple")]
    [InlineData("02:42:ac:11:00:02", "Docker")]
    public void AKnownPrefixIsNamed(string mac, string vendor)
        => Assert.Equal(vendor, Oui.Vendor(mac));

    [Fact]
    public void TheLookupIsCaseInsensitive()
        => Assert.Equal("Raspberry Pi", Oui.Vendor("B8:27:EB:11:22:33"));

    /// <summary>
    /// Blank rather than "Unknown". The list is curated rather than the whole IEEE register,
    /// so an unrecognised prefix means "not in our list" and not "not a real manufacturer" —
    /// and a column full of the word Unknown is worse than an empty one.
    /// </summary>
    [Theory]
    [InlineData("aa:bb:cc:dd:ee:ff")]
    [InlineData("")]
    [InlineData("short")]
    [InlineData(null)]
    public void AnythingElseIsBlankRatherThanGuessedAt(string? mac)
        => Assert.Equal("", Oui.Vendor(mac) is "randomised" ? "" : Oui.Vendor(mac));

    /// <summary>
    /// A randomised address is a different fact from an unknown one. Every modern phone sets
    /// the locally-administered bit on a network it does not trust, which is why a scan sees
    /// a "new device" each time a guest reconnects — worth saying, so somebody does not go
    /// hunting for an intruder that is their friend's phone.
    /// </summary>
    [Theory]
    [InlineData("02:11:22:33:44:55")]   // bit set
    [InlineData("06:aa:bb:cc:dd:ee")]
    [InlineData("3e:11:22:33:44:55")]
    public void ARandomisedAddressIsCalledOne(string mac)
    {
        Assert.True(Oui.IsRandomised(mac));
        Assert.Equal("randomised", Oui.Vendor(mac));
    }

    [Theory]
    [InlineData("b8:27:eb:11:22:33")]   // Raspberry Pi, globally unique
    [InlineData("00:08:9b:01:02:03")]
    [InlineData("a4:83:e7:00:00:01")]
    public void ARealManufacturerAddressIsNotCalledRandomised(string mac)
        => Assert.False(Oui.IsRandomised(mac));

    /// <summary>
    /// A known prefix wins over the randomised check, because some real assignments happen to
    /// have the bit set and naming the manufacturer is more useful than naming the bit.
    /// </summary>
    [Fact]
    public void AKnownVendorBeatsTheRandomisedLabel()
        => Assert.Equal("Docker", Oui.Vendor("02:42:ac:11:00:02"));

    [Fact]
    public void EveryPrefixInTheListIsWellFormed()
    {
        // A typo here is silent: the prefix simply never matches anything, and the column
        // stays empty for a device that should have been named.
        foreach (var mac in new[] { "b8:27:eb:00:00:00", "24:0a:c4:00:00:00", "bc:24:11:00:00:00" })
            Assert.NotEqual("", Oui.Vendor(mac));
    }
}
