#pragma warning disable BL0006 // The interactive test renderer reads its own render tree back, which is the whole point of it.
using System.Net;
using LabbyTwo.Components.Shared;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace LabbyTwo.Tests;

/// <summary>
/// The background against a real database and a real data folder: the cached block, the
/// stored picture, the settings component drawn by the real renderer — and the picture
/// served by the real app, with its caching headers.
/// </summary>
public sealed class BackdropServiceTests : IAsyncDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private readonly List<WebApplicationFactory<Program>> _factories = [];
    private InteractiveRenderer? _renderer;

    /// <summary>Answers the picture sampler with a fixed grid; everything else gets nothing.</summary>
    private sealed class FakeScript : Microsoft.JSInterop.IJSRuntime
    {
        public string[]? Samples { get; set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            identifier == "labbyBackdrop.sample" && Samples is { } samples
                ? ValueTask.FromResult((TValue)(object)samples)
                : ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }

    private readonly FakeScript _script = new();

    public BackdropServiceTests()
    {
        _services = TestHost.Build(_directory, services =>
        {
            services.AddSingleton<ThemeStore>();
            services.AddSingleton<ThemeService>();
            services.AddSingleton<BackdropImageStore>();
            services.AddSingleton<BackdropService>();
            services.AddSingleton<Offload>();
            services.AddSingleton<Microsoft.JSInterop.IJSRuntime>(_script);
        });
        Get<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (_renderer is not null)
            await _renderer.DisposeAsync();
        foreach (var factory in _factories)
            await factory.DisposeAsync();
        TestHost.Teardown(_services, _directory);
    }

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private InteractiveRenderer Renderer => _renderer ??= new InteractiveRenderer(_services);

    private Task SaveAsync(Backdrop backdrop) => Get<BackdropService>().SaveAsync(backdrop);

    // ---- the cached block --------------------------------------------------------------------

    [Fact]
    public async Task AFreshInstallHasAnEmptyBlock()
    {
        var active = await Get<BackdropService>().ActiveAsync();
        Assert.Same(ActiveBackdrop.Empty, active);
    }

    [Fact]
    public async Task TheBlockIsBuiltOnceAndRebuiltOnAChange()
    {
        var backdrops = Get<BackdropService>();
        var told = 0;
        backdrops.Changed += () => told++;

        await SaveAsync(Backdrop.Default with { Kind = BackdropKind.Gradient });
        var first = await backdrops.ActiveAsync();
        Assert.Same(first, await backdrops.ActiveAsync());
        Assert.Contains("linear-gradient", first.Css);
        Assert.True(told >= 1);

        await SaveAsync(Backdrop.Default with { Kind = BackdropKind.Solid });
        var second = await backdrops.ActiveAsync();
        Assert.NotEqual(first.Hash, second.Hash);
        Assert.DoesNotContain("linear-gradient(180deg", second.Css);
    }

    [Fact]
    public async Task OnlyWithOneThemeFollowsTheThemeInUse()
    {
        await SaveAsync(Backdrop.Default with { Kind = BackdropKind.Solid, ThemeOnly = "nord" });
        Assert.Equal("", (await Get<BackdropService>().ActiveAsync()).Css);

        await Get<ThemeService>().ApplyAsync("nord");
        Assert.Contains("body > .app-shell::before", (await Get<BackdropService>().ActiveAsync()).Css);
    }

    [Fact]
    public async Task UploadingAPictureDrawsItAndRemovingItStops()
    {
        await SaveAsync(Backdrop.Default with { Kind = BackdropKind.Image });
        Assert.Equal("", (await Get<BackdropService>().ActiveAsync()).Css);

        var image = await Get<BackdropImageStore>().SaveAsync(new MemoryStream(BackdropTestImages.Jpeg()));
        var css = (await Get<BackdropService>().ActiveAsync()).Css;
        Assert.Contains($"url(\"/backdrop/{image.Hash}.jpg\")", css);

        Get<BackdropImageStore>().Clear();
        Assert.Equal("", (await Get<BackdropService>().ActiveAsync()).Css);
    }

    // ---- the stored picture ----------------------------------------------------------------

    [Fact]
    public async Task TheStoredFileIsStrippedAndNamedByItsContent()
    {
        var store = Get<BackdropImageStore>();
        var image = await store.SaveAsync(new MemoryStream(BackdropTestImages.Jpeg()));

        var bytes = await File.ReadAllBytesAsync(store.PathOf(image)!);
        Assert.False(BackdropTestImages.Contains(bytes, BackdropTestImages.Secret));
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))[..16], image.Hash);
        Assert.Equal((640, 480, ".jpg"), (image.Width, image.Height, image.Extension));
    }

    [Fact]
    public async Task ANewPictureReplacesTheOldOne()
    {
        var store = Get<BackdropImageStore>();
        var first = await store.SaveAsync(new MemoryStream(BackdropTestImages.Jpeg()));
        var second = await store.SaveAsync(new MemoryStream(BackdropTestImages.Png()));

        Assert.False(File.Exists(store.PathOf(first)));
        Assert.True(File.Exists(store.PathOf(second)));
        Assert.Single(Directory.GetFiles(store.Directory));
        Assert.Equal(second, store.Current());
    }

    [Fact]
    public async Task ARefusedUploadChangesNothing()
    {
        var store = Get<BackdropImageStore>();
        var kept = await store.SaveAsync(new MemoryStream(BackdropTestImages.Png()));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveAsync(new MemoryStream("<svg onload=\"alert(1)\"></svg>"u8.ToArray())));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveAsync(new MemoryStream(new byte[BackdropImageFile.MaxBytes + 10])));

        Assert.Equal(kept, store.Current());
        Assert.Single(Directory.GetFiles(store.Directory));
    }

    [Fact]
    public async Task APictureSurvivesARestart()
    {
        var image = await Get<BackdropImageStore>().SaveAsync(new MemoryStream(BackdropTestImages.WebP()));
        var again = new BackdropImageStore(Get<Microsoft.Extensions.Options.IOptions<LabbyOptions>>(), Get<Microsoft.Extensions.Hosting.IHostEnvironment>());
        Assert.Equal(image, again.Current());
    }

    // ---- the settings, drawn ----------------------------------------------------------------

    private async Task<string> RenderAsync(string waitFor)
    {
        await Renderer.RenderAsync<BackdropSettings>(new Dictionary<string, object?>());
        return WebUtility.HtmlDecode(await Renderer.WaitForAsync(waitFor));
    }

    [Fact]
    public async Task ChoosingAGradientSavesItAndThePreviewDrawsIt()
    {
        var html = await RenderAsync("Behind the cards");
        Assert.DoesNotContain("Start from", html);

        await Renderer.ClickAsync("A gradient");
        html = WebUtility.HtmlDecode(await Renderer.WaitForAsync("Start from"));

        Assert.Equal(BackdropKind.Gradient, Backdrop.From(await Get<AppSettingsStore>().AllAsync()).Kind);
        Assert.Contains("linear-gradient(180deg, var(--panel-2), var(--ink))", html);
        Assert.Contains("class=\"backdrop-preview-layer\"", html);
        Assert.Contains("Depth", html);
    }

    [Fact]
    public async Task SlidersAreClampedAndSaved()
    {
        await SaveAsync(Backdrop.Default with { Kind = BackdropKind.Solid, Glass = true });
        await RenderAsync("Frosted glass cards");

        await Renderer.ChangeAsync("backdrop-dim", "60");
        await Renderer.ChangeAsync("glass-opacity", "5");
        await Renderer.ChangeAsync("glass-blur", "not a number");

        var saved = Backdrop.From(await Get<AppSettingsStore>().AllAsync());
        Assert.Equal(60, saved.Dim);
        Assert.Equal(Backdrop.MinGlassOpacity, saved.GlassOpacity);
        Assert.Equal(Backdrop.Default.GlassBlur, saved.GlassBlur);

        var html = WebUtility.HtmlDecode(await Renderer.WaitForAsync("Dim: 60%"));
        Assert.Contains($"color-mix(in srgb, var(--panel) {Backdrop.MinGlassOpacity}%, transparent)", html); // the sample card
    }

    [Fact]
    public async Task AColourThatIsNotOneIsIgnored()
    {
        await SaveAsync(Backdrop.Default with { Kind = BackdropKind.Solid });
        await RenderAsync("Colour");

        await Renderer.ChangeAsync("backdrop-solid", "red;}body{display:none");
        Assert.Equal(Backdrop.Default.Solid, Backdrop.From(await Get<AppSettingsStore>().AllAsync()).Solid);

        await Renderer.ChangeAsync("backdrop-solid", "accent");
        Assert.Equal("accent", Backdrop.From(await Get<AppSettingsStore>().AllAsync()).Solid.Token);

        await Renderer.ChangeAsync("backdrop-solid", "custom");
        var custom = Backdrop.From(await Get<AppSettingsStore>().AllAsync()).Solid;
        Assert.Null(custom.Token);
        Assert.Equal(BuiltInThemes.LabbyTwo.Dark!.Effective("accent"), custom.Colour);
    }

    [Fact]
    public async Task ABrightBackgroundGetsAWarning()
    {
        await SaveAsync(Backdrop.Default with { Kind = BackdropKind.Solid, Solid = BackdropColour.Of(new ThemeColor(255, 255, 255)), Dim = 0 });
        var html = await RenderAsync("may be hard to read");
        Assert.Contains("Headings on the background", html);
        Assert.Contains("grade-fail", html);

        // Dimmed nearly away, the theme's text reads again.
        await Renderer.ChangeAsync("backdrop-dim", "90");
        html = WebUtility.HtmlDecode(await Renderer.WaitForAsync("Text should stay easy to read"));
        Assert.DoesNotContain("may be hard to read", html);
    }

    [Fact]
    public async Task APictureIsJudgedByTheColoursTheBrowserSampled()
    {
        await Get<BackdropImageStore>().SaveAsync(new MemoryStream(BackdropTestImages.Png()));
        await SaveAsync(Backdrop.Default with { Kind = BackdropKind.Image, Dim = 0 });
        _script.Samples = [.. Enumerable.Repeat("#ffffff", 16)];

        var html = await RenderAsync("may be hard to read");
        Assert.Contains("64 × 48", html);
    }

    [Fact]
    public async Task UntilThePictureIsSampledItSaysSo()
    {
        await Get<BackdropImageStore>().SaveAsync(new MemoryStream(BackdropTestImages.Png()));
        await SaveAsync(Backdrop.Default with { Kind = BackdropKind.Image });
        var html = await RenderAsync("once the picture has loaded");
        Assert.DoesNotContain("grade-fail", html);
    }

    // ---- served by the real app ----------------------------------------------------------------

    private (WebApplicationFactory<Program> Factory, HttpClient Client) StartApp(bool login = false)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            if (login)
            {
                host.UseSetting("Labby:Auth:Username", "labby");
                host.UseSetting("Labby:Auth:Password", "correct horse battery staple");
            }
            host.UseSetting("Labby:DatabasePath", Path.Combine(_directory, Guid.NewGuid().ToString("n"), "labbytwo.db"));
            host.UseSetting("Labby:PluginPath", Path.Combine(_directory, "plugins"));
        });
        _factories.Add(factory);
        return (factory, factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }));
    }

    [Fact]
    public async Task ThePictureIsServedWithALongCacheAndAnETag()
    {
        var (factory, client) = StartApp();
        var image = await factory.Services.GetRequiredService<BackdropImageStore>().SaveAsync(new MemoryStream(BackdropTestImages.Png()));

        var response = await client.GetAsync(image.Url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("immutable", response.Headers.CacheControl?.ToString());
        Assert.Equal(31536000, (int)response.Headers.CacheControl!.MaxAge!.Value.TotalSeconds);
        Assert.Equal($"\"{image.Hash}\"", response.Headers.ETag?.Tag);
        Assert.Contains("nosniff", response.Headers.GetValues("X-Content-Type-Options"));
        Assert.False(BackdropTestImages.Contains(await response.Content.ReadAsByteArrayAsync(), BackdropTestImages.Secret));

        using var again = new HttpRequestMessage(HttpMethod.Get, image.Url);
        again.Headers.IfNoneMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue($"\"{image.Hash}\""));
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(again)).StatusCode);
    }

    [Fact]
    public async Task OnlyTheCurrentPictureIsServed()
    {
        var (factory, client) = StartApp();
        var store = factory.Services.GetRequiredService<BackdropImageStore>();
        var old = await store.SaveAsync(new MemoryStream(BackdropTestImages.Png()));
        var current = await store.SaveAsync(new MemoryStream(BackdropTestImages.Jpeg()));

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(old.Url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/backdrop/{current.Hash}.png")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/backdrop/..%2F..%2Flabbytwo.db")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(current.Url)).StatusCode);

        store.Clear();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(current.Url)).StatusCode);
    }

    [Fact]
    public async Task ThePictureIsBehindTheLogin()
    {
        var (factory, client) = StartApp(login: true);
        var image = await factory.Services.GetRequiredService<BackdropImageStore>().SaveAsync(new MemoryStream(BackdropTestImages.Png()));

        var response = await client.GetAsync(image.Url);
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual("image/png", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task EveryPageCarriesTheBlock()
    {
        var (factory, client) = StartApp();
        var page = await client.GetStringAsync("/settings/appearance");
        Assert.Contains("<style id=\"labby-backdrop\"></style>", page);
        Assert.Matches(@"src=""[^""]*backdrop(\.[a-z0-9]+)?\.js""", page);

        await factory.Services.GetRequiredService<BackdropService>().SaveAsync(Backdrop.Default with { Kind = BackdropKind.Gradient, Glass = true });
        page = await client.GetStringAsync("/settings/appearance");
        Assert.Contains("body > .app-shell::before", page);
        Assert.Contains("prefers-reduced-transparency", page);
        Assert.Contains("id=\"background\"", page); // the settings section itself
    }
}
