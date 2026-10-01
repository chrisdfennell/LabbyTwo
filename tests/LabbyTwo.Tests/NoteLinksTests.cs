using LabbyTwo.Core;

namespace LabbyTwo.Tests;

/// <summary>
/// <c>[[Note title]]</c> links as text — what counts as one, what does not, writing one back
/// — and resolving them against a set of notes: titles in any case, a page in front, a
/// heading behind, titles that themselves contain a slash or a hash, two notes of one
/// title, and a note renamed after it was linked to.
/// </summary>
public sealed class NoteLinksTests
{
    // ---------- finding ----------

    [Fact]
    public void APlainLinkIsItsTitle()
    {
        var link = Assert.Single(NoteLinks.Find("See [[Plex runbook]] first."));
        Assert.Equal("Plex runbook", link.Target);
        Assert.Null(link.Text);
        Assert.Equal("[[Plex runbook]]", link.Source);
        Assert.Equal(4, link.Index);
        Assert.Equal(16, link.Length);
    }

    [Fact]
    public void TextHeadingAndPageAreReadAsWritten()
    {
        var links = NoteLinks.Find("[[Plex runbook|the Plex steps]] and [[Runbooks / Plex runbook#Restart]]");
        Assert.Equal(2, links.Count);
        Assert.Equal("Plex runbook", links[0].Target);
        Assert.Equal("the Plex steps", links[0].Text);
        Assert.Equal("Runbooks / Plex runbook#Restart", links[1].Target);
    }

    [Fact]
    public void SpacesInsideAreTidied()
    {
        var link = Assert.Single(NoteLinks.Find("[[Plex   runbook |  the  steps]]"));
        Assert.Equal("Plex runbook", link.Target);
        Assert.Equal("the steps", link.Text);
    }

    [Theory]
    [InlineData("if [[ -f /etc/x ]]; then")]         // a shell test is not a link
    [InlineData("[[]]")]                             // nothing in it
    [InlineData("[[Plex\nrunbook]]")]                 // across a line
    [InlineData("[[Plex [beta] runbook]]")]           // another bracket inside
    [InlineData("[[Plex runbook]")]                  // never closed
    [InlineData(@"\[[Plex runbook]]")]                // escaped
    [InlineData("[[{{status: NAS}}]]")]               // a shortcode inside stays a shortcode
    [InlineData("[[|just words]]")]                   // no target
    [InlineData("[x](y) [ [not] ]")]                  // ordinary Markdown
    public void ThingsThatAreNotLinks(string text) => Assert.Empty(NoteLinks.Find(text));

    [Fact]
    public void AnEscapedBackslashStillLinks()
    {
        var link = Assert.Single(NoteLinks.Find(@"\\[[Plex]]"));
        Assert.Equal("Plex", link.Target);
    }

    [Fact]
    public void AStrayOpeningDoesNotSwallowTheNextLink()
    {
        var link = Assert.Single(NoteLinks.Find("[[unfinished and then [[Plex]]"));
        Assert.Equal("Plex", link.Target);
    }

    [Fact]
    public void AVeryLongRunIsNotALink() =>
        Assert.Empty(NoteLinks.Find("[[" + new string('a', NoteLinks.MaxLength + 5) + "]]"));

    [Fact]
    public void TargetsAreDistinctAndFolded() =>
        Assert.Equal(["plex runbook", "nas#restart"], NoteLinks.Targets("[[Plex Runbook]] [[plex  runbook|x]] [[NAS#Restart]]"));

    // ---------- writing ----------

    [Theory]
    [InlineData("Plex runbook", null, null, null, "[[Plex runbook]]")]
    [InlineData("Plex runbook", "Restart", "the steps", "Runbooks", "[[Runbooks / Plex runbook#Restart|the steps]]")]
    [InlineData("  Plex   runbook ", "", "", "", "[[Plex runbook]]")]
    public void WrittenLinksReadBack(string title, string? heading, string? text, string? page, string expected)
    {
        var written = NoteLinks.Write(title, heading, text, page);
        Assert.Equal(expected, written);
        Assert.Single(NoteLinks.Find(written));
    }

    [Theory]
    [InlineData("A [b]")]
    [InlineData("A|B")]
    [InlineData("A {{status: NAS}}")]
    [InlineData("Two\nlines")]
    [InlineData("   ")]
    public void TitlesThatCannotBeLinked(string title)
    {
        Assert.False(NoteLinks.CanLink(title));
        Assert.Null(NoteLinks.Write(title));
    }

    // ---------- resolving ----------

    private static readonly NotePlace Runbooks = new("tab-runbooks", "Runbooks", null, "t/runbooks");
    private static readonly NotePlace Home = new("tab-home", "Home notes", null, "t/home");
    private static readonly NotePlace Section = new("block-1", "Overview", "Lab notes", "t/overview");

    private static NoteGraph Graph(IEnumerable<NoteEntry> notes, params NoteLinkRow[] links) =>
        new([Runbooks, Home, Section], notes, links);

    [Fact]
    public void ATitleResolvesInAnyCase()
    {
        var graph = Graph([new("n1", "Plex runbook", Runbooks)]);
        var target = graph.Resolve("PLEX RUNBOOK", null);
        Assert.Equal("n1", target.Note?.Id);
        Assert.Null(target.Heading);
        Assert.Equal("t/runbooks#note-n1", target.Note!.Href);
    }

    [Fact]
    public void AHeadingIsSplitOffAndTravelsInTheQuery()
    {
        var graph = Graph([new("n1", "Plex runbook", Runbooks)]);
        var target = graph.Resolve("Plex runbook#Restart it", null);
        Assert.Equal("n1", target.Note?.Id);
        Assert.Equal("Restart it", target.Heading);
        Assert.Equal("t/runbooks?heading=Restart%20it#note-n1", target.Note!.HrefTo(target.Heading));
    }

    [Fact]
    public void ATitleWithASlashOrHashIsTriedWholeFirst()
    {
        var graph = Graph([new("n1", "C# notes", Runbooks), new("n2", "TCP/IP", Home)]);
        Assert.Equal("n1", graph.Resolve("C# notes", null).Note?.Id);
        Assert.Null(graph.Resolve("C# notes", null).Heading);
        Assert.Equal("n2", graph.Resolve("TCP/IP", null).Note?.Id);
        Assert.Equal("n2", graph.Resolve("Home notes / TCP/IP#Ports", null).Note?.Id);
    }

    [Fact]
    public void APageInFrontPicksBetweenTwoOfTheSameTitle()
    {
        var graph = Graph([new("n1", "Restart", Runbooks), new("n2", "Restart", Home), new("n3", "Restart", Section)]);
        Assert.Equal("n2", graph.Resolve("Home notes / Restart", null).Note?.Id);
        Assert.Equal("n1", graph.Resolve("runbooks / restart", null).Note?.Id);
        // A notes section answers to its own title and to its page's name.
        Assert.Equal("n3", graph.Resolve("Lab notes / Restart", null).Note?.Id);
        Assert.Equal("n3", graph.Resolve("Overview / Restart", null).Note?.Id);
    }

    [Fact]
    public void TwoOfTheSameTitlePreferTheOneOnTheSamePage()
    {
        var graph = Graph([new("n1", "Restart", Runbooks), new("n2", "Restart", Home), new("from", "Index", Home)]);
        Assert.Equal("n2", graph.Resolve("Restart", "from").Note?.Id);
        // From a Markdown card, which is on no notes page: the first by page name.
        Assert.Equal("n2", graph.Resolve("Restart", "some-card").Note?.Id);
    }

    [Fact]
    public void AMissingNoteKeepsItsTitleAndThePageItNamed()
    {
        var graph = Graph([new("n1", "Plex runbook", Runbooks)]);
        var missing = graph.Resolve("Runbooks / Sonarr runbook#Restart", null);
        Assert.Null(missing.Note);
        Assert.Equal("Sonarr runbook", missing.Title);
        Assert.Equal("Runbooks", missing.Page);
        Assert.Equal("Restart", missing.Heading);
        Assert.Equal("t/runbooks?new=Sonarr%20runbook&in=tab-runbooks", Runbooks.NewNoteHref(missing.Title));

        // A slash that names no page is part of the title.
        Assert.Equal("Backups 1/2", graph.Resolve("Backups 1/2", null).Title);
    }

    [Fact]
    public void ARenamedNoteIsStillFoundByTheIdTheIndexRemembered()
    {
        // "Plex runbook" was renamed "Plex"; the link was written, and resolved, before that.
        var graph = Graph([new("n1", "Plex", Runbooks), new("from", "Index", Home)],
            new NoteLinkRow("from", "plex runbook#restart", "n1"));
        var target = graph.Resolve("Plex runbook#Restart", "from");
        Assert.Equal("n1", target.Note?.Id);
        Assert.True(target.ByRememberedId);
        Assert.Equal("Restart", target.Heading);
        Assert.Equal(["from"], graph.Backlinks("n1").Select(n => n.Id));
        // Found by id, not by title: renaming it again would not make that link's text staler.
        Assert.Empty(graph.LinkingByTitle("n1"));
    }

    [Fact]
    public void BacklinksAreDistinctSortedAndNeverTheNoteItself()
    {
        var graph = Graph(
            [new("target", "Plex", Runbooks), new("b", "Beta", Home), new("a", "Alpha", Home)],
            new NoteLinkRow("b", "plex", null),
            new NoteLinkRow("b", "plex#restart", null),
            new NoteLinkRow("a", "runbooks / plex", null),
            new NoteLinkRow("target", "plex", null),
            new NoteLinkRow("gone", "plex", null));

        Assert.Equal(["Alpha", "Beta"], graph.Backlinks("target").Select(n => n.Title));
        Assert.Equal(["Alpha", "Beta"], graph.LinkingByTitle("target").Select(n => n.Title));
        Assert.Contains("gone", graph.Orphans);
        // Every row that resolved is written back with the note it found.
        Assert.All(graph.Corrections, row => Assert.Equal("target", row.ToId));
        Assert.Equal(4, graph.Corrections.Count);
    }

    [Fact]
    public void NotesOnNoKnownPageAreLeftOut()
    {
        var graph = Graph([new("n1", "Plex", Runbooks)]);
        Assert.Null(graph.Note("elsewhere"));
        Assert.Equal(Runbooks, graph.PlaceOf("n1"));
        Assert.Equal(Home, graph.PlaceOf("tab-home"));
        Assert.Null(graph.PlaceOf("a-card"));
    }

    // ---------- retargeting after a rename ----------

    [Fact]
    public void RetargetRewritesOnlyTheLinksToTheRenamedNote()
    {
        var before = Graph([new("n1", "Plex runbook", Runbooks), new("n2", "Sonarr", Runbooks)]);
        const string markdown = "[[Plex runbook]], [[plex runbook#Restart|the steps]], [[Runbooks / Plex runbook]], [[Sonarr]], `[[Plex runbook]]` x";

        var rewritten = NoteLinks.Retarget(markdown,
            link => before.Resolve(link.Target, null) is { Note.Id: "n1" } found ? found : null, "Plex");

        Assert.Equal("[[Plex]], [[Plex#Restart|the steps]], [[Runbooks / Plex]], [[Sonarr]], `[[Plex]]` x", rewritten);
    }

    [Fact]
    public void RetargetLeavesTextWithNoLinksAlone()
    {
        const string markdown = "Nothing {{status: NAS}} here.";
        Assert.Same(markdown, NoteLinks.Retarget(markdown, _ => null, "X"));
    }
}
