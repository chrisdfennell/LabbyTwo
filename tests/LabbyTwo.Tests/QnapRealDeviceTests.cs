using System.Xml.Linq;
using LabbyTwo.Providers;

namespace LabbyTwo.Tests;

/// <summary>
/// The QNAP parsing against what a real TS-464 on QTS 5.2.10 actually sent, rather than
/// what the documentation suggested it would. Each of these was wrong on that NAS before:
/// every disk showed red, the slowest fan read -1 rpm, "none" was offered as a firmware
/// update, and the volume layout it uses was not read at all. The fixtures are the
/// device's own answers with serial numbers and MAC addresses replaced.
/// </summary>
public sealed class QnapRealDeviceTests
{
    private static XDocument Fixture(string name) =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Qnap", $"ts464-qts5210-{name}.xml"));

    [Fact]
    public void The_one_system_fan_is_read_and_the_power_supplys_minus_one_is_not()
    {
        // sysinfo carries sysfan1 = 903 beside sysfan_count, sysfan_fail1, sysfan1_stat
        // and PowerFanStatus1 = -1. Only the first is a fan speed; the last is where the
        // "slowest fan -1 rpm" on the card came from.
        var fans = QnapProvider.ReadFans(Fixture("sysinfo"));

        var fan = Assert.Single(fans);
        Assert.Equal(903, fan.Rpm);
    }

    [Fact]
    public void Four_healthy_drives_and_no_empty_m2_slots()
    {
        // QTS lists both empty M.2 slots as disks 0:1 and 0:2 — no model, Disk_Status -5,
        // and still Health "OK". Those were the two red dots on the card. The four fitted
        // drives say "OK" too, which the old healthy-word list did not include.
        var disks = QnapProvider.ReadDisks(Fixture("smart"));

        Assert.Equal(4, disks.Count);
        Assert.All(disks, disk => Assert.False(disk.IsFailing));
        Assert.DoesNotContain(disks, disk => disk.Slot is "0:1" or "0:2");
        Assert.All(disks, disk => Assert.Equal("WD80EFPX-68C4ZN0", disk.Model));
        Assert.Equal([39.0, 38.0, 38.0, 41.0], disks.Select(disk => disk.TempC ?? -1));
    }

    [Fact]
    public void No_update_is_offered_when_qts_says_none()
    {
        // Both the official and recommended channels answer newVersion "none".
        Assert.Null(QnapProvider.ReadAvailableFirmware(Fixture("firmware"), "5.2.10"));
    }

    [Fact]
    public void The_volume_list_layout_is_read_and_the_empty_second_volume_is_skipped()
    {
        // volumeList names DataVol1 (volumeValue 1); volumeUseList carries its sizes, and
        // a second entry with total_size 0 that has no volume behind it.
        var volumes = QnapProvider.ReadVolumes(Fixture("volumes"));

        var volume = Assert.Single(volumes);
        Assert.Equal("DataVol1", volume.Label);
        Assert.Equal("1", volume.Id);
        Assert.Equal(18_793_549_795_328, volume.TotalBytes);
        Assert.Equal(5_687_991_943_168, volume.FreeBytes);
        Assert.Equal(69.7, Math.Round(volume.UsedPercent, 1));
    }
}
