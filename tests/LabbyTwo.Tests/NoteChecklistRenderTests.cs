#pragma warning disable BL0006 // The interactive test renderer is a Renderer, which is the whole point of it.
using System.Net;
using System.Text.RegularExpressions;
using LabbyTwo.Components.Shared;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace LabbyTwo.Tests;

/// <summary>Changing a form field, for the render tests that have boxes to tick.</summary>
internal sealed partial class InteractiveRenderer
{
    /// <summary>
    /// Sends the nth element with a change handler the value a browser would send. Throws
    /// when there are not that many, so a test never passes by changing nothing.
    /// </summary>
    public Task ChangeAsync(int nth, object value) => Dispatcher.InvokeAsync(async () =>
    {
        var found = new List<ulong>();
        FindChanges(_root, found);
        if (found.Count <= nth)
            throw new InvalidOperationException($"Only {found.Count} element(s) can be changed.");
        await DispatchEventAsync(found[nth], new EventFieldInfo(), new ChangeEventArgs { Value = value });
    });

    private void FindChanges(int componentId, List<ulong> found)
    {
        var frames = GetCurrentRenderTreeFrames(componentId);
        FindChanges(frames.Array, 0, frames.Count, found);
    }

    private void FindChanges(RenderTreeFrame[] frames, int start, int end, List<ulong> found)
    {
        for (var i = start; i < end; i++)
        {
            var frame = frames[i];
            switch (frame.FrameType)
            {
                case RenderTreeFrameType.Attribute when frame.AttributeName == "onchange" && frame.AttributeEventHandlerId != 0:
                    found.Add(frame.AttributeEventHandlerId);
                    break;
                case RenderTreeFrameType.Component:
                    FindChanges(frame.ComponentId, found);
                    i += frame.ComponentSubtreeLength - 1;
                    break;
            }
        }
    }
}

/// <summary>
/// Checklists drawn by the real components against a real database: boxes that can be
/// ticked in a note and are display-only everywhere else, ticks that every viewer sees,
/// item text that stays text — and the whole of a real note, every kind of shortcode in
/// it, still drawing without a single question mark now that its boxes are live.
/// </summary>
public sealed partial class NoteChecklistRenderTests : IAsyncDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private readonly List<InteractiveRenderer> _renderers = [];
    private readonly Stub _stub = new();
    private readonly RecordingBoundaryLogger _boundary = new();

    /// <summary>What any card in the note threw, so a card that failed quietly still fails the test.</summary>
    private sealed class RecordingBoundaryLogger : IErrorBoundaryLogger
    {
        public System.Collections.Concurrent.ConcurrentQueue<Exception> Errors { get; } = new();

        public ValueTask LogErrorAsync(Exception exception)
        {
            Errors.Enqueue(exception);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A connection with readings and an action, for the note's live values and its button.</summary>
    private sealed class Stub : IConnectionProvider
    {
        public string Type => "stub";
        public string DisplayName => "Stub";
        public string Icon => "";
        public string Description => "";
        public IReadOnlyList<FieldSpec> Fields => [new("url", "Address")];
        public IReadOnlyList<MetricSpec> Metrics => [new("disk_percent", "Disk used", "%", 1), new("cpu_percent", "CPU", "%", 1)];
        public IReadOnlyList<ProviderAction> Actions => [new("poke", "Poke") { Confirms = false }];

        public double Disk { get; set; } = 50;

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
            Task.FromResult(ProbeResult.Up(TimeSpan.FromMilliseconds(3), "OK",
                new Dictionary<string, double> { ["disk_percent"] = Disk, ["cpu_percent"] = 12 }));

        public Task<ActionResult> RunActionAsync(Connection connection, ProviderAction action, SettingsBag input, CancellationToken ct) =>
            Task.FromResult(ActionResult.Done("Poked"));
    }

    public NoteChecklistRenderTests()
    {
        _services = TestHost.Build(_directory, services =>
        {
            services.AddSingleton<IConnectionProvider>(_stub);
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
            services.AddSingleton<NoteChecklists>();
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

    private static Task RenderNoteAsync(InteractiveRenderer renderer, string markdown, string? noteId, bool checklist = true) =>
        renderer.RenderAsync<LiveMarkdown>(new Dictionary<string, object?>
        {
            [nameof(LiveMarkdown.Content)] = markdown,
            [nameof(LiveMarkdown.OwnerId)] = noteId,
            [nameof(LiveMarkdown.Checklist)] = checklist,
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

    [Fact]
    public async Task ANotesBoxesCanBeTickedAndEveryViewerSeesIt()
    {
        const string note = "## Before the NAS restart\n\n- [ ] Stopped Plex\n- [x] Took a snapshot\n- [ ] Told the family\n";
        var first = Renderer();
        var second = Renderer();
        await RenderNoteAsync(first, note, "note-1");
        await RenderNoteAsync(second, note, "note-1");

        // Ready once the ticks are read: the live boxes are no longer disabled.
        var html = await first.WaitForAsync(h => Boxes(h).Count(b => !b.Contains(" disabled", StringComparison.Ordinal)) == 2);
        var boxes = Boxes(html);
        Assert.Equal(3, boxes.Count);
        Assert.Contains(" checked", boxes[1]);
        Assert.Contains("Ticked in the note", boxes[1]);
        Assert.DoesNotContain(" checked", boxes[0]);
        Assert.Contains("Stopped Plex", html);
        await second.WaitForAsync(h => Boxes(h).Count(b => !b.Contains(" disabled", StringComparison.Ordinal)) == 2);

        await first.ChangeAsync(0, true);

        // The other viewer's box ticks without it asking for anything.
        var seen = Text(await second.WaitForAsync(h => Boxes(h)[0].Contains(" checked", StringComparison.Ordinal)));
        Assert.Contains(" checked", Boxes(seen)[0]);
        Assert.Contains("Ticked by someone, today at", Boxes(seen)[0]);

        // Stored apart from the note: the store has it, under the item's key.
        var ticks = await Get<ChecklistStore>().TicksAsync("note-1");
        var tick = Assert.Single(ticks.Values);
        Assert.Equal("Stopped Plex", tick.Text);

        await second.ChangeAsync(0, false);
        await first.WaitForAsync(h => !Boxes(h)[0].Contains(" checked", StringComparison.Ordinal));
        Assert.Empty(await Get<ChecklistStore>().TicksAsync("note-1"));
    }

    [Fact]
    public async Task OutsideANoteTheBoxesAreTheRenderersOwn()
    {
        const string text = "- [ ] Stopped Plex\n- [x] Done\n";
        var renderer = Renderer();
        await RenderNoteAsync(renderer, text, "card-1", checklist: false);
        var html = await renderer.HtmlAsync();

        Assert.Equal(new Markdown().ToHtml(text), html);
    }

    [Fact]
    public async Task ItemTextIsText()
    {
        var renderer = Renderer();
        await RenderNoteAsync(renderer, "- [ ] <script>alert(1)</script> and <img src=x onerror=alert(2)>\n- [ ] \"><b>quoted\n", "note-x");
        var html = await renderer.WaitForAsync(h => Boxes(h).All(b => !b.Contains(" disabled", StringComparison.Ordinal)));

        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("<b>", html);
        Assert.Contains("&lt;script&gt;", html);
        // The words are in the box's label too, encoded as an attribute.
        Assert.Contains("aria-label=\"Tick “&lt;script&gt;alert(1)&lt;/script&gt;", html);
    }

    [Fact]
    public async Task AResetUnticksEverythingAndSaysSoInWhatChanged()
    {
        var store = Get<ChecklistStore>();
        var items = Get<Markdown>().ChecklistItems("- [ ] One\n- [ ] Two\n");
        await store.SetAsync("note-r", items[0], true, "chris", DateTimeOffset.Now);
        await store.SetAsync("note-r", items[1], true, "chris", DateTimeOffset.Now);

        var renderer = Renderer();
        await RenderNoteAsync(renderer, "- [ ] One\n- [ ] Two\n", "note-r");
        await renderer.WaitForAsync(h => Boxes(h).All(b => b.Contains(" checked", StringComparison.Ordinal)));

        var cleared = await Get<NoteChecklists>().ResetAsync("note-r", "Restart", "chris");

        Assert.Equal(2, cleared);
        await renderer.WaitForAsync(h => Boxes(h).All(b => !b.Contains(" checked", StringComparison.Ordinal)));
        var changes = await Get<ChangeStore>().QueryAsync(new ChangeQuery(DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now.AddHours(1), [ChangeKinds.Checklist]));
        var change = Assert.Single(changes);
        Assert.Equal("Checklist in “Restart” reset", change.Title);
        Assert.Equal("2 ticks cleared by chris", change.Detail);

        // Nothing to clear: nothing recorded.
        Assert.Equal(0, await Get<NoteChecklists>().ResetAsync("note-r", "Restart", "chris"));
        Assert.Single(await Get<ChangeStore>().QueryAsync(new ChangeQuery(DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now.AddHours(1), [ChangeKinds.Checklist])));
    }

    [Fact]
    public async Task TicksFollowTheirItemsThroughAnEdit()
    {
        var markdown = Get<Markdown>();
        var store = Get<ChecklistStore>();
        var before = markdown.ChecklistItems("- [ ] Stop Plex\n- [ ] Snapshot\n- [ ] Gone soon\n");
        foreach (var item in before)
            await store.SetAsync("note-e", item, true, "chris", DateTimeOffset.Now);

        const string after = "New intro line.\n\n- [ ] Stop Plex and Sonarr\n- [ ] Snapshot\n";
        await Get<NoteChecklists>().ReconcileAsync("note-e", after);

        var ticks = await store.TicksAsync("note-e");
        var now = markdown.ChecklistItems(after);
        Assert.Equal(2, ticks.Count);
        Assert.True(ticks.ContainsKey(now[0].Key));
        Assert.True(ticks.ContainsKey(now[1].Key));
        Assert.Equal("Stop Plex and Sonarr", ticks[now[0].Key].Text);
    }

    /// <summary>
    /// The note this whole feature has to leave alone: every shortcode its owner uses, in
    /// one page, with a checklist in it. Drawn as a note — checklists on — it must show
    /// every value and never a "?", a placeholder or a shortcode's raw text.
    /// </summary>
    [Fact]
    public async Task TheOwnersRealNoteStillDrawsEverything()
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

        const string note = """
            # Home lab

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

            > [!CAUTION]
            > Never pull the plug during a scrub.
            """;

        var renderer = Renderer();
        await RenderNoteAsync(renderer, note, "owners-note");
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
                   && Boxes(h).Count(b => !b.Contains(" disabled", StringComparison.Ordinal)) == 1;
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
        Assert.Contains("<code>docker compose up -d</code>", html);
        Assert.Contains("NAS disk", html);
        Assert.Contains("Poke", html);
        Assert.Contains("http://nas.lan", html);
        Assert.Equal(2, Boxes(html).Count);
        // The down branch is not drawn while the NAS is up, and nor is the over-85% warning.
        Assert.DoesNotContain("Do not restart Plex", html);
        Assert.DoesNotContain("Over 85% full", html);
    }
}
