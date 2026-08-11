using LabbyTwo.Core;

namespace LabbyTwo.Tests;

/// <summary>
/// Quiet hours decide whether a phone rings at 3am, so the wrapping window and the "which
/// alerts still get through" rule are worth pinning down. Both are easy to get subtly
/// wrong and impossible to notice you have.
/// </summary>
public class AlertPolicyTests
{
    private static DateTimeOffset At(int hour, int minute = 0) =>
        new(new DateTime(2026, 8, 11, hour, minute, 0, DateTimeKind.Local));

    private static AlertPolicy Overnight(string mode = AlertPolicy.DownOnly) =>
        new(new TimeOnly(23, 0), new TimeOnly(7, 0), mode);

    [Fact]
    public void OffWhenBothEndsMatch()
    {
        var policy = AlertPolicy.Default;

        Assert.False(policy.QuietHoursOn);
        Assert.False(policy.IsQuiet(At(3)));
        Assert.True(policy.Allows(new Alert(AlertLevel.Info, "any", ""), At(3)));
    }

    [Theory]
    [InlineData(23, 30, true)]   // after the start, before midnight
    [InlineData(2, 0, true)]     // the far side of midnight
    [InlineData(6, 59, true)]    // the last minute
    [InlineData(7, 0, false)]    // the end is exclusive
    [InlineData(12, 0, false)]
    [InlineData(22, 59, false)]
    public void TheWindowWrapsMidnight(int hour, int minute, bool quiet)
        => Assert.Equal(quiet, Overnight().IsQuiet(At(hour, minute)));

    [Fact]
    public void DownStillGetsThroughButRecoveryDoesNot()
    {
        var policy = Overnight();

        Assert.True(policy.Allows(new Alert(AlertLevel.Down, "NAS is down", ""), At(3)));

        // Being woken to be told something came back is the purest pointless alert.
        Assert.False(policy.Allows(new Alert(AlertLevel.Up, "NAS is back", ""), At(3)));
        Assert.False(policy.Allows(new Alert(AlertLevel.Info, "FYI", ""), At(3)));
    }

    [Fact]
    public void NothingModeHoldsEvenAnOutage()
    {
        var policy = Overnight(AlertPolicy.Nothing);
        Assert.False(policy.Allows(new Alert(AlertLevel.Down, "NAS is down", ""), At(3)));
    }

    [Fact]
    public void EverythingGoesOutOnceTheWindowEnds()
    {
        var policy = Overnight(AlertPolicy.Nothing);
        Assert.True(policy.Allows(new Alert(AlertLevel.Up, "NAS is back", ""), At(9)));
    }

    [Fact]
    public void UnreadableTimesTurnItOffRatherThanGuessing()
    {
        // A blank or malformed setting must not silently choose midnight for somebody.
        var policy = AlertPolicy.From(new SettingsBag
        {
            [AlertPolicy.FromKey] = "",
            [AlertPolicy.ToKey] = "not a time",
        });

        Assert.False(policy.QuietHoursOn);
    }

    [Fact]
    public void SilenceIsAMomentNotAFlag()
    {
        var connection = new Connection { SilencedUntil = At(4) };

        Assert.True(connection.IsSilenced(At(3, 59)));
        Assert.False(connection.IsSilenced(At(4, 1)));
        Assert.False(new Connection().IsSilenced(At(3)));
    }
}
