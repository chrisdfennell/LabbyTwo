using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// Says so in the change feed when LabbyTwo loses sight of the lab, and when it gets it
/// back: one line at each end.
///
/// The notification for a long spell — worth sending even though every down alert is being
/// held, because a monitor that has quietly stopped seeing is exactly what nobody finds out
/// about until they needed it — is <see cref="SelfWatch"/>'s now, beside LabbyTwo's other
/// news about itself. Same rule as before (once a spell has lasted
/// <see cref="BlindnessRules.NotifyAfter"/>, once per spell), plus a notice when it ends,
/// and held by maintenance, mute windows and quiet hours like the rest.
/// </summary>
public sealed class BlindnessWatcher(
    HealthMonitor monitor,
    ChangeStore changes,
    ILogger<BlindnessWatcher> log) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        monitor.BlindnessChanged += OnChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        monitor.BlindnessChanged -= OnChanged;
        return Task.CompletedTask;
    }

    private void OnChanged(Blindness before, Blindness after) =>
        _ = Task.Run(() => RecordAsync(before, after, DateTimeOffset.Now, CancellationToken.None));

    /// <summary>The line in the feed for a start or an end. Public, with the clock as a parameter, for tests.</summary>
    public async Task RecordAsync(Blindness before, Blindness after, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            await changes.RecordAsync(Entry(before, after, now), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not record that LabbyTwo {What} the lab", after.Impaired ? "lost sight of" : "can see");
        }
    }

    /// <summary>What the feed says when sight is lost or comes back.</summary>
    public static Change Entry(Blindness before, Blindness after, DateTimeOffset now)
    {
        if (after.Impaired)
            return new Change(now, ChangeKinds.Monitor, ChangeActions.Down, null, "labbytwo",
                $"LabbyTwo can't see the lab: {after.Reason}", after.Detail);

        var lasted = before.Since is { } since && now > since ? $" after {Ago.Duration(now - since)}" : "";
        return new Change(now, ChangeKinds.Monitor, ChangeActions.Up, null, "labbytwo",
            $"LabbyTwo can see the lab again{lasted}",
            "Checks are being recorded again. Anything that really went down while it could not see is reported by " +
            "the next checks in the usual way.");
    }
}
