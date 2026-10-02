namespace LabbyTwo.Services;

/// <summary>
/// Turns the lights on at sunset: sleeps until the next moment any screen's light/dark mode
/// changes by itself ("follow the sun", "on a schedule"), then calls
/// <see cref="ThemeService.Refresh"/> so ThemeSync pushes the new data-theme to every open
/// page — the wall that nobody reloads included.
///
/// <para>Not a minute-ticking job, though that would have worked: it wakes once per switch,
/// which is two or four times a day, and the switch lands on the second rather than up to a
/// minute late. It also wakes when anything changes the look — a new mode, a moved home,
/// different times — and works the next switch out again. And it never sleeps longer than
/// an hour, so a server clock that jumps (a laptop waking, a VM restored) or a zone the host
/// changed is caught within the hour without anything having to notice it.</para>
///
/// <para>Pages rendered fresh do not depend on this: ThemeService re-decides a cached mode
/// once its switch has passed. This exists for the pages that are already open.</para>
/// </summary>
public sealed class ThemeScheduler(ThemeService themes, ILogger<ThemeScheduler> log, TimeProvider? clock = null) : BackgroundService
{
    private static readonly TimeSpan LongestSleep = TimeSpan.FromHours(1);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private TaskCompletionSource _wake = NewSignal();

    /// <summary>The switch it is waiting for, for tests and anybody curious. Null when nothing is automatic.</summary>
    public DateTimeOffset? Waiting { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        themes.Changed += Wake;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Taken before working anything out, so a change that lands while the next
                // switch is being computed still wakes the sleep below.
                var fresh = NewSignal();
                Volatile.Write(ref _wake, fresh);
                var signal = fresh.Task;

                DateTimeOffset? next;
                try
                {
                    next = await StepAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Settings unreadable for a moment — try again in a minute rather than stop.
                    log.LogWarning(ex, "Could not work out the next light/dark switch");
                    next = _clock.GetUtcNow().AddMinutes(1);
                }

                var wait = next is { } at ? at - _clock.GetUtcNow() : LongestSleep;
                wait = wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait > LongestSleep ? LongestSleep : wait;

                // The sleep is cancelled when a change wakes the loop early, so a morning of
                // settings saves does not leave an hour-long timer behind for each one.
                using var sleep = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var delay = Task.Delay(wait, _clock, sleep.Token);
                await Task.WhenAny(delay, signal);
                await sleep.CancelAsync();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            themes.Changed -= Wake;
        }
    }

    /// <summary>
    /// One look at the clock: if a switch is due, tell every open page, then return the one
    /// after it. The loop above is only this and a sleep, so this is what the tests drive.
    /// </summary>
    public async Task<DateTimeOffset?> StepAsync(CancellationToken ct = default)
    {
        var next = await themes.NextSwitchAsync(ct);

        // NextSwitchAsync only ever returns a moment after now, so "due" means the clock has
        // passed the one we were waiting for since it was worked out.
        if (Waiting is { } waited && waited <= _clock.GetUtcNow())
        {
            log.LogDebug("Light/dark switch due at {At}; refreshing open pages", waited);
            themes.Refresh();
        }

        Waiting = next;
        return next;
    }

    private void Wake() => Volatile.Read(ref _wake).TrySetResult();

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
