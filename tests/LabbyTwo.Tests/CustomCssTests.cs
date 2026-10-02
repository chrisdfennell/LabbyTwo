#pragma warning disable BL0006 // The interactive test renderer is a Renderer, which is the whole point of it.
using System.Net;
using System.Text;
using LabbyTwo.Components.Shared;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace LabbyTwo.Tests;

/// <summary>
/// The rules custom CSS is held to — the style element cannot be broken out of, nothing
/// outside the house is fetched, the size is capped — then the service that caches, saves and
/// restores it, safe mode, and the editor drawn by the real component.
/// </summary>
public sealed class CustomCssTests : IAsyncDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private readonly ScriptedJs _js = new();
    private InteractiveRenderer? _renderer;

    /// <summary>
    /// Stands in for custom-css.js: hands back <see cref="Draft"/> as the box's text, the way
    /// the browser streams it, and remembers every other call.
    /// </summary>
    private sealed class ScriptedJs : IJSRuntime
    {
        public string Draft { get; set; } = "";
        public List<(string Identifier, object?[] Args)> Calls { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            lock (Calls)
                Calls.Add((identifier, args ?? []));
            if (identifier == "labbyCustomCss.read" && typeof(TValue) == typeof(IJSStreamReference))
                return ValueTask.FromResult((TValue)(object)new TextStream(Draft));
            return ValueTask.FromResult(default(TValue)!);
        }

        public object?[]? Last(string identifier)
        {
            lock (Calls)
                return Calls.LastOrDefault(c => c.Identifier == identifier).Args;
        }
    }

    private sealed class TextStream(string text) : IJSStreamReference
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(text);

        public long Length => _bytes.Length;

        public ValueTask<Stream> OpenReadStreamAsync(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) =>
            Length > maxAllowedSize
                ? throw new InvalidOperationException($"The incoming data stream of length {Length} exceeds the maximum allowed length {maxAllowedSize}.")
                : ValueTask.FromResult<Stream>(new MemoryStream(_bytes));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public CustomCssTests()
    {
        _services = TestHost.Build(_directory, services =>
        {
            services.AddSingleton<CustomCssStore>();
            services.AddSingleton<CustomCssService>();
            services.AddSingleton<Offload>();
            services.AddSingleton<IJSRuntime>(_js);
        });
        Get<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (_renderer is not null)
            await _renderer.DisposeAsync();
        TestHost.Teardown(_services, _directory);
    }

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private InteractiveRenderer Renderer => _renderer ??= new InteractiveRenderer(_services);

    // ---- the rules ----------------------------------------------------------------------

    [Fact]
    public void OrdinaryCssUsingTheTokensPasses()
    {
        var check = CustomCss.Validate("""
            /* Brand off, dots bigger */
            .nav-brand-text { display: none; }
            .status-dot { width: 1rem; height: 1rem; box-shadow: 0 0 0 2px var(--panel); }
            .wall-stage .widget { border-radius: 1.25rem; background: url(/icon.svg) no-repeat; }
            @media (max-width: 600px) { .app-nav { display: none; } }
            """);
        Assert.True(check.Ok, string.Join("; ", check.Errors));
        Assert.Empty(check.Warnings);
    }

    [Theory]
    [InlineData("body { color: red } </style><script>alert(1)</script>")]
    [InlineData("a { } </STYLE >")]
    [InlineData("/* </style */")]
    public void AStyleEndTagIsRefusedWhereverItIs(string css)
    {
        var check = CustomCss.Validate(css);
        Assert.False(check.Ok);
        Assert.Contains(check.Errors, e => e.Contains("</style", StringComparison.Ordinal));
    }

    /// <summary>The second lock, for a row that reached the database without the first.</summary>
    [Fact]
    public void WhatGoesIntoTheStyleElementCannotEndIt()
    {
        var emitted = CustomCss.ForStyleElement("a{} </style><script>alert(1)</script>");
        Assert.DoesNotContain("</", emitted, StringComparison.Ordinal);
        Assert.Contains("<\\/style>", emitted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("@import url(https://example.com/x.css);")]
    [InlineData("@IMPORT 'theme.css';")]
    [InlineData("@\\69mport 'theme.css';")]
    [InlineData("@im/**/port 'theme.css';")]
    public void ImportIsRefusedHoweverItIsSpelled(string css)
    {
        var check = CustomCss.Validate(css);
        Assert.False(check.Ok);
        Assert.Contains(check.Errors, e => e.Contains("@import", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("body { background: url(https://tracker.example/pixel.png) }")]
    [InlineData("body { background: url(\"http://tracker.example/pixel.png\") }")]
    [InlineData("body { background: url('//cdn.example/pixel.png') }")]
    [InlineData("body { background: URL( https://tracker.example/x ) }")]
    [InlineData("body { background: u\\72l(https://tracker.example/x) }")]
    [InlineData("body { background: url(ht\\74ps://tracker.example/x) }")]
    [InlineData("body { background: url(http://tracker.example/x/*) }")]
    [InlineData("body { background: image-set(\"https://tracker.example/x.png\" 1x) }")]
    [InlineData("@font-face { font-family: X; src: url(https://fonts.example/x.woff2) }")]
    [InlineData("body { background: url(javascript:alert(1)) }")]
    // A literal backslash, which in CSS is written doubled; "/\" is read by a browser as "//".
    [InlineData("body { background: url(/\\\\tracker.example/x) }")]
    public void AddressesOutsideTheDashboardAreRefused(string css)
    {
        var check = CustomCss.Validate(css);
        Assert.False(check.Ok, $"Should have been refused: {css}");
        Assert.Contains(check.Errors, e => e.Contains("Only paths on this dashboard", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("body { background: url(/icon.svg) }")]
    [InlineData("body { background: url(\"icon-192.png\") }")]
    [InlineData("body { background: url('data:image/svg+xml,%3Csvg xmlns=%22http://www.w3.org/2000/svg%22/%3E') }")]
    [InlineData("body { background: url(#gradient) }")]
    [InlineData(".x::after { content: \"see the wiki\" }")]
    public void PathsAndSmallDataUrlsAreAllowed(string css) =>
        Assert.True(CustomCss.Validate(css).Ok, css);

    [Fact]
    public void ABigDataUrlIsRefused()
    {
        var big = "body { background: url(data:image/png;base64," + new string('A', CustomCss.MaxDataUrlBytes) + ") }";
        var check = CustomCss.Validate(big);
        Assert.False(check.Ok);
        Assert.Contains(check.Errors, e => e.Contains("data: URL", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSizeIsCapped()
    {
        var rule = ".a { color: var(--text); }\n";
        var atLimit = string.Concat(Enumerable.Repeat(rule, CustomCss.MaxBytes / rule.Length));
        Assert.True(CustomCss.Validate(atLimit).Ok);

        var over = atLimit + new string(' ', CustomCss.MaxBytes);
        var check = CustomCss.Validate(over);
        Assert.False(check.Ok);
        Assert.Contains(check.Errors, e => e.Contains("100 KB", StringComparison.Ordinal));
    }

    /// <summary>A broken look, not a broken page — so it is said, and saved anyway.</summary>
    [Theory]
    [InlineData(".a { color: var(--text);")]
    [InlineData(".a { color: var(--text); } }")]
    public void UnbalancedBracesWarnWithoutRefusing(string css)
    {
        var check = CustomCss.Validate(css);
        Assert.True(check.Ok);
        Assert.Contains(check.Warnings, w => w.Contains("braces", StringComparison.Ordinal));
    }

    [Fact]
    public void BracesInsideStringsAndCommentsAreNotCounted()
    {
        var check = CustomCss.Validate(".a::before { content: \"{\"; } /* } */");
        Assert.True(check.Ok);
        Assert.Empty(check.Warnings);
    }

    // ---- the service --------------------------------------------------------------------

    [Fact]
    public async Task ASaveReachesEveryPageButTheFamilyPage()
    {
        var css = Get<CustomCssService>();
        Assert.True((await css.SaveAsync(".nav-brand-text { display: none; }", "chris")).Ok);

        Assert.Equal(".nav-brand-text { display: none; }", await css.CssForAsync(ThemeSurface.Dashboard, safe: false));
        Assert.Equal(".nav-brand-text { display: none; }", await css.CssForAsync(ThemeSurface.Wall, safe: false));
        Assert.Equal("", await css.CssForAsync(ThemeSurface.Family, safe: false));

        await css.SetOnFamilyAsync(true);
        Assert.Equal(".nav-brand-text { display: none; }", await css.CssForAsync(ThemeSurface.Family, safe: false));
    }

    [Fact]
    public async Task SafeModeAndTheSwitchBothLeaveItOut()
    {
        var css = Get<CustomCssService>();
        await css.SaveAsync("body { display: none; }", "chris");
        await css.SetOnFamilyAsync(true);

        Assert.Equal("", await css.CssForAsync(ThemeSurface.Dashboard, safe: true));
        Assert.Equal("", await css.CssForAsync(ThemeSurface.Family, safe: true));

        await css.SetEnabledAsync(false);
        Assert.Equal("", await css.CssForAsync(ThemeSurface.Dashboard, safe: false));
        Assert.Equal("", await css.CssForAsync(ThemeSurface.Family, safe: false));
        // Off, not forgotten.
        Assert.Equal("body { display: none; }", (await css.CurrentAsync()).Css);
    }

    [Fact]
    public async Task RefusedCssIsNotSaved()
    {
        var css = Get<CustomCssService>();
        await css.SaveAsync(".a { color: var(--up); }", "chris");

        var check = await css.SaveAsync("@import 'https://example.com/x.css';", "chris");
        Assert.False(check.Ok);
        Assert.Equal(".a { color: var(--up); }", (await css.CurrentAsync()).Css);
        Assert.Single(await css.HistoryAsync());
    }

    /// <summary>The whole point of the cache: a page render costs nothing after the first.</summary>
    [Fact]
    public async Task TheStateIsHeldUntilASave()
    {
        var css = Get<CustomCssService>();
        var first = await css.CurrentAsync();
        Assert.Same(first, await css.CurrentAsync());

        var changed = 0;
        css.Changed += () => changed++;
        await css.SaveAsync(".a { }", "chris");
        Assert.True(changed > 0);
        Assert.NotSame(first, await css.CurrentAsync());
        Assert.Equal(".a { }", (await css.CurrentAsync()).Css);
    }

    /// <summary>A row edited by hand to something the rules refuse is left out, not trusted.</summary>
    [Fact]
    public async Task ASavedRowThatNoLongerPassesIsLeftOut()
    {
        await Get<AppSettingsStore>().SaveAsync(CustomCss.CssKey, "a{} </style><script>alert(1)</script>");
        Assert.Equal("", await Get<CustomCssService>().CssForAsync(ThemeSurface.Dashboard, safe: false));
    }

    [Fact]
    public async Task HistoryKeepsTheLastTwentyAndRestoringIsItselfASave()
    {
        var css = Get<CustomCssService>();
        for (var i = 1; i <= CustomCss.HistoryLimit + 3; i++)
            await css.SaveAsync($".v{i} {{ }}", "chris");

        var history = await css.HistoryAsync();
        Assert.Equal(CustomCss.HistoryLimit, history.Count);
        Assert.Equal($".v{CustomCss.HistoryLimit + 3} {{ }}", history[0].Css);
        Assert.Equal(".v4 { }", history[^1].Css);
        Assert.All(history, v => Assert.Equal("chris", v.SavedBy));

        var old = history.First(v => v.Css == ".v10 { }");
        var restored = await css.RestoreAsync(old.Id, "sam");
        Assert.NotNull(restored);
        Assert.True(restored.Ok);
        Assert.Equal(".v10 { }", (await css.CurrentAsync()).Css);

        var after = await css.HistoryAsync();
        Assert.Equal(CustomCss.HistoryLimit, after.Count);
        Assert.Equal(".v10 { }", after[0].Css);
        Assert.Equal("sam", after[0].SavedBy);
        Assert.StartsWith("Restored the version from", after[0].Note, StringComparison.Ordinal);

        Assert.Null(await css.RestoreAsync(-1, "sam"));
    }

    [Fact]
    public async Task SavingTheSameTextTwiceRecordsItOnce()
    {
        var css = Get<CustomCssService>();
        await css.SaveAsync(".a { }", "chris");
        await css.SaveAsync(".a { }", "chris");
        Assert.Single(await css.HistoryAsync());
    }

    // ---- safe mode ----------------------------------------------------------------------

    [Fact]
    public void SafeModeIsAskedForInTheAddressAndRememberedInACookie()
    {
        var asked = new DefaultHttpContext();
        asked.Request.QueryString = new QueryString("?safe=1");
        Assert.True(SafeMode.IsOn(asked));
        var cookie = asked.Response.Headers.SetCookie.ToString();
        Assert.Contains(SafeMode.CookieName + "=1", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        // A session cookie: closing the browser ends safe mode.
        Assert.DoesNotContain("expires", cookie, StringComparison.OrdinalIgnoreCase);

        var later = new DefaultHttpContext();
        later.Request.Headers.Cookie = SafeMode.CookieName + "=1";
        Assert.True(SafeMode.IsOn(later));

        var ended = new DefaultHttpContext();
        ended.Request.Headers.Cookie = SafeMode.CookieName + "=1";
        ended.Request.QueryString = new QueryString("?safe=0");
        Assert.False(SafeMode.IsOn(ended));
        Assert.Contains(SafeMode.CookieName + "=;", ended.Response.Headers.SetCookie.ToString(), StringComparison.Ordinal);

        Assert.False(SafeMode.IsOn(new DefaultHttpContext()));
        Assert.False(SafeMode.IsOn(null));
    }

    // ---- the editor ---------------------------------------------------------------------

    private Task RenderEditorAsync() => Renderer.RenderAsync<CustomCssEditor>(new Dictionary<string, object?>());

    [Fact]
    public async Task TheEditorStartsWithTheSwitchOnAndNoHistory()
    {
        await RenderEditorAsync();
        var html = WebUtility.HtmlDecode(await Renderer.WaitForAsync("Nothing saved yet"));
        Assert.Contains("Custom CSS is on", html, StringComparison.Ordinal);
        Assert.Contains("id=\"custom-css-text\"", html, StringComparison.Ordinal);
        Assert.Contains("?safe=1", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavingFromTheEditorStoresItAndAppliesItToThisPage()
    {
        await RenderEditorAsync();
        await Renderer.WaitForAsync("Nothing saved yet");

        _js.Draft = ".status-dot { width: 1rem; }";
        await Renderer.ClickAsync("Save");
        await Renderer.WaitForAsync("Saved (");

        Assert.Equal(".status-dot { width: 1rem; }", (await Get<CustomCssService>().CurrentAsync()).Css);
        Assert.Equal(".status-dot { width: 1rem; }", _js.Last("labbyCustomCss.apply")?[0]);
        var html = WebUtility.HtmlDecode(await Renderer.WaitForAsync("in use"));
        // No login in this host, so nobody's name to record.
        Assert.Contains("no login set up", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheEditorSaysWhyItRefusedAndKeepsWhatWasSaved()
    {
        await Get<CustomCssService>().SaveAsync(".a { }", "chris");
        await RenderEditorAsync();
        await Renderer.WaitForAsync("in use");

        _js.Draft = "@import 'https://example.com/x.css';";
        await Renderer.ClickAsync("Save");
        var html = WebUtility.HtmlDecode(await Renderer.WaitForAsync("Not saved"));
        Assert.Contains("@import is not allowed", html, StringComparison.Ordinal);
        Assert.Equal(".a { }", (await Get<CustomCssService>().CurrentAsync()).Css);
    }

    [Fact]
    public async Task PreviewChangesOnlyThisBrowser()
    {
        await Get<CustomCssService>().SaveAsync(".a { }", "chris");
        await RenderEditorAsync();
        await Renderer.WaitForAsync("in use");

        _js.Draft = ".b { color: var(--down); }";
        await Renderer.ClickAsync("Preview");
        await Renderer.WaitForAsync("Previewing in this browser only");

        Assert.Equal(".b { color: var(--down); }", _js.Last("labbyCustomCss.preview")?[0]);
        Assert.Equal(".a { }", (await Get<CustomCssService>().CurrentAsync()).Css);

        await Renderer.ClickAsync("Revert");
        await Renderer.WaitForAsync("Back to the saved version");
        Assert.Equal(".a { }", _js.Last("labbyCustomCss.apply")?[0]);
        Assert.Equal(".a { }", _js.Last("labbyCustomCss.setValue")?[1]);
    }

    [Fact]
    public async Task AnEarlierSaveCanBeComparedAndRestored()
    {
        var css = Get<CustomCssService>();
        await css.SaveAsync(".old { color: var(--up); }", "chris");
        await css.SaveAsync(".new { color: var(--down); }", "chris");

        await RenderEditorAsync();
        await Renderer.WaitForAsync("Compare with current");

        await Renderer.ClickAsync("Compare with current");
        var html = WebUtility.HtmlDecode(await Renderer.WaitForAsync("custom-css-diff-added"));
        Assert.Contains("custom-css-diff-removed", html, StringComparison.Ordinal);
        Assert.Contains(".old { color: var(--up); }", html, StringComparison.Ordinal);

        await Renderer.ClickAsync("Restore");
        await Renderer.WaitForAsync("Restored. Open pages have it now.");
        Assert.Equal(".old { color: var(--up); }", (await css.CurrentAsync()).Css);
        Assert.Equal(".old { color: var(--up); }", _js.Last("labbyCustomCss.setValue")?[1]);
        Assert.Equal(3, (await css.HistoryAsync()).Count);
    }

    [Fact]
    public async Task TheBigSwitchTurnsItOffEverywhere()
    {
        await Get<CustomCssService>().SaveAsync("body { display: none; }", "chris");
        await RenderEditorAsync();
        await Renderer.WaitForAsync("Custom CSS is on");

        await Renderer.ChangeAsync("custom-css-on", false);
        await Renderer.WaitForAsync("Custom CSS is off");
        Assert.Equal("", await Get<CustomCssService>().CssForAsync(ThemeSurface.Dashboard, safe: false));
    }
}
