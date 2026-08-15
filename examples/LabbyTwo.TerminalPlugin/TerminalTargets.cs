using LabbyTwo.Core;

namespace LabbyTwo.TerminalPlugin;

/// <summary>
/// One thing a terminal can be attached to: an SSH host, or a container on a Docker
/// connection. Both end up as a duplex byte stream, which is why one plugin covers them
/// rather than two — everything past <see cref="ITerminalSession"/> is identical.
///
/// The string form travels in a URL, so it is deliberately narrow: two shapes, no
/// escaping, and a container name cannot contain a colon so splitting is unambiguous.
/// </summary>
public sealed record TerminalTarget(string Kind, string ConnectionId, string Container = "")
{
    public const string Ssh = "ssh";
    public const string Docker = "docker";

    public string Id => Kind == Docker
        ? $"{Docker}:{ConnectionId}:{Container}"
        : $"{Ssh}:{ConnectionId}";

    public static TerminalTarget Of(Connection connection) => new(Ssh, connection.Id);

    public static TerminalTarget Of(Connection docker, string container) =>
        new(Docker, docker.Id, container);

    /// <summary>
    /// Null rather than an exception for anything malformed — this parses a query string,
    /// and a query string is whatever somebody typed.
    /// </summary>
    public static TerminalTarget? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var parts = raw.Split(':', 3);
        return parts switch
        {
            [Ssh, { Length: > 0 } connection] => new TerminalTarget(Ssh, connection),
            [Docker, { Length: > 0 } connection, { Length: > 0 } container]
                => new TerminalTarget(Docker, connection, container),
            _ => null,
        };
    }

    public bool IsDocker => Kind == Docker;
}

/// <summary>
/// What a particular tab or widget is allowed to attach to.
///
/// This exists because the picker on the page is decoration: the browser sends a target
/// in a query string, and anyone who can open the page can edit it. So the tab — or the
/// widget — is the policy object, its id travels with every attach, and the endpoint
/// resolves the policy from the database and asks it. A terminal tab pointed at one
/// container really is pointed at one container, rather than being a shell on the host
/// with a helpful list next to it.
/// </summary>
public sealed record TerminalPolicy
{
    public bool AllowSsh { get; init; } = true;
    public bool AllowDocker { get; init; } = true;

    /// <summary>Container names this may attach to. Empty means every container.</summary>
    public IReadOnlyList<string> Containers { get; init; } = [];

    /// <summary>Exactly one target and nothing else — what a widget carries.</summary>
    public string? Pinned { get; init; }

    /// <summary>
    /// The command run inside a container. Empty means the fallback below, which is the
    /// right answer often enough that nobody should have to type it.
    /// </summary>
    public string Shell { get; init; } = "";

    public int IdleMinutes { get; init; } = 30;

    /// <summary>
    /// Alpine has no bash and Debian's default sh is dash, so neither one is a safe guess.
    /// This asks for the better shell and settles for the other.
    ///
    /// It has to look before it leaps. The obvious spelling —
    /// <c>exec bash || exec sh</c> — does not work, because POSIX says a non-interactive
    /// shell exits when <c>exec</c> cannot find the command: on an image without bash the
    /// <c>||</c> is never reached and the terminal opens and closes again in the same
    /// instant. <c>command -v</c> tests without replacing the process, so the fallback
    /// survives to be used.
    /// </summary>
    public const string DefaultShell = "/bin/sh -c 'command -v bash >/dev/null 2>&1 && exec bash || exec sh'";

    public string ShellOrDefault => Shell.Trim() is { Length: > 0 } shell ? shell : DefaultShell;

    public static TerminalPolicy ForTab(SettingsBag settings) => new()
    {
        AllowSsh = settings.GetBool("allow_ssh", true),
        AllowDocker = settings.GetBool("allow_docker", true),
        Containers = Split(settings.Get("containers")),
        Shell = settings.Get("shell"),
        IdleMinutes = settings.GetInt("idle_minutes", 30),
    };

    /// <summary>
    /// A card is always one target, assembled from the two fields the host can render a
    /// picker for: a connection, and — if it is a Docker one — which container.
    /// </summary>
    public static TerminalPolicy ForWidget(SettingsBag settings)
    {
        var connection = settings.Get("connection");
        var container = settings.Get("container");

        return new TerminalPolicy
        {
            Pinned = connection.Length == 0 ? null
                : container.Length > 0 ? new TerminalTarget(TerminalTarget.Docker, connection, container).Id
                : new TerminalTarget(TerminalTarget.Ssh, connection).Id,
            Shell = settings.Get("shell"),
            IdleMinutes = settings.GetInt("idle_minutes", 30),
        };
    }

    /// <summary>The single target a card is fixed to, or null if it has not been set up yet.</summary>
    public TerminalTarget? PinnedTarget => TerminalTarget.Parse(Pinned);

    /// <summary>The reason this target is not allowed, or null if it is.</summary>
    public string? Refuse(TerminalTarget target)
    {
        if (Pinned is { Length: > 0 } pinned)
        {
            return string.Equals(pinned, target.Id, StringComparison.Ordinal)
                ? null
                : "This card is fixed to one target and that is not it.";
        }

        if (target.IsDocker)
        {
            if (!AllowDocker)
                return "This page does not open container shells.";

            if (Containers.Count > 0 &&
                !Containers.Contains(target.Container, StringComparer.OrdinalIgnoreCase))
            {
                return $"“{target.Container}” is not one of the containers this page may open.";
            }

            return null;
        }

        return AllowSsh ? null : "This page does not open SSH sessions.";
    }

    public bool Allows(TerminalTarget target) => Refuse(target) is null;

    private static IReadOnlyList<string> Split(string raw) =>
        [.. raw.Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
