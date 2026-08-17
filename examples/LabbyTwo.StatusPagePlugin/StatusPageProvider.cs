using LabbyTwo.Core;

namespace LabbyTwo.StatusPagePlugin;

/// <summary>
/// Where the public status page gets its token, its title and — the part worth thinking
/// about — the list of what it is allowed to say out loud.
///
/// The whole point of the page is that it answers without a login, so every setting here is
/// really a decision about what a stranger with the link is told. That is why the published
/// list is opt-out by name rather than a checkbox marked "public".
/// </summary>
public sealed class StatusPageProvider : IConnectionProvider
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
        new("token", "Link token", FieldKind.Password, Required: true,
            Help: "The ?k= on the end of the link, and the only thing standing between the page and the " +
                  "open internet if you forward a port to it. Generate one with: openssl rand -hex 24"),

        new("title", "Page title", FieldKind.Text, "Service status", Default: "Service status"),

        new("publish", "Publish these", FieldKind.Textarea,
            Help: "One connection name per line, exactly as it appears on the Connections page. " +
                  "Leave it blank to publish everything monitored — fine at home, and worth a second " +
                  "thought before the link leaves the house, because the names alone are a map of what you run."),

        new("uptime_days", "Uptime over (days)", FieldKind.Number, Default: "7") { Advanced = true },

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
                "A link token of at least 16 characters is needed — the page answers without a login, " +
                "so the token is the whole of the door."));

        return Task.FromResult(ProbeResult.Up(TimeSpan.Zero,
            $"Share {ExtensionRoutes.PathFor(StatusPageEndpoints.RouteKey)}?k= followed by the token."));
    }
}
