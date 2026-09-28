using System.Text.RegularExpressions;

namespace LabbyTwo.Services;

/// <summary>
/// The rules that keep the Containers tab from being the fastest way to take a home lab
/// down by accident.
///
/// Two kinds of container get more than a confirmation. LabbyTwo's own, because stopping it
/// from here is the one action this page can never undo — the button to start it again is
/// on the page that just went away. And whatever the tab's owner lists as protected, which
/// on most setups is the tunnel or the reverse proxy: stopping cloudflared from a phone
/// outside the house is how you find out you cannot get back in. For those, stopping,
/// pausing and removing need the container's name typed out; a restart, which comes back
/// by itself, gets a plain confirmation with a sharper warning.
/// </summary>
public static class ContainerSafety
{
    /// <summary>
    /// Whether this is the container LabbyTwo runs in. Inside Docker the hostname is the
    /// container's short id unless a compose file set another, so a matching id prefix is
    /// the same test Docker makes for a short id; a hostname set to the container's name is
    /// caught by the second half.
    /// </summary>
    public static bool IsSelf(ContainerRow container, string hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            return false;

        return (hostname.Length >= 12 && container.Id.StartsWith(hostname, StringComparison.OrdinalIgnoreCase))
               || container.Name.Equals(hostname, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The tab's protected list: one per line or comma-separated, <c>*</c> as a wildcard.</summary>
    public static IReadOnlyList<string> ParseList(string? setting) =>
    [
        .. (setting ?? "")
            .Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(entry => entry.Length > 0),
    ];

    /// <summary>
    /// Matched against the container name and its Compose service, so "cloudflared" covers
    /// the container a compose project called "tunnel-cloudflared-1" as well.
    /// </summary>
    public static bool IsListed(ContainerRow container, IReadOnlyList<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$";
            if (Regex.IsMatch(container.Name, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                (container.Service is { } service &&
                 Regex.IsMatch(service, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Whether the action has to be confirmed by typing the container's name. Removing
    /// always does — it is the one action nothing here can reverse. Stopping or pausing does
    /// for a protected container, since either leaves it down until someone brings it back.
    /// </summary>
    public static bool NeedsTypedName(ContainerAction action, bool self, bool listed) =>
        action == ContainerAction.Remove ||
        ((self || listed) && action is ContainerAction.Stop or ContainerAction.Pause);

    /// <summary>What the confirmation says. The sentence differs by consequence, not by verb.</summary>
    public static string Warning(ContainerAction action, ContainerRow container, bool self, bool listed)
    {
        var name = container.Name;
        if (self)
        {
            return action switch
            {
                ContainerAction.Stop or ContainerAction.Pause =>
                    $"“{name}” is the container LabbyTwo itself runs in. It will not come back on its own — " +
                    "this page goes away with it, and starting it again needs a terminal or another Docker tool. " +
                    "Only do this if you mean it.",
                ContainerAction.Restart =>
                    $"“{name}” is the container LabbyTwo itself runs in. This page will go away and reconnect " +
                    "when it has restarted.",
                _ => $"“{name}” is the container LabbyTwo itself runs in.",
            };
        }

        var protectedNote = listed
            ? " It is on this tab's protected list — if it carries your tunnel or reverse proxy, you may lose the way back in."
            : "";

        return action switch
        {
            ContainerAction.Start => $"“{name}” will be started.",
            ContainerAction.Stop =>
                $"“{name}” will stop and stay stopped until somebody starts it again. Anything depending on it goes down with it." +
                protectedNote,
            ContainerAction.Restart =>
                $"“{name}” will stop and start again. Anything depending on it is down until it comes back." + protectedNote,
            ContainerAction.Pause =>
                $"“{name}” will be frozen in place — its processes stop running but keep their memory. " +
                "Connections to it hang rather than fail." + protectedNote,
            ContainerAction.Unpause => $"“{name}” will carry on from where it was paused.",
            ContainerAction.Remove =>
                $"“{name}” will be deleted. Its image and named volumes stay; anything written inside the container " +
                "itself is gone for good, and so is its configuration unless a compose file or a script can recreate it." +
                protectedNote,
            _ => "",
        };
    }

    /// <summary>
    /// The containers a bulk action on a Compose project would touch, and the ones it would
    /// leave alone and why. LabbyTwo's own container and protected ones are always left out
    /// of a bulk stop, pause or restart: acting on them is worth doing one at a time, with
    /// the typed confirmation, not as a side effect of "restart the media stack".
    /// </summary>
    public static (IReadOnlyList<ContainerRow> Act, IReadOnlyList<(ContainerRow Container, string Why)> Skip) PlanBulk(
        ContainerAction action, IEnumerable<ContainerRow> containers, string hostname, IReadOnlyList<string> protectedList)
    {
        var act = new List<ContainerRow>();
        var skip = new List<(ContainerRow, string)>();
        foreach (var container in containers)
        {
            var self = IsSelf(container, hostname);
            var listed = IsListed(container, protectedList);
            if (!DockerContainers.Applies(action, container))
                skip.Add((container, $"already {Describe(container)}"));
            else if (self && action != ContainerAction.Start)
                skip.Add((container, "LabbyTwo itself — do it on its own"));
            else if (listed && action != ContainerAction.Start && action != ContainerAction.Unpause)
                skip.Add((container, "protected — do it on its own"));
            else
                act.Add(container);
        }
        return (act, skip);
    }

    private static string Describe(ContainerRow container) =>
        container.IsStopped ? "stopped" : container.State;

    /// <summary>
    /// The connections that reach this container by its name — <c>http://sonarr:8989</c>, or a
    /// host field of <c>sonarr</c> — which is how the README recommends writing them when
    /// LabbyTwo shares a Docker network with the thing it watches. Addresses by IP are not
    /// matched: there is no telling from here which container owns a published port.
    /// </summary>
    public static IReadOnlyList<LabbyTwo.Core.Connection> ConnectionsReaching(
        ContainerRow container, IEnumerable<LabbyTwo.Core.Connection> connections)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { container.Name };
        if (container.Service is { } service)
            names.Add(service);

        return
        [
            .. connections.Where(connection => connection.Provider != "docker" &&
                                               connection.Settings.Values.Any(value => HostOf(value) is { } host && names.Contains(host))),
        ];
    }

    /// <summary>The host in a URL or a bare "host" or "host:port", or null for anything else.</summary>
    public static string? HostOf(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 300 || value.Any(char.IsWhiteSpace))
            return null;

        if (value.Contains("://", StringComparison.Ordinal))
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : null;

        var host = value.Split(':', 2)[0];
        return Regex.IsMatch(host, "^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant) ? host : null;
    }

    /// <summary>One environment variable, split at its first "=".</summary>
    public sealed record EnvEntry(string Key, string Value);

    public static IReadOnlyList<EnvEntry> ParseEnv(IEnumerable<string> environment) =>
    [
        .. environment.Select(line =>
        {
            var equals = line.IndexOf('=');
            return equals < 0 ? new EnvEntry(line, "") : new EnvEntry(line[..equals], line[(equals + 1)..]);
        }),
    ];

    /// <summary>
    /// What a hidden value shows. The same length whatever the value, so the mask does not
    /// give away how long a password is.
    /// </summary>
    public const string Mask = "••••••••";

    public static string Display(EnvEntry entry, bool revealed) =>
        revealed || entry.Value.Length == 0 ? entry.Value : Mask;
}
