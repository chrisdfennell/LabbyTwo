using LabbyTwo.Core;

namespace LabbyTwo.Services;

/// <summary>
/// Finding the one-shot Watchtower helpers LabbyTwo started, reading the end of their
/// logs, and stopping one when somebody presses the button. Found by asking Docker rather
/// than remembered, so a helper started before a restart — or by an older LabbyTwo, before
/// helpers were labelled — is still found. Which ones count and when one is stuck is
/// <see cref="UpdateHelperRules"/>.
/// </summary>
public static class UpdateHelpers
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>How many log lines a stuck helper is shown with.</summary>
    public const int LogLines = 8;

    /// <summary>The helpers on this Docker host, running or not.</summary>
    public static async Task<IReadOnlyList<UpdateHelper>> FindAsync(string endpoint, CancellationToken ct = default) =>
        UpdateHelperRules.Find(await DockerSocket.GetAsync(endpoint, Timeout, "/containers/json?all=1", ct));

    /// <summary>The last few lines it logged — usually the one saying what it is stuck on.</summary>
    public static async Task<IReadOnlyList<string>> LogTailAsync(string endpoint, string id, CancellationToken ct = default)
    {
        var lines = await DockerLogs.TailAsync(endpoint, Timeout, id, tty: false, LogLines, ct);
        return [.. lines.Select(line => line.Text)];
    }

    /// <summary>
    /// Stops it. The helper was created to remove itself, so a stopped one goes away; a
    /// container it was in the middle of recreating may be left stopped, which the
    /// confirmation says.
    /// </summary>
    public static Task StopAsync(string endpoint, string id, CancellationToken ct = default) =>
        DockerSocket.PostAsync(endpoint, TimeSpan.FromSeconds(40), $"/containers/{Uri.EscapeDataString(id)}/stop?t=20", null, ct);
}
