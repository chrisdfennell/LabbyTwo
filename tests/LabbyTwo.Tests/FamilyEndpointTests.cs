using System.Net;
using System.Text.RegularExpressions;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace LabbyTwo.Tests;

/// <summary>
/// The family status page through the real app, with a password set: the link check, what
/// the page does and does not say, the report form with its antiforgery token, the rate
/// limits, and — the point of all of it — that the link opens this page and nothing else.
/// </summary>
public sealed partial class FamilyEndpointTests : IDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly LoginThrottleTests.ManualClock _clock = new();
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    /// <summary>A connection on the page, whose own name and address must never be.</summary>
    private static readonly Connection Plex = new()
    {
        Id = "conn-plex-7f2a", Provider = "http", Name = "plex-docker-01",
        Settings = new SettingsBag { ["url"] = "http://127.0.0.1:1/internal-plex-path" },
    };

    /// <summary>A connection not on the page at all.</summary>
    private static readonly Connection Hidden = new()
    {
        Id = "conn-hidden-91c", Provider = "http", Name = "Hidden-NAS-9f3",
        Settings = new SettingsBag { ["url"] = "http://127.0.0.1:1/hidden-nas" },
    };

    public void Dispose()
    {
        foreach (var factory in _factories)
            factory.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // SQLite's pool can hold the file a moment longer; a temp folder left behind is harmless.
        }
    }

    private sealed record App(WebApplicationFactory<Program> Factory, HttpClient Client)
    {
        public T Get<T>() where T : notnull => Factory.Services.GetRequiredService<T>();
    }

    private async Task<(App App, string Token)> StartAsync(
        string socket = "203.0.113.50", FamilyThrottle.Limits? limits = null, bool lan = false)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseSetting("Labby:Auth:Username", "labby");
            host.UseSetting("Labby:Auth:Password", "correct horse battery staple");
            host.UseSetting("Labby:DatabasePath", Path.Combine(_directory, Guid.NewGuid().ToString("n"), "labbytwo.db"));
            host.UseSetting("Labby:PluginPath", Path.Combine(_directory, "plugins"));
            host.ConfigureTestServices(services =>
            {
                services.RemoveAll<FamilyThrottle>();
                services.AddSingleton(new FamilyThrottle(_clock, limits));
                services.AddSingleton<IStartupFilter>(new SocketAddress(IPAddress.Parse(socket)));
            });
        });
        _factories.Add(factory);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var app = new App(factory, client);

        var config = app.Get<ConfigStore>();
        await config.SaveConnectionAsync(Plex);
        await config.SaveConnectionAsync(Hidden);

        var token = FamilyStatusSettings.NewToken();
        await app.Get<FamilyStatus>().SaveAsync(new FamilyStatusSettings(true, token, lan, "",
            [new FamilyItem("item-tv", Plex.Id, "Plex", "📺", "TV & Movies")]));
        return (app, token);
    }

    private sealed class SocketAddress(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, following) =>
            {
                context.Connection.RemoteIpAddress = address;
                return following(context);
            });
            next(app);
        };
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryField();

    private static async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> GetAsync(
        HttpClient client, string path, string? forwardedFor = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (forwardedFor is not null)
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        var response = await client.SendAsync(request);
        return (response.StatusCode, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()), response);
    }

    private static async Task<HttpResponseMessage> ReportAsync(
        HttpClient client, string basePath, string what, string message, string name, bool withToken = true)
    {
        var form = await client.GetStringAsync($"{basePath}/report");
        var token = AntiforgeryField().Match(form);
        Assert.True(token.Success, "The report form has no antiforgery field.");

        var fields = new Dictionary<string, string> { ["what"] = what, ["message"] = message, ["name"] = name };
        if (withToken)
            fields["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value);
        return await client.PostAsync($"{basePath}/report", new FormUrlEncodedContent(fields));
    }

    [Fact]
    public async Task The_link_opens_the_page_without_signing_in_and_shows_only_what_was_chosen()
    {
        var (app, token) = await StartAsync();

        var (status, body, response) = await GetAsync(app.Client, $"/family/{token}");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Plex", body);
        Assert.Contains("TV & Movies", body);
        Assert.Contains("📺", body);
        Assert.Contains("Something's broken?", body);

        // Nothing the owner did not choose, and nothing internal about what they did.
        foreach (var secret in new[]
                 {
                     Hidden.Name, Hidden.Id, "hidden-nas", Plex.Name, Plex.Id, "127.0.0.1", "internal-plex-path",
                     "refused", "Exception", "http://",
                 })
        {
            Assert.DoesNotContain(secret, body, StringComparison.OrdinalIgnoreCase);
        }

        // Static: no Blazor script, so no circuit to anything.
        Assert.DoesNotContain("blazor", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", body, StringComparison.OrdinalIgnoreCase);

        // A secret in the URL: not cached, not framed, never passed on as a referrer.
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("http-equiv=\"refresh\"", body);

        // The two stylesheets it links are open to a visitor who is not signed in.
        foreach (var sheet in new[] { "/app.css", "/family.css" })
            Assert.Equal(HttpStatusCode.OK, (await GetAsync(app.Client, sheet)).Status);
    }

    /// <summary>
    /// The household sees the owner's theme, and so does every page of the app — the theme
    /// block is in the head of both, built from memory. A theme's export is behind the login.
    /// </summary>
    [Fact]
    public async Task The_family_page_and_the_app_wear_the_owners_theme()
    {
        var (app, token) = await StartAsync();
        await app.Get<ThemeService>().ApplyAsync(BuiltInThemes.Dracula.Id);

        var (_, family, _) = await GetAsync(app.Client, $"/family/{token}");
        Assert.Contains("--ink: #21222c;", family);
        Assert.Contains("data-theme=\"dark\"", family);
        Assert.Contains("content=\"#21222c\"", family);

        var (status, login, _) = await GetAsync(app.Client, "/login");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("<style id=\"labby-theme\">", login);
        Assert.Contains("--ink: #21222c;", login);
        // Dracula has no light end, so the page is stamped dark whatever the setting says.
        Assert.Contains("data-theme=\"dark\"", login);
        Assert.Contains("data-bs-theme=\"dark\"", login);

        var (export, _, _) = await GetAsync(app.Client, "/api/share/theme?id=nord");
        Assert.NotEqual(HttpStatusCode.OK, export);
    }

    [Fact]
    public async Task A_wrong_link_a_switched_off_page_and_an_old_link_are_all_the_same_404()
    {
        var (app, token) = await StartAsync();

        var (wrong, wrongBody, _) = await GetAsync(app.Client, $"/family/{FamilyStatusSettings.NewToken()}");
        Assert.Equal(HttpStatusCode.NotFound, wrong);
        Assert.DoesNotContain("Plex", wrongBody);

        // A new link takes the old one back at once.
        var family = app.Get<FamilyStatus>();
        var settings = await family.SettingsAsync();
        var fresh = FamilyStatusSettings.NewToken();
        await family.SaveAsync(settings with { Token = fresh });

        var (old, oldBody, _) = await GetAsync(app.Client, $"/family/{token}");
        Assert.Equal(HttpStatusCode.NotFound, old);
        Assert.Equal(wrongBody, oldBody);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(app.Client, $"/family/{fresh}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(app.Client, $"/family/{token}/report")).Status);

        // Switched off: even the right link is not found.
        await family.SaveAsync(settings with { Token = fresh, Enabled = false });
        var (off, offBody, _) = await GetAsync(app.Client, $"/family/{fresh}");
        Assert.Equal(HttpStatusCode.NotFound, off);
        Assert.Equal(wrongBody, offBody);
    }

    [Fact]
    public async Task The_link_opens_nothing_else_in_the_app()
    {
        var (app, token) = await StartAsync();

        // Visiting the page first, so any cookie it might set is in the client's jar.
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(app.Client, $"/family/{token}")).Status);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(app.Client, $"/family/{token}/report")).Status);

        foreach (var path in new[]
                 {
                     "/", "/settings", "/settings/connections", "/changes", "/incidents",
                     "/api/export", "/api/backup", "/api/health/details", $"/status/{token}",
                     $"/settings?token={token}", $"/family/{token}/../settings",
                 })
        {
            var (status, body, response) = await GetAsync(app.Client, path);
            var redirectedToLogin = (int)status is >= 300 and < 400
                && response.Headers.Location?.ToString().Contains("login", StringComparison.OrdinalIgnoreCase) == true;
            Assert.True(redirectedToLogin || status is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound,
                $"{path} answered {(int)status} to an anonymous visitor holding the family link.");
            Assert.DoesNotContain(Hidden.Name, body);
        }
    }

    [Fact]
    public async Task The_home_network_address_is_off_by_default_and_refuses_anything_proxied()
    {
        var (lanOff, _) = await StartAsync(socket: "192.168.1.40");
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(lanOff.Client, "/family")).Status);

        var (lan, _) = await StartAsync(socket: "192.168.1.40", lan: true);
        var (ok, body, _) = await GetAsync(lan.Client, "/family");
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Contains("Plex", body);

        // Through a proxy that is not trusted — a tunnel on the LAN, say — the socket is
        // private but the visitor is not.
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(lan.Client, "/family", forwardedFor: "203.0.113.7")).Status);

        var (internet, _) = await StartAsync(socket: "203.0.113.50", lan: true);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(internet.Client, "/family")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(internet.Client, "/family/report")).Status);
    }

    [Fact]
    public async Task A_report_is_kept_as_text_goes_in_the_feed_and_is_never_echoed()
    {
        var (app, token) = await StartAsync();
        const string message = "<script>alert('x')</script> **bold** [link](http://evil.example) @everyone";

        using var sent = await ReportAsync(app.Client, $"/family/{token}", "item-tv", message, "<b>Sam</b>");
        Assert.Equal(HttpStatusCode.Redirect, sent.StatusCode);
        Assert.Equal($"/family/{token}?sent=1", sent.Headers.Location?.ToString());

        var (_, thanks, _) = await GetAsync(app.Client, $"/family/{token}?sent=1");
        Assert.Contains("passed on", thanks);
        Assert.DoesNotContain("alert('x')", thanks);
        Assert.DoesNotContain("<b>Sam</b>", thanks);

        var report = Assert.Single(await app.Get<FamilyReportStore>().ListAsync());
        Assert.Equal("Plex", report.ItemName);
        Assert.Equal(Plex.Id, report.ConnectionId);
        Assert.Equal(message, report.Message);
        Assert.Equal("<b>Sam</b>", report.Reporter);

        var change = Assert.Single(await app.Get<ChangeStore>().QueryAsync(new ChangeQuery(
            DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now, [ChangeKinds.Report])));
        Assert.Equal(ChangeActions.Reported, change.Action);
        Assert.Equal("<b>Sam</b> reported Plex isn't working", change.Title);
        Assert.Equal(message, change.Detail);
    }

    [Fact]
    public async Task A_report_form_rendered_again_encodes_what_was_typed()
    {
        var (app, token) = await StartAsync();

        // Nothing chosen: the form comes back with the message in it, encoded.
        using var refused = await ReportAsync(app.Client, $"/family/{token}", "", "</textarea><script>x()</script>", "");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var raw = await refused.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<script>x()", raw);
        Assert.Contains("&lt;/textarea&gt;&lt;script&gt;", raw);
        Assert.Empty(await app.Get<FamilyReportStore>().ListAsync());
    }

    [Fact]
    public async Task A_report_without_the_antiforgery_token_or_for_something_not_on_the_page_is_refused()
    {
        var (app, token) = await StartAsync();

        using var forged = await ReportAsync(app.Client, $"/family/{token}", "item-tv", "hi", "", withToken: false);
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);

        // The hidden connection's id is not an item, and nor is anything else a visitor makes up.
        using var hidden = await ReportAsync(app.Client, $"/family/{token}", Hidden.Id, "hi", "");
        Assert.Equal(HttpStatusCode.BadRequest, hidden.StatusCode);

        Assert.Empty(await app.Get<FamilyReportStore>().ListAsync());

        // With a wrong link the post is simply not found.
        using var wrong = await app.Client.PostAsync($"/family/{FamilyStatusSettings.NewToken()}/report",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["what"] = "other" }));
        Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
    }

    [Fact]
    public async Task Reports_are_rate_limited()
    {
        var (app, token) = await StartAsync(limits: new FamilyThrottle.Limits { ReportsShort = 2 });

        for (var i = 0; i < 2; i++)
        {
            using var ok = await ReportAsync(app.Client, $"/family/{token}", "other", $"number {i}", "");
            Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        }

        using var third = await ReportAsync(app.Client, $"/family/{token}", "other", "number 3", "");
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.True(int.Parse(third.Headers.GetValues("Retry-After").Single()) > 0);
        Assert.Equal(2, (await app.Get<FamilyReportStore>().ListAsync()).Count);
    }

    [Fact]
    public async Task Page_loads_and_token_guesses_are_rate_limited()
    {
        var (app, token) = await StartAsync(limits: new FamilyThrottle.Limits { PageLoads = 3 });

        for (var i = 0; i < 3; i++)
            await GetAsync(app.Client, $"/family/{FamilyStatusSettings.NewToken()}");

        // Guessing used up the budget, and the right link now waits too.
        var (status, body, response) = await GetAsync(app.Client, $"/family/{token}");
        Assert.Equal(HttpStatusCode.TooManyRequests, status);
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.DoesNotContain("Plex", body);

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(app.Client, $"/family/{token}")).Status);
    }
}
