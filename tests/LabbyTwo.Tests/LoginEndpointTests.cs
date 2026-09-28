using System.Net;
using System.Text.RegularExpressions;
using LabbyTwo.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabbyTwo.Tests;

/// <summary>
/// The throttle posted at through the real login page, in the real app: the form binding,
/// the antiforgery token, the forwarded-header middleware and the page's own wiring all
/// have to be right for a wrong password to be slowed, and none of that is visible to a
/// test of the throttle on its own.
/// </summary>
public sealed partial class LoginEndpointTests : IDisposable
{
    private const string Password = "correct horse battery staple";

    private readonly string _directory = TestHost.TempDirectory();
    private readonly LoginThrottleTests.ManualClock _clock = new();
    private readonly List<WebApplicationFactory<Program>> _factories = [];

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

    /// <summary>
    /// The app with a password, its own database and plugin folder, and a throttle on a
    /// clock the test holds still, so "the sixth attempt is locked for a second" cannot
    /// turn into "the seventh arrived after the second had passed" on a slow machine.
    /// </summary>
    /// <param name="socket">The address every request appears to come from, as the socket would say.</param>
    private HttpClient Start(string socket, string trustedProxies = "")
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseSetting("Labby:Auth:Username", "labby");
            host.UseSetting("Labby:Auth:Password", Password);
            host.UseSetting("Labby:DatabasePath", Path.Combine(_directory, Guid.NewGuid().ToString("n"), "labbytwo.db"));
            host.UseSetting("Labby:PluginPath", Path.Combine(_directory, "plugins"));
            host.UseSetting("Labby:Proxy:TrustedProxies", trustedProxies);
            host.ConfigureTestServices(services =>
            {
                services.RemoveAll<LoginThrottle>();
                services.AddSingleton(new LoginThrottle(NullLogger<LoginThrottle>.Instance, _clock));
                services.AddSingleton<IStartupFilter>(new SocketAddress(IPAddress.Parse(socket)));
            });
        });
        _factories.Add(factory);
        return factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    /// <summary>
    /// The test server has no socket, so this stands in for one: it runs before the app's
    /// own pipeline, and so before ForwardedHeaders, exactly where Kestrel would have set it.
    /// </summary>
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

    private static async Task<HttpResponseMessage> SignIn(
        HttpClient client, string password, string? forwardedFor = null)
    {
        var form = await client.GetStringAsync("/login");
        var token = AntiforgeryField().Match(form);
        Assert.True(token.Success, "The login page has no antiforgery field.");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_handler"] = "login",
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value),
                ["Model.Username"] = "labby",
                ["Model.Password"] = password,
            }),
        };
        if (forwardedFor is not null)
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        return await client.SendAsync(request);
    }

    private static async Task<(HttpStatusCode Status, string Body)> Read(Task<HttpResponseMessage> sending)
    {
        using var response = await sending;
        return (response.StatusCode, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Wrong_passwords_to_the_login_page_are_slowed_and_the_page_says_for_how_long()
    {
        var client = Start("192.168.1.66");

        for (var i = 0; i < 5; i++)
        {
            var (status, body) = await Read(SignIn(client, "guess" + i));
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Contains("Wrong username or password.", body);
            Assert.DoesNotContain("Too many attempts", body);
        }

        var (_, sixth) = await Read(SignIn(client, "guess5"));
        Assert.Contains("Too many attempts — try again in 1 second.", sixth);

        // Even the right password is refused while locked, with the same words either way.
        using var refused = await SignIn(client, Password);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("1", refused.Headers.GetValues("Retry-After").Single());
        var body7 = WebUtility.HtmlDecode(await refused.Content.ReadAsStringAsync());
        Assert.Contains("Too many attempts — try again in 1 second.", body7);
        Assert.DoesNotContain("Wrong username", body7);

        // Once the wait is over the right password works, and sets the cookie.
        _clock.Advance(TimeSpan.FromSeconds(1));
        using var signedIn = await SignIn(client, Password);
        Assert.True((int)signedIn.StatusCode is >= 300 and < 400, $"Expected a redirect, got {signedIn.StatusCode}.");
        Assert.Contains(signedIn.Headers.GetValues("Set-Cookie"), c => c.StartsWith("labbytwo.auth="));
    }

    [Fact]
    public async Task A_forwarded_address_is_ignored_unless_the_proxy_is_trusted()
    {
        // Untrusted: rotating X-Forwarded-For gets an attacker nothing, every guess is
        // counted against the socket's address.
        var client = Start("172.18.0.5");
        for (var i = 0; i < 6; i++)
            await Read(SignIn(client, "guess", forwardedFor: $"203.0.113.{i + 1}"));
        var (status, _) = await Read(SignIn(client, "guess", forwardedFor: "203.0.113.99"));
        Assert.Equal(HttpStatusCode.TooManyRequests, status);
    }

    [Fact]
    public async Task Behind_a_trusted_proxy_each_client_is_counted_on_its_own()
    {
        // Trusted: one visitor through the tunnel locking themselves out does not lock out
        // the owner coming through the same tunnel.
        var client = Start("172.18.0.5", trustedProxies: "172.16.0.0/12");
        for (var i = 0; i < 6; i++)
            await Read(SignIn(client, "guess", forwardedFor: "203.0.113.1"));
        var (locked, _) = await Read(SignIn(client, "guess", forwardedFor: "203.0.113.1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, locked);

        using var owner = await SignIn(client, Password, forwardedFor: "198.51.100.20");
        Assert.True((int)owner.StatusCode is >= 300 and < 400, $"Expected a redirect, got {owner.StatusCode}.");
    }
}
