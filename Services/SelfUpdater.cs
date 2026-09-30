using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using LabbyTwo.Core;
using LabbyTwo.Storage;
using Microsoft.Extensions.Options;

namespace LabbyTwo.Services;

/// <summary>
/// Updates LabbyTwo from inside LabbyTwo.
///
/// A container cannot replace itself — the moment it stops, whatever was doing the work
/// stops too. So something outside has to pull the new image and recreate this one, and
/// there are two ways to get it done:
///
/// <list type="bullet">
/// <item>Ask a Watchtower that is already running, through its HTTP API. LabbyTwo holds a
/// token that means "check for updates now" and nothing else; the Docker socket stays with
/// Watchtower. This is the one to prefer, and it is used whenever it is configured.</item>
/// <item>Start a throwaway Watchtower over the Docker API. That needs permission to create
/// containers, which is root on the host however the API is reached — a socket proxy that
/// allows it is not restricting anything that matters.</item>
/// </list>
///
/// For the second, three things have to be true, and each is reported rather than assumed:
/// Docker is reachable, this process can work out which container it is, and the image it
/// is running came from a registry. A locally built image has nothing to compare against
/// and nothing to pull.
/// </summary>
public sealed class SelfUpdater(
    ConfigStore config, IHttpClientFactory httpFactory, IOptions<LabbyOptions> options, ILogger<SelfUpdater> log)
{
    public const string WatchtowerImage = "containrrr/watchtower";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long to wait for Watchtower to answer an update request. It answers only once it
    /// has finished, and when it finds a new image, finishing includes stopping this
    /// process — so a long wait is the normal shape of success, and the answer that does
    /// arrive in time means it found nothing to do.
    /// </summary>
    private static readonly TimeSpan WatchtowerWait = TimeSpan.FromMinutes(3);

    /// <summary>How "Update now" would do its job, so the page can say which.</summary>
    public enum UpdateMode
    {
        /// <summary>It cannot: neither a Watchtower API nor Docker to start one with.</summary>
        None,

        /// <summary>Create a one-shot Watchtower through the Docker API.</summary>
        DockerApi,

        /// <summary>Ask a long-running Watchtower through its HTTP API.</summary>
        WatchtowerApi,
    }

    /// <param name="Container">This container's name, as Docker knows it.</param>
    /// <param name="Image">The image reference it was started from.</param>
    /// <param name="Digest">The repo digest actually running, or null for a local build.</param>
    /// <param name="Dns">The DNS servers this container was given with "dns:", if any.</param>
    /// <param name="Network">The network it is on, which a helper has to join to reach a socket proxy by name.</param>
    public sealed record Self(string Container, ImageRef Image, string? Digest, string[]? Dns = null, string? Network = null);

    /// <param name="Ready">Whether the button should be offered at all.</param>
    /// <param name="Behind">True only when a newer digest is definitely published.</param>
    /// <param name="Reason">Why it cannot update, or why the comparison was inconclusive.</param>
    /// <param name="Mode">Which of the two ways "Update now" would use.</param>
    public sealed record Status(bool Ready, bool Behind, Self? Self, string? Reason, UpdateMode Mode = UpdateMode.None);

    private LabbyOptions.WatchtowerSettings Watchtower => options.Value.Watchtower;

    /// <summary>
    /// How an update of any container would be done: through the Watchtower API when one
    /// is configured, a one-shot Watchtower otherwise. Whether the second can actually
    /// work — Docker reachable, a proxy that lets containers be created — is found out by
    /// trying, and the refusal names the flag.
    /// </summary>
    public UpdateMode ContainerMode => Watchtower.Enabled ? UpdateMode.WatchtowerApi : UpdateMode.DockerApi;

    private const string NoDocker =
        "LabbyTwo cannot reach Docker: there is no Docker connection, DOCKER_HOST is not set, and the socket is " +
        "not mounted at /var/run/docker.sock.";

    /// <summary>
    /// Restarts the container LabbyTwo is running in.
    ///
    /// The call does not return in the useful sense: Docker stops this process partway
    /// through answering, so the circuit drops and the page has to notice and reconnect.
    /// That is the honest behaviour of asking a program to restart itself, and the button
    /// says so rather than appearing to hang.
    ///
    /// It exists because the plugin updater needs one. Plugins are replaced during startup,
    /// so "update them, then restart" is the whole flow, and until now the second half meant
    /// finding a terminal.
    /// </summary>
    public async Task RestartSelfAsync(CancellationToken ct = default)
    {
        var endpoint = await EndpointAsync()
            ?? throw new InvalidOperationException(NoDocker + " Nothing here can restart the container.");

        var self = await IdentifyAsync(endpoint, ct)
            ?? throw new InvalidOperationException(
                "LabbyTwo could not work out which container it is running in, so it will not "
                + "restart one and hope.");

        await DockerSocket.PostAsync(endpoint, Timeout, $"/containers/{self.Container}/restart", null, ct);
    }

    /// <summary>
    /// The container's own inspect payload, or null if this process cannot be placed.
    ///
    /// The id from /proc is asked for first, because it is the one that stays true: after
    /// Watchtower recreates the container, the hostname is still the *old* container's id,
    /// and asking for that 404s — which is why "Update now" worked once and then said it
    /// could not work out which container it was, until the container was recreated by hand.
    /// The hostname is kept as the second try for hosts where /proc does not name the id.
    ///
    /// Each is tried as a name first, then as an id prefix against the list — the same match
    /// Docker itself does for a short id, for a hostname that is one.
    ///
    /// A socket proxy refusing the call is let through rather than read as "not found": the
    /// fix for that is a flag on the proxy, and hiding it behind "could not work out which
    /// container" would send the reader after the wrong thing all over again.
    /// </summary>
    private static async Task<string?> InspectAsync(string endpoint, CancellationToken ct)
    {
        string[] hints = [.. new[] { SelfContainer.Id, Environment.MachineName }
            .OfType<string>().Where(hint => hint.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];

        foreach (var hint in hints)
        {
            try
            {
                return await DockerSocket.GetAsync(endpoint, Timeout, $"/containers/{hint}/json", ct);
            }
            catch (InvalidOperationException ex) when (ex is not DockerProxyDeniedException)
            {
            }
        }

        try
        {
            var listed = await DockerSocket.GetAsync(endpoint, Timeout, "/containers/json", ct);
            using var containers = JsonDocument.Parse(listed);
            if (containers.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var hint in hints)
            foreach (var entry in containers.RootElement.EnumerateArray())
            {
                var id = entry.TryGetProperty("Id", out var raw) ? raw.GetString() ?? "" : "";
                if (id.Length == 0 || !id.StartsWith(hint, StringComparison.OrdinalIgnoreCase))
                    continue;

                return await DockerSocket.GetAsync(endpoint, Timeout, $"/containers/{id}/json", ct);
            }
        }
        catch (InvalidOperationException ex) when (ex is not DockerProxyDeniedException)
        {
        }

        return null;
    }

    /// <summary>
    /// The Docker endpoint to use: whichever the user configured on a Docker connection,
    /// then DOCKER_HOST, then the standard socket path. Reusing their connection means the
    /// endpoint they already got working — a named pipe, a socket proxy's TCP address — is
    /// the one this uses too.
    /// </summary>
    /// <summary>
    /// The Docker endpoint the one-shot helper is started on, or null when there is none —
    /// for finding a helper that is still running (see <see cref="UpdateHelpers"/>).
    /// </summary>
    public Task<string?> HelperEndpointAsync() => EndpointAsync();

    private async Task<string?> EndpointAsync()
    {
        var connections = await config.ConnectionsAsync();
        if (connections.FirstOrDefault(c => c.Provider == "docker" && c.Enabled) is { } docker)
            return docker.Settings.Get("endpoint", DockerSocket.EnvironmentEndpoint);

        if (Environment.GetEnvironmentVariable("DOCKER_HOST") is { Length: > 0 })
            return DockerSocket.EnvironmentEndpoint;

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
        // With a Watchtower API configured the button works whether or not LabbyTwo can see
        // Docker at all — that is rather the point of it. What Docker still adds is knowing
        // which container this is and whether a newer image exists, so every failure below
        // becomes a caveat under a working button instead of the reason there is none.
        var viaWatchtower = Watchtower.Enabled;
        var mode = viaWatchtower ? UpdateMode.WatchtowerApi : UpdateMode.DockerApi;

        var endpoint = await EndpointAsync();
        if (endpoint is null)
        {
            return viaWatchtower
                ? new Status(true, false, null,
                    "LabbyTwo cannot see Docker itself, so it cannot say whether a newer image is published. " +
                    "Watchtower can, and pressing the button asks it.", mode)
                : new Status(false, false, null, NoDocker + " Nothing here can start the update for you.");
        }

        try
        {
            var self = await IdentifyAsync(endpoint, ct);
            if (self is null)
            {
                return viaWatchtower
                    ? new Status(true, false, null,
                        "LabbyTwo could not work out which container it is running in. Watchtower does not need it " +
                        "to: it updates whatever it was started watching.", mode)
                    : new Status(false, false, null,
                        "LabbyTwo could not work out which container it is running in. That happens outside " +
                        "Docker, or when neither /proc nor the hostname names a container Docker still has.");
            }

            if (self.Digest is null)
            {
                // True in both modes: Watchtower has nothing to pull for a local build either.
                return new Status(false, false, self,
                    $"This container runs {self.Image}, which was built here rather than pulled from a " +
                    "registry. There is no published image to update to — switch the compose file to " +
                    "`image:` first, or keep using install.sh.", mode);
            }

            // Through a proxy, the one-shot Watchtower needs the proxy to allow creating
            // containers. That is worth saying before the click rather than after it: the
            // flags that allow it are the ones that make the proxy pointless.
            var caveat = !viaWatchtower && DockerEndpoint.Parse(endpoint).IsTcp
                ? "Docker is reached over TCP, so this is probably a socket proxy. Updating from here creates a " +
                  "container, which needs CONTAINERS=1, IMAGES=1 and POST=1 on it — the same power as the raw " +
                  "socket. Pointing Labby__Watchtower__Url at a Watchtower with its HTTP API on avoids that."
                : null;

            if (!self.Image.IsDockerHub)
            {
                // Watchtower can still do the update; only the "is there a newer one"
                // question needs a registry API this does not speak.
                return new Status(true, false, self, Join(
                    $"Running from {self.Image.Registry}, which this cannot query for a newer digest. " +
                    "Updating will pull whatever that tag points at now.", caveat), mode);
            }

            if (!checkRegistry)
                return new Status(true, false, self, caveat, mode);

            var published = await PublishedDigestAsync(self.Image, ct);
            if (published is null)
                return new Status(true, false, self, Join("Docker Hub did not report a digest for that tag.", caveat), mode);

            var behind = !string.Equals(published, self.Digest, StringComparison.OrdinalIgnoreCase);
            return new Status(true, behind, self,
                Join(behind ? null : "The running image is the one published for that tag.", caveat), mode);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Self-update status check failed");
            var message = ex.GetBaseException().Message;
            return viaWatchtower
                ? new Status(true, false, null, $"{message} Watchtower does not need this to update.", mode)
                : new Status(false, false, null, message);
        }
    }

    private static string? Join(string? first, string? second) =>
        first is null ? second : second is null ? first : $"{first} {second}";

    /// <summary>
    /// Which container this process is, found from the id /proc names or the hostname — see
    /// <see cref="SelfContainer"/> for why the hostname alone stops being enough.
    /// </summary>
    private static async Task<Self?> IdentifyAsync(string endpoint, CancellationToken ct)
    {
        if (await InspectAsync(endpoint, ct) is not { } inspected)
            return null;

        using var container = JsonDocument.Parse(inspected);
        var root = container.RootElement;
        var name = root.TryGetProperty("Name", out var rawName)
            ? rawName.GetString()?.TrimStart('/') ?? ""
            : "";

        var image = root.TryGetProperty("Config", out var configNode)
                    && configNode.TryGetProperty("Image", out var imageName)
            ? imageName.GetString() ?? ""
            : "";

        if (name.Length == 0 || image.Length == 0)
            return null;

        string[]? dns = null;
        if (root.TryGetProperty("HostConfig", out var hostConfig) &&
            hostConfig.TryGetProperty("Dns", out var servers) && servers.ValueKind == JsonValueKind.Array)
        {
            dns = servers.EnumerateArray().Select(s => s.GetString()).OfType<string>().ToArray();
            if (dns.Length == 0)
                dns = null;
        }

        // RepoDigests is empty for an image built locally, which is exactly the signal
        // that there is nothing published to compare against. A proxy refusing to show the
        // image is not that signal, so it is let through rather than read as "built here".
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
        catch (InvalidOperationException ex) when (ex is not DockerProxyDeniedException)
        {
        }

        return new Self(name, ImageRef.Parse(image), digest, dns, NetworkOf(root));
    }

    /// <summary>
    /// The network a helper should join to see what this container sees. The primary one
    /// — Compose's "project_default" — is where a socket proxy in the same compose file
    /// lives. The built-in modes are skipped: "host" and "none" cannot be joined by name in
    /// a way that helps, "bridge" has no name resolution, and "container:" would tie the
    /// helper's network to the very container it is about to remove.
    /// </summary>
    private static string? NetworkOf(JsonElement root)
    {
        static bool Usable(string? name) =>
            !string.IsNullOrEmpty(name) && name is not ("default" or "bridge" or "host" or "none") &&
            !name.StartsWith("container:", StringComparison.Ordinal);

        if (root.TryGetProperty("HostConfig", out var hostConfig) &&
            hostConfig.TryGetProperty("NetworkMode", out var mode) && Usable(mode.GetString()))
            return mode.GetString();

        if (root.TryGetProperty("NetworkSettings", out var settings) &&
            settings.TryGetProperty("Networks", out var networks) && networks.ValueKind == JsonValueKind.Object)
        {
            foreach (var network in networks.EnumerateObject())
                if (Usable(network.Name))
                    return network.Name;
        }

        return null;
    }

    private async Task<string?> PublishedDigestAsync(ImageRef image, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(Providers.ProviderHttp.ClientName);
        var url = $"https://hub.docker.com/v2/repositories/{image.HubRepository}/tags/{image.Tag}";

        using var document = JsonDocument.Parse(await http.GetStringAsync(url, ct));
        return document.RootElement.TryGetProperty("digest", out var digest) ? digest.GetString() : null;
    }

    /// <summary>
    /// Starts the update and returns. There is usually no success to report back: if this
    /// works, this process is killed a few seconds later, mid-response.
    /// </summary>
    /// <returns>
    /// Null when the update is under way, or a sentence when Watchtower answered — which it
    /// only does in time when it found nothing to replace.
    /// </returns>
    public async Task<string?> StartUpdateAsync(CancellationToken ct = default)
    {
        if (Watchtower.Enabled)
        {
            // An answer means Watchtower finished while this process was still alive to hear
            // it, so it replaced nothing here.
            return await TriggerWatchtowerAsync(null, ct)
                ? "Watchtower checked and LabbyTwo is still running, so it found nothing newer for this " +
                  "container — or it is not watching it. Its log says which."
                : null;
        }

        await StartOneShotAsync(ct);
        return null;
    }

    /// <param name="Name">The container's name, which the one-shot Watchtower is given to watch.</param>
    /// <param name="Image">
    /// The image name Watchtower's API compares against — see
    /// <see cref="ContainerUpdate.WatchtowerImage"/> for why it is cut the way it is.
    /// </param>
    public sealed record Target(string Name, string Image);

    /// <summary>
    /// Updates the named containers, the same two ways "Update now" updates LabbyTwo:
    /// asking a running Watchtower for just those images, or starting a one-shot Watchtower
    /// told just those names. Never a Watchtower with no names — that watches, and updates,
    /// everything on the host.
    ///
    /// The one-shot helper is given what this container would give it (its DNS servers and
    /// its network, for reaching a proxy by name) when LabbyTwo can place itself on that
    /// endpoint; on a Docker host LabbyTwo does not run on, it starts without them.
    /// </summary>
    /// <param name="endpoint">The Docker connection the containers are on, for the one-shot route.</param>
    /// <returns>
    /// Null when a one-shot Watchtower has been started and is working in the background;
    /// otherwise what Watchtower said, once it finished.
    /// </returns>
    /// <param name="keepPrevious">
    /// Start the one-shot helper without <c>--cleanup</c>, so the image each container ran
    /// before is still there to roll back to (see <see cref="SafeUpdates"/>). A Watchtower
    /// reached through its API cleans up or not by its own flags.
    /// </param>
    public async Task<string?> UpdateContainersAsync(
        string endpoint, IReadOnlyList<Target> targets, CancellationToken ct = default, bool keepPrevious = false)
    {
        if (targets.Count == 0)
            throw new ArgumentException("Nothing to update.", nameof(targets));

        if (Watchtower.Enabled)
        {
            var images = targets.Select(t => t.Image).Distinct(StringComparer.Ordinal).ToList();
            return await TriggerWatchtowerAsync(images, ct)
                ? "Watchtower has finished. It only replaces containers it is watching, and not ones labelled to be " +
                  "left alone — check again to see which it did, or read its log."
                : "Watchtower is still pulling. It carries on without anyone waiting; check again in a few minutes.";
        }

        log.LogWarning("Container update requested — starting a one-shot {Image} for {Containers}",
            WatchtowerImage, string.Join(", ", targets.Select(t => t.Name)));

        Self? self = null;
        try
        {
            self = await IdentifyAsync(endpoint, ct);
        }
        catch (InvalidOperationException ex) when (ex is not DockerProxyDeniedException)
        {
        }

        await StartHelperAsync(endpoint, [.. targets.Select(t => t.Name).Distinct(StringComparer.Ordinal)],
            self?.Dns, self?.Network, ct, cleanup: !keepPrevious);
        return null;
    }

    /// <summary>The Watchtower API's update URL, from whatever the user gave: a base or the full path.</summary>
    public static string WatchtowerUpdateUrl(string configured)
    {
        var url = configured.Trim().TrimEnd('/');
        if (!url.Contains("://", StringComparison.Ordinal))
            url = "http://" + url;
        return url.EndsWith("/v1/update", StringComparison.OrdinalIgnoreCase) ? url : url + "/v1/update";
    }

    /// <summary>
    /// The same, narrowed to some images: <c>/v1/update?image=a,b</c>, which Watchtower reads
    /// as a comma-separated list (it splits every <c>image</c> value on commas).
    /// </summary>
    public static string WatchtowerUpdateUrl(string configured, IReadOnlyList<string>? images)
    {
        var url = WatchtowerUpdateUrl(configured);
        return images is { Count: > 0 }
            ? url + "?image=" + string.Join(',', images.Select(Uri.EscapeDataString))
            : url;
    }

    /// <summary>
    /// Asks a running Watchtower to check now. Watchtower holds the socket; LabbyTwo holds a
    /// token that can do nothing but this. The worst a stolen token does is update whatever
    /// that Watchtower already watches, a little earlier than it would have.
    /// </summary>
    /// <param name="images">
    /// Only these images, through Watchtower's <c>?image=</c>; null for everything it
    /// watches. Watchtower still applies its own filter on top — the containers it was
    /// started naming, the enable label — so this narrows what it does and never widens it.
    /// </param>
    /// <returns>True when Watchtower answered; false when it was still working after minutes.</returns>
    private async Task<bool> TriggerWatchtowerAsync(IReadOnlyList<string>? images, CancellationToken ct)
    {
        var url = WatchtowerUpdateUrl(Watchtower.Url, images);
        log.LogWarning("Update requested — asking Watchtower at {Url} to check {What} now",
            WatchtowerUpdateUrl(Watchtower.Url), images is null ? "what it watches" : string.Join(", ", images));

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (Watchtower.Token is { Length: > 0 } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(WatchtowerWait);

        HttpResponseMessage response;
        try
        {
            var http = httpFactory.CreateClient();
            http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, wait.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Still working after minutes is what a large pull looks like, and Watchtower
            // carries on whether or not anyone is still waiting for its answer.
            return false;
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Could not reach Watchtower: {ProbeError.Describe(ex, url)}", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new InvalidOperationException(
                    "Watchtower refused the token. Labby__Watchtower__Token has to match the " +
                    "WATCHTOWER_HTTP_API_TOKEN Watchtower was started with.");

            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidOperationException(
                    $"Nothing at {url} takes update requests. Watchtower only serves it when started with " +
                    "--http-api-update (or WATCHTOWER_HTTP_API_UPDATE=true).");

            if (!response.IsSuccessStatusCode)
            {
                var body = (await response.Content.ReadAsStringAsync(ct)).Trim();
                throw new InvalidOperationException(
                    $"Watchtower answered HTTP {(int)response.StatusCode}{(body.Length > 0 ? $": {body}" : ".")}");
            }
        }

        return true;
    }

    private static readonly JsonSerializerOptions CreateJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Starts the one-shot Watchtower through the Docker API and returns.
    /// </summary>
    private async Task StartOneShotAsync(CancellationToken ct)
    {
        var endpoint = await EndpointAsync()
                       ?? throw new InvalidOperationException(NoDocker);

        var self = await IdentifyAsync(endpoint, ct)
                   ?? throw new InvalidOperationException("Could not identify this container.");

        log.LogWarning("Self-update requested — starting a one-shot {Image} to replace {Container}",
            WatchtowerImage, self.Container);

        await StartHelperAsync(endpoint, [self.Container], self.Dns, self.Network, ct);
    }

    /// <summary>Pulls Watchtower, creates the one-shot helper for these names, and starts it.</summary>
    private static async Task StartHelperAsync(
        string endpoint, IReadOnlyList<string> containers, string[]? dns, string? network, CancellationToken ct,
        bool cleanup = true)
    {
        // Pull first. On a host that has never run Watchtower, creating the container would
        // otherwise fail with "no such image" and leave nothing to show for the click.
        await DockerSocket.PostAsync(endpoint, TimeSpan.FromMinutes(3),
            $"/images/create?fromImage={Uri.EscapeDataString(WatchtowerImage)}&tag=latest", null, ct);

        // Named, so it is recognisable in `docker ps` as LabbyTwo's rather than Docker's
        // made-up "jolly_bardeen" — which is all anybody had to go on when one hung.
        using var created = JsonDocument.Parse(await DockerSocket.PostAsync(
            endpoint, Timeout, $"/containers/create?name={UpdateHelperRules.NameFor(DateTimeOffset.Now)}",
            OneShotRequest(endpoint, containers, dns, network, cleanup), ct));

        var id = created.RootElement.TryGetProperty("Id", out var identifier) ? identifier.GetString() : null;
        if (id is null)
            throw new InvalidOperationException("Docker did not return an id for the update container.");

        await DockerSocket.PostAsync(endpoint, Timeout, $"/containers/{id}/start", null, ct);
    }

    /// <summary>
    /// The body of the /containers/create call for the one-shot Watchtower.
    ///
    /// A socket can be handed to the helper as a bind mount. A TCP endpoint cannot — there
    /// is no file to mount, and binding "tcp://…" would be taken as a relative path — so the
    /// helper is told where Docker is with DOCKER_HOST instead, and joins this container's
    /// network so the name in that address resolves for it too.
    /// </summary>
    public static string OneShotRequest(string endpoint, Self self) =>
        OneShotRequest(endpoint, [self.Container], self.Dns, self.Network);

    /// <summary>
    /// The one-shot request for any containers: the names go on the command line, which is
    /// what keeps Watchtower from treating "no names" as "everything on the host".
    /// </summary>
    /// <param name="dns">DNS servers for the helper — LabbyTwo's own, where it has some.</param>
    /// <param name="network">A network to join when Docker is reached over TCP, so a proxy's name resolves.</param>
    /// <param name="cleanup">Whether Watchtower deletes each old image once the container is recreated.
    /// Off for a safe update, which keeps the old image to roll back to.</param>
    public static string OneShotRequest(
        string endpoint, IReadOnlyList<string> containers, string[]? dns, string? network, bool cleanup = true)
    {
        if (containers.Count == 0 || containers.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("A one-shot Watchtower needs container names; without any it updates everything.",
                nameof(containers));

        var target = DockerEndpoint.Parse(endpoint);

        return JsonSerializer.Serialize(new
        {
            Image = $"{WatchtowerImage}:latest",
            // --run-once so it does the job and exits, rather than becoming a second
            // scheduler competing with whatever the user already runs.
            Cmd = (cleanup ? new[] { "--run-once", "--cleanup" } : ["--run-once"]).Concat(containers).ToArray(),
            // Watchtower's Docker client asks for API 1.25 unless told otherwise, and Docker 29
            // refuses anything that old — the helper would start, log one error and exit,
            // leaving nothing updated and nothing on the page to say why.
            Env = target.IsTcp
                ? new[] { $"DOCKER_HOST={target}", $"DOCKER_API_VERSION={DockerSocket.ApiVersionNumber}" }
                : new[] { $"DOCKER_API_VERSION={DockerSocket.ApiVersionNumber}" },
            // So LabbyTwo can find it again — after a restart too — and say when it has
            // been running far longer than updating that many containers takes (see
            // UpdateHelperRules). The count sets its time limit.
            Labels = new Dictionary<string, string>
            {
                [UpdateHelperRules.Label] = "true",
                [UpdateHelperRules.TargetsLabel] = containers.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            HostConfig = new
            {
                Binds = target.IsTcp
                    ? null
                    : new[] { $"{(target.Kind == DockerEndpointKind.Unix ? target.Address : endpoint)}:/var/run/docker.sock" },
                NetworkMode = target.IsTcp ? network : null,
                AutoRemove = true,
                // Watchtower asks the registry for the new digest from inside its own
                // container. On a host whose containers get no working resolver by default,
                // "dns:" in LabbyTwo's compose file is what made LabbyTwo work — and a
                // helper started without it fails on the very lookup it exists to make.
                Dns = dns,
            },
        }, CreateJson);
    }
}
