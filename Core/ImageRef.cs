namespace LabbyTwo.Core;

/// <summary>
/// A container image reference, pulled apart. Needed because deciding whether an update is
/// available means asking a registry about a repository and a tag, and all of that arrives
/// as one string like <c>ghcr.io/someone/app:v2</c>.
/// </summary>
/// <param name="Registry">Host name, or "docker.io" when the reference does not name one.</param>
/// <param name="Repository">Everything between the registry and the tag.</param>
/// <param name="Tag">The tag, defaulting to "latest".</param>
public sealed record ImageRef(string Registry, string Repository, string Tag)
{
    public const string DockerHub = "docker.io";

    public bool IsDockerHub => Registry.Equals(DockerHub, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What Docker Hub's API wants. Official images live under "library", so plain
    /// <c>postgres</c> is really <c>library/postgres</c> as far as the API is concerned.
    /// </summary>
    public string HubRepository => Repository.Contains('/') ? Repository : $"library/{Repository}";

    public override string ToString() =>
        IsDockerHub ? $"{Repository}:{Tag}" : $"{Registry}/{Repository}:{Tag}";

    /// <summary>
    /// Parses a reference the way Docker does. The awkward part is that a colon means
    /// either a tag or a registry port, and a slash means either a registry or a user —
    /// which is why <c>localhost:5000/app</c> and <c>someone/app:5000</c> have to be told
    /// apart by looking at what is before the first slash rather than by splitting on ':'.
    /// </summary>
    public static ImageRef Parse(string? image)
    {
        var text = (image ?? "").Trim();
        if (text.Length == 0)
            return new ImageRef(DockerHub, "", "latest");

        // A digest pins the exact image; the repository is still the part before it.
        var at = text.IndexOf('@');
        if (at >= 0)
            text = text[..at];

        var registry = DockerHub;
        var remainder = text;

        var slash = text.IndexOf('/');
        if (slash > 0)
        {
            var head = text[..slash];

            // Only a registry if it looks like a host: has a dot, has a port, or is
            // localhost. Otherwise it is a Docker Hub user name.
            if (head.Contains('.') || head.Contains(':') || head.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                registry = head;
                remainder = text[(slash + 1)..];
            }
        }

        var tag = "latest";
        var colon = remainder.LastIndexOf(':');
        if (colon >= 0 && !remainder[(colon + 1)..].Contains('/'))
        {
            tag = remainder[(colon + 1)..];
            remainder = remainder[..colon];
        }

        return new ImageRef(registry, remainder, tag.Length > 0 ? tag : "latest");
    }
}
