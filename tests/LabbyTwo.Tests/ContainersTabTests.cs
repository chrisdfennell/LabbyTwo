using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using LabbyTwo.Components.Pages.Kinds;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace LabbyTwo.Tests;

/// <summary>
/// A Docker Engine that answers from a script, over real HTTP — the same stand-in the
/// self-updater's tests use, grown a handler so each test decides what Docker says. Tracks
/// how many requests are in flight at once, which is how the stats gate is tested.
/// </summary>
internal sealed class ScriptedDocker : IDisposable
{
    private readonly HttpListener _listener;
    private int _inFlight;

    public ScriptedDocker(Func<HttpListenerContext, Task> handle)
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

                _ = Task.Run(async () =>
                {
                    var now = Interlocked.Increment(ref _inFlight);
                    lock (Requests)
                    {
                        Requests.Add($"{context.Request.HttpMethod} {context.Request.Url!.PathAndQuery}");
                        MaxInFlight = Math.Max(MaxInFlight, now);
                    }
                    try
                    {
                        await handle(context);
                    }
                    catch (Exception)
                    {
                        // The client went away; nothing to answer.
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _inFlight);
                        try
                        {
                            context.Response.Close();
                        }
                        catch (Exception)
                        {
                        }
                    }
                });
            }
        });
    }

    public string Endpoint { get; }
    public List<string> Requests { get; } = [];
    public int MaxInFlight { get; private set; }

    public static async Task Json(HttpListenerContext context, object body, int status = 200)
    {
        var bytes = Encoding.UTF8.GetBytes(body as string ?? JsonSerializer.Serialize(body));
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
    }

    /// <summary>What both socket proxies send: HAProxy's HTML page, not Docker's JSON.</summary>
    public static async Task Forbidden(HttpListenerContext context)
    {
        var page = Encoding.UTF8.GetBytes(
            "<html><body><h1>403 Forbidden</h1>\nRequest forbidden by administrative rules.\n</body></html>\n");
        context.Response.StatusCode = 403;
        context.Response.ContentType = "text/html";
        context.Response.ContentLength64 = page.Length;
        await context.Response.OutputStream.WriteAsync(page);
    }

    public static Task NoSuchContainer(HttpListenerContext context) =>
        Json(context, new { message = "No such container: x" }, 404);

    public void Dispose()
    {
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (Exception)
        {
        }
    }
}

/// <summary>
/// The log reader. Docker multiplexes stdout and stderr behind eight-byte headers unless the
/// container has a TTY, and network reads split headers, frames, lines and characters
/// anywhere they like — each of which is a way to show garbage or swallow a line.
/// </summary>
public class DockerLogReaderTests
{
    private static byte[] Frame(int stream, string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var frame = new byte[8 + payload.Length];
        frame[0] = (byte)stream;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(4), payload.Length);
        payload.CopyTo(frame, 8);
        return frame;
    }

    [Fact]
    public void FramesAreSplitIntoLinesByStream()
    {
        var reader = new DockerLogReader(tty: false, timestamps: false);
        var bytes = Frame(1, "hello\nworld\n").Concat(Frame(2, "oops\n")).ToArray();

        var lines = reader.Feed(bytes);

        Assert.Equal(["hello", "world", "oops"], lines.Select(l => l.Text));
        Assert.Equal([LogStream.Stdout, LogStream.Stdout, LogStream.Stderr], lines.Select(l => l.Stream));
    }

    [Fact]
    public void AHeaderSplitAcrossReadsIsReassembled()
    {
        var reader = new DockerLogReader(tty: false, timestamps: false);
        var bytes = Frame(1, "first\n").Concat(Frame(2, "second\n")).ToArray();

        // One byte at a time: every header and every line is split as badly as it can be.
        var lines = new List<LogLine>();
        foreach (var b in bytes)
            lines.AddRange(reader.Feed([b]));

        Assert.Equal(["first", "second"], lines.Select(l => l.Text));
        Assert.Equal(LogStream.Stderr, lines[1].Stream);
    }

    [Fact]
    public void ALineSpanningTwoFramesIsOneLine()
    {
        // Docker flushes on its own schedule, so one line can come in two frames.
        var reader = new DockerLogReader(tty: false, timestamps: false);
        var lines = reader.Feed(Frame(1, "half a ").Concat(Frame(1, "line\n")).ToArray());

        Assert.Equal("half a line", Assert.Single(lines).Text);
    }

    [Fact]
    public void AMultiByteCharacterSplitAcrossReadsSurvives()
    {
        var reader = new DockerLogReader(tty: false, timestamps: false);
        var frame = Frame(1, "café ☕\n");
        var split = frame.Length - 3; // inside the three bytes of the cup

        var lines = reader.Feed(frame.AsSpan(0, split)).Concat(reader.Feed(frame.AsSpan(split))).ToList();

        Assert.Equal("café ☕", Assert.Single(lines).Text);
    }

    [Fact]
    public void ATtyStreamIsReadAsItIs()
    {
        // With a TTY there are no headers — and a reader expecting them would take "hell" as
        // a length. The bytes are the text.
        var reader = new DockerLogReader(tty: true, timestamps: false);
        var lines = reader.Feed(Encoding.UTF8.GetBytes("hello\r\nthere\n"));

        Assert.Equal(["hello", "there"], lines.Select(l => l.Text));
        Assert.All(lines, l => Assert.Equal(LogStream.Stdout, l.Stream));
    }

    [Fact]
    public void TimestampsAreTakenOffTheFront()
    {
        var reader = new DockerLogReader(tty: true);
        var line = Assert.Single(reader.Feed(Encoding.UTF8.GetBytes("2026-09-28T10:11:12.123456789Z started up\n")));

        Assert.Equal("started up", line.Text);
        Assert.Equal(new DateTimeOffset(2026, 9, 28, 10, 11, 12, 123, TimeSpan.Zero).AddTicks(4567), line.Time);
    }

    [Fact]
    public void TheLastLineWithoutANewlineComesOutOnFlush()
    {
        var reader = new DockerLogReader(tty: false, timestamps: false);
        Assert.Empty(reader.Feed(Frame(2, "panic: the end")));

        Assert.Equal("panic: the end", Assert.Single(reader.Flush()).Text);
    }

    [Fact]
    public void AnEndlessLineIsCut()
    {
        var reader = new DockerLogReader(tty: true, timestamps: false);
        reader.Feed(Encoding.UTF8.GetBytes(new string('x', DockerLogReader.MaxLineBytes * 3)));
        var line = Assert.Single(reader.Feed("\n"u8));

        Assert.Equal(DockerLogReader.MaxLineBytes, line.Text.Length);
    }

    [Fact]
    public void TheBufferKeepsTheNewestLines()
    {
        var buffer = new LogBuffer(capacity: 3);
        buffer.Add(Enumerable.Range(1, 5).Select(i => new LogLine(null, i.ToString(), LogStream.Stdout)));

        Assert.Equal(["3", "4", "5"], buffer.Snapshot().Select(l => l.Text));
        Assert.Equal(2, buffer.Dropped);
    }

    [Fact]
    public async Task TailReadsAFramedResponseOverHttp()
    {
        using var docker = new ScriptedDocker(async context =>
        {
            var body = Frame(1, "2026-01-01T00:00:00Z one\n").Concat(Frame(2, "2026-01-01T00:00:01Z two\n")).ToArray();
            context.Response.ContentType = "application/vnd.docker.multiplexed-stream";
            await context.Response.OutputStream.WriteAsync(body);
        });

        var lines = await DockerLogs.TailAsync(docker.Endpoint, TimeSpan.FromSeconds(5), "abc", tty: false, 200, default);

        Assert.Equal(["one", "two"], lines.Select(l => l.Text));
        Assert.Contains("GET /v1.41/containers/abc/logs?stdout=1&stderr=1&timestamps=1&tail=200", docker.Requests);
    }

    [Fact]
    public async Task FollowStreamsUntilCancelled()
    {
        var release = new TaskCompletionSource();
        using var docker = new ScriptedDocker(async context =>
        {
            context.Response.SendChunked = true;
            await context.Response.OutputStream.WriteAsync(Frame(1, "early\n"));
            await context.Response.OutputStream.FlushAsync();
            await release.Task;
        });

        using var cancel = new CancellationTokenSource();
        var seen = new List<string>();
        var got = new TaskCompletionSource();
        var follow = DockerLogs.FollowAsync(docker.Endpoint, TimeSpan.FromSeconds(5), "abc", false, 10, lines =>
        {
            lock (seen)
                seen.AddRange(lines.Select(l => l.Text));
            got.TrySetResult();
        }, cancel.Token);

        await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => follow.WaitAsync(TimeSpan.FromSeconds(10)));
        release.TrySetResult();

        Assert.Equal(["early"], seen);
        Assert.Contains(docker.Requests, r => r.Contains("follow=1"));
    }
}

/// <summary>The numbers in the CPU and memory columns, done the way `docker stats` does them.</summary>
public class ContainerStatsTests
{
    [Fact]
    public void CpuIsTheShareOfHostTimeScaledToCores()
    {
        // 0.5 s of container CPU over 2 s of host time on 4 CPUs: a quarter of the box, one core.
        var cpu = DockerContainers.CpuPercent(1_500_000_000, 1_000_000_000, 12_000_000_000, 10_000_000_000, 4);

        Assert.Equal(100.0, cpu!.Value, 6);
    }

    [Fact]
    public void NoDifferenceMeansNoReading()
    {
        Assert.Null(DockerContainers.CpuPercent(5, 5, 10, 10, 4));
        // A restart resets the container's counter; a negative delta is not a reading.
        Assert.Null(DockerContainers.CpuPercent(1, 5, 20, 10, 4));
    }

    private static string Stats(
        ulong cpu, ulong system, ulong preCpu = 0, ulong preSystem = 0, int online = 2,
        string read = "2026-09-28T10:00:00Z", ulong rx = 1000, ulong tx = 500, object? memoryStats = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["read"] = read,
            ["cpu_stats"] = new { cpu_usage = new { total_usage = cpu }, system_cpu_usage = system, online_cpus = online },
            ["precpu_stats"] = new { cpu_usage = new { total_usage = preCpu }, system_cpu_usage = preSystem },
            ["memory_stats"] = new { usage = 300UL * 1024 * 1024, limit = 1024UL * 1024 * 1024, stats = memoryStats },
            ["networks"] = new
            {
                eth0 = new { rx_bytes = rx, tx_bytes = tx },
                eth1 = new { rx_bytes = rx, tx_bytes = tx },
            },
        });

    [Fact]
    public void DockersOwnPreviousSampleIsUsedWhenItHasOne()
    {
        var sample = DockerContainers.ParseSample(Stats(2_000, 20_000, preCpu: 1_000, preSystem: 10_000));
        var stats = DockerContainers.Compute(sample, previous: null);

        Assert.Equal(20.0, stats.CpuPercent!.Value, 6); // 1000/10000 * 2 * 100
        Assert.Equal(2000UL, stats.RxBytes); // two networks summed
        Assert.Equal(1000UL, stats.TxBytes);
    }

    [Fact]
    public void AOneShotSampleIsDifferencedAgainstTheLastOne()
    {
        var first = DockerContainers.ParseSample(Stats(1_000, 10_000, read: "2026-09-28T10:00:00Z", rx: 0, tx: 0));
        var second = DockerContainers.ParseSample(Stats(4_000, 40_000, read: "2026-09-28T10:00:10Z", rx: 10_000, tx: 5_000));

        Assert.Null(DockerContainers.Compute(first, null).CpuPercent);

        var stats = DockerContainers.Compute(second, first);
        Assert.Equal(20.0, stats.CpuPercent!.Value, 6); // 3000/30000 * 2 * 100
        Assert.Equal(2000.0, stats.RxPerSecond!.Value, 6); // 20 000 bytes over 10 s
    }

    [Fact]
    public void PageCacheIsNotCountedAsMemoryUse()
    {
        var v2 = DockerContainers.ParseSample(Stats(1, 1, memoryStats: new { inactive_file = 100UL * 1024 * 1024 }));
        var v1 = DockerContainers.ParseSample(Stats(1, 1, memoryStats: new { total_inactive_file = 50UL * 1024 * 1024 }));

        Assert.Equal(200UL * 1024 * 1024, v2.MemoryUsed);
        Assert.Equal(250UL * 1024 * 1024, v1.MemoryUsed);
        Assert.Equal(1024UL * 1024 * 1024, v2.MemoryLimit);
    }

    [Fact]
    public async Task StatsAreFetchedAFewAtATimeAndThenOneShot()
    {
        using var docker = new ScriptedDocker(async context =>
        {
            // Slow enough that an ungated client would have all of them open at once.
            await Task.Delay(150);
            await ScriptedDocker.Json(context, Stats(1_000, 10_000, 500, 5_000));
        });

        var ids = Enumerable.Range(0, 12).Select(i => $"c{i}").ToList();
        var first = await DockerContainers.StatsAsync(docker.Endpoint, TimeSpan.FromSeconds(10), ids, default);

        Assert.Equal(12, first.Count);
        Assert.InRange(docker.MaxInFlight, 1, DockerContainers.StatsConcurrency);
        Assert.All(docker.Requests, r => Assert.DoesNotContain("one-shot", r));

        // Within the reuse window a second viewer is served from the first one's answers.
        var again = await DockerContainers.StatsAsync(docker.Endpoint, TimeSpan.FromSeconds(10), ids, default);
        Assert.Equal(12, again.Count);
        Assert.Equal(12, docker.Requests.Count);
    }

    [Fact]
    public void BytesReadLikeDockerStats()
    {
        Assert.Equal("512 B", DockerContainers.Bytes(512));
        Assert.Equal("1.5 KiB", DockerContainers.Bytes(1536));
        Assert.Equal("300 MiB", DockerContainers.Bytes(300 * 1024 * 1024));
    }
}

/// <summary>The list: parsing it, grouping it by Compose project, filtering and sorting it.</summary>
public class ContainerListTests
{
    internal static ContainerRow Row(
        string name, string state = "running", string status = "Up 2 hours", string? project = null,
        string? service = null, string id = "", string image = "nginx:latest", string imageId = "sha256:aaa")
    {
        var labels = new Dictionary<string, string>();
        if (project is not null)
            labels[ContainerRow.ProjectLabel] = project;
        if (service is not null)
            labels[ContainerRow.ServiceLabel] = service;
        return new ContainerRow(id.Length > 0 ? id : name + "0123456789abcdef", name, image, imageId, state, status,
            DateTimeOffset.UnixEpoch, [], labels);
    }

    [Fact]
    public void TheListIsParsedWithPortsLabelsAndHealth()
    {
        const string payload = """
            [{"Id":"abc123def4567890","Names":["/sonarr"],"Image":"lscr.io/linuxserver/sonarr:latest","ImageID":"sha256:111",
              "State":"running","Status":"Up 3 hours (healthy)","Created":1700000000,
              "Ports":[{"IP":"0.0.0.0","PrivatePort":8989,"PublicPort":8989,"Type":"tcp"},
                       {"IP":"::","PrivatePort":8989,"PublicPort":8989,"Type":"tcp"},
                       {"PrivatePort":9000,"Type":"udp"}],
              "Labels":{"com.docker.compose.project":"media","com.docker.compose.service":"sonarr"}},
             {"Id":"fff","Names":["/old"],"Image":"alpine","State":"exited","Status":"Exited (137) 2 days ago","Ports":[],"Labels":null}]
            """;

        var rows = DockerContainers.ParseList(payload);

        var sonarr = rows[0];
        Assert.Equal("sonarr", sonarr.Name);
        Assert.Equal("media", sonarr.Project);
        Assert.Equal("sonarr", sonarr.Service);
        Assert.Equal(ContainerHealth.Healthy, sonarr.Health);
        Assert.Equal("Up 3 hours", sonarr.Uptime);
        Assert.Equal(["8989→8989", "9000/udp"], sonarr.Ports.Select(p => p.ToString()));

        Assert.Equal(137, rows[1].ExitCode);
        Assert.Null(rows[1].Project);
        Assert.True(rows[1].IsStopped);
    }

    [Theory]
    [InlineData("Up 5 minutes (unhealthy)", ContainerHealth.Unhealthy)]
    [InlineData("Up 5 seconds (health: starting)", ContainerHealth.Starting)]
    [InlineData("Up 5 minutes", ContainerHealth.None)]
    public void HealthIsReadFromTheStatus(string status, ContainerHealth expected) =>
        Assert.Equal(expected, Row("x", status: status).Health);

    [Fact]
    public void ProjectsAreGroupedByNameWithTheRestLast()
    {
        var groups = DockerContainers.Group(
        [
            Row("watchtower"),
            Row("radarr", project: "media"),
            Row("gitea", project: "git"),
            Row("sonarr", project: "media"),
            Row("cloudflared"),
        ]);

        Assert.Equal(["git", "media", null], groups.Select(g => g.Project));
        Assert.Equal(["radarr", "sonarr"], groups[1].Containers.Select(c => c.Name));
        Assert.Equal(["cloudflared", "watchtower"], groups[2].Containers.Select(c => c.Name));
        Assert.Equal("Not in a Compose project", groups[2].Title);
    }

    [Fact]
    public void TroubleFirstPutsUnhealthyAndRestartingAtTheTop()
    {
        var sorted = DockerContainers.Sort(
        [
            Row("a-stopped", state: "exited", status: "Exited (0) 1 hour ago"),
            Row("b-fine"),
            Row("c-sick", status: "Up 1 hour (unhealthy)"),
            Row("d-looping", state: "restarting", status: "Restarting (1) 5 seconds ago"),
        ], DockerContainers.SortBy.State);

        Assert.Equal(["c-sick", "d-looping", "b-fine", "a-stopped"], sorted.Select(r => r.Name));
    }

    [Fact]
    public void SortingByCpuUsesTheLatestStats()
    {
        var stats = new Dictionary<string, ContainerStats>
        {
            ["low0123456789abcdef"] = new(1, 0, 0, 0, 0, default),
            ["high0123456789abcdef"] = new(90, 0, 0, 0, 0, default),
        };
        var sorted = DockerContainers.Sort([Row("low"), Row("none"), Row("high")], DockerContainers.SortBy.Cpu,
            id => stats.TryGetValue(id, out var s) ? s : null);

        Assert.Equal(["high", "low", "none"], sorted.Select(r => r.Name));
    }

    [Fact]
    public void SearchMatchesNameImageProjectAndService()
    {
        var row = Row("media-sonarr-1", project: "media", service: "sonarr", image: "lscr.io/linuxserver/sonarr");

        Assert.True(DockerContainers.Matches(row, "SONARR", "all"));
        Assert.True(DockerContainers.Matches(row, "linuxserver", null));
        Assert.True(DockerContainers.Matches(row, "media", "running"));
        Assert.False(DockerContainers.Matches(row, "radarr", "all"));
        Assert.False(DockerContainers.Matches(row, "", "stopped"));
        Assert.True(DockerContainers.Matches(Row("x", status: "Up (unhealthy)"), null, "unhealthy"));
    }

    [Fact]
    public void AMovedTagIsSpottedFromLocalImagesOnly()
    {
        var rows = new[]
        {
            Row("current", image: "nginx", imageId: "sha256:new"),
            Row("behind", image: "nginx:latest", imageId: "sha256:old"),
            Row("pinned", image: "redis:7", imageId: "sha256:r7"),
            Row("local", image: "mybuild", imageId: "sha256:mine"),
        };
        var references = rows.ToDictionary(r => r.Id, r => r.Image);
        var tags = DockerContainers.ParseImageTags("""
            [{"Id":"sha256:new","RepoTags":["nginx:latest"]},{"Id":"sha256:r7","RepoTags":["redis:7"]},
             {"Id":"sha256:dangling","RepoTags":["<none>:<none>"]}]
            """);

        var stale = DockerContainers.StaleImages(rows, references, tags);

        Assert.Equal(["behind"], stale.Keys.Select(id => rows.Single(r => r.Id == id).Name));
    }
}

/// <summary>What each button asks Docker for, and when it is offered at all.</summary>
public class ContainerActionTests
{
    [Theory]
    [InlineData(ContainerAction.Start, "POST", "/containers/abc/start")]
    [InlineData(ContainerAction.Stop, "POST", "/containers/abc/stop")]
    [InlineData(ContainerAction.Restart, "POST", "/containers/abc/restart")]
    [InlineData(ContainerAction.Pause, "POST", "/containers/abc/pause")]
    [InlineData(ContainerAction.Unpause, "POST", "/containers/abc/unpause")]
    [InlineData(ContainerAction.Remove, "DELETE", "/containers/abc")]
    public void EachActionIsOneCall(ContainerAction action, string method, string path)
    {
        var request = DockerContainers.Request(action, "abc");

        Assert.Equal(method, request.Method.Method);
        Assert.Equal(path, request.Path);
    }

    [Fact]
    public void OnlyTheActionsThatMeanSomethingAreOffered()
    {
        var running = ContainerListTests.Row("r");
        var stopped = ContainerListTests.Row("s", state: "exited", status: "Exited (0) 1 hour ago");
        var paused = ContainerListTests.Row("p", state: "paused", status: "Up 1 hour (Paused)");

        Assert.True(DockerContainers.Applies(ContainerAction.Stop, running));
        Assert.False(DockerContainers.Applies(ContainerAction.Start, running));
        Assert.False(DockerContainers.Applies(ContainerAction.Remove, running));
        Assert.True(DockerContainers.Applies(ContainerAction.Remove, stopped));
        Assert.True(DockerContainers.Applies(ContainerAction.Start, stopped));
        Assert.True(DockerContainers.Applies(ContainerAction.Unpause, paused));
        Assert.False(DockerContainers.Applies(ContainerAction.Pause, paused));
    }

    [Fact]
    public async Task ActionsGoToTheRightPlaceAndNotModifiedIsSuccess()
    {
        using var docker = new ScriptedDocker(context =>
        {
            // Starting a running container is Docker's 304 — already the state asked for.
            if (context.Request.Url!.AbsolutePath.EndsWith("/start"))
            {
                context.Response.StatusCode = 304;
                return Task.CompletedTask;
            }
            context.Response.StatusCode = 204;
            return Task.CompletedTask;
        });

        await DockerContainers.RunAsync(docker.Endpoint, ContainerAction.Start, "abc", default);
        await DockerContainers.RunAsync(docker.Endpoint, ContainerAction.Remove, "abc", default);

        Assert.Equal(["POST /v1.41/containers/abc/start", "DELETE /v1.41/containers/abc"], docker.Requests);
    }
}

/// <summary>
/// Working out what a socket proxy lets through without pressing anything real: each probe
/// names a container that cannot exist, so the proxy's 403 and Docker's 404 are the only
/// two answers — and only the first means no.
/// </summary>
public class DockerCapabilityTests
{
    private static ScriptedDocker Proxy(Func<string, string, bool> forbid) => new(context =>
        forbid(context.Request.HttpMethod, context.Request.Url!.AbsolutePath)
            ? ScriptedDocker.Forbidden(context)
            : ScriptedDocker.NoSuchContainer(context));

    [Fact]
    public async Task ARawSocketAllowsEverything()
    {
        using var docker = Proxy((_, _) => false);

        var capabilities = await DockerContainers.CapabilitiesAsync(docker.Endpoint, TimeSpan.FromSeconds(5), false, default);

        Assert.Empty(capabilities.Denied);
        Assert.All(docker.Requests, r => Assert.Contains(DockerContainers.ProbeName, r));
    }

    [Fact]
    public async Task ARefusedCallIsDisabledWithTheFlagThatFixesIt()
    {
        // linuxserver/socket-proxy with CONTAINERS=1 ALLOW_RESTARTS=1 POST=0: restarts and
        // stops through, everything else that writes refused, and no IMAGES.
        using var docker = Proxy((method, path) =>
            path.StartsWith("/v1.41/images") ||
            (method != "GET" && !path.EndsWith("/restart") && !path.EndsWith("/stop")));

        var capabilities = await DockerContainers.CapabilitiesAsync(docker.Endpoint, TimeSpan.FromSeconds(5), false, default);

        Assert.True(capabilities.Allows(ContainerAction.Restart));
        Assert.True(capabilities.Allows(ContainerAction.Stop));
        Assert.True(capabilities.Allows(DockerCapabilities.Logs));
        Assert.True(capabilities.Allows(DockerCapabilities.Stats));
        Assert.False(capabilities.Allows(ContainerAction.Start));
        Assert.False(capabilities.Allows(ContainerAction.Remove));
        Assert.False(capabilities.Allows(DockerCapabilities.Images));

        Assert.Contains("ALLOW_START=1", capabilities.Why(ContainerAction.Start));
        Assert.Contains("ALLOW_PAUSE=1", capabilities.Why(ContainerAction.Pause));
        Assert.Contains("POST=1", capabilities.Why(ContainerAction.Remove));
        Assert.Contains("IMAGES=1", capabilities.Why(DockerCapabilities.Images));
    }

    [Theory]
    [InlineData("POST", "/containers/abc/stop", "ALLOW_STOP=1")]
    [InlineData("POST", "/containers/abc/pause", "ALLOW_PAUSE=1")]
    [InlineData("POST", "/containers/abc/unpause", "ALLOW_UNPAUSE=1")]
    [InlineData("DELETE", "/containers/abc", "POST=1")]
    [InlineData("GET", "/containers/abc/logs?follow=1", "ALLOW_LOGS=1")]
    [InlineData("GET", "/containers/abc/stats?stream=false", "CONTAINERS=1")]
    public void EachRefusalNamesTheFlagForIt(string method, string path, string flag) =>
        Assert.Contains(flag, DockerProxyDeniedException.RuleFor(method, path).Flags);

    [Fact]
    public async Task TheAnswerIsAskedForOnceAndRemembered()
    {
        using var docker = Proxy((method, _) => method == "DELETE");

        await DockerContainers.CapabilitiesAsync(docker.Endpoint, TimeSpan.FromSeconds(5), false, default);
        var asked = docker.Requests.Count;
        var cached = await DockerContainers.CapabilitiesAsync(docker.Endpoint, TimeSpan.FromSeconds(5), false, default);

        Assert.Equal(asked, docker.Requests.Count);
        Assert.False(cached.Allows(ContainerAction.Remove));
        Assert.False(DockerContainers.CachedCapabilities(docker.Endpoint).Allows(ContainerAction.Remove));

        // "Check again" asks again.
        await DockerContainers.CapabilitiesAsync(docker.Endpoint, TimeSpan.FromSeconds(5), true, default);
        Assert.Equal(asked * 2, docker.Requests.Count);
    }

    [Fact]
    public async Task ARefusalDuringARealActionIsRememberedToo()
    {
        using var docker = Proxy((method, _) => method != "GET");

        var refused = await Assert.ThrowsAsync<DockerProxyDeniedException>(() =>
            DockerContainers.RunAsync(docker.Endpoint, ContainerAction.Pause, "abc", default));

        Assert.Contains("ALLOW_PAUSE=1", refused.Message);
        Assert.False(DockerContainers.CachedCapabilities(docker.Endpoint).Allows(ContainerAction.Pause));
    }

    [Fact]
    public async Task NothingIsRememberedWhenDockerCannotBeReached()
    {
        var endpoint = "tcp://127.0.0.1:1"; // nothing listens on port 1

        var answer = await DockerContainers.CapabilitiesAsync(endpoint, TimeSpan.FromSeconds(2), false, default);

        Assert.Empty(answer.Denied);
        Assert.Same(DockerCapabilities.All, DockerContainers.CachedCapabilities(endpoint));
    }
}

/// <summary>The guard rails: LabbyTwo's own container, the protected list, and hidden environment values.</summary>
public class ContainerSafetyTests
{
    [Fact]
    public void LabbyTwoKnowsItsOwnContainerByHostname()
    {
        var self = ContainerListTests.Row("labbytwo", id: "3f2a9c1b7d4e5f60718293a4b5c6d7e8");

        Assert.True(ContainerSafety.IsSelf(self, "3f2a9c1b7d4e"));
        Assert.True(ContainerSafety.IsSelf(self, "labbytwo"));
        Assert.False(ContainerSafety.IsSelf(self, "3f2a"));  // too short to be a container id
        Assert.False(ContainerSafety.IsSelf(self, "MY-DESKTOP"));
        Assert.False(ContainerSafety.IsSelf(self, ""));
    }

    [Fact]
    public void TheProtectedListMatchesNamesServicesAndWildcards()
    {
        var list = ContainerSafety.ParseList("cloudflared\n traefik , vpn-*");

        Assert.Equal(["cloudflared", "traefik", "vpn-*"], list);
        Assert.True(ContainerSafety.IsListed(ContainerListTests.Row("tunnel-cloudflared-1", service: "cloudflared"), list));
        Assert.True(ContainerSafety.IsListed(ContainerListTests.Row("Traefik"), list));
        Assert.True(ContainerSafety.IsListed(ContainerListTests.Row("vpn-gluetun"), list));
        Assert.False(ContainerSafety.IsListed(ContainerListTests.Row("sonarr"), list));
    }

    [Theory]
    [InlineData(ContainerAction.Stop, false, false, false)]
    [InlineData(ContainerAction.Stop, true, false, true)]
    [InlineData(ContainerAction.Stop, false, true, true)]
    [InlineData(ContainerAction.Pause, false, true, true)]
    [InlineData(ContainerAction.Restart, true, false, false)]
    [InlineData(ContainerAction.Restart, false, true, false)]
    [InlineData(ContainerAction.Remove, false, false, true)]
    public void StoppingSomethingProtectedNeedsItsNameTyped(ContainerAction action, bool self, bool listed, bool typed) =>
        Assert.Equal(typed, ContainerSafety.NeedsTypedName(action, self, listed));

    [Fact]
    public void StoppingLabbyTwoSaysItWillNotComeBack()
    {
        var warning = ContainerSafety.Warning(ContainerAction.Stop, ContainerListTests.Row("labbytwo"), self: true, listed: false);

        Assert.Contains("LabbyTwo itself runs in", warning);
        Assert.Contains("will not come back", warning);
    }

    [Fact]
    public void AProjectWideStopLeavesLabbyTwoAndProtectedContainersAlone()
    {
        var hostname = "aaaaaaaaaaaa";
        var containers = new[]
        {
            ContainerListTests.Row("labbytwo", id: hostname + "0000", project: "home"),
            ContainerListTests.Row("cloudflared", project: "home"),
            ContainerListTests.Row("homepage", project: "home"),
            ContainerListTests.Row("old", state: "exited", status: "Exited (0) 1 day ago", project: "home"),
        };

        var (act, skip) = ContainerSafety.PlanBulk(ContainerAction.Stop, containers, hostname, ["cloudflared"]);

        Assert.Equal(["homepage"], act.Select(c => c.Name));
        Assert.Equal(["labbytwo", "cloudflared", "old"], skip.Select(s => s.Container.Name));
        Assert.Contains("already stopped", skip.Single(s => s.Container.Name == "old").Why);

        // Starting is always safe to do in bulk.
        var (starts, _) = ContainerSafety.PlanBulk(ContainerAction.Start, containers, hostname, ["cloudflared"]);
        Assert.Equal(["old"], starts.Select(c => c.Name));
    }

    [Fact]
    public void EnvironmentValuesAreSplitAtTheFirstEqualsAndMasked()
    {
        var env = ContainerSafety.ParseEnv(["DB_URL=postgres://u:p@db/x?a=b", "EMPTY=", "BARE"]);

        Assert.Equal("postgres://u:p@db/x?a=b", env[0].Value);
        Assert.Equal(ContainerSafety.Mask, ContainerSafety.Display(env[0], revealed: false));
        Assert.Equal("postgres://u:p@db/x?a=b", ContainerSafety.Display(env[0], revealed: true));
        Assert.Equal("", ContainerSafety.Display(env[1], revealed: false));
        Assert.Equal(("BARE", ""), (env[2].Key, env[2].Value));
    }

    [Fact]
    public void ConnectionsThatReachAContainerByNameAreFound()
    {
        var sonarr = ContainerListTests.Row("media-sonarr-1", service: "sonarr");
        var connections = new[]
        {
            new Connection { Name = "Sonarr", Provider = "sonarr", Settings = new SettingsBag { ["url"] = "http://sonarr:8989" } },
            new Connection { Name = "By IP", Provider = "sonarr", Settings = new SettingsBag { ["url"] = "http://192.168.1.5:8989" } },
            new Connection { Name = "Ping", Provider = "ping", Settings = new SettingsBag { ["host"] = "media-sonarr-1" } },
            new Connection { Name = "Docker", Provider = "docker", Settings = new SettingsBag { ["endpoint"] = "sonarr:2375" } },
        };

        Assert.Equal(["Sonarr", "Ping"], ContainerSafety.ConnectionsReaching(sonarr, connections).Select(c => c.Name));
    }
}

/// <summary>
/// The page's parts drawn by the framework's own HTML renderer — enough to show what a
/// browser would be sent, which for the environment panel is the point: a hidden value must
/// not be in the page at all.
/// </summary>
public class ContainersTabRenderTests
{
    private static async Task<string> RenderAsync<T>(IServiceProvider services, Dictionary<string, object?> parameters)
        where T : IComponent
    {
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters));
            return output.ToHtmlString();
        });
    }

    private static ServiceProvider Bare() => new ServiceCollection().AddLogging().BuildServiceProvider();

    [Fact]
    public async Task EnvironmentValuesAreNotSentUntilRevealed()
    {
        var details = new ContainerDetails(
            "abc", "db", "postgres:16", "sha256:0123456789abcdef", false, DateTimeOffset.UnixEpoch, null, null,
            "unless-stopped", 0, ["POSTGRES_PASSWORD=hunter2-very-secret", "TZ=Europe/London"],
            [new ContainerMount("volume", "pgdata", "/var/lib/postgresql/data", true)],
            [new ContainerNetwork("backend", "172.18.0.4", ["db"])],
            new Dictionary<string, string> { ["com.docker.compose.project"] = "git" },
            "docker-entrypoint.sh postgres");

        await using var services = Bare();
        var html = await RenderAsync<ContainerInspect>(services, new() { [nameof(ContainerInspect.Details)] = details });

        Assert.DoesNotContain("hunter2", html);
        Assert.DoesNotContain("Europe/London", html);
        Assert.Contains("POSTGRES_PASSWORD", html);
        Assert.Contains(ContainerSafety.Mask, WebUtility.HtmlDecode(html));
        Assert.Contains("Reveal", html);
        Assert.Contains("pgdata", html);
        Assert.Contains("172.18.0.4", html);
        Assert.Contains("Unless stopped by hand", html);
    }

    [Fact]
    public async Task ARefusedActionIsDrawnDisabledWithTheProxyFlag()
    {
        var group = new ContainerGroup("media",
        [
            ContainerListTests.Row("sonarr", project: "media", status: "Up 1 hour (healthy)"),
            ContainerListTests.Row("cloudflared", project: "media"),
            ContainerListTests.Row("old", state: "exited", status: "Exited (1) 2 days ago", project: "media"),
        ]);
        var capabilities = new DockerCapabilities
        {
            Denied = new Dictionary<string, string>
            {
                [DockerCapabilities.KeyFor(ContainerAction.Remove)] = "Enable CONTAINERS=1 and POST=1 on the proxy",
            },
        };

        await using var services = Bare();
        var html = await RenderAsync<ContainerGroupCard>(services, new()
        {
            [nameof(ContainerGroupCard.Group)] = group,
            [nameof(ContainerGroupCard.Capabilities)] = capabilities,
            [nameof(ContainerGroupCard.IsListed)] = (Func<ContainerRow, bool>)(r => r.Name == "cloudflared"),
            [nameof(ContainerGroupCard.Stale)] = new Dictionary<string, string> { [group.Containers[0].Id] = "nginx:latest" },
            [nameof(ContainerGroupCard.Stats)] = new Dictionary<string, ContainerStats>
            {
                [group.Containers[0].Id] = new(12.5, 300 * 1024 * 1024, 1024 * 1024 * 1024, 2048, 1024, default),
            },
        });

        Assert.Contains("2 of 3 running", html);
        Assert.Contains("healthy", html);
        Assert.Contains("protected", html);
        Assert.Contains("newer image", html);
        Assert.Contains("12.5%", html);
        Assert.Contains("300 MiB / 1 GiB", html);

        // The remove button on the stopped one is there, disabled, and says why.
        var remove = html[html.IndexOf("data-action=\"remove\"", StringComparison.Ordinal)..];
        remove = remove[..remove.IndexOf('>')];
        var tag = html[..html.IndexOf("data-action=\"remove\"", StringComparison.Ordinal)];
        tag = tag[tag.LastIndexOf("<button", StringComparison.Ordinal)..] + remove;
        Assert.Contains("disabled", tag);
        Assert.Contains("POST=1", tag);

        // Running containers get stop and restart, and nothing offers to start them.
        Assert.Contains("aria-label=\"Restart sonarr\"", html);
        Assert.DoesNotContain("aria-label=\"Start sonarr\"", html);
    }

    [Fact]
    public async Task ReadOnlyDrawsNoButtonsThatChangeAnything()
    {
        var group = new ContainerGroup(null, [ContainerListTests.Row("sonarr")]);

        await using var services = Bare();
        var html = await RenderAsync<ContainerGroupCard>(services, new()
        {
            [nameof(ContainerGroupCard.Group)] = group,
            [nameof(ContainerGroupCard.AllowActions)] = false,
        });

        Assert.DoesNotContain("data-action=\"stop\"", html);
        Assert.DoesNotContain("data-action=\"restart\"", html);
        Assert.Contains("data-action=\"logs\"", html);
        Assert.Contains("data-action=\"inspect\"", html);
    }

    [Fact]
    public async Task TheTabWithoutADockerConnectionSaysWhatItNeeds()
    {
        var directory = TestHost.TempDirectory();
        var services = TestHost.ReadyHost(directory);
        try
        {
            await using var withExtras = new ServiceCollection()
                .AddLogging()
                .AddSingleton(services.GetRequiredService<ConfigStore>())
                .AddSingleton(services.GetRequiredService<Registry>())
                .AddSingleton(services.GetRequiredService<Microsoft.Extensions.Options.IOptions<LabbyOptions>>())
                .AddSingleton(new Offload(NullLogger<Offload>.Instance))
                .AddSingleton<IJSRuntime, NoJs>()
                .BuildServiceProvider();

            var html = await RenderAsync<ContainersTab>(withExtras, new()
            {
                [nameof(ContainersTab.Tab)] = new Tab { Name = "Containers", Kind = ContainersTabKind.KindKey },
            });

            Assert.Contains("needs a Docker connection", html);
        }
        finally
        {
            TestHost.Teardown(services, directory);
        }
    }

    [Fact]
    public void TheKindIsDiscovered()
    {
        var directory = TestHost.TempDirectory();
        var services = TestHost.Build(directory);
        try
        {
            var kind = services.GetRequiredService<Registry>().TabKind(ContainersTabKind.KindKey);

            Assert.NotNull(kind);
            Assert.Equal(typeof(ContainersTab), kind!.Component);
            Assert.Contains("controls every container", kind.Description);
        }
        finally
        {
            TestHost.Teardown(services, directory);
        }
    }

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new JSException("No browser in a test.");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new JSException("No browser in a test.");
    }
}
