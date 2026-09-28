using System.Net;
using System.Text;
using LabbyTwo.Services;

namespace LabbyTwo.Tests;

/// <summary>
/// An HttpListener on a free loopback port. A random port alone collides now and then when
/// test classes run in parallel — or with another test run on the same machine — and the
/// failure is an unrelated-looking "file is being used by another process" from Start.
/// </summary>
internal static class LoopbackListener
{
    public static HttpListener Start(out int port)
    {
        for (var attempt = 0; ; attempt++)
        {
            port = 20000 + Random.Shared.Next(20000);
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return listener;
            }
            catch (HttpListenerException) when (attempt < 20)
            {
                listener.Close();
            }
        }
    }
}

/// <summary>
/// Reaching Docker by any of the addresses people write, and saying something useful when a
/// socket proxy in the way says no.
/// </summary>
public sealed class DockerEndpointTests
{
    [Theory]
    [InlineData("/var/run/docker.sock", DockerEndpointKind.Unix, "/var/run/docker.sock")]
    [InlineData("unix:///var/run/docker.sock", DockerEndpointKind.Unix, "/var/run/docker.sock")]
    [InlineData("", DockerEndpointKind.Unix, "/var/run/docker.sock")]
    [InlineData("tcp://socket-proxy:2375", DockerEndpointKind.Tcp, "socket-proxy:2375")]
    [InlineData("tcp://socket-proxy", DockerEndpointKind.Tcp, "socket-proxy:2375")]
    [InlineData("TCP://192.168.1.50:2375/", DockerEndpointKind.Tcp, "192.168.1.50:2375")]
    [InlineData("http://socket-proxy:2375", DockerEndpointKind.Tcp, "socket-proxy:2375")]
    [InlineData("socket-proxy:2375", DockerEndpointKind.Tcp, "socket-proxy:2375")]
    [InlineData("tcp://[fd00::1]", DockerEndpointKind.Tcp, "[fd00::1]:2375")]
    [InlineData("tcp://[fd00::1]:2376", DockerEndpointKind.Tcp, "[fd00::1]:2376")]
    [InlineData("npipe:////./pipe/docker_engine", DockerEndpointKind.NamedPipe, "docker_engine")]
    [InlineData("npipe://./pipe/docker_engine", DockerEndpointKind.NamedPipe, "docker_engine")]
    [InlineData(@"\\.\pipe\docker_engine", DockerEndpointKind.NamedPipe, "docker_engine")]
    public void Reads_every_spelling_of_an_endpoint(string written, DockerEndpointKind kind, string address)
    {
        var endpoint = DockerEndpoint.Parse(written);

        Assert.Equal(kind, endpoint.Kind);
        Assert.Equal(address, endpoint.Address);
    }

    [Fact]
    public void Writes_a_tcp_endpoint_the_way_docker_host_wants_it()
    {
        // This is what the one-shot Watchtower is given, so it has to be a form the Docker
        // client inside it accepts — scheme included, port filled in.
        Assert.Equal("tcp://socket-proxy:2375", DockerEndpoint.Parse("socket-proxy:2375").ToString());
        Assert.Equal("tcp://socket-proxy:2375", DockerEndpoint.Parse("http://socket-proxy").ToString());
    }

    [Theory]
    [InlineData("https://docker:2376")]
    [InlineData("ssh://me@nas")]
    public void Refuses_transports_it_cannot_speak_by_name(string written)
    {
        // Attempting these would fail with a TLS or protocol error about something else
        // entirely; saying so up front points at the fix.
        var failure = Assert.Throws<InvalidOperationException>(() => DockerEndpoint.Parse(written));
        Assert.Contains("does not speak to Docker", failure.Message);
    }

    [Theory]
    [InlineData("GET", "/containers/json?all=1", "CONTAINERS=1")]
    [InlineData("GET", "/containers/abc123/json", "CONTAINERS=1")]
    [InlineData("GET", "/images/fennch%2Flabbytwo%3Alatest/json", "IMAGES=1")]
    [InlineData("POST", "/containers/abc123/restart", "ALLOW_RESTARTS=1")]
    [InlineData("POST", "/v1.41/containers/abc123/restart", "ALLOW_RESTARTS=1")]
    [InlineData("POST", "/containers/abc123/start", "ALLOW_START=1")]
    [InlineData("POST", "/containers/create", "CONTAINERS=1 and POST=1")]
    [InlineData("POST", "/images/create?fromImage=containrrr%2Fwatchtower&tag=latest", "IMAGES=1 and POST=1")]
    [InlineData("POST", "/containers/abc123/exec", "EXEC=1")]
    [InlineData("POST", "/exec/xyz/start", "EXEC=1")]
    [InlineData("GET", "/info", "INFO=1")]
    [InlineData("POST", "/networks/create", "NETWORKS=1 and POST=1")]
    public void Names_the_proxy_flag_each_call_needs(string method, string path, string flag) =>
        Assert.Contains(flag, DockerProxyDeniedException.RuleFor(method, path).Flags);

    [Fact]
    public void Is_honest_that_tecnativa_needs_post_for_restarts()
    {
        // The difference between the two proxies that decides whether restarting is safe to
        // allow; leaving it out would have people enable POST=1 without knowing what it opens.
        var rule = DockerProxyDeniedException.RuleFor("POST", "/containers/abc/restart");
        Assert.Contains("POST=1", rule.Flags);
        Assert.Contains("creating containers", rule.Flags);
    }

    /// <summary>Answers every request with one status and body, like a proxy that says no.</summary>
    private sealed class OneAnswer : IDisposable
    {
        private readonly HttpListener _listener;

        public int Port { get; }

        public OneAnswer(int status, string contentType, string body)
        {
            _listener = LoopbackListener.Start(out var port);
            Port = port;
            _ = Task.Run(async () =>
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

                    var bytes = Encoding.UTF8.GetBytes(body);
                    context.Response.StatusCode = status;
                    context.Response.ContentType = contentType;
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            });
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

    private const string HaproxyForbidden =
        "<html><body><h1>403 Forbidden</h1>\nRequest forbidden by administrative rules.\n</body></html>\n";

    [Fact]
    public async Task A_proxy_refusing_a_read_says_to_enable_containers()
    {
        using var proxy = new OneAnswer(403, "text/html", HaproxyForbidden);

        var failure = await Assert.ThrowsAsync<DockerProxyDeniedException>(() => DockerSocket.GetAsync(
            $"tcp://127.0.0.1:{proxy.Port}", TimeSpan.FromSeconds(5), "/containers/json?all=1", CancellationToken.None));

        Assert.Contains("socket proxy refused GET /containers/json", failure.Message);
        Assert.Contains("CONTAINERS=1", failure.Message);

        // The query string is noise in a sentence meant for a person.
        Assert.DoesNotContain("all=1", failure.Message);
    }

    [Fact]
    public async Task A_proxy_refusing_a_restart_says_to_enable_restarts()
    {
        using var proxy = new OneAnswer(403, "text/html", HaproxyForbidden);

        var failure = await Assert.ThrowsAsync<DockerProxyDeniedException>(() => DockerSocket.PostAsync(
            $"tcp://127.0.0.1:{proxy.Port}", TimeSpan.FromSeconds(5), "/containers/abc/restart", null,
            CancellationToken.None));

        Assert.Contains("ALLOW_RESTARTS=1", failure.Message);
    }

    [Fact]
    public async Task The_docker_card_shows_the_flag_rather_than_a_status_code()
    {
        using var proxy = new OneAnswer(403, "text/html", HaproxyForbidden);
        var connection = new LabbyTwo.Core.Connection
        {
            Provider = "docker",
            Name = "Docker",
            Settings = new LabbyTwo.Core.SettingsBag { ["endpoint"] = $"tcp://127.0.0.1:{proxy.Port}" },
        };

        var result = await new LabbyTwo.Providers.DockerProvider().ProbeAsync(connection, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("CONTAINERS=1", result.Message);
    }

    [Fact]
    public async Task A_403_from_docker_itself_keeps_dockers_own_reason()
    {
        // An authorization plugin on the daemon answers 403 in Docker's JSON. That is not a
        // proxy flag, and blaming one would send the reader after the wrong thing.
        using var daemon = new OneAnswer(403, "application/json", "{\"message\":\"authorization denied by plugin opa\"}");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => DockerSocket.GetAsync(
            $"tcp://127.0.0.1:{daemon.Port}", TimeSpan.FromSeconds(5), "/containers/json", CancellationToken.None));

        Assert.IsNotType<DockerProxyDeniedException>(failure);
        Assert.Equal("Docker: authorization denied by plugin opa", failure.Message);
    }
}
