using LabbyTwo.Core;

namespace LabbyTwo.Components.Pages.Kinds;

/// <summary>
/// Every container on one Docker host, and the buttons to run them — start, stop, restart,
/// pause, remove, logs, inspect.
///
/// A page rather than a card because that is the size of the job: forty containers on a NAS
/// need searching, grouping by Compose project and a logs panel, and a card that tried to be
/// all of that would be a page squeezed into a grid cell. The Containers card stays what it
/// is — a glance with one restart button.
///
/// In its own file, apart from the kinds in <see cref="GridTabKind"/>'s, because the rest
/// of this page's machinery — log streaming, stats polling, the proxy probes — is its own too.
/// </summary>
public sealed class ContainersTabKind : ITabKind
{
    public const string KindKey = "containers";

    public string Kind => KindKey;
    public string DisplayName => "Containers";
    public string Icon => "🐳";

    public string Description =>
        "Every container on a Docker host — state, health, CPU and memory, logs, and start/stop/restart. " +
        "Anyone who can open it controls every container, so keep a login on.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("connection", "Docker connection", FieldKind.Connection,
            Help: "Which Docker host to show. Blank uses the first Docker connection. This page can stop and " +
                  "remove any container on it — whoever can open the page has that power, so keep LabbyTwo's " +
                  "login switched on.")
            { ProviderFilter = "docker" },

        new("protected", "Protected containers", FieldKind.Textarea, "cloudflared\ntraefik",
            Help: "One per line (or comma-separated), * as a wildcard; matched against the container name and " +
                  "its Compose service. Stopping, pausing or removing one of these needs its name typed out, " +
                  "and project-wide buttons leave them alone. LabbyTwo's own container is always protected."),

        new("allow_actions", "Allow start, stop and restart", FieldKind.Bool, Default: "true",
            Help: "Off makes the page read-only: list, stats, logs and inspect, but no buttons that change anything."),

        new("stats", "Show live CPU and memory", FieldKind.Bool, Default: "true",
            Help: "Read every ten seconds while the page is open and visible, four containers at a time."),

        new("update_hint", "Point out containers running an older image", FieldKind.Bool, Default: "true",
            Help: "When a newer image with the same tag has been pulled but the container was not recreated. " +
                  "Compares local images only — no registry is contacted.") { Advanced = true },

        new("log_lines", "Log lines to load", FieldKind.Number, Default: "200") { Advanced = true },
    ];

    public Type Component => typeof(ContainersTab);
}
