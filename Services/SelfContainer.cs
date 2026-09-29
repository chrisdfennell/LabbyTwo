using System.Text.RegularExpressions;

namespace LabbyTwo.Services;

/// <summary>
/// Which container this process runs in, without asking Docker.
///
/// The hostname used to be the whole answer: Docker sets it to the container's short id.
/// But Watchtower recreates a container by copying its config, hostname included, so after
/// an update the new container still carries the old one's id as its hostname — an id that
/// no longer exists. Everything that relied on it then failed quietly: "Update now" said it
/// could not work out which container it was, and the Containers tab stopped recognising
/// its own row, until someone recreated the container by hand over SSH.
///
/// The files Docker bind-mounts into every container — /etc/hostname, /etc/hosts,
/// /etc/resolv.conf — live under /var/lib/docker/containers/&lt;full id&gt;/, and that path is
/// listed in /proc/self/mountinfo. It is the real id, whatever the hostname says. cgroup
/// v1 hosts name it in /proc/self/cgroup as well, which is read when mountinfo has nothing.
/// </summary>
public static partial class SelfContainer
{
    private static readonly Lazy<string?> FromProc = new(() =>
        IdFrom(Read("/proc/self/mountinfo")) ?? IdFrom(Read("/proc/self/cgroup")));

    /// <summary>The full container id, or null outside Docker or where /proc does not say.</summary>
    public static string? Id => FromProc.Value;

    /// <summary>
    /// What to match this container by: its real id where /proc gives it, otherwise the
    /// hostname, which is still right for any container Watchtower has not recreated.
    /// </summary>
    public static string Hint => Id ?? Environment.MachineName;

    /// <summary>The first container id named in a mountinfo or cgroup listing.</summary>
    public static string? IdFrom(string? listing)
    {
        if (string.IsNullOrEmpty(listing))
            return null;

        var match = ContainerPath().Match(listing);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? Read(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // "/docker/containers/<id>/hostname" in mountinfo; "/docker/<id>" or "docker-<id>.scope"
    // in a cgroup v1 listing. Exactly 64 hex characters, so an image layer id — also 64 hex,
    // but under /overlay2/ or /image/ — is not mistaken for one.
    [GeneratedRegex(@"(?:/containers/|/docker/|docker-)([0-9a-f]{64})(?=[/.\s]|$)", RegexOptions.Multiline)]
    private static partial Regex ContainerPath();
}
