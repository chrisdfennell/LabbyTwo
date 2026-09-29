using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LabbyTwo.Core;
using LabbyTwo.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabbyTwo.Tests;

/// <summary>
/// Whether every container's registry has something newer, against a stand-in registry and
/// a stand-in Docker daemon — nothing here reaches the internet. The risks worth pinning are
/// the quiet ones: comparing against a platform's digest instead of the index's, which calls
/// every multi-arch image out of date for ever; guessing about a local build; and asking a
/// rate-limited registry forty times when it said stop after one.
/// </summary>
public sealed class ContainerUpdateTests : IDisposable
{
    private const string Index = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string Newer = "sha256:2222222222222222222222222222222222222222222222222222222222222222";
    private const string Platform = "sha256:3333333333333333333333333333333333333333333333333333333333333333";

    /// <summary>
    /// A registry: the v2 manifest API behind an anonymous token service, and Docker Hub's tag
    /// API beside it. Each request is handled on its own task, so a test can see how many
    /// were in flight at once.
    /// </summary>
    private sealed class FakeRegistry : IDisposable
    {
        private readonly HttpListener _listener;
        private int _inFlight;

        public int Port { get; }
        public string Host => $"127.0.0.1:{Port}";

        /// <summary>"repo:tag" → the digest its manifest has.</summary>
        public Dictionary<string, string> Digests { get; } = new(StringComparer.Ordinal);

        /// <summary>Repositories the token service will not give an anonymous token for.</summary>
        public HashSet<string> Private { get; } = new(StringComparer.Ordinal);

        public bool SendDigestHeader { get; set; } = true;
        public bool RateLimit { get; set; }
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;
        public int MaxInFlight;

        public List<string> Requests { get; } = [];
        public List<string> Accepts { get; } = [];
        public List<string> TokenQueries { get; } = [];

        public FakeRegistry()
        {
            _listener = LoopbackListener.Start(out var port);
            Port = port;
            _ = Task.Run(Loop);
        }

        public static byte[] Manifest(string repo) => Encoding.UTF8.GetBytes($$"""{"schemaVersion":2,"repo":"{{repo}}"}""");

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
                _ = Task.Run(() => HandleAsync(context));
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            var now = Interlocked.Increment(ref _inFlight);
            InterlockedMax(ref MaxInFlight, now);
            try
            {
                if (Delay > TimeSpan.Zero)
                    await Task.Delay(Delay);

                var request = context.Request;
                var path = request.Url?.AbsolutePath ?? "";
                lock (Requests)
                    Requests.Add($"{request.HttpMethod} {path}");

                var response = context.Response;
                if (RateLimit)
                {
                    response.StatusCode = 429;
                    response.AddHeader("Retry-After", "120");
                    response.Close();
                    return;
                }

                if (path == "/token")
                {
                    lock (TokenQueries)
                        TokenQueries.Add(request.Url!.Query);
                    var scope = request.QueryString["scope"] ?? "";
                    if (Private.Any(p => scope.Contains(p, StringComparison.Ordinal)))
                    {
                        response.StatusCode = 401;
                        response.Close();
                        return;
                    }
                    await WriteAsync(response, """{"token":"t0k"}""");
                    return;
                }

                if (path.StartsWith("/v2/repositories/", StringComparison.Ordinal))
                {
                    // Docker Hub's tag API: /v2/repositories/<ns>/<name>/tags/<tag>
                    var parts = path["/v2/repositories/".Length..].Split("/tags/");
                    if (!Digests.TryGetValue($"{parts[0]}:{parts[1]}", out var hubDigest))
                    {
                        response.StatusCode = 404;
                        response.Close();
                        return;
                    }
                    await WriteAsync(response, JsonSerializer.Serialize(new
                    {
                        name = parts[1],
                        digest = hubDigest,
                        tag_last_pushed = "2026-09-20T08:15:30.123456789Z",
                        images = new[] { new { digest = Platform, architecture = "amd64" } },
                    }));
                    return;
                }

                if (path.StartsWith("/v2/moved/", StringComparison.Ordinal))
                {
                    // What lscr.io does: send every request on to ghcr.io.
                    response.StatusCode = 307;
                    response.AddHeader("Location", $"http://127.0.0.1:{Port}/v2/{path["/v2/moved/".Length..]}");
                    response.Close();
                    return;
                }

                if (path.StartsWith("/v2/", StringComparison.Ordinal) && path.Contains("/manifests/"))
                {
                    lock (Accepts)
                        Accepts.Add(request.Headers["Accept"] ?? "");

                    var repo = path["/v2/".Length..path.IndexOf("/manifests/", StringComparison.Ordinal)];
                    var tag = path[(path.IndexOf("/manifests/", StringComparison.Ordinal) + "/manifests/".Length)..];

                    if (request.Headers["Authorization"] != "Bearer t0k")
                    {
                        response.StatusCode = 401;
                        response.AddHeader("WWW-Authenticate",
                            $"Bearer realm=\"http://127.0.0.1:{Port}/token\",service=\"fake-registry\",scope=\"repository:{repo}:pull\"");
                        response.Close();
                        return;
                    }

                    if (!Digests.TryGetValue($"{repo}:{tag}", out var digest))
                    {
                        response.StatusCode = 404;
                        response.Close();
                        return;
                    }

                    response.ContentType = "application/vnd.oci.image.index.v1+json";
                    if (SendDigestHeader)
                        response.AddHeader("Docker-Content-Digest", digest);

                    var body = Manifest(repo);
                    response.ContentLength64 = body.Length;
                    if (request.HttpMethod == "GET")
                        await response.OutputStream.WriteAsync(body);
                    response.Close();
                    return;
                }

                response.StatusCode = 404;
                response.Close();
            }
            catch (Exception)
            {
                // A client that gave up; nothing to tell anybody.
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int seen;
            while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
            {
            }
        }

        private static async Task WriteAsync(HttpListenerResponse response, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
            response.Close();
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

    /// <summary>A Docker Engine with a container list and the image inspects behind it.</summary>
    private sealed class FakeDocker : IDisposable
    {
        private readonly HttpListener _listener;

        public int Port { get; }
        public string Endpoint => $"tcp://127.0.0.1:{Port}";
        public string ContainersJson { get; set; } = "[]";

        /// <summary>Image id → its inspect payload.</summary>
        public Dictionary<string, string> Images { get; } = new(StringComparer.Ordinal);

        public List<string> Paths { get; } = [];

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

                var path = Uri.UnescapeDataString(context.Request.Url?.AbsolutePath ?? "");
                lock (Paths)
                    Paths.Add($"{context.Request.HttpMethod} {path}");

                string? body = null;
                if (path.EndsWith("/containers/json", StringComparison.Ordinal))
                    body = ContainersJson;
                else if (path.Contains("/images/", StringComparison.Ordinal) && path.EndsWith("/json", StringComparison.Ordinal))
                {
                    var id = path[(path.IndexOf("/images/", StringComparison.Ordinal) + "/images/".Length)..^"/json".Length];
                    Images.TryGetValue(id, out body);
                }

                if (body is null)
                {
                    var missing = Encoding.UTF8.GetBytes("""{"message":"No such image"}""");
                    context.Response.StatusCode = 404;
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = missing.Length;
                    await context.Response.OutputStream.WriteAsync(missing);
                    context.Response.Close();
                    continue;
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

    private readonly FakeRegistry _registry = new();
    private readonly FakeDocker _docker = new();
    private readonly ServiceProvider _http;
    private readonly ImageRegistry _client;

    public ContainerUpdateTests()
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient();
        _http = services.BuildServiceProvider();
        _client = new ImageRegistry(_http.GetRequiredService<IHttpClientFactory>(), NullLogger<ImageRegistry>.Instance)
        {
            HubApi = $"http://127.0.0.1:{_registry.Port}",
        };
    }

    public void Dispose()
    {
        _registry.Dispose();
        _docker.Dispose();
        _http.Dispose();
    }

    private ImageRef Local(string repoAndTag) => ImageRef.Parse($"{_registry.Host}/{repoAndTag}");

    // ---- the registry ---------------------------------------------------------------

    [Fact]
    public async Task A_registry_is_asked_with_an_anonymous_token_for_the_index_digest()
    {
        _registry.Digests["team/app:1.0"] = Index;

        var published = await _client.LatestAsync(Local("team/app:1.0"));

        Assert.Equal(Index, published.Digest);
        Assert.Null(published.Problem);

        // The token service named in WWW-Authenticate, asked for a pull of just that repo.
        var query = Uri.UnescapeDataString(Assert.Single(_registry.TokenQueries));
        Assert.Contains("service=fake-registry", query);
        Assert.Contains("scope=repository:team/app:pull", query);

        // The index types asked for first: for a multi-arch image that is the digest RepoDigests
        // records, and a platform's manifest digest would never match it.
        var accept = _registry.Accepts.Last();
        Assert.StartsWith("application/vnd.oci.image.index.v1+json", accept);
        Assert.Contains("application/vnd.docker.distribution.manifest.list.v2+json", accept);

        // HEAD, not GET: nothing is downloaded, and Docker Hub does not count a HEAD as a pull.
        Assert.DoesNotContain(_registry.Requests, r => r.StartsWith("GET /v2/team", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_registry_that_redirects_is_given_the_token_where_it_redirected_to()
    {
        // A redirect drops the Authorization header, so sending the token round it again
        // would be a 401 every time — and every lscr.io image "private".
        _registry.Digests["team/app:1.0"] = Index;

        var published = await _client.LatestAsync(Local("moved/team/app:1.0"));

        Assert.Equal(Index, published.Digest);
    }

    [Fact]
    public async Task Docker_hub_answers_through_its_tag_api_with_the_date_it_was_pushed()
    {
        _registry.Digests["library/postgres:16"] = Index;

        var published = await _client.LatestAsync(ImageRef.Parse("postgres:16"));

        Assert.Equal(Index, published.Digest);
        Assert.Equal("2026-09-20T08:15:30", published.PushedAt?.UtcDateTime.ToString("s"));
        Assert.Contains("GET /v2/repositories/library/postgres/tags/16", _registry.Requests);
    }

    [Fact]
    public async Task A_registry_that_leaves_the_digest_header_off_is_hashed_instead()
    {
        _registry.Digests["team/app:latest"] = Index;
        _registry.SendDigestHeader = false;

        var published = await _client.LatestAsync(Local("team/app"));

        var expected = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(FakeRegistry.Manifest("team/app")));
        Assert.Equal(expected, published.Digest);
    }

    [Fact]
    public async Task A_private_image_is_said_to_be_private_rather_than_behind()
    {
        _registry.Private.Add("team/secret");

        var published = await _client.LatestAsync(Local("team/secret:1"));

        Assert.Null(published.Digest);
        Assert.Contains("private", published.Problem);
    }

    [Fact]
    public async Task A_missing_tag_says_so()
    {
        var published = await _client.LatestAsync(Local("team/app:nope"));

        Assert.Null(published.Digest);
        Assert.Contains("no tag “nope”", published.Problem);
    }

    [Fact]
    public async Task A_rate_limited_registry_is_left_alone_until_it_says_to_come_back()
    {
        _registry.RateLimit = true;

        var first = await _client.LatestAsync(Local("team/one:1"));
        var asked = _registry.Requests.Count;
        var second = await _client.LatestAsync(Local("team/two:1"));

        Assert.Contains("rate-limiting", first.Problem);
        Assert.Contains("rate-limiting", second.Problem);

        // The second image on the same host was never asked about.
        Assert.Equal(asked, _registry.Requests.Count);
    }

    [Fact]
    public async Task Only_a_few_calls_go_to_one_registry_at_once()
    {
        for (var i = 0; i < 8; i++)
            _registry.Digests[$"team/app{i}:latest"] = Index;
        _registry.Delay = TimeSpan.FromMilliseconds(150);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => _client.LatestAsync(Local($"team/app{i}"))));

        Assert.InRange(_registry.MaxInFlight, 1, ImageRegistry.PerHostConcurrency);
    }

    [Fact]
    public async Task An_answer_is_reused_rather_than_asked_for_again()
    {
        _registry.Digests["team/app:1"] = Index;

        await _client.LatestAsync(Local("team/app:1"));
        var asked = _registry.Requests.Count;
        await _client.LatestAsync(Local("team/app:1"));

        Assert.Equal(asked, _registry.Requests.Count);
    }

    [Theory]
    [InlineData("Bearer realm=\"https://ghcr.io/token\",service=\"ghcr.io\",scope=\"repository:a/b:pull\"",
        "Bearer", "https://ghcr.io/token", "ghcr.io", "repository:a/b:pull")]
    [InlineData("Bearer realm=\"https://auth.docker.io/token\",service=\"registry.docker.io\"",
        "Bearer", "https://auth.docker.io/token", "registry.docker.io", null)]
    [InlineData("Bearer realm=\"https://q/t\",scope=\"repository:a:pull,push\"", "Bearer", "https://q/t", null, "repository:a:pull,push")]
    public void A_challenge_is_read_into_its_parts(string header, string scheme, string realm, string? service, string? scope)
    {
        var challenge = ImageRegistry.ParseChallenge(header)!;

        Assert.Equal(scheme, challenge.Scheme);
        Assert.Equal(realm, challenge.Parameters["realm"]);
        Assert.Equal(service, challenge.Parameters.TryGetValue("service", out var s) ? s : null);
        Assert.Equal(scope, challenge.Parameters.TryGetValue("scope", out var sc) ? sc : null);
    }

    [Theory]
    [InlineData("localhost:5000", "http://localhost:5000")]
    [InlineData("127.0.0.1:5000", "http://127.0.0.1:5000")]
    [InlineData("ghcr.io", "https://ghcr.io")]
    [InlineData("registry.home.lan:5000", "https://registry.home.lan:5000")]
    public void Plain_http_is_only_for_a_registry_on_this_machine(string host, string expected) =>
        Assert.Equal(expected, ImageRegistry.BaseUrl(host));

    [Theory]
    [InlineData("nginx", "registry-1.docker.io", "library/nginx")]
    [InlineData("lscr.io/linuxserver/sonarr:latest", "lscr.io", "linuxserver/sonarr")]
    [InlineData("ghcr.io/home-assistant/home-assistant:stable", "ghcr.io", "home-assistant/home-assistant")]
    public void Each_reference_knows_which_registry_api_to_ask(string reference, string host, string repository)
    {
        var image = ImageRef.Parse(reference);
        Assert.Equal(host, image.RegistryHost);
        Assert.Equal(repository, image.RegistryRepository);
    }

    // ---- classifying ----------------------------------------------------------------

    private static ContainerRow Row(string name, string image, Dictionary<string, string>? labels = null) =>
        new(name + "-0123456789abcdef", name, image, "sha256:img-" + name, "running", "Up 2 days",
            DateTimeOffset.UnixEpoch, [], labels ?? new Dictionary<string, string>());

    private static readonly DateTimeOffset Built = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private static ContainerUpdates.LocalImage Pulled(string reference, params string[] digests) =>
        new(reference, new ImageFacts(Built, [.. digests.Select(d => $"{ImageRef.Parse(reference).Repository}@{d}")]), null);

    private static ImageRegistry.Published Says(string digest) => new(digest, Built.AddDays(100), null, DateTimeOffset.UtcNow);

    [Fact]
    public void A_multi_arch_image_matching_the_index_is_current()
    {
        var update = ContainerUpdates.Classify(Row("sonarr", "lscr.io/linuxserver/sonarr"),
            Pulled("lscr.io/linuxserver/sonarr", Index), Says(Index));

        Assert.Equal(UpdateState.Current, update.State);
        Assert.Equal(Built, update.ImageCreated);
    }

    [Fact]
    public void A_different_digest_is_behind_and_says_when_the_new_one_was_published()
    {
        var update = ContainerUpdates.Classify(Row("sonarr", "lscr.io/linuxserver/sonarr"),
            Pulled("lscr.io/linuxserver/sonarr", Index), Says(Newer));

        Assert.Equal(UpdateState.Behind, update.State);
        Assert.True(update.CanUpdate);
        Assert.Equal(Newer, update.Latest);
        Assert.Equal(Built.AddDays(100), update.Published);
    }

    [Fact]
    public void Any_of_several_repo_digests_counts()
    {
        // The same image pulled under two names has a RepoDigest for each; content addresses
        // do not care which repository they came through.
        var local = new ContainerUpdates.LocalImage("ghcr.io/x/app",
            new ImageFacts(Built, [$"x/app@{Platform}", $"ghcr.io/x/app@{Index}"]), null);

        Assert.Equal(UpdateState.Current, ContainerUpdates.Classify(Row("app", "ghcr.io/x/app"), local, Says(Index)).State);
    }

    [Fact]
    public void A_local_build_is_said_to_be_one_and_never_asked_about()
    {
        var local = new ContainerUpdates.LocalImage("labbytwo-labbytwo", new ImageFacts(Built, []), null);

        var update = ContainerUpdates.Classify(Row("labbytwo", "labbytwo-labbytwo"), local, null);

        Assert.Equal(UpdateState.LocalBuild, update.State);
        Assert.Contains("Built on this host", update.Note);
        Assert.False(update.CanUpdate);
    }

    [Fact]
    public void A_container_pinned_to_a_digest_has_nothing_newer()
    {
        var reference = $"postgres@{Index}";
        var update = ContainerUpdates.Classify(Row("db", reference), Pulled(reference, Index), null);

        Assert.Equal(UpdateState.Pinned, update.State);
    }

    [Fact]
    public void A_container_watchtower_is_told_to_leave_alone_is_excluded_even_when_behind()
    {
        var labels = new Dictionary<string, string> { [ContainerUpdates.ExcludeLabel] = "false" };
        var update = ContainerUpdates.Classify(Row("db", "postgres:16", labels), Pulled("postgres:16", Index), Says(Newer));

        Assert.True(update.IsBehind);
        Assert.True(update.Excluded);
        Assert.False(update.CanUpdate);
    }

    [Fact]
    public void Before_a_check_nothing_is_called_unknown()
    {
        var update = ContainerUpdates.Classify(Row("sonarr", "lscr.io/linuxserver/sonarr"),
            Pulled("lscr.io/linuxserver/sonarr", Index), null, checkedRegistry: false);

        Assert.Equal(UpdateState.Unchecked, update.State);
        Assert.Equal(Built, update.ImageCreated);
    }

    [Fact]
    public void A_registry_that_would_not_say_leaves_it_unknown_with_why()
    {
        var update = ContainerUpdates.Classify(Row("app", "ghcr.io/x/app"), Pulled("ghcr.io/x/app", Index),
            new ImageRegistry.Published(null, null, "ghcr.io wants a login", DateTimeOffset.UtcNow));

        Assert.Equal(UpdateState.Unknown, update.State);
        Assert.Equal("ghcr.io wants a login", update.Note);
    }

    [Theory]
    [InlineData("lscr.io/linuxserver/sonarr:latest", "lscr.io/linuxserver/sonarr")]
    [InlineData("postgres:16", "postgres")]
    [InlineData("nginx", "nginx")]
    public void Watchtower_is_given_the_image_name_it_compares_against(string reference, string expected) =>
        Assert.Equal(expected, new ContainerUpdate("id", "n", reference, UpdateState.Behind).WatchtowerImage);

    [Theory]
    [InlineData(0.5, "less than a day")]
    [InlineData(1.5, "1 day")]
    [InlineData(12, "12 days")]
    [InlineData(95, "3 months")]
    [InlineData(800, "2 years")]
    public void Ages_are_said_in_the_unit_software_is_thought_of_in(double days, string expected)
    {
        var now = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, ContainerUpdates.Age(now.AddDays(-days), now));
    }

    // ---- the whole review -----------------------------------------------------------

    private void Lab()
    {
        _registry.Digests["team/app:1.0"] = Newer;
        _registry.Digests["team/tool:latest"] = Index;

        _docker.ContainersJson = JsonSerializer.Serialize(new object[]
        {
            new { Id = "aaa0123456789abcdef", Names = new[] { "/app" }, Image = $"{_registry.Host}/team/app:1.0", ImageID = "sha256:app", State = "running", Status = "Up 1 day" },
            new { Id = "bbb0123456789abcdef", Names = new[] { "/app-worker" }, Image = $"{_registry.Host}/team/app:1.0", ImageID = "sha256:app", State = "running", Status = "Up 1 day" },
            new { Id = "ccc0123456789abcdef", Names = new[] { "/tool" }, Image = $"{_registry.Host}/team/tool", ImageID = "sha256:tool", State = "running", Status = "Up 1 day" },
            new { Id = "ddd0123456789abcdef", Names = new[] { "/homemade" }, Image = "homemade", ImageID = "sha256:homemade", State = "exited", Status = "Exited (0) 1 day ago" },
        });
        _docker.Images["sha256:app"] = JsonSerializer.Serialize(new { Created = "2026-01-02T03:04:05.123456789Z", RepoDigests = new[] { $"{_registry.Host}/team/app@{Index}" } });
        _docker.Images["sha256:tool"] = JsonSerializer.Serialize(new { Created = "2026-06-01T00:00:00Z", RepoDigests = new[] { $"{_registry.Host}/team/tool@{Index}" } });
        _docker.Images["sha256:homemade"] = JsonSerializer.Serialize(new { Created = "2026-09-01T00:00:00Z", RepoDigests = Array.Empty<string>() });
    }

    [Fact]
    public async Task A_check_compares_every_container_and_asks_once_per_image()
    {
        Lab();

        var review = await ContainerUpdates.ReviewAsync("docker", "NAS", _docker.Endpoint, TimeSpan.FromSeconds(5), _client, default);

        Assert.Null(review.Error);
        Assert.Equal(["app", "app-worker"], review.Behind.Select(b => b.Name));
        Assert.Equal(UpdateState.Current, review.Containers.Single(c => c.Name == "tool").State);
        Assert.Equal(UpdateState.LocalBuild, review.Containers.Single(c => c.Name == "homemade").State);
        Assert.Equal("2026-01-02T03:04:05", review.Containers.Single(c => c.Name == "app").ImageCreated?.UtcDateTime.ToString("s"));

        // Two containers on one tag are one question; a local build is none.
        Assert.Single(_registry.TokenQueries, q => Uri.UnescapeDataString(q).Contains("repository:team/app:pull"));
        Assert.DoesNotContain(_registry.Requests, r => r.Contains("homemade", StringComparison.Ordinal));
        Assert.Equal("2 behind, 1 up to date, 1 built here", review.Summary);
    }

    [Fact]
    public async Task Without_a_registry_only_docker_is_asked()
    {
        Lab();

        var review = await ContainerUpdates.ReviewAsync("docker", "NAS", _docker.Endpoint, TimeSpan.FromSeconds(5), null, default);

        // The local half is what a page may do whenever it likes: ages and local builds,
        // no verdicts, and not one request to a registry.
        Assert.Empty(_registry.Requests);
        Assert.All(review.Containers.Where(c => c.Name != "homemade"), c => Assert.Equal(UpdateState.Unchecked, c.State));
        Assert.All(review.Containers, c => Assert.NotNull(c.ImageCreated));
    }

    [Fact]
    public async Task A_proxy_that_refuses_images_names_its_flag()
    {
        Lab();
        _docker.Images.Clear();
        using var proxy = new ForbiddingProxy();

        var review = await ContainerUpdates.ReviewAsync("docker", "NAS", proxy.Endpoint, TimeSpan.FromSeconds(5), _client, default);

        Assert.Contains("IMAGES=1", review.Error);
    }

    /// <summary>A socket proxy with CONTAINERS=1 and not IMAGES=1: an empty-ish list, and 403 for images.</summary>
    private sealed class ForbiddingProxy : IDisposable
    {
        private readonly HttpListener _listener;
        public string Endpoint { get; }

        public ForbiddingProxy()
        {
            _listener = LoopbackListener.Start(out var port);
            Endpoint = $"tcp://127.0.0.1:{port}";
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

                    var path = context.Request.Url?.AbsolutePath ?? "";
                    var images = path.Contains("/images/", StringComparison.Ordinal);
                    var bytes = Encoding.UTF8.GetBytes(images
                        ? "<html><body><h1>403 Forbidden</h1>\nRequest forbidden by administrative rules.\n</body></html>\n"
                        : """[{"Id":"aaa0123456789abcdef","Names":["/app"],"Image":"nginx","ImageID":"sha256:x","State":"running","Status":"Up"}]""");
                    context.Response.StatusCode = images ? 403 : 200;
                    context.Response.ContentType = images ? "text/html" : "application/json";
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

    // ---- safety -----------------------------------------------------------------------

    [Fact]
    public void Updating_all_leaves_labbytwo_protected_and_excluded_containers_alone()
    {
        var self = Row("labbytwo", "fennch/labbytwo");
        var tunnel = Row("cloudflared", "cloudflare/cloudflared");
        var db = Row("db", "postgres:16", new Dictionary<string, string> { [ContainerUpdates.ExcludeLabel] = "false" });
        var sonarr = Row("sonarr", "lscr.io/linuxserver/sonarr");

        (ContainerRow, ContainerUpdate) Behind(ContainerRow row) =>
            (row, new ContainerUpdate(row.Id, row.Name, row.Image, UpdateState.Behind, Excluded: ContainerUpdates.IsExcluded(row.Labels)));

        var (act, skip) = ContainerSafety.PlanUpdates([Behind(self), Behind(tunnel), Behind(db), Behind(sonarr)],
            self.Id[..12], ["cloudflared"]);

        Assert.Equal(["sonarr"], act.Select(a => a.Name));
        Assert.Contains(skip, s => s.Container.Name == "labbytwo" && s.Why.Contains("LabbyTwo itself"));
        Assert.Contains(skip, s => s.Container.Name == "cloudflared" && s.Why.Contains("protected"));
        Assert.Contains(skip, s => s.Container.Name == "db" && s.Why.Contains("watchtower.enable=false"));
    }

    [Fact]
    public void Updating_labbytwo_warns_like_a_restart_and_a_protected_one_needs_its_name()
    {
        var self = Row("labbytwo", "fennch/labbytwo");

        Assert.Contains("reconnect", ContainerSafety.UpdateWarning(self, true, false, SelfUpdater.UpdateMode.DockerApi));
        Assert.False(ContainerSafety.NeedsTypedNameToUpdate(self: true, listed: false));
        Assert.True(ContainerSafety.NeedsTypedNameToUpdate(self: false, listed: true));
        Assert.False(ContainerSafety.NeedsTypedNameToUpdate(self: false, listed: false));
        Assert.Contains("protected list",
            ContainerSafety.UpdateWarning(Row("cloudflared", "cloudflare/cloudflared"), false, true, SelfUpdater.UpdateMode.WatchtowerApi));
    }
}
