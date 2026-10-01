#pragma warning disable BL0006 // Reading the render tree back into HTML is the whole point of InteractiveRenderer.
using System.Net;
using LabbyTwo.Components.Pages.Kinds;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace LabbyTwo.Tests;

/// <summary>
/// The notes page with links in it, drawn by the real page against a real database:
/// "Linked from" under a note, a missing note's link opening the editor with its title,
/// and renaming a linked-to note offering to change the links — and doing it.
/// </summary>
public sealed class NotesTabLinkTests : IAsyncDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private ServiceProvider? _services;
    private InteractiveRenderer? _renderer;
    private Tab _tab = default!;

    /// <summary>An address the page can read its question from, and that records where it was sent.</summary>
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation(string uri) => Initialize("http://labby.test/", uri);

        protected override void NavigateToCore(string uri, NavigationOptions options) => Uri = ToAbsoluteUri(uri).ToString();
    }

    private sealed class NoScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new JSException("No browser in a test.");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new JSException("No browser in a test.");
    }

    private async Task StartAsync(string address = "http://labby.test/t/runbooks")
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
        services.AddSingleton<LatestReadings>();
        services.AddSingleton<Offload>();
        services.AddSingleton<Markdown>();
        services.AddSingleton<ActionRunner>();
        services.AddSingleton<NotesStore>();
        services.AddSingleton<NoteDirectory>();
        services.AddSingleton<NoteTemplateStore>();
        services.AddSingleton<ChangeStore>();
        services.AddSingleton<ChecklistStore>();
        services.AddSingleton<MarkdownChecklists>();
        services.AddSingleton<IJSRuntime, NoScript>();
        services.AddSingleton<NavigationManager>(new TestNavigation(address));
        _services = services.BuildServiceProvider();
        await Get<Db>().EnsureSchemaAsync();
        _tab = new Tab { Name = "Runbooks", Slug = "runbooks", Kind = TabKinds.Notes };
        await Get<ConfigStore>().SaveTabAsync(_tab);
        _renderer = new InteractiveRenderer(_services);
    }

    public async ValueTask DisposeAsync()
    {
        if (_renderer is not null)
            await _renderer.DisposeAsync();
        if (_services is not null)
            TestHost.Teardown(_services, _directory);
    }

    private T Get<T>() where T : notnull => _services!.GetRequiredService<T>();

    private Task RenderAsync(Tab? tab = null) =>
        _renderer!.RenderAsync<NotesTab>(new Dictionary<string, object?> { [nameof(NotesTab.Tab)] = tab ?? _tab });

    [Fact]
    public async Task ANoteSaysWhichNotesLinkToIt()
    {
        await StartAsync();
        var plex = await Get<NotesStore>().SaveAsync(null, _tab.Id, "Plex", "## Restart");
        var index = await Get<NotesStore>().SaveAsync(null, _tab.Id, "Index", "See [[Plex#Restart]].");

        await RenderAsync();
        var html = WebUtility.HtmlDecode(await _renderer!.WaitForAsync("Linked from:"));

        Assert.Contains($"<a class=\"note-link\" href=\"t/runbooks#note-{index}\" title=\"Runbooks\">Index</a>", html);
        Assert.Contains($"href=\"t/runbooks?heading=Restart#note-{plex}\"", html);
    }

    [Fact]
    public async Task AMissingNotesLinkOpensTheEditorWithItsTitle()
    {
        await StartAsync("http://labby.test/t/runbooks?new=Sonarr%20runbook");
        await RenderAsync();
        var html = WebUtility.HtmlDecode(await _renderer!.WaitForAsync("New note"));

        Assert.Contains("value=\"Sonarr runbook\"", html);
        // Taken off the address, so a reload does not open it again.
        Assert.Equal("http://labby.test/t/runbooks", Get<NavigationManager>().Uri);
    }

    [Fact]
    public async Task RenamingALinkedNoteOffersToChangeTheLinksAndDoesIt()
    {
        await StartAsync();
        var notes = Get<NotesStore>();
        var plex = await notes.SaveAsync(null, _tab.Id, "Plex runbook", "x");
        var index = await notes.SaveAsync(null, _tab.Id, "Index", "[[Plex runbook#Restart|steps]] and [[Other]]");
        // The newest note is drawn first; make sure the one being renamed is.
        await notes.SaveAsync(plex, _tab.Id, "Plex runbook", "x");

        await RenderAsync();
        await _renderer!.WaitForAsync("Linked from:");
        await _renderer.ClickAsync("Edit");
        await _renderer.ChangeAsync("note-title", "Plex", "oninput");
        await _renderer.ClickAsync("Save", last: true);

        var html = WebUtility.HtmlDecode(await _renderer.WaitForAsync("Change them to “Plex”"));
        Assert.Contains("One note links to this note as “Plex runbook”: Index.", System.Text.RegularExpressions.Regex.Replace(html, @"\s+", " "));

        await _renderer.ClickAsync("Change them to “Plex”");
        var source = Assert.Single(await notes.ByIdsAsync([index]));
        Assert.Equal("[[Plex#Restart|steps]] and [[Other]]", source.Content);
        html = WebUtility.HtmlDecode(await _renderer.WaitForAsync(h => !h.Contains("Change them to", StringComparison.Ordinal)));
        Assert.DoesNotContain("Change them to", html);
    }
}
