namespace LabbyTwo.Core;

/// <summary>Where one update started from the Containers tab has got to.</summary>
public enum SafeUpdateState
{
    /// <summary>Asked for; the container is still on its old image. Watchtower pulls first, which takes a while.</summary>
    Waiting,

    /// <summary>On the new image, and being watched until the watch is over.</summary>
    Watching,

    /// <summary>The watch ended with nothing wrong. The previous image is kept for a manual roll-back.</summary>
    Passed,

    /// <summary>Updated with safe update off: nothing watched, but the previous image is kept all the same.</summary>
    Updated,

    /// <summary>Being put back on the previous image right now.</summary>
    RollingBack,

    /// <summary>Put back on the previous image — by the watch, or by the button.</summary>
    RolledBack,

    /// <summary>Should have been put back, and could not be. Needs somebody.</summary>
    RollbackFailed,

    /// <summary>Still on the same image long after the update was asked for: Watchtower found nothing, or does not watch it.</summary>
    NotUpdated,

    /// <summary>Taken out of LabbyTwo's hands — replaced again or removed by somebody else — so nothing more is done.</summary>
    Abandoned,

    /// <summary>
    /// LabbyTwo could not look at it while its watch was running — the job stalled, Docker
    /// did not answer, or LabbyTwo could not see the lab — so it was neither passed nor
    /// failed. Left on the new image; the previous one is kept for a roll-back by hand.
    /// </summary>
    NotChecked,
}

/// <summary>
/// One container update started from the Containers tab, and everything needed to undo it:
/// the image it ran before, by id, by registry digest and by a tag LabbyTwo put on it so it
/// is not tidied away; and how the watch after the update is going.
/// </summary>
/// <param name="ContainerId">The container as it was before the update.</param>
/// <param name="Reference">The image reference it was created from — <c>lscr.io/linuxserver/sonarr:latest</c> —
/// which a roll-back points back at the old image and creates the container from again.</param>
/// <param name="PreviousImage">The image id it ran before.</param>
/// <param name="PreviousDigest"><c>repo@sha256:…</c> for that image, so it can be pulled again
/// if a Watchtower with cleanup on deleted it. Empty for a local build.</param>
/// <param name="RollbackTag"><c>labbytwo-rollback/sonarr:20260929-140201</c>, or empty if tagging was refused.</param>
/// <param name="Watch">Whether safe update was on for it.</param>
/// <param name="NewImage">The image it moved to, once it has.</param>
/// <param name="NewContainerId">The container Watchtower created, once it has.</param>
/// <param name="WatchFrom">When it was first seen on the new image.</param>
/// <param name="RestartBaseline">Docker's restart count for the new container when the watch started.</param>
/// <param name="Reason">Why it ended as it did, in words: what failed, why a roll-back could not be done.</param>
public sealed record SafeUpdate(
    long Id,
    string ConnectionId,
    string Container,
    string ContainerId,
    string Reference,
    string PreviousImage,
    string PreviousDigest,
    string RollbackTag,
    DateTimeOffset RequestedAt,
    bool Watch,
    TimeSpan WatchFor,
    SafeUpdateState State,
    string NewImage = "",
    string NewContainerId = "",
    DateTimeOffset? WatchFrom = null,
    int RestartBaseline = 0,
    DateTimeOffset? EndedAt = null,
    string Reason = "")
{
    /// <summary>When the watch ends, once it has started.</summary>
    public DateTimeOffset? WatchUntil => WatchFrom + WatchFor;

    /// <summary>Still something the watch job has to look at.</summary>
    public bool IsActive => State is SafeUpdateState.Waiting or SafeUpdateState.Watching or SafeUpdateState.RollingBack;

    /// <summary>
    /// Whether "Roll back to previous image" makes sense: the update happened, and nothing
    /// has already put it back. The caller also checks the container still runs the image
    /// the update moved it to — one recreated since on something else is not this update's.
    /// </summary>
    public bool CanRollBack =>
        State is SafeUpdateState.Watching or SafeUpdateState.Passed or SafeUpdateState.Updated or SafeUpdateState.RollbackFailed
            or SafeUpdateState.NotChecked &&
        NewImage.Length > 0 && PreviousImage.Length > 0;

    /// <summary>How the state is stored — a word, so the partial index can name the active ones.</summary>
    public static string Word(SafeUpdateState state) => state switch
    {
        SafeUpdateState.Waiting => "waiting",
        SafeUpdateState.Watching => "watching",
        SafeUpdateState.Passed => "passed",
        SafeUpdateState.Updated => "updated",
        SafeUpdateState.RollingBack => "rolling-back",
        SafeUpdateState.RolledBack => "rolled-back",
        SafeUpdateState.RollbackFailed => "rollback-failed",
        SafeUpdateState.NotUpdated => "not-updated",
        SafeUpdateState.NotChecked => "not-checked",
        _ => "abandoned",
    };

    public static SafeUpdateState Parse(string word) => word switch
    {
        "waiting" => SafeUpdateState.Waiting,
        "watching" => SafeUpdateState.Watching,
        "passed" => SafeUpdateState.Passed,
        "updated" => SafeUpdateState.Updated,
        "rolling-back" => SafeUpdateState.RollingBack,
        "rolled-back" => SafeUpdateState.RolledBack,
        "rollback-failed" => SafeUpdateState.RollbackFailed,
        "not-updated" => SafeUpdateState.NotUpdated,
        "not-checked" => SafeUpdateState.NotChecked,
        _ => SafeUpdateState.Abandoned,
    };

    /// <summary>
    /// What the Containers tab says about it, or null when it is not worth a badge — an
    /// update that passed long ago is simply the container's normal state.
    /// </summary>
    public string? Badge(DateTimeOffset now) => State switch
    {
        SafeUpdateState.Waiting => Watch ? "updating — will watch it after" : null,
        SafeUpdateState.Watching when WatchUntil is { } until =>
            $"watching after update… {Math.Max(1, (int)Math.Ceiling((until - now).TotalMinutes))} min left",
        SafeUpdateState.Passed when EndedAt is { } ended && now - ended < TimeSpan.FromHours(24) => "update passed its watch",
        SafeUpdateState.RollingBack => "rolling back…",
        SafeUpdateState.RolledBack when EndedAt is { } ended && now - ended < TimeSpan.FromDays(7) => $"rolled back: {Reason}",
        SafeUpdateState.RollbackFailed => $"roll-back failed: {Reason}",
        SafeUpdateState.NotChecked when EndedAt is { } ended && now - ended < TimeSpan.FromHours(24) => "update not checked — see What changed",
        _ => null,
    };
}

/// <summary>What Docker says about the container being watched, at one look.</summary>
/// <param name="Id">The container's id — a different one means it was recreated.</param>
/// <param name="ImageId">The image it runs.</param>
/// <param name="Status">Docker's word: running, exited, restarting, paused, dead, created.</param>
/// <param name="Health">The healthcheck's verdict — healthy, unhealthy, starting — or empty without one.</param>
/// <param name="RestartCount">How many times Docker's restart policy has brought it back.</param>
/// <param name="ExitCode">Its last exit code, for saying why it stopped.</param>
public sealed record WatchedContainer(
    string Id,
    string ImageId,
    string Status,
    string Health,
    int RestartCount,
    int ExitCode,
    string? Service = null);

/// <summary>A LabbyTwo connection that reaches the watched container, and how it is doing.</summary>
/// <param name="IsUp">Its last probe; null when it has not been probed.</param>
/// <param name="Message">What the probe said.</param>
/// <param name="Since">When it last went up or down. One that was already down before the
/// update was asked for is not the update's fault, and does not fail the watch.</param>
public sealed record WatchedConnection(string Id, string Name, bool? IsUp, string Message = "", DateTimeOffset? Since = null);

/// <summary>What one look at a safe update decides.</summary>
/// <param name="State">The state to move to; the same state to carry on.</param>
/// <param name="Reason">Why, in words, for the feed and the badge.</param>
/// <param name="RollBack">True when the watch failed and the previous image should go back.</param>
public sealed record SafeUpdateStep(SafeUpdateState State, string Reason = "", bool RollBack = false);

/// <summary>
/// The rules of the watch after an update. Pure: handed the update, what Docker says about
/// the container now, the connections that point at it and any trouble on them in the feed,
/// it says what happens next — so every way an update can fail is a test, not an outage.
///
/// <list type="bullet">
/// <item><b>Waiting</b> becomes <b>watching</b> the first time the container is seen on a
/// different image. Watchtower pulls before it recreates, so this can take minutes; after
/// <see cref="WaitLimit"/> on the same image it is given up as not updated.</item>
/// <item>The watch <b>fails</b> — and the previous image goes back — when the container
/// stops, restarts (by its restart policy, counted by Docker), or its healthcheck says
/// unhealthy; when a connection that reaches it by name is down or goes down after
/// <see cref="Grace"/>; or when an alert rule fires on one of those connections.</item>
/// <item>The grace is for the connections only. A new version takes a moment to start
/// listening — the same two minutes a restart silences them for — and a probe during that
/// is the update, not a failure. A container that exits in that time has failed already.</item>
/// <item>A container <b>replaced or removed</b> by somebody else during the watch is left
/// alone: that is no longer the update LabbyTwo made, and putting an old image under a
/// container somebody just recreated by hand would be the wrong thing done confidently.</item>
/// <item>A watch that sees nothing wrong for its whole length <b>passes</b>.</item>
/// <item><b>A watch is only judged inside its window</b> — from when the new container was
/// first seen running to the end of the watch, plus <see cref="Slack"/> for the job's own
/// minute. Once that is over, nothing seen later can fail it: if the watch was looked at
/// up to its end it passes, and if it was not — the job stalled, Docker did not answer,
/// LabbyTwo could not see the lab — it ends as <b>not checked</b> and is left on the new
/// image. Never a roll-back hours later: that is how two healthy containers were put back
/// fourteen hours after a ten-minute watch, on the strength of a night of false downs.</item>
/// <item><b>Nothing waits for ever.</b> Any update still waiting or watching
/// <see cref="MaxAge"/> after it was asked for ends as not checked.</item>
/// <item>The watch <b>starts</b> only once the container on the new image has been
/// started. Watchtower creates it and then starts it; a look in between sees "created",
/// which is not a failure — and a container still only "created" after the grace is.</item>
/// </list>
///
/// Whether LabbyTwo can see the lab at all is the caller's to check: while it cannot, the
/// job does not look (see <c>SafeUpdates</c>), which the window rule above turns into
/// "not checked" rather than a verdict.
/// </summary>
public static class SafeUpdateRules
{
    /// <summary>How long an update may take to happen before it is said not to have.</summary>
    public static readonly TimeSpan WaitLimit = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How far past the end of its watch an update may still be judged: the job runs every
    /// minute, so the look that ends a watch lands up to a minute after it — and one more
    /// for a slow Docker.
    /// </summary>
    public static readonly TimeSpan Slack = TimeSpan.FromMinutes(2);

    /// <summary>The most any update may stay in progress, from when it was asked for, on top of the watch's own length.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(2);

    /// <summary>How long after the new container is seen before its connections count against it.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

    /// <summary>The watch lengths Settings offers.</summary>
    public static readonly IReadOnlyList<int> Minutes = [5, 10, 15, 30, 60];

    public const int DefaultMinutes = 10;

    /// <param name="container">What Docker says now, or null when there is no container by that name.</param>
    /// <param name="connections">The connections that reach it by name.</param>
    /// <param name="trouble">Status and alert changes recorded on those connections since the watch started.</param>
    /// <param name="lastLook">When the job last looked at this update and got an answer from
    /// Docker, or null if it has not in this run of LabbyTwo — what tells a watch that was
    /// looked at to its end from one that nobody was watching.</param>
    public static SafeUpdateStep Next(
        SafeUpdate update,
        WatchedContainer? container,
        IReadOnlyList<WatchedConnection> connections,
        IReadOnlyList<Change> trouble,
        DateTimeOffset now,
        DateTimeOffset? lastLook = null)
    {
        if (Overdue(update, now, lastLook) is { } overdue)
            return overdue;

        switch (update.State)
        {
            case SafeUpdateState.Waiting:
                // On the new image and started. "created" is the moment between Watchtower
                // creating the new container and starting it, and watching from there would
                // fail it for being stopped. One that is still only created at the wait
                // limit is watched all the same, and fails for never having started.
                if (container is not null && container.ImageId.Length > 0 && container.ImageId != update.PreviousImage &&
                    (container.Status != "created" || now - update.RequestedAt >= WaitLimit))
                    return new SafeUpdateStep(update.Watch ? SafeUpdateState.Watching : SafeUpdateState.Updated);
                if (container is not null && container.ImageId.Length > 0 && container.ImageId != update.PreviousImage)
                    return new SafeUpdateStep(SafeUpdateState.Waiting);
                if (now - update.RequestedAt < WaitLimit)
                    return new SafeUpdateStep(SafeUpdateState.Waiting);
                return container is null
                    ? new SafeUpdateStep(SafeUpdateState.Abandoned, "the container disappeared before it was updated")
                    : new SafeUpdateStep(SafeUpdateState.NotUpdated,
                        $"still on the same image {(int)WaitLimit.TotalMinutes} minutes after the update was asked for — " +
                        "Watchtower found nothing newer, or is not watching it");

            case SafeUpdateState.Watching:
                return Watching(update, container, connections, trouble, now);

            default:
                return new SafeUpdateStep(update.State, update.Reason);
        }
    }

    private static SafeUpdateStep Watching(
        SafeUpdate update, WatchedContainer? container, IReadOnlyList<WatchedConnection> connections,
        IReadOnlyList<Change> trouble, DateTimeOffset now)
    {
        if (container is null)
            return new SafeUpdateStep(SafeUpdateState.Abandoned, "removed during the watch, so there was nothing left to watch");

        if (container.ImageId != update.NewImage ||
            (update.NewContainerId.Length > 0 && container.Id != update.NewContainerId))
        {
            return new SafeUpdateStep(SafeUpdateState.Abandoned,
                "recreated by something else during the watch, so it is no longer the update LabbyTwo made");
        }

        if (container.Status is "exited" or "dead" ||
            (container.Status == "created" && now - (update.WatchFrom ?? now) >= Grace))
        {
            return Fail(container.ExitCode != 0
                ? $"it stopped with exit code {container.ExitCode}"
                : "it stopped");
        }

        if (container.Status == "restarting" || container.RestartCount > update.RestartBaseline)
        {
            var times = container.RestartCount - update.RestartBaseline;
            return Fail(times > 1 ? $"it restarted {times} times" : "it crashed and restarted");
        }

        if (container.Health.Equals("unhealthy", StringComparison.OrdinalIgnoreCase))
            return Fail("its health check says unhealthy");

        var from = update.WatchFrom ?? now;
        var ids = connections.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

        // Alerts count from the start: a rule has its own "for", so one that fires at all
        // during the watch is saying something about the new version.
        if (trouble.FirstOrDefault(c => c is { Kind: ChangeKinds.Alert, Action: ChangeActions.Firing } &&
                                        c.ConnectionId is { } id && ids.Contains(id) && c.At >= from) is { } alert)
            return Fail(alert.Title.StartsWith("Alert fired: ", StringComparison.Ordinal)
                ? $"an alert fired — {alert.Title["Alert fired: ".Length..]}"
                : alert.Title);

        if (now - from >= Grace)
        {
            if (connections.FirstOrDefault(c => c.IsUp == false && !(c.Since < update.RequestedAt)) is { } down)
                return Fail($"{down.Name} is down{(down.Message.Length > 0 ? $" ({down.Message})" : "")}");

            // Down and back up again between two looks still counts once the grace is over.
            if (trouble.FirstOrDefault(c => c is { Kind: ChangeKinds.Status, Action: ChangeActions.Down } &&
                                            c.ConnectionId is { } id && ids.Contains(id) && c.At >= from + Grace) is { } went)
                return Fail(went.Title);
        }

        return update.WatchUntil is { } until && now >= until
            ? new SafeUpdateStep(SafeUpdateState.Passed)
            : new SafeUpdateStep(SafeUpdateState.Watching);
    }

    private static SafeUpdateStep Fail(string reason) => new(SafeUpdateState.RollingBack, reason, RollBack: true);

    /// <summary>
    /// The verdict for an update whose time is up however it looks now, or null while it
    /// may still be judged on what is seen. Needs nothing from Docker, so it is asked even
    /// when Docker is not answering and LabbyTwo cannot see — which is exactly when an
    /// update would otherwise be left for a verdict hours late.
    /// </summary>
    public static SafeUpdateStep? Overdue(SafeUpdate update, DateTimeOffset now, DateTimeOffset? lastLook)
    {
        if (!update.IsActive || update.State == SafeUpdateState.RollingBack)
            return null;

        if (update is { State: SafeUpdateState.Watching, WatchUntil: { } until } && now > until + Slack)
        {
            // Looked at right up to the end with nothing wrong: anything wrong would have
            // ended it at that look, so it passed, only noticed a little late.
            if (lastLook is { } looked && looked >= until - Slack)
                return new SafeUpdateStep(SafeUpdateState.Passed);

            return new SafeUpdateStep(SafeUpdateState.NotChecked,
                $"LabbyTwo could not check on it through its {(int)update.WatchFor.TotalMinutes}-minute watch" +
                (lastLook is { } last && last >= update.WatchFrom ? $" (the last look was at {last.ToLocalTime():HH:mm})" : "") +
                ", so it was neither passed nor rolled back — it is left on the new image, and the previous one is kept " +
                "for rolling back by hand");
        }

        // Past the watch's own length, so a long watch is not cut short by this.
        if (now - update.RequestedAt > MaxAge + update.WatchFor)
        {
            return new SafeUpdateStep(SafeUpdateState.NotChecked,
                $"still {(update.State == SafeUpdateState.Waiting ? "waiting to be updated" : "being watched")} " +
                $"{(int)(now - update.RequestedAt).TotalHours} hours after the update was asked for, so LabbyTwo stopped " +
                "waiting — nothing was rolled back");
        }

        return null;
    }
}
