using LabbyTwo.Core;

namespace LabbyTwo.Tests;

/// <summary>
/// When an escalation is due, and where the recovery goes. The timing is pure, so the
/// cases a restart makes awkward — one due while the app was stopped, a repeat that should
/// not come as a burst — are settled here without a clock.
/// </summary>
public sealed class EscalationPolicyTests
{
    private static readonly DateTimeOffset Fired = DateTimeOffset.Parse("2026-01-01T10:00:00Z");

    [Fact]
    public void OffIsNeverDue()
    {
        Assert.Null(EscalationPolicy.Off.DueAt(Fired, null));
        Assert.False(EscalationPolicy.Off.On);
    }

    [Fact]
    public void TheFirstEscalationIsDueTheChosenTimeAfterItFired()
    {
        var policy = new EscalationPolicy(15, ["push"], 0);
        Assert.Equal(Fired.AddMinutes(15), policy.DueAt(Fired, null));
    }

    [Fact]
    public void WithoutARepeatItEscalatesOnce()
    {
        var policy = new EscalationPolicy(15, ["push"], 0);
        Assert.Null(policy.DueAt(Fired, Fired.AddMinutes(15)));
    }

    [Fact]
    public void ARepeatCountsFromTheLastOneSentSoMissedOnesNeverComeAsABurst()
    {
        var policy = new EscalationPolicy(15, ["push"], 30);

        Assert.Equal(Fired.AddMinutes(45), policy.DueAt(Fired, Fired.AddMinutes(15)));

        // The app was stopped for three hours: the escalation that goes when it comes back
        // is the one due, and the next is half an hour after that — not five in a row.
        var late = Fired.AddHours(3);
        Assert.Equal(late.AddMinutes(30), policy.DueAt(Fired, late));
    }

    [Fact]
    public void ARuleThatNeverChoseFollowsTheDefault()
    {
        var fallback = new EscalationPolicy(20, ["push"], 0);

        Assert.Same(fallback, EscalationPolicy.For(new AlertRule(), fallback));
        Assert.Same(fallback, EscalationPolicy.For(null, fallback));
    }

    [Fact]
    public void ARuleCanOptOutOrSetItsOwn()
    {
        var fallback = new EscalationPolicy(20, ["push"], 0);

        Assert.False(EscalationPolicy.For(new AlertRule { EscalateAfterMinutes = 0 }, fallback).On);

        var own = EscalationPolicy.For(
            new AlertRule { EscalateAfterMinutes = 5, EscalateTo = "sms, email", EscalateRepeatMinutes = 10 }, fallback);
        Assert.Equal(5, own.AfterMinutes);
        Assert.Equal(["sms", "email"], own.Channels);
        Assert.Equal(10, own.RepeatMinutes);
    }

    [Fact]
    public void TheDefaultSurvivesBeingStored()
    {
        var policy = new EscalationPolicy(15, ["a", "b"], 30);
        var read = EscalationPolicy.From(new SettingsBag(policy.ToSettings()));

        Assert.Equal(15, read.AfterMinutes);
        Assert.Equal(["a", "b"], read.Channels);
        Assert.Equal(30, read.RepeatMinutes);
        Assert.False(EscalationPolicy.From(new SettingsBag()).On);
    }

    // ---------- Where the recovery goes ----------

    private static FiringAlert Entry(params string[] escalatedTo) =>
        new("rule:r:c", "r", "c", Fired, Fired, null, escalatedTo.Length > 0 ? Fired : null, escalatedTo.Length,
            escalatedTo, "Title", "Body", null, 0);

    [Fact]
    public void ARecoveryGoesWhereTheAlertWentAndEverywhereItWasEscalatedTo()
    {
        Assert.Equal(["email", "push"], FiringAlert.RecoveryChannels("email", Entry("push")));
        Assert.Equal(["email"], FiringAlert.RecoveryChannels("email", Entry()));
        Assert.Equal(["email"], FiringAlert.RecoveryChannels("email", null));
    }

    [Fact]
    public void EveryChannelAnywhereMeansEveryChannel()
    {
        Assert.Null(FiringAlert.RecoveryChannels(null, Entry("push")));
        Assert.Null(FiringAlert.RecoveryChannels("email", Entry(FiringAlert.AllChannels)));
    }

    // ---------- Quiet hours ----------

    [Fact]
    public void DownOnlyQuietHoursLetAnEscalationThroughAndNothingHoldsIt()
    {
        var escalation = new Alert(AlertLevel.Down, "Still firing · NAS is down", "");
        var night = DateTimeOffset.Parse("2026-01-01T03:00:00Z");

        var downOnly = new AlertPolicy(new TimeOnly(23, 0), new TimeOnly(7, 0), AlertPolicy.DownOnly);
        var nothing = downOnly with { QuietMode = AlertPolicy.Nothing };

        Assert.True(downOnly.Allows(escalation, night, TimeZoneInfo.Utc));
        Assert.False(nothing.Allows(escalation, night, TimeZoneInfo.Utc));
        Assert.True(nothing.Allows(escalation, night.AddHours(5), TimeZoneInfo.Utc));
    }
}
