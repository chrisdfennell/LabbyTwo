using System.Text.RegularExpressions;
using LabbyTwo.Components.Pages.Kinds;
using LabbyTwo.Components.Widgets;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;

namespace LabbyTwo.Tests;

/// <summary>
/// Custom pages: blocks laid out with a width and a height, and whole other pages drawn
/// inline as sections. The layout arithmetic is pinned here without a browser; the
/// sections are rendered for real, because what matters about them — that a page cannot
/// hold itself, and that a plugin's page going missing leaves a note rather than a hole —
/// is only true if it is what actually draws.
/// </summary>
public sealed class CustomPageTests : IDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;

    public CustomPageTests()
    {
        Directory.CreateDirectory(_directory);
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        services.AddTestStorage(_directory);
        services.AddModules(typeof(Registry).Assembly, Path.Combine(_directory, "plugins"),
            LoggerFactory.Create(_ => { }).CreateLogger("test"));
        services.AddSingleton<Registry>();
        services.AddSingleton<AlertRuleStore>();
        services.AddSingleton<HistoryStore>();
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<NotesStore>();
        services.AddSingleton<UndoService>();
        services.AddSingleton<Deletions>();
        services.AddSingleton<ShareTransfer>();
        services.AddSingleton<IJSRuntime, NoScript>();
        services.AddSingleton<IErrorBoundaryLogger, QuietBoundaryLogger>();
        _services = services.BuildServiceProvider();
        _services.GetRequiredService<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => TestHost.Teardown(_services, _directory);

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    // ---- layout ------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(7, 7)]
    [InlineData(40, 12)]
    public void WidthsStayInsideTheTwelveColumns(int width, int expected) =>
        Assert.Equal(expected, PageBlocks.ClampWidth(width));

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(5, 5)]
    [InlineData(99, PageBlocks.MaxRows)]
    public void HeightsAreAutoOrAWholeNumberOfRows(int rows, int expected) =>
        Assert.Equal(expected, PageBlocks.ClampRows(rows));

    [Fact]
    public void HeightStepsGoThroughAutoAndStopAtTheTop()
    {
        Assert.Equal(1, PageBlocks.StepRows(0, 1));
        Assert.Equal(0, PageBlocks.StepRows(1, -1));      // one row, then back to fitting
        Assert.Equal(0, PageBlocks.StepRows(0, -1));
        Assert.Equal(PageBlocks.MaxRows, PageBlocks.StepRows(PageBlocks.MaxRows, 1));
    }

    [Fact]
    public void AFixedHeightSpansRowsAndAnAutoOneDoesNot()
    {
        var fixedHeight = new Widget { Type = "clock", Width = 6, Height = 4 };
        Assert.Equal("w-6 has-rows", PageBlocks.Classes(fixedHeight));
        Assert.Equal("--rows: 4", PageBlocks.Style(fixedHeight));

        var auto = new Widget { Type = PageBlocks.Heading, Width = 30, Height = 0 };
        Assert.Equal("w-12 is-bare", PageBlocks.Classes(auto));
        Assert.Null(PageBlocks.Style(auto));
    }

    /// <summary>
    /// On a phone every block fits its content. That is CSS, not C#, so it is checked in
    /// the stylesheet: the phone breakpoint has to switch the row span and the fixed height
    /// off, or a block keeps a desktop height as a tiny scrolling window in one column.
    /// </summary>
    [Fact]
    public void OnAPhoneEveryBlockFitsItsContent()
    {
        var css = File.ReadAllText(FindAppCss());

        var phone = Regex.Matches(css, @"@media \(max-width: 768px\) \{(?<body>(?:[^{}]|\{[^{}]*\})*)\}")
            .Select(m => m.Groups["body"].Value)
            .FirstOrDefault(body => body.Contains(".custom-grid > .has-rows"));

        Assert.NotNull(phone);
        Assert.Contains("grid-row: auto", phone);
        Assert.Contains("height: auto", phone);

        // And the one-column rule that already applies to every grid covers this one too,
        // because a custom page is a .widget-grid as well.
        Assert.Contains(".widget-grid > .widget { grid-column: span 12; }", css);
    }

    private static string FindAppCss()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "wwwroot", "app.css");
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException("wwwroot/app.css was not found above the test output.");
    }

    // ---- sections ----------------------------------------------------------------------

    [Fact]
    public void APageCannotHoldItselfOrADashboard()
    {
        Assert.False(PageBlocks.CanEmbed(TabKinds.Custom));
        Assert.False(PageBlocks.CanEmbed("CUSTOM"));
        Assert.False(PageBlocks.CanEmbed(TabKinds.Grid));
        Assert.False(PageBlocks.CanEmbed(""));
        Assert.True(PageBlocks.CanEmbed("weather-station"));

        var offered = PageBlocks.Embeddable(Get<Registry>()).Select(k => k.Kind).ToList();
        Assert.DoesNotContain(TabKinds.Custom, offered);
        Assert.DoesNotContain(TabKinds.Grid, offered);
        Assert.Contains(TabKinds.Status, offered);
        Assert.Contains(TabKinds.Notes, offered);
        Assert.Contains("weather-station", offered);

        var nested = Section(TabKinds.Custom);
        Assert.Equal(PageBlocks.SectionState.NotEmbeddable, PageBlocks.Resolve(Get<Registry>(), nested).State);
    }

    [Fact]
    public void TheCustomPageIsAKindLikeAnyOther()
    {
        var kind = Get<Registry>().TabKind(TabKinds.Custom);
        Assert.NotNull(kind);
        Assert.Equal(typeof(CustomTab), kind.Component);

        // The blocks are widget types, so everything that handles a card handles them.
        foreach (var type in new[] { PageBlocks.Section, PageBlocks.Heading, PageBlocks.Divider })
            Assert.NotNull(Get<Registry>().WidgetType(type));
    }

    [Fact]
    public async Task ASectionIsThePageItNamesGivenTheBlocksOwnTab()
    {
        var registry = new Registry([], [], [new EchoKind()]);
        var block = Section(EchoKind.Key, id: "block-1", title: "", settings: new() { ["days"] = "14" });

        var html = await RenderAsync<SectionBlock>(registry, block);

        // The block's id, so what the page keeps per tab belongs to this block; the kind's
        // own name when the block has no title; its settings without the section's key.
        Assert.Contains("echo:block-1|Echo page|echo|14|False", html);
    }

    [Fact]
    public async Task ASectionWhosePluginIsGoneSaysSo()
    {
        var block = Section("agenda");

        Assert.Equal(PageBlocks.SectionState.Missing, PageBlocks.Resolve(Get<Registry>(), block).State);

        var html = await RenderAsync<SectionBlock>(Get<Registry>(), block);
        Assert.Contains("section-placeholder", html);
        Assert.Contains("The “agenda” page this section showed is not installed.", html);
    }

    [Fact]
    public async Task ASectionHoldingACustomPageDrawsANoteNotItself()
    {
        var html = await RenderAsync<SectionBlock>(Get<Registry>(), Section(TabKinds.Custom));

        Assert.Contains("A custom page cannot be a section.", html);
        Assert.DoesNotContain("custom-grid", html);
    }

    [Fact]
    public async Task ASectionWithNothingChosenAsksForOne()
    {
        var html = await RenderAsync<SectionBlock>(Get<Registry>(), Section(""));
        Assert.Contains("This section shows nothing yet.", html);
    }

    [Fact]
    public async Task ASectionThatThrowsCostsOnlyItself()
    {
        var registry = new Registry([], [], [new ThrowingKind()]);
        var html = await RenderAsync<SectionBlock>(registry, Section(ThrowingKind.Key, title: "Broken"));

        Assert.Contains("card-failed", html);
        Assert.Contains("The “Broken” section failed to load.", html);
    }

    [Fact]
    public async Task HeadingsAndDividersDrawTheirText()
    {
        var heading = new Widget
        {
            Type = PageBlocks.Heading,
            Settings = new SettingsBag { ["text"] = "Downstairs", ["level"] = "3", ["icon"] = "🛋️", ["subtitle"] = "Lounge" },
        };
        var html = await RenderAsync<HeadingBlock>(Get<Registry>(), heading);
        Assert.Contains("<h3>🛋️ Downstairs</h3>", html);
        Assert.Contains("Lounge", html);

        var divider = new Widget { Type = PageBlocks.Divider, Settings = new SettingsBag { ["label"] = "Media" } };
        html = await RenderAsync<DividerBlock>(Get<Registry>(), divider);
        Assert.Contains("role=\"separator\"", html);
        Assert.Contains("<span>Media</span>", html);
    }

    /// <summary>
    /// The page as the server first sends it. Headings are there already, sized and placed;
    /// a section and a card are placeholders, because prerendering waits on everything and
    /// a section can be a whole weather page's worth of waiting.
    /// </summary>
    [Fact]
    public async Task ThePrerenderedPageIsLaidOutWithPlaceholdersForTheHeavyBlocks()
    {
        var config = Get<ConfigStore>();
        var tab = new Tab { Id = "page", Slug = "page", Name = "Home", Kind = TabKinds.Custom };
        await config.SaveTabAsync(tab);
        await config.SaveWidgetAsync(new Widget
        {
            Id = "h", TabId = "page", Type = PageBlocks.Heading, Sort = 0, Width = 12,
            Settings = new SettingsBag { ["text"] = "Upstairs" },
        });
        await config.SaveWidgetAsync(new Widget
        {
            Id = "s", TabId = "page", Type = PageBlocks.Section, Sort = 1, Width = 8, Height = 6,
            Settings = new SettingsBag { [PageBlocks.KindKey] = TabKinds.Status },
        });
        await config.SaveWidgetAsync(new Widget { Id = "c", TabId = "page", Type = "clock", Sort = 2, Width = 4, Height = 2 });

        var html = await RenderPageAsync(tab);

        Assert.Contains("widget-grid custom-grid", html);
        Assert.Contains("<h2>Upstairs</h2>", html);
        Assert.Matches("class=\"widget w-8 has-rows is-bare block-page-section *\"[^>]*style=\"--rows: 6\"", html);
        Assert.Matches("class=\"widget w-4 has-rows block-clock *\"[^>]*style=\"--rows: 2\"", html);
        Assert.Equal(2, Regex.Matches(html, "widget-skeleton").Count);
    }

    // ---- storage, deleting, sharing -----------------------------------------------------

    [Fact]
    public async Task AHeightIsSavedWithTheBlock()
    {
        var config = Get<ConfigStore>();
        await config.SaveWidgetAsync(new Widget { Id = "w", TabId = "t", Type = "clock", Width = 6, Height = 5 });

        var saved = Assert.Single(await config.WidgetsForTabAsync("t"));
        Assert.Equal(5, saved.Height);
        Assert.Equal(6, saved.Width);
    }

    [Fact]
    public async Task ANotesSectionTakesItsNotesWithItAndBringsThemBack()
    {
        var config = Get<ConfigStore>();
        var notes = Get<NotesStore>();
        await config.SaveTabAsync(new Tab { Id = "page", Slug = "page", Name = "Page", Kind = TabKinds.Custom });
        var section = Section(TabKinds.Notes, id: "notes-block");
        await config.SaveWidgetAsync(section with { TabId = "page" });
        await notes.SaveAsync(null, "notes-block", "Wi-Fi", "The password is on the fridge.");

        await Get<Deletions>().WidgetAsync(section with { TabId = "page" });
        Assert.Empty(await notes.ForTabAsync("notes-block"));

        Assert.True(await Get<UndoService>().UndoAsync());
        Assert.Single(await notes.ForTabAsync("notes-block"));

        // Deleting the whole page takes the section's notes too, and undo restores them.
        var page = (await config.TabAsync("page"))!;
        await Get<Deletions>().TabAsync(page);
        Assert.Empty(await notes.ForTabAsync("notes-block"));
        Assert.True(await Get<UndoService>().UndoAsync());
        Assert.Single(await notes.ForTabAsync("notes-block"));
    }

    /// <summary>
    /// A custom page shared and imported: every block comes back with its size, and a
    /// weather section's connections — which live in the section's settings, not in the
    /// card's binding — travel as names and land on the matching connection here.
    /// </summary>
    [Fact]
    public async Task ACustomPageRoundTripsThroughSharing()
    {
        var config = Get<ConfigStore>();
        var station = new Connection { Provider = "ambient", Name = "Back garden", Settings = new SettingsBag() };
        await config.SaveConnectionAsync(station);

        var tab = new Tab { Slug = "home", Name = "Home", Icon = "🏡", Kind = TabKinds.Custom };
        await config.SaveTabAsync(tab);

        await config.SaveWidgetAsync(new Widget
        {
            TabId = tab.Id, Type = PageBlocks.Heading, Sort = 0, Width = 12,
            Settings = new SettingsBag { ["text"] = "Outside", ["level"] = "2" },
        });
        await config.SaveWidgetAsync(new Widget
        {
            TabId = tab.Id, Type = PageBlocks.Section, Title = "Weather", Sort = 1, Width = 8, Height = 9,
            Settings = new SettingsBag
            {
                [PageBlocks.KindKey] = "weather-station",
                ["connection"] = station.Id,
                ["radar"] = "false",
            },
        });
        await config.SaveWidgetAsync(new Widget { TabId = tab.Id, Type = "clock", Sort = 2, Width = 4, Height = 3 });
        await config.SaveWidgetAsync(new Widget
        {
            TabId = tab.Id, Type = PageBlocks.Divider, Sort = 3, Width = 12,
            Settings = new SettingsBag { ["label"] = "Inside" },
        });

        var share = Get<ShareTransfer>();
        var (json, _) = await share.ExportTabAsync(tab.Id);

        // The connection went out as a name, never as this install's id.
        Assert.DoesNotContain(station.Id, json);
        Assert.Contains("Back garden", json);

        var result = await share.ApplyAsync(await share.PlanAsync(ShareTransfer.Read(json)));
        Assert.Equal(4, result.Widgets);

        var copy = (await config.TabsAsync()).Single(t => t.Slug == result.TabSlug);
        Assert.Equal(TabKinds.Custom, copy.Kind);

        var blocks = await config.WidgetsForTabAsync(copy.Id);
        Assert.Equal(
            [PageBlocks.Heading, PageBlocks.Section, "clock", PageBlocks.Divider],
            blocks.Select(b => b.Type));

        var weather = blocks[1];
        Assert.Equal("Weather", weather.Title);
        Assert.Equal((8, 9), (weather.Width, weather.Height));
        Assert.Equal("weather-station", weather.Settings.Get(PageBlocks.KindKey));
        Assert.Equal(station.Id, weather.Settings.Get("connection"));
        Assert.Equal("false", weather.Settings.Get("radar"));

        Assert.Equal(3, blocks[2].Height);
        Assert.Equal("Inside", blocks[3].Settings.Get("label"));
    }

    [Fact]
    public async Task AWholeConfigExportKeepsHeights()
    {
        var config = Get<ConfigStore>();
        await config.SaveTabAsync(new Tab { Id = "page", Slug = "page", Name = "Page", Kind = TabKinds.Custom });
        await config.SaveWidgetAsync(new Widget { Id = "w", TabId = "page", Type = "clock", Width = 6, Height = 7 });

        var transfer = ActivatorUtilities.CreateInstance<ConfigTransfer>(_services);
        var json = await transfer.ExportAsync(includeSecrets: false);

        await config.SaveWidgetAsync(new Widget { Id = "w", TabId = "page", Type = "clock", Width = 6, Height = 0 });
        await transfer.ImportAsync(json);

        Assert.Equal(7, Assert.Single(await config.WidgetsForTabAsync("page")).Height);
    }

    [Fact]
    public void AFileFromBeforeHeightsStillReads()
    {
        // No "height" anywhere: what every export written before custom pages looks like.
        var share = ShareTransfer.Read("""
            { "version": 1, "kind": "widget",
              "widgets": [ { "type": "clock", "title": "", "sort": 0, "width": 4,
                             "settings": {}, "connections": {} } ] }
            """);

        Assert.Equal(0, Assert.Single(share.Widgets).Height);
    }

    // ---- helpers -----------------------------------------------------------------------

    private static Widget Section(string kind, string id = "section", string title = "", SettingsBag? settings = null)
    {
        var bag = settings ?? new SettingsBag();
        if (kind.Length > 0)
            bag[PageBlocks.KindKey] = kind;
        return new Widget { Id = id, Type = PageBlocks.Section, Title = title, Width = 12, Settings = bag };
    }

    private async Task<string> RenderAsync<TComponent>(Registry registry, Widget widget) where TComponent : IComponent
    {
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton<IErrorBoundaryLogger, QuietBoundaryLogger>()
            .AddSingleton(registry)
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Context"] = new WidgetContext(widget, null),
            });
            var output = await renderer.RenderComponentAsync<TComponent>(parameters);
            // Decoded, so an emoji or a curly quote can be asserted as itself rather than as
            // the entity the encoder writes it as.
            return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }

    private async Task<string> RenderPageAsync(Tab tab)
    {
        await using var renderer = new HtmlRenderer(_services, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?> { ["Tab"] = tab });
            var output = await renderer.RenderComponentAsync<CustomTab>(parameters);
            // Decoded, so an emoji or a curly quote can be asserted as itself rather than as
            // the entity the encoder writes it as.
            return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }

    private sealed class QuietBoundaryLogger : IErrorBoundaryLogger
    {
        public ValueTask LogErrorAsync(Exception exception) => ValueTask.CompletedTask;
    }

    /// <summary>Static rendering never runs a script; the page asks for one only after it is interactive.</summary>
    private sealed class NoScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => default;
    }

    private sealed class EchoKind : ITabKind
    {
        public const string Key = "echo";
        public string Kind => Key;
        public string DisplayName => "Echo page";
        public string Icon => "";
        public string Description => "";
        public Type Component => typeof(EchoPage);
    }

    private sealed class EchoPage : ComponentBase
    {
        [Parameter] public Tab Tab { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            builder.AddContent(0,
                $"echo:{Tab.Id}|{Tab.Name}|{Tab.Kind}|{Tab.Settings.Get("days")}|{Tab.Settings.ContainsKey(PageBlocks.KindKey)}");
    }

    private sealed class ThrowingKind : ITabKind
    {
        public const string Key = "throws";
        public string Kind => Key;
        public string DisplayName => "Throws";
        public string Icon => "";
        public string Description => "";
        public Type Component => typeof(ThrowingPage);
    }

    private sealed class ThrowingPage : ComponentBase
    {
        [Parameter] public Tab Tab { get; set; } = default!;

        protected override void OnInitialized() => throw new InvalidOperationException("the plugin answered in Klingon");
    }
}
