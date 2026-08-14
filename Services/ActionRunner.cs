using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// The one place a <see cref="ProviderAction"/> is actually run. Every button in the app
/// goes through here rather than calling a provider directly, so the things that must
/// happen around an action happen once instead of in every card that grows a button:
/// a bound timeout, a log line, the silence that stops a reboot you asked for from
/// paging you, and a re-probe so the tile catches up without waiting for the next sweep.
/// </summary>
public sealed class ActionRunner(
    Registry registry,
    HealthMonitor health,
    ConfigStore config,
    ILogger<ActionRunner> log)
{
    /// <summary>
    /// Longer than a probe's thirty seconds. Telling a NAS to shut down is not a request
    /// it hurries to answer, and half of these reply only once the work has begun.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    /// <summary>The actions this connection can offer right now, or empty if none.</summary>
    public IReadOnlyList<ProviderAction> ActionsFor(Connection connection)
    {
        try
        {
            return registry.Provider(connection.Provider)?.ActionsFor(connection) ?? [];
        }
        catch (Exception ex)
        {
            // Same bargain the Registry already strikes: a plugin that cannot describe
            // itself costs you the plugin, not the page it was going to be drawn on.
            log.LogError(ex, "{Provider} could not list its actions", connection.Provider);
            return [];
        }
    }

    public async Task<ActionResult> RunAsync(
        Connection connection, string actionId, SettingsBag? input = null, CancellationToken ct = default)
    {
        var action = ActionsFor(connection).FirstOrDefault(a =>
            string.Equals(a.Id, actionId, StringComparison.OrdinalIgnoreCase));

        return action is null
            ? ActionResult.Failed($"“{actionId}” is not something {connection.Name} can do.")
            : await RunAsync(connection, action, input, ct);
    }

    public async Task<ActionResult> RunAsync(
        Connection connection, ProviderAction action, SettingsBag? input = null, CancellationToken ct = default)
    {
        if (registry.Provider(connection.Provider) is not { } provider)
            return ActionResult.Failed($"The {connection.Provider} integration is not installed.");

        // Silence first. If the action succeeds the machine is already on its way down,
        // and a monitor sweep can land between the request returning and this line — which
        // is exactly the alert this is here to prevent.
        var silenced = false;
        if (action.Disrupts is { } window)
        {
            try
            {
                await config.SilenceAsync(connection.Id, DateTimeOffset.Now + window);
                silenced = true;
            }
            catch (Exception ex)
            {
                // Worth an alert you did not want; not worth refusing to do the thing.
                log.LogWarning(ex, "Could not silence {Connection} before {Action}", connection.Name, action.Id);
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        ActionResult result;
        try
        {
            log.LogInformation("Running {Action} on {Connection}", action.Id, connection.Name);
            result = await provider.RunActionAsync(connection, action, input ?? new SettingsBag(), timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result = ActionResult.Failed($"{connection.Name} did not answer within {Timeout.TotalSeconds:0} seconds.");
        }
        catch (Exception ex)
        {
            result = ActionResult.Failed(ProbeError.Describe(ex, connection.Settings.Get("host")));
        }

        if (!result.Ok)
        {
            log.LogWarning("{Action} on {Connection} failed: {Message}", action.Id, connection.Name, result.Message);

            // Lift a silence taken for an action that never happened, so a machine that is
            // genuinely down still says so.
            if (silenced)
            {
                try
                {
                    await config.SilenceAsync(connection.Id, null);
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Could not lift the silence on {Connection}", connection.Name);
                }
            }
            return result;
        }

        // Something changed; show it. Skipped for a disruptive action, where the only
        // thing a probe can discover is that the machine we just rebooted is not answering.
        if (action.Disrupts is null)
        {
            try
            {
                await health.RefreshAsync(connection, ct);
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Re-probe after {Action} on {Connection} failed", action.Id, connection.Name);
            }
        }

        return result;
    }
}
