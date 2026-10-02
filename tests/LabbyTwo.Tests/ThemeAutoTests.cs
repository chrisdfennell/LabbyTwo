#pragma warning disable BL0006 // The interactive test renderer is a Renderer, which is the whole point of it.
using System.Net;
using System.Reflection;
using LabbyTwo.Components.Layout;
using LabbyTwo.Components.Shared;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabbyTwo.Tests;

/// <summary>
/// Day/night switching and per-screen themes against a real database and a clock the test
/// moves by hand: the cache that re-decides at a switch, the scheduler that tells open pages,
/// ThemeSync pushing the new end, the settings components, and the server-rendered page.
/// </summary>
public sealed class ThemeAutoTests : IAsyncDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private readonly LoginThrottleTests.ManualClock _clock = new() { Now = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero) };
    private readonly RecordingScript _script = new();
    private InteractiveRenderer? _renderer;
    private WebApplicationFactory<Program>? _factory;

    /// <summary>Records what ThemeSync sends to the page.</summary>
    private sealed class RecordingScript : Microsoft.JSInterop.IJSRuntime
    {
        public List<(string Identifier, object?[]? Args)> Calls { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            lock (Calls)
                Calls.Add((identifier, args));
            return ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);

        public string? LastMode()
        {
            lock (Calls)
            {
                var last = Calls.LastOrDefault(c => c.Identifier == "labbyTheme.apply");
                return last.Args?[0]?.GetType().GetProperty("mode")?.GetValue(last.Args[0]) as string;
            }
        }

        public int Pushes()
        {
            lock (Calls)
                return Calls.Count(c => c.Identifier == "labbyTheme.apply");
        }
    }

    public ThemeAutoTests()
    {
        _services = TestHost.Build(_directory, services =>
        {
            services.AddSingleton<TimeProvider>(_clock);
            services.AddSingleton<ThemeStore>();
            services.AddSingleton<ThemeService>();
            services.AddSingleton<ThemeScheduler>();
            services.AddSingleton<Offload>();
            services.AddSingleton<Microsoft.JSInterop.IJSRuntime>(_script);
        });
        Get<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();
        Get<ThemeService>().Zone = TimeZoneInfo.Utc;
    }

    public async ValueTask DisposeAsync()
    {
        if (_renderer is not null)
            await _renderer.DisposeAsync();
        _factory?.Dispose();
        TestHost.Teardown(_services, _directory);
    }

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private InteractiveRenderer Renderer => _renderer ??= new InteractiveRenderer(_services);

    private Task SaveAsync(params (string Key, string Value)[] values) =>
        Get<AppSettingsStore>().SaveAsync(values.ToDictionary(v => v.Key, v => v.Value));

    /// <summary>Light 07:00–19:00 UTC on the dashboard.</summary>
    private Task OnAScheduleAsync() => SaveAsync(
        (Appearance.ThemeKey, ThemeModes.Schedule),
        (ThemeTimes.LightFromKey, "07:00"),
        (ThemeTimes.LightToKey, "19:00"));

    // ---- the cache --------------------------------------------------------------------

    [Fact]
    public async Task AScheduledModeIsServedFromTheCacheUntilItsSwitchThenReDecided()
    {
        await OnAScheduleAsync();
        var themes = Get<ThemeService>();

        _clock.Now = new DateTimeOffset(2026, 6, 1, 18, 59, 0, TimeSpan.Zero);
        var before = await themes.ActiveAsync();
        Assert.Equal("light", before.ModeAttribute(Appearance.Default));
        Assert.Same(before, await themes.ActiveAsync());

        _clock.Now = new DateTimeOffset(2026, 6, 1, 19, 0, 1, TimeSpan.Zero);
        var after = await themes.ActiveAsync();
        Assert.Equal("dark", after.ModeAttribute(Appearance.Default));
        // Only the end changed: the CSS is the same string, not rendered again.
        Assert.Same(before.Css, after.Css);
        Assert.Equal("dark", (await themes.SnapshotAsync()).Mode);
    }

    [Fact]
    public async Task ADashboardOnTheSunWithNoHomeFollowsTheDevice()
    {
        await SaveAsync((Appearance.ThemeKey, ThemeModes.Sun));
        Assert.Null((await Get<ThemeService>().SnapshotAsync()).Mode);
        Assert.Equal(ThemeSchedule.NoLocation, (await Get<ThemeService>().DecisionAsync()).Problem);
    }

    [Fact]
    public async Task ADashboardOnTheSunWithAHomeIsLightAtMidday()
    {
        await SaveAsync(
            (Appearance.ThemeKey, ThemeModes.Sun),
            (HomeLocation.LatitudeKey, "51.5"),
            (HomeLocation.LongitudeKey, "0"));
        Assert.Equal("light", (await Get<ThemeService>().SnapshotAsync()).Mode);

        _clock.Now = new DateTimeOffset(2026, 6, 1, 23, 0, 0, TimeSpan.Zero);
        Assert.Equal("dark", (await Get<ThemeService>().SnapshotAsync()).Mode);
    }

    // ---- per screen ---------------------------------------------------------------------

    [Fact]
    public async Task EachScreenResolvesItsOwnThemeAndMode()
    {
        await Get<ThemeService>().ApplyAsync(BuiltInThemes.Nord.Id);
        await SaveAsync(
            (Appearance.ThemeKey, ThemeModes.Light),
            (ThemeService.WallThemeKey, BuiltInThemes.Black.Id),
            (ThemeService.WallModeKey, ThemeModes.Dark),
            (ThemeService.PhoneModeKey, ThemeModes.Auto));
        var themes = Get<ThemeService>();

        var dashboard = await themes.SnapshotAsync(ThemeSurface.Dashboard);
        Assert.Equal("nord", (await themes.ActiveAsync(ThemeSurface.Dashboard)).Theme.Id);
        Assert.Equal("light", dashboard.Mode);

        Assert.Equal("black", (await themes.ActiveAsync(ThemeSurface.Wall)).Theme.Id);
        Assert.Equal("dark", (await themes.SnapshotAsync(ThemeSurface.Wall)).Mode);

        Assert.Equal("nord", (await themes.ActiveAsync(ThemeSurface.Phone)).Theme.Id);
        Assert.Null((await themes.SnapshotAsync(ThemeSurface.Phone)).Mode);

        // The family page was given nothing of its own.
        Assert.Equal("nord", (await themes.ActiveAsync(ThemeSurface.Family)).Theme.Id);
        Assert.Equal("light", (await themes.SnapshotAsync(ThemeSurface.Family)).Mode);
    }

    [Fact]
    public async Task AScreensDeletedThemeFallsBackToTheDashboards()
    {
        await Get<ThemeService>().ApplyAsync(BuiltInThemes.Gruvbox.Id);
        await SaveAsync((ThemeService.WallThemeKey, "user-gone"));
        Assert.Equal("gruvbox", (await Get<ThemeService>().ActiveAsync(ThemeSurface.Wall)).Theme.Id);
    }

    [Fact]
    public async Task TheNextSwitchIsTheSoonestOfAnyScreen()
    {
        await OnAScheduleAsync();
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 19, 0, 0, TimeSpan.Zero), await Get<ThemeService>().NextSwitchAsync());

        // A wall that is always dark does not change it; nothing at all automatic means none.
        await SaveAsync((ThemeService.WallModeKey, ThemeModes.Dark), (Appearance.ThemeKey, ThemeModes.Auto));
        Assert.Null(await Get<ThemeService>().NextSwitchAsync());
    }

    // ---- switching live -----------------------------------------------------------------

    [Fact]
    public async Task TheSchedulerRefreshesOpenPagesAtTheSwitchAndNotBefore()
    {
        await OnAScheduleAsync();
        var scheduler = Get<ThemeScheduler>();
        var told = 0;
        Get<ThemeService>().Changed += () => told++;

        _clock.Now = new DateTimeOffset(2026, 6, 1, 18, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 19, 0, 0, TimeSpan.Zero), await scheduler.StepAsync());
        _clock.Now = new DateTimeOffset(2026, 6, 1, 18, 59, 59, TimeSpan.Zero);
        await scheduler.StepAsync();
        Assert.Equal(0, told);

        _clock.Now = new DateTimeOffset(2026, 6, 1, 19, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 6, 2, 7, 0, 0, TimeSpan.Zero), await scheduler.StepAsync());
        Assert.Equal(1, told);
    }

    /// <summary>
    /// The whole chain on an open wall: the scheduler fires at 19:00, ThemeService raises
    /// Changed, and ThemeSync pushes the dark end to the page — nobody touched anything.
    /// </summary>
    [Fact]
    public async Task AnOpenWallIsPushedTheDarkEndAtTheSwitch()
    {
        await OnAScheduleAsync();
        _clock.Now = new DateTimeOffset(2026, 6, 1, 18, 59, 0, TimeSpan.Zero);
        var scheduler = Get<ThemeScheduler>();
        await scheduler.StepAsync();

        await Renderer.RenderAsync<ThemeSync>(new Dictionary<string, object?> { [nameof(ThemeSync.Surface)] = ThemeSurface.Wall });
        await WaitForSubscriberAsync();
        Assert.Equal(0, _script.Pushes());

        _clock.Now = new DateTimeOffset(2026, 6, 1, 19, 0, 0, TimeSpan.Zero);
        await scheduler.StepAsync();

        await WaitAsync(() => _script.Pushes() == 1);
        Assert.Equal("dark", _script.LastMode());
    }

    [Fact]
    public async Task GivingTheWallItsOwnModePushesItToAnOpenWall()
    {
        await Renderer.RenderAsync<ThemeSync>(new Dictionary<string, object?> { [nameof(ThemeSync.Surface)] = ThemeSurface.Wall });
        await WaitForSubscriberAsync();

        await SaveAsync((ThemeService.WallModeKey, ThemeModes.Light));
        await WaitAsync(() => _script.Pushes() == 1);
        Assert.Equal("light", _script.LastMode());

        // A change that only touches another screen is not pushed here.
        await SaveAsync((ThemeService.PhoneModeKey, ThemeModes.Dark));
        await Task.Delay(100);
        Assert.Equal(1, _script.Pushes());
    }

    /// <summary>ThemeSync subscribes after its first render; wait for that before changing anything.</summary>
    private async Task WaitForSubscriberAsync()
    {
        var field = typeof(ThemeService).GetField(nameof(ThemeService.Changed), BindingFlags.Instance | BindingFlags.NonPublic)!;
        await WaitAsync(() => (field.GetValue(Get<ThemeService>()) as Delegate)?.GetInvocationList()
            .Any(d => d.Target?.GetType() == typeof(ThemeSync)) == true);
    }

    private static async Task WaitAsync(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < until)
            await Task.Delay(20);
        Assert.True(done(), "Timed out waiting.");
    }

    // ---- the settings components ----------------------------------------------------------

    [Fact]
    public async Task TheModePickerSaysWhenItNextSwitches()
    {
        await OnAScheduleAsync();

        await Renderer.RenderAsync<ThemeModePicker>(new Dictionary<string, object?>());
        var html = WebUtility.HtmlDecode(await Renderer.WaitForAsync("switches to dark at 19:00"));

        Assert.Contains("Light now — switches to dark at 19:00.", html);
        Assert.Contains("id=\"theme-light-from\"", html);
        Assert.Contains("value=\"07:00\"", html);
        Assert.Matches("aria-pressed=\"true\"[^>]*>On a schedule<", html);
    }

    [Fact]
    public async Task ChoosingTheSunWithNowhereSetSaysHowToSetIt()
    {
        await Renderer.RenderAsync<ThemeModePicker>(new Dictionary<string, object?>());
        await Renderer.WaitForAsync("Follow the sun");

        await Renderer.ClickAsync("Follow the sun");
        var html = WebUtility.HtmlDecode(await Renderer.WaitForAsync("Settings → Where you are"));

        Assert.Equal(ThemeModes.Sun, (await Get<AppSettingsStore>().AllAsync()).Get(Appearance.ThemeKey));
        Assert.Contains("id=\"theme-sunset-offset\"", html);
    }

    [Fact]
    public async Task TheModePickerClampsOffsetsAndRefusesAnEmptyWindow()
    {
        await OnAScheduleAsync();
        await Renderer.RenderAsync<ThemeModePicker>(new Dictionary<string, object?>());
        await Renderer.WaitForAsync("theme-light-to");

        await Renderer.ChangeAsync("theme-light-to", "07:00");
        await Renderer.WaitForAsync("cannot be the same time");
        Assert.Equal("19:00", (await Get<AppSettingsStore>().AllAsync()).Get(ThemeTimes.LightToKey));

        await Renderer.ChangeAsync("theme-light-to", "21:30");
        await Renderer.WaitForAsync("switches to dark at 21:30");

        await SaveAsync((Appearance.ThemeKey, ThemeModes.Sun));
        await Renderer.WaitForAsync("theme-sunset-offset");
        await Renderer.ChangeAsync("theme-sunset-offset", "-600");
        Assert.Equal("-180", (await Get<AppSettingsStore>().AllAsync()).Get(ThemeTimes.SunsetOffsetKey));
    }

    [Fact]
    public async Task ThePerScreenPickersShowEachScreensThemeAndSaveAChoice()
    {
        await Get<ThemeService>().ApplyAsync(BuiltInThemes.Nord.Id);

        await Renderer.RenderAsync<ScreenThemes>(new Dictionary<string, object?>());
        var html = WebUtility.HtmlDecode(await Renderer.WaitForAsync("Family page uses…"));

        Assert.Contains("Wall mode uses…", html);
        Assert.Contains("Phone view uses…", html);
        Assert.Contains("Same as the dashboard (Nord)", html);
        Assert.Contains("Same as the dashboard (Follow the device)", html);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(html, "theme-preview theme-scope").Count);

        await Renderer.ChangeAsync("screen-theme-wall", BuiltInThemes.Black.Id);
        await Renderer.ChangeAsync("screen-mode-wall", ThemeModes.Dark);
        // Something not on the list is not stored.
        await Renderer.ChangeAsync("screen-theme-phone", "user-made-up");

        var settings = await Get<AppSettingsStore>().AllAsync();
        Assert.Equal("black", settings.Get(ThemeService.WallThemeKey));
        Assert.Equal(ThemeModes.Dark, settings.Get(ThemeService.WallModeKey));
        Assert.Equal("", settings.Get(ThemeService.PhoneThemeKey));

        html = WebUtility.HtmlDecode(await Renderer.WaitForAsync("Dark now."));
        Assert.Contains("--ink: #000000;", html); // True black's tile
    }

    // ---- the page as the server sends it ----------------------------------------------------

    /// <summary>
    /// A wall loaded fresh is stamped with the wall's own theme and mode before any script
    /// runs, and the dashboard beside it keeps its own.
    /// </summary>
    [Fact]
    public async Task TheServerRenderedPageWearsItsScreensThemeAndMode()
    {
        var directory = Path.Combine(_directory, "app");
        Directory.CreateDirectory(directory);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseSetting("Labby:DatabasePath", Path.Combine(directory, "labbytwo.db"));
            host.UseSetting("Labby:PluginPath", Path.Combine(directory, "plugins"));
        });
        var client = _factory.CreateClient();

        var settings = _factory.Services.GetRequiredService<AppSettingsStore>();
        await settings.SaveAsync(new Dictionary<string, string>
        {
            [ThemeService.ThemeIdKey] = BuiltInThemes.Nord.Id,
            [Appearance.ThemeKey] = ThemeModes.Light,
            [ThemeService.WallThemeKey] = BuiltInThemes.Black.Id,
            [ThemeService.WallModeKey] = ThemeModes.Dark,
            [ThemeService.PhoneModeKey] = ThemeModes.Dark,
        });

        var wall = await client.GetStringAsync("/wall");
        Assert.Contains("data-theme=\"dark\"", wall);
        Assert.Contains("--ink: #000000;", wall);

        var phone = await client.GetStringAsync("/m");
        Assert.Contains("data-theme=\"dark\"", phone);
        Assert.Contains("--ink: #2e3440;", phone); // Nord, the dashboard's theme, in its dark end

        var dashboard = await client.GetStringAsync("/");
        Assert.Contains("data-theme=\"light\"", dashboard);
        Assert.DoesNotContain("--ink: #000000;", dashboard);
    }
}
