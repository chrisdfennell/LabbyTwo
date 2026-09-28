using System.Text.Json;
using System.Xml.Linq;
using LabbyTwo.Core;
using LabbyTwo.Providers;
using LabbyTwo.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LabbyTwo.Tests;

/// <summary>
/// Each volume recorded, labelled and forecast on its own, alongside the fullest-volume
/// aggregate that everything already reads. The NAS payloads here are the shapes the
/// community documentation describes, since the parsing was written from that rather than
/// a device — which is exactly why every one of them must be read without throwing.
/// </summary>
public class PerVolumeTests
{
    private static XDocument Xml(string body) => XDocument.Parse($"<QDocRoot>{body}</QDocRoot>");

    // ---------- QNAP parsing ----------

    [Fact]
    public void QnapVolumesWithSizesInsideEachVolumeAreRead()
    {
        var volumes = QnapProvider.ReadVolumes(Xml("""
            <volumeList>
              <volume><volumeValue>1</volumeValue><volumeLabel>DataVol1</volumeLabel>
                <total_size>1000</total_size><free_size>100</free_size></volume>
              <volume><volumeValue>2</volumeValue><volumeLabel>Media</volumeLabel>
                <total_size>2000</total_size><free_size>1200</free_size></volume>
            </volumeList>
            """));

        Assert.Equal(["DataVol1", "Media"], volumes.Select(v => v.Label));
        Assert.Equal(["1", "2"], volumes.Select(v => v.Id));
        Assert.Equal(90, volumes[0].UsedPercent, 3);
        Assert.Equal(40, volumes[1].UsedPercent, 3);
    }

    [Fact]
    public void QnapVolumesWithSizesInASeparateUsageListAreJoinedByNumber()
    {
        // The shape qnapstats reads from the same endpoint: names in one list, sizes in
        // another, in a different order. The provider used to find no sizes in this at all.
        var volumes = QnapProvider.ReadVolumes(Xml("""
            <volumeList>
              <volume><volumeValue>1</volumeValue><volumeLabel>DataVol1</volumeLabel><volumeStatus>0</volumeStatus></volume>
              <volume><volumeValue>2</volumeValue><volumeLabel>Backups</volumeLabel><volumeStatus>0</volumeStatus></volume>
            </volumeList>
            <volumeUseList>
              <volumeUse><volumeValue>2</volumeValue><total_size>4000</total_size><free_size>3000</free_size></volumeUse>
              <volumeUse><volumeValue>1</volumeValue><total_size>1000</total_size><free_size>500</free_size></volumeUse>
            </volumeUseList>
            """));

        Assert.Equal(["DataVol1", "Backups"], volumes.Select(v => v.Label));
        Assert.Equal(50, volumes[0].UsedPercent, 3);
        Assert.Equal(25, volumes[1].UsedPercent, 3);
    }

    [Fact]
    public void QnapVolumesWithNothingBehindThemAreLeftOut()
    {
        // Unmounted or initialising: no size, a zero size, or sizes that are not numbers.
        var volumes = QnapProvider.ReadVolumes(Xml("""
            <volume><volumeValue>1</volumeValue><volumeLabel>Ready</volumeLabel>
              <total_size>1000</total_size><free_size>250</free_size></volume>
            <volume><volumeValue>2</volumeValue><volumeLabel>Initialising</volumeLabel>
              <total_size>0</total_size></volume>
            <volume><volumeValue>3</volumeValue><volumeLabel>Unmounted</volumeLabel></volume>
            <volume><volumeValue>4</volumeValue><volumeLabel>Odd</volumeLabel>
              <total_size>--</total_size><free_size>--</free_size></volume>
            <volume><volumeValue>1</volumeValue><volumeLabel>Ready again</volumeLabel>
              <total_size>1000</total_size><free_size>250</free_size></volume>
            """));

        var volume = Assert.Single(volumes);
        Assert.Equal("Ready", volume.Label);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<volumeList/>")]
    [InlineData("<volume/>")]
    [InlineData("<volume><volumeLabel>No number</volumeLabel></volume>")]
    [InlineData("<volumeUseList><volumeUse><total_size>5</total_size></volumeUse></volumeUseList>")]
    public void QnapVolumeParsingNeverThrows(string body)
    {
        var volumes = QnapProvider.ReadVolumes(Xml(body));
        Assert.Empty(volumes);
    }

    [Fact]
    public void QnapUsageWithoutAVolumeListStillCounts()
    {
        var volume = Assert.Single(QnapProvider.ReadVolumes(Xml("""
            <volumeUseList><volumeUse><volumeValue>3</volumeValue><total_size>100</total_size><free_size>10</free_size></volumeUse></volumeUseList>
            """)));

        Assert.Equal("Volume 3", volume.Label);
        Assert.Equal("3", volume.Id);
    }

    [Fact]
    public void QnapVolumeWithNoLabelIsNamedByItsNumber()
    {
        var volume = Assert.Single(QnapProvider.ReadVolumes(Xml(
            "<volume><volumeValue>2</volumeValue><total_size>100</total_size><free_size>10</free_size></volume>")));
        Assert.Equal("Volume 2", volume.Label);
    }

    // ---------- Naming ----------

    [Fact]
    public void AQnapVolumeIsKeyedByItsNumberSoARenameKeepsItsHistory()
    {
        var before = QnapProvider.VolumeSeries([new QnapProvider.VolumeInfo("Media", 100, 50) { Id = "2" }]);
        var after = QnapProvider.VolumeSeries([new QnapProvider.VolumeInfo("Films", 100, 50) { Id = "2" }]);

        Assert.Equal("disk_percent:vol2", Assert.Single(before).Key);
        Assert.Equal("disk_percent:vol2", Assert.Single(after).Key);
        Assert.Equal("Films", after[0].Name);
        Assert.Equal(50, after[0].Percent, 3);
    }

    [Fact]
    public void AVolumeWithNoIdIsKeyedByItsName()
    {
        var series = QnapProvider.VolumeSeries([new QnapProvider.VolumeInfo("My Photos (RAID 1)", 100, 50)]);
        Assert.Equal("disk_percent:my-photos-raid-1", Assert.Single(series).Key);
    }

    [Theory]
    [InlineData("volume_1", "volume_1")]
    [InlineData("  Media Library ", "media-library")]
    [InlineData("Ünïcode/Share", "n-code-share")]
    [InlineData("***", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void SlugsAreKeySafe(string? raw, string? expected) =>
        Assert.Equal(expected, VolumeMetric.Slug(raw));

    [Fact]
    public void ALongNameIsCutToAReadableKey()
    {
        var slug = VolumeMetric.Slug(new string('a', 100))!;
        Assert.Equal(VolumeMetric.MaxSlugLength, slug.Length);
    }

    [Fact]
    public void VolumesAreCappedInListingOrder()
    {
        var readings = Enumerable.Range(1, 40)
            .Select(i => new VolumeMetric.Reading($"v{i}", $"Volume {i}", 100, 100 - i))
            .ToList();

        var series = VolumeMetric.Select("disk_percent", readings);

        Assert.Equal(VolumeMetric.MaxPerConnection, series.Count);
        // The first sixteen listed, not the sixteen fullest, so the set is the same every probe.
        Assert.Equal("disk_percent:v1", series[0].Key);
        Assert.Equal($"disk_percent:v{VolumeMetric.MaxPerConnection}", series[^1].Key);
    }

    [Fact]
    public void EmptyAndSystemVolumesAreNotRecorded()
    {
        var series = VolumeMetric.Select("disk_percent",
        [
            new("tank", "tank", 100, 40),
            new("boot-pool", "boot-pool", 100, 20),
            new("empty", "empty", 0, 0),
            new("nan", "nan", double.NaN, 1),
            new(null, null, 100, 10),
        ]);

        Assert.Equal(["disk_percent:tank"], series.Select(s => s.Key));
    }

    [Fact]
    public void TwoVolumesThatSlugAlikeKeepSeparateKeys()
    {
        var series = VolumeMetric.Select("disk_percent",
        [
            new(null, "Media!", 100, 10),
            new(null, "Media?", 100, 20),
        ]);

        Assert.Equal(["disk_percent:media", "disk_percent:media-2"], series.Select(s => s.Key));
    }

    [Fact]
    public void APercentOutsideTheScaleIsClamped()
    {
        var series = VolumeMetric.Select("disk_percent", [new("a", "A", 100, 130), new("b", "B", 100, -5)]);
        Assert.Equal([100d, 0d], series.Select(s => s.Percent));
    }

    [Theory]
    [InlineData("disk_percent:vol2", true, "disk_percent", "vol2")]
    [InlineData("disk_percent", false, "", "")]
    [InlineData("days_until_full:disk_percent", false, "", "")]
    [InlineData("days_until_full:disk_percent:vol2", false, "", "")]
    [InlineData(":vol2", false, "", "")]
    [InlineData("disk_percent:", false, "", "")]
    public void AVolumeKeyIsToldApartFromAForecastKey(string key, bool parses, string metric, string volume)
    {
        Assert.Equal(parses, VolumeMetric.TryParse(key, out var measured, out var name));
        Assert.Equal(metric, measured);
        Assert.Equal(volume, name);
    }

    [Fact]
    public void AForecastOfOneVolumeNamesTheVolume()
    {
        Assert.True(CapacityMetric.TryParse(CapacityMetric.KeyFor("disk_percent:vol2"), out var measured));
        Assert.Equal("disk_percent:vol2", measured);
    }

    [Theory]
    [InlineData("vol2", "Volume 2")]
    [InlineData("volume_1", "Volume 1")]
    [InlineData("volume1", "Volume 1")]
    [InlineData("3", "Volume 3")]
    [InlineData("tank", "Tank")]
    [InlineData("media-library", "Media library")]
    public void AnUnnamedVolumeIsNamedFromItsKey(string volume, string expected) =>
        Assert.Equal(expected, VolumeMetric.FallbackName(volume));

    // ---------- Specs and the registry ----------

    /// <summary>The real registry, with every built-in provider.</summary>
    private static Registry Registry()
    {
        var directory = TestHost.TempDirectory();
        var services = TestHost.Build(directory);
        try
        {
            return services.GetRequiredService<Registry>();
        }
        finally
        {
            TestHost.Teardown(services, directory);
        }
    }

    [Fact]
    public void AVolumeInheritsItsMetricsUnitAndCapacity()
    {
        var registry = Registry();
        var nas = new Connection { Provider = "qnap", Name = "NAS" };

        var spec = registry.Metric(nas, "disk_percent:vol2");

        Assert.Equal("Volume 2", spec.Label);
        Assert.Equal("%", spec.Unit);
        Assert.Equal(1, spec.Decimals);
        Assert.Equal("disk_percent:vol2", spec.Key);
        Assert.Equal(CapacityLimit.Percent, registry.CapacityOf(nas, "disk_percent:vol2"));
    }

    [Fact]
    public void AVolumesForecastIsLabelledAfterTheVolume()
    {
        var registry = Registry();
        var nas = new Connection { Provider = "synology", Name = "NAS" };

        var spec = registry.Metric(nas, CapacityMetric.KeyFor("disk_percent:volume_2"));

        Assert.Equal("Volume 2: days until full", spec.Label);
        Assert.Equal(" days", spec.Unit);
        Assert.Null(registry.CapacityOf(nas, CapacityMetric.KeyFor("disk_percent:volume_2")));
    }

    [Fact]
    public void AVolumeFromAnyProviderIsACapacityWhenItsMetricIs()
    {
        // A JSON API connection or a plugin recording disk_percent:<something> declared
        // nothing about it, and it still fills towards 100.
        var registry = Registry();

        Assert.NotNull(registry.CapacityOf(new Connection { Provider = "json" }, "disk_percent:scratch"));
        Assert.NotNull(registry.CapacityOf(null, "disk_percent:scratch"));
        Assert.Null(registry.CapacityOf(null, "cpu_percent:core0"));
        Assert.Equal("Scratch", MetricSpec.Fallback("disk_percent:scratch").Label);
        Assert.Equal("%", MetricSpec.Fallback("disk_percent:scratch").Unit);
    }

    [Fact]
    public void TheAggregateKeepsItsLabel()
    {
        var registry = Registry();
        var nas = new Connection { Provider = "qnap", Name = "NAS" };
        Assert.Equal("Fullest volume", registry.Metric(nas, "disk_percent").Label);
    }

    [Fact]
    public void SynologyVolumesAreReadFromLoadInfo()
    {
        using var document = JsonDocument.Parse("""
            {
              "volumes": [
                { "id": "volume_1", "vol_path": "/volume1", "status": "normal",
                  "size": { "total": "1000", "used": "900" } },
                { "id": "volume_2", "vol_path": "/volume2", "display_name": "Media",
                  "size": { "total": "4000", "used": "1600" } },
                { "id": "volume_3", "status": "crashed" },
                { "vol_path": "/volume4", "size": { "total": "0", "used": "0" } },
                { "vol_path": "/volume5", "size": { "total": 200, "used": 50 } },
                "not an object"
              ]
            }
            """);

        var series = VolumeMetric.Select("disk_percent", SynologyProvider.ReadVolumes(document.RootElement));

        // Keyed by DSM's id even where there is a display name, so renaming keeps history;
        // the mount path stands in for an id that is missing.
        Assert.Equal(["disk_percent:volume_1", "disk_percent:volume_2", "disk_percent:volume5"], series.Select(s => s.Key));
        Assert.Equal(["Volume 1", "Media", "Volume 5"], series.Select(s => s.Name));
        Assert.Equal([90d, 40d, 25d], series.Select(s => s.Percent));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "volumes": {} }""")]
    [InlineData("[]")]
    [InlineData("null")]
    public void SynologyVolumeParsingNeverThrows(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Empty(SynologyProvider.ReadVolumes(document.RootElement));
    }

    [Fact]
    public void TrueNasPoolsAreReadByName()
    {
        using var document = JsonDocument.Parse("""
            [
              { "id": 1, "name": "tank", "status": "ONLINE", "allocated": 800, "free": 200 },
              { "id": 2, "name": "fast", "status": "DEGRADED", "allocated": 100, "free": 300 },
              { "id": 3, "name": "offline", "status": "OFFLINE" }
            ]
            """);

        var (fullest, count, degraded, worst, pools) = TrueNasProvider.ReadPools(document.RootElement);
        var series = VolumeMetric.Select("disk_percent", pools);

        Assert.Equal(80, fullest, 3);
        Assert.Equal(3, count);
        Assert.Equal(2, degraded);
        Assert.Equal("offline", worst);
        Assert.Equal(["disk_percent:tank", "disk_percent:fast"], series.Select(s => s.Key));
        Assert.Equal(["tank", "fast"], series.Select(s => s.Name));
    }

    // ---------- Which forecasts are shown ----------

    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static CapacityForecasts.Entry Entry(string metric, ForecastState state, double? days = null, string connection = "nas") =>
        new(connection, metric,
            new CapacityForecast(state, 50, days, state == ForecastState.Filling ? 1 : null, ForecastConfidence.High, 0.95,
                TimeSpan.FromDays(14), null),
            Now);

    [Fact]
    public void ANasWithOneVolumeIsOneRow()
    {
        var rows = CapacityForecasts.Representatives(
        [
            Entry("disk_percent", ForecastState.Filling, 20),
            Entry("disk_percent:vol1", ForecastState.Filling, 20),
        ]);

        Assert.Equal(["disk_percent:vol1"], rows.Select(r => r.Metric));
    }

    [Fact]
    public void VolumesReplaceTheAggregateOnceTheyHaveAForecast()
    {
        var rows = CapacityForecasts.Representatives(
        [
            Entry("disk_percent", ForecastState.NotFilling),
            Entry("disk_percent:vol1", ForecastState.NotFilling),
            Entry("disk_percent:vol2", ForecastState.Filling, 21),
            Entry("disk_percent:vol3", ForecastState.NotEnoughHistory),
            Entry("free_gb", ForecastState.Filling, 40),
        ]);

        Assert.Equal(["disk_percent:vol1", "disk_percent:vol2", "disk_percent:vol3", "free_gb"], rows.Select(r => r.Metric));
    }

    [Fact]
    public void TheAggregateStandsInWhileVolumesAreStillLearning()
    {
        // The first two days after upgrading: the aggregate has a fortnight of history and
        // the volumes have none. Showing "not enough history" three times would hide the
        // one forecast there is.
        var rows = CapacityForecasts.Representatives(
        [
            Entry("disk_percent", ForecastState.Filling, 30),
            Entry("disk_percent:vol1", ForecastState.NotEnoughHistory),
            Entry("disk_percent:vol2", ForecastState.NotEnoughHistory),
        ]);

        Assert.Equal(["disk_percent"], rows.Select(r => r.Metric));
    }

    [Fact]
    public void VolumesWithNoAggregateAreKept()
    {
        var rows = CapacityForecasts.Representatives([Entry("disk_percent:vol1", ForecastState.NotEnoughHistory)]);
        Assert.Single(rows);
    }

    [Fact]
    public void DedupingIsPerConnection()
    {
        var rows = CapacityForecasts.Representatives(
        [
            Entry("disk_percent", ForecastState.Filling, 10, connection: "a"),
            Entry("disk_percent:vol1", ForecastState.Filling, 10, connection: "a"),
            Entry("disk_percent", ForecastState.Filling, 12, connection: "b"),
        ]);

        Assert.Equal([("a", "disk_percent:vol1"), ("b", "disk_percent")], rows.Select(r => (r.ConnectionId, r.Metric)));
    }

    [Fact]
    public void TheNasCardShowsTheSoonestFillingVolumesAndCountsTheRest()
    {
        var (shown, more) = CapacityForecasts.Filling(
        [
            Entry("disk_percent", ForecastState.Filling, 90),
            Entry("disk_percent:vol1", ForecastState.NotFilling),
            Entry("disk_percent:vol2", ForecastState.Filling, 21),
            Entry("disk_percent:vol3", ForecastState.Full, 0),
            Entry("disk_percent:vol4", ForecastState.Filling, 200),
        ], max: 2);

        Assert.Equal(["disk_percent:vol3", "disk_percent:vol2"], shown.Select(e => e.Metric));
        Assert.Equal(1, more);
    }

    [Fact]
    public void TheNasCardSaysNothingWhenNothingIsFilling()
    {
        var (shown, more) = CapacityForecasts.Filling(
        [
            Entry("disk_percent", ForecastState.NotFilling),
            Entry("disk_percent:vol1", ForecastState.NotFilling),
        ], max: 2);

        Assert.Empty(shown);
        Assert.Equal(0, more);
    }

    [Fact]
    public void EachVolumeGetsItsOwnForecast()
    {
        // Volume 1 flat at 90%, Volume 2 climbing from 40% by two points a day.
        var start = Now.AddDays(-14);
        var flat = Enumerable.Range(0, 14 * 24).Select(h => (start.AddHours(h), 90.0));
        var rising = Enumerable.Range(0, 14 * 24).Select(h => (start.AddHours(h), 40 + h / 24.0 * 2));

        var volume1 = CapacityForecast.Compute(flat, CapacityLimit.Percent, Now);
        var volume2 = CapacityForecast.Compute(rising, CapacityLimit.Percent, Now);

        Assert.Equal(ForecastState.NotFilling, volume1.State);
        Assert.Equal(ForecastState.Filling, volume2.State);
        Assert.InRange(volume2.DaysLeft!.Value, 14, 18);
    }
}
