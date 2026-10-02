using System.Net;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace LabbyTwo.Tests;

/// <summary>
/// Custom CSS through the real app: where its block sits in &lt;head&gt; (after app.css and
/// after the theme, so it wins over both), safe mode from the address to the cookie to the
/// banner, and the family page leaving it out until asked.
/// </summary>
public sealed class CustomCssEndpointTests : IDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
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

    private (WebApplicationFactory<Program> Factory, HttpClient Client) Start()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseSetting("Labby:Auth:Username", "labby");
            host.UseSetting("Labby:Auth:Password", "correct horse battery staple");
            host.UseSetting("Labby:DatabasePath", Path.Combine(_directory, Guid.NewGuid().ToString("n"), "labbytwo.db"));
            host.UseSetting("Labby:PluginPath", Path.Combine(_directory, "plugins"));
        });
        _factories.Add(factory);
        return (factory, factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }));
    }

    private static async Task<string> PageAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private const string Rule = ".nav-brand-text { display: none; }";

    [Fact]
    public async Task TheCustomBlockComesAfterAppCssAndTheTheme()
    {
        var (factory, client) = Start();
        await PageAsync(client, "/login");
        await factory.Services.GetRequiredService<CustomCssService>().SaveAsync(Rule, "labby");

        var html = await PageAsync(client, "/login");
        // Fingerprinted by MapStaticAssets: app.{hash}.css.
        var appCss = html.IndexOf("href=\"app.", StringComparison.Ordinal);
        var theme = html.IndexOf("<style id=\"labby-theme\">", StringComparison.Ordinal);
        var custom = html.IndexOf("<style id=\"labby-custom\">", StringComparison.Ordinal);
        Assert.True(appCss > 0 && theme > appCss && custom > theme, $"app.css {appCss}, theme {theme}, custom {custom}");
        Assert.Contains($"<style id=\"labby-custom\">{Rule}</style>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("safe-mode-banner", html, StringComparison.Ordinal);
        // The way back is on the login page.
        Assert.Contains("login?safe=1", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SafeModeSkipsItForTheRestOfTheVisitAndSaysSo()
    {
        var (factory, client) = Start();
        await PageAsync(client, "/login");
        await factory.Services.GetRequiredService<CustomCssService>().SaveAsync(Rule, "labby");

        var response = await client.GetAsync("/login?safe=1");
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(SafeMode.CookieName + "=1", StringComparison.Ordinal));
        var safe = await response.Content.ReadAsStringAsync();
        Assert.Contains("<style id=\"labby-custom\"></style>", safe, StringComparison.Ordinal);
        Assert.Contains("data-safe=\"1\"", safe, StringComparison.Ordinal);
        Assert.Contains("Custom CSS is off for this visit", WebUtility.HtmlDecode(safe), StringComparison.Ordinal);

        // The cookie carries it to the next page, with no ?safe in the address.
        var next = await PageAsync(client, "/login");
        Assert.Contains("<style id=\"labby-custom\"></style>", next, StringComparison.Ordinal);
        Assert.Contains("safe-mode-banner", next, StringComparison.Ordinal);

        // A redirect to the login page carries it too — the page asked for may be the broken one.
        var redirected = await client.GetAsync("/settings/appearance?safe=1");
        Assert.Contains(redirected.Headers.GetValues("Set-Cookie"), c => c.StartsWith(SafeMode.CookieName + "=1", StringComparison.Ordinal));

        var ended = await PageAsync(client, "/login?safe=0");
        Assert.Contains($"<style id=\"labby-custom\">{Rule}</style>", ended, StringComparison.Ordinal);
        Assert.DoesNotContain("safe-mode-banner", ended, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFamilyPageLeavesItOutUntilAsked()
    {
        var (factory, client) = Start();
        await PageAsync(client, "/login");
        var css = factory.Services.GetRequiredService<CustomCssService>();
        await css.SaveAsync(Rule, "labby");

        var token = FamilyStatusSettings.NewToken();
        await factory.Services.GetRequiredService<FamilyStatus>().SaveAsync(new FamilyStatusSettings(true, token, false, "", []));

        var without = await PageAsync(client, $"/family/{token}");
        Assert.DoesNotContain("labby-custom", without, StringComparison.Ordinal);

        await css.SetOnFamilyAsync(true);
        var with = await PageAsync(client, $"/family/{token}");
        Assert.Contains($"<style id=\"labby-custom\">{Rule}</style>", with, StringComparison.Ordinal);
        Assert.True(with.IndexOf("labby-custom", StringComparison.Ordinal) > with.IndexOf("family.css", StringComparison.Ordinal));

        var safe = await PageAsync(client, $"/family/{token}?safe=1");
        Assert.DoesNotContain("labby-custom", safe, StringComparison.Ordinal);
    }
}
