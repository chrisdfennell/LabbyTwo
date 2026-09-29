using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// A remediation made concrete: what it will do in words, whether it may, and the call
/// that does it.
/// </summary>
/// <param name="Doing">"restart plex" — for "LabbyTwo will try: …" and "Could not …".</param>
/// <param name="Did">"Restarted plex" — for "… at 02:14 — it recovered".</param>
/// <param name="Refused">Why it must not run, in words, or null when it may.</param>
/// <param name="Run">The call itself; null when refused.</param>
/// <param name="Disrupts">How long the action expects its target to be unreachable, which the check waits out.</param>
public sealed record RemediationPlan(
    string Doing,
    string Did,
    string? Refused,
    Func<CancellationToken, Task<ActionResult>>? Run,
    TimeSpan? Disrupts = null)
{
    public static RemediationPlan Refuse(string doing, string did, string why) => new(doing, did, why, null);
}

/// <summary>
/// Turns a <see cref="Remediation"/> into something that can run. An interface so the
/// service's rules — attempts, cooldowns, caps, verdicts — are tested with a fake that
/// counts calls, and the real one is tested for its guardrails alone.
/// </summary>
public interface IRemediationActions
{
    /// <summary>"restart plex", without asking anything but memory — for the first notification, which must not wait on Docker.</summary>
    Task<string> DescribeAsync(Remediation remediation, CancellationToken ct);

    /// <summary>Looks the target up, checks it may be acted on, and hands back the call.</summary>
    Task<RemediationPlan> PrepareAsync(Remediation remediation, CancellationToken ct);
}

/// <summary>
/// The guardrails that belong to the target rather than to the schedule. Pure, so each is a test.
/// </summary>
public static class RemediationGuards
{
    /// <summary>
    /// Why this container must not be restarted automatically, or null. LabbyTwo's own is
    /// never restarted by itself, opt-in or not: the process deciding whether the restart
    /// helped is the one being restarted, and a remediation that could restart its own
    /// judge could loop with nobody watching. A protected container — the tunnel, the
    /// reverse proxy — needs the remediation to say so explicitly, because restarting one
    /// from outside the house is how you lose the way back in.
    /// </summary>
    public static string? ContainerRefusal(ContainerRow row, string selfHint, IReadOnlyList<string> protectedList, bool allowProtected)
    {
        if (ContainerSafety.IsSelf(row, selfHint))
            return $"“{row.Name}” is the container LabbyTwo itself runs in, which it never restarts by itself.";
        if (!allowProtected && ContainerSafety.IsListed(row, protectedList))
            return $"“{row.Name}” is on a Containers page's protected list. Tick “Allow protected containers” on this remediation if you really want it restarted automatically.";
        return null;
    }

    /// <summary>
    /// Why this provider action must not run automatically, or null. A dangerous one — the
    /// red buttons, which end with something switched off — needs the same explicit opt-in
    /// as a protected container. One that asks for input cannot run at all: nobody is there
    /// to answer it.
    /// </summary>
    public static string? ActionRefusal(ProviderAction action, string connectionName, bool allowProtected)
    {
        if (action.Fields.Any(f => f.Required))
            return $"“{action.Label}” on {connectionName} asks for something before it runs, and nobody is there to answer.";
        if (action.Dangerous && !allowProtected)
            return $"“{action.Label}” on {connectionName} is marked dangerous. Tick “Allow protected containers and dangerous actions” if you really want it run automatically.";
        return null;
    }

    /// <summary>
    /// The Containers page's kind key. Written out rather than taken from the page's own
    /// class, which lives with the components a service should not reach into.
    /// </summary>
    public const string ContainersTabKind = "containers";

    /// <summary>
    /// The protected list, from every Containers page. The union rather than the page for
    /// this Docker host alone: which page somebody wrote "cloudflared" on is an accident of
    /// where they were at the time, and being too careful here costs one opt-in tick.
    /// </summary>
    public static IReadOnlyList<string> ProtectedList(IEnumerable<Tab> tabs) =>
    [
        .. tabs.Where(t => t.Kind == ContainersTabKind)
            .SelectMany(t => ContainerSafety.ParseList(t.Settings.Get("protected")))
            .Distinct(StringComparer.OrdinalIgnoreCase),
    ];

    /// <summary>Finds a container by name or Compose service, as the protected list and the connection matching do.</summary>
    public static ContainerRow? Find(IEnumerable<ContainerRow> rows, string name)
    {
        var wanted = name.Trim().TrimStart('/');
        var list = rows.ToList();
        return list.FirstOrDefault(r => r.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
               ?? list.FirstOrDefault(r => r.Service?.Equals(wanted, StringComparison.OrdinalIgnoreCase) == true);
    }
}

/// <summary>
/// The real <see cref="IRemediationActions"/>: container restarts through the Docker API,
/// and provider actions through <see cref="ActionRunner"/> — the same runner every button
/// uses, with its timeout, its log line and its silence around a reboot.
/// </summary>
public sealed class RemediationActions(ConfigStore config, ActionRunner runner, ILogger<RemediationActions> log) : IRemediationActions
{
    /// <summary>Listing containers to find the one to restart. Short: this is not the time to wait on a busy socket.</summary>
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(20);

    public async Task<string> DescribeAsync(Remediation remediation, CancellationToken ct)
    {
        if (remediation.Kind == RemediationKind.RestartContainer)
            return $"restart {remediation.Container}";

        var connection = await config.ConnectionAsync(remediation.TargetConnectionId, ct);
        var action = connection is null ? null : runner.ActionsFor(connection).FirstOrDefault(a => Is(a, remediation.ActionId));
        return Doing(action?.Label ?? remediation.ActionId, connection?.Name ?? "a deleted connection");
    }

    public async Task<RemediationPlan> PrepareAsync(Remediation remediation, CancellationToken ct) =>
        remediation.Kind == RemediationKind.RestartContainer
            ? await PrepareRestartAsync(remediation, ct)
            : await PrepareActionAsync(remediation, ct);

    private async Task<RemediationPlan> PrepareRestartAsync(Remediation remediation, CancellationToken ct)
    {
        var name = remediation.Container;
        var doing = $"restart {name}";
        var did = $"Restarted {name}";

        if (await config.ConnectionAsync(remediation.TargetConnectionId, ct) is not { Provider: "docker" } docker)
            return RemediationPlan.Refuse(doing, did, "The Docker connection it restarts through no longer exists.");

        var endpoint = docker.Settings.Get("endpoint", DockerSocket.DefaultEndpoint);
        IReadOnlyList<ContainerRow> rows;
        try
        {
            rows = await DockerContainers.ListAsync(endpoint, ListTimeout, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return RemediationPlan.Refuse(doing, did, $"Could not list the containers on {docker.Name}: {DockerContainers.Explain(ex, endpoint)}");
        }

        if (RemediationGuards.Find(rows, name) is not { } row)
            return RemediationPlan.Refuse(doing, did, $"There is no container called “{name}” on {docker.Name}.");

        var protectedList = RemediationGuards.ProtectedList(await config.TabsAsync(ct));
        if (RemediationGuards.ContainerRefusal(row, SelfContainer.Hint, protectedList, remediation.AllowProtected) is { } why)
            return RemediationPlan.Refuse(doing, did, why);

        return new RemediationPlan(doing, did, null, async token =>
        {
            try
            {
                log.LogWarning("Self-healing: restarting container {Container} on {Docker}", row.Name, docker.Name);
                await DockerContainers.RunAsync(endpoint, ContainerAction.Restart, row.Id, token);
                // A runbook listing containers should not keep showing the old state for
                // the few seconds a shared list is otherwise reused.
                DockerContainers.ForgetSharedLists();
                return ActionResult.Done($"Restarted {row.Name}.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                return ActionResult.Failed(ex.GetBaseException().Message);
            }
        });
    }

    private async Task<RemediationPlan> PrepareActionAsync(Remediation remediation, CancellationToken ct)
    {
        var connection = await config.ConnectionAsync(remediation.TargetConnectionId, ct);
        var action = connection is null ? null : runner.ActionsFor(connection).FirstOrDefault(a => Is(a, remediation.ActionId));
        var doing = Doing(action?.Label ?? remediation.ActionId, connection?.Name ?? "a deleted connection");
        var did = $"Ran “{action?.Label ?? remediation.ActionId}” on {connection?.Name ?? "a deleted connection"}";

        if (connection is null)
            return RemediationPlan.Refuse(doing, did, "The connection whose action it runs no longer exists.");
        if (action is null)
            return RemediationPlan.Refuse(doing, did, $"{connection.Name} does not offer “{remediation.ActionId}” with its current settings.");
        if (RemediationGuards.ActionRefusal(action, connection.Name, remediation.AllowProtected) is { } why)
            return RemediationPlan.Refuse(doing, did, why);

        return new RemediationPlan(doing, did, null, token => runner.RunAsync(connection, action, new SettingsBag(), token), action.Disrupts);
    }

    private static bool Is(ProviderAction action, string id) => string.Equals(action.Id, id, StringComparison.OrdinalIgnoreCase);

    private static string Doing(string label, string connection) => $"run “{label}” on {connection}";
}
