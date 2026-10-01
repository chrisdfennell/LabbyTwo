using LabbyTwo.Core;
using LabbyTwo.Services;

namespace LabbyTwo.Tests;

/// <summary>
/// How a note's task-list items are found and keyed, and how stored ticks follow them
/// through an edit. Pure — the storage and the drawing have tests of their own.
/// </summary>
public sealed class ChecklistTests
{
    private readonly Markdown _markdown = new();

    [Fact]
    public void ItemsAreFoundInOrderWithTheirWords()
    {
        var items = _markdown.ChecklistItems("# Restart\n\n- [ ] Stopped Plex\n- [x] Backed up\n  - [ ] Nested  step \n\n1. [ ] Numbered\n");

        Assert.Equal(["Stopped Plex", "Backed up", "Nested step", "Numbered"], items.Select(i => i.Text));
        Assert.Equal([0, 1, 2, 3], items.Select(i => i.Position));
        Assert.Equal([false, true, false, false], items.Select(i => i.TickedInSource));
    }

    [Fact]
    public void TheSameWordsTwiceAreTwoItems()
    {
        var items = _markdown.ChecklistItems("- [ ] Check logs\n- [ ] Restart\n- [ ] Check logs\n");

        Assert.Equal(3, items.Select(i => i.Key).Distinct().Count());
        Assert.Equal(Checklists.Key("Check logs", 0), items[0].Key);
        Assert.Equal(Checklists.Key("Check logs", 1), items[2].Key);
    }

    [Fact]
    public void AnEditToAnotherLineKeepsEveryKey()
    {
        var before = _markdown.ChecklistItems("- [ ] One\n- [ ] Two\n");
        var after = _markdown.ChecklistItems("Intro added.\n\n- [ ] Zero\n- [ ] One\n\nMore text.\n\n- [ ] Two\n");

        Assert.Equal(before[0].Key, after[1].Key);
        Assert.Equal(before[1].Key, after[2].Key);
    }

    [Fact]
    public void ItemsAreCountedAcrossSectionsAndFolds()
    {
        var items = _markdown.ChecklistItems("- [ ] Step\n{{if down: NAS}}\n- [ ] Step\n{{else}}\n- [ ] Step\n{{end}}\n{{details: More}}\n- [ ] Step\n{{end}}\n");

        Assert.Equal(4, items.Count);
        Assert.Equal([0, 1, 2, 3], items.Select(i => i.Position));
        Assert.Equal(4, items.Select(i => i.Key).Distinct().Count());
    }

    [Fact]
    public void BoxesBecomePlaceholdersOnlyWhenAsked()
    {
        const string text = "- [ ] Stopped Plex\n- [x] Done\n";

        var plain = _markdown.PreparePage(text);
        var section = Assert.IsType<LiveSection>(Assert.Single(plain.Parts));
        Assert.Empty(section.Document.Shortcodes);
        Assert.Contains("type=\"checkbox\"", section.Document.Html);

        var live = Assert.IsType<LiveSection>(Assert.Single(_markdown.PreparePage(text, checklists: true).Parts));
        Assert.DoesNotContain("<input", live.Document.Html);
        Assert.Equal(2, live.Document.Shortcodes.Count);
        Assert.All(live.Document.Shortcodes, c => Assert.Equal(Checklists.Kind, c.Kind));
        Assert.Equal(2, Markdown.Placeholders.Matches(live.Document.Html).Count);
    }

    [Fact]
    public void BoxesInCodeAreNotItems()
    {
        Assert.Empty(_markdown.ChecklistItems("```\n- [ ] not a task\n```\n\nand `- [ ] this` neither.\n"));
    }

    [Fact]
    public void AWrittenTaskShortcodeIsNotABox()
    {
        // {{task: …}} parses as an unknown kind, never as the stand-in.
        var code = Shortcodes.Parse("{{task: key=x}}");
        Assert.NotNull(code);
        Assert.NotEqual(Checklists.Kind, code.Kind);
        Assert.Null(Checklists.FromShortcode(code));
    }

    [Fact]
    public void ReconcileKeepsTicksFollowsARewordAndDropsTheGone()
    {
        var now = DateTimeOffset.UtcNow;
        var before = _markdown.ChecklistItems("- [ ] Stop Plex\n- [ ] Snapshot\n- [ ] Restart\n");
        var ticks = before.Select(i => new ChecklistTick(i.Key, i.Text, i.Position, "chris", now)).ToList();

        // Reworded the first, kept the second, removed the third.
        var after = _markdown.ChecklistItems("- [ ] Stop Plex and Sonarr\n- [ ] Snapshot\n");
        var kept = Checklists.Reconcile(after, ticks);

        Assert.Equal(2, kept.Count);
        Assert.Contains(kept, t => t.Key == after[0].Key && t.Text == "Stop Plex and Sonarr" && t.By == "chris");
        Assert.Contains(kept, t => t.Key == after[1].Key);
    }

    [Fact]
    public void ReconcileNeverMovesATickOntoAnItemThatHasItsOwn()
    {
        var now = DateTimeOffset.UtcNow;
        var before = _markdown.ChecklistItems("- [ ] A\n- [ ] B\n");
        var ticks = new List<ChecklistTick> { new(before[0].Key, "A", 0, "x", now), new(before[1].Key, "B", 1, "y", now) };

        // A removed: B moves to position 0, and A's orphaned tick must not land on it twice.
        var after = _markdown.ChecklistItems("- [ ] B\n");
        var kept = Checklists.Reconcile(after, ticks);

        var only = Assert.Single(kept);
        Assert.Equal("y", only.By);
    }

    [Fact]
    public void NormaliseFoldsWhitespace() => Assert.Equal("a b c", Checklists.Normalise("  a \t b\r\n c  "));
}
