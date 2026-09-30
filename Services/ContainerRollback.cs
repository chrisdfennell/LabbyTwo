using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LabbyTwo.Core;

namespace LabbyTwo.Services;

/// <summary>
/// Puts a container back on the image it ran before an update, with the settings it has now.
///
/// Watchtower cannot do this: it only ever moves a container to whatever its tag points at,
/// so an older image has to be put under the container by hand — the way Watchtower and
/// Compose themselves recreate one, done with the Docker API step by step:
///
/// <list type="number">
/// <item>Find the previous image: the roll-back tag LabbyTwo put on it, or its id, or —
/// when a Watchtower with cleanup on deleted it — pull it again by its registry digest.</item>
/// <item>Point the container's own reference (<c>sonarr:latest</c>) back at it, so the
/// container is created from exactly the reference it was before and a later
/// <c>docker compose up</c> does not quietly bring the new version back without a pull.</item>
/// <item>Inspect the current container and build the create request from its Config,
/// HostConfig and networks — taking out the new image's own defaults (its environment,
/// labels, command) the way Watchtower does, so the old image's come back rather than the
/// new one's being baked into the old version.</item>
/// <item>Stop it and rename it out of the way, rather than removing it: until the new one
/// has started, the old one is the way back.</item>
/// <item>Create the container under its name, connect the networks beyond the first
/// (the API version used here takes one at create), and start it.</item>
/// <item>Only then remove the old one — keeping its volumes, which the new container has
/// been given: named ones by the same settings, anonymous ones by name, which a plain
/// recreate would otherwise lose.</item>
/// </list>
///
/// If creating or starting fails, the new container is removed and the old one renamed back
/// and started, so a failed roll-back leaves things as they were rather than worse.
/// </summary>
public static partial class ContainerRollback
{
    /// <summary>The repository roll-back tags are kept under: <c>labbytwo-rollback/sonarr:20260929-140201</c>.</summary>
    public const string TagRepository = "labbytwo-rollback";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>A pull of a large image over a home connection.</summary>
    private static readonly TimeSpan PullTimeout = TimeSpan.FromMinutes(10);

    [GeneratedRegex("[^a-z0-9._-]+", RegexOptions.CultureInvariant)]
    private static partial Regex NotAllowedInRepository();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex AnonymousVolume();

    /// <summary>The repository and tag of a roll-back tag for this container at this moment.</summary>
    public static (string Repository, string Tag) TagFor(string container, DateTimeOffset at)
    {
        var name = NotAllowedInRepository().Replace(container.ToLowerInvariant(), "-").Trim('-', '.', '_');
        return ($"{TagRepository}/{(name.Length > 0 ? name : "container")}", at.UtcDateTime.ToString("yyyyMMdd-HHmmss"));
    }

    /// <summary>
    /// A reference split where <c>POST /images/{id}/tag</c> wants it: <c>lscr.io/linuxserver/sonarr</c>
    /// and <c>latest</c>. Null for one that cannot be retagged — pinned by digest, or a bare id.
    /// </summary>
    public static (string Repository, string Tag)? SplitReference(string reference)
    {
        if (reference.Length == 0 || reference.Contains('@') || reference.StartsWith("sha256:", StringComparison.Ordinal) ||
            Regex.IsMatch(reference, "^[0-9a-f]{12,64}$"))
            return null;

        var slash = reference.LastIndexOf('/');
        var colon = reference.LastIndexOf(':');
        return colon > slash ? (reference[..colon], reference[(colon + 1)..]) : (reference, "latest");
    }

    /// <summary>What a roll-back is given to find the previous image by.</summary>
    public sealed record Previous(string ImageId, string Digest, string Tag, string Reference);

    /// <summary>
    /// Rolls <paramref name="container"/> back and returns the new container's id. Throws
    /// with a sentence saying what went wrong, and leaves the container as it found it
    /// whenever it got far enough to change anything.
    /// </summary>
    /// <param name="staleHostnames">Short ids the container's hostname may still be set to —
    /// Docker defaults it to the container's own, and a recreate that copied it would name the
    /// new container after the old one.</param>
    public static async Task<string> RollBackAsync(
        string endpoint, string container, Previous previous, IReadOnlyCollection<string> staleHostnames, CancellationToken ct)
    {
        var imageId = await FindPreviousAsync(endpoint, previous, ct);

        // The container's own reference goes back to the old image, and the container is
        // created from that reference, so its settings read exactly as they did before.
        var image = previous.Reference;
        if (SplitReference(previous.Reference) is { } split)
        {
            await DockerSocket.PostAsync(endpoint, Timeout,
                $"/images/{Uri.EscapeDataString(imageId)}/tag?repo={Uri.EscapeDataString(split.Repository)}&tag={Uri.EscapeDataString(split.Tag)}",
                null, ct);
        }
        else
        {
            image = imageId;
        }

        var inspected = await DockerSocket.GetAsync(endpoint, Timeout, $"/containers/{Uri.EscapeDataString(container)}/json", ct);
        using var document = JsonDocument.Parse(inspected);
        var root = document.RootElement;
        var id = Str(root, "Id");
        var name = Str(root, "Name").TrimStart('/');
        if (id.Length == 0 || name.Length == 0)
            throw new InvalidOperationException($"Docker did not describe {container} well enough to recreate it.");

        string? newImage = null;
        try
        {
            newImage = await DockerSocket.GetAsync(endpoint, Timeout, $"/images/{Uri.EscapeDataString(Str(root, "Image"))}/json", ct);
        }
        catch (InvalidOperationException ex) when (ex is not DockerProxyDeniedException)
        {
            // Without the new image's defaults there is nothing to take out; its settings go across as they are.
        }

        var plan = Plan(inspected, newImage, image, staleHostnames);

        var stopWait = plan.StopTimeout ?? 10;
        await DockerSocket.PostAsync(endpoint, TimeSpan.FromSeconds(stopWait + 30),
            $"/containers/{id}/stop?t={stopWait}", null, ct);

        var aside = $"{name}-labbytwo-replaced-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        await DockerSocket.PostAsync(endpoint, Timeout, $"/containers/{id}/rename?name={Uri.EscapeDataString(aside)}", null, ct);

        string? created = null;
        try
        {
            using var answer = JsonDocument.Parse(await DockerSocket.PostAsync(endpoint, Timeout,
                $"/containers/create?name={Uri.EscapeDataString(name)}", plan.Body, ct));
            created = answer.RootElement.TryGetProperty("Id", out var newId) ? newId.GetString() : null;
            if (string.IsNullOrEmpty(created))
                throw new InvalidOperationException("Docker did not return an id for the recreated container.");

            foreach (var (network, endpointConfig) in plan.Networks)
            {
                var connect = new JsonObject { ["Container"] = created, ["EndpointConfig"] = endpointConfig.DeepClone() };
                await DockerSocket.PostAsync(endpoint, Timeout, $"/networks/{Uri.EscapeDataString(network)}/connect",
                    connect.ToJsonString(), ct);
            }

            await DockerSocket.PostAsync(endpoint, Timeout, $"/containers/{created}/start", null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await RestoreAsync(endpoint, id, name, created);
            throw new InvalidOperationException(
                $"Could not recreate {name} on its previous image, so it was put back as it was: {ex.GetBaseException().Message}", ex);
        }

        try
        {
            // Not v=1: the new container has been given the old one's volumes, anonymous ones too.
            await DockerSocket.SendAsync(endpoint, Timeout, HttpMethod.Delete, $"/containers/{id}", null, ct);
        }
        catch (InvalidOperationException)
        {
            // The roll-back worked; a stopped container left behind under another name is
            // untidy, not broken, and removing it by hand is one command.
        }

        return created;
    }

    /// <summary>
    /// The previous image's id: by the roll-back tag, by the id itself, or pulled again by
    /// digest when both are gone — a Watchtower with cleanup on removes the old image, tags
    /// and all, the moment it has recreated the container.
    /// </summary>
    private static async Task<string> FindPreviousAsync(string endpoint, Previous previous, CancellationToken ct)
    {
        foreach (var name in new[] { previous.Tag, previous.ImageId }.Where(n => n.Length > 0))
        {
            try
            {
                using var found = JsonDocument.Parse(
                    await DockerSocket.GetAsync(endpoint, Timeout, $"/images/{Uri.EscapeDataString(name)}/json", ct));
                if (Str(found.RootElement, "Id") is { Length: > 0 } id)
                    return id;
            }
            catch (InvalidOperationException ex) when (ex is not DockerProxyDeniedException)
            {
                // Not there; try the next way.
            }
        }

        if (previous.Digest.Length == 0)
        {
            throw new InvalidOperationException(
                "The previous image is gone — deleted after the update — and it was built on this host, so there is no " +
                "registry to pull it from again.");
        }

        await DockerSocket.PostAsync(endpoint, PullTimeout,
            $"/images/create?fromImage={Uri.EscapeDataString(previous.Digest)}", null, ct);
        using var pulled = JsonDocument.Parse(
            await DockerSocket.GetAsync(endpoint, Timeout, $"/images/{Uri.EscapeDataString(previous.Digest)}/json", ct));
        return Str(pulled.RootElement, "Id") is { Length: > 0 } pulledId
            ? pulledId
            : throw new InvalidOperationException($"Pulled {previous.Digest}, but Docker would not say which image it is.");
    }

    /// <summary>Takes away a half-made replacement and puts the original back under its name, running.</summary>
    private static async Task RestoreAsync(string endpoint, string id, string name, string? created)
    {
        // Not the caller's token: this has to finish even if whatever asked has given up.
        var ct = CancellationToken.None;
        if (created is not null)
        {
            try
            {
                await DockerSocket.SendAsync(endpoint, Timeout, HttpMethod.Delete, $"/containers/{created}?force=1", null, ct);
            }
            catch (InvalidOperationException)
            {
            }
        }

        try
        {
            await DockerSocket.PostAsync(endpoint, Timeout, $"/containers/{id}/rename?name={Uri.EscapeDataString(name)}", null, ct);
            await DockerSocket.SendAsync(endpoint, Timeout, HttpMethod.Post, $"/containers/{id}/start", null, ct);
        }
        catch (InvalidOperationException)
        {
            // Nothing more to try from here; the error that is thrown says what happened.
        }
    }

    /// <summary>The create request, and the networks to connect once it exists.</summary>
    /// <param name="Body">The JSON for <c>POST /containers/create</c>.</param>
    /// <param name="Networks">Every network after the first, with its endpoint settings.</param>
    /// <param name="StopTimeout">The container's own stop timeout, if it has one.</param>
    public sealed record CreatePlan(string Body, IReadOnlyList<(string Network, JsonObject EndpointConfig)> Networks, int? StopTimeout);

    /// <summary>
    /// The create request that recreates an inspected container on another image. Pure, so
    /// the parts that go wrong quietly — a hostname copied from the old id, the new image's
    /// environment baked into the old version, an anonymous volume left behind, a second
    /// network lost — are each pinned by a test.
    /// </summary>
    /// <param name="inspect">The container's <c>GET /containers/{id}/json</c>.</param>
    /// <param name="currentImage">The <c>GET /images/{id}/json</c> of the image it runs now, whose
    /// defaults are taken out; null to take nothing out.</param>
    /// <param name="image">What to create it from.</param>
    /// <param name="staleHostnames">Short ids that, as a hostname, only mean Docker's default.</param>
    public static CreatePlan Plan(string inspect, string? currentImage, string image, IReadOnlyCollection<string> staleHostnames)
    {
        var root = JsonNode.Parse(inspect)?.AsObject() ?? throw new InvalidOperationException("Not a container.");
        var config = root["Config"]?.DeepClone().AsObject() ?? new JsonObject();
        var hostConfig = root["HostConfig"]?.DeepClone().AsObject() ?? new JsonObject();
        var imageConfig = currentImage is null ? null : JsonNode.Parse(currentImage)?["Config"] as JsonObject;

        config["Image"] = image;

        if (imageConfig is not null)
            RemoveImageDefaults(config, imageConfig);

        var mode = hostConfig["NetworkMode"]?.GetValue<string>() ?? "";
        var sharesNetwork = mode.StartsWith("container:", StringComparison.Ordinal);
        var shortId = (root["Id"]?.GetValue<string>() ?? "") is { Length: >= 12 } fullId ? fullId[..12] : "";
        if (config["Hostname"]?.GetValue<string>() is { } hostname &&
            (hostname == shortId || staleHostnames.Contains(hostname, StringComparer.OrdinalIgnoreCase) || sharesNetwork))
            config.Remove("Hostname");
        if (sharesNetwork)
        {
            // Docker refuses a hostname, domain or exposed port beside another container's network.
            config.Remove("Domainname");
            config.Remove("ExposedPorts");
        }

        // Inspect writes links as "/db:/web/db"; create wants "db:db".
        if (hostConfig["Links"] is JsonArray links)
        {
            hostConfig["Links"] = new JsonArray([
                .. links.Select(l => l?.GetValue<string>() ?? "").Where(l => l.Contains(':'))
                    .Select(l => l.Split(':', 2))
                    .Select(p => (JsonNode)JsonValue.Create($"{p[0].TrimStart('/')}:{p[1][(p[1].LastIndexOf('/') + 1)..]}")!),
            ]);
        }

        KeepAnonymousVolumes(root, hostConfig);

        var body = new JsonObject();
        foreach (var (key, value) in config)
            body[key] = value?.DeepClone();
        body["HostConfig"] = hostConfig;

        var extra = new List<(string, JsonObject)>();
        if (!sharesNetwork && mode is not ("host" or "none") && root["NetworkSettings"]?["Networks"] is JsonObject networks &&
            networks.Count > 0)
        {
            var names = networks.Select(n => n.Key).ToList();
            var primary = names.FirstOrDefault(n => n == mode) ?? (mode == "default" && names.Contains("bridge") ? "bridge" : names[0]);
            var endpoints = new JsonObject { [primary] = EndpointSettings(networks[primary], shortId, staleHostnames) };
            body["NetworkingConfig"] = new JsonObject { ["EndpointsConfig"] = endpoints };
            foreach (var other in names.Where(n => n != primary))
                extra.Add((other, EndpointSettings(networks[other], shortId, staleHostnames)));
        }

        var stopTimeout = config["StopTimeout"] is JsonValue timeout && timeout.TryGetValue<int>(out var seconds) ? seconds : (int?)null;
        return new CreatePlan(body.ToJsonString(), extra, stopTimeout);
    }

    /// <summary>
    /// What is kept of a network endpoint: the aliases (less the old container's id, which
    /// Docker adds by itself and would otherwise leave the new container answering to its
    /// predecessor's name), a fixed address, links and driver options. The rest — the
    /// endpoint's id, the address it happened to get — belongs to the old container.
    /// </summary>
    private static JsonObject EndpointSettings(JsonNode? network, string shortId, IReadOnlyCollection<string> staleHostnames)
    {
        var settings = new JsonObject();
        if (network is not JsonObject source)
            return settings;

        if (source["Aliases"] is JsonArray aliases)
        {
            var kept = aliases.Select(a => a?.GetValue<string>() ?? "")
                .Where(a => a.Length > 0 && a != shortId && !staleHostnames.Contains(a, StringComparer.OrdinalIgnoreCase))
                .Select(a => (JsonNode)JsonValue.Create(a)!)
                .ToArray();
            if (kept.Length > 0)
                settings["Aliases"] = new JsonArray(kept);
        }

        foreach (var key in new[] { "IPAMConfig", "Links", "DriverOpts" })
        {
            if (source[key] is { } value && value.GetValueKind() != JsonValueKind.Null)
                settings[key] = value.DeepClone();
        }
        return settings;
    }

    /// <summary>
    /// Watchtower's own rule for which settings came from the image rather than from
    /// whoever created the container: an entry the image also has, with the same value, is
    /// the image's. Taken out here so the image the container is recreated on supplies its
    /// own — the old version's PATH, not the new one's.
    /// </summary>
    private static void RemoveImageDefaults(JsonObject config, JsonObject image)
    {
        if (config["Env"] is JsonArray env && image["Env"] is JsonArray imageEnv)
        {
            var baked = imageEnv.Select(e => e?.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            config["Env"] = new JsonArray([
                .. env.Select(e => e?.GetValue<string>() ?? "").Where(e => !baked.Contains(e)).Select(e => (JsonNode)JsonValue.Create(e)!),
            ]);
        }

        foreach (var key in new[] { "Labels", "Volumes", "ExposedPorts" })
        {
            if (config[key] is JsonObject mine && image[key] is JsonObject theirs)
            {
                foreach (var entry in theirs)
                {
                    if (mine[entry.Key] is { } value && JsonNode.DeepEquals(value, entry.Value))
                        mine.Remove(entry.Key);
                }
            }
        }

        if (JsonNode.DeepEquals(config["Entrypoint"], image["Entrypoint"]))
        {
            config.Remove("Entrypoint");
            if (JsonNode.DeepEquals(config["Cmd"], image["Cmd"]))
                config.Remove("Cmd");
        }

        foreach (var key in new[] { "Healthcheck", "WorkingDir", "User", "StopSignal" })
        {
            if (config[key] is { } value && JsonNode.DeepEquals(value, image[key]))
                config.Remove(key);
        }
    }

    /// <summary>
    /// Gives the new container the old one's anonymous volumes by name. They are not in its
    /// settings — the image's VOLUME line made them — so a plain recreate would make fresh,
    /// empty ones, and whatever the container kept there would seem to vanish.
    /// </summary>
    private static void KeepAnonymousVolumes(JsonObject root, JsonObject hostConfig)
    {
        if (root["Mounts"] is not JsonArray mounts)
            return;

        var taken = new HashSet<string>(StringComparer.Ordinal);
        if (hostConfig["Binds"] is JsonArray binds)
        {
            foreach (var bind in binds.Select(b => b?.GetValue<string>() ?? ""))
            {
                var parts = bind.Split(':');
                if (parts.Length >= 2)
                    taken.Add(parts[1]);
            }
        }

        var list = hostConfig["Mounts"] as JsonArray ?? [];
        foreach (var mount in list.OfType<JsonObject>())
            if (mount["Target"]?.GetValue<string>() is { } target)
                taken.Add(target);

        var added = false;
        foreach (var mount in mounts.OfType<JsonObject>())
        {
            var name = mount["Name"]?.GetValue<string>() ?? "";
            var destination = mount["Destination"]?.GetValue<string>() ?? "";
            if (mount["Type"]?.GetValue<string>() != "volume" || !AnonymousVolume().IsMatch(name) ||
                destination.Length == 0 || !taken.Add(destination))
                continue;
            list.Add(new JsonObject { ["Type"] = "volume", ["Source"] = name, ["Target"] = destination });
            added = true;
        }

        if (added)
            hostConfig["Mounts"] = list;
    }

    private static string Str(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
