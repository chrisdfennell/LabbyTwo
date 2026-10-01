using LabbyTwo.Core;
using LabbyTwo.Services;

namespace LabbyTwo.Tests;

/// <summary>
/// The built-in note templates: what they write for a connection, and that a connection's
/// name — the user's own text — can never become a shortcode, a section, a table cell or
/// formatting of its own, however it is spelt.
/// </summary>
public sealed class NoteTemplatesTests
{
    private static readonly TemplateConnection Plex = new("Plex", "cpu_percent", "CPU", ["restart", "stop"], HasWebPage: true);

    /// <summary>Names that would break a note if they went in raw.</summary>
    public static TheoryData<string> Hostile =>
    [
        "NAS}} {{button: Router / reboot",
        "my_nas *bold* | cell",
        "Living room / TV",
        "\"quoted\" name",
        "[[Other note]] {{end}}",
        "# not a heading",
    ];

    [Fact]
    public void ARunbookHasTheLiveLinesTheSectionsAndAChecklist()
    {
        var draft = NoteTemplates.RunbookFor(Plex);
        Assert.Equal("Plex runbook", draft.Title);

        var md = draft.Markdown;
        Assert.Contains("**Plex** is {{status: Plex}} — since {{since: Plex}}, up {{uptime: Plex}} of the last 30 days, checked {{ago: Plex}}.", md);
        Assert.Contains("{{sparkline: Plex / cpu_percent 24h}} CPU over the last 24 hours.", md);
        Assert.Contains("{{if down: Plex}}", md);
        Assert.Contains("{{button: Plex / restart}} {{button: Plex / stop}}", md);
        Assert.Contains("{{else}}", md);
        Assert.Contains("{{details: If it won't come back}}", md);
        Assert.Contains("- [ ] Write down what fixed it", md);
        Assert.Contains("- Its own page: {{link: Plex}}", md);
        // The link syntax is shown, in code, rather than written as a link to nothing.
        Assert.Contains("`[[Note title]]`", md);
        Assert.Empty(NoteLinks.Find(md.Replace("`[[Note title]]`", "")));
    }

    [Fact]
    public void ARunbookWithoutAMetricOrButtonsStillReadsWell()
    {
        var md = NoteTemplates.RunbookFor(new TemplateConnection("Router")).Markdown;
        Assert.Contains("{{sparkline: Router window=\"24h\"}} Response time over the last 24 hours.", md);
        Assert.DoesNotContain("{{button", md);
        Assert.DoesNotContain("{{link", md);
    }

    [Theory]
    [MemberData(nameof(Hostile))]
    public void ANameNeverBecomesAShortcodeOfItsOwn(string name)
    {
        var connection = new TemplateConnection(name, "disk_percent", "Disk *used*", ["restart"], HasWebPage: true);
        foreach (var draft in new[]
                 {
                     NoteTemplates.RunbookFor(connection),
                     NoteTemplates.ServiceOverview([connection, Plex]),
                     NoteTemplates.MaintenancePlan([connection], new DateOnly(2026, 11, 7)),
                 })
        {
            foreach (var found in Shortcodes.Find(draft.Markdown))
            {
                var code = found.Code;
                Assert.True(code.IsKnown, $"{code.Source} in {draft.Title}");
                // Every shortcode that names a connection names exactly this one, or Plex.
                if (code.Kind is "status" or "since" or "uptime" or "ago" or "sparkline" or "uptimebar" or "button" or "link")
                    Assert.Contains(code.Part(0), new[] { name, "Plex" });
                if (code.Kind == "if" && code.Part(0).StartsWith("down:", StringComparison.Ordinal))
                    Assert.Equal(name, Runbook.Parse(draft.Markdown, []).OfType<RunbookIf>().First().Condition?.Connection);
            }
            // No link to a note sneaks in through a name: the one link is the example, in code.
            Assert.Empty(NoteLinks.Find(draft.Markdown.Replace("`[[Note title]]`", "")));
        }
    }

    [Theory]
    [MemberData(nameof(Hostile))]
    public void EverySectionInATemplateIsClosed(string name)
    {
        var connection = new TemplateConnection(name, null, null, [], false);
        var markdown = new Markdown();
        foreach (var draft in new[]
                 {
                     NoteTemplates.RunbookFor(connection),
                     NoteTemplates.ServiceOverview([connection]),
                     NoteTemplates.MaintenancePlan([connection], new DateOnly(2026, 11, 7)),
                     NoteTemplates.MaintenancePlan([], new DateOnly(2026, 11, 7)),
                 })
        {
            var page = markdown.PreparePage(draft.Markdown);
            Assert.DoesNotContain(Flatten(page.Parts), p => p is LiveProblem);
            Assert.All(page.Sections(), s => Assert.Null(s.Section.Problem));
        }
    }

    [Fact]
    public void TheOverviewIsATableWithARowPerConnection()
    {
        var draft = NoteTemplates.ServiceOverview([Plex, new TemplateConnection("a|b")]);
        var md = draft.Markdown;
        Assert.Contains("| Service | Status | Since | Uptime, 30 days | Last 24 hours |", md);
        Assert.Contains("| {{link: Plex}} | {{status: Plex}} | {{since: Plex}} | {{uptime: Plex}} | {{sparkline: Plex / cpu_percent 24h}} |", md);
        // A pipe in a name is escaped where it is text, so it cannot split the row.
        Assert.Contains(@"| a\|b | {{status: a|b}} |", md);
        var html = new Markdown().Prepare(md).Html;
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(html, "<tr>").Count);
    }

    [Fact]
    public void AMaintenancePlanHasWhenWhoAndARollback()
    {
        var draft = NoteTemplates.MaintenancePlan([Plex], new DateOnly(2026, 11, 7));
        Assert.Equal("Maintenance plan · 7 Nov 2026", draft.Title);
        var md = draft.Markdown;
        Assert.Contains("- **When:** Saturday 7 November 2026, {{countdown: 2026-11-07}}", md);
        Assert.Contains("- **Who:**", md);
        Assert.Contains("## Rollback", md);
        Assert.Contains("- [ ] Check the last backup worked", md);
        Assert.Contains("| Plex | {{status: Plex}} |", md);
        Assert.Contains("{{changes: last=\"24h\" only=\"Plex\"}}", md);
    }

    [Fact]
    public void TemplatesListTheFourBuiltIns() =>
        Assert.Equal([NoteTemplates.Runbook, NoteTemplates.Incident, NoteTemplates.Maintenance, NoteTemplates.Overview],
            NoteTemplates.All.Select(t => t.Key));

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
}
