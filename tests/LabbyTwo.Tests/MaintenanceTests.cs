using System.Globalization;
using LabbyTwo.Core;

namespace LabbyTwo.Tests;

/// <summary>
/// A maintenance window decides whether a whole installation stays quiet, so the thing
/// worth pinning down is that it ends. Expiry is computed on read rather than by anything
/// that ticks, which is what makes a window survive a restart and still lift on time —
/// and also what makes "it never came back" a silent, months-long failure if it is wrong.
/// </summary>
public class MaintenanceTests
{
    private static readonly DateTimeOffset Now =
        new(new DateTime(2026, 8, 16, 14, 0, 0, DateTimeKind.Local));

    private static SettingsBag Bag(string? value) =>
        value is null ? new SettingsBag() : new SettingsBag { [Maintenance.Key] = value };

    [Fact]
    public void OffWhenNothingIsStored()
    {
        var maintenance = Maintenance.From(Bag(null), Now);

        Assert.False(maintenance.On);
        Assert.Null(maintenance.Reason);
    }

    [Fact]
    public void OffOnceTheWindowHasPassed()
    {
        var lapsed = Now.AddMinutes(-1).ToString("o", CultureInfo.InvariantCulture);

        Assert.False(Maintenance.From(Bag(lapsed), Now).On);
    }

    [Fact]
    public void OnWhileTheWindowIsOpen()
    {
        var open = Now.AddMinutes(30).ToString("o", CultureInfo.InvariantCulture);

        var maintenance = Maintenance.From(Bag(open), Now);

        Assert.True(maintenance.On);
        Assert.NotNull(maintenance.Until);
    }

    [Fact]
    public void IndefiniteStaysOnHoweverLongItHasBeen()
    {
        var maintenance = Maintenance.From(Bag(Maintenance.Indefinite), Now.AddYears(1));

        Assert.True(maintenance.On);
        Assert.Null(maintenance.Until);
        Assert.Equal("all alerts are silenced", maintenance.Reason);
    }

    /// <summary>
    /// A value nobody can read must not mean "stay silent". Failing open is the only safe
    /// direction here: the cost of a wrong guess is either one unwanted alert or every
    /// alert lost, and only one of those is recoverable.
    /// </summary>
    [Theory]
    [InlineData("not a date")]
    [InlineData("")]
    public void RubbishMeansOffRatherThanSilent(string stored)
        => Assert.False(Maintenance.From(Bag(stored), Now).On);

    [Fact]
    public void ValueRoundTripsThroughTheSetting()
    {
        var stored = Maintenance.Value(TimeSpan.FromHours(4), Now);

        var maintenance = Maintenance.From(Bag(stored), Now);

        Assert.True(maintenance.On);
        Assert.Equal(Now.AddHours(4), maintenance.Until!.Value);
    }

    [Fact]
    public void ValueForNoEndIsTheIndefiniteMarker()
        => Assert.Equal(Maintenance.Indefinite, Maintenance.Value(null, Now));

    [Fact]
    public void ClearedTurnsItOff()
        => Assert.False(Maintenance.From(Bag(Maintenance.Cleared), Now).On);
}
