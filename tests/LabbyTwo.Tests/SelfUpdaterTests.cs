using System.Net;
using System.Text;
using System.Text.Json;
using LabbyTwo.Core;
using LabbyTwo.Providers;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LabbyTwo.Tests;

/// <summary>
/// The self-updater against a stand-in Docker daemon. Worth testing at this level rather
/// than in pieces: the risk is not that a helper is wrong, it is that the container we ask
/// Docker to create is subtly the wrong one — watching the wrong name, or missing the
/// socket it needs to do anything.
/// </summary>
public sealed class SelfUpdaterTests : IDisposable
{
    private sealed class Env(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "LabbyTwo.Tests";
        public string ContentRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    /// <summary>A Docker Engine that answers just enough, over TCP so no socket is needed.</summary>
    private sealed class FakeDocker : IDisposable
    {
        private readonly HttpListener _listener;

        public int Port { get; }
        public string ImageName { get; set; } = "fennch/labbytwo:latest";
        public string[] RepoDigests { get; set; } = ["fennch/labbytwo@sha256:deadbeef"];
        public string[]? Dns { get; set; }
        public string NetworkMode { get; set; } = "labbytwo_default";

        /// <summary>Calls to refuse the way a socket proxy does: HTTP 403 and HAProxy's HTML page.</summary>
        public Func<string, string, bool> Forbid { get; set; } = (_, _) => false;

        public List<string> Paths { get; } = [];
        public string? CreateBody { get; private set; }

        public FakeDocker()
        {
            _listener = LoopbackListener.Start(out var port);
            Port = port;
            _ = Task.Run(Loop);
        }

        private async Task Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                var path = context.Request.Url?.AbsolutePath ?? "";
                lock (Paths)
                    Paths.Add($"{context.Request.HttpMethod} {path}");

                string body = "{}";

                if (Forbid(context.Request.HttpMethod, path))
                {
                    var page = Encoding.UTF8.GetBytes(
                        "<html><body><h1>403 Forbidden</h1>\nRequest forbidden by administrative rules.\n</body></html>\n");
                    context.Response.StatusCode = 403;
                    context.Response.ContentType = "text/html";
                    context.Response.ContentLength64 = page.Length;
                    await context.Response.OutputStream.WriteAsync(page);
                    context.Response.Close();
                    continue;
                }

                if (path.Contains("/containers/") && path.EndsWith("/json"))
                {
                    body = JsonSerializer.Serialize(new
                    {
                        Name = "/labbytwo-labbytwo-1",
                        Config = new { Image = ImageName },
                        HostConfig = new { Dns, NetworkMode },
                    });
                }
                else if (path.Contains("/images/") && path.EndsWith("/json"))
                {
                    body = JsonSerializer.Serialize(new { RepoDigests });
                }
                else if (path.EndsWith("/containers/create"))
                {
                    using var reader = new StreamReader(context.Request.InputStream);
                    CreateBody = await reader.ReadToEndAsync();
                    body = JsonSerializer.Serialize(new { Id = "update123" });
                }

                var bytes = Encoding.UTF8.GetBytes(body);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }

        public void Dispose()
        {
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch (Exception)
            {
                // Nothing useful to do while tearing down a test double.
            }
        }
    }

    private readonly string _directory;
    private readonly ServiceProvider _services;
    private readonly FakeDocker _docker = new();
    private readonly LabbyOptions _options;

    public SelfUpdaterTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "labbytwo-update-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_directory);

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        services.AddSingleton<IHostEnvironment>(new Env(_directory));
        // Kept so a test can switch on the Watchtower API before the updater reads it.
        _options = new LabbyOptions { DatabasePath = Path.Combine(_directory, "t.db") };
        services.AddSingleton(Options.Create(_options));
        services.AddSingleton<IConnectionProvider>(new DockerProvider());
        services.AddSingleton<IEnumerable<IWidgetType>>([]);
        services.AddSingleton<IEnumerable<ITabKind>>([]);
        services.AddSingleton<Registry>();
        services.AddSingleton<Db>();
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<SelfUpdater>();
        _services = services.BuildServiceProvider();
    }

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    /// <summary>Points the updater at the fake daemon the same way a user's Docker connection would.</summary>
    private async Task ConnectAsync() =>
        await Get<ConfigStore>().SaveConnectionAsync(new Connection
        {
            Provider = "docker",
            Name = "Docker",
            Settings = new SettingsBag { ["endpoint"] = $"tcp://127.0.0.1:{_docker.Port}" },
        });

    [Fact]
    public async Task Works_out_which_container_it_is_and_what_it_is_running()
    {
        await ConnectAsync();

        var status = await Get<SelfUpdater>().StatusAsync();

        Assert.True(status.Ready);
        Assert.Equal("labbytwo-labbytwo-1", status.Self?.Container);
        Assert.Equal("fennch/labbytwo", status.Self?.Image.Repository);
        Assert.Equal("sha256:deadbeef", status.Self?.Digest);
    }

    [Fact]
    public async Task Does_not_contact_a_registry_unless_asked()
    {
        await ConnectAsync();

        await Get<SelfUpdater>().StatusAsync();

        // The settings page promises nothing is contacted until you press the button. A
        // status check that reached Docker Hub to render a button would break that quietly.
        Assert.DoesNotContain(_docker.Paths, path => path.Contains("hub.docker.com"));
        Assert.All(_docker.Paths, path => Assert.StartsWith("GET ", path));
    }

    [Fact]
    public async Task Refuses_when_the_image_was_built_here_rather_than_pulled()
    {
        // What "docker compose build" leaves behind: an image with no repo digest. There
        // is nothing published to compare against and nothing to pull.
        _docker.ImageName = "labbytwo-labbytwo";
        _docker.RepoDigests = [];
        await ConnectAsync();

        var status = await Get<SelfUpdater>().StatusAsync();

        Assert.False(status.Ready);
        Assert.Contains("built here", status.Reason);
    }

    [Fact]
    public async Task Refuses_when_there_is_no_socket_at_all()
    {
        // No Docker connection configured and, on a test agent, no socket at the default
        // path either.
        var status = await Get<SelfUpdater>().StatusAsync();

        if (File.Exists(DockerSocket.DefaultEndpoint))
            return;

        Assert.False(status.Ready);
        Assert.Contains("not mounted", status.Reason);
    }

    [Fact]
    public async Task Asks_docker_for_a_one_shot_watchtower_aimed_at_this_container()
    {
        await ConnectAsync();

        await Get<SelfUpdater>().StartUpdateAsync();

        Assert.NotNull(_docker.CreateBody);
        using var request = JsonDocument.Parse(_docker.CreateBody!);
        var root = request.RootElement;

        Assert.Equal($"{SelfUpdater.WatchtowerImage}:latest", root.GetProperty("Image").GetString());

        var command = root.GetProperty("Cmd").EnumerateArray().Select(a => a.GetString()).ToArray();

        // --run-once matters: without it the update container stays alive as a second
        // scheduler, competing with whatever the user already runs.
        Assert.Contains("--run-once", command);

        // Naming the container matters just as much. Watchtower with no target watches
        // every container on the host, so a bug here updates the whole NAS.
        Assert.Contains("labbytwo-labbytwo-1", command);

        // Without Docker it cannot do anything at all. The fake is reached over TCP, as a
        // socket proxy would be, so Docker is handed over as DOCKER_HOST on the same network
        // rather than as a bind mount of an address that is not a file.
        var env = root.GetProperty("Env").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains($"DOCKER_HOST=tcp://127.0.0.1:{_docker.Port}", env);
        Assert.Equal("labbytwo_default", root.GetProperty("HostConfig").GetProperty("NetworkMode").GetString());
        Assert.False(root.GetProperty("HostConfig").TryGetProperty("Binds", out _));

        // And it should clean itself up rather than leaving a dead container behind.
        Assert.True(root.GetProperty("HostConfig").GetProperty("AutoRemove").GetBoolean());

        Assert.Contains("POST /v1.41/containers/update123/start", _docker.Paths);
    }

    [Fact]
    public async Task Gives_watchtower_the_same_dns_servers_as_this_container()
    {
        // A container that only resolves anything because of "dns:" in its compose file
        // would otherwise start a helper that cannot find Docker Hub.
        _docker.Dns = ["192.168.1.1", "1.1.1.1"];
        await ConnectAsync();

        await Get<SelfUpdater>().StartUpdateAsync();

        using var request = JsonDocument.Parse(_docker.CreateBody!);
        var dns = request.RootElement.GetProperty("HostConfig").GetProperty("Dns")
            .EnumerateArray().Select(d => d.GetString()!).ToArray();
        Assert.Equal(["192.168.1.1", "1.1.1.1"], dns);
    }

    [Fact]
    public async Task Pulls_watchtower_before_creating_it()
    {
        // A host that has never run Watchtower would otherwise fail with "no such image"
        // and leave nothing to show for the click.
        await ConnectAsync();

        await Get<SelfUpdater>().StartUpdateAsync();

        var pull = _docker.Paths.FindIndex(p => p.Contains("/images/create"));
        var create = _docker.Paths.FindIndex(p => p.EndsWith("/containers/create"));

        Assert.True(pull >= 0, "Watchtower was never pulled.");
        Assert.True(pull < create, "The container was created before its image was pulled.");
    }

    [Fact]
    public void A_socket_is_handed_to_the_helper_as_a_bind_mount()
    {
        var self = new SelfUpdater.Self("labbytwo-labbytwo-1", ImageRef.Parse("fennch/labbytwo:latest"), "sha256:x",
            Network: "labbytwo_default");

        foreach (var (endpoint, path) in new[]
                 {
                     ("/var/run/docker.sock", "/var/run/docker.sock"),
                     ("unix:///run/user/1000/docker.sock", "/run/user/1000/docker.sock"),
                 })
        {
            using var request = JsonDocument.Parse(SelfUpdater.OneShotRequest(endpoint, self));
            var host = request.RootElement.GetProperty("HostConfig");

            var binds = host.GetProperty("Binds").EnumerateArray().Select(b => b.GetString()!).ToArray();
            Assert.Equal([$"{path}:/var/run/docker.sock"], binds);

            // The socket brings its own reach; joining a network is only for finding a proxy.
            Assert.False(host.TryGetProperty("NetworkMode", out _));

            // The socket is where Docker is; DOCKER_HOST would only point somewhere else.
            var env = request.RootElement.GetProperty("Env").EnumerateArray().Select(e => e.GetString()!).ToArray();
            Assert.DoesNotContain(env, e => e.StartsWith("DOCKER_HOST="));

            // Watchtower's client defaults to API 1.25, which Docker 29 refuses outright.
            Assert.Contains($"DOCKER_API_VERSION={DockerSocket.ApiVersionNumber}", env);
        }
    }

    [Fact]
    public async Task Says_which_proxy_flag_is_missing_rather_than_blaming_the_image()
    {
        // A proxy with CONTAINERS=1 but not IMAGES=1. The image inspect used to be read as
        // "no digest", which says the image was built here — true of nothing on this box.
        _docker.Forbid = (_, path) => path.Contains("/images/");
        await ConnectAsync();

        var status = await Get<SelfUpdater>().StatusAsync();

        Assert.False(status.Ready);
        Assert.Contains("IMAGES=1", status.Reason);
        Assert.DoesNotContain("built here", status.Reason);
    }

    [Fact]
    public async Task Says_which_proxy_flag_is_missing_when_it_cannot_see_containers()
    {
        _docker.Forbid = (_, path) => path.Contains("/containers");
        await ConnectAsync();

        var status = await Get<SelfUpdater>().StatusAsync();

        Assert.False(status.Ready);
        Assert.Contains("CONTAINERS=1", status.Reason);
        Assert.DoesNotContain("could not work out", status.Reason);
    }

    [Fact]
    public async Task Status_says_the_docker_api_is_how_it_would_update_when_no_watchtower_is_configured()
    {
        await ConnectAsync();

        var status = await Get<SelfUpdater>().StatusAsync();

        Assert.Equal(SelfUpdater.UpdateMode.DockerApi, status.Mode);

        // Over TCP it is probably a proxy, and the flags this needs make the proxy pointless.
        // Worth saying before the click.
        Assert.Contains("POST=1", status.Reason);
    }

    [Fact]
    public async Task Status_says_watchtower_when_its_api_is_configured_even_without_docker()
    {
        // No Docker connection at all: with a Watchtower to ask, LabbyTwo does not need one.
        _options.Watchtower.Url = "http://watchtower:8080";

        var status = await Get<SelfUpdater>().StatusAsync();

        if (File.Exists(DockerSocket.DefaultEndpoint) || Environment.GetEnvironmentVariable("DOCKER_HOST") is { Length: > 0 })
            return;

        Assert.True(status.Ready);
        Assert.Equal(SelfUpdater.UpdateMode.WatchtowerApi, status.Mode);
        Assert.Null(status.Self);
    }

    [Fact]
    public async Task Status_still_identifies_the_container_in_watchtower_mode()
    {
        _options.Watchtower.Url = "http://watchtower:8080";
        await ConnectAsync();

        var status = await Get<SelfUpdater>().StatusAsync();

        Assert.True(status.Ready);
        Assert.Equal(SelfUpdater.UpdateMode.WatchtowerApi, status.Mode);
        Assert.Equal("labbytwo-labbytwo-1", status.Self?.Container);

        // The proxy caveat is about creating containers, which this mode never does.
        Assert.Null(status.Reason);
    }

    /// <summary>A Watchtower HTTP API that records what it was asked and answers as told.</summary>
    private sealed class FakeWatchtower : IDisposable
    {
        private readonly HttpListener _listener;

        public int Port { get; }
        public HttpStatusCode Answer { get; set; } = HttpStatusCode.OK;
        public List<(string Method, string Path, string? Authorization)> Requests { get; } = [];

        public FakeWatchtower()
        {
            _listener = LoopbackListener.Start(out var port);
            Port = port;
            _ = Task.Run(Loop);
        }

        private async Task Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                lock (Requests)
                    Requests.Add((context.Request.HttpMethod, context.Request.Url?.AbsolutePath ?? "",
                        context.Request.Headers["Authorization"]));

                context.Response.StatusCode = (int)Answer;
                context.Response.ContentLength64 = 0;
                context.Response.Close();
            }
        }

        public void Dispose()
        {
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch (Exception)
            {
                // Nothing useful to do while tearing down a test double.
            }
        }
    }

    [Fact]
    public async Task Asks_watchtower_over_its_api_instead_of_creating_a_container()
    {
        using var watchtower = new FakeWatchtower();
        _options.Watchtower.Url = $"http://127.0.0.1:{watchtower.Port}";
        _options.Watchtower.Token = "s3cret";
        await ConnectAsync();

        var answer = await Get<SelfUpdater>().StartUpdateAsync();

        var request = Assert.Single(watchtower.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/v1/update", request.Path);
        Assert.Equal("Bearer s3cret", request.Authorization);

        // The whole point: nothing was created or pulled through Docker.
        Assert.DoesNotContain(_docker.Paths, p => p.StartsWith("POST "));

        // Watchtower answering while this process is still alive means it replaced nothing,
        // and the page should say so rather than sit on "Starting…" for ever.
        Assert.Contains("found nothing newer", answer);
    }

    [Fact]
    public async Task Says_the_token_is_wrong_when_watchtower_refuses_it()
    {
        using var watchtower = new FakeWatchtower { Answer = HttpStatusCode.Unauthorized };
        _options.Watchtower.Url = $"http://127.0.0.1:{watchtower.Port}/v1/update";
        _options.Watchtower.Token = "wrong";

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Get<SelfUpdater>().StartUpdateAsync());

        Assert.Contains("WATCHTOWER_HTTP_API_TOKEN", failure.Message);
        Assert.Equal("/v1/update", Assert.Single(watchtower.Requests).Path);
    }

    [Fact]
    public async Task Says_the_api_is_off_when_watchtower_has_no_update_endpoint()
    {
        using var watchtower = new FakeWatchtower { Answer = HttpStatusCode.NotFound };
        _options.Watchtower.Url = $"http://127.0.0.1:{watchtower.Port}";

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Get<SelfUpdater>().StartUpdateAsync());

        Assert.Contains("--http-api-update", failure.Message);
    }

    [Theory]
    [InlineData("http://watchtower:8080", "http://watchtower:8080/v1/update")]
    [InlineData("http://watchtower:8080/", "http://watchtower:8080/v1/update")]
    [InlineData("http://watchtower:8080/v1/update", "http://watchtower:8080/v1/update")]
    [InlineData("watchtower:8080", "http://watchtower:8080/v1/update")]
    public void Takes_the_watchtower_address_with_or_without_the_path(string configured, string expected) =>
        Assert.Equal(expected, SelfUpdater.WatchtowerUpdateUrl(configured));

    public void Dispose()
    {
        _docker.Dispose();
        TestHost.Teardown(_services, _directory);
    }
}
