using System.Collections.Concurrent;
using System.Diagnostics;
using LabbyTwo.Core;
using LabbyTwo.Storage;
using Microsoft.Extensions.Options;

namespace LabbyTwo.Services;

/// <summary>
/// The one place a <c>{{ssh: …}}</c> button's command is actually run, so the rules around it
/// are checked in one place however many notes hold one — the same bargain
/// <see cref="ActionRunner"/> strikes for action buttons.
///
/// In the order they are checked:
/// <list type="number">
/// <item>LabbyTwo has a login. Without one, anybody who can reach the dashboard could run
/// commands on the NAS — the Terminal plugin refuses for the same reason, and so does this.</item>
/// <item>The connection's provider can run commands (<see cref="ICommandRunner"/>), and the
/// connection has "Allow runbook commands" ticked. Off by default, per machine.</item>
/// <item>The same command on the same machine is not already running. A double tap, or two
/// people with the same runbook open, gets "already running" rather than a second restart.</item>
/// <item>A timeout, the shortcode's own or a minute, and no more than ten.</item>
/// </list>
///
/// Every run that gets past those is written into the change feed — who pressed it, what ran
/// where, the exit code and how long it took — whether it worked or not. The output handed
/// back is the last <see cref="RunbookCommands.OutputLines"/> lines, cleaned of terminal codes
/// and masked as the log search masks, and is only ever shown as text.
/// </summary>
public sealed class RunbookCommandRunner(
    Registry registry,
    ChangeStore changes,
    IOptions<LabbyOptions> options,
    ILogger<RunbookCommandRunner> log)
{
    /// <summary>The commands running now, by machine and command — what stops one running twice at once.</summary>
    private readonly ConcurrentDictionary<string, byte> _running = new(StringComparer.Ordinal);

    /// <summary>What one press did.</summary>
    /// <param name="Ran">False when it was refused before anything ran; <see cref="Message"/> says why.</param>
    /// <param name="ExitCode">Null when it never finished.</param>
    /// <param name="Lines">The end of its output, cleaned and masked. Text, never markup.</param>
    public sealed record Outcome(bool Ran, int? ExitCode, string Message, IReadOnlyList<string> Lines, TimeSpan Took)
    {
        public bool Ok => Ran && ExitCode == 0;

        public static Outcome Refused(string why) => new(false, null, why, [], TimeSpan.Zero);
    }

    /// <summary>
    /// Why this connection cannot run runbook commands right now, or null when it can. The
    /// button asks before it is drawn, so a refusal is a disabled button with the reason
    /// beside it rather than a surprise after the confirmation.
    /// </summary>
    public string? Refusal(Connection connection)
    {
        if (!options.Value.Auth.Enabled)
        {
            return "Runbook commands need LabbyTwo to have a login — without one, anybody who can reach the " +
                   "dashboard could run them. Set LABBY_AUTH_PASSWORD and restart it.";
        }
        if (registry.Provider(connection.Provider) is not ICommandRunner)
        {
            return registry.Provider(connection.Provider) is { } provider
                ? $"{connection.Name} is a {provider.DisplayName} connection, which cannot run commands. Use an SSH host connection (the Terminal plugin)."
                : $"The {connection.Provider} integration is not installed.";
        }
        if (!connection.Enabled)
            return $"{connection.Name} is switched off.";
        if (!connection.Settings.GetBool(RunbookCommands.AllowKey))
            return $"{connection.Name} does not allow runbook commands. Tick “Allow runbook commands” on the connection to let notes run them.";
        return null;
    }

    /// <summary>Whether this command is running on this machine now.</summary>
    public bool IsRunning(Connection connection, string command) => _running.ContainsKey(Key(connection, command));

    /// <summary>
    /// Runs one command, through every check above, and waits for it. Never throws for
    /// anything the command or the machine can do wrong — that is an outcome.
    /// </summary>
    /// <param name="who">Who pressed it, for the change feed: their login name.</param>
    public async Task<Outcome> RunAsync(Connection connection, string command, TimeSpan timeout, string who, CancellationToken ct = default)
    {
        if (Refusal(connection) is { } refused)
            return Outcome.Refused(refused);
        if (registry.Provider(connection.Provider) is not ICommandRunner runner)
            return Outcome.Refused($"The {connection.Provider} integration cannot run commands.");

        timeout = TimeSpan.FromTicks(Math.Clamp(timeout.Ticks, RunbookCommands.ShortestTimeout.Ticks, RunbookCommands.LongestTimeout.Ticks));
        var key = Key(connection, command);
        if (!_running.TryAdd(key, 0))
            return Outcome.Refused("That command is already running on " + connection.Name + ". Wait for it to finish.");

        var started = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        CommandResult result;
        try
        {
            // The provider is told the timeout, and this is the backstop for one that ignores
            // it: a few seconds' grace to report its own timeout before it is cut off.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout + TimeSpan.FromSeconds(5));
            log.LogInformation("{Who} is running a runbook command on {Connection}: {Command}", who, connection.Name, command);
            try
            {
                result = await runner.RunCommandAsync(connection, command, timeout, limit.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                result = CommandResult.Failed($"Stopped after {Ago.Duration(timeout)} without finishing.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = CommandResult.Failed(ProbeError.Describe(ex, connection.Settings.Get("host")));
            }
        }
        finally
        {
            _running.TryRemove(key, out _);
        }
        stopwatch.Stop();

        var lines = RunbookCommands.Tail(result.Output, LogSearch.Mask);
        var message = result.Error is { } error ? error
            : result.ExitCode == 0 ? "Finished with exit code 0."
            : $"Finished with exit code {result.ExitCode}.";
        var outcome = new Outcome(true, result.ExitCode, message, lines, stopwatch.Elapsed);

        await RecordAsync(connection, command, who, started, outcome);
        return outcome;
    }

    private async Task RecordAsync(Connection connection, string command, string who, DateTimeOffset at, Outcome outcome)
    {
        var shown = command.Length > 120 ? command[..117] + "…" : command;
        var how = outcome.ExitCode is { } code ? $"exit code {code}" : outcome.Message.TrimEnd('.');
        try
        {
            // Not cancelled with the page: somebody closing the tab mid-command does not
            // un-run it, and the feed is where they would look to see what it did.
            await changes.RecordAsync(new Change(at,
                ChangeKinds.Command,
                outcome.Ok ? ChangeActions.Completed : ChangeActions.Failed,
                connection.Id,
                shown,
                $"{who} ran “{shown}” on {connection.Name}",
                $"{how} · {Ago.Duration(outcome.Took)} · from a note"));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not record a runbook command on {Connection} in the change feed", connection.Name);
        }
    }

    private static string Key(Connection connection, string command) => connection.Id + "\n" + command;
}
