#pragma warning disable BL0006 // A renderer that can press buttons has to read the render tree it is given.
using System.Runtime.ExceptionServices;
using System.Text;
using LabbyTwo.Components.Shared;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LabbyTwo.Tests;

/// <summary>
/// Runbooks drawn by the real components: {{down}}, {{if}} sections switching as the
/// monitor's verdict changes, and {{button}} — including pressing it, through a renderer
/// that can dispatch clicks, to show the press goes through ActionRunner and that a
/// Dangerous action asks first whatever the provider says.
/// </summary>
public sealed class RunbookRenderTests : IAsyncDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _host;
    private readonly ServiceProvider _services;
    private readonly HtmlRenderer _renderer;
    private readonly Stub _stub = new();

    public RunbookRenderTests()
    {
        _host = TestHost.ReadyHost(_directory);
        var config = _host.GetRequiredService<ConfigStore>();
        var history = _host.GetRequiredService<HistoryStore>();
        // The real providers and the stub, so "http" connections and the stub's actions
        // are both known.
        var registry = new Registry(
            _host.GetServices<IConnectionProvider>().Append(_stub),
            _host.GetServices<IWidgetType>(),
            _host.GetServices<ITabKind>());
        // One failed probe is down, so a test can take something down in one step.
        var options = Options.Create(new LabbyOptions
        {
            DatabasePath = Path.Combine(_directory, "test.db"),
            FailuresBeforeDown = 1,
        });
        var health = new HealthMonitor(config, registry, history, options, NullLogger<HealthMonitor>.Instance);

        _services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton<IErrorBoundaryLogger, QuietBoundaryLogger>()
            .AddSingleton(config)
            .AddSingleton(registry)
            .AddSingleton(history)
            .AddSingleton<Markdown>()
            .AddSingleton(health)
            .AddSingleton(new ActionRunner(registry, health, config, NullLogger<ActionRunner>.Instance))
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
    private HealthMonitor Health => _services.GetRequiredService<HealthMonitor>();

    /// <summary>A connection whose probe answers whatever <see cref="Stub.Up"/> says, and which has actions.</summary>
    private sealed class Stub : IConnectionProvider
    {
        public string Type => "stub";
        public string DisplayName => "Stub";
        public string Icon => "";
        public string Description => "";
        public IReadOnlyList<FieldSpec> Fields => [];

        public bool Up { get; set; } = true;
        public List<string> Ran { get; } = [];

        public IReadOnlyList<ProviderAction> Actions =>
        [
            // Asks to skip its confirmation, which a Dangerous action may not do.
            new("reboot", "Reboot") { Dangerous = true, Confirms = false, Disrupts = TimeSpan.FromMinutes(15) },
            new("poke", "Poke") { Confirms = false },
            new("wake", "Wake it"),
        ];

        // Wake is declared but only offered with a MAC, as the QNAP does it.
        public IReadOnlyList<ProviderAction> ActionsFor(Connection connection) =>
            connection.Settings.Get("mac").Length > 0 ? Actions : [.. Actions.Where(a => a.Id != "wake")];

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
            Task.FromResult(Up ? ProbeResult.Up(TimeSpan.Zero) : ProbeResult.Down(TimeSpan.Zero, "Connection refused"));

        public Task<ActionResult> RunActionAsync(Connection connection, ProviderAction action, SettingsBag input, CancellationToken ct)
        {
            Ran.Add(action.Id);
            return Task.FromResult(ActionResult.Done($"{action.Label} sent."));
        }
    }

    private async Task<Connection> ConnectionAsync(string name, string provider = "stub")
    {
        var connection = new Connection { Provider = provider, Name = name };
        await Config.SaveConnectionAsync(connection);
        return connection;
    }

    private Task<HtmlRootComponent> RenderAsync(string markdown) =>
        _renderer.Dispatcher.InvokeAsync(() => _renderer.RenderComponentAsync<LiveMarkdown>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(LiveMarkdown.Content)] = markdown })));

    private async Task<string> HtmlAsync(HtmlRootComponent root) =>
        System.Net.WebUtility.HtmlDecode(await _renderer.Dispatcher.InvokeAsync(() => root.ToHtmlString()));

    /// <summary>The (decoded) HTML once <paramref name="done"/> says so, or whatever it was when time ran out.</summary>
    private async Task<string> WaitForAsync(HtmlRootComponent root, Func<string, bool> done)
    {
        var until = DateTime.UtcNow + Deadline;
        while (true)
        {
            var html = await HtmlAsync(root);
            if (done(html) || DateTime.UtcNow > until)
                return html;
            await Task.Delay(25);
        }
    }

    private Task<string> WaitForAsync(HtmlRootComponent root, string expected) =>
        WaitForAsync(root, html => html.Contains(expected, StringComparison.Ordinal));

    private const string Runbook = """
        {{down}}

        {{if down: The NAS}}
        ### The NAS is down
        1. Check the power light.
        {{else}}
        The NAS is fine.
        {{end}}
        """;

    [Fact]
    public async Task TheSectionSwitchesWithTheVerdictAndTheHiddenOneIsNotInThePage()
    {
        var nas = await ConnectionAsync("The NAS");
        _stub.Up = true;
        await Health.RefreshAsync(nas);

        var root = await RenderAsync(Runbook);
        var up = await WaitForAsync(root, "The NAS is fine.");

        Assert.Contains("Everything’s up", up);
        Assert.DoesNotContain("Check the power light", up);
        Assert.DoesNotContain("{{", up);

        _stub.Up = false;
        await Health.RefreshAsync(nas);
        var down = await WaitForAsync(root, "Check the power light");

        Assert.DoesNotContain("The NAS is fine.", down);
        Assert.Contains("<h3 id=\"the-nas-is-down\">The NAS is down</h3>", down);
        Assert.DoesNotContain("Everything’s up", down);
        // {{down}} lists it with its dot, how long, and what the probe said.
        Assert.Matches(@"status-dot status-down.*<strong>The NAS</strong>.*down for under a minute.*Connection refused", down.ReplaceLineEndings(" "));

        _stub.Up = true;
        await Health.RefreshAsync(nas);
        var back = await WaitForAsync(root, "The NAS is fine.");
        Assert.DoesNotContain("Check the power light", back);
    }

    [Fact]
    public async Task WhileStillCheckingNeitherPartIsShown()
    {
        await ConnectionAsync("The NAS");

        var root = await RenderAsync("{{if down: The NAS}}\nDOWN\n{{end}}\n{{if up: The NAS}}\nUP\n{{end}}\nend of page");
        var html = await WaitForAsync(root, "end of page");
        // Give the scope time to land; neither part may appear once it has.
        await Task.Delay(200);
        html = await HtmlAsync(root);

        Assert.DoesNotContain("DOWN", html);
        Assert.DoesNotContain("UP", html);
    }

    [Fact]
    public async Task AConditionNamingNothingHidesBothPartsAndSaysWhy()
    {
        var root = await RenderAsync("{{if down: Fridge}}\nA\n{{else}}\nB\n{{end}}\n");
        var html = await WaitForAsync(root, "sc-problem");

        Assert.Contains("No connection called “Fridge”. ({{if down: Fridge}})", html);
        Assert.DoesNotContain("<p>A</p>", html);
        Assert.DoesNotContain("<p>B</p>", html);
    }

    [Fact]
    public async Task MistakesInTheStructureAreVisibleNotSwallowed()
    {
        var root = await RenderAsync("First\n\n{{end}}\n\n{{if all up}}\nStill shown\n");
        var html = await WaitForAsync(root, "never closed");

        Assert.Contains("<p>First</p>", html);
        Assert.Contains("{{end}} on line 3 has no {{if …}} or {{details: …}} to end.", html);
        Assert.Contains("<p>Still shown</p>", html);
    }

    [Fact]
    public async Task ASectionWordInASentenceIsAQuestionMark()
    {
        var root = await RenderAsync("Stop here {{end}} please.");
        var html = await WaitForAsync(root, "sc-problem");

        Assert.Contains("each go on a line of their own", html);
    }

    [Fact]
    public async Task DownIsEverythingsUpWhenNothingIs()
    {
        var root = await RenderAsync("{{down}}");
        var html = await WaitForAsync(root, "Everything’s up");

        Assert.Contains("status-dot status-up", html);
        Assert.DoesNotContain("<p>", html);
    }

    [Fact]
    public async Task AButtonIsTheConnectionsOwnActionWithAnOptionalLabel()
    {
        await ConnectionAsync("The NAS");

        var root = await RenderAsync("If stuck, {{button: The NAS / reboot label=\"Reboot the NAS\"}} and wait.\n\n{{button: The NAS / Poke}}");
        var html = await WaitForAsync(root, "Poke");

        Assert.Contains("<p>If stuck, <span class=\"md-action\"><button type=\"button\" class=\"btn  btn-outline-danger btn-sm\"", html);
        Assert.Contains("Reboot the NAS</span></button>", html);
        Assert.Contains(" Poke</span></button>", html);
        Assert.Empty(_stub.Ran);
    }

    [Fact]
    public async Task AnUnknownActionListsWhatTheConnectionCanDo()
    {
        await ConnectionAsync("The NAS");

        var root = await RenderAsync("{{button: The NAS / explode}}");
        var html = await WaitForAsync(root, "sc-problem");

        Assert.Contains("The NAS has no action called “explode”. It can: reboot (Reboot), poke (Poke).", html);
        Assert.DoesNotContain("<button", html);
    }

    [Fact]
    public async Task AnActionTheSettingsRuleOutIsNotDrawn()
    {
        await ConnectionAsync("The NAS");

        var root = await RenderAsync("{{button: The NAS / wake}}");
        var html = await WaitForAsync(root, "sc-problem");

        Assert.Contains("cannot “Wake it” with its current settings", html);
        Assert.DoesNotContain("<button", html);
    }

    [Fact]
    public async Task ScriptInAButtonOrAConditionStaysText()
    {
        await ConnectionAsync("The NAS");

        var root = await RenderAsync(
            "{{button: The NAS / poke label=\"<script>alert(1)</script>\"}}\n\n{{if down: \"<img src=x onerror=alert(2)>\"}}\nx\n{{end}}\n");
        // Undecoded this time: what matters is exactly what the browser would be sent.
        await WaitForAsync(root, html => html.Contains("<button", StringComparison.Ordinal) && html.Contains("sc-problem", StringComparison.Ordinal));
        var raw = await _renderer.Dispatcher.InvokeAsync(() => root.ToHtmlString());

        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", raw);
        Assert.Contains("&lt;img src=x onerror=alert(2)&gt;", raw);
        Assert.DoesNotContain("<script", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", raw, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- pressing it ----------

    [Fact]
    public async Task ADangerousButtonAsksFirstEvenWhenItsProviderSaysNotTo()
    {
        var nas = await ConnectionAsync("The NAS");
        await using var clicks = new ClickRenderer(_services);
        await clicks.RenderAsync("{{button: The NAS / reboot}}");

        await clicks.ClickAsync(await clicks.WaitForButtonAsync(b => b.Text == "Reboot" && b.Class.Contains("btn-outline-danger")));

        // The dialog, and nothing run yet.
        var confirm = await clicks.WaitForButtonAsync(b => b.Text == "Reboot" && b.Class.Contains("btn-danger") && !b.Class.Contains("outline"));
        Assert.Empty(_stub.Ran);

        await clicks.ClickAsync(confirm);

        Assert.Equal(["reboot"], _stub.Ran);
        // The silence is taken by ActionRunner, not the provider: seeing it proves the
        // press went through the runner rather than straight to the provider.
        var stored = await Config.ConnectionAsync(nas.Id);
        Assert.True(stored!.IsSilenced(DateTimeOffset.Now));
    }

    [Fact]
    public async Task AnOrdinaryButtonThatNeedsNoConfirmingRunsOnThePress()
    {
        await ConnectionAsync("The NAS");
        await using var clicks = new ClickRenderer(_services);
        await clicks.RenderAsync("Try {{button: The NAS / poke}} first.");

        await clicks.ClickAsync(await clicks.WaitForButtonAsync(b => b.Text == "Poke"));

        Assert.Equal(["poke"], _stub.Ran);
    }

    /// <summary>
    /// Enough of a browser to press a button: it keeps each button's click handler as the
    /// button is first drawn, and dispatches a click to it as the real renderer would.
    /// </summary>
    private sealed class ClickRenderer(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        public sealed record Button(string Text, string Class, ulong Handler);

        private readonly List<Button> _buttons = [];
        private readonly object _lock = new();

        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        protected override void HandleException(Exception exception) => ExceptionDispatchInfo.Capture(exception).Throw();

        protected override Task UpdateDisplayAsync(in RenderBatch batch)
        {
            var frames = batch.ReferenceFrames;
            for (var i = 0; i < frames.Count; i++)
            {
                var frame = frames.Array[i];
                if (frame.FrameType != RenderTreeFrameType.Element || frame.ElementName != "button")
                    continue;
                ulong handler = 0;
                var text = new StringBuilder();
                var cssClass = "";
                for (var j = i + 1; j < i + frame.ElementSubtreeLength && j < frames.Count; j++)
                {
                    var child = frames.Array[j];
                    if (child.FrameType == RenderTreeFrameType.Attribute && child.AttributeName == "onclick")
                        handler = child.AttributeEventHandlerId;
                    else if (child.FrameType == RenderTreeFrameType.Attribute && child.AttributeName == "class")
                        cssClass = child.AttributeValue?.ToString() ?? "";
                    else if (child.FrameType == RenderTreeFrameType.Text)
                        text.Append(child.TextContent);
                    else if (child.FrameType == RenderTreeFrameType.Markup)
                        text.Append(child.MarkupContent);
                }
                if (handler != 0)
                {
                    lock (_lock)
                        _buttons.Add(new Button(text.ToString().Trim(), cssClass, handler));
                }
            }
            return Task.CompletedTask;
        }

        public Task RenderAsync(string markdown) => Dispatcher.InvokeAsync(async () =>
        {
            var id = AssignRootComponentId(InstantiateComponent(typeof(LiveMarkdown)));
            await RenderRootComponentAsync(id, ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(LiveMarkdown.Content)] = markdown,
            }));
        });

        /// <summary>The newest button drawn that matches, waiting for one to appear.</summary>
        public async Task<Button> WaitForButtonAsync(Func<Button, bool> match)
        {
            var until = DateTime.UtcNow + Deadline;
            while (true)
            {
                lock (_lock)
                {
                    if (_buttons.LastOrDefault(match) is { } found)
                        return found;
                }
                if (DateTime.UtcNow > until)
                    throw new TimeoutException("No such button was drawn: " + string.Join(", ", _buttons.Select(b => $"{b.Text} [{b.Class}]")));
                await Task.Delay(25);
            }
        }

        public Task ClickAsync(Button button) =>
            Dispatcher.InvokeAsync(() => DispatchEventAsync(button.Handler, null, new MouseEventArgs()));
    }

    private sealed class QuietBoundaryLogger : IErrorBoundaryLogger
    {
        public ValueTask LogErrorAsync(Exception exception) => ValueTask.CompletedTask;
    }
}
