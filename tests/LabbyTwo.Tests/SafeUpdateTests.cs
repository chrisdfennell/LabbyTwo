using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LabbyTwo.Tests;

/// <summary>
/// A Docker Engine with state: containers that can be stopped, renamed, created, started
/// and removed, and images that can be tagged, untagged and pulled — enough for a safe
/// update to be prepared, watched and rolled back against it, and for the test to read
/// what the host looks like afterwards rather than only which requests were made.
/// </summary>
internal sealed class StatefulDocker : IDisposable
{
    public sealed class Box
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Reference { get; set; } = "";
        public string ImageId { get; set; } = "";
        public string Status { get; set; } = "running";
        public string Health { get; set; } = "";
        public int RestartCount { get; set; }
        public int ExitCode { get; set; }
        public string Hostname { get; set; } = "";
        public JsonObject? Created { get; set; }
    }

    private readonly ScriptedDocker _docker;
    private int _next = 100;

    public List<Box> Containers { get; } = [];

    /// <summary>Every name an image answers to — its id, "repo:tag", "repo@sha256:…" — mapped to the id.</summary>
    public Dictionary<string, string> Images { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string[]> RepoDigests { get; } = new(StringComparer.Ordinal);

    /// <summary>What a pull of each digest reference yields: the image id it brings back.</summary>
    public Dictionary<string, string> Registry { get; } = new(StringComparer.Ordinal);

    public List<(string Network, string Body)> Connected { get; } = [];

    /// <summary>A request to refuse, the way a socket proxy does: method and path.</summary>
    public Func<string, string, bool> Forbid { get; set; } = (_, _) => false;

    /// <summary>A request to fail with a Docker error: method and path.</summary>
    public Func<string, string, bool> Fail { get; set; } = (_, _) => false;

    public StatefulDocker() => _docker = new ScriptedDocker(HandleAsync);

    public string Endpoint => _docker.Endpoint;

    public List<string> Requests => _docker.Requests;

    public Box Add(string name, string reference, string imageId, string? id = null)
    {
        var box = new Box { Id = id ?? NewId(), Name = name, Reference = reference, ImageId = imageId };
        box.Hostname = box.Id[..12];
        Containers.Add(box);
        Images[imageId] = imageId;
        Images[reference.Contains(':') ? reference : reference + ":latest"] = imageId;
        return box;
    }

    public Box Named(string name) => Containers.Single(c => c.Name == name);

    private string NewId() => (_next++).ToString("x").PadRight(64, 'c');

    private Box? Find(string key) =>
        Containers.FirstOrDefault(c => c.Name == key) ?? Containers.FirstOrDefault(c => c.Id.StartsWith(key, StringComparison.Ordinal));

    private string? Image(string key) =>
        Images.TryGetValue(key, out var id) ? id
        : Images.TryGetValue(key + ":latest", out var latest) ? latest
        : null;

    private async Task HandleAsync(HttpListenerContext context)
    {
        var method = context.Request.HttpMethod;
        var path = Uri.UnescapeDataString(context.Request.Url!.AbsolutePath);
        path = System.Text.RegularExpressions.Regex.Replace(path, @"^/v[\d.]+", "");
        var query = context.Request.QueryString;
        string body;
        using (var reader = new StreamReader(context.Request.InputStream))
            body = await reader.ReadToEndAsync();

        if (Forbid(method, path))
        {
            await ScriptedDocker.Forbidden(context);
            return;
        }
        if (Fail(method, path))
        {
            await ScriptedDocker.Json(context, new { message = "something went wrong" }, 500);
            return;
        }

        var (status, answer) = Route(method, path, query, body);
        await ScriptedDocker.Json(context, answer ?? "", status);
    }

    private (int, object?) Route(string method, string path, System.Collections.Specialized.NameValueCollection query, string body)
    {
        lock (Containers)
        {
            var parts = path.Trim('/').Split('/');
            if (parts[0] == "containers" && parts.Length == 3 && parts[2] == "json" && method == "GET")
                return Find(parts[1]) is { } box ? (200, Inspect(box)) : (404, new { message = $"No such container: {parts[1]}" });

            if (parts[0] == "containers" && parts.Length == 2 && parts[1] == "create" && method == "POST")
            {
                var request = JsonNode.Parse(body)!.AsObject();
                var name = query["name"] ?? "";
                if (Containers.Any(c => c.Name == name))
                    return (409, new { message = $"Conflict. The container name \"/{name}\" is already in use" });
                var reference = request["Image"]!.GetValue<string>();
                var image = Image(reference);
                if (image is null)
                    return (404, new { message = $"No such image: {reference}" });
                var box = new Box { Id = NewId(), Name = name, Reference = reference, ImageId = image, Status = "created", Created = request };
                box.Hostname = request["Hostname"]?.GetValue<string>() ?? box.Id[..12];
                Containers.Add(box);
                return (201, new { Id = box.Id });
            }

            if (parts[0] == "containers" && parts.Length == 3 && method == "POST" && Find(parts[1]) is { } target)
            {
                switch (parts[2])
                {
                    case "stop":
                        target.Status = "exited";
                        return (204, null);
                    case "start":
                        target.Status = "running";
                        return (204, null);
                    case "rename":
                        target.Name = query["name"] ?? target.Name;
                        return (204, null);
                }
            }

            if (parts[0] == "containers" && parts.Length == 2 && method == "DELETE" && Find(parts[1]) is { } removed)
            {
                Containers.Remove(removed);
                return (204, null);
            }

            if (parts[0] == "networks" && parts.Length == 3 && parts[2] == "connect")
            {
                Connected.Add((parts[1], body));
                return (200, null);
            }

            if (parts[0] == "images" && parts.Length == 2 && parts[1] == "create" && method == "POST")
            {
                var from = query["fromImage"] ?? "";
                if (!Registry.TryGetValue(from, out var pulled))
                    return (404, new { message = $"manifest for {from} not found" });
                Images[pulled] = pulled;
                Images[from] = pulled;
                return (200, new { status = "Downloaded" });
            }

            // Image names contain slashes, so everything between /images/ and the verb is the name.
            if (parts[0] == "images" && parts.Length >= 3)
            {
                var verb = parts[^1];
                var name = string.Join('/', parts[1..^1]);
                if (verb == "json" && method == "GET")
                {
                    return Image(name) is { } id
                        ? (200, new { Id = id, RepoDigests = RepoDigests.GetValueOrDefault(id, []), Config = new { Env = new[] { "PATH=/usr/bin" } } })
                        : (404, new { message = $"No such image: {name}" });
                }
                if (verb == "tag" && method == "POST")
                {
                    if (Image(name) is not { } id)
                        return (404, new { message = $"No such image: {name}" });
                    Images[$"{query["repo"]}:{query["tag"]}"] = id;
                    return (201, null);
                }
            }

            if (parts[0] == "images" && method == "DELETE")
            {
                var name = string.Join('/', parts[1..]);
                return Images.Remove(name) ? (200, new[] { new { Untagged = name } }) : (404, new { message = $"No such image: {name}" });
            }

            return (404, new { message = $"page not found: {method} {path}" });
        }
    }

    private static object Inspect(Box box) => new
    {
        box.Id,
        Name = "/" + box.Name,
        Image = box.ImageId,
        box.RestartCount,
        State = new
        {
            box.Status,
            Running = box.Status == "running",
            Restarting = box.Status == "restarting",
            box.ExitCode,
            Health = box.Health.Length > 0 ? new { Status = box.Health } : null,
        },
        Config = new
        {
            box.Hostname,
            Image = box.Reference,
            Env = new[] { "PATH=/usr/bin", "PUID=1000" },
            Labels = new Dictionary<string, string> { ["com.docker.compose.service"] = box.Name },
        },
        HostConfig = new { NetworkMode = "media_default", RestartPolicy = new { Name = "unless-stopped" } },
        NetworkSettings = new
        {
            Networks = new Dictionary<string, object>
            {
                ["media_default"] = new { Aliases = new[] { box.Name, box.Id[..12] } },
                ["proxy"] = new { Aliases = new[] { box.Name } },
            },
        },
        Mounts = Array.Empty<object>(),
    };

    public void Dispose() => _docker.Dispose();
}

/// <summary>What a container's configuration is taken to be, and how two versions compare.</summary>
public class ContainerConfigTests
{
    private static readonly byte[] Salt = [1, 2, 3, 4, 5, 6, 7, 8];

    private const string Inspect = """
        {
          "Id": "c1", "Name": "/sonarr",
          "Config": {
            "Image": "lscr.io/linuxserver/sonarr:latest",
            "Env": ["PATH=/usr/bin", "PUID=1000", "TZ=Europe/London", "API_KEY=abc123", "DB_URL=postgres://sonarr:hunter2@db/sonarr", "EMPTY_PASSWORD="],
            "Cmd": ["/init"],
            "Labels": {
              "com.docker.compose.service": "sonarr",
              "com.docker.compose.config-hash": "deadbeef",
              "org.opencontainers.image.version": "4.0",
              "traefik.http.middlewares.a.basicauth.users": "me:$apr1$xyz"
            }
          },
          "HostConfig": {
            "NetworkMode": "media_default",
            "RestartPolicy": { "Name": "on-failure", "MaximumRetryCount": 5 },
            "PortBindings": { "8989/tcp": [{ "HostIp": "", "HostPort": "8989" }], "9000/udp": [{ "HostIp": "127.0.0.1", "HostPort": "9000" }] }
          },
          "Mounts": [
            { "Type": "volume", "Name": "media_sonarr", "Destination": "/config", "RW": true },
            { "Type": "volume", "Name": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "Destination": "/cache", "RW": true },
            { "Type": "bind", "Source": "/mnt/tv", "Destination": "/tv", "RW": false }
          ],
          "NetworkSettings": { "Networks": { "media_default": { "Aliases": ["sonarr", "c1"] }, "proxy": { "IPAMConfig": { "IPv4Address": "10.0.0.5" } } } }
        }
        """;

    private const string Image = """
        { "Id": "sha256:new", "Config": { "Env": ["PATH=/usr/bin"], "Cmd": ["/init"], "Labels": { "org.opencontainers.image.version": "4.0" } } }
        """;

    [Fact]
    public void SecretsAreHashedNeverStoredAndTheImagesDefaultsAreLeftOut()
    {
        var config = ContainerConfigs.Parse(Inspect, Image, Salt);
        var json = config.ToJson();

        Assert.DoesNotContain("abc123", json);
        Assert.DoesNotContain("hunter2", json);
        Assert.DoesNotContain("$apr1$xyz", json);
        Assert.StartsWith(ContainerConfigs.HiddenPrefix, config.Env["API_KEY"]);
        Assert.StartsWith(ContainerConfigs.HiddenPrefix, config.Env["DB_URL"]);
        Assert.StartsWith(ContainerConfigs.HiddenPrefix, config.Labels["traefik.http.middlewares.a.basicauth.users"]);
        Assert.Equal("", config.Env["EMPTY_PASSWORD"]);
        Assert.Equal("1000", config.Env["PUID"]);

        // The image's own PATH, label and command are not the container's configuration.
        Assert.False(config.Env.ContainsKey("PATH"));
        Assert.False(config.Labels.ContainsKey("org.opencontainers.image.version"));
        Assert.False(config.Labels.ContainsKey("com.docker.compose.config-hash"));
        Assert.Equal("", config.Command);
        Assert.True(config.ImageDefaultsRemoved);

        Assert.Equal("8989", config.Ports["8989/tcp"]);
        Assert.Equal("127.0.0.1:9000", config.Ports["9000/udp"]);
        Assert.Equal("volume media_sonarr", config.Mounts["/config"]);
        Assert.Equal("volume (anonymous)", config.Mounts["/cache"]);
        Assert.Equal("bind /mnt/tv (read-only)", config.Mounts["/tv"]);
        Assert.Equal("10.0.0.5", config.Networks["proxy"]);
        Assert.Equal("on-failure:5", config.Restart);

        // Round trip, and stable: the same settings read twice are the same version.
        var back = ContainerConfig.FromJson(json)!;
        Assert.True(back.SameAs(ContainerConfigs.Parse(Inspect, Image, Salt)));
    }

    [Fact]
    public void AChangedSecretShowsAsChangedWithoutSayingWhatItIs()
    {
        var before = ContainerConfigs.Parse(Inspect, Image, Salt);
        var after = ContainerConfigs.Parse(Inspect.Replace("abc123", "xyz789").Replace("\"PUID=1000\"", "\"PUID=1001\""), Image, Salt);

        var differences = ContainerConfigs.Diff(before, after);
        Assert.Equal(["API_KEY", "PUID"], differences.Select(d => d.Key));
        Assert.All(differences, d => Assert.Equal("env", d.Section));
        Assert.DoesNotContain(differences, d => (d.Before + d.After).Contains("xyz789") || (d.Before + d.After).Contains("abc123"));
        Assert.Equal("env API_KEY, PUID", ContainerConfigs.Summary(differences));
        Assert.Equal("PUID: 1000 → 1001", differences[1].Describe());

        // A different salt — another install — hashes the same secret differently.
        Assert.NotEqual(before.Env["API_KEY"], ContainerConfigs.Parse(Inspect, Image, [9, 9, 9]).Env["API_KEY"]);
    }

    [Fact]
    public void TheSummaryNamesVariablesAndSectionsOnce()
    {
        var before = ContainerConfigs.Parse(Inspect, Image, Salt);
        var after = ContainerConfigs.Parse(
            Inspect.Replace("\"TZ=Europe/London\", ", "").Replace("\"8989\"", "\"8990\"").Replace("on-failure", "always"),
            Image, Salt);

        var differences = ContainerConfigs.Diff(before, after);
        Assert.Equal("env TZ; ports; restart policy", ContainerConfigs.Summary(differences));
        Assert.Equal("removed TZ (was Europe/London)", differences[0].Describe());
    }

    [Fact]
    public void WithoutTheImageItsDefaultsStayInAndTheVersionSaysSo()
    {
        var config = ContainerConfigs.Parse(Inspect, null, Salt);
        Assert.False(config.ImageDefaultsRemoved);
        Assert.Equal("/usr/bin", config.Env["PATH"]);
        Assert.Equal("/init", config.Command);
    }
}

/// <summary>The watch after an update: every way it can pass, fail or be given up.</summary>
public class SafeUpdateRuleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static SafeUpdate Waiting(bool watch = true) => new(1, "docker", "sonarr", "old-container", "lscr.io/linuxserver/sonarr:latest",
        "sha256:old", "lscr.io/linuxserver/sonarr@sha256:aaa", "labbytwo-rollback/sonarr:1", T0, watch, TimeSpan.FromMinutes(10),
        SafeUpdateState.Waiting);

    private static SafeUpdate Watching(int restarts = 0) => Waiting() with
    {
        State = SafeUpdateState.Watching,
        NewImage = "sha256:new",
        NewContainerId = "new-container",
        WatchFrom = T0.AddMinutes(1),
        RestartBaseline = restarts,
    };

    private static WatchedContainer Running(string image = "sha256:new", string id = "new-container") =>
        new(id, image, "running", "", 0, 0);

    [Fact]
    public void AnUpdateIsWatchedOnceTheContainerIsOnANewImage()
    {
        var old = Running("sha256:old", "old-container");
        Assert.Equal(SafeUpdateState.Waiting, SafeUpdateRules.Next(Waiting(), old, [], [], T0.AddMinutes(5)).State);
        Assert.Equal(SafeUpdateState.Waiting, SafeUpdateRules.Next(Waiting(), null, [], [], T0.AddMinutes(1)).State);

        Assert.Equal(SafeUpdateState.Watching, SafeUpdateRules.Next(Waiting(), Running(), [], [], T0.AddMinutes(2)).State);
        Assert.Equal(SafeUpdateState.Updated, SafeUpdateRules.Next(Waiting(watch: false), Running(), [], [], T0.AddMinutes(2)).State);

        var never = SafeUpdateRules.Next(Waiting(), old, [], [], T0.AddMinutes(31));
        Assert.Equal(SafeUpdateState.NotUpdated, never.State);
        Assert.Contains("Watchtower found nothing newer", never.Reason);
    }

    [Theory]
    [InlineData("exited", "", 0, 1, "it stopped with exit code 1")]
    [InlineData("restarting", "", 1, 0, "it crashed and restarted")]
    [InlineData("running", "", 3, 0, "it restarted 3 times")]
    [InlineData("running", "unhealthy", 0, 0, "its health check says unhealthy")]
    public void TheContainerFailingRollsItBack(string status, string health, int restarts, int exit, string reason)
    {
        var step = SafeUpdateRules.Next(Watching(), new WatchedContainer("new-container", "sha256:new", status, health, restarts, exit),
            [], [], T0.AddMinutes(3));

        Assert.True(step.RollBack);
        Assert.Equal(SafeUpdateState.RollingBack, step.State);
        Assert.Equal(reason, step.Reason);
    }

    [Fact]
    public void RestartsBeforeTheWatchStartedAreNotCountedAgainstIt()
    {
        var step = SafeUpdateRules.Next(Watching(restarts: 2), new WatchedContainer("new-container", "sha256:new", "running", "healthy", 2, 0),
            [], [], T0.AddMinutes(3));
        Assert.Equal(SafeUpdateState.Watching, step.State);
    }

    [Fact]
    public void AConnectionDownOnlyCountsAfterTheGraceAndOnlyIfItWasUpBefore()
    {
        var down = new WatchedConnection("sonarr-api", "Sonarr", false, "Connection refused", T0.AddMinutes(1));

        // One minute into the watch: a new version still starting up.
        Assert.Equal(SafeUpdateState.Watching, SafeUpdateRules.Next(Watching(), Running(), [down], [], T0.AddMinutes(2)).State);

        var failed = SafeUpdateRules.Next(Watching(), Running(), [down], [], T0.AddMinutes(4));
        Assert.True(failed.RollBack);
        Assert.Equal("Sonarr is down (Connection refused)", failed.Reason);

        // Down since before the update was asked for: not the update's doing.
        var already = down with { Since = T0.AddHours(-3) };
        Assert.Equal(SafeUpdateState.Watching, SafeUpdateRules.Next(Watching(), Running(), [already], [], T0.AddMinutes(4)).State);
    }

    [Fact]
    public void AnAlertOnAConnectionPointingAtItRollsItBack()
    {
        var connection = new WatchedConnection("sonarr-api", "Sonarr", true);
        var fired = new Change(T0.AddMinutes(2), ChangeKinds.Alert, ChangeActions.Firing, "sonarr-api", "r1", "Alert fired: Sonarr · queue");
        var elsewhere = fired with { ConnectionId = "nas" };

        Assert.Equal(SafeUpdateState.Watching, SafeUpdateRules.Next(Watching(), Running(), [connection], [elsewhere], T0.AddMinutes(3)).State);

        var step = SafeUpdateRules.Next(Watching(), Running(), [connection], [fired], T0.AddMinutes(3));
        Assert.True(step.RollBack);
        Assert.Equal("an alert fired — Sonarr · queue", step.Reason);

        // Down and back between two looks, after the grace, counts too.
        var blip = new Change(T0.AddMinutes(5), ChangeKinds.Status, ChangeActions.Down, "sonarr-api", "", "Sonarr went down");
        Assert.Equal("Sonarr went down", SafeUpdateRules.Next(Watching(), Running(), [connection], [blip], T0.AddMinutes(6)).Reason);
    }

    [Fact]
    public void AContainerSomebodyElseReplacedOrRemovedIsLeftAlone()
    {
        var recreated = SafeUpdateRules.Next(Watching(), Running("sha256:other", "another"), [], [], T0.AddMinutes(3));
        Assert.Equal(SafeUpdateState.Abandoned, recreated.State);
        Assert.False(recreated.RollBack);

        Assert.Equal(SafeUpdateState.Abandoned, SafeUpdateRules.Next(Watching(), null, [], [], T0.AddMinutes(3)).State);
    }

    [Fact]
    public void NothingWrongForTheWholeWatchPasses()
    {
        Assert.Equal(SafeUpdateState.Watching, SafeUpdateRules.Next(Watching(), Running(), [], [], T0.AddMinutes(10)).State);
        Assert.Equal(SafeUpdateState.Passed, SafeUpdateRules.Next(Watching(), Running(), [], [], T0.AddMinutes(11)).State);
    }

    [Fact]
    public void TheBadgeCountsDownAndSaysWhyItRolledBack()
    {
        var watching = Watching();
        Assert.Equal("watching after update… 6 min left", watching.Badge(T0.AddMinutes(5)));

        var rolled = watching with { State = SafeUpdateState.RolledBack, EndedAt = T0.AddMinutes(4), Reason = "it crashed and restarted" };
        Assert.Equal("rolled back: it crashed and restarted", rolled.Badge(T0.AddMinutes(5)));
        Assert.Null(rolled.Badge(T0.AddDays(8)));
        Assert.False(rolled.CanRollBack);
        Assert.True((watching with { State = SafeUpdateState.Passed }).CanRollBack);
    }
}

/// <summary>Recreating a container on its previous image — the request, and the whole dance against a daemon.</summary>
public class ContainerRollbackTests
{
    private const string Id = "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890";

    private const string Inspect = """
        {
          "Id": "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890", "Name": "/sonarr", "Image": "sha256:new",
          "Config": {
            "Hostname": "abcdef123456", "Image": "lscr.io/linuxserver/sonarr:latest",
            "Env": ["PATH=/new/bin", "VERSION=4.1", "PUID=1000"],
            "Cmd": ["/init"], "Entrypoint": null,
            "Labels": { "org.opencontainers.image.version": "4.1", "com.docker.compose.service": "sonarr" },
            "Volumes": { "/config": {} },
            "StopTimeout": 30
          },
          "HostConfig": { "NetworkMode": "media_default", "Binds": ["/srv/sonarr:/config"], "Links": ["/db:/sonarr/database"] },
          "Mounts": [
            { "Type": "bind", "Source": "/srv/sonarr", "Destination": "/config" },
            { "Type": "volume", "Name": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "Destination": "/cache" }
          ],
          "NetworkSettings": { "Networks": {
            "media_default": { "Aliases": ["sonarr", "abcdef123456"], "NetworkID": "n1", "IPAddress": "172.18.0.4" },
            "proxy": { "Aliases": ["sonarr"], "IPAMConfig": { "IPv4Address": "10.0.0.5" } }
          } }
        }
        """;

    private const string NewImage = """
        { "Config": { "Env": ["PATH=/new/bin", "VERSION=4.1"], "Cmd": ["/init"], "Entrypoint": null,
          "Labels": { "org.opencontainers.image.version": "4.1" }, "Volumes": { "/config": {} } } }
        """;

    [Fact]
    public void TheRequestKeepsTheSettingsAndLeavesOutTheNewImagesDefaults()
    {
        var plan = ContainerRollback.Plan(Inspect, NewImage, "lscr.io/linuxserver/sonarr:latest", []);
        var body = JsonNode.Parse(plan.Body)!.AsObject();

        Assert.Equal("lscr.io/linuxserver/sonarr:latest", body["Image"]!.GetValue<string>());

        // The new version's PATH and VERSION would otherwise be baked into the old one.
        Assert.Equal(["PUID=1000"], body["Env"]!.AsArray().Select(e => e!.GetValue<string>()));
        Assert.Null(body["Cmd"]);
        Assert.False(body["Labels"]!.AsObject().ContainsKey("org.opencontainers.image.version"));
        Assert.Equal("sonarr", body["Labels"]!["com.docker.compose.service"]!.GetValue<string>());

        // Docker's default hostname is the old container's id; copying it would name the new one after it.
        Assert.Null(body["Hostname"]);

        var host = body["HostConfig"]!.AsObject();
        Assert.Equal(["db:database"], host["Links"]!.AsArray().Select(l => l!.GetValue<string>()));
        var mount = Assert.Single(host["Mounts"]!.AsArray())!;
        Assert.Equal("/cache", mount["Target"]!.GetValue<string>());
        Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", mount["Source"]!.GetValue<string>());

        var primary = body["NetworkingConfig"]!["EndpointsConfig"]!.AsObject();
        Assert.Equal(["media_default"], primary.Select(p => p.Key));
        Assert.Equal(["sonarr"], primary["media_default"]!["Aliases"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Null(primary["media_default"]!["IPAddress"]);

        var (network, settings) = Assert.Single(plan.Networks);
        Assert.Equal("proxy", network);
        Assert.Equal("10.0.0.5", settings["IPAMConfig"]!["IPv4Address"]!.GetValue<string>());
        Assert.Equal(30, plan.StopTimeout);
    }

    [Fact]
    public void ReferencesSplitWhereTheTagEndpointWantsThem()
    {
        Assert.Equal(("lscr.io/linuxserver/sonarr", "latest"), ContainerRollback.SplitReference("lscr.io/linuxserver/sonarr:latest"));
        Assert.Equal(("localhost:5000/app", "latest"), ContainerRollback.SplitReference("localhost:5000/app"));
        Assert.Equal(("postgres", "16"), ContainerRollback.SplitReference("postgres:16"));
        Assert.Null(ContainerRollback.SplitReference("postgres@sha256:abc"));
        Assert.Null(ContainerRollback.SplitReference("sha256:abc"));

        var (repository, tag) = ContainerRollback.TagFor("My_App.1", new DateTimeOffset(2026, 9, 29, 14, 2, 1, TimeSpan.Zero));
        Assert.Equal("labbytwo-rollback/my_app.1", repository);
        Assert.Equal("20260929-140201", tag);
    }

    [Fact]
    public async Task ARollBackPutsTheOldImageUnderTheSameNameAndRemovesTheFailedContainer()
    {
        using var docker = new StatefulDocker();
        var failed = docker.Add("sonarr", "lscr.io/linuxserver/sonarr:latest", "sha256:new");
        docker.Images["sha256:old"] = "sha256:old";
        docker.Images["labbytwo-rollback/sonarr:1"] = "sha256:old";

        var id = await ContainerRollback.RollBackAsync(docker.Endpoint, "sonarr",
            new ContainerRollback.Previous("sha256:old", "", "labbytwo-rollback/sonarr:1", "lscr.io/linuxserver/sonarr:latest"),
            [], CancellationToken.None);

        var back = Assert.Single(docker.Containers);
        Assert.Equal(id, back.Id);
        Assert.Equal("sonarr", back.Name);
        Assert.Equal("sha256:old", back.ImageId);
        Assert.Equal("running", back.Status);
        Assert.NotEqual(failed.Id, back.Id);

        // Created from the reference it always was, now pointing back at the old image.
        Assert.Equal("lscr.io/linuxserver/sonarr:latest", back.Reference);
        Assert.Equal("sha256:old", docker.Images["lscr.io/linuxserver/sonarr:latest"]);
        Assert.NotEqual(failed.Id[..12], back.Hostname);

        var (network, body) = Assert.Single(docker.Connected);
        Assert.Equal("proxy", network);
        Assert.Contains(id, body);

        var order = docker.Requests.Select(r => r.Split('?')[0]).Where(r => !r.StartsWith("GET", StringComparison.Ordinal)).ToList();
        Assert.Equal(
        [
            $"POST /v1.41/images/sha256:old/tag",
            $"POST /v1.41/containers/{failed.Id}/stop",
            $"POST /v1.41/containers/{failed.Id}/rename",
            "POST /v1.41/containers/create",
            "POST /v1.41/networks/proxy/connect",
            $"POST /v1.41/containers/{id}/start",
            $"DELETE /v1.41/containers/{failed.Id}",
        ], order.Select(Uri.UnescapeDataString));
    }

    [Fact]
    public async Task AnImageCleanupDeletedIsPulledBackByDigest()
    {
        using var docker = new StatefulDocker();
        docker.Add("sonarr", "lscr.io/linuxserver/sonarr:latest", "sha256:new");
        docker.Registry["lscr.io/linuxserver/sonarr@sha256:aaa"] = "sha256:old";

        await ContainerRollback.RollBackAsync(docker.Endpoint, "sonarr",
            new ContainerRollback.Previous("sha256:old", "lscr.io/linuxserver/sonarr@sha256:aaa", "labbytwo-rollback/sonarr:1",
                "lscr.io/linuxserver/sonarr:latest"),
            [], CancellationToken.None);

        Assert.Equal("sha256:old", docker.Named("sonarr").ImageId);
        Assert.Contains(docker.Requests, r => r.StartsWith("POST /v1.41/images/create", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedCreateLeavesTheContainerAsItWas()
    {
        using var docker = new StatefulDocker { Fail = (method, path) => method == "POST" && path == "/containers/create" };
        var failed = docker.Add("sonarr", "lscr.io/linuxserver/sonarr:latest", "sha256:new");
        docker.Images["sha256:old"] = "sha256:old";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ContainerRollback.RollBackAsync(docker.Endpoint, "sonarr",
            new ContainerRollback.Previous("sha256:old", "", "", "lscr.io/linuxserver/sonarr:latest"), [], CancellationToken.None));

        Assert.Contains("put back as it was", ex.Message);
        var still = Assert.Single(docker.Containers);
        Assert.Equal(failed.Id, still.Id);
        Assert.Equal("sonarr", still.Name);
        Assert.Equal("running", still.Status);
    }

    [Fact]
    public async Task APreviousImageThatIsGoneForGoodSaysSo()
    {
        using var docker = new StatefulDocker();
        docker.Add("homemade", "homemade:latest", "sha256:new");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ContainerRollback.RollBackAsync(docker.Endpoint, "homemade",
            new ContainerRollback.Previous("sha256:gone", "", "", "homemade:latest"), [], CancellationToken.None));
        Assert.Contains("built on this host", ex.Message);
        Assert.Equal("sha256:new", docker.Named("homemade").ImageId);
    }

    [Fact]
    public void AOneShotForASafeUpdateKeepsTheOldImage()
    {
        using var keep = JsonDocument.Parse(SelfUpdater.OneShotRequest("/var/run/docker.sock", ["sonarr"], null, null, cleanup: false));
        Assert.Equal(["--run-once", "sonarr"], keep.RootElement.GetProperty("Cmd").EnumerateArray().Select(e => e.GetString()));
    }
}

/// <summary>
/// Safe updates and the config history against a real database and a stand-in Docker host:
/// what is written down before an update, how the watch moves on, what a failed watch does
/// to the container and to the feed, and that every new query seeks its index.
/// </summary>
public sealed class SafeUpdateServiceTests : IDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private readonly StatefulDocker _docker = new();

    public SafeUpdateServiceTests()
    {
        Directory.CreateDirectory(_directory);
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        services.AddTestStorage(_directory);
        services.AddSingleton<IEnumerable<IWidgetType>>([]);
        services.AddSingleton<IEnumerable<ITabKind>>([]);
        services.AddSingleton<Registry>();
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<HistoryStore>();
        services.AddSingleton<HealthMonitor>();
        services.AddSingleton<ChangeStore>();
        services.AddSingleton<IncidentStore>();
        services.AddSingleton<SafeUpdateStore>();
        services.AddSingleton<SafeUpdates>();
        services.AddSingleton<ContainerConfigStore>();
        services.AddSingleton<ContainerConfigHistory>();
        _services = services.BuildServiceProvider();
        Get<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _docker.Dispose();
        TestHost.Teardown(_services, _directory);
    }

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private async Task<Connection> DockerAsync()
    {
        var connection = new Connection { Provider = "docker", Name = "NAS", Settings = new SettingsBag { ["endpoint"] = _docker.Endpoint } };
        await Get<ConfigStore>().SaveConnectionAsync(connection);
        return connection;
    }

    private static ContainerRow Row(StatefulDocker.Box box) =>
        new(box.Id, box.Name, box.Reference, box.ImageId, box.Status, "Up 1 hour", DateTimeOffset.UnixEpoch, [],
            new Dictionary<string, string>());

    private async Task<IReadOnlyList<Change>> FeedAsync() =>
        await Get<ChangeStore>().QueryAsync(new ChangeQuery(DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now.AddHours(1)));

    [Fact]
    public async Task AFailedUpdateIsWatchedRolledBackAndWrittenDown()
    {
        var connection = await DockerAsync();
        var sonarr = _docker.Add("sonarr", "lscr.io/linuxserver/sonarr:latest", "sha256:old");
        _docker.RepoDigests["sha256:old"] = ["lscr.io/linuxserver/sonarr@sha256:aaa"];
        var safe = Get<SafeUpdates>();

        var (prepared, notes) = await safe.PrepareAsync(connection, _docker.Endpoint, [Row(sonarr)],
            new SafeUpdates.Choice(true, TimeSpan.FromMinutes(10)));
        Assert.Empty(notes);
        var update = Assert.Single(prepared);
        Assert.Equal("sha256:old", update.PreviousImage);
        Assert.Equal("lscr.io/linuxserver/sonarr@sha256:aaa", update.PreviousDigest);
        Assert.StartsWith("labbytwo-rollback/sonarr:", update.RollbackTag);
        Assert.Equal("sha256:old", _docker.Images[update.RollbackTag]);

        // Watchtower has not got to it yet.
        await safe.WatchAsync();
        Assert.Equal(SafeUpdateState.Waiting, (await Get<SafeUpdateStore>().GetAsync(update.Id))!.State);

        // Watchtower recreates it on the new image.
        _docker.Containers.Remove(sonarr);
        _docker.Images["sha256:new"] = "sha256:new";
        _docker.Images["lscr.io/linuxserver/sonarr:latest"] = "sha256:new";
        var updated = _docker.Add("sonarr", "lscr.io/linuxserver/sonarr:latest", "sha256:new");
        await safe.WatchAsync();
        var watching = (await Get<SafeUpdateStore>().GetAsync(update.Id))!;
        Assert.Equal(SafeUpdateState.Watching, watching.State);
        Assert.Equal(updated.Id, watching.NewContainerId);
        Assert.Equal("watching after update… 10 min left", watching.Badge(watching.WatchFrom!.Value));
        Assert.True((await safe.LatestAsync(connection.Id))["sonarr"].CanRollBack);

        // And it falls over.
        updated.Status = "exited";
        updated.ExitCode = 1;
        await safe.WatchAsync();

        var done = (await Get<SafeUpdateStore>().GetAsync(update.Id))!;
        Assert.Equal(SafeUpdateState.RolledBack, done.State);
        Assert.Equal("it stopped with exit code 1", done.Reason);
        var back = Assert.Single(_docker.Containers);
        Assert.Equal("sha256:old", back.ImageId);
        Assert.Equal("running", back.Status);

        var feed = await FeedAsync();
        Assert.Contains(feed, c => c is { Action: ChangeActions.Watching, Subject: "sonarr" } && c.Title.Contains("10 minutes"));
        Assert.Contains(feed, c => c is { Action: ChangeActions.Failed } && c.Title == "sonarr failed its watch after updating: it stopped with exit code 1");
        var rolled = Assert.Single(feed, c => c.Action == ChangeActions.RolledBack);
        Assert.Equal("sonarr was rolled back to its previous image", rolled.Title);
        Assert.Equal(connection.Id, rolled.ConnectionId);

        // Nothing left to watch; another pass changes nothing.
        await safe.WatchAsync();
        Assert.Empty(await Get<SafeUpdateStore>().ActiveAsync());
    }

    [Fact]
    public async Task ARollBackByHandUndoesAnUpdateThatPassed()
    {
        var connection = await DockerAsync();
        var sonarr = _docker.Add("sonarr", "lscr.io/linuxserver/sonarr:latest", "sha256:old");
        var safe = Get<SafeUpdates>();
        var (prepared, _) = await safe.PrepareAsync(connection, _docker.Endpoint, [Row(sonarr)],
            new SafeUpdates.Choice(false, TimeSpan.FromMinutes(10)));

        _docker.Containers.Remove(sonarr);
        _docker.Images["lscr.io/linuxserver/sonarr:latest"] = "sha256:new";
        _docker.Images["sha256:new"] = "sha256:new";
        _docker.Add("sonarr", "lscr.io/linuxserver/sonarr:latest", "sha256:new");

        // Not watched: it only notes the update happened, and keeps the way back.
        await safe.WatchAsync();
        var updated = (await safe.LatestAsync(connection.Id))["sonarr"];
        Assert.Equal(SafeUpdateState.Updated, updated.State);
        Assert.True(updated.CanRollBack);
        Assert.Null(updated.Badge(DateTimeOffset.Now));

        Assert.Null(await safe.RollBackAsync(prepared[0].Id));
        Assert.Equal("sha256:old", _docker.Named("sonarr").ImageId);
        var after = (await safe.LatestAsync(connection.Id))["sonarr"];
        Assert.Equal(SafeUpdateState.RolledBack, after.State);
        Assert.Equal("rolled back by hand", after.Reason);
        Assert.Contains("not on an update", await safe.RollBackAsync(after.Id));
    }

    [Fact]
    public async Task OnlyOneRollBackTagIsKeptPerContainer()
    {
        var connection = await DockerAsync();
        var sonarr = _docker.Add("sonarr", "lscr.io/linuxserver/sonarr:latest", "sha256:old");
        var safe = Get<SafeUpdates>();
        var choice = new SafeUpdates.Choice(true, TimeSpan.FromMinutes(10));

        var (first, _) = await safe.PrepareAsync(connection, _docker.Endpoint, [Row(sonarr)], choice);
        await Task.Delay(1100); // tags are to the second
        sonarr.ImageId = "sha256:mid";
        _docker.Images["sha256:mid"] = "sha256:mid";
        var (second, _) = await safe.PrepareAsync(connection, _docker.Endpoint, [Row(sonarr)], choice);

        Assert.False(_docker.Images.ContainsKey(first[0].RollbackTag));
        Assert.True(_docker.Images.ContainsKey(second[0].RollbackTag));
        Assert.Equal("", (await Get<SafeUpdateStore>().GetAsync(first[0].Id))!.RollbackTag);

        // The second update ends the first's wait.
        Assert.Equal(SafeUpdateState.Abandoned, (await Get<SafeUpdateStore>().GetAsync(first[0].Id))!.State);
    }

    [Fact]
    public async Task AProxyThatWillNotTagStillLeavesAWayBack()
    {
        var connection = await DockerAsync();
        var sonarr = _docker.Add("sonarr", "lscr.io/linuxserver/sonarr:latest", "sha256:old");
        _docker.Forbid = (method, path) => method == "POST" && path.StartsWith("/images/", StringComparison.Ordinal);

        var (prepared, notes) = await Get<SafeUpdates>().PrepareAsync(connection, _docker.Endpoint, [Row(sonarr)],
            new SafeUpdates.Choice(true, TimeSpan.FromMinutes(10)));

        Assert.Equal("", Assert.Single(prepared).RollbackTag);
        Assert.Contains(notes, n => n.Contains("could not tag") && n.Contains("POST=1"));
    }

    [Fact]
    public async Task TheSettingsDefaultIsOnAndATabCanOverrideIt()
    {
        var safe = Get<SafeUpdates>();
        Assert.Equal(new SafeUpdates.Choice(true, TimeSpan.FromMinutes(10)), await safe.ChoiceAsync(""));
        Assert.False((await safe.ChoiceAsync("off")).Watch);

        await Get<AppSettingsStore>().SaveAsync(SafeUpdates.EnabledKey, "false");
        await Get<AppSettingsStore>().SaveAsync(SafeUpdates.MinutesKey, "30");
        Assert.Equal(new SafeUpdates.Choice(false, TimeSpan.FromMinutes(30)), await safe.ChoiceAsync(null));
        Assert.True((await safe.ChoiceAsync("on")).Watch);
    }

    // ---------- config history ----------

    private static string InspectFor(string id, string puid, string port = "8989") => $$"""
        { "Id": "{{id}}", "Name": "/sonarr",
          "Config": { "Image": "lscr.io/linuxserver/sonarr:latest", "Env": ["PUID={{puid}}", "API_KEY=secret-{{puid}}"] },
          "HostConfig": { "RestartPolicy": { "Name": "unless-stopped" }, "PortBindings": { "8989/tcp": [{ "HostPort": "{{port}}" }] } } }
        """;

    private static ContainerRow Named(string name, string id) =>
        new(id, name, "lscr.io/linuxserver/sonarr:latest", "sha256:x", "running", "Up 1 hour", DateTimeOffset.UnixEpoch, [],
            new Dictionary<string, string>());

    [Fact]
    public async Task TheConfigHistoryInspectsOnlyNewContainersAndRecordsWhatChanged()
    {
        var connection = await DockerAsync();
        var history = Get<ContainerConfigHistory>();
        var reads = new List<string>();
        var settings = new Dictionary<string, string> { ["c1"] = InspectFor("c1", "1000"), ["c2"] = InspectFor("c2", "1000"), ["c3"] = InspectFor("c3", "1001", "8990") };
        ContainerConfigHistory.Reader read = (row, _) =>
        {
            reads.Add(row.Id);
            return Task.FromResult<(string, string?)?>((settings[row.Id], null));
        };
        var at = DateTimeOffset.Now;

        // The first look only remembers.
        Assert.Empty(await history.NoteAsync(connection, [Named("sonarr", "c1")], at, read, CancellationToken.None));
        Assert.Equal(["c1"], reads);

        // The same container again: not even read.
        Assert.Empty(await history.NoteAsync(connection, [Named("sonarr", "c1")], at, read, CancellationToken.None));
        Assert.Single(reads);

        // Recreated with the same settings: read once, nothing written to the feed.
        Assert.Empty(await history.NoteAsync(connection, [Named("sonarr", "c2")], at, read, CancellationToken.None));
        Assert.Equal(["c1", "c2"], reads);

        // After a restart the stored version is compared, and its container id is still known.
        history.Forget();
        Assert.Empty(await history.NoteAsync(connection, [Named("sonarr", "c2")], at, read, CancellationToken.None));
        Assert.Equal(2, reads.Count);

        // Recreated with different settings: one feed entry saying what.
        var change = Assert.Single(await history.NoteAsync(connection, [Named("sonarr", "c3")], at, read, CancellationToken.None));
        Assert.Equal("sonarr config changed: env API_KEY, PUID; ports", change.Title);
        Assert.Contains("PUID: 1000 → 1001", change.Detail);
        Assert.DoesNotContain("secret-", change.Detail);
        Assert.Equal(ChangeActions.Changed, change.Action);

        var versions = await Get<ContainerConfigStore>().HistoryAsync(connection.Id, "sonarr");
        Assert.Equal(2, versions.Count);
        Assert.Equal("c3", versions[0].ContainerId);
        Assert.Equal("1001", versions[0].Config.Env["PUID"]);
        Assert.DoesNotContain("secret-", versions[0].Config.ToJson());
    }

    [Fact]
    public async Task OnlyTheNewestVersionsAreKept()
    {
        var store = Get<ContainerConfigStore>();
        for (var i = 0; i < ContainerConfigStore.Keep + 5; i++)
            await store.AddAsync("docker", "sonarr", $"c{i}", DateTimeOffset.Now, new ContainerConfig { Image = $"v{i}" });
        await store.AddAsync("docker", "radarr", "r1", DateTimeOffset.Now, new ContainerConfig { Image = "r" });

        var versions = await store.HistoryAsync("docker", "sonarr", 100);
        Assert.Equal(ContainerConfigStore.Keep, versions.Count);
        Assert.Equal($"v{ContainerConfigStore.Keep + 4}", versions[0].Config.Image);
        Assert.Single(await store.HistoryAsync("docker", "radarr"));

        var latest = await store.LatestAsync("docker");
        Assert.Equal(["radarr", "sonarr"], latest.Keys.Order());
        Assert.Equal($"c{ContainerConfigStore.Keep + 4}", latest["sonarr"].ContainerId);
    }

    // ---------- query plans ----------

    private async Task<List<string>> PlanAsync(string sql)
    {
        await using var db = await Get<Db>().OpenAsync();
        var explain = db.CreateCommand();
        explain.CommandText = "EXPLAIN QUERY PLAN " + sql;
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(sql, @"\$\w+"))
        {
            if (!explain.Parameters.Contains(match.Value))
                explain.Parameters.AddWithValue(match.Value, match.Value is "$connection" or "$container" ? "x" : 1);
        }
        var steps = new List<string>();
        await using var reader = await explain.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            steps.Add(reader.GetString(3));
        return steps;
    }

    [Fact]
    public async Task TheConfigHistorySeeksItsIndexAndNeverSorts()
    {
        foreach (var sql in new[] { ContainerConfigStore.LatestSql, ContainerConfigStore.HistorySql, ContainerConfigStore.PruneSql })
        {
            var plan = await PlanAsync(sql);
            Assert.DoesNotContain(plan, step => step.StartsWith("SCAN container_configs", StringComparison.Ordinal) && !step.Contains("INDEX"));
            Assert.Contains(plan, step => step.Contains("ix_container_configs_name", StringComparison.Ordinal));
            Assert.DoesNotContain(plan, step => step.Contains("TEMP B-TREE", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task SafeUpdateQueriesSeekTheirIndexes()
    {
        var active = await PlanAsync(SafeUpdateStore.ActiveSql);
        Assert.Contains(active, step => step.Contains("ix_safe_updates_active", StringComparison.Ordinal));
        Assert.DoesNotContain(active, step => step.Contains("TEMP B-TREE", StringComparison.Ordinal));

        foreach (var sql in new[] { SafeUpdateStore.LatestSql, SafeUpdateStore.TaggedSql, SafeUpdateStore.PruneSql })
        {
            var plan = await PlanAsync(sql);
            Assert.Contains(plan, step => step.Contains("ix_safe_updates_container", StringComparison.Ordinal));
            Assert.DoesNotContain(plan, step => step.StartsWith("SCAN safe_updates", StringComparison.Ordinal) && !step.Contains("INDEX"));
            Assert.DoesNotContain(plan, step => step.Contains("TEMP B-TREE", StringComparison.Ordinal));
        }
    }
}

/// <summary>What the Containers tab draws for a watch in progress, a roll-back, and a config history.</summary>
public class SafeUpdateRenderTests
{
    private static async Task<string> RenderAsync<T>(Dictionary<string, object?> parameters)
        where T : Microsoft.AspNetCore.Components.IComponent
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new Microsoft.AspNetCore.Components.Web.HtmlRenderer(services,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        return WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<T>(Microsoft.AspNetCore.Components.ParameterView.FromDictionary(parameters)))
            .ToHtmlString()));
    }

    private static SafeUpdate Update(string name, SafeUpdateState state, string newImage) => new(1, "docker", name, "old",
        "lscr.io/linuxserver/" + name, "sha256:0123456789abcdef", "", "labbytwo-rollback/" + name + ":1", DateTimeOffset.Now.AddMinutes(-3),
        true, TimeSpan.FromMinutes(10), state, newImage, "new", DateTimeOffset.Now.AddMinutes(-2), EndedAt: DateTimeOffset.Now,
        Reason: "it crashed and restarted");

    [Fact]
    public async Task AWatchCountsDownAndAnUpdateThatCanBeUndoneGetsTheButton()
    {
        var sonarr = ContainerListTests.Row("sonarr");
        var radarr = ContainerListTests.Row("radarr");
        var labby = ContainerListTests.Row("labbytwo");
        var safe = new Dictionary<string, SafeUpdate>
        {
            ["sonarr"] = Update("sonarr", SafeUpdateState.Watching, sonarr.ImageId),
            ["radarr"] = Update("radarr", SafeUpdateState.RolledBack, radarr.ImageId),
            ["labbytwo"] = Update("labbytwo", SafeUpdateState.Passed, labby.ImageId),
        };

        var html = await RenderAsync<Components.Pages.Kinds.ContainerGroupCard>(new()
        {
            [nameof(Components.Pages.Kinds.ContainerGroupCard.Group)] = new ContainerGroup(null, [sonarr, radarr, labby]),
            [nameof(Components.Pages.Kinds.ContainerGroupCard.Safe)] = safe,
            [nameof(Components.Pages.Kinds.ContainerGroupCard.IsSelf)] = (Func<ContainerRow, bool>)(r => r.Name == "labbytwo"),
        });

        Assert.Contains("watching after update… 8 min left", html);
        Assert.Contains("aria-label=\"Roll sonarr back to its previous image\"", html);

        // Already rolled back: says why, and offers nothing more.
        Assert.Contains("rolled back: it crashed and restarted", html);
        Assert.DoesNotContain("aria-label=\"Roll radarr back", html);

        // Never for LabbyTwo itself, whatever is recorded.
        Assert.DoesNotContain("aria-label=\"Roll labbytwo back", html);
    }

    [Fact]
    public async Task TheInspectShowsEachVersionAgainstTheOneBefore()
    {
        var details = new ContainerDetails("c3", "sonarr", "lscr.io/linuxserver/sonarr", "sha256:x", false, null, null, null,
            "unless-stopped", 0, [], [], [], new Dictionary<string, string>(), "");
        static ContainerConfig Version(string puid, string key) => new()
        {
            Image = "lscr.io/linuxserver/sonarr",
            Env = new(StringComparer.Ordinal) { ["PUID"] = puid, ["API_KEY"] = key },
            Restart = "unless-stopped",
        };
        var history = new List<ContainerConfigVersion>
        {
            new(3, "c3", DateTimeOffset.Now, Version("1001", "hidden #bbbbbbbb")),
            new(2, "c2", DateTimeOffset.Now.AddDays(-1), Version("1000", "hidden #aaaaaaaa")),
        };

        var html = await RenderAsync<Components.Pages.Kinds.ContainerInspect>(new()
        {
            [nameof(Components.Pages.Kinds.ContainerInspect.Details)] = details,
            [nameof(Components.Pages.Kinds.ContainerInspect.History)] = history,
        });

        Assert.Contains("Config history", html);
        Assert.Contains("env API_KEY, PUID", html);
        Assert.Contains("the first version recorded", html);
        Assert.Contains("1000", html);
        Assert.Contains("1001", html);
        Assert.Contains("never stored", html);

        var none = await RenderAsync<Components.Pages.Kinds.ContainerInspect>(new()
        {
            [nameof(Components.Pages.Kinds.ContainerInspect.Details)] = details,
            [nameof(Components.Pages.Kinds.ContainerInspect.History)] = new List<ContainerConfigVersion>(),
        });
        Assert.Contains("Nothing recorded yet", none);
    }
}
