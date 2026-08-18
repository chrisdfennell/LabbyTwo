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
        private readonly HttpListener _listener = new();

        public int Port { get; }
        public string ImageName { get; set; } = "fennch/labbytwo:latest";
        public string[] RepoDigests { get; set; } = ["fennch/labbytwo@sha256:deadbeef"];
        public List<string> Paths { get; } = [];
        public string? CreateBody { get; private set; }

        public FakeDocker()
        {
            Port = 20000 + Random.Shared.Next(10000);
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
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

                if (path.Contains("/containers/") && path.EndsWith("/json"))
                {
                    body = JsonSerializer.Serialize(new
                    {
                        Name = "/labbytwo-labbytwo-1",
                        Config = new { Image = ImageName },
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

    public SelfUpdaterTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "labbytwo-update-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_directory);

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        services.AddSingleton<IHostEnvironment>(new Env(_directory));
        services.AddSingleton(Options.Create(new LabbyOptions { DatabasePath = Path.Combine(_directory, "t.db") }));
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

        // Without the socket it cannot do anything at all.
        var binds = root.GetProperty("HostConfig").GetProperty("Binds")
            .EnumerateArray().Select(b => b.GetString()).ToArray();
        Assert.Contains(binds, bind => bind!.EndsWith(":/var/run/docker.sock"));

        // And it should clean itself up rather than leaving a dead container behind.
        Assert.True(root.GetProperty("HostConfig").GetProperty("AutoRemove").GetBoolean());

        Assert.Contains("POST /v1.41/containers/update123/start", _docker.Paths);
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

    public void Dispose()
    {
        _docker.Dispose();
        TestHost.Teardown(_services, _directory);
    }
}
