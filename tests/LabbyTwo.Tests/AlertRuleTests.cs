using LabbyTwo.Core;

namespace LabbyTwo.Tests;

/// <summary>
/// The comparison logic on its own. Breaching and clearing are deliberately not each
/// other's inverse, and getting that wrong is how an alert either flaps every sweep or
/// fires once and never clears.
/// </summary>
public class AlertRuleTests
{
    private static AlertRule Above(double threshold, double? clear = null) =>
        new() { Metric = "disk_percent", Comparison = Comparison.Above, Threshold = threshold, ClearThreshold = clear };

    private static AlertRule Below(double threshold, double? clear = null) =>
        new() { Metric = "battery_percent", Comparison = Comparison.Below, Threshold = threshold, ClearThreshold = clear };

    [Theory]
    [InlineData(89.9, false)]
    [InlineData(90, true)]
    [InlineData(95, true)]
    public void AnAboveRuleBreachesAtOrOverTheThreshold(double value, bool expected)
        => Assert.Equal(expected, Above(90).IsBreaching(value));

    [Theory]
    [InlineData(20.1, false)]
    [InlineData(20, true)]
    [InlineData(5, true)]
    public void ABelowRuleBreachesAtOrUnderTheThreshold(double value, bool expected)
        => Assert.Equal(expected, Below(20).IsBreaching(value));

    [Fact]
    public void WithNoHysteresisTheRuleClearsAsSoonAsItStopsBreaching()
    {
        var rule = Above(90);
        Assert.True(rule.IsBreaching(90));
        Assert.False(rule.IsCleared(90));
        Assert.True(rule.IsCleared(89.9));
    }

    [Fact]
    public void BetweenTheThresholdsTheRuleIsNeitherBreachingNorCleared()
    {
        // 85–90 is the dead band: a disk sitting at 87 after firing stays fired, and one
        // climbing through 87 has not fired yet. That is what stops the flapping.
        var rule = Above(90, clear: 85);

        Assert.False(rule.IsBreaching(87));
        Assert.False(rule.IsCleared(87));

        Assert.True(rule.IsBreaching(91));
        Assert.True(rule.IsCleared(84));
    }

    [Fact]
    public void HysteresisWorksTheOtherWayRoundForABelowRule()
    {
        var rule = Below(20, clear: 30);

        Assert.True(rule.IsBreaching(15));
        Assert.False(rule.IsBreaching(25));
        Assert.False(rule.IsCleared(25));
        Assert.True(rule.IsCleared(31));
    }

    [Fact]
    public void ClearingDefaultsToTheThresholdWhenUnset()
    {
        Assert.Equal(90, Above(90).ClearsAt);
        Assert.Equal(85, Above(90, clear: 85).ClearsAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ARuleWatchingEverythingSaysSoRatherThanLeavingADanglingSeparator(string? noConnection)
    {
        // The UI looks a name up by id and gets "" for an unpinned rule, so only handling
        // null rendered "· Disk used above 90".
        Assert.Equal("Any connection · Disk used above 90", Above(90).Describe("Disk used", noConnection));
    }

    [Fact]
    public void AnUnnamedRuleDescribesItself()
    {
        Assert.Equal("NAS · Disk used above 90", Above(90).Describe("Disk used", "NAS"));
        Assert.Equal("My rule", (Above(90) with { Name = "My rule" }).Describe("Disk used", "NAS"));
    }

    [Fact]
    public void ARuleHoveringExactlyOnTheLineDoesNotOscillate()
    {
        // The realistic failure: a value that keeps landing on the threshold. With a dead
        // band it can never be both breaching and cleared, so state cannot flip per sweep.
        var rule = Above(90, clear: 88);
        foreach (var value in new[] { 90.0, 89.5, 90.0, 89.0, 90.1 })
            Assert.False(rule.IsBreaching(value) && rule.IsCleared(value));
    }
}
