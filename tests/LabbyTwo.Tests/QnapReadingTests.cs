using System.Xml.Linq;
using LabbyTwo.Providers;

namespace LabbyTwo.Tests;

/// <summary>
/// What the QNAP provider makes of the XML QTS sends back. Every one of these is a reading
/// that was wrong on a real NAS in a way that raised an alert about a problem it did not
/// have, which is the failure that teaches people to stop reading alerts.
/// </summary>
public class QnapReadingTests
{
    private static XDocument Xml(string body) => XDocument.Parse($"<QDocRoot>{body}</QDocRoot>");

    // ---------- Fans ----------

    [Fact]
    public void FanSpeedsAreReadAndTheirBookkeepingIsNot()
    {
        // The shape a two-bay answers with: one fitted fan, a count, a status and a mode,
        // all of them numbers and all of them with "fan" in the name.
        var fans = QnapProvider.ReadFans(Xml("""
            <func><ownContent>
              <sysfan_count>1</sysfan_count>
              <sysfan1>1024 RPM</sysfan1>
              <sysfan1_stat>0</sysfan1_stat>
              <sysfan_mode>2</sysfan_mode>
              <cpufan_count>0</cpufan_count>
            </ownContent></func>
            """));

        var fan = Assert.Single(fans);
        Assert.Equal("sysfan1", fan.Label);
        Assert.Equal(1024, fan.Rpm);
    }

    [Fact]
    public void SlotsWithNoFanFittedAreNotStoppedFans()
    {
        // QTS reports every slot the chassis could hold, and -1 for the empty ones. Read
        // as a speed, the slowest fan was -1 rpm and "a fan has stopped" fired for good.
        var fans = QnapProvider.ReadFans(Xml("""
            <sysfan1>980</sysfan1>
            <sysfan2>-1</sysfan2>
            <sysfan3>-1</sysfan3>
            <cpufan1>1500</cpufan1>
            """));

        Assert.Equal(["sysfan1", "cpufan1"], fans.Select(f => f.Label));
        Assert.Equal(980, fans.Min(f => f.Rpm));
    }

    [Theory]
    [InlineData("sysfan1", true)]
    [InlineData("SysFan12", true)]
    [InlineData("cpufan1", true)]
    [InlineData("fan2", true)]
    [InlineData("sysfan_count", false)]
    [InlineData("sysfan1_stat", false)]
    [InlineData("sysfan_mode", false)]
    [InlineData("fan_speed", false)]
    public void OnlyANumberedFanIsAFan(string element, bool isFan)
    {
        var fans = QnapProvider.ReadFans(Xml($"<{element}>1200</{element}>"));

        Assert.Equal(isFan, fans.Count == 1);
    }

    [Fact]
    public void AStoppedFanStillReadsAsZero()
    {
        // The thing the fan rule exists for must survive the filtering above.
        var fan = Assert.Single(QnapProvider.ReadFans(Xml("<sysfan1>0</sysfan1>")));
        Assert.Equal(0, fan.Rpm);
    }

    // ---------- Disks ----------

    [Fact]
    public void EmptyBaysAreNotDisks()
    {
        var disks = QnapProvider.ReadDisks(Xml("""
            <Disk_Info>
              <entry><HDNo>1</HDNo><Model>WDC WD40EFRX</Model><Health>OK</Health><Temperature><oC>34</oC></Temperature></entry>
              <entry><HDNo>2</HDNo><Model></Model><Health>--</Health></entry>
              <entry><HDNo>3</HDNo><Model>--</Model><Health>--</Health></entry>
              <entry><HDNo>4</HDNo><Model>ST4000VN008</Model><Health>Good</Health><Temperature><oC>37</oC></Temperature></entry>
            </Disk_Info>
            """));

        Assert.Equal(["1", "4"], disks.Select(d => d.Slot));
        Assert.DoesNotContain(disks, d => d.IsFailing);
        Assert.Equal(37, disks.Max(d => d.TempC));
    }

    [Fact]
    public void ADriveQtsIsWorriedAboutStillCounts()
    {
        var disk = Assert.Single(QnapProvider.ReadDisks(Xml("""
            <entry><HDNo>2</HDNo><Model>WDC WD40EFRX</Model><Health>Warning</Health></entry>
            """)));

        Assert.True(disk.IsFailing);
    }

    // ---------- Firmware ----------

    [Theory]
    [InlineData("none")]
    [InlineData("None")]
    [InlineData("0")]
    [InlineData("--")]
    [InlineData("N/A")]
    [InlineData("")]
    public void NothingWaitingIsNotAnUpdateCalledNothing(string word)
    {
        // The installed build sits right beside it. That used to be quoted as the offer's,
        // giving "Firmware none build 20260731" and a notice that never went away.
        var doc = Xml($"""
            <func><ownContent>
              <newVersion>{word}</newVersion>
              <version>5.2.1</version>
              <build>20260731</build>
            </ownContent></func>
            """);

        Assert.Null(QnapProvider.ReadAvailableFirmware(doc, "5.2.1"));
    }

    [Fact]
    public void ARealOfferIsReportedWithItsOwnBuild()
    {
        var doc = Xml("""
            <newVersion>5.2.2</newVersion>
            <newBuild>20260901</newBuild>
            <build>20260731</build>
            """);

        Assert.Equal("5.2.2 build 20260901", QnapProvider.ReadAvailableFirmware(doc, "5.2.1"));
    }

    [Fact]
    public void TheInstalledBuildIsNeverPassedOffAsTheNewOne()
    {
        // No newBuild, only the installed <build>: say the version and nothing more.
        var doc = Xml("""
            <newVersion>5.2.2</newVersion>
            <build>20260731</build>
            """);

        Assert.Equal("5.2.2", QnapProvider.ReadAvailableFirmware(doc, "5.2.1"));
    }

    [Fact]
    public void TheInstalledVersionReportedBackIsNotAnOffer()
    {
        Assert.Null(QnapProvider.ReadAvailableFirmware(Xml("<version>5.2.1</version>"), "5.2.1"));
        Assert.Null(QnapProvider.ReadAvailableFirmware(Xml("<newVersion>5.2.1</newVersion>"), "5.2.1 (20260731)"));
    }
}
