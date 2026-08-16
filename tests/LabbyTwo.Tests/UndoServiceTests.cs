using LabbyTwo.Services;

namespace LabbyTwo.Tests;

/// <summary>
/// The offer itself, with no database in the way. What matters here is that it stops being
/// on offer: an undo bar that outlives its window, or that can be pressed twice, restores
/// something on top of whatever the user did next.
/// </summary>
public class UndoServiceTests
{
    private static readonly DateTimeOffset Now =
        new(new DateTime(2026, 8, 16, 14, 0, 0, DateTimeKind.Local));

    [Fact]
    public void NothingIsOfferedToBeginWith()
        => Assert.Null(new UndoService().Current(Now));

    [Fact]
    public void AnOfferLapses()
    {
        var undo = new UndoService();
        undo.Add("Deleted a card.", _ => Task.CompletedTask);

        Assert.NotNull(undo.Current());
        Assert.Null(undo.Current(DateTimeOffset.Now + UndoService.Window + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task ALapsedOfferWillNotRun()
    {
        var undo = new UndoService();
        var ran = false;

        undo.Add("Deleted a card.", _ => { ran = true; return Task.CompletedTask; });

        // Reaching in the way an idle circuit would: the offer is stale, so pressing the
        // button that is still on screen must do nothing rather than replay it.
        typeof(UndoService)
            .GetField("_offer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(undo, new UndoService.Offer("stale", null, _ => { ran = true; return Task.CompletedTask; },
                DateTimeOffset.Now - TimeSpan.FromSeconds(1)));

        Assert.False(await undo.UndoAsync());
        Assert.False(ran);
    }

    [Fact]
    public async Task RestoringTwiceRestoresOnce()
    {
        var undo = new UndoService();
        var runs = 0;
        undo.Add("Deleted a card.", _ => { runs++; return Task.CompletedTask; });

        Assert.True(await undo.UndoAsync());
        Assert.False(await undo.UndoAsync());
        Assert.Equal(1, runs);
    }

    /// <summary>
    /// One deep. A second delete replaces the first offer rather than queueing behind it,
    /// so Undo always means "the thing that just vanished" and never something older that
    /// the user has stopped thinking about.
    /// </summary>
    [Fact]
    public async Task ASecondDeleteReplacesTheFirst()
    {
        var undo = new UndoService();
        var restored = "";

        undo.Add("Deleted the “one” card.", _ => { restored = "one"; return Task.CompletedTask; });
        undo.Add("Deleted the “two” card.", _ => { restored = "two"; return Task.CompletedTask; });

        Assert.Equal("Deleted the “two” card.", undo.Current()!.Description);
        await undo.UndoAsync();
        Assert.Equal("two", restored);
    }

    [Fact]
    public void DismissTakesTheOfferAway()
    {
        var undo = new UndoService();
        undo.Add("Deleted a card.", _ => Task.CompletedTask);

        undo.Dismiss();

        Assert.Null(undo.Current());
    }

    [Fact]
    public void TheCaveatIsCarried()
    {
        var undo = new UndoService();
        undo.Add("Deleted the “NAS” connection.", _ => Task.CompletedTask,
            caveat: "Its recorded history is not coming back.");

        Assert.Equal("Its recorded history is not coming back.", undo.Current()!.Caveat);
    }
}
