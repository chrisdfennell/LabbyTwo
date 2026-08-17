using System.Security.Cryptography;
using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.StatusPagePlugin;

/// <summary>
/// Where the public status page gets its token, its title and — the part worth thinking
/// about — the list of what it is allowed to say out loud.
///
/// The whole point of the page is that it answers without a login, so every setting here is
/// really a decision about what a stranger with the link is told. That is why the published
/// list is opt-out by name rather than a checkbox marked "public".
/// </summary>
public sealed class StatusPageProvider(ConfigStore config) : IConnectionProvider
{
    public const string ProviderType = "status-page";

    public string Type => ProviderType;
    public string DisplayName => "Public status page";
    public string Icon => "🚦";
    public string Category => "Monitoring";

    public string Description =>
        "A read-only status page anyone with the link can open, without a LabbyTwo login. " +
        "For the “is Plex down or is it me” question, answered without handing out an account.";

    public bool IsMonitored => false;

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("token", "Link token", FieldKind.Password,
            Help: "Leave it blank and press Save — the Make a link button below fills it in for you. " +
                  "It is the ?k= on the end of the link, and the only thing standing between the page " +
                  "and the open internet if you forward a port to it."),

        new("title", "Page title", FieldKind.Text, "Service status", Default: "Service status"),

        new("publish", "Publish these", FieldKind.Textarea,
            Help: "One connection name per line, exactly as it appears on the Connections page. " +
                  "Leave it blank to publish everything monitored — fine at home, and worth a second " +
                  "thought before the link leaves the house, because the names alone are a map of what you run."),

        new("uptime_days", "Uptime over (days)", FieldKind.Number, Default: "7") { Advanced = true },

        new("show_history", "Show recent changes", FieldKind.Bool, Default: "true",
            Help: "A short list of what went down and came back, with times but no messages. " +
                  "It answers “was it down earlier, or is it just me?”, which is the second thing " +
                  "anyone opening this page wants to know."),

        new("refresh_seconds", "Refresh every (seconds)", FieldKind.Number, Default: "60",
            Help: "The page reloads itself, so a phone left open on the counter stays current. " +
                  "Zero turns it off.")
        {
            Advanced = true,
        },

        new("footer", "Footer note", FieldKind.Text,
            Help: "Shown under the list. Somewhere to say who to shout at, or that maintenance is planned.")
        {
            Advanced = true,
        },
    ];

    public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var token = connection.Settings.Get("token");
        if (token.Length < 16)
            return Task.FromResult(ProbeResult.Down(TimeSpan.Zero,
                "No link yet. Press “Make a link” and this becomes the address you can hand out."));

        return Task.FromResult(ProbeResult.Up(TimeSpan.Zero,
            $"Published at {Path(token)} — press “Show the link” for the whole thing."));
    }

    /// <summary>
    /// Two buttons, because the token is a password field: it is encrypted at rest and never
    /// rendered back to the browser, so once saved there is no way to read it out of the
    /// form. Without these, losing the link means the page is unreachable and the only
    /// remedy is to guess that replacing the token is what fixes it.
    /// </summary>
    public IReadOnlyList<ProviderAction> Actions =>
    [
        new("link", "Show the link", "🔗")
        {
            Description = "Prints the address to hand out, token and all.",
            Confirms = false,
        },
        new("new-token", "Make a link", "🎲")
        {
            Description = "Generates a token and saves it. Use this the first time, or to revoke.",
            ConfirmMessage = "A new token replaces the old one, so any link already handed out stops "
                             + "working. That is the point if you are revoking; it is a nuisance otherwise.",
            Dangerous = true,
        },
    ];

    /// <summary>
    /// Confirming only matters once there is something to lose. On a connection with no
    /// token yet, "Make a link" is the ordinary first step and a dialog warning about
    /// breaking existing links would be warning about nothing.
    /// </summary>
    public IReadOnlyList<ProviderAction> ActionsFor(Connection connection) =>
        connection.Settings.Get("token").Length >= 16
            ? Actions
            : [.. Actions
                .Where(action => action.Id == "new-token")
                .Select(action => action with { Dangerous = false, ConfirmMessage = null, Confirms = false })];

    public async Task<ActionResult> RunActionAsync(
        Connection connection, ProviderAction action, SettingsBag input, CancellationToken ct)
    {
        switch (action.Id)
        {
            case "link":
                var existing = connection.Settings.Get("token");
                return existing.Length >= 16
                    ? ActionResult.Done(Hand(existing))
                    : ActionResult.Failed("There is no token yet — press “Make a link” first.");

            case "new-token":
                // 24 bytes of real randomness. Long enough that guessing is not a strategy,
                // short enough to survive being pasted into a message.
                var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

                var settings = connection.Settings.Clone();
                settings["token"] = token;
                await config.SaveConnectionAsync(connection with { Settings = settings }, ct);

                return ActionResult.Done(Hand(token));

            default:
                return ActionResult.Failed($"The status page does not know how to run “{action.Id}”.");
        }
    }

    private static string Path(string token) =>
        $"{ExtensionRoutes.PathFor(StatusPageEndpoints.RouteKey)}?k={token}";

    /// <summary>
    /// LabbyTwo does not know what address you reach it on — it could be a LAN IP, a
    /// hostname or something behind a reverse proxy — so the link is given as a path with
    /// the one instruction needed to finish it.
    /// </summary>
    private static string Hand(string token) =>
        $"Put this on the end of your dashboard's address — the one in your browser's bar right now: {Path(token)}";
}
