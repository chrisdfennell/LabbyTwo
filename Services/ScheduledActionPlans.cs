using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// Turns a <see cref="ScheduledAction"/> into something that can run. An interface for the
/// same reason as <see cref="IRemediationActions"/>: the service's rules — when, how late,
/// never twice at once — are tested with a fake that counts calls, and the real one is only
/// the guardrails, which it borrows from self-healing.
/// </summary>
public interface IScheduledActionPlans
{
    /// <summary>"restart plex", "run “Backup” on NAS" — from memory, for a run that is skipped before anything is asked.</summary>
    Task<string> DescribeAsync(ScheduledAction action, CancellationToken ct);

    /// <summary>Looks the target up, checks it may be acted on, and hands back the call.</summary>
    Task<RemediationPlan> PrepareAsync(ScheduledAction action, CancellationToken ct);

    /// <summary>
    /// Why this action cannot be saved as it is, beyond what <see cref="ScheduledAction.Problem"/>
    /// can tell without the provider: an action that asks a question, or a dangerous one without
    /// the opt-in. Null when it can.
    /// </summary>
    Task<string?> SaveProblemAsync(ScheduledAction action, CancellationToken ct);
}

/// <summary>
/// The real <see cref="IScheduledActionPlans"/>: container restarts, starts and stops through
/// the Docker API and provider actions through <see cref="ActionRunner"/>, both prepared by
/// <see cref="RemediationActions"/> — so a scheduled action is held to exactly the rules
/// self-healing is, from the same code.
/// </summary>
public sealed class ScheduledActionPlans(RemediationActions actions, ConfigStore config, ActionRunner runner) : IScheduledActionPlans
{
    private const string Owner = "this scheduled action";

    public async Task<string> DescribeAsync(ScheduledAction action, CancellationToken ct)
    {
        if (action.Target == ScheduledTarget.Container)
            return $"{Verb(action.Verb).ToString().ToLowerInvariant()} {action.Container}";

        var connection = await config.ConnectionAsync(action.TargetConnectionId, ct);
        var found = connection is null ? null : Find(connection, action.ActionId);
        return $"run “{found?.Label ?? action.ActionId}” on {connection?.Name ?? "a deleted connection"}";
    }

    public Task<RemediationPlan> PrepareAsync(ScheduledAction action, CancellationToken ct) =>
        action.Target == ScheduledTarget.Container
            ? actions.PrepareContainerAsync(action.TargetConnectionId, action.Container, Verb(action.Verb),
                action.AllowProtected, Owner, "Scheduled action", ct)
            : actions.PrepareProviderActionAsync(action.TargetConnectionId, action.ActionId, action.AllowProtected, ct);

    public async Task<string?> SaveProblemAsync(ScheduledAction action, CancellationToken ct)
    {
        if (action.Target != ScheduledTarget.ProviderAction)
            return null;
        if (await config.ConnectionAsync(action.TargetConnectionId, ct) is not { } connection)
            return "The connection it runs an action on no longer exists.";
        if (Find(connection, action.ActionId) is not { } found)
            return $"{connection.Name} does not offer that action with its current settings.";
        return RemediationGuards.ActionRefusal(found, connection.Name, action.AllowProtected);
    }

    private ProviderAction? Find(Connection connection, string id) =>
        runner.ActionsFor(connection).FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The Docker verb. Only these three: pausing or removing on a timer is not something anyone should build by accident.</summary>
    public static ContainerAction Verb(ContainerVerb verb) => verb switch
    {
        ContainerVerb.Start => ContainerAction.Start,
        ContainerVerb.Stop => ContainerAction.Stop,
        _ => ContainerAction.Restart,
    };
}
