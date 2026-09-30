using System.Text.Json;
using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// Safe updates: keeping the image a container ran before an update started from the
/// Containers tab, watching the container for a while after it, and putting the old image
/// back if the new one does not hold up.
///
/// <para><b>Before the update</b> (<see cref="PrepareAsync"/>) the previous image is written
/// down three ways, because any one of them can be gone by the time it is needed: its id;
/// its registry digest, which can always be pulled again; and a tag,
/// <c>labbytwo-rollback/sonarr:20260929-140201</c>, so nothing that tidies up untagged images
/// takes it. The one-shot Watchtower is started without <c>--cleanup</c> for the same reason.
/// A Watchtower reached through its HTTP API runs with whatever flags it was given; with
/// cleanup on it deletes the old image, tag and all, and a roll-back then pulls it again by
/// digest. Only one roll-back tag is kept per container: the next update's replaces it.</para>
///
/// <para><b>After it</b>, <see cref="WatchAsync"/> — run every minute by
/// <see cref="SafeUpdateJob"/> — waits for the container to appear on a new image, then
/// watches it for the chosen time by <see cref="SafeUpdateRules"/>. A failed watch rolls
/// back (<see cref="ContainerRollback"/>) at once, and every step is written to the change
/// feed, where an incident's timeline picks it up.</para>
///
/// <para><b>LabbyTwo's own container</b> is never watched. Once it has been replaced there
/// is no LabbyTwo left running the old image to watch the new one, and the new one
/// watching itself could not roll itself back — stopping the container is the end of
/// whatever was doing the stopping.</para>
/// </summary>
public sealed class SafeUpdates(
    SafeUpdateStore store,
    ChangeStore changes,
    IncidentStore incidents,
    ConfigStore config,
    HealthMonitor monitor,
    AppSettingsStore settings,
    ILogger<SafeUpdates> log)
{
    /// <summary>App setting: whether updates are watched unless a tab says otherwise. On by default.</summary>
    public const string EnabledKey = "safe_updates";

    /// <summary>App setting: how many minutes a watch lasts.</summary>
    public const string MinutesKey = "safe_update_minutes";

    /// <summary>The tab setting: "on", "off", or empty for the Settings default.</summary>
    public const string TabKey = "safe_update";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>How long connections reaching a container are silenced while it is rolled back.</summary>
    private static readonly TimeSpan RollbackSilence = TimeSpan.FromMinutes(3);

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Raised whenever an update moves on, so an open Containers tab redraws its badges.</summary>
    public event Action? Changed;

    /// <summary>Whether to watch, and for how long, as a tab and Settings decide together.</summary>
    public sealed record Choice(bool Watch, TimeSpan For);

    public async Task<Choice> ChoiceAsync(string? tabSetting, CancellationToken ct = default)
    {
        var bag = await settings.AllAsync(ct);
        var watch = (tabSetting ?? "").Trim().ToLowerInvariant() switch
        {
            "on" or "true" => true,
            "off" or "false" => false,
            _ => bag.GetBool(EnabledKey, true),
        };
        return new Choice(watch, TimeSpan.FromMinutes(Math.Clamp(bag.GetInt(MinutesKey, SafeUpdateRules.DefaultMinutes), 1, 240)));
    }

    /// <summary>The newest update of each container on one host, by name — what the tab's badges and roll-back button read.</summary>
    public Task<IReadOnlyDictionary<string, SafeUpdate>> LatestAsync(string connectionId, CancellationToken ct = default) =>
        store.LatestAsync(connectionId, ct);

    // ---- before -------------------------------------------------------------------

    /// <summary>
    /// Writes down what each container runs now, and tags that image, before an update is
    /// started. Never throws for one container: a container that could not be prepared is
    /// updated all the same, just without a way back, and the sentence saying so is
    /// returned for the page.
    /// </summary>
    /// <returns>The updates recorded, and anything worth telling the person who pressed the button.</returns>
    public async Task<(IReadOnlyList<SafeUpdate> Prepared, IReadOnlyList<string> Notes)> PrepareAsync(
        Connection docker, string endpoint, IReadOnlyList<ContainerRow> containers, Choice choice, CancellationToken ct = default)
    {
        var prepared = new List<SafeUpdate>();
        var notes = new List<string>();
        foreach (var row in containers)
        {
            try
            {
                var (update, note) = await PrepareOneAsync(docker, endpoint, row, choice, ct);
                prepared.Add(update);
                if (note is not null)
                    notes.Add(note);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Could not record {Container}'s image before updating it", row.Name);
                notes.Add($"{row.Name}: could not record its current image, so it cannot be rolled back — {ex.GetBaseException().Message}");
            }
        }

        if (prepared.Count > 0)
            Changed?.Invoke();
        return (prepared, notes);
    }

    private async Task<(SafeUpdate Update, string? Note)> PrepareOneAsync(
        Connection docker, string endpoint, ContainerRow row, Choice choice, CancellationToken ct)
    {
        var reference = row.Image;
        var imageId = row.ImageId;
        using (var inspected = JsonDocument.Parse(
                   await DockerSocket.GetAsync(endpoint, Timeout, $"/containers/{Uri.EscapeDataString(row.Id)}/json", ct)))
        {
            var root = inspected.RootElement;
            if (root.TryGetProperty("Config", out var cfg) && cfg.TryGetProperty("Image", out var image) &&
                image.GetString() is { Length: > 0 } created)
                reference = created;
            if (root.TryGetProperty("Image", out var id) && id.GetString() is { Length: > 0 } running)
                imageId = running;
        }

        string? note = null;
        var digest = "";
        try
        {
            var facts = ContainerUpdates.ParseFacts(
                await DockerSocket.GetAsync(endpoint, Timeout, $"/images/{Uri.EscapeDataString(imageId)}/json", ct));
            digest = DigestFor(reference, facts.RepoDigests);
        }
        catch (InvalidOperationException ex)
        {
            note = $"{row.Name}: Docker would not describe its image ({ex.GetBaseException().Message}), so if it is deleted " +
                   "after the update it cannot be pulled back.";
        }

        var now = DateTimeOffset.Now;
        var (repository, tag) = ContainerRollback.TagFor(row.Name, now);
        var tagged = "";
        try
        {
            await DockerSocket.PostAsync(endpoint, Timeout,
                $"/images/{Uri.EscapeDataString(imageId)}/tag?repo={Uri.EscapeDataString(repository)}&tag={Uri.EscapeDataString(tag)}",
                null, ct);
            tagged = $"{repository}:{tag}";
        }
        catch (InvalidOperationException ex)
        {
            // Keeping it by tag is the belt; the id and the digest are the braces.
            log.LogInformation("Could not tag {Container}'s previous image: {Reason}", row.Name, ex.Message);
            note ??= $"{row.Name}: could not tag its current image to keep it ({ex.GetBaseException().Message}). It can still " +
                     "be rolled back while the image is there" + (digest.Length > 0 ? ", or pulled again by digest." : ".");
        }

        await _gate.WaitAsync(ct);
        try
        {
            // A second update of the same container ends the first's watch: it is the new one that counts now.
            foreach (var old in (await store.ActiveAsync(ct)).Where(u => u.ConnectionId == docker.Id && u.Container == row.Name))
                await store.SaveAsync(old with { State = SafeUpdateState.Abandoned, EndedAt = now, Reason = "updated again" }, ct);

            var update = await store.AddAsync(new SafeUpdate(0, docker.Id, row.Name, row.Id, reference, imageId, digest, tagged,
                now, choice.Watch, choice.For, SafeUpdateState.Waiting), ct);

            // One kept image per container: the last update's tag comes off. Without force, so
            // an image something still runs — a container rolled back onto it — just loses the name.
            foreach (var (id, oldTag) in await store.OlderTagsAsync(update, ct))
            {
                if (oldTag != tagged)
                {
                    try
                    {
                        await DockerSocket.SendAsync(endpoint, Timeout, HttpMethod.Delete, $"/images/{Uri.EscapeDataString(oldTag)}", null, ct);
                    }
                    catch (InvalidOperationException)
                    {
                        // Already gone, or still in use; either way it is no longer this container's way back.
                    }
                }
                await store.ClearTagAsync(id, ct);
            }

            return (update, note);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Ends updates that were prepared but never started — Watchtower could not be asked —
    /// so they are not left waiting half an hour to be called "not updated".
    /// </summary>
    public async Task AbandonAsync(IReadOnlyList<SafeUpdate> updates, string reason, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            foreach (var update in updates)
            {
                if (await store.GetAsync(update.Id, ct) is { State: SafeUpdateState.Waiting } current)
                    await store.SaveAsync(current with { State = SafeUpdateState.Abandoned, EndedAt = DateTimeOffset.Now, Reason = reason }, ct);
            }
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// The <c>repo@sha256:…</c> to pull the image again by: the one from the repository the
    /// container was created from, where the image was pulled from several.
    /// </summary>
    public static string DigestFor(string reference, IReadOnlyList<string> repoDigests)
    {
        var wanted = ImageRef.Parse(reference);
        foreach (var candidate in repoDigests)
        {
            var parsed = ImageRef.Parse(candidate);
            if (parsed.Registry.Equals(wanted.Registry, StringComparison.OrdinalIgnoreCase) &&
                parsed.Repository.Equals(wanted.Repository, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        return repoDigests.FirstOrDefault() ?? "";
    }

    // ---- after --------------------------------------------------------------------

    /// <summary>
    /// One look at every update still in progress. Cheap when there are none — one read of
    /// a partial index that is empty — which is nearly always.
    /// </summary>
    public async Task WatchAsync(CancellationToken ct = default)
    {
        var active = await store.ActiveAsync(ct);
        if (active.Count == 0)
            return;

        var connections = await config.ConnectionsAsync(ct);
        foreach (var update in active)
        {
            try
            {
                await _gate.WaitAsync(ct);
                try
                {
                    // Read again under the gate: a roll-back by hand may have moved it on.
                    if (await store.GetAsync(update.Id, ct) is { IsActive: true } current)
                        await StepAsync(current, connections, DateTimeOffset.Now, ct);
                }
                finally
                {
                    _gate.Release();
                }
            }
            // A Docker call that timed out is a cancellation too — HttpClient's timeout is
            // one — and letting it through failed the whole job ("Operation canceled") and
            // left every other update in the list unlooked-at. Only the job being stopped
            // is a reason to stop.
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("Could not check on the update of {Container}: {Reason}", update.Container,
                    ex is OperationCanceledException ? "Docker did not answer in time" : ex.GetBaseException().Message);
            }
        }

        // Forget looks at updates that are over.
        foreach (var id in _looked.Keys.Where(id => active.All(u => u.Id != id)))
            _looked.TryRemove(id, out _);
    }

    /// <summary>
    /// When each update in progress was last looked at with an answer from Docker. In
    /// memory: after a restart nothing has been looked at, which is the honest answer —
    /// a watch that ended while LabbyTwo was down was not watched.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, DateTimeOffset> _looked = new();

    private DateTimeOffset? LastLook(long id) => _looked.TryGetValue(id, out var at) ? at : null;

    private async Task StepAsync(SafeUpdate update, IReadOnlyList<Connection> connections, DateTimeOffset now, CancellationToken ct)
    {
        if (update.State == SafeUpdateState.RollingBack)
        {
            // Only left in this state by LabbyTwo stopping part-way through a roll-back.
            var stuck = update with
            {
                State = SafeUpdateState.RollbackFailed,
                EndedAt = now,
                Reason = "LabbyTwo restarted in the middle of rolling it back — check the container, and whether a stopped " +
                         $"copy named {update.Container}-labbytwo-replaced-… was left behind",
            };
            await SaveAndRecordAsync(stuck, new Change(now, ChangeKinds.Container, ChangeActions.Failed, update.ConnectionId,
                update.Container, $"Could not finish rolling {update.Container} back", stuck.Reason + "."), ct);
            return;
        }

        if (connections.FirstOrDefault(c => c.Id == update.ConnectionId) is not { } docker)
        {
            await SaveAsync(update with { State = SafeUpdateState.Abandoned, EndedAt = now, Reason = "its Docker connection was deleted" }, ct);
            return;
        }

        // Time up, whatever Docker would say: decided before asking it, so a Docker that is
        // not answering cannot leave an update hanging for a verdict that comes hours late.
        if (SafeUpdateRules.Overdue(update, now, LastLook(update.Id)) is { } overdue)
        {
            await FinishAsync(update, overdue, now, ct);
            return;
        }

        // Nothing is judged while LabbyTwo cannot see the lab: a connection that looks down
        // then is LabbyTwo's trouble, not the update's. Not looking also means this is not
        // counted as a look, so a watch that spends its end blind ends as not checked.
        if (monitor.IsBlind)
            return;

        var endpoint = Endpoint(docker);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(docker.Settings.GetInt("timeout", 10), 1, 120));
        var (container, labels) = await LookAsync(endpoint, timeout, update.Container, ct);

        var reaching = container is null ? [] : Reaching(update.Container, labels, container.Id, container.ImageId, connections);
        var watched = reaching
            .Select(c => monitor.State(c.Id) is { } state
                ? new WatchedConnection(c.Id, c.Name, state.IsUp, state.Message, state.ChangedAt)
                : new WatchedConnection(c.Id, c.Name, null))
            .ToList();

        IReadOnlyList<Change> trouble = [];
        if (update is { State: SafeUpdateState.Watching, WatchFrom: { } from } && watched.Count > 0)
            trouble = await changes.QueryAsync(new ChangeQuery(from, now, [ChangeKinds.Status, ChangeKinds.Alert]), ct);

        var step = SafeUpdateRules.Next(update, container, watched, trouble, now, LastLook(update.Id));
        _looked[update.Id] = now;
        if (step.State == update.State)
            return;

        var name = update.Container;
        switch (step.State)
        {
            case SafeUpdateState.Watching when container is not null:
            {
                var watching = update with
                {
                    State = SafeUpdateState.Watching,
                    NewImage = container.ImageId,
                    NewContainerId = container.Id,
                    WatchFrom = now,
                    RestartBaseline = container.RestartCount,
                };
                var also = watched.Count > 0 ? $", or {Names(watched)} goes down or fires an alert" : "";
                await SaveAndRecordAsync(watching, new Change(now, ChangeKinds.Container, ChangeActions.Watching, update.ConnectionId,
                    name, $"{name} was updated — watching it for {Minutes(update.WatchFor)}",
                    $"From image {ContainerChanges.ShortImage(update.PreviousImage)} to {ContainerChanges.ShortImage(container.ImageId)}. " +
                    $"If it stops, restarts or turns unhealthy{also}, LabbyTwo puts the previous image back."), ct);
                break;
            }

            case SafeUpdateState.Updated when container is not null:
                // Not watched, so nothing to say beyond what the feed already has: the watcher
                // records the new image itself.
                await SaveAsync(update with
                {
                    State = SafeUpdateState.Updated,
                    NewImage = container.ImageId,
                    NewContainerId = container.Id,
                    EndedAt = now,
                }, ct);
                break;

            case SafeUpdateState.Passed or SafeUpdateState.NotChecked:
                await FinishAsync(update, step, now, ct);
                break;

            case SafeUpdateState.NotUpdated or SafeUpdateState.Abandoned:
                await SaveAndRecordAsync(update with { State = step.State, EndedAt = now, Reason = step.Reason },
                    new Change(now, ChangeKinds.Container, ChangeActions.Changed, update.ConnectionId, name,
                        step.State == SafeUpdateState.NotUpdated ? $"{name} was not updated" : $"Stopped watching {name}",
                        Sentence(step.Reason)), ct);
                break;

            case SafeUpdateState.RollingBack when step.RollBack:
                await RecordAsync(new Change(now, ChangeKinds.Container, ChangeActions.Failed, update.ConnectionId, name,
                    $"{name} failed its watch after updating: {step.Reason}",
                    $"Rolling it back to image {ContainerChanges.ShortImage(update.PreviousImage)}."), ct);
                await RollBackLockedAsync(update, docker, connections, step.Reason, reaching, ct);
                break;
        }
    }

    /// <summary>Ends a watch without touching the container: it passed, or it could not be checked.</summary>
    private async Task FinishAsync(SafeUpdate update, SafeUpdateStep step, DateTimeOffset now, CancellationToken ct)
    {
        var name = update.Container;
        var kept = update.RollbackTag.Length > 0
            ? $"The previous image is kept as {update.RollbackTag}, for rolling back by hand."
            : "The previous image is kept while nothing deletes it, for rolling back by hand.";

        if (step.State == SafeUpdateState.Passed)
        {
            await SaveAndRecordAsync(update with { State = SafeUpdateState.Passed, EndedAt = now },
                new Change(now, ChangeKinds.Container, ChangeActions.Passed, update.ConnectionId, name,
                    $"{name} passed its {Minutes(update.WatchFor)} watch after updating", kept), ct);
            return;
        }

        await SaveAndRecordAsync(update with { State = step.State, EndedAt = now, Reason = step.Reason },
            new Change(now, ChangeKinds.Container, ChangeActions.Changed, update.ConnectionId, name,
                $"{name}'s update was not checked", $"{Sentence(Capitalise(step.Reason))} {(step.Reason.Contains("kept") ? "" : kept)}".Trim()), ct);
    }

    // ---- rolling back -------------------------------------------------------------

    /// <summary>
    /// "Roll back to previous image", pressed on the tab. Returns null when it worked, or
    /// why not. Waits for a watch pass that is running, so the two never recreate the same
    /// container at once.
    /// </summary>
    public async Task<string?> RollBackAsync(long updateId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (await store.GetAsync(updateId, ct) is not { } update)
                return "That update is no longer recorded.";
            if (!update.CanRollBack)
                return $"{update.Container} is not on an update LabbyTwo can undo.";

            var connections = await config.ConnectionsAsync(ct);
            if (connections.FirstOrDefault(c => c.Id == update.ConnectionId) is not { } docker)
                return "Its Docker connection has been deleted.";

            var (_, labels) = await LookAsync(Endpoint(docker), Timeout, update.Container, ct);
            var reaching = Reaching(update.Container, labels, "", "", connections);
            return await RollBackLockedAsync(update, docker, connections, "rolled back by hand", reaching, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> RollBackLockedAsync(
        SafeUpdate update, Connection docker, IReadOnlyList<Connection> connections, string reason,
        IReadOnlyList<Connection> reaching, CancellationToken ct)
    {
        var name = update.Container;
        var now = DateTimeOffset.Now;
        await SaveAsync(update with { State = SafeUpdateState.RollingBack, Reason = reason }, ct);

        // Recreating is a restart as far as anything probing it can tell.
        foreach (var connection in reaching)
        {
            try
            {
                await config.SilenceAsync(connection.Id, now + RollbackSilence, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogDebug(ex, "Could not silence {Connection} for the roll-back", connection.Name);
            }
        }

        var stale = new[] { update.ContainerId, update.NewContainerId }
            .Where(id => id.Length >= 12).Select(id => id[..12]).ToList();
        try
        {
            await ContainerRollback.RollBackAsync(Endpoint(docker), name,
                new ContainerRollback.Previous(update.PreviousImage, update.PreviousDigest, update.RollbackTag, update.Reference),
                stale, ct);
            DockerContainers.ForgetSharedLists();

            var incident = await IncidentNoteAsync(reaching, ct);
            await SaveAndRecordAsync(update with { State = SafeUpdateState.RolledBack, EndedAt = DateTimeOffset.Now, Reason = reason },
                new Change(DateTimeOffset.Now, ChangeKinds.Container, ChangeActions.RolledBack, update.ConnectionId, name,
                    $"{name} was rolled back to its previous image",
                    $"{Sentence(Capitalise(reason))} It runs image {ContainerChanges.ShortImage(update.PreviousImage)} again, " +
                    $"recreated with the same settings from {update.Reference}. A newer image is still published, so the next " +
                    $"update — by LabbyTwo or by a Watchtower on a schedule — will offer it again.{incident}"), ct);
            log.LogWarning("Rolled {Container} back to {Image}: {Reason}", name, update.PreviousImage, reason);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var why = ex.GetBaseException().Message;
            log.LogError(ex, "Could not roll {Container} back", name);
            await SaveAndRecordAsync(update with { State = SafeUpdateState.RollbackFailed, EndedAt = DateTimeOffset.Now, Reason = why },
                new Change(DateTimeOffset.Now, ChangeKinds.Container, ChangeActions.Failed, update.ConnectionId, name,
                    $"Could not roll {name} back", $"{Sentence(Capitalise(reason))} {Sentence(why)}"), ct);
            return why;
        }
    }

    /// <summary>
    /// " Part of the incident “Sonarr” (#12)." for an open incident one of the connections
    /// is in — the incident's timeline already shows the roll-back, being the feed; this
    /// says it the other way round.
    /// </summary>
    private async Task<string> IncidentNoteAsync(IReadOnlyList<Connection> reaching, CancellationToken ct)
    {
        if (reaching.Count == 0)
            return "";
        try
        {
            var ids = reaching.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            var open = (await incidents.OpenAsync(ct)).FirstOrDefault(i => i.Members.Any(m => m.ConnectionId is { } id && ids.Contains(id)));
            return open is null ? "" : $" Part of the incident “{open.Title}” (#{open.Id}).";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogDebug(ex, "Could not look up open incidents");
            return "";
        }
    }

    // ---- helpers ------------------------------------------------------------------

    /// <summary>
    /// What Docker says about the container by that name now, and its labels; nulls when
    /// there is none. Anything else Docker says — a proxy refusing, a timeout — is thrown,
    /// so the job tries again next minute rather than reading it as "removed".
    /// </summary>
    private static async Task<(WatchedContainer? Container, IReadOnlyDictionary<string, string> Labels)> LookAsync(
        string endpoint, TimeSpan timeout, string name, CancellationToken ct)
    {
        string payload;
        try
        {
            payload = await DockerSocket.GetAsync(endpoint, timeout, $"/containers/{Uri.EscapeDataString(name)}/json", ct);
        }
        catch (InvalidOperationException ex) when (ex is not DockerProxyDeniedException &&
                                                    ex.Message.Contains("No such container", StringComparison.OrdinalIgnoreCase))
        {
            return (null, new Dictionary<string, string>());
        }
        return Watched(payload);
    }

    /// <summary>The watch's view of an inspect payload.</summary>
    public static (WatchedContainer Container, IReadOnlyDictionary<string, string> Labels) Watched(string payload)
    {
        var details = DockerContainers.ParseDetails(payload);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var state = root.TryGetProperty("State", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;

        string Text(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

        int Number(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
            value.TryGetInt32(out var number) ? number : 0;

        var health = state.ValueKind == JsonValueKind.Object && state.TryGetProperty("Health", out var h) &&
                     h.ValueKind == JsonValueKind.Object ? Text(h, "Status") : "";
        var status = Text(state, "Status");
        if (state.ValueKind == JsonValueKind.Object && state.TryGetProperty("Restarting", out var restarting) &&
            restarting.ValueKind == JsonValueKind.True)
            status = "restarting";

        return (new WatchedContainer(details.Id, details.ImageId, status, health, Number(root, "RestartCount"),
            Number(state, "ExitCode")), details.Labels);
    }

    private static IReadOnlyList<Connection> Reaching(
        string name, IReadOnlyDictionary<string, string> labels, string id, string imageId, IReadOnlyList<Connection> connections) =>
        ContainerSafety.ConnectionsReaching(
            new ContainerRow(id, name, "", imageId, "", "", DateTimeOffset.MinValue, [], labels), connections);

    private static string Endpoint(Connection docker) => docker.Settings.Get("endpoint", DockerSocket.EnvironmentEndpoint);

    private static string Minutes(TimeSpan span) =>
        span.TotalMinutes is var minutes && minutes == 1 ? "1 minute" : $"{minutes:0} minutes";

    private static string Names(IReadOnlyList<WatchedConnection> connections) =>
        connections.Count switch
        {
            1 => connections[0].Name,
            2 => $"{connections[0].Name} or {connections[1].Name}",
            _ => $"{connections[0].Name} or {connections.Count - 1} other connections pointing at it",
        };

    private static string Capitalise(string text) => text.Length > 0 ? char.ToUpperInvariant(text[0]) + text[1..] : text;

    private static string Sentence(string text) =>
        text.Length == 0 || text.EndsWith('.') || text.EndsWith('!') || text.EndsWith('?') ? text : text + ".";

    private async Task SaveAsync(SafeUpdate update, CancellationToken ct)
    {
        await store.SaveAsync(update, ct);
        Changed?.Invoke();
    }

    private async Task SaveAndRecordAsync(SafeUpdate update, Change change, CancellationToken ct)
    {
        await SaveAsync(update, ct);
        await RecordAsync(change, ct);
    }

    private async Task RecordAsync(Change change, CancellationToken ct)
    {
        try
        {
            await changes.RecordAsync(change, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not record the change \"{Title}\"", change.Title);
        }
    }
}

/// <summary>
/// Looks at every update in progress once a minute — see <see cref="SafeUpdates"/>. Runs at
/// startup too, so a watch that was going when LabbyTwo restarted carries on straight away;
/// with nothing being watched each run is one read of an empty index.
/// </summary>
public sealed class SafeUpdateJob(SafeUpdates updates) : IBackgroundJob
{
    public string Name => "safe-updates";

    public TimeSpan Interval => TimeSpan.FromMinutes(1);

    public bool RunAtStartup => true;

    public Task RunAsync(CancellationToken ct) => updates.WatchAsync(ct);
}
