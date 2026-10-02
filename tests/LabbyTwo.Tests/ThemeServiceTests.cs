#pragma warning disable BL0006 // The interactive test renderer is a Renderer, which is the whole point of it.
using System.Net;
using LabbyTwo.Components.Pages;
using LabbyTwo.Components.Shared;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace LabbyTwo.Tests;

/// <summary>
/// The active theme, its cache and the user's own themes against a real database — and the
/// gallery and editor drawn by the real components.
/// </summary>
public sealed class ThemeServiceTests : IAsyncDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private InteractiveRenderer? _renderer;

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://labby.test/", "http://labby.test/settings/appearance");

        protected override void NavigateToCore(string uri, NavigationOptions options) => Uri = ToAbsoluteUri(uri).ToString();
    }

    private sealed class NoScript : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!); // InputFile wires itself up through JS; nothing to wire here.

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!); // InputFile wires itself up through JS; nothing to wire here.
    }

    public ThemeServiceTests()
    {
        _services = TestHost.Build(_directory, services =>
        {
            services.AddSingleton<ThemeStore>();
            services.AddSingleton<ThemeService>();
            services.AddSingleton<Offload>();
            services.AddSingleton<NavigationManager>(new TestNavigation());
            services.AddSingleton<Microsoft.JSInterop.IJSRuntime, NoScript>();
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

    // ---- resolution and caching ---------------------------------------------------------

    [Fact]
    public async Task AFreshInstallUsesLabbyTwo()
    {
        var active = await Get<ThemeService>().ActiveAsync();
        Assert.Equal(BuiltInThemes.DefaultId, active.Theme.Id);
        Assert.Equal(ThemeCss.Render(BuiltInThemes.LabbyTwo), active.Css);
    }

    /// <summary>The whole point of the cache: a page render costs nothing after the first.</summary>
    [Fact]
    public async Task TheActiveThemeIsBuiltOnceAndHeld()
    {
        var themes = Get<ThemeService>();
        var first = await themes.ActiveAsync();
        var second = await themes.ActiveAsync();
        Assert.Same(first, second);
    }

    [Fact]
    public async Task ChoosingAThemeDropsTheCacheAndSaysSo()
    {
        var themes = Get<ThemeService>();
        var before = await themes.ActiveAsync();
        var told = 0;
        themes.Changed += () => told++;

        await themes.ApplyAsync(BuiltInThemes.Nord.Id);

        var after = await themes.ActiveAsync();
        Assert.NotSame(before, after);
        Assert.Equal("nord", after.Theme.Id);
        Assert.NotEqual(before.Hash, after.Hash);
        Assert.True(told >= 1);
    }

    /// <summary>Picking a theme brings its own accent back — an old override would hide it.</summary>
    [Fact]
    public async Task ChoosingAThemeClearsTheAccentOverride()
    {
        await Get<AppSettingsStore>().SaveAsync(Appearance.AccentKey, "#ef4444");
        Assert.Equal("#ef4444", (await Get<ThemeService>().SnapshotAsync()).Accent);

        await Get<ThemeService>().ApplyAsync(BuiltInThemes.Dracula.Id);

        var snapshot = await Get<ThemeService>().SnapshotAsync();
        Assert.Null(snapshot.Accent);
        Assert.DoesNotContain("--accent", Appearance.From(await Get<AppSettingsStore>().AllAsync()).StyleAttribute);
    }

    /// <summary>The old "dark shade" setting named two palettes that are themes now; the choice carries over.</summary>
    [Theory]
    [InlineData("slate", "slate")]
    [InlineData("black", "black")]
    [InlineData("midnight", "labbytwo")]
    [InlineData("", "labbytwo")]
    public async Task AnOldDarkShadeBecomesItsTheme(string palette, string expected)
    {
        if (palette.Length > 0)
            await Get<AppSettingsStore>().SaveAsync(Appearance.DarkPaletteKey, palette);
        Assert.Equal(expected, (await Get<ThemeService>().ActiveAsync()).Theme.Id);
    }

    [Fact]
    public async Task AThemeThatHasGoneFallsBackToTheDefault()
    {
        await Get<AppSettingsStore>().SaveAsync(ThemeService.ThemeIdKey, "user-gone");
        Assert.Equal(BuiltInThemes.DefaultId, (await Get<ThemeService>().ActiveAsync()).Theme.Id);
    }

    [Fact]
    public async Task ADarkOnlyThemeForcesTheModeWhateverTheSetting()
    {
        await Get<AppSettingsStore>().SaveAsync(Appearance.ThemeKey, "light");
        await Get<ThemeService>().ApplyAsync(BuiltInThemes.Dracula.Id);

        var snapshot = await Get<ThemeService>().SnapshotAsync();
        Assert.Equal("dark", snapshot.Mode);
        Assert.Equal("#21222c", snapshot.Meta);
    }

    [Fact]
    public async Task ATwoEndedThemeFollowsTheSetting()
    {
        await Get<AppSettingsStore>().SaveAsync(Appearance.ThemeKey, "light");
        await Get<ThemeService>().ApplyAsync(BuiltInThemes.Nord.Id);
        var snapshot = await Get<ThemeService>().SnapshotAsync();
        Assert.Equal("light", snapshot.Mode);
        Assert.Equal("#e5e9f0", snapshot.Meta);

        await Get<AppSettingsStore>().SaveAsync(Appearance.ThemeKey, "system");
        Assert.Null((await Get<ThemeService>().SnapshotAsync()).Mode);
    }

    // ---- the user's own -----------------------------------------------------------------

    [Fact]
    public async Task ASavedThemeComesBackAndCanBeUsed()
    {
        var mine = BuiltInThemes.Nord with { Id = "", Name = "My Nord", BuiltIn = false };
        var id = await Get<ThemeStore>().SaveAsync(mine);

        Assert.StartsWith(ThemeStore.IdPrefix, id);
        var all = await Get<ThemeService>().AllAsync();
        Assert.Contains(all, t => t.Id == id && t.Name == "My Nord" && !t.BuiltIn);

        await Get<ThemeService>().ApplyAsync(id);
        var active = await Get<ThemeService>().ActiveAsync();
        Assert.Equal(id, active.Theme.Id);
        Assert.Equal(ThemeCss.Render(BuiltInThemes.Nord), active.Css);
    }

    /// <summary>Nothing can be saved over a built-in: a built-in's id makes a new theme instead.</summary>
    [Fact]
    public async Task SavingWithABuiltInsIdMakesACopy()
    {
        var id = await Get<ThemeStore>().SaveAsync(BuiltInThemes.Dracula with { Name = "Mine" });
        Assert.NotEqual("dracula", id);
        Assert.Equal("Dracula", (await Get<ThemeService>().FindAsync("dracula"))!.Name);
    }

    [Fact]
    public async Task SavingAgainWithItsIdUpdatesIt()
    {
        var id = await Get<ThemeStore>().SaveAsync(BuiltInThemes.Nord with { Id = "", Name = "One" });
        await Get<ThemeStore>().SaveAsync(BuiltInThemes.Nord with { Id = id, Name = "Two" });
        var mine = (await Get<ThemeStore>().AllAsync()).Where(t => t.Id == id).ToList();
        Assert.Single(mine);
        Assert.Equal("Two", mine[0].Name);
    }

    /// <summary>
    /// A row edited by hand to hold something other than colours is ignored when read — the
    /// store re-validates every row, so the database is not a way round the importer.
    /// </summary>
    [Fact]
    public async Task AHandEditedRowThatIsNotAThemeIsIgnored()
    {
        var good = await Get<ThemeStore>().SaveAsync(BuiltInThemes.Nord with { Id = "", Name = "Good" });
        await using (var connection = await Get<Db>().OpenAsync())
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO user_themes (id, name, json) VALUES ('user-bad', 'Bad', $json)";
            cmd.Parameters.AddWithValue("$json",
                ThemeJson.Export(BuiltInThemes.Nord).Replace("\"#2e3440\"", "\"#000;}body{display:none\"", StringComparison.Ordinal));
            await cmd.ExecuteNonQueryAsync();
        }
        await Get<ThemeStore>().DeleteAsync("nothing"); // drops the cache

        var all = await Get<ThemeStore>().AllAsync();
        Assert.Contains(all, t => t.Id == good);
        Assert.DoesNotContain(all, t => t.Id == "user-bad");

        await Get<AppSettingsStore>().SaveAsync(ThemeService.ThemeIdKey, "user-bad");
        var active = await Get<ThemeService>().ActiveAsync();
        Assert.Equal(BuiltInThemes.DefaultId, active.Theme.Id);
        Assert.DoesNotContain("display:none", active.Css);
    }

    [Fact]
    public async Task DeletingAThemeTakesItOutOfTheList()
    {
        var id = await Get<ThemeStore>().SaveAsync(BuiltInThemes.Nord with { Id = "", Name = "Brief" });
        await Get<ThemeStore>().DeleteAsync(id);
        Assert.DoesNotContain(await Get<ThemeService>().AllAsync(), t => t.Id == id);
    }

    // ---- the gallery and the editor, rendered -------------------------------------------

    [Fact]
    public async Task TheGalleryShowsEveryThemeAsALivePreviewAndMarksTheOneInUse()
    {
        await Get<ThemeService>().ApplyAsync(BuiltInThemes.GitHub.Id);

        await Renderer.RenderAsync<ThemeGallery>(new Dictionary<string, object?>());
        var html = WebUtility.HtmlDecode(await Renderer.WaitForAsync(h => h.Contains("theme-tile is-active", StringComparison.Ordinal)));

        foreach (var theme in BuiltInThemes.All)
            Assert.Contains($">{theme.Name}", html);

        // A preview is the theme's own tokens, inline.
        Assert.Contains("theme-preview theme-scope", html);
        Assert.Contains("--ink: #2e3440;", html); // Nord's dark end
        Assert.Contains("--ink: #e5e9f0;", html); // and its light one
        Assert.Contains("Dark only", html);

        var active = html[html.IndexOf("theme-tile is-active", StringComparison.Ordinal)..];
        Assert.Contains("GitHub", active[..active.IndexOf("theme-tile-actions", StringComparison.Ordinal)]);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "aria-pressed=\"true\""));

        Assert.Contains("href=\"settings/appearance/themes/nord\"", html);
        Assert.Contains("href=\"api/share/theme?id=nord\"", html);
        // Built-ins cannot be deleted.
        Assert.DoesNotContain(">Delete<", html);
    }

    [Fact]
    public async Task TheGalleryRedrawsWhenTheThemeChangesElsewhere()
    {
        await Renderer.RenderAsync<ThemeGallery>(new Dictionary<string, object?>());
        await Renderer.WaitForAsync(h => h.Contains("theme-tile is-active", StringComparison.Ordinal));

        var id = await Get<ThemeStore>().SaveAsync(BuiltInThemes.Solarized with { Id = "", Name = "Sunny" });
        await Get<ThemeService>().ApplyAsync(id);

        static string ActiveTile(string page)
        {
            var at = page.IndexOf("theme-tile is-active", StringComparison.Ordinal);
            if (at < 0)
                return "";
            var tile = page[at..];
            return tile[..tile.IndexOf("theme-tile-actions", StringComparison.Ordinal)];
        }

        var html = WebUtility.HtmlDecode(await Renderer.WaitForAsync(h => ActiveTile(h).Contains("Sunny", StringComparison.Ordinal)));
        Assert.Contains(">Delete<", html);
        Assert.Contains("Sunny", ActiveTile(html));
    }

    /// <summary>A user theme's name is text: shown encoded, never markup, and never in a style.</summary>
    [Fact]
    public async Task AHostileThemeNameIsShownAsText()
    {
        await Get<ThemeStore>().SaveAsync(BuiltInThemes.Nord with { Id = "", Name = "<img src=x onerror=alert(1)>" });

        await Renderer.RenderAsync<ThemeGallery>(new Dictionary<string, object?>());
        var html = await Renderer.WaitForAsync(h => h.Contains("&lt;img", StringComparison.Ordinal));
        Assert.DoesNotContain("<img", html);
    }

    [Fact]
    public async Task CustomisingABuiltInOpensAnEditorForACopy()
    {
        await Renderer.RenderAsync<ThemeEditor>(new Dictionary<string, object?> { [nameof(ThemeEditor.Id)] = "nord" });
        var html = WebUtility.HtmlDecode(await Renderer.WaitForAsync("Customise Nord"));

        Assert.Contains("saves as a theme of your own", html);
        Assert.Contains("value=\"Nord (custom)\"", html);
        // Every token has a row, grouped.
        foreach (var token in ThemeTokens.All)
            Assert.Contains($"id=\"tok-{token.Name}\"", html);
        foreach (var group in Enum.GetValues<ThemeGroup>())
            Assert.Contains($"<legend>{group}</legend>", html);
        // An optional token Nord leaves unset says what it inherits.
        Assert.Contains("placeholder=\"inherits: var(--panel-2)\"", html);
        // The live preview, in the large form, and the contrast table.
        Assert.Contains("theme-preview theme-scope is-large", html);
        Assert.Contains("Text on a card", html);
        Assert.Contains("A primary button's label", html);
        Assert.Matches(@"\d+\.\d\d:1", html);
        Assert.Contains("Save and use", html);
    }

    [Fact]
    public async Task EditingYourOwnThemeOffersACopy()
    {
        var id = await Get<ThemeStore>().SaveAsync(BuiltInThemes.Nord with { Id = "", Name = "Mine", Author = "Me" });

        await Renderer.RenderAsync<ThemeEditor>(new Dictionary<string, object?> { [nameof(ThemeEditor.Id)] = id });
        var html = WebUtility.HtmlDecode(await Renderer.WaitForAsync("Edit Mine"));

        Assert.Contains("Save as a copy", html);
        Assert.Contains("value=\"Me\"", html);
    }

    [Fact]
    public async Task ADarkOnlyThemesEditorOffersToAddALightEnd()
    {
        await Renderer.RenderAsync<ThemeEditor>(new Dictionary<string, object?> { [nameof(ThemeEditor.Id)] = "dracula" });
        var html = WebUtility.HtmlDecode(await Renderer.WaitForAsync("Customise Dracula"));
        Assert.Contains("id=\"tok-ink\"", html);
        Assert.Contains("--ink: #21222c;", html);
    }

    [Fact]
    public async Task AnUnknownThemeSaysSo()
    {
        await Renderer.RenderAsync<ThemeEditor>(new Dictionary<string, object?> { [nameof(ThemeEditor.Id)] = "nope" });
        var html = await Renderer.WaitForAsync("There is no theme with that id");
        Assert.Contains("Back to Appearance", html);
    }

    /// <summary>The contrast table flags a pair that falls short, in a theme built to fail.</summary>
    [Fact]
    public async Task TheEditorFlagsFailingContrast()
    {
        var poor = BuiltInThemes.LabbyTwo with
        {
            Id = "",
            Name = "Murky",
            Light = null,
            Dark = BuiltInThemes.LabbyTwo.Dark!.With(("muted", "#2a3344")),
        };
        var id = await Get<ThemeStore>().SaveAsync(poor);

        await Renderer.RenderAsync<ThemeEditor>(new Dictionary<string, object?> { [nameof(ThemeEditor.Id)] = id });
        var html = WebUtility.HtmlDecode(await Renderer.WaitForAsync("Edit Murky"));
        Assert.Contains("class=\"is-fail\"", html);
        Assert.Matches(@">\s*Fails\s*<", html);
    }
}
