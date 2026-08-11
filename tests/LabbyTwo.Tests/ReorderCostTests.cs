using System.Diagnostics;
using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Tests;

/// <summary>
/// Dragging a card used to take about thirty seconds on a real dashboard. Reordering
/// renumbers every card on the tab, and each save was dropping the whole cache — including
/// connections, whose secrets are decrypted again on the next read — then raising a change
/// event that made every subscriber reload. Twelve cards times twenty connections is a lot
/// of cryptography to move one box.
///
/// These are the two properties that keep it fixed, and both are about counting rather
/// than timing, so they hold on a slow CI runner as well as here.
/// </summary>
public class ReorderCostTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "labbytwo-reorder-" + Guid.NewGuid().ToString("n"));

    private readonly ConfigStore _config;

    public ReorderCostTests()
    {
        Directory.CreateDirectory(_directory);
        _config = TestHost.ConfigStore(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private async Task<Tab> SeedAsync(int widgets, int connections)
    {
        var tab = new Tab { Slug = "dash", Name = "Dashboard" };
        await _config.SaveTabAsync(tab);

        for (var i = 0; i < connections; i++)
        {
            await _config.SaveConnectionAsync(new Connection
            {
                Provider = "http",
                Name = $"Service {i}",
                Settings = new SettingsBag { ["url"] = "http://example.com" },
            });
        }

        for (var i = 0; i < widgets; i++)
            await _config.SaveWidgetAsync(new Widget { TabId = tab.Id, Type = "clock", Sort = i });

        return tab;
    }

    [Fact]
    public async Task MovingOneCardAnnouncesOneChange()
    {
        var tab = await SeedAsync(widgets: 12, connections: 20);
        var cards = await _config.WidgetsForTabAsync(tab.Id);

        var changes = 0;
        void Count() => changes++;
        _config.Changed += Count;

        try
        {
            // Last card to the front: the worst case, since every sort value shifts.
            await _config.MoveWidgetAsync(cards[^1].Id, cards[0].Id);
        }
        finally
        {
            _config.Changed -= Count;
        }

        // One save per card meant one event per card, and every subscriber reloaded the
        // whole dashboard each time.
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task MovingACardDoesNotThrowAwayTheDecryptedConnections()
    {
        var tab = await SeedAsync(widgets: 12, connections: 20);
        var cards = await _config.WidgetsForTabAsync(tab.Id);

        // Warm the cache, then time a read that should now come from it.
        _ = await _config.ConnectionsAsync();
        await _config.MoveWidgetAsync(cards[^1].Id, cards[0].Id);

        var stopwatch = Stopwatch.StartNew();
        var after = await _config.ConnectionsAsync();
        stopwatch.Stop();

        Assert.Equal(20, after.Count);

        // A cache hit is microseconds; rebuilding it means twenty rows read and decrypted.
        // The threshold is deliberately loose — this is asserting "did not rebuild", not a
        // performance budget.
        Assert.True(stopwatch.ElapsedMilliseconds < 25,
            $"Reading connections after a widget move took {stopwatch.ElapsedMilliseconds} ms, " +
            "which means moving a card threw the decrypted connections away again.");
    }

    [Fact]
    public async Task TheOrderIsActuallyRightAfterwards()
    {
        var tab = await SeedAsync(widgets: 5, connections: 0);
        var before = await _config.WidgetsForTabAsync(tab.Id);

        await _config.MoveWidgetAsync(before[4].Id, before[1].Id);

        var after = await _config.WidgetsForTabAsync(tab.Id);
        Assert.Equal(
            [before[0].Id, before[4].Id, before[1].Id, before[2].Id, before[3].Id],
            after.Select(w => w.Id));
    }
}
