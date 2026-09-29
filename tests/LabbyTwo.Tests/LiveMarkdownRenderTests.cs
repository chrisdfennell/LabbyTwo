using LabbyTwo.Components.Shared;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LabbyTwo.Tests;

/// <summary>
/// Markdown with live values in it, drawn by the real components with the framework's own
/// HTML renderer against a real database — so what is checked is what a page would show:
/// the value where the shortcode was, a question mark where it named nothing, script as
/// text, and the value changing when the readings behind it arrive.
/// </summary>
public sealed class LiveMarkdownRenderTests : IAsyncDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _host;
    private readonly ServiceProvider _services;
    private readonly HtmlRenderer _renderer;

    public LiveMarkdownRenderTests()
    {
        _host = TestHost.ReadyHost(_directory);
        var config = _host.GetRequiredService<ConfigStore>();
        var registry = _host.GetRequiredService<Registry>();
        var history = _host.GetRequiredService<HistoryStore>();
        var options = _host.GetRequiredService<IOptions<LabbyOptions>>();

        _services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton<IErrorBoundaryLogger, QuietBoundaryLogger>()
            .AddSingleton(config)
            .AddSingleton(registry)
            .AddSingleton(history)
            .AddSingleton<Markdown>()
            .AddSingleton(new HealthMonitor(config, registry, history, options, NullLogger<HealthMonitor>.Instance))
            .AddSingleton(new LatestReadings(history, config, NullLogger<LatestReadings>.Instance))
            .AddSingleton(new CapacityForecasts(config, registry, history, NullLogger<CapacityForecasts>.Instance))
            .AddSingleton(new Offload(NullLogger<Offload>.Instance))
            .AddSingleton(new DisplayUnits(_host.GetRequiredService<AppSettingsStore>()))
            .BuildServiceProvider();
        _renderer = new HtmlRenderer(_services, NullLoggerFactory.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _renderer.DisposeAsync();
        await _services.DisposeAsync();
        TestHost.Teardown(_host, _directory);
    }

    private ConfigStore Config => _host.GetRequiredService<ConfigStore>();

    private async Task<Connection> ConnectionAsync(string name)
    {
        var connection = new Connection { Provider = "http", Name = name };
        await Config.SaveConnectionAsync(connection);
        return connection;
    }

    private Task<HtmlRootComponent> RenderAsync(string markdown, string? owner = null, EmbedChain? chain = null) =>
        _renderer.Dispatcher.InvokeAsync(() =>
        {
            RenderFragment content = builder =>
            {
                builder.OpenComponent<LiveMarkdown>(0);
                builder.AddComponentParameter(1, nameof(LiveMarkdown.Content), markdown);
                builder.AddComponentParameter(2, nameof(LiveMarkdown.OwnerId), owner);
                builder.CloseComponent();
            };
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(Host.Chain)] = chain,
                [nameof(Host.ChildContent)] = content,
            });
            return _renderer.RenderComponentAsync<Host>(parameters);
        });

    /// <summary>The HTML once it contains <paramref name="expected"/>, or whatever it was when time ran out.</summary>
    private async Task<string> WaitForAsync(HtmlRootComponent root, string expected)
    {
        var until = DateTime.UtcNow + Deadline;
        string html;
        while (true)
        {
            html = await _renderer.Dispatcher.InvokeAsync(() => root.ToHtmlString());
            if (html.Contains(expected, StringComparison.Ordinal) || DateTime.UtcNow > until)
                return html;
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task MarkdownWithoutShortcodesIsTheRenderersOwnHtml()
    {
        const string text = "# Hello\n\nA *plain* note.";

        var root = await RenderAsync(text);
        var html = await _renderer.Dispatcher.InvokeAsync(() => root.ToHtmlString());

        Assert.Equal(new Markdown().ToHtml(text), html);
    }

    [Fact]
    public async Task AStatusIsDrawnInsideTheSentence()
    {
        await ConnectionAsync("NAS");

        var root = await RenderAsync("The NAS is {{status: nas}} today.");
        var html = await WaitForAsync(root, "checking");

        Assert.Matches(@"<p>The NAS is <span class=""sc-value""[^>]*><span class=""status-dot status-unknown""[^>]*></span>checking</span> today\.</p>", html);
    }

    [Fact]
    public async Task AMetricChangesWhenItsReadingsArrive()
    {
        var nas = await ConnectionAsync("NAS");
        // Written straight to the table rather than through HistoryStore, which would also
        // put it in memory. This way the first draw finds nothing — LatestReadings answers
        // from memory and starts a load — and the only thing that can bring the value in
        // afterwards is its Changed event when that load lands. No sweep runs in this test.
        await using (var db = await _host.GetRequiredService<Db>().OpenAsync())
        {
            var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO samples (connection_id, metric, ts, value) VALUES ($id, 'disk_percent', $ts, 72.4)";
            cmd.Parameters.AddWithValue("$id", nas.Id);
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync();
        }

        var root = await RenderAsync("Disk: {{metric: NAS / Disk used}}.");
        var html = await WaitForAsync(root, "72%");

        Assert.Contains("Disk: <span class=\"sc-value\" title=\"NAS · Disk used\">72%</span>.", System.Net.WebUtility.HtmlDecode(html));
    }

    [Fact]
    public async Task UnknownNamesBecomeAQuestionMarkWithTheReason()
    {
        await ConnectionAsync("NAS");

        var root = await RenderAsync("{{status: Fridge}} {{metric: NAS / nope}} {{stauts: NAS}}");
        var html = System.Net.WebUtility.HtmlDecode(await WaitForAsync(root, "Fridge"));

        Assert.Contains("No connection called “Fridge”", html);
        Assert.Contains("has not reported a metric called “nope”", html);
        Assert.Contains("“stauts” is not a live value", html);
        Assert.Equal(3, html.Split("class=\"sc-problem\"").Length - 1);
    }

    [Fact]
    public async Task ScriptInAShortcodeIsShownAsText()
    {
        var root = await RenderAsync("""Hi {{status: "<script>alert(1)</script>"}} and <img src=x onerror=alert(2)>""");
        var html = await WaitForAsync(root, "sc-problem");

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public async Task ACardInTheMiddleOfASentenceIsRefused()
    {
        var root = await RenderAsync("See {{widget: CPU}} for more.");
        var html = await WaitForAsync(root, "sc-problem");

        Assert.Contains("goes on a line of its own", html);
        Assert.DoesNotContain("md-embed", html);
    }

    [Fact]
    public async Task ACardThatEmbedsItselfStops()
    {
        var tab = new Tab { Slug = "t", Name = "T" };
        await Config.SaveTabAsync(tab);
        var widget = new Widget
        {
            TabId = tab.Id,
            Type = "markdown",
            Title = "Loop",
            Settings = new SettingsBag { ["content"] = "{{widget: Loop}}" },
        };
        await Config.SaveWidgetAsync(widget);

        var root = await RenderAsync("{{widget: Loop}}", owner: widget.Id);
        var html = await WaitForAsync(root, "not drawn inside itself");

        Assert.Contains("not drawn inside itself", html);
    }

    [Fact]
    public async Task EmbedsStopAtTheDepthLimit()
    {
        var deep = EmbedChain.Empty;
        for (var i = 0; i < EmbedChain.MaxDepth; i++)
            deep = deep.With("owner" + i);

        var root = await RenderAsync("{{card: clock}}", chain: deep);
        var html = await WaitForAsync(root, "nested more than");

        Assert.Contains($"nested more than {EmbedChain.MaxDepth} deep", html);
    }

    [Fact]
    public async Task AnAdHocCardIsFramedAndNamed()
    {
        var root = await RenderAsync("{{card: clock title=\"Time here\"}}");
        var html = await WaitForAsync(root, "Time here");

        // Statically rendered, so the card itself is a placeholder until a circuit starts;
        // the frame and its title are what this renderer can show.
        Assert.Contains("class=\"md-embed\"", html);
        Assert.Contains("Time here", html);
        Assert.DoesNotContain("<p>", html);
    }

    /// <summary>Stands in for whatever a real page puts around the Markdown, including an outer embed chain.</summary>
    private sealed class Host : ComponentBase
    {
        [Parameter] public EmbedChain? Chain { get; set; }
        [Parameter] public RenderFragment? ChildContent { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (Chain is null)
            {
                builder.AddContent(0, ChildContent);
                return;
            }
            builder.OpenComponent<CascadingValue<EmbedChain>>(1);
            builder.AddComponentParameter(2, nameof(CascadingValue<EmbedChain>.Value), Chain);
            builder.AddComponentParameter(3, nameof(CascadingValue<EmbedChain>.ChildContent), ChildContent);
            builder.CloseComponent();
        }
    }

    private sealed class QuietBoundaryLogger : IErrorBoundaryLogger
    {
        public ValueTask LogErrorAsync(Exception exception) => ValueTask.CompletedTask;
    }
}
