using System.Collections.Concurrent;
using System.Diagnostics;
using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// Runs <see cref="ScheduledAction"/>s when their time comes. Ticked once a minute by
/// <see cref="ScheduledActionJob"/>; everything it decides is <see cref="ScheduledRules"/>.
///
/// Cheap when nothing is due, which is almost every minute. The actions and when each is
/// next due live in memory, built from the store's own in-memory copy and rebuilt only when
/// somebody saves or deletes one; a tick compares a handful of instants with the clock and
/// returns. The database is touched only when something is actually due.
///
/// The guardrails, in the order they are checked before anything runs:
/// <list type="number">
/// <item>The installation switch (<see cref="ScheduledSettings.Enabled"/>) is on. Off, due
/// runs are passed over silently — switching back on never runs a backlog.</item>
/// <item>The moment is recent: within the grace period (<see cref="ScheduledSettings.GraceMinutes"/>),
/// or it is recorded as missed. Only the latest missed moment is ever considered, so
/// however long LabbyTwo was down, it runs at most once when it comes back.</item>
/// <item>The same action is not still running from last time. It never runs twice at once;
/// the moment that found it busy is recorded as skipped.</item>
/// <item>The lab is not in maintenance, unless the action says it may run anyway
/// (<see cref="ScheduledAction.RunInMaintenance"/>).</item>
/// <item>The target may be acted on — never LabbyTwo's own container; a protected container
/// or a dangerous action only with the opt-in; never an action that asks a question. These
/// are self-healing's own checks (<see cref="RemediationGuards"/>), run by the same code.</item>
/// </list>
///
/// Every run, skip and miss goes into the change feed as <see cref="ChangeKinds.Scheduled"/>
/// against the connection it acted on, and into the action's own short history. A failure
/// is sent to the alert channels when the action asks for that, held by whatever holds an
/// alert: maintenance, a silence, a mute window covering the connection, quiet hours.
///
/// "Run now" goes through the same path, guardrails and all, so pressing it is an honest
/// rehearsal of what the schedule will do — except that maintenance does not hold it, since
/// the person pressing it is the one doing the maintenance.
/// </summary>
public sealed class ScheduledActions : IDisposable
{
    private readonly ScheduledActionStore _store;
    private readonly IScheduledActionPlans _plans;
    private readonly AppSettingsStore _settings;
    private readonly AlertService _alerts;
    private readonly ConfigStore _config;
    private readonly ChangeStore _changes;
    private readonly ILogger<ScheduledActions> _log;

    /// <summary>What is due when, by action id. Replaced whole on a rebuild, changed one entry at a time by a tick.</summary>
    private ConcurrentDictionary<string, Slot>? _slots;
    private volatile bool _dirty = true;
    private readonly SemaphoreSlim _build = new(1, 1);

    /// <summary>The runs in progress, by action id — what stops one action running twice at once.</summary>
    private readonly ConcurrentDictionary<string, Task<ScheduledRun>> _running = new();

    /// <summary>Cancelled when the app stops, so a run in progress is not left holding a socket.</summary>
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>One action and when it is next due; null when it never is (off, or incomplete).</summary>
    private sealed record Slot(ScheduledAction Action, DateTimeOffset? Next);

    public ScheduledActions(
        ScheduledActionStore store,
        IScheduledActionPlans plans,
        AppSettingsStore settings,
        AlertService alerts,
        ConfigStore config,
        ChangeStore changes,
        ILogger<ScheduledActions> log)
    {
        _store = store;
        _plans = plans;
        _settings = settings;
        _alerts = alerts;
        _config = config;
        _changes = changes;
        _log = log;
        _store.Changed += OnStoreChanged;
    }

    /// <summary>Raised after a run finishes, so an open page can show it without polling.</summary>
    public event Action<ScheduledRun>? Ran;

    /// <summary>The zone schedules are read in: the alert service's, which is the machine's.</summary>
    public TimeZoneInfo Zone => _alerts.Zone;

    private void OnStoreChanged() => _dirty = true;

    public void Dispose()
    {
        _store.Changed -= OnStoreChanged;
        _stopping.Cancel();
        _stopping.Dispose();
    }

    // ---- the tick ---------------------------------------------------------------------

    /// <summary>
    /// One tick at <paramref name="now"/>. Public so tests move the clock rather than wait for
    /// it. Starts whatever is due and returns without waiting for it to finish — a restart
    /// that takes a minute must not hold up the next tick, or another action due meanwhile.
    /// </summary>
    public async Task TickAsync(DateTimeOffset now, CancellationToken ct)
    {
        var slots = await SlotsAsync(now, ct);

        // The whole of a quiet minute: a few comparisons in memory.
        var due = slots.Values.Where(s => s.Next is { } next && next <= now).ToList();
        if (due.Count == 0)
            return;

        var global = ScheduledSettings.From(await _settings.AllAsync(ct));
        foreach (var slot in due)
        {
            try
            {
                await DueAsync(slot.Action, global, now, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One action that cannot be dealt with must not stop the rest this minute.
                _log.LogError(ex, "Scheduled action {Name} could not be started", slot.Action.Name);
            }
        }
    }

    private async Task DueAsync(ScheduledAction action, ScheduledSettings global, DateTimeOffset now, CancellationToken ct)
    {
        if (!global.Enabled)
        {
            // Passed over without a word: switching scheduled actions off is the answer to
            // "stop all of this", and switching them back on must not run a backlog.
            await CoverAsync(action, now, ct);
            return;
        }

        var decision = ScheduledRules.Decide(action, now, Zone, global.Grace);
        switch (decision.Step)
        {
            case ScheduledRules.Step.Wait:
                Replace(action, decision.Next);
                return;

            case ScheduledRules.Step.Missed:
                await CoverAsync(action, now, ct);
                await SkippedAsync(action, now,
                    $"Its {Clock(decision.Occurrence ?? now)} run came while LabbyTwo was not running, more than " +
                    $"{Ago.Duration(global.Grace)} ago — too late to run it now.",
                    "Missed", ct);
                return;
        }

        // Covered before it starts, and stored: if LabbyTwo stops half way through a
        // restart, it does not run it again when it comes back.
        await CoverAsync(action, now, ct);

        var trigger = decision.Late ? ScheduledTriggers.CatchUp : ScheduledTriggers.Schedule;
        if (Start(action, trigger, now) is null)
        {
            await SkippedAsync(action, now,
                "The last run was still going, so this one was skipped rather than run on top of it.", null, ct);
        }
    }

    /// <summary>
    /// Starts a run in the background, or returns null when this action is already running.
    /// The run is registered before it starts, so two callers racing for the same action
    /// cannot both get in.
    /// </summary>
    private Task<ScheduledRun>? Start(ScheduledAction action, string trigger, DateTimeOffset now)
    {
        var done = new TaskCompletionSource<ScheduledRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_running.TryAdd(action.Id, done.Task))
            return null;

        _ = Task.Run(async () =>
        {
            ScheduledRun? run = null;
            Exception? failure = null;
            try
            {
                run = await ExecuteAsync(action, trigger, now, _stopping.Token);
            }
            catch (Exception ex)
            {
                failure = ex;
                _log.LogError(ex, "Scheduled action {Name} failed unexpectedly", action.Name);
            }
            finally
            {
                // Gone from the running set before anybody waiting hears it finished, so
                // "Run now" pressed straight after a run is not told it is still going.
                _running.TryRemove(action.Id, out _);
            }

            if (run is not null)
            {
                done.SetResult(run);
                Ran?.Invoke(run);
            }
            else
            {
                done.SetException(failure ?? new InvalidOperationException("The run ended without a result."));
            }
        });
        return done.Task;
    }

    /// <summary>
    /// One run: the maintenance hold, the target's guardrails, the call, and what is written
    /// afterwards. Never throws for anything an action can do wrong — that is a failed run.
    /// </summary>
    private async Task<ScheduledRun> ExecuteAsync(ScheduledAction action, string trigger, DateTimeOffset now, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        if (trigger != ScheduledTriggers.Manual && !action.RunInMaintenance
            && Maintenance.From(await _settings.AllAsync(ct), now) is { On: true } maintenance)
        {
            return await SkippedAsync(action, now,
                $"Held back: {maintenance.Reason}. Tick “Run during maintenance” if it should run anyway.", null, ct);
        }

        RemediationPlan plan;
        try
        {
            plan = await _plans.PrepareAsync(action, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            plan = RemediationPlan.Refuse("run its action", "Ran its action", ex.GetBaseException().Message);
        }

        ActionResult result;
        if (plan.Run is { } call)
        {
            try
            {
                result = await call(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                result = ActionResult.Failed(ex.GetBaseException().Message);
            }
        }
        else
        {
            // Refused by a guardrail. Nothing ran, but the schedule asked for something that
            // did not happen, and that is worth hearing about: a Sunday restart that is quietly
            // refused every week because the container was protected is a restart nobody gets.
            result = ActionResult.Failed(plan.Refused ?? "It could not be prepared.");
        }
        stopwatch.Stop();

        var run = await RecordRunAsync(new ScheduledRun(action.Id, now, trigger,
            result.Ok ? ScheduledOutcomes.Ok : ScheduledOutcomes.Failed, result.Message, stopwatch.Elapsed), ct);

        var how = trigger switch
        {
            ScheduledTriggers.Manual => " (Run now)",
            ScheduledTriggers.CatchUp => ", catching up after LabbyTwo was not running",
            _ => " on schedule",
        };

        if (result.Ok)
        {
            _log.LogInformation("Scheduled action {Name}: {Did}", action.Name, plan.Did);
            await FeedAsync(new Change(now, ChangeKinds.Scheduled, ChangeActions.Completed, Connection(action), action.Id,
                $"{plan.Did}{how}", $"“{action.Name}”. {result.Message}".Trim()), ct);
            return run;
        }

        _log.LogWarning("Scheduled action {Name} failed: {Message}", action.Name, result.Message);
        await FeedAsync(new Change(now, ChangeKinds.Scheduled, ChangeActions.Failed, Connection(action), action.Id,
            $"Could not {plan.Doing}{how}", $"“{action.Name}”: {result.Message}"), ct);

        // Whoever pressed "Run now" is looking at the answer already.
        if (trigger != ScheduledTriggers.Manual)
            await NotifyAsync(action, plan.Doing, result.Message, now, ct);
        return run;
    }

    /// <summary>
    /// A failed run, sent through the alert channels when the action asks for that. Held by
    /// anything that holds an alert about the connection it acted on — maintenance, a
    /// silence, LabbyTwo not being able to see the lab, a mute window covering it — and by
    /// quiet hours, which it never overrides: a failed scheduled restart is worth knowing
    /// about in the morning, not worth being woken for. The change feed has it either way.
    /// </summary>
    private async Task NotifyAsync(ScheduledAction action, string doing, string message, DateTimeOffset now, CancellationToken ct)
    {
        if (!action.NotifyOnFailure)
            return;

        try
        {
            if (Maintenance.From(await _settings.AllAsync(ct), now).On)
                return;
            if (await _config.ConnectionAsync(action.TargetConnectionId, ct) is { } connection)
            {
                if (await _alerts.SuppressedAsync(connection, false, now, ct) is { } held)
                {
                    _log.LogInformation("Not sending the failure of {Name}: {Why}", action.Name, held);
                    return;
                }
                if (_alerts.MutedBy(null, connection.Id, now) is { } window)
                {
                    _log.LogInformation("Not sending the failure of {Name}: muted by {Window}", action.Name, window.Name);
                    return;
                }
            }

            var alert = new Alert(AlertLevel.Info,
                $"Scheduled action failed · {action.Name}",
                $"LabbyTwo tried to {doing} at {Clock(now)}, but it failed: {message.TrimEnd('.')}.")
            {
                Tag = $"scheduled:{action.Id}",
                Link = "settings/scheduled",
            };
            await _alerts.BroadcastAsync(alert, action.ChannelId.Length > 0 ? [action.ChannelId] : null, now, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Could not send the failure of scheduled action {Name}", action.Name);
        }
    }

    /// <summary>A run that did not happen, and why — in the history and the feed, the same as one that did.</summary>
    /// <param name="verb">"Missed" for a moment passed over while down; null for "Did not …".</param>
    private async Task<ScheduledRun> SkippedAsync(ScheduledAction action, DateTimeOffset now, string why, string? verb, CancellationToken ct)
    {
        var doing = await SafeDescribeAsync(action, ct);
        var run = await RecordRunAsync(new ScheduledRun(action.Id, now, ScheduledTriggers.Schedule,
            ScheduledOutcomes.Skipped, why, TimeSpan.Zero), ct);
        await FeedAsync(new Change(now, ChangeKinds.Scheduled, ChangeActions.Skipped, Connection(action), action.Id,
            verb is null ? $"Did not {doing} on schedule" : $"{verb} “{action.Name}”", why), ct);
        _log.LogInformation("Scheduled action {Name} did not run: {Why}", action.Name, why);
        return run;
    }

    // ---- for the page -----------------------------------------------------------------

    /// <summary>
    /// "Run now": runs the action at once, through every guardrail but maintenance, and
    /// waits for the answer. Does not move the schedule — the next scheduled run still
    /// happens when it was going to.
    /// </summary>
    public async Task<ScheduledRun> RunNowAsync(string id, CancellationToken ct = default)
    {
        var action = (await SlotsAsync(DateTimeOffset.Now, ct)).TryGetValue(id, out var slot)
            ? slot.Action
            : throw new InvalidOperationException("That scheduled action no longer exists.");

        var now = DateTimeOffset.Now;
        if (Start(action, ScheduledTriggers.Manual, now) is not { } running)
        {
            return new ScheduledRun(id, now, ScheduledTriggers.Manual, ScheduledOutcomes.Skipped,
                "It is already running. Wait for that run to finish.", TimeSpan.Zero);
        }
        return await running.WaitAsync(ct);
    }

    /// <summary>Whether this action is running right now.</summary>
    public bool IsRunning(string id) => _running.ContainsKey(id);

    /// <summary>Waits for every run in progress. For tests, and for a shutdown that wants to be tidy.</summary>
    public async Task WhenIdleAsync()
    {
        while (_running.Values.ToList() is { Count: > 0 } runs)
        {
            try
            {
                await Task.WhenAll(runs);
            }
            catch (Exception)
            {
                // Already logged by the run itself.
            }
        }
    }

    /// <summary>The next <paramref name="count"/> times it will run, or none when it is off or incomplete.</summary>
    public IReadOnlyList<DateTimeOffset> Upcoming(ScheduledAction action, DateTimeOffset now, int count = 3) =>
        action.Enabled && action.Problem() is null ? action.Schedule.Upcoming(now, Zone, count) : [];

    /// <summary>
    /// Saves an action and starts its schedule from <paramref name="now"/>: an edit never runs
    /// an occurrence it "missed" a moment ago. Throws <see cref="InvalidOperationException"/>
    /// with a sentence for the page when it cannot be saved.
    /// </summary>
    public async Task SaveAsync(ScheduledAction action, DateTimeOffset now, CancellationToken ct = default)
    {
        if (action.Problem() is { } problem)
            throw new InvalidOperationException(problem);
        if (await _plans.SaveProblemAsync(action, ct) is { } refused)
            throw new InvalidOperationException(refused);

        await _store.SaveAsync(action with { Name = action.Name.Trim(), Container = action.Container.Trim(), CoveredUntil = now }, ct);
    }

    public Task DeleteAsync(string id, CancellationToken ct = default) => _store.DeleteAsync(id, ct);

    /// <summary>The recent automatic runs across every action, newest first.</summary>
    public Task<IReadOnlyList<Change>> RecentAsync(int days, int limit, DateTimeOffset now, CancellationToken ct = default) =>
        _changes.QueryAsync(new ChangeQuery(now.AddDays(-days), now.AddSeconds(1), [ChangeKinds.Scheduled], Limit: limit), ct);

    // ---- helpers ----------------------------------------------------------------------

    /// <summary>
    /// The in-memory schedule, rebuilt only after a save or delete. Each action's covered
    /// instant is the later of the stored one and the one in memory: the store's cached copy
    /// is not updated as runs move it on, so it can only ever be behind.
    /// </summary>
    private async Task<ConcurrentDictionary<string, Slot>> SlotsAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!_dirty && _slots is { } current)
            return current;

        await _build.WaitAsync(ct);
        try
        {
            if (!_dirty && _slots is { } built)
                return built;

            // Cleared before reading, so a save that lands during the read marks it dirty again.
            _dirty = false;
            var actions = await _store.AllAsync(ct);
            var previous = _slots;
            var next = new ConcurrentDictionary<string, Slot>();
            foreach (var stored in actions)
            {
                var covered = Later(stored.CoveredUntil, previous?.GetValueOrDefault(stored.Id)?.Action.CoveredUntil) ?? now;
                var action = stored with { CoveredUntil = covered };
                next[action.Id] = new Slot(action, NextOf(action));
            }
            _slots = next;
            return next;
        }
        finally
        {
            _build.Release();
        }
    }

    private DateTimeOffset? NextOf(ScheduledAction action) =>
        action.Enabled && action.Problem() is null && action.CoveredUntil is { } covered
            ? action.Schedule.NextAfter(covered, Zone)
            : null;

    /// <summary>Marks everything up to <paramref name="now"/> dealt with, in memory at once and then in the database.</summary>
    private async Task CoverAsync(ScheduledAction action, DateTimeOffset now, CancellationToken ct)
    {
        var covered = action with { CoveredUntil = now };
        Replace(covered, NextOf(covered));
        try
        {
            await _store.SetCoveredAsync(action.Id, now, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Memory has it, so this process will not run it twice; only a restart before
            // the next successful write could, and that is still bounded by the grace period.
            _log.LogWarning(ex, "Could not store how far {Name} has run", action.Name);
        }
    }

    private void Replace(ScheduledAction action, DateTimeOffset? next)
    {
        if (_slots is { } slots && slots.ContainsKey(action.Id))
            slots[action.Id] = new Slot(action, next);
    }

    private async Task<ScheduledRun> RecordRunAsync(ScheduledRun run, CancellationToken ct)
    {
        try
        {
            return await _store.RecordRunAsync(run, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The history is the record, not the mechanism: losing a row must not lose the run.
            _log.LogWarning(ex, "Could not record a run of scheduled action {Id}", run.ActionId);
            return run;
        }
    }

    private async Task FeedAsync(Change change, CancellationToken ct)
    {
        try
        {
            await _changes.RecordAsync(change, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Could not record \"{Title}\" in the change feed", change.Title);
        }
    }

    private async Task<string> SafeDescribeAsync(ScheduledAction action, CancellationToken ct)
    {
        try
        {
            return await _plans.DescribeAsync(action, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Could not describe a scheduled action");
            return $"run “{action.Name}”";
        }
    }

    private static string? Connection(ScheduledAction action) =>
        action.TargetConnectionId.Length > 0 ? action.TargetConnectionId : null;

    private static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;

    /// <summary>A time as the notices say it, in the zone schedules are read in.</summary>
    private string Clock(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Zone).ToString("ddd HH:mm");
}

/// <summary>
/// Ticks <see cref="ScheduledActions"/> once a minute — the runner's floor, and the finest
/// grain a schedule can ask for. At startup too: that first tick is what runs a moment missed
/// while LabbyTwo was down, if it is still within the grace period.
/// </summary>
public sealed class ScheduledActionJob(ScheduledActions actions) : IBackgroundJob
{
    public string Name => "scheduled-actions";

    public TimeSpan Interval => TimeSpan.FromMinutes(1);

    public bool RunAtStartup => true;

    public Task RunAsync(CancellationToken ct) => actions.TickAsync(DateTimeOffset.Now, ct);
}
