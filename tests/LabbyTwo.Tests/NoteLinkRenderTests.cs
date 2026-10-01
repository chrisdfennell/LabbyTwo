#pragma warning disable BL0006 // Reading the render tree back into HTML is the whole point of InteractiveRenderer.
using System.Net;
using LabbyTwo.Components.Shared;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LabbyTwo.Tests;

/// <summary>
/// Links between notes, end to end against a real database: drawn by the real components
/// (a link, a missing one offering to write it, examples in code left alone, the words never
/// HTML), the link index written by every save and read back as "Linked from", a note
/// renamed after it was linked to, notes from before there was an index, and the user's
/// templates. And the regression that matters most: a note that uses every shortcode there
/// is reads exactly as it did.
/// </summary>
public sealed class NoteLinkRenderTests : IAsyncDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private readonly InteractiveRenderer _renderer;

    public NoteLinkRenderTests()
    {
        Directory.CreateDirectory(_directory);
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        services.AddTestStorage(_directory);
        services.AddSingleton<IEnumerable<IConnectionProvider>>([]);
        services.AddSingleton<IEnumerable<IWidgetType>>([]);
        services.AddSingleton<IEnumerable<ITabKind>>([]);
        services.AddSingleton<Registry>();
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<AlertRuleStore>();
        services.AddSingleton<HistoryStore>();
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
        services.AddSingleton<NoteDirectory>();
        services.AddSingleton<NoteTemplateStore>();
        _services = services.BuildServiceProvider();
        Get<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();
        _renderer = new InteractiveRenderer(_services);
    }

    public async ValueTask DisposeAsync()
    {
        await _renderer.DisposeAsync();
        TestHost.Teardown(_services, _directory);
    }

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private NotesStore Notes => Get<NotesStore>();

    private async Task<Tab> NotesTabAsync(string name, string slug)
    {
        var tab = new Tab { Name = name, Slug = slug, Kind = TabKinds.Notes };
        await Get<ConfigStore>().SaveTabAsync(tab);
        return tab;
    }

    private Task RenderAsync(string markdown, string? owner = null) =>
        _renderer.RenderAsync<LiveMarkdown>(new Dictionary<string, object?>
        {
            [nameof(LiveMarkdown.Content)] = markdown,
            [nameof(LiveMarkdown.OwnerId)] = owner,
        });

    private async Task<IReadOnlyList<NoteLinkRow>> RowsAsync() => (await Notes.DirectoryAsync()).Links;

    private async Task SqlAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var db = await Get<Db>().OpenAsync();
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string Text(string html) => WebUtility.HtmlDecode(html);

    // ---------- drawing ----------

    [Fact]
    public async Task ALinkGoesToTheNoteAndItsHeading()
    {
        var tab = await NotesTabAsync("Runbooks", "runbooks");
        var plex = await Notes.SaveAsync(null, tab.Id, "Plex runbook", "## Restart\nPress it.");
        var index = await Notes.SaveAsync(null, tab.Id, "Index", "x");

        await RenderAsync("See [[plex runbook]], or [[Plex runbook#Restart|the restart steps]].", index);
        var html = Text(await _renderer.WaitForAsync("the restart steps</a>"));

        // The words are the link as written; the note's own title is the tooltip.
        Assert.Contains($"<a class=\"note-link\" href=\"t/runbooks#note-{plex}\" title=\"Plex runbook — Runbooks\">plex runbook</a>", html);
        Assert.Contains($"<a class=\"note-link\" href=\"t/runbooks?heading=Restart#note-{plex}\" title=\"Plex runbook — Runbooks\">the restart steps</a>", html);
        Assert.DoesNotContain("[[", html);
    }

    [Fact]
    public async Task AMissingNoteOffersToWriteItAndBecomesALinkOnceWritten()
    {
        var tab = await NotesTabAsync("Runbooks", "runbooks");
        var index = await Notes.SaveAsync(null, tab.Id, "Index", "x");

        await RenderAsync("Next: [[Sonarr runbook]].", index);
        var html = Text(await _renderer.WaitForAsync("is-missing"));
        Assert.Contains($"<a class=\"note-link is-missing\" href=\"t/runbooks?new=Sonarr%20runbook&in={tab.Id}\"", html);
        Assert.Contains("There is no note called “Sonarr runbook” yet. Click to write it.", html);

        // Writing it is enough: the directory is told, and the link redraws by itself.
        var sonarr = await Notes.SaveAsync(null, tab.Id, "Sonarr runbook", "");
        html = Text(await _renderer.WaitForAsync($"#note-{sonarr}"));
        Assert.DoesNotContain("is-missing", html);
    }

    [Fact]
    public async Task WithNoNotesPageAMissingLinkSaysSoAndLinksNowhere()
    {
        await RenderAsync("[[Anything]]");
        var html = Text(await _renderer.WaitForAsync("is-missing"));
        Assert.Contains("<span class=\"note-link is-missing\"", html);
        Assert.Contains("no notes page to write it on", html);
    }

    [Fact]
    public async Task LinksInCodeAndEscapedOnesStayText()
    {
        await NotesTabAsync("Runbooks", "runbooks");
        await RenderAsync("""
            Inline `[[Plex runbook]]` and \[[Escaped]] and {{today}}.

            ```
            if [[ -f /x ]]; then [[Plex runbook]]; fi
            ```
            """);
        var html = Text(await _renderer.WaitForAsync(html => !html.Contains("is-waiting", StringComparison.Ordinal)));

        Assert.Contains("<code>[[Plex runbook]]</code>", html);
        Assert.Contains("[[Escaped]]", html);
        Assert.Contains("if [[ -f /x ]]; then [[Plex runbook]]; fi", html);
        Assert.DoesNotContain("note-link", html);
    }

    [Fact]
    public async Task WhatALinkSaysIsNeverHtml()
    {
        await NotesTabAsync("Runbooks", "runbooks");
        await RenderAsync("[[<script>alert(1)</script>|<b>bold</b>]] and [[x\" onmouseover=\"alert(1)]]");
        var html = await _renderer.WaitForAsync("is-missing");

        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<b>", html);
        Assert.Contains("&lt;b&gt;bold&lt;/b&gt;", html);
        Assert.DoesNotContain("\" onmouseover=\"", html);
    }

    [Fact]
    public async Task ALinkInATableCellWithAPipeKeepsTheRow()
    {
        var tab = await NotesTabAsync("Runbooks", "runbooks");
        var plex = await Notes.SaveAsync(null, tab.Id, "Plex", "");
        await RenderAsync("| What | Where |\n|---|---|\n| Plex | [[Plex|its runbook]] |\n");
        var html = Text(await _renderer.WaitForAsync("its runbook</a>"));
        Assert.Contains($"<td><a class=\"note-link\" href=\"t/runbooks#note-{plex}\"", html);
    }

    // ---------- the user's own note, unchanged ----------

    /// <summary>A note using every shortcode and structure there is, as people's notes do.</summary>
    private const string EverythingNote = """
        # When something breaks

        {{today}} · the NAS was checked {{ago: NAS}} · renewal {{countdown: 2026-12-01}}

        {{down}}

        {{if all up}}
        > [!TIP]
        > Everything is up.
        {{end}}

        {{alerts}}

        {{containers: stopped connection="Docker"}}
        {{containers: unhealthy connection="Docker"}}
        {{containers: running connection="Docker"}}

        The NAS is {{status: NAS}} since {{since: NAS}}, {{uptime: NAS}} up. {{uptimebar: NAS}}

        {{if down: NAS}}
        ## The NAS is down
        {{button: NAS / restart}} — its page: {{link: NAS}}
        {{else}}
        CPU {{metric: NAS / cpu_percent}} {{sparkline: NAS / cpu_percent 24h}}, full {{forecast: NAS}}.

        {{card: gauge connection="NAS" metric="disk_percent" title="Disk"}}

        {{if metric: NAS / disk_percent > 85}}
        > [!WARNING]
        > Over 85% full.
        {{end}}
        {{end}}

        {{details: Full restart procedure}}
        1. Stop the containers.
        2. {{button: NAS / restart}}
        {{end}}

        | Service | Status |
        |---|---|
        | NAS | {{status: NAS}} |

        {{renewals: days=60}}

        Code stays code: `{{status: NAS}}` and `[[not a link]]`.
        """;

    [Fact]
    public void ANoteWithEveryShortcodeIsPreparedExactlyAsBefore()
    {
        var markdown = Get<Markdown>();
        var page = markdown.PreparePage(EverythingNote);

        var prepared = Codes(page.Parts).Select(c => c.Source).ToList();
        // Every shortcode outside code, in order — and nothing else: no link was found in a
        // note that has none, and the one in a code span is still an example.
        var expected = new[]
        {
            "{{today}}", "{{ago: NAS}}", "{{countdown: 2026-12-01}}", "{{down}}", "{{alerts}}",
            "{{containers: stopped connection=\"Docker\"}}", "{{containers: unhealthy connection=\"Docker\"}}",
            "{{containers: running connection=\"Docker\"}}",
            "{{status: NAS}}", "{{since: NAS}}", "{{uptime: NAS}}", "{{uptimebar: NAS}}",
            "{{button: NAS / restart}}", "{{link: NAS}}",
            "{{metric: NAS / cpu_percent}}", "{{sparkline: NAS / cpu_percent 24h}}", "{{forecast: NAS}}",
            "{{card: gauge connection=\"NAS\" metric=\"disk_percent\" title=\"Disk\"}}",
            "{{button: NAS / restart}}", "{{status: NAS}}", "{{renewals: days=60}}",
        };
        Assert.Equal(expected.Order(), prepared.Order());
        Assert.DoesNotContain(Codes(page.Parts), c => c.Kind == NoteLinks.Kind);
        Assert.Equal(3, page.Sections().Count());
        Assert.All(page.Sections(), s => Assert.Null(s.Section.Problem));

        // And the Markdown around them renders as it always did.
        var html = string.Concat(Flatten(page.Parts).OfType<LiveSection>().Select(s => s.Document.Html));
        Assert.Contains("<code>[[not a link]]</code>", html);
        Assert.Contains("<table>", html);
        Assert.Contains("md-callout", html);
    }

    [Fact]
    public async Task ANoteWithEveryShortcodeStillDraws()
    {
        var nas = new Connection { Provider = "http", Name = "NAS" };
        await Get<ConfigStore>().SaveConnectionAsync(nas);

        await RenderAsync(EverythingNote, "the-note");
        var html = Text(await _renderer.WaitForAsync("Full restart procedure"));

        Assert.Contains("<h1 id=\"when-something-breaks\">When something breaks</h1>", html);
        Assert.Contains("<details class=\"md-details\">", html);
        Assert.Contains("<code>{{status: NAS}}</code>", html);
        Assert.Contains("<code>[[not a link]]</code>", html);
        Assert.DoesNotContain("note-link", html);
    }

    private static IEnumerable<Shortcode> Codes(IEnumerable<LivePart> parts) =>
        Flatten(parts).OfType<LiveSection>().SelectMany(s => s.Document.Shortcodes);

    private static IEnumerable<LivePart> Flatten(IEnumerable<LivePart> parts)
    {
        foreach (var part in parts)
        {
            yield return part;
            var inner = part switch
            {
                LiveIf section => section.Then.Concat(section.Else),
                LiveDetails fold => fold.Body,
                _ => [],
            };
            foreach (var each in Flatten(inner))
                yield return each;
        }
    }

    // ---------- the index ----------

    [Fact]
    public async Task EverySaveKeepsTheIndexAndBacklinksFollow()
    {
        var tab = await NotesTabAsync("Runbooks", "runbooks");
        var plex = await Notes.SaveAsync(null, tab.Id, "Plex", "");
        var index = await Notes.SaveAsync(null, tab.Id, "Index", "[[Plex]] and [[plex|again]] and [[Nowhere]] and `[[Plex#Restart]]`");

        Assert.Equal(["nowhere", "plex", "plex#restart"], (await RowsAsync()).Select(r => r.Target).Order());

        var graph = await Get<NoteDirectory>().GetAsync();
        Assert.Equal([index], graph.Backlinks(plex).Select(n => n.Id));
        Assert.Empty(graph.Backlinks(index));

        // The directory wrote down which note each link found.
        Assert.All((await RowsAsync()).Where(r => r.Target.StartsWith("plex", StringComparison.Ordinal)), r => Assert.Equal(plex, r.ToId));

        // An edit drops the links that went and keeps what the others remembered.
        await Notes.SaveAsync(index, tab.Id, "Index", "Only [[Plex]] now.");
        var rows = await RowsAsync();
        Assert.Equal(plex, Assert.Single(rows).ToId);

        await Notes.DeleteAsync(index);
        Assert.Empty(await RowsAsync());
        Assert.Empty((await Get<NoteDirectory>().GetAsync()).Backlinks(plex));
    }

    [Fact]
    public async Task ARenamedNoteKeepsItsLinksAndSaysWhichNotesNamedIt()
    {
        var tab = await NotesTabAsync("Runbooks", "runbooks");
        var plex = await Notes.SaveAsync(null, tab.Id, "Plex runbook", "## Restart");
        var index = await Notes.SaveAsync(null, tab.Id, "Index", "[[Plex runbook#Restart|steps]]");
        var before = await Get<NoteDirectory>().GetAsync();
        Assert.Equal([index], before.LinkingByTitle(plex).Select(n => n.Id));

        await Notes.SaveAsync(plex, tab.Id, "Plex", "## Restart");
        var after = await Get<NoteDirectory>().GetAsync();
        var target = after.Resolve("Plex runbook#Restart", index);
        Assert.Equal(plex, target.Note?.Id);
        Assert.True(target.ByRememberedId);
        Assert.Equal([index], after.Backlinks(plex).Select(n => n.Id));

        // What the rename offer does: rewrite the links with the new title.
        var source = Assert.Single(await Notes.ByIdsAsync([index]));
        var rewritten = NoteLinks.Retarget(source.Content,
            link => before.Resolve(link.Target, index) is { Note.Id: var id } found && id == plex ? found : null, "Plex");
        Assert.Equal("[[Plex#Restart|steps]]", rewritten);
    }

    [Fact]
    public async Task NotesWrittenBeforeTheIndexAreIndexedOnFirstRead()
    {
        var tab = await NotesTabAsync("Runbooks", "runbooks");
        var plex = await Notes.SaveAsync(null, tab.Id, "Plex", "");
        // As an older version wrote them: straight into the table, never indexed.
        await SqlAsync("INSERT INTO notes (id, tab_id, title, content, sort, updated_at) VALUES ('old', $tab, 'Old', 'See [[Plex]].', 0, 0)",
            ("$tab", tab.Id));

        var graph = await Get<NoteDirectory>().GetAsync();
        Assert.Equal(["old"], graph.Backlinks(plex).Select(n => n.Id));
        Assert.Equal(0, await Notes.IndexMissingAsync());
    }

    [Fact]
    public async Task ADeletedTabTakesItsNotesOutOfTheDirectoryAndTheIndex()
    {
        var keep = await NotesTabAsync("Runbooks", "runbooks");
        var gone = await NotesTabAsync("Scratch", "scratch");
        var plex = await Notes.SaveAsync(null, keep.Id, "Plex", "");
        await Notes.SaveAsync(null, gone.Id, "Scratch note", "[[Plex]]");
        Assert.Single((await Get<NoteDirectory>().GetAsync()).Backlinks(plex));

        await Get<ConfigStore>().DeleteTabAsync(gone.Id);
        var graph = await Get<NoteDirectory>().GetAsync();
        Assert.Empty(graph.Backlinks(plex));
        Assert.DoesNotContain(graph.Places, p => p.ContainerId == gone.Id);
        Assert.Empty(await RowsAsync());
    }

    [Fact]
    public async Task ANotesSectionOnACustomPageIsAPlaceToo()
    {
        var page = new Tab { Name = "Home", Slug = "home", Kind = TabKinds.Custom };
        await Get<ConfigStore>().SaveTabAsync(page);
        var section = new Widget
        {
            TabId = page.Id,
            Type = PageBlocks.Section,
            Title = "Lab notes",
            Settings = new SettingsBag { [PageBlocks.KindKey] = TabKinds.Notes },
        };
        await Get<ConfigStore>().SaveWidgetAsync(section);
        var note = await Notes.SaveAsync(null, section.Id, "Wi-Fi", "");

        var graph = await Get<NoteDirectory>().GetAsync();
        var target = graph.Resolve("Lab notes / Wi-Fi", null);
        Assert.Equal(note, target.Note?.Id);
        Assert.Equal($"t/home#note-{note}", target.Note!.Href);
    }

    // ---------- templates ----------

    [Fact]
    public async Task UserTemplatesAreSavedEditedAndDeleted()
    {
        var store = Get<NoteTemplateStore>();
        var id = await store.SaveAsync(null, "", "Weekly check", "- [ ] {{status: NAS}}");
        var saved = Assert.Single(await store.AllAsync());
        Assert.Equal("Weekly check", saved.Name);
        Assert.Equal("- [ ] {{status: NAS}}", saved.Content);

        await store.SaveAsync(id, "Checks", "Weekly check", "changed");
        saved = Assert.Single(await store.AllAsync());
        Assert.Equal(("Checks", "changed"), (saved.Name, saved.Content));

        await store.DeleteAsync(id);
        Assert.Empty(await store.AllAsync());
    }
}
