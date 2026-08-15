using LabbyTwo.Core;

namespace LabbyTwo.TerminalPlugin;

/// <summary>
/// A page of terminals: what you can open a shell on down one side, the shell itself
/// filling the rest.
///
/// The settings are the interesting part, because on this tab kind they are not
/// preferences — they are the boundary. What the page offers and what the socket will
/// open are read from the same row, so narrowing a tab to one container narrows it
/// everywhere rather than only in the list.
/// </summary>
public sealed class TerminalTabKind : ITabKind
{
    public const string KindKey = "terminal";

    public string Kind => KindKey;
    public string DisplayName => "Terminal";
    public string Icon => "🖥️";

    public string Description =>
        "A real shell in the dashboard — SSH to a host, or into a running container. Needs a login set.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("allow_ssh", "Offer SSH hosts", FieldKind.Bool, Default: "true",
            Help: "Every enabled SSH connection can be opened from this page."),

        new("allow_docker", "Offer containers", FieldKind.Bool, Default: "true",
            Help: "Running containers on every Docker connection. Needs the socket mounted, same as the " +
                  "Docker provider does."),

        new("containers", "Only these containers", FieldKind.Textarea,
            Help: "One name per line, or comma-separated. Empty means all of them. This is enforced when the " +
                  "terminal opens, not only in the list — so a page narrowed to one container really is " +
                  "narrowed to one container."),

        new("idle_minutes", "Close after idle (minutes)", FieldKind.Number, Default: "30",
            Help: "0 never closes it. A forgotten browser tab is a live shell, and the tablet on the kitchen " +
                  "wall is exactly where one gets forgotten."),

        new("shell", "Shell to run in containers", FieldKind.Text, TerminalPolicy.DefaultShell,
            Help: "The default asks the container for bash and settles for sh, which is right almost " +
                  "everywhere. Change it for an image whose shell is somewhere unusual.")
        { Advanced = true },
    ];

    public Type Component => typeof(TerminalTab);
}

/// <summary>
/// The same terminal as a card. Fixed to one target, because a dashboard card that could
/// be repointed from the dashboard would put the choice of what to open a shell on into
/// the hands of whoever is looking at the dashboard.
/// </summary>
public sealed class TerminalWidget : IWidgetType
{
    public const string TypeKey = "terminal";

    public string Type => TypeKey;
    public string DisplayName => "Terminal";
    public string Icon => "🖥️";
    public string Description => "A shell on one host or container, on the dashboard.";

    public int DefaultWidth => 6;

    /// <summary>
    /// No <c>ProviderTypes</c>: the connection is a field rather than the card's binding,
    /// because a Docker one also needs to be told which container, and a widget has one
    /// binding to give.
    /// </summary>
    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("connection", "Connection", FieldKind.Connection, Required: true,
            Help: "An SSH host, or a Docker connection — with the container named below."),

        new("container", "Container", FieldKind.Text,
            Help: "Only for a Docker connection. Leave it empty for SSH."),

        new("rows", "Height (rows)", FieldKind.Number, Default: "18"),

        new("idle_minutes", "Close after idle (minutes)", FieldKind.Number, Default: "30") { Advanced = true },

        new("shell", "Shell to run in containers", FieldKind.Text, TerminalPolicy.DefaultShell)
        { Advanced = true },
    ];

    public Type Component => typeof(TerminalCard);
}
