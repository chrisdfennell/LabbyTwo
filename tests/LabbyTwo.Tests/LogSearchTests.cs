using System.Buffers.Binary;
using System.Net;
using System.Text;
using LabbyTwo.Components.Pages;
using LabbyTwo.Components.Shared;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabbyTwo.Tests;

/// <summary>
/// The log search against a Docker Engine that answers from a script over real HTTP: both
/// stream formats, the time window as Docker is asked for it, a regex that runs away, the
/// masking, the caps, cancelling, and the incident link.
/// </summary>
public sealed class LogSearchTests
{
    private static readonly DateTimeOffset Nine = new(2026, 9, 30, 9, 0, 0, TimeSpan.Zero);

    private static byte[] Frame(int stream, string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var frame = new byte[8 + payload.Length];
        frame[0] = (byte)stream;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(4), payload.Length);
        payload.CopyTo(frame, 8);
        return frame;
    }

    private static string Stamp(int minute, string text) => $"2026-09-30T10:{minute:00}:00.000000000Z {text}\n";

    private static LogTarget Target(ScriptedDocker docker, string name) =>
        new("docker1", "NAS", docker.Endpoint, TimeSpan.FromSeconds(5), ContainerListTests.Row(name, id: name));

    private static LogSearchQuery Query(string text = "", params string[] quick) =>
        new() { Text = text, Quick = quick, Since = Nine };

    /// <summary>Answers each container's log from a map of id to the bytes it should send.</summary>
    private static ScriptedDocker Serving(Dictionary<string, byte[]> logs) => new(async context =>
    {
        var path = context.Request.Url!.AbsolutePath;
        var id = path.Split('/')[^2];
        if (!logs.TryGetValue(id, out var body))
        {
            await ScriptedDocker.NoSuchContainer(context);
            return;
        }
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body);
    });

    // ---- the two stream formats ---------------------------------------------------

    [Fact]
    public void AFramedStreamIsRecognisedFromItsFirstBytesEvenOneAtATime()
    {
        var bytes = Frame(1, Stamp(0, "hello")).Concat(Frame(2, Stamp(1, "oops"))).ToArray();
        var reader = new SniffingLogReader();
        var lines = new List<LogLine>();
        foreach (var b in bytes)
            lines.AddRange(reader.Feed([b]));
        lines.AddRange(reader.Flush());

        Assert.False(reader.Tty);
        Assert.Equal(["hello", "oops"], lines.Select(l => l.Text));
        Assert.Equal([LogStream.Stdout, LogStream.Stderr], lines.Select(l => l.Stream));
        Assert.Equal(Nine.AddHours(1).AddMinutes(1), lines[1].Time);
    }

    [Fact]
    public void ATtyStreamIsRecognisedByTheTimestampInFront()
    {
        var reader = new SniffingLogReader();
        var lines = reader.Feed(Encoding.UTF8.GetBytes(Stamp(0, "hello") + Stamp(1, "there")));

        Assert.True(reader.Tty);
        Assert.Equal(["hello", "there"], lines.Select(l => l.Text));
    }

    [Fact]
    public void ALogShorterThanAHeaderIsDecidedWhenItEnds()
    {
        var reader = new SniffingLogReader();
        Assert.Empty(reader.Feed("2026"u8));

        Assert.Equal("2026", Assert.Single(reader.Flush()).Text);
        Assert.True(reader.Tty);
    }

    [Fact]
    public async Task ASearchReadsFramedAndTtyContainersAlike()
    {
        using var docker = Serving(new()
        {
            ["framed"] = [.. Frame(1, Stamp(0, "all good")), .. Frame(2, Stamp(1, "ERROR disk full")), .. Frame(1, Stamp(2, "fine"))],
            ["tty"] = Encoding.UTF8.GetBytes(Stamp(0, "an error here") + Stamp(1, "nothing") + Stamp(2, "another error")),
            ["quiet"] = Encoding.UTF8.GetBytes(Stamp(0, "all quiet")),
        });

        var run = new LogSearchRun(Query("error"), [Target(docker, "framed"), Target(docker, "tty"), Target(docker, "quiet")]);
        await run.RunAsync(default).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(run.Finished);
        Assert.Equal(3, run.TotalMatches);
        var framed = run.Groups.Single(g => g.Target.Container.Name == "framed");
        var hit = Assert.Single(framed.Hits());
        Assert.Equal("ERROR disk full", hit.Line.Text);
        Assert.Equal(LogStream.Stderr, hit.Line.Stream);
        Assert.Equal((0, 5), Assert.Single(hit.Spans));
        Assert.Equal(2, run.Groups.Single(g => g.Target.Container.Name == "tty").Matches);
        Assert.Equal(LogGroupState.Done, run.Groups.Single(g => g.Target.Container.Name == "quiet").State);
        Assert.Equal(0, run.Groups.Single(g => g.Target.Container.Name == "quiet").Matches);
    }

    [Fact]
    public async Task MatchesKeepTheLinesAroundThem()
    {
        var text = string.Concat(Enumerable.Range(0, 10).Select(i => Stamp(i, i == 5 ? "boom failed" : $"line {i}")));
        using var docker = Serving(new() { ["app"] = Encoding.UTF8.GetBytes(text) });

        var run = new LogSearchRun(Query("", "failed") with { ContextLines = 2 }, [Target(docker, "app")]);
        await run.RunAsync(default).WaitAsync(TimeSpan.FromSeconds(20));

        var hit = Assert.Single(run.Groups[0].Hits());
        Assert.Equal(["line 3", "line 4"], hit.Before.Select(l => l.Text));
        Assert.Equal(["line 6", "line 7"], hit.After.Select(l => l.Text));
    }

    // ---- since and until ------------------------------------------------------------

    [Fact]
    public void TheWindowIsPassedAsDockerWantsIt()
    {
        var since = new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero).AddTicks(1234567);
        var until = since.AddHours(1);

        Assert.Equal("1790762400.123456700", DockerLogs.UnixTime(since));

        // A window ending now asks for the newest lines; one in the past cannot, because
        // Docker applies tail before since and until.
        var toNow = DockerLogs.WindowPath("abc", since, null, 500);
        Assert.Contains("since=1790762400.123456700", toNow);
        Assert.DoesNotContain("until=", toNow);
        Assert.Contains("tail=500", toNow);

        var past = DockerLogs.WindowPath("abc", since, until, null, stdout: true, stderr: false);
        Assert.Contains("until=1790766000.123456700", past);
        Assert.DoesNotContain("tail=", past);
        Assert.Contains("stderr=0", past);
    }

    [Fact]
    public async Task TheSearchAsksDockerForItsWindow()
    {
        using var docker = Serving(new() { ["app"] = Encoding.UTF8.GetBytes(Stamp(0, "x")) });
        var until = Nine.AddHours(2);

        var run = new LogSearchRun(Query("x") with { Until = until, Stderr = false }, [Target(docker, "app")]);
        await run.RunAsync(default).WaitAsync(TimeSpan.FromSeconds(20));

        var request = Assert.Single(docker.Requests);
        Assert.Contains($"since={DockerLogs.UnixTime(Nine)}", request);
        Assert.Contains($"until={DockerLogs.UnixTime(until)}", request);
        Assert.Contains("stdout=1&stderr=0&timestamps=1", request);
        Assert.DoesNotContain("tail=", request);
    }

    [Fact]
    public async Task LinesOutsideTheWindowAreDroppedEvenIfDockerSendsThem()
    {
        // An old daemon that ignores `until` must not put an hour it was not asked for on the page.
        using var docker = Serving(new() { ["app"] = Encoding.UTF8.GetBytes(Stamp(0, "error early") + Stamp(30, "error late")) });

        var run = new LogSearchRun(Query("error") with { Until = new DateTimeOffset(2026, 9, 30, 10, 10, 0, TimeSpan.Zero) },
            [Target(docker, "app")]);
        await run.RunAsync(default).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(["error early"], run.Groups[0].Hits().Select(h => h.Line.Text));
    }

    // ---- patterns -------------------------------------------------------------------

    [Fact]
    public void ASimpleRegexRunsOnTheEngineThatCannotRunAway()
    {
        var matcher = LogMatcher.Create(Query(@"time(out|d out)") with { IsRegex = true });

        Assert.True(matcher.NonBacktracking);
        Assert.NotNull(matcher.Match("request TIMED OUT"));
        Assert.Null(matcher.Match("all fine"));
    }

    [Fact]
    public void AnInvalidRegexIsSaidInWords()
    {
        var failure = Assert.Throws<ArgumentException>(() => LogMatcher.Create(Query("(unclosed") with { IsRegex = true }));
        Assert.StartsWith("That is not a valid regular expression", failure.Message);

        Assert.Throws<ArgumentException>(() => LogMatcher.Create(Query("")));
    }

    [Fact]
    public void CaseSensitivityAndQuickFiltersCombine()
    {
        var sensitive = LogMatcher.Create(Query("Disk", "error") with { CaseSensitive = true });
        Assert.NotNull(sensitive.Match("Disk error on sda"));
        Assert.Null(sensitive.Match("disk error on sda"));
        Assert.Null(sensitive.Match("Disk is fine"));

        var quick = LogMatcher.Create(Query("", "exception", "warn"));
        Assert.NotNull(quick.Match("System.NullReferenceException: boom"));
        Assert.NotNull(quick.Match("WARNING low memory"));
        Assert.Null(quick.Match("warned nobody"));
    }

    [Fact]
    public async Task ARegexThatRunsAwayStopsTheSearch()
    {
        // The back-reference keeps it off the non-backtracking engine; on thirty a's and no
        // match it backtracks exponentially, so the per-line timeout is what ends it.
        var runaway = new string('a', 30) + "!";
        var body = string.Concat(Enumerable.Range(0, 20).Select(i => Stamp(i, runaway)));
        using var docker = Serving(new() { ["app"] = Encoding.UTF8.GetBytes(body), ["other"] = Encoding.UTF8.GetBytes(body) });

        var query = Query(@"^(a+)+\1b$") with { IsRegex = true, RegexTimeout = TimeSpan.FromMilliseconds(5) };
        var run = new LogSearchRun(query, [Target(docker, "app"), Target(docker, "other")]);
        Assert.False(run.NonBacktracking);

        await run.RunAsync(default).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(run.Stopped);
        Assert.Contains("took too long", run.Stopped);
        Assert.Equal(0, run.TotalMatches);
    }

    // ---- masking --------------------------------------------------------------------

    [Theory]
    [InlineData("DB_PASSWORD=hunter2 connecting", "DB_PASSWORD=•••••••• connecting")]
    [InlineData("{\"api_key\": \"abc123\", \"user\": \"bob\"}", "{\"api_key\": \"••••••••\", \"user\": \"bob\"}")]
    [InlineData("GET /feed?token=s3cr3t&page=2", "GET /feed?token=••••••••&page=2")]
    [InlineData("connecting to postgres://app:hunter2@db:5432/app", "connecting to postgres://app:••••••••@db:5432/app")]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.x.y", "Authorization: Bearer ••••••••")]
    [InlineData("secret: swordfish", "secret: ••••••••")]
    [InlineData("started in 3.2s on port 8080", "started in 3.2s on port 8080")]
    public void SecretLookingValuesAreMasked(string line, string shown)
    {
        Assert.Equal(shown, LogSearch.Mask(line));
    }

    [Fact]
    public async Task ASecretCannotBeFoundBySearchingForIt()
    {
        // Masked before matching: what is found is what is shown, and a search is no way of
        // confirming a guess at somebody's password.
        using var docker = Serving(new() { ["db"] = Encoding.UTF8.GetBytes(Stamp(0, "error: login failed PASSWORD=hunter2")) });

        var guess = new LogSearchRun(Query("hunter2"), [Target(docker, "db")]);
        await guess.RunAsync(default).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, guess.TotalMatches);

        var errors = new LogSearchRun(Query("error"), [Target(docker, "db")]);
        await errors.RunAsync(default).WaitAsync(TimeSpan.FromSeconds(20));
        var hit = Assert.Single(errors.Groups[0].Hits());
        Assert.DoesNotContain("hunter2", hit.Line.Text);
        Assert.Contains(ContainerSafety.Mask, hit.Line.Text);
    }

    // ---- caps -----------------------------------------------------------------------

    [Fact]
    public async Task OneContainerStopsAtItsLineCap()
    {
        var body = string.Concat(Enumerable.Range(0, 50).Select(i => Stamp(i, "error " + i)));
        using var docker = Serving(new() { ["app"] = Encoding.UTF8.GetBytes(body) });

        var run = new LogSearchRun(Query("error") with { Until = Nine.AddHours(3), MaxLinesPerContainer = 10 }, [Target(docker, "app")]);
        await run.RunAsync(default).WaitAsync(TimeSpan.FromSeconds(20));

        var group = run.Groups[0];
        Assert.Equal(10, group.Matches);
        Assert.Equal("stopped after 10 lines", group.Cut);
        Assert.Equal("error 0", group.Hits()[0].Line.Text);
    }

    [Fact]
    public async Task OneContainerStopsAtItsByteCap()
    {
        var body = string.Concat(Enumerable.Range(0, 2000).Select(i => Stamp(i % 60, "error " + new string('x', 100))));
        using var docker = Serving(new() { ["app"] = Encoding.UTF8.GetBytes(body) });

        var run = new LogSearchRun(Query("error") with { MaxBytesPerContainer = 16 * 1024 }, [Target(docker, "app")]);
        await run.RunAsync(default).WaitAsync(TimeSpan.FromSeconds(20));

        var group = run.Groups[0];
        Assert.StartsWith("stopped after 16", group.Cut);
        Assert.InRange(group.BytesRead, 16 * 1024, 64 * 1024);
        Assert.True(group.Matches < 2000);
    }

    [Fact]
    public async Task TheWholeSearchHoldsNoMoreThanItsCapButCountsEverything()
    {
        var body = string.Concat(Enumerable.Range(0, 40).Select(i => Stamp(i, "error " + i)));
        using var docker = Serving(new() { ["a"] = Encoding.UTF8.GetBytes(body), ["b"] = Encoding.UTF8.GetBytes(body) });

        var run = new LogSearchRun(Query("error") with { MaxHeld = 25, ContextLines = 0 }, [Target(docker, "a"), Target(docker, "b")]);
        await run.RunAsync(default).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(run.Capped);
        Assert.Equal(80, run.TotalMatches);
        Assert.Equal(25, run.Groups.Sum(g => g.Held));
        Assert.Equal(25, run.HeldLines);
    }

    [Fact]
    public async Task NoMoreThanFourLogsAreReadAtOnce()
    {
        using var docker = new ScriptedDocker(async context =>
        {
            await Task.Delay(150);
            var body = Encoding.UTF8.GetBytes(Stamp(0, "error"));
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body);
        });

        var targets = Enumerable.Range(0, 10).Select(i => Target(docker, "c" + i)).ToList();
        var run = new LogSearchRun(Query("error"), targets);
        await run.RunAsync(default).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(10, run.TotalMatches);
        Assert.InRange(docker.MaxInFlight, 1, LogSearch.Concurrency);
    }

    // ---- refusals and cancelling -------------------------------------------------------

    [Fact]
    public async Task AProxyThatRefusesLogsIsAskedOnceAndNamesItsFlag()
    {
        using var docker = new ScriptedDocker(ScriptedDocker.Forbidden);

        var targets = Enumerable.Range(0, 6).Select(i => Target(docker, "c" + i)).ToList();
        var run = new LogSearchRun(Query("error"), targets);
        await run.RunAsync(default, concurrency: 1).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Single(docker.Requests);
        Assert.All(run.Groups, g =>
        {
            Assert.Equal(LogGroupState.Failed, g.State);
            Assert.Contains("ALLOW_LOGS=1", g.Error);
        });
    }

    [Fact]
    public async Task CancellingStopsReadingAtOnce()
    {
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        using var docker = new ScriptedDocker(async context =>
        {
            context.Response.SendChunked = true;
            await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(Stamp(0, "error first")));
            await context.Response.OutputStream.FlushAsync();
            started.TrySetResult();
            await release.Task;
        });

        try
        {
            using var cancel = new CancellationTokenSource();
            var targets = Enumerable.Range(0, 6).Select(i => Target(docker, "c" + i)).ToList();
            var run = new LogSearchRun(Query("error"), targets);
            var running = run.RunAsync(cancel.Token);

            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancel.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(run.Finished);
            Assert.All(run.Groups, g => Assert.Equal(LogGroupState.Failed, g.State));
            Assert.Contains(run.Groups, g => g.Error!.Contains("Not read"));
            Assert.True(docker.Requests.Count <= LogSearch.Concurrency);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    // ---- scopes, incidents, highlighting -------------------------------------------------

    [Fact]
    public void ScopesPickTheRightContainers()
    {
        var rows = new[]
        {
            ContainerListTests.Row("sonarr", project: "media"),
            ContainerListTests.Row("radarr", state: "exited", status: "Exited (0) 1 hour ago", project: "media"),
            ContainerListTests.Row("gitea", project: "git"),
        };

        Assert.Equal(["gitea", "radarr", "sonarr"], LogSearch.Pick(rows, LogScope.All).Select(r => r.Name));
        Assert.Equal(["gitea", "sonarr"], LogSearch.Pick(rows, LogScope.Running).Select(r => r.Name));
        Assert.Equal(["radarr"], LogSearch.Pick(rows, LogScope.Chosen, ["RADARR"]).Select(r => r.Name));
        Assert.Equal(["radarr", "sonarr"], LogSearch.Pick(rows, LogScope.Project, project: "media").Select(r => r.Name));
    }

    [Fact]
    public void AnIncidentFillsInItsWindowAndTheContainersItsServicesAreReachedThrough()
    {
        var sonarrConnection = new Connection { Id = "sonarr-c", Name = "Sonarr", Provider = "sonarr", Settings = new SettingsBag { ["url"] = "http://sonarr:8989" } };
        var nasConnection = new Connection { Id = "nas-c", Name = "NAS", Provider = "ping", Settings = new SettingsBag { ["host"] = "192.168.1.5" } };
        var docker = new Connection { Id = "docker1", Name = "Docker", Provider = "docker" };
        var started = new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);
        var ended = started.AddMinutes(20);
        var incident = new Incident(7, started, ended, ended, false,
        [
            new IncidentMember("status:sonarr-c", ChangeKinds.Status, "sonarr-c", "Sonarr", started, ended),
            new IncidentMember("status:nas-c", ChangeKinds.Status, "nas-c", "NAS", started.AddMinutes(1), ended),
        ]);
        var rows = new Dictionary<string, IReadOnlyList<ContainerRow>>
        {
            ["docker1"] = [ContainerListTests.Row("media-sonarr-1", service: "sonarr"), ContainerListTests.Row("plex")],
        };

        var prefill = LogSearch.ForIncident(incident, [sonarrConnection, nasConnection, docker], rows, started.AddHours(5));

        Assert.Equal(started - IncidentRules.ContextBefore, prefill.Since);
        Assert.Equal(ended + IncidentRules.ContextAfter, prefill.Until);
        Assert.Equal(("docker1", "media-sonarr-1"), Assert.Single(prefill.Containers));
        Assert.Equal("logs?incident=7", LogSearch.IncidentLink(7));

        // Still open: the window reaches now, which is what lets Docker send the newest lines.
        var open = incident with { EndedAt = null };
        Assert.Null(LogSearch.ForIncident(open, [sonarrConnection], rows, started.AddHours(1)).Until);
    }

    [Fact]
    public async Task AMatchIsMarkedAndNothingInALogBecomesMarkup()
    {
        var text = "<script>alert('error')</script> error";
        var spans = LogMatcher.Create(Query("error")).Match(text)!;

        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<Highlighted>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(Highlighted.Text)] = text,
                [nameof(Highlighted.Spans)] = spans,
            }));
            return output.ToHtmlString();
        });

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Equal(2, html.Split("<mark>").Length - 1);
        Assert.Contains("<mark>error</mark>", html);
    }

    // ---- the page ---------------------------------------------------------------------

    /// <summary>A navigation manager at a fixed address, which is all the page's query parameters need.</summary>
    private sealed class FixedNavigation : NavigationManager
    {
        public FixedNavigation(string uri) => Initialize("http://labby.test/", uri);
    }

    private static ServiceProvider PageServices(ServiceProvider host, string uri) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton(host.GetRequiredService<ConfigStore>())
            .AddSingleton(host.GetRequiredService<IncidentStore>())
            .AddSingleton<NavigationManager>(new FixedNavigation(uri))
            .AddSupplyValueFromQueryProvider()
            .BuildServiceProvider();

    [Fact]
    public async Task ThePageReadsNothingUntilAskedAndSaysWhatItWillReadAndHide()
    {
        var requests = 0;
        using var docker = new ScriptedDocker(context =>
        {
            Interlocked.Increment(ref requests);
            return ScriptedDocker.Json(context, "[]");
        });
        var directory = TestHost.TempDirectory();
        var host = TestHost.ReadyHost(directory);
        try
        {
            await host.GetRequiredService<ConfigStore>().SaveConnectionAsync(
                new Connection { Name = "NAS Docker", Provider = "docker", Settings = new SettingsBag { ["endpoint"] = docker.Endpoint } });

            await using var services = PageServices(host, "http://labby.test/logs");
            using var renderer = new InteractiveRenderer(services);
            await renderer.RenderAsync<LogsPage>(new Dictionary<string, object?>());
            var html = System.Text.RegularExpressions.Regex.Replace(
                WebUtility.HtmlDecode(await renderer.WaitForAsync("Nothing is read until you press Search")), @"\s+", " ");

            Assert.Contains("5,000 lines or 4 MB", html);
            Assert.Contains("best effort", html);
            Assert.Contains("20,000", html);
            foreach (var (_, label, _) in LogSearch.QuickFilters)
                Assert.Contains(label, html);
            Assert.DoesNotContain("logs-results", html);

            await Task.Delay(200);
            Assert.Equal(0, Volatile.Read(ref requests));
        }
        finally
        {
            TestHost.Teardown(host, directory);
        }
    }

    [Fact]
    public async Task TheIncidentLinkSearchesTheInvolvedContainersOverTheIncidentsWindow()
    {
        var started = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddHours(-3).ToUnixTimeSeconds());
        var ended = started.AddMinutes(20);
        var list = """
            [
              {"Id": "sonarr1", "Names": ["/media-sonarr-1"], "Image": "sonarr", "ImageID": "sha256:a", "State": "running",
               "Status": "Up 3 hours", "Created": 1, "Labels": {"com.docker.compose.service": "sonarr"}},
              {"Id": "plex1", "Names": ["/plex"], "Image": "plex", "ImageID": "sha256:b", "State": "running",
               "Status": "Up 3 hours", "Created": 1, "Labels": {}}
            ]
            """;
        using var docker = new ScriptedDocker(async context =>
        {
            var path = context.Request.Url!.AbsolutePath;
            if (path.EndsWith("/containers/json", StringComparison.Ordinal))
            {
                await ScriptedDocker.Json(context, list);
                return;
            }
            var at = started.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", System.Globalization.CultureInfo.InvariantCulture);
            var body = Encoding.UTF8.GetBytes($"{at} ERROR indexer failed <b>twice</b>\n{at} all fine\n");
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body);
        });

        var directory = TestHost.TempDirectory();
        var host = TestHost.ReadyHost(directory);
        try
        {
            var config = host.GetRequiredService<ConfigStore>();
            var sonarr = new Connection { Name = "Sonarr", Provider = "sonarr", Settings = new SettingsBag { ["url"] = "http://sonarr:8989" } };
            await config.SaveConnectionAsync(sonarr);
            await config.SaveConnectionAsync(
                new Connection { Name = "NAS Docker", Provider = "docker", Settings = new SettingsBag { ["endpoint"] = docker.Endpoint } });
            var incident = await host.GetRequiredService<IncidentStore>().SaveAsync(new Incident(0, started, ended, ended, false,
                [new IncidentMember(IncidentMember.StatusKey(sonarr.Id), ChangeKinds.Status, sonarr.Id, "Sonarr", started, ended)]));

            await using var services = PageServices(host, "http://labby.test/" + LogSearch.IncidentLink(incident.Id));
            using var renderer = new InteractiveRenderer(services);
            await renderer.RenderAsync<LogsPage>(new Dictionary<string, object?>());
            static string Text(string h) => System.Text.RegularExpressions.Regex.Replace(WebUtility.HtmlDecode(h), @"\s+", " ");
            var html = Text(await renderer.WaitForAsync(h => Text(h).Contains("· done ·", StringComparison.Ordinal), TimeSpan.FromSeconds(20)));
            Assert.Contains("· done ·", html);

            Assert.Contains("Around the incident “Sonarr”", html);
            Assert.Contains("media-sonarr-1", html);
            Assert.Contains("in 1 of 1 container", html);

            // Only the container Sonarr is reached through, over the timeline's window.
            var request = Assert.Single(docker.Requests, r => r.Contains("/logs", StringComparison.Ordinal));
            Assert.Contains("/containers/sonarr1/logs", request);
            Assert.Contains($"since={DockerLogs.UnixTime(started - IncidentRules.ContextBefore)}", request);
            Assert.Contains($"until={DockerLogs.UnixTime(ended + IncidentRules.ContextAfter)}", request);

            // The match is marked, and the line's own markup stays text.
            var raw = await renderer.HtmlAsync();
            Assert.Contains("<mark>ERROR</mark>", raw);
            Assert.Contains("&lt;b&gt;twice&lt;/b&gt;", raw);
            Assert.DoesNotContain("all fine", raw);
        }
        finally
        {
            TestHost.Teardown(host, directory);
        }
    }

    /// <summary>The highlighter on its own, as a component the HTML renderer can draw.</summary>
    public sealed class Highlighted : ComponentBase
    {
        [Parameter] public string Text { get; set; } = "";
        [Parameter] public IReadOnlyList<(int Start, int Length)> Spans { get; set; } = [];

        protected override void BuildRenderTree(RenderTreeBuilder builder) => LogHighlight.Build(builder, Text, Spans);
    }
}
