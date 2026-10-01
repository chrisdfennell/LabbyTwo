#pragma warning disable BL0006 // The interactive test renderer is a Renderer, which is the whole point of it.
using System.Net;
using System.Text.RegularExpressions;
using LabbyTwo.Components.Widgets;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace LabbyTwo.Tests;

/// <summary>
/// Checklists in a Markdown card — a custom page's Markdown block, which is what the
/// owner's "Home Lab Roundup" page is, and a dashboard's Markdown card, which is the same
/// component — drawn by the real components against a real database: boxes anyone can
/// tick, seen by every viewer, read from the database once and from memory after; a reset
/// link once something is ticked; and the owner's real page, every shortcode in it, still
/// drawing everything now its boxes are live.
/// </summary>
public sealed partial class CardChecklistRenderTests : IAsyncDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private readonly List<InteractiveRenderer> _renderers = [];
    private readonly RecordingBoundaryLogger _boundary = new();

    /// <summary>What any card in the page threw, so a card that failed quietly still fails the test.</summary>
    private sealed class RecordingBoundaryLogger : IErrorBoundaryLogger
    {
        public System.Collections.Concurrent.ConcurrentQueue<Exception> Errors { get; } = new();

        public ValueTask LogErrorAsync(Exception exception)
        {
            Errors.Enqueue(exception);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A connection with readings and an action, for the page's live values and its button.</summary>
    private sealed class Stub : IConnectionProvider
    {
        public string Type => "stub";
        public string DisplayName => "Stub";
        public string Icon => "";
        public string Description => "";
        public IReadOnlyList<FieldSpec> Fields => [new("url", "Address")];
        public IReadOnlyList<MetricSpec> Metrics => [new("disk_percent", "Disk used", "%", 1), new("cpu_percent", "CPU", "%", 1)];
        public IReadOnlyList<ProviderAction> Actions => [new("poke", "Poke") { Confirms = false }];

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
            Task.FromResult(ProbeResult.Up(TimeSpan.FromMilliseconds(3), "OK",
                new Dictionary<string, double> { ["disk_percent"] = 50, ["cpu_percent"] = 12 }));

        public Task<ActionResult> RunActionAsync(Connection connection, ProviderAction action, SettingsBag input, CancellationToken ct) =>
            Task.FromResult(ActionResult.Done("Poked"));
    }

    public CardChecklistRenderTests()
    {
        _services = TestHost.Build(_directory, services =>
        {
            services.AddSingleton<IConnectionProvider>(new Stub());
            services.AddSingleton<IErrorBoundaryLogger>(_boundary);
            services.AddSingleton<HealthMonitor>();
            services.AddSingleton<AlertService>();
            services.AddSingleton<CapacityForecasts>();
            services.AddSingleton<MetricBaselines>();
            services.AddSingleton<MetricAlertService>();
            services.AddSingleton<LatestReadings>();
            services.AddSingleton<Offload>();
            services.AddSingleton<DisplayUnits>();
            services.AddSingleton<SharedSeries>();
            services.AddSingleton<Markdown>();
            services.AddSingleton<ActionRunner>();
            services.AddSingleton<NotesStore>();
            services.AddSingleton<ChecklistStore>();
            services.AddSingleton<MarkdownChecklists>();
            services.AddSingleton<WidgetHistoryStore>();
        });
        Get<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var renderer in _renderers)
            await renderer.DisposeAsync();
        TestHost.Teardown(_services, _directory);
    }

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private InteractiveRenderer Renderer()
    {
        var renderer = new InteractiveRenderer(_services);
        _renderers.Add(renderer);
        return renderer;
    }

    /// <summary>A Markdown block on a custom page, saved as the page saves one.</summary>
    private async Task<Widget> BlockAsync(string markdown, string id = "roundup")
    {
        var page = new Tab { Id = "home-lab-roundup", Name = "Home Lab Roundup", Slug = "home-lab-roundup", Kind = TabKinds.Custom };
        await Get<ConfigStore>().SaveTabAsync(page);
        var block = new Widget
        {
            Id = id,
            TabId = page.Id,
            Type = WidgetHistoryStore.MarkdownType,
            Width = 12,
            Settings = new SettingsBag { [WidgetHistoryStore.ContentKey] = markdown },
        };
        await Get<ConfigStore>().SaveWidgetAsync(block);
        return block;
    }

    private static Task RenderCardAsync(InteractiveRenderer renderer, Widget block) =>
        renderer.RenderAsync<MarkdownCard>(new Dictionary<string, object?>
        {
            [nameof(MarkdownCard.Context)] = new WidgetContext(block, null),
        });

    private async Task<Connection> ConnectionAsync(string name, string provider = "stub", params (string Key, string Value)[] settings)
    {
        var bag = new SettingsBag();
        foreach (var (key, value) in settings)
            bag[key] = value;
        var connection = new Connection { Provider = provider, Name = name, Settings = bag };
        await Get<ConfigStore>().SaveConnectionAsync(connection);
        return connection;
    }

    private static string Text(string html) => WebUtility.HtmlDecode(html);

    [GeneratedRegex("""<input[^>]*type="checkbox"[^>]*>""")]
    private static partial Regex Box();

    private static List<string> Boxes(string html) => [.. Box().Matches(html).Select(m => m.Value)];

    private static bool Ready(string html, int live) => Boxes(html).Count(b => !b.Contains(" disabled", StringComparison.Ordinal)) == live;

    [Fact]
    public async Task ABlocksBoxesCanBeTickedAndEveryViewerSeesItFromOneRead()
    {
        var block = await BlockAsync("## Before the NAS restart\n\n- [ ] Stopped Plex\n- [x] Took a snapshot\n- [ ] Told the family\n");
        var first = Renderer();
        await RenderCardAsync(first, block);
        var html = await first.WaitForAsync(h => Ready(h, 2));
        var boxes = Boxes(html);
        Assert.Equal(3, boxes.Count);
        Assert.Contains(" checked", boxes[1]);
        Assert.DoesNotContain(" checked", boxes[0]);
        Assert.DoesNotContain("Reset checklist", html);

        // A second viewer — the wall display — is drawn from memory: not one connection opened.
        var before = Get<Db>().Opens;
        var second = Renderer();
        await RenderCardAsync(second, block);
        await second.WaitForAsync(h => Ready(h, 2));
        Assert.Equal(before, Get<Db>().Opens);

        await first.ChangeAsync(0, true);

        // The other viewer's box ticks without it asking for anything; the tick was one write.
        var seen = Text(await second.WaitForAsync(h => Boxes(h)[0].Contains(" checked", StringComparison.Ordinal)));
        Assert.Contains("Ticked by someone, today at", Boxes(seen)[0]);
        Assert.Contains("Reset checklist (1 ticked)", Text(await first.WaitForAsync("Reset checklist (1 ticked)")));
        Assert.Equal(before + 1, Get<Db>().Opens);

        // Kept under the block, not under a note, and the text is untouched.
        var tick = Assert.Single((await Get<ChecklistStore>().TicksAsync(ChecklistOwners.Widget(block.Id))).Values);
        Assert.Equal("Stopped Plex", tick.Text);
        Assert.Empty(await Get<ChecklistStore>().TicksAsync(ChecklistOwners.Note(block.Id)));
        var stored = (await Get<ConfigStore>().WidgetsAsync()).Single(w => w.Id == block.Id);
        Assert.Contains("- [ ] Stopped Plex", stored.Settings.Get(WidgetHistoryStore.ContentKey));
        Assert.Empty(await Get<WidgetHistoryStore>().VersionsAsync(block.Id));
    }

    [Fact]
    public async Task AResetUnticksTheBlockForEveryoneAndSaysSoInWhatChanged()
    {
        var block = await BlockAsync("- [ ] One\n- [ ] Two\n");
        var items = Get<Markdown>().ChecklistItems("- [ ] One\n- [ ] Two\n");
        var owner = ChecklistOwners.Widget(block.Id);
        await Get<ChecklistStore>().SetAsync(owner, items[0], true, "chris", DateTimeOffset.Now);
        await Get<ChecklistStore>().SetAsync(owner, items[1], true, "chris", DateTimeOffset.Now);

        var renderer = Renderer();
        await RenderCardAsync(renderer, block);
        await renderer.WaitForAsync(h => Boxes(h).All(b => b.Contains(" checked", StringComparison.Ordinal)) && h.Contains("Reset checklist (2 ticked)", StringComparison.Ordinal));

        Assert.Equal(2, await Get<MarkdownChecklists>().ResetAsync(owner, "Home Lab Roundup", "chris"));

        var html = await renderer.WaitForAsync(h => Boxes(h).All(b => !b.Contains(" checked", StringComparison.Ordinal)));
        Assert.DoesNotContain("Reset checklist", html);
        var change = Assert.Single(await Get<ChangeStore>().QueryAsync(new ChangeQuery(DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now.AddHours(1), [ChangeKinds.Checklist])));
        Assert.Equal("Checklist in “Home Lab Roundup” reset", change.Title);
    }

    [Fact]
    public async Task ItemTextInABlockIsText()
    {
        var block = await BlockAsync("- [ ] <script>alert(1)</script> and <img src=x onerror=alert(2)>\n");
        var renderer = Renderer();
        await RenderCardAsync(renderer, block);
        var html = await renderer.WaitForAsync(h => Ready(h, 1));

        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("aria-label=\"Tick “&lt;script&gt;alert(1)&lt;/script&gt;", html);
    }

    /// <summary>
    /// A block's History lists what it said before, with who and how big, and draws the
    /// text as it is now with display-only boxes: ticking belongs to the block, not to a
    /// view of its history.
    /// </summary>
    [Fact]
    public async Task ABlocksHistoryListsItsVersionsAndItsBoxesAreDisplayOnly()
    {
        var block = await BlockAsync("- [ ] One\n");
        await Get<ConfigStore>().SaveWidgetAsync(block with { Settings = new SettingsBag { [WidgetHistoryStore.ContentKey] = "- [ ] One\n- [ ] Two\n" } }, "alice");
        var now = (await Get<ConfigStore>().WidgetsAsync()).Single(w => w.Id == block.Id);

        var renderer = Renderer();
        await renderer.RenderAsync<LabbyTwo.Components.Shared.CardHistory>(new Dictionary<string, object?>
        {
            [nameof(LabbyTwo.Components.Shared.CardHistory.Widget)] = now,
            [nameof(LabbyTwo.Components.Shared.CardHistory.Name)] = "Home Lab Roundup",
            [nameof(LabbyTwo.Components.Shared.CardHistory.Noun)] = "block",
        });
        var html = Text(await renderer.WaitForAsync("10 characters · replaced"));

        Assert.Contains("History of “Home Lab Roundup”", html);
        Assert.Contains("As the block", html);
        Assert.Contains("by alice", html);
        Assert.Equal(2, Boxes(html).Count);
        Assert.All(Boxes(html), b => Assert.Contains(" disabled", b));
        Assert.DoesNotContain("md-check", html);
    }

    /// <summary>
    /// The owner's real page: a custom page whose Markdown block uses every shortcode they
    /// use, with a checklist in it. Drawn as the page draws it, it must show every value and
    /// never a "?", a placeholder or a shortcode's raw text — and its boxes must be live.
    /// </summary>
    [Fact]
    public async Task TheOwnersRoundupPageStillDrawsEverything()
    {
        var nas = await ConnectionAsync("NAS", "stub", ("url", "http://nas.lan"));
        using var docker = new ScriptedDocker(async context =>
        {
            if (context.Request.Url!.AbsolutePath.EndsWith("/containers/json", StringComparison.Ordinal))
            {
                await ScriptedDocker.Json(context, """
                    [{"Id":"aaa111","Names":["/sonarr"],"Image":"sonarr:latest","State":"running","Status":"Up 3 hours (healthy)","Labels":{}},
                     {"Id":"bbb222","Names":["/radarr"],"Image":"radarr:latest","State":"running","Status":"Up 5 minutes (unhealthy)","Labels":{}},
                     {"Id":"ccc333","Names":["/old-backup"],"Image":"alpine:3","State":"exited","Status":"Exited (137) 2 days ago","Labels":{}}]
                    """);
            }
            else
            {
                await ScriptedDocker.NoSuchContainer(context);
            }
        });
        await ConnectionAsync("Docker", "docker", ("endpoint", docker.Endpoint));
        await Get<HealthMonitor>().RefreshAsync(nas);

        const string page = """
            # Home Lab Roundup

            {{today}} · the NAS was checked {{ago: NAS}} · Christmas is {{countdown: 2026-12-25}}

            {{down}}

            {{if all up}}
            > [!TIP]
            > Everything's up.
            {{end}}

            {{alerts}}

            ## Containers

            {{containers: stopped connection="Docker"}}

            {{containers: unhealthy connection="Docker"}}

            {{containers: running connection="Docker"}}

            | Service | Status | Since | Uptime |
            |---|---|---|---|
            | NAS | {{status: NAS}} | {{since: NAS}} | {{uptime: NAS}} |

            {{uptimebar: NAS}}

            {{if down: NAS}}
            > [!CAUTION]
            > The NAS is down. Do not restart Plex.
            {{else}}
            The NAS disk is at {{metric: NAS / disk_percent}} {{sparkline: NAS / disk_percent 24h}}, full {{forecast: NAS}}.

            > [!TIP]
            > It answered {{ago: NAS}}.
            {{end}}

            {{card: gauge connection="NAS" metric=disk_percent title="NAS disk"}}

            {{if metric: NAS / disk_percent > 85}}
            > [!WARNING]
            > Over 85% full — clear the recycle bin.
            {{end}}

            {{details: Restart procedure}}
            1. Stop it from here: {{button: NAS / poke}}
            2. Open its page: {{link: NAS}}
            3. Run `docker compose up -d` and then `docker ps`
            {{end}}

            > [!NOTE]
            > Domains and certificates due soon:

            {{renewals: days=60}}

            ## Before a restart

            > [!WARNING]
            > Plex's library lives on the NAS.

            - [ ] Stopped Plex
            - [x] Took a snapshot
            - [ ] Told the family

            > [!CAUTION]
            > Never pull the plug during a scrub.
            """;

        var block = await BlockAsync(page);
        var renderer = Renderer();
        await RenderCardAsync(renderer, block);
        var html = Text(await renderer.WaitForAsync(h =>
        {
            var text = Text(h);
            return text.Contains("sonarr", StringComparison.Ordinal)
                   && text.Contains("old-backup", StringComparison.Ordinal)
                   && text.Contains("50.0%", StringComparison.Ordinal)
                   && text.Contains("Everything", StringComparison.Ordinal)
                   && text.Contains("Restart procedure", StringComparison.Ordinal)
                   && text.Contains("Nothing due in the next 60 days", StringComparison.Ordinal)
                   && text.Contains("gauge-value", StringComparison.Ordinal)
                   && Ready(h, 2);
        }, TimeSpan.FromSeconds(20)));

        Assert.Empty(_boundary.Errors);
        Assert.DoesNotContain("sc-problem", html);
        Assert.DoesNotContain("is-problem", html);
        Assert.DoesNotContain("{{", html);
        Assert.DoesNotContain("}}", html);
        Assert.DoesNotMatch(@"lt[a-z]{10}q[0-9]+q", html);
        Assert.Contains("md-callout md-callout-note", html);
        Assert.Contains("md-callout md-callout-tip", html);
        Assert.Contains("md-callout md-callout-warning", html);
        Assert.Contains("md-callout md-callout-caution", html);
        Assert.Contains("The NAS disk is at", html);
        Assert.Contains("<table>", html);
        Assert.Contains("<ol>", html);
        Assert.Contains("<code>docker compose up -d</code>", html);
        Assert.Contains("<code>docker ps</code>", html);
        Assert.Contains("NAS disk", html);
        Assert.Contains("Poke", html);
        Assert.Contains("http://nas.lan", html);
        Assert.Equal(3, Boxes(html).Count);
        Assert.Contains("Stopped Plex", html);
        // The down branch is not drawn while the NAS is up, and nor is the over-85% warning.
        Assert.DoesNotContain("Do not restart Plex", html);
        Assert.DoesNotContain("Over 85% full", html);

        // And the boxes work on it.
        await renderer.ChangeAsync(0, true);
        await renderer.WaitForAsync("Reset checklist (1 ticked)");
        Assert.Single(await Get<ChecklistStore>().TicksAsync(ChecklistOwners.Widget(block.Id)));
    }
}
