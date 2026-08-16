using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace LabbyTwo.Tests;

/// <summary>
/// What an undo actually puts back, against a real database.
///
/// The offer is easy; the fidelity is not. A delete in here cascades — a tab takes its
/// cards and its notes, a connection cuts loose every card bound to it — and an undo that
/// restores only the row you clicked leaves a dashboard that looks restored and is not.
/// That failure is invisible in a diff and obvious to whoever pressed the button.
/// </summary>
public sealed class DeletionsTests : IDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;

    private readonly ConfigStore _config;
    private readonly NotesStore _notes;
    private readonly UndoService _undo;
    private readonly Deletions _deletions;

    public DeletionsTests()
    {
        _services = TestHost.Build(_directory);
        _services.GetRequiredService<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();

        // The shared host stops at storage. The three below are built by hand rather than
        // added to it, because an undo offer is not something every test wants a copy of.
        _config = _services.GetRequiredService<ConfigStore>();
        _notes = new NotesStore(_services.GetRequiredService<Db>());
        _undo = new UndoService();
        _deletions = new Deletions(_config, _notes, _undo);
    }

    public void Dispose()
    {
        _services.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory should not fail an otherwise passing run.
        }
    }

    [Fact]
    public async Task UndoingACardPutsItBackInItsOwnSlot()
    {
        await _config.SaveTabAsync(new Tab { Id = "tab", Slug = "home", Name = "Home" });

        var widget = new Widget { Id = "w", TabId = "tab", Type = "metric", Title = "Ping", Sort = 3, Width = 6 };
        await _config.SaveWidgetAsync(widget);

        await _deletions.WidgetAsync(widget);
        Assert.Empty(await _config.WidgetsForTabAsync("tab"));

        Assert.True(await _undo.UndoAsync());

        var back = Assert.Single(await _config.WidgetsForTabAsync("tab"));
        Assert.Equal("Ping", back.Title);
        // Sort and width come back too, or the card returns to the end of the grid at the
        // wrong size, which is not the dashboard anybody had.
        Assert.Equal(3, back.Sort);
        Assert.Equal(6, back.Width);
    }

    [Fact]
    public async Task UndoingATabBringsBackItsCardsAndItsNotes()
    {
        await _config.SaveTabAsync(new Tab { Id = "tab", Slug = "media", Name = "Media", Kind = TabKinds.Notes });
        await _config.SaveWidgetAsync(new Widget { Id = "a", TabId = "tab", Type = "metric", Sort = 0 });
        await _config.SaveWidgetAsync(new Widget { Id = "b", TabId = "tab", Type = "metric", Sort = 1 });
        await _notes.SaveAsync("n1", "tab", "First", "one");
        await _notes.SaveAsync("n2", "tab", "Second", "two");

        var tab = (await _config.TabsAsync()).Single(t => t.Id == "tab");
        await _deletions.TabAsync(tab);

        Assert.Empty(await _config.TabsAsync());
        Assert.Empty(await _config.WidgetsForTabAsync("tab"));
        Assert.Empty(await _notes.ForTabAsync("tab"));

        Assert.True(await _undo.UndoAsync());

        Assert.Equal("Media", (await _config.TabsAsync()).Single().Name);
        Assert.Equal(2, (await _config.WidgetsForTabAsync("tab")).Count);
        Assert.Equal(["First", "Second"], (await _notes.ForTabAsync("tab")).Select(n => n.Title));
    }

    /// <summary>
    /// The one that would have shipped broken. Deleting a connection sets connection_id to
    /// NULL on every card that used it rather than deleting the cards, so restoring only
    /// the connection row leaves the dashboard covered in "the connection this widget used
    /// is gone" — an undo that visibly did not undo.
    /// </summary>
    [Fact]
    public async Task UndoingAConnectionRebindsTheCardsThatUsedIt()
    {
        await _config.SaveTabAsync(new Tab { Id = "tab", Slug = "home", Name = "Home" });

        var connection = new Connection { Id = "c", Provider = "http", Name = "Sonarr" };
        await _config.SaveConnectionAsync(connection);
        await _config.SaveWidgetAsync(new Widget { Id = "bound", TabId = "tab", Type = "metric", ConnectionId = "c" });
        await _config.SaveWidgetAsync(new Widget { Id = "loose", TabId = "tab", Type = "metric", ConnectionId = null });

        await _deletions.ConnectionAsync(connection);

        Assert.Empty(await _config.ConnectionsAsync());
        Assert.All(await _config.WidgetsAsync(), widget => Assert.Null(widget.ConnectionId));

        Assert.True(await _undo.UndoAsync());

        Assert.Equal("Sonarr", (await _config.ConnectionsAsync()).Single().Name);

        var widgets = await _config.WidgetsAsync();
        Assert.Equal("c", widgets.Single(w => w.Id == "bound").ConnectionId);
        // And the one that was never bound stays unbound, rather than being adopted.
        Assert.Null(widgets.Single(w => w.Id == "loose").ConnectionId);
    }

    /// <summary>
    /// Deleting twenty things at once is when a way back matters most, so a bulk delete
    /// goes down the same path and lands as one offer rather than as twenty that have
    /// already replaced each other.
    /// </summary>
    [Fact]
    public async Task UndoingABulkDeleteBringsThemAllBackBoundAsTheyWere()
    {
        await _config.SaveTabAsync(new Tab { Id = "tab", Slug = "home", Name = "Home" });

        foreach (var name in (string[])["Sonarr", "Radarr", "Prowlarr"])
        {
            await _config.SaveConnectionAsync(new Connection { Id = name, Provider = "http", Name = name });
            await _config.SaveWidgetAsync(new Widget
            {
                Id = $"w-{name}", TabId = "tab", Type = "metric", ConnectionId = name,
            });
        }

        var all = await _config.ConnectionsAsync();
        await _deletions.ConnectionsAsync([.. all]);

        Assert.Empty(await _config.ConnectionsAsync());

        var offer = _undo.Current();
        Assert.NotNull(offer);
        Assert.Contains("3 connections", offer.Description);

        Assert.True(await _undo.UndoAsync());

        Assert.Equal(3, (await _config.ConnectionsAsync()).Count);

        // Each card back on its own connection, not all on whichever was restored last.
        var widgets = await _config.WidgetsAsync();
        foreach (var name in (string[])["Sonarr", "Radarr", "Prowlarr"])
            Assert.Equal(name, widgets.Single(w => w.Id == $"w-{name}").ConnectionId);
    }

    [Fact]
    public async Task DeletingAConnectionSaysWhatUndoCannotBringBack()
    {
        var connection = new Connection { Id = "c", Provider = "http", Name = "NAS" };
        await _config.SaveConnectionAsync(connection);

        await _deletions.ConnectionAsync(connection);

        var offer = _undo.Current();
        Assert.NotNull(offer);
        Assert.Contains("NAS", offer.Description);
        Assert.False(string.IsNullOrWhiteSpace(offer.Caveat));
    }
}
