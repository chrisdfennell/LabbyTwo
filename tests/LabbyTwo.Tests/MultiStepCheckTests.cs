using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LabbyTwo.Core;
using LabbyTwo.Providers;
using Microsoft.Extensions.Logging;

namespace LabbyTwo.Tests;

/// <summary>
/// A tiny web app on the loopback interface for multi-step checks to walk: every request is
/// handed to <see cref="Handle"/> on its own task, so a slow answer does not hold up the next.
/// No internet anywhere — every address is 127.0.0.1.
/// </summary>
internal sealed class FakeApp : IDisposable
{
    private readonly HttpListener _listener;

    public FakeApp(Func<HttpListenerContext, string, Task> handle)
    {
        _listener = LoopbackListener.Start(out var port);
        Base = $"http://127.0.0.1:{port}";
        Handle = handle;
        _ = Task.Run(LoopAsync);
    }

    public string Base { get; }

    public Func<HttpListenerContext, string, Task> Handle { get; }

    /// <summary>"METHOD /path" for every request, in the order they arrived.</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch
            {
                return;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    Requests.Enqueue($"{context.Request.HttpMethod} {context.Request.Url!.AbsolutePath}");
                    await Handle(context, body);
                }
                catch
                {
                    // The client gave up — a timeout test, usually.
                }
                finally
                {
                    try
                    {
                        context.Response.Close();
                    }
                    catch
                    {
                        // Already gone.
                    }
                }
            });
        }
    }

    public static async Task WriteAsync(HttpListenerContext context, string body, int status = 200, string type = "text/html")
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = type;
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
    }
}

/// <summary>Every line a logger was asked to write, for proving a secret never reached one.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Lines.Enqueue(formatter(state, exception) + (exception is null ? "" : " " + exception));
}

/// <summary>
/// Multi-step checks against a real HTTP server: a login whose session cookie carries
/// over, a token pulled out of one answer into the next request's header, JSON and regex
/// assertions, failures that say which step and what, and the things a check must never
/// do — show a secret, reach the cloud metadata address, read an endless body, or overrun
/// the monitor's deadline.
/// </summary>
public sealed class MultiStepCheckTests
{
    private const string Password = "hunter2-\"quoted\"&more";

    private readonly CapturingLogger<ProviderHttpLogger> _log = new();

    private MultiStepCheckProvider Provider() => new(_log);

    private static SettingsBag Settings(IEnumerable<CheckStep> steps, IReadOnlyDictionary<string, string>? secrets = null,
        params (string Key, string Value)[] extra)
    {
        var bag = new SettingsBag
        {
            ["steps"] = StepCheck.Serialize(steps),
            ["secrets"] = StepCheck.SerializeSecrets(secrets ?? new Dictionary<string, string>()),
        };
        foreach (var (key, value) in extra)
            bag[key] = value;
        return bag;
    }

    private static Connection Connection(SettingsBag settings) => new() { Provider = "steps", Name = "App", Settings = settings };

    // ---- a login, start to finish -----------------------------------------------------

    private static FakeApp LoginApp() => new(async (context, body) =>
    {
        var path = context.Request.Url!.AbsolutePath;
        var cookie = context.Request.Cookies["sid"]?.Value;
        switch (context.Request.HttpMethod, path)
        {
            case ("GET", "/login"):
                await FakeApp.WriteAsync(context, "<form><input type=\"hidden\" name=\"csrf_token\" value=\"tok-42\"></form>");
                return;
            case ("POST", "/login"):
            {
                var form = System.Web.HttpUtility.ParseQueryString(body);
                if (form["csrf_token"] != "tok-42" || form["password"] != Password || form["username"] != "monitor")
                {
                    await FakeApp.WriteAsync(context, "Invalid password", 401);
                    return;
                }
                context.Response.AppendHeader("Set-Cookie", "sid=session-abcdef123; Path=/; HttpOnly");
                context.Response.StatusCode = 302;
                context.Response.RedirectLocation = "/dashboard";
                return;
            }
            case ("GET", "/dashboard"):
                if (cookie == "session-abcdef123")
                    await FakeApp.WriteAsync(context, "<h1>Welcome back</h1><a>Log out</a><li>report.pdf</li>");
                else
                    await FakeApp.WriteAsync(context, "Please log in", 403);
                return;
            default:
                await FakeApp.WriteAsync(context, "Not here", 404);
                return;
        }
    });

    private static List<CheckStep> LoginSteps(string root) =>
    [
        new()
        {
            Name = "Open the login page", Url = $"{root}/login",
            Checks = [new() { Kind = StepAssertKind.Status, Value = "200" }],
            Save = [new() { Name = "csrf", From = StepExtractFrom.Regex, Expression = "name=\"csrf_token\" value=\"([^\"]+)\"" }],
        },
        new()
        {
            Name = "Log in", Method = "POST", Url = $"{root}/login", BodyKind = StepBodyKind.Form,
            Body = "username=monitor\npassword=${secret:password}\ncsrf_token=${csrf}",
            Checks = [new() { Kind = StepAssertKind.BodyContains, Value = "Welcome" }],
            Save = [new() { Name = "sid", From = StepExtractFrom.Cookie, Expression = "sid" }],
        },
        new()
        {
            Name = "Open the dashboard", Url = $"{root}/dashboard",
            Headers = "X-Session-Echo: ${sid}",
            Checks =
            [
                new() { Kind = StepAssertKind.Status, Value = "2xx" },
                new() { Kind = StepAssertKind.BodyMatches, Value = @"Welcome \w+" },
                new() { Kind = StepAssertKind.TimeUnder, Value = "5000" },
            ],
        },
    ];

    [Fact]
    public async Task A_login_carries_its_session_cookie_through_a_redirect_to_the_next_steps()
    {
        using var app = LoginApp();
        var settings = Settings(LoginSteps(app.Base), new Dictionary<string, string> { ["password"] = Password });

        var result = await Provider().ProbeAsync(Connection(settings), CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("All 3 steps passed.", result.Message);
        Assert.Equal(3, result.Metrics!["steps_passed"]);
        Assert.Equal(0, result.Metrics["failed_step"]);
        Assert.True(result.Metrics["total_ms"] > 0);
        Assert.Contains("step_ms:open_the_login_page", result.Metrics.Keys);
        Assert.Contains("step_ms:log_in", result.Metrics.Keys);
        Assert.Contains("step_ms:open_the_dashboard", result.Metrics.Keys);
        Assert.Equal(["GET /login", "POST /login", "GET /dashboard", "GET /dashboard"], app.Requests.ToArray());
        Assert.StartsWith("HTTP 200 in ", result.Details!["Step 2 · Log in"]);
        Assert.EndsWith("passed", result.Details["Step 3 · Open the dashboard"]);
    }

    [Fact]
    public async Task Each_run_starts_with_an_empty_cookie_jar()
    {
        using var app = LoginApp();
        var provider = Provider();
        var login = Settings(LoginSteps(app.Base), new Dictionary<string, string> { ["password"] = Password });
        Assert.True((await provider.ProbeAsync(Connection(login), CancellationToken.None)).Ok);

        // The same provider, the dashboard alone: last run's session must not leak in.
        var alone = Settings([new CheckStep { Name = "Dashboard", Url = $"{app.Base}/dashboard" }]);
        var result = await provider.ProbeAsync(Connection(alone), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("expected a response below 400 — got HTTP 403", result.Message);
    }

    [Fact]
    public async Task With_redirects_off_a_step_can_assert_on_the_redirect_itself()
    {
        using var app = LoginApp();
        var steps = LoginSteps(app.Base).Take(2).ToList();
        steps[1].Checks =
        [
            new() { Kind = StepAssertKind.Status, Value = "302" },
            new() { Kind = StepAssertKind.HeaderEquals, Target = "location", Value = "/dashboard" },
            new() { Kind = StepAssertKind.HeaderPresent, Target = "Set-Cookie" },
        ];
        var settings = Settings(steps, new Dictionary<string, string> { ["password"] = Password }, ("follow_redirects", "false"));

        var result = await Provider().ProbeAsync(Connection(settings), CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(["GET /login", "POST /login"], app.Requests.ToArray());
    }

    // ---- an API with a token ----------------------------------------------------------

    private static FakeApp ApiApp() => new(async (context, body) =>
    {
        var path = context.Request.Url!.AbsolutePath;
        if (path == "/token")
        {
            // The password has a quote and an ampersand in it: it only arrives intact if it
            // was JSON-escaped on the way into the body.
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.GetProperty("password").GetString() != Password)
            {
                await FakeApp.WriteAsync(context, "{\"error\":\"bad password\"}", 401, "application/json");
                return;
            }
            await FakeApp.WriteAsync(context, "{\"access_token\":\"eyJ-token-0123456789\",\"expires\":3600}", 200, "application/json");
            return;
        }
        if (context.Request.Headers["Authorization"] != "Bearer eyJ-token-0123456789")
        {
            await FakeApp.WriteAsync(context, "{\"error\":\"no token\"}", 401, "application/json");
            return;
        }
        context.Response.AppendHeader("X-Version", "4.2");
        await FakeApp.WriteAsync(context,
            "{\"status\":\"ok\",\"healthy\":true,\"queue\":3,\"disks\":[{\"name\":\"ssd\",\"used\":41.5}]}", 200, "application/json");
    });

    private static List<CheckStep> ApiSteps(string root) =>
    [
        new()
        {
            Name = "Get a token", Method = "POST", Url = $"{root}/token", BodyKind = StepBodyKind.Json,
            Body = "{\"username\":\"monitor\",\"password\":\"${secret:password}\"}",
            Checks = [new() { Kind = StepAssertKind.JsonExists, Target = "access_token" }],
            Save = [new() { Name = "token", From = StepExtractFrom.Json, Expression = "access_token" }],
        },
        new()
        {
            Name = "Check the status", Url = $"{root}/status",
            Headers = "Authorization: Bearer ${token}",
            Checks =
            [
                new() { Kind = StepAssertKind.Status, Value = "200-299" },
                new() { Kind = StepAssertKind.JsonEquals, Target = "status", Value = "ok" },
                new() { Kind = StepAssertKind.JsonEquals, Target = "healthy", Value = "true" },
                new() { Kind = StepAssertKind.JsonEquals, Target = "disks[0].used", Value = "41.5" },
                new() { Kind = StepAssertKind.JsonEquals, Target = "disks[0].name", Value = "ssd" },
                new() { Kind = StepAssertKind.HeaderEquals, Target = "X-Version", Value = "4.2" },
                new() { Kind = StepAssertKind.BodyMatches, Value = "\"queue\":\\s*\\d+" },
            ],
        },
    ];

    [Fact]
    public async Task A_token_saved_from_one_answer_goes_into_the_next_requests_header()
    {
        using var app = ApiApp();
        var settings = Settings(ApiSteps(app.Base), new Dictionary<string, string> { ["password"] = Password });

        var result = await Provider().ProbeAsync(Connection(settings), CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(2, result.Metrics!["steps_passed"]);
    }

    [Fact]
    public async Task A_json_value_that_differs_names_the_path_what_was_expected_and_what_came_back()
    {
        using var app = ApiApp();
        var steps = ApiSteps(app.Base);
        steps[1].Checks.Insert(1, new() { Kind = StepAssertKind.JsonEquals, Target = "queue", Value = "0" });
        var settings = Settings(steps, new Dictionary<string, string> { ["password"] = Password });

        var result = await Provider().ProbeAsync(Connection(settings), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Matches(@"^Step 2 'Check the status': expected 'queue' to be '0' — it was '3' \(HTTP 200, \d+ ms\)$", result.Message);
        Assert.Equal(2, result.Metrics!["failed_step"]);
        Assert.Equal(1, result.Metrics["steps_passed"]);
    }

    // ---- failures in plain English ----------------------------------------------------

    [Fact]
    public async Task A_failing_step_is_named_with_its_assertion_and_the_steps_after_it_are_not_run()
    {
        using var app = new FakeApp((context, _) => FakeApp.WriteAsync(context, "<li>notes.txt</li>"));
        var settings = Settings(
        [
            new CheckStep { Name = "Home", Url = $"{app.Base}/" },
            new CheckStep
            {
                Name = "Open the file", Url = $"{app.Base}/files",
                Checks = [new() { Kind = StepAssertKind.BodyContains, Value = "report.pdf" }],
            },
            new CheckStep { Name = "Log out", Url = $"{app.Base}/logout" },
        ]);

        var result = await Provider().ProbeAsync(Connection(settings), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Matches(@"^Step 2 'Open the file': expected the body to contain 'report\.pdf' — it didn't \(HTTP 200, \d+ ms\)$", result.Message);
        Assert.Equal(2, result.Metrics!["failed_step"]);
        Assert.Equal(1, result.Metrics["steps_passed"]);
        Assert.Equal("not run", result.Details!["Step 3 · Log out"]);
        Assert.DoesNotContain("step_ms:log_out", result.Metrics.Keys);
        Assert.Equal(["GET /", "GET /files"], app.Requests.ToArray());
    }

    [Theory]
    [InlineData("${token}", "uses ${token}, which no earlier step saved")]
    [InlineData("${secret:missing}", "uses ${secret:missing}, which is not one of this check's secrets")]
    [InlineData("${env:HOME}", "cannot read LabbyTwo's environment")]
    public async Task A_reference_that_cannot_be_filled_in_fails_before_anything_is_sent(string reference, string expected)
    {
        using var app = new FakeApp((context, _) => FakeApp.WriteAsync(context, "ok"));
        var settings = Settings([new CheckStep { Name = "Call", Url = $"{app.Base}/?x={reference}" }]);

        var result = await Provider().ProbeAsync(Connection(settings), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.StartsWith("Step 1 'Call' ", result.Message);
        Assert.Contains(expected, result.Message);
        Assert.Empty(app.Requests);
    }

    [Fact]
    public async Task A_value_that_cannot_be_found_to_save_fails_the_step_that_should_have_had_it()
    {
        using var app = new FakeApp((context, _) => FakeApp.WriteAsync(context, "{\"other\":1}", 200, "application/json"));
        var settings = Settings(
        [
            new CheckStep
            {
                Name = "Log in", Url = $"{app.Base}/",
                Save = [new() { Name = "token", From = StepExtractFrom.Json, Expression = "access_token" }],
            },
        ]);

        var result = await Provider().ProbeAsync(Connection(settings), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("couldn't find a value for 'token' (JSON path 'access_token') in the response", result.Message);
    }

    // ---- secrets stay secret ----------------------------------------------------------

    [Fact]
    public async Task A_secret_never_appears_in_the_message_the_details_the_run_now_output_or_the_log()
    {
        // An app that echoes everything it is sent — the worst case for a secret.
        using var app = new FakeApp(async (context, body) =>
        {
            context.Response.AppendHeader("X-Echo", context.Request.Headers["X-Key"] ?? "");
            context.Response.AppendHeader("Set-Cookie", "sid=cookie-value-987654; Path=/");
            await FakeApp.WriteAsync(context,
                $"you sent {body} and {context.Request.Url} with key {context.Request.Headers["X-Key"]} " +
                $"and token {context.Request.Headers["X-Token"]}");
        });
        var secrets = new Dictionary<string, string> { ["password"] = Password, ["key"] = "sk-live-abcdef" };
        var settings = Settings(
        [
            new CheckStep
            {
                Name = "Echo", Method = "POST", Url = $"{app.Base}/echo?key=${{secret:key}}",
                Headers = "X-Key: ${secret:key}", BodyKind = StepBodyKind.Form, Body = "password=${secret:password}",
                Save = [new() { Name = "token", From = StepExtractFrom.Regex, Expression = "sent (password=[^ ]+)" }],
            },
            new CheckStep
            {
                Name = "Again", Url = $"{app.Base}/again", Headers = "X-Token: ${token}",
                Checks = [new() { Kind = StepAssertKind.BodyContains, Value = "not there ${secret:password}" }],
            },
        ], secrets);

        var provider = Provider();
        var run = await provider.RunAsync(settings, trace: true, CancellationToken.None);
        var probe = run.ToProbeResult();

        Assert.False(run.Ok);
        var everything = new StringBuilder()
            .AppendLine(run.Message)
            .AppendJoin('\n', probe.Details!.Select(d => d.Key + d.Value)).AppendLine();
        foreach (var step in run.Steps)
        {
            everything.AppendLine(step.Url).AppendLine(step.Failure).AppendLine(step.BodySample)
                .AppendJoin('\n', step.RequestHeaders.Select(h => h.Name + h.Value)).AppendLine()
                .AppendJoin('\n', step.ResponseHeaders.Select(h => h.Name + h.Value)).AppendLine()
                .AppendJoin('\n', step.Saved).AppendLine();
        }
        everything.AppendJoin('\n', _log.Lines);
        var text = everything.ToString();

        Assert.Contains("you sent", text);   // the body sample is there…
        Assert.Contains(StepMasker.Mask, text); // …with the secrets taken out
        foreach (var secret in new[]
                 {
                     Password, Uri.EscapeDataString(Password), WebUtility.UrlEncode(Password), "sk-live-abcdef",
                     "cookie-value-987654", "hunter2",
                 })
            Assert.DoesNotContain(secret, text);
        Assert.NotEmpty(_log.Lines);
        Assert.Contains("token: found (", string.Join('\n', run.Steps[0].Saved));
    }

    [Fact]
    public void A_masker_takes_every_spelling_of_a_secret_out_and_leaves_short_ones_alone()
    {
        var masker = new StepMasker();
        masker.Add("p@ss word\"");
        masker.Add("ab");

        Assert.Equal("a=•••• b=•••• c=•••• ab", masker.Apply("a=p@ss word\" b=p%40ss%20word%22 c=p@ss word\\u0022 ab"));
    }

    // ---- what a check may reach -------------------------------------------------------

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/", "link-local")]
    [InlineData("http://[fe80::1]/", "link-local")]
    [InlineData("file:///etc/passwd", "only http:// and https://")]
    [InlineData("ftp://nas.lan/", "only http:// and https://")]
    [InlineData("not a url", "is not an address this can use")]
    public async Task Addresses_a_check_must_not_reach_are_refused(string url, string expected)
    {
        var result = await Provider().ProbeAsync(Connection(Settings([new CheckStep { Name = "Bad", Url = url }])), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.StartsWith("Step 1 'Bad': ", result.Message);
        Assert.Contains(expected, result.Message);
    }

    [Fact]
    public async Task A_name_that_resolves_to_the_metadata_address_is_refused_too()
    {
        var provider = Provider();
        provider.Resolve = (_, _) => Task.FromResult(new[] { IPAddress.Parse("169.254.169.254") });

        var result = await provider.ProbeAsync(
            Connection(Settings([new CheckStep { Name = "Sneaky", Url = "http://metadata.lan/" }])), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("metadata.lan resolves to 169.254.169.254, a link-local address", result.Message);
    }

    [Fact]
    public async Task A_redirect_to_the_metadata_address_is_refused()
    {
        using var app = new FakeApp((context, _) =>
        {
            context.Response.StatusCode = 302;
            context.Response.RedirectLocation = "http://169.254.169.254/latest/meta-data/iam/";
            return Task.CompletedTask;
        });

        var result = await Provider().ProbeAsync(
            Connection(Settings([new CheckStep { Name = "Hop", Url = $"{app.Base}/" }])), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("169.254.169.254 is a link-local address", result.Message);
    }

    [Fact]
    public async Task Only_the_first_megabyte_of_a_huge_body_is_read()
    {
        using var app = new FakeApp(async (context, _) =>
        {
            context.Response.ContentType = "text/plain";
            context.Response.SendChunked = true;
            var chunk = Encoding.ASCII.GetBytes(new string('a', 64 * 1024));
            await context.Response.OutputStream.WriteAsync(Encoding.ASCII.GetBytes("START "));
            for (var i = 0; i < 48; i++)   // three megabytes
                await context.Response.OutputStream.WriteAsync(chunk);
            await context.Response.OutputStream.WriteAsync(Encoding.ASCII.GetBytes(" needle-at-the-end"));
        });
        var step = new CheckStep
        {
            Name = "Big", Url = $"{app.Base}/",
            Checks = [new() { Kind = StepAssertKind.BodyContains, Value = "START" }],
        };

        var run = await Provider().RunAsync(Settings([step]), trace: true, CancellationToken.None);
        Assert.True(run.Ok, run.Message);
        Assert.True(run.Steps[0].Truncated);
        Assert.Equal(MultiStepCheckProvider.MaxBodyBytes, run.Steps[0].BodyLength);
        Assert.Equal(MultiStepCheckProvider.SampleBytes, run.Steps[0].BodySample.Length);

        step.Checks.Add(new() { Kind = StepAssertKind.BodyContains, Value = "needle-at-the-end" });
        run = await Provider().RunAsync(Settings([step]), trace: false, CancellationToken.None);
        Assert.False(run.Ok);
        Assert.Contains("it didn't (only the first part of a very large response was read)", run.Message);
    }

    // ---- inside the deadline ----------------------------------------------------------

    [Fact]
    public async Task A_step_that_does_not_answer_stops_at_its_own_timeout_and_says_so()
    {
        using var app = new FakeApp(async (context, _) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(8));
            await FakeApp.WriteAsync(context, "late");
        });
        var settings = Settings([new CheckStep { Name = "Slow", Url = $"{app.Base}/", TimeoutSeconds = 1 }]);

        var started = DateTime.UtcNow;
        using var capture = ProbeError.Capture();
        var result = await Provider().ProbeAsync(Connection(settings), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.StartsWith("Step 1 'Slow': no answer within 1 s.", result.Message);
        Assert.Equal(ProbeFailure.Timeout, capture.Kind);
        Assert.Equal(1, result.Metrics!["failed_step"]);
    }

    [Fact]
    public async Task The_whole_check_stops_at_its_budget_whatever_each_step_allows()
    {
        using var app = new FakeApp(async (context, _) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1300));
            await FakeApp.WriteAsync(context, "ok");
        });
        var settings = Settings(
        [
            new CheckStep { Name = "One", Url = $"{app.Base}/1" },
            new CheckStep { Name = "Two", Url = $"{app.Base}/2" },
            new CheckStep { Name = "Three", Url = $"{app.Base}/3" },
        ], extra: [("budget", "2"), ("step_timeout", "10")]);

        var started = DateTime.UtcNow;
        var result = await Provider().ProbeAsync(Connection(settings), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(4));
        Assert.StartsWith("Step 2 'Two': the whole check ran out of its 2 s budget.", result.Message);
    }

    [Fact]
    public void The_budget_can_never_reach_the_monitors_probe_deadline()
    {
        // With room to spare: the step that runs out reports itself, rather than the monitor
        // abandoning the whole probe with a message that names no step.
        Assert.True(MultiStepCheckProvider.MaxBudget + TimeSpan.FromSeconds(3) < LabbyTwo.Services.HealthMonitor.DefaultProbeDeadline);
    }

    // ---- DNS failures are DNS failures ------------------------------------------------

    [Fact]
    public async Task A_step_whose_host_will_not_resolve_is_classified_as_dns_so_blindness_sees_it()
    {
        var provider = Provider();
        provider.Resolve = (_, _) => throw new SocketException((int)SocketError.TryAgain);
        var settings = Settings([new CheckStep { Name = "Log in", Url = "http://app.lan/login" }]);

        using var capture = ProbeError.Capture();
        var result = await provider.ProbeAsync(Connection(settings), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.StartsWith("Step 1 'Log in': DNS lookup failed for now", result.Message);
        Assert.Equal(ProbeFailure.Dns, capture.Kind);
        Assert.True(capture.Kind.IsBlind());
        Assert.Equal(ProbeFailure.Dns, ProbeError.ClassifyMessage(result.Message));
    }

    [Fact]
    public async Task A_name_that_does_not_exist_is_dns_as_well()
    {
        var provider = Provider();
        provider.Resolve = (_, _) => throw new SocketException((int)SocketError.HostNotFound);

        using var capture = ProbeError.Capture();
        var result = await provider.ProbeAsync(
            Connection(Settings([new CheckStep { Name = "Home", Url = "http://nowhere.lan/" }])), CancellationToken.None);

        Assert.Contains("Could not resolve the host", result.Message);
        Assert.Equal(ProbeFailure.Dns, capture.Kind);
    }

    // ---- the pure parts ---------------------------------------------------------------

    [Theory]
    [InlineData("200", 200, true)]
    [InlineData("200", 201, false)]
    [InlineData("2xx", 204, true)]
    [InlineData("200-299", 302, false)]
    [InlineData("200-399", 302, true)]
    [InlineData("200, 207", 207, true)]
    public void Status_specs_read_the_ways_people_write_them(string spec, int status, bool expected) =>
        Assert.Equal(expected, StepCheck.StatusMatches(spec, status));

    [Fact]
    public void A_status_spec_that_makes_no_sense_says_so_rather_than_failing_quietly() =>
        Assert.Null(StepCheck.StatusMatches("ok", 200));

    [Fact]
    public void Variables_fill_in_and_a_double_dollar_is_literal()
    {
        var variables = new StepVariables(new Dictionary<string, string> { ["pw"] = "a\"b" });
        variables.Set("token", "t1");

        Assert.Equal("Bearer t1 / a\"b / ${x}", StepCheck.Substitute("Bearer ${token} / ${secret:pw} / $${x}", variables));
        Assert.Equal("{\"p\":\"a\\u0022b\"}", StepCheck.Substitute("{\"p\":\"${secret:pw}\"}", variables,
            v => System.Text.Json.JsonEncodedText.Encode(v).ToString()));
    }

    [Fact]
    public void Two_steps_with_the_same_name_get_different_metric_keys()
    {
        var keys = StepCheck.StepMetricKeys([new CheckStep { Name = "Open page" }, new CheckStep { Name = "Open page" }, new CheckStep()]);
        Assert.Equal(["step_ms:open_page", "step_ms:open_page_2", "step_ms:step_3"], keys);
    }

    [Fact]
    public void Every_example_round_trips_and_only_uses_values_it_saves()
    {
        Assert.Equal(StepCheck.Templates.Count, StepCheck.Templates.Select(t => t.Id).Distinct().Count());
        foreach (var template in StepCheck.Templates)
        {
            var copy = template.Copy();
            Assert.Equal(StepCheck.Serialize(template.Steps), StepCheck.Serialize(copy));
            var saved = new HashSet<string>();
            foreach (var step in copy)
            {
                foreach (var reference in new[] { step.Url, step.Headers, step.Body, step.BasicPassword }.SelectMany(StepCheck.References))
                {
                    if (reference.StartsWith("secret:"))
                        Assert.Contains(reference["secret:".Length..], template.Secrets);
                    else
                        Assert.Contains(reference, saved);
                }
                foreach (var save in step.Save)
                    saved.Add(save.Name);
            }
        }
    }

    [Fact]
    public void Steps_that_cannot_be_read_are_no_steps_rather_than_an_exception()
    {
        Assert.Empty(StepCheck.Parse("{not json"));
        Assert.Empty(StepCheck.ParseSecrets("[1,2]"));
    }

    [Fact]
    public async Task An_empty_check_says_what_to_do()
    {
        var result = await Provider().ProbeAsync(Connection(Settings([])), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal("No steps yet — add at least one.", result.Message);
    }

    [Fact]
    public void The_secrets_field_is_encrypted_like_a_password_and_metrics_name_each_step()
    {
        var provider = Provider();
        Assert.True(provider.Fields.Single(f => f.Key == "secrets").IsSecret);
        Assert.False(provider.Fields.Single(f => f.Key == "steps").IsSecret);

        var metrics = provider.MetricsFor(Connection(Settings([new CheckStep { Name = "Log in" }])));
        Assert.Contains(metrics, m => m.Key == "step_ms:log_in" && m.Label == "Step 1: Log in");
        Assert.Contains(metrics, m => m.Key == "failed_step");
    }
}
