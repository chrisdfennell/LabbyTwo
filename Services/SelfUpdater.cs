using System.Text.Json;
using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// Updates LabbyTwo from inside LabbyTwo.
///
/// A container cannot replace itself — the moment it stops, whatever was doing the work
/// stops too. So this does what a person would do over SSH: it starts a throwaway
/// Watchtower container over the Docker socket and lets *that* pull the new image and
/// recreate this one. LabbyTwo dies mid-request and comes back a few seconds later on the
/// new version.
///
/// Three things have to be true, and each is reported rather than assumed:
/// the socket is mounted, this process can work out which container it is, and the image
/// it is running came from a registry. A locally built image has nothing to compare
/// against and nothing to pull.
/// </summary>
public sealed class SelfUpdater(ConfigStore config, IHttpClientFactory httpFactory, ILogger<SelfUpdater> log)
{
    public const string WatchtowerImage = "containrrr/watchtower";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <param name="Container">This container's name, as Docker knows it.</param>
    /// <param name="Image">The image reference it was started from.</param>
    /// <param name="Digest">The repo digest actually running, or null for a local build.</param>
    public sealed record Self(string Container, ImageRef Image, string? Digest);

    /// <param name="Ready">Whether the button should be offered at all.</param>
    /// <param name="Behind">True only when a newer digest is definitely published.</param>
    /// <param name="Reason">Why it cannot update, or why the comparison was inconclusive.</param>
    public sealed record Status(bool Ready, bool Behind, Self? Self, string? Reason);

    /// <summary>
    /// The socket to use: whichever the user configured on a Docker connection, falling
    /// back to the standard path. Reusing their connection means the endpoint they already
    /// got working — a named pipe, a TCP address — is the one this uses too.
    /// </summary>
    private async Task<string?> EndpointAsync()
    {
        var connections = await config.ConnectionsAsync();
        if (connections.FirstOrDefault(c => c.Provider == "docker" && c.Enabled) is { } docker)
            return docker.Settings.Get("endpoint", DockerSocket.DefaultEndpoint);

        // No Docker connection is fine — the socket may still be mounted. Only a path can
        // be checked for existence; a pipe or TCP address has to be tried.
        return File.Exists(DockerSocket.DefaultEndpoint) ? DockerSocket.DefaultEndpoint : null;
    }

    /// <param name="checkRegistry">
    /// Whether to ask the registry what is published. Left off by default so opening the
    /// settings page contacts nothing — the page promises that LabbyTwo does not phone home
    /// until asked, and quietly querying Docker Hub to render a button would break it.
    /// </param>
    public async Task<Status> StatusAsync(bool checkRegistry = false, CancellationToken ct = default)
    {
        var endpoint = await EndpointAsync();
        if (endpoint is null)
        {
            return new Status(false, false, null,
                "The Docker socket is not mounted, so nothing here can start the update for you.");
        }

        try
        {
            var self = await IdentifyAsync(endpoint, ct);
            if (self is null)
            {
                return new Status(false, false, null,
                    "LabbyTwo could not work out which container it is running in. That happens outside " +
                    "Docker, or when the container's hostname has been overridden.");
            }

            if (self.Digest is null)
            {
                return new Status(false, false, self,
                    $"This container runs {self.Image}, which was built here rather than pulled from a " +
                    "registry. There is no published image to update to — switch the compose file to " +
                    "`image:` first, or keep using install.sh.");
            }

            if (!self.Image.IsDockerHub)
            {
                // Watchtower can still do the update; only the "is there a newer one"
                // question needs a registry API this does not speak.
                return new Status(true, false, self,
                    $"Running from {self.Image.Registry}, which this cannot query for a newer digest. " +
                    "Updating will pull whatever that tag points at now.");
            }

            if (!checkRegistry)
                return new Status(true, false, self, null);

            var published = await PublishedDigestAsync(self.Image, ct);
            if (published is null)
                return new Status(true, false, self, "Docker Hub did not report a digest for that tag.");

            var behind = !string.Equals(published, self.Digest, StringComparison.OrdinalIgnoreCase);
            return new Status(true, behind, self,
                behind ? null : "The running image is the one published for that tag.");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Self-update status check failed");
            return new Status(false, false, null, ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Which container this process is. Inside Docker the hostname is the container's short
    /// id unless somebody overrode it, which is what makes this possible without being told.
    /// </summary>
    private static async Task<Self?> IdentifyAsync(string endpoint, CancellationToken ct)
    {
        string inspected;
        try
        {
            inspected = await DockerSocket.GetAsync(endpoint, Timeout, $"/containers/{Environment.MachineName}/json", ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        using var container = JsonDocument.Parse(inspected);
        var name = container.RootElement.TryGetProperty("Name", out var rawName)
            ? rawName.GetString()?.TrimStart('/') ?? ""
            : "";

        var image = container.RootElement.TryGetProperty("Config", out var configNode)
                    && configNode.TryGetProperty("Image", out var imageName)
            ? imageName.GetString() ?? ""
            : "";

        if (name.Length == 0 || image.Length == 0)
            return null;

        // RepoDigests is empty for an image built locally, which is exactly the signal
        // that there is nothing published to compare against.
        string? digest = null;
        try
        {
            using var inspectedImage = JsonDocument.Parse(
                await DockerSocket.GetAsync(endpoint, Timeout, $"/images/{Uri.EscapeDataString(image)}/json", ct));

            if (inspectedImage.RootElement.TryGetProperty("RepoDigests", out var digests) &&
                digests.ValueKind == JsonValueKind.Array && digests.GetArrayLength() > 0 &&
                digests[0].GetString() is { } first && first.Contains('@'))
                digest = first[(first.IndexOf('@') + 1)..];
        }
        catch (InvalidOperationException)
        {
        }

        return new Self(name, ImageRef.Parse(image), digest);
    }

    private async Task<string?> PublishedDigestAsync(ImageRef image, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(Providers.ProviderHttp.ClientName);
        var url = $"https://hub.docker.com/v2/repositories/{image.HubRepository}/tags/{image.Tag}";

        using var document = JsonDocument.Parse(await http.GetStringAsync(url, ct));
        return document.RootElement.TryGetProperty("digest", out var digest) ? digest.GetString() : null;
    }

    /// <summary>
    /// Starts the one-shot Watchtower and returns. There is no success to report back: if
    /// this works, this process is killed a few seconds later, mid-response.
    /// </summary>
    public async Task StartUpdateAsync(CancellationToken ct = default)
    {
        var endpoint = await EndpointAsync()
                       ?? throw new InvalidOperationException("The Docker socket is not available.");

        var self = await IdentifyAsync(endpoint, ct)
                   ?? throw new InvalidOperationException("Could not identify this container.");

        log.LogWarning("Self-update requested — starting a one-shot {Image} to replace {Container}",
            WatchtowerImage, self.Container);

        // Pull first. On a host that has never run Watchtower, creating the container would
        // otherwise fail with "no such image" and leave nothing to show for the click.
        await DockerSocket.PostAsync(endpoint, TimeSpan.FromMinutes(3),
            $"/images/create?fromImage={Uri.EscapeDataString(WatchtowerImage)}&tag=latest", null, ct);

        var request = JsonSerializer.Serialize(new
        {
            Image = $"{WatchtowerImage}:latest",
            // --run-once so it does the job and exits, rather than becoming a second
            // scheduler competing with whatever the user already runs.
            Cmd = new[] { "--run-once", "--cleanup", self.Container },
            HostConfig = new
            {
                Binds = new[] { $"{endpoint}:/var/run/docker.sock" },
                AutoRemove = true,
            },
        });

        using var created = JsonDocument.Parse(await DockerSocket.PostAsync(
            endpoint, Timeout, "/containers/create", request, ct));

        var id = created.RootElement.TryGetProperty("Id", out var identifier) ? identifier.GetString() : null;
        if (id is null)
            throw new InvalidOperationException("Docker did not return an id for the update container.");

        await DockerSocket.PostAsync(endpoint, Timeout, $"/containers/{id}/start", null, ct);
    }
}
