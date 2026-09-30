using LabbyTwo.Core;

namespace LabbyTwo.Services;

/// <summary>
/// Whether LabbyTwo can see the lab — the monitor's judgement of itself, made once per
/// sweep before any probe is recorded. The rules are <see cref="BlindnessRules"/>; this
/// only keeps the answer and says when it changes.
///
/// Everything that acts on a failure asks this first, or never hears of the failure at
/// all: while it holds, a failed probe keeps its connection's last known state (see
/// <see cref="HealthMonitor.ProbeState.CantCheck"/>) and so raises no
/// <see cref="HealthMonitor.StatusChanged"/> — which is what alerting, the change feed,
/// incidents and self-healing are all driven by. The ones with a clock of their own —
/// threshold rules, safe updates — check <see cref="HealthMonitor.Blindness"/> themselves.
/// </summary>
public sealed partial class HealthMonitor
{
    private Blindness _blindness = Blindness.Clear;

    /// <summary>As of the last sweep. <see cref="Blindness.Clear"/> until one says otherwise.</summary>
    public Blindness Blindness => Volatile.Read(ref _blindness);

    /// <summary>True while LabbyTwo cannot see the lab. Shorthand for the many places that only need yes or no.</summary>
    public bool IsBlind => Blindness.Impaired;

    /// <summary>
    /// Raised when LabbyTwo stops being able to see, or starts again — the transition only,
    /// with the state before and after. Listeners must be quick and must not throw.
    /// </summary>
    public event Action<Blindness, Blindness>? BlindnessChanged;

    /// <summary>One sweep, now, as the timer would run it. For tests, which cannot wait for the timer.</summary>
    public Task SweepOnceAsync(CancellationToken ct = default) => TimedSweepAsync(ct);

    /// <summary>Judges a sweep, keeps the verdict, and announces a change of it.</summary>
    private Blindness Judge(IReadOnlyCollection<ProbeOutcome> sweep, TimeSpan? lastSweep, DateTimeOffset now)
    {
        var period = TimeSpan.FromSeconds(Math.Clamp(options.Value.ProbeSeconds, 5, 3600));
        var before = Blindness;
        var after = BlindnessRules.Assess(before, sweep, lastSweep, period, now);
        Volatile.Write(ref _blindness, after);

        if (before.Impaired == after.Impaired)
            return after;

        if (after.Impaired)
            log.LogWarning("LabbyTwo cannot see the lab: {Reason} ({Blind} of {Probed} checks). Failures are held until it clears",
                after.Reason, after.Blind, after.Probed);
        else
            log.LogInformation("LabbyTwo can see the lab again, after {Minutes:0} min", (now - (before.Since ?? now)).TotalMinutes);

        try
        {
            BlindnessChanged?.Invoke(before, after);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "A blindness listener threw");
        }
        return after;
    }
}
