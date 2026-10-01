using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace LabbyTwo.Tests;

/// <summary>
/// A note's history against a real database: every save keeping what it replaced, the
/// caps on how much is kept, restoring (itself undoable), deleted notes coming back with
/// their history and ticks, the line diff between versions — and every new query read
/// through an index rather than a scan.
/// </summary>
public sealed class NoteHistoryTests : IDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;

    public NoteHistoryTests()
    {
        _services = TestHost.Build(_directory, services =>
        {
            services.AddSingleton<NotesStore>();
            services.AddSingleton<ChecklistStore>();
        });
        _services.GetRequiredService<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => TestHost.Teardown(_services, _directory);

    private NotesStore Notes => _services.GetRequiredService<NotesStore>();

    private async Task SqlAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var db = await _services.GetRequiredService<Db>().OpenAsync();
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task EachSaveKeepsWhatItReplacedWithWhoWroteIt()
    {
        var id = await Notes.SaveAsync(null, "tab", "Restart", "one", "alice");
        await Notes.SaveAsync(id, "tab", "Restart", "one\ntwo", "bob");
        await Notes.SaveAsync(id, "tab", "Restart steps", "one\ntwo\nthree", "chris");

        var versions = await Notes.VersionsAsync(id);

        Assert.Equal(2, versions.Count);
        // Newest first: what bob wrote, then what alice wrote.
        Assert.Equal(("Restart", "bob", 7), (versions[0].Title, versions[0].WrittenBy, versions[0].Size));
        Assert.Equal(("Restart", "alice", 3), (versions[1].Title, versions[1].WrittenBy, versions[1].Size));
        Assert.All(versions, v => Assert.Equal("", v.Content));
        Assert.Equal("one\ntwo", (await Notes.VersionAsync(versions[0].Id))!.Content);

        var note = Assert.Single(await Notes.ForTabAsync("tab"));
        Assert.Equal("chris", note.UpdatedBy);
    }

    [Fact]
    public async Task SavingTheSameTextIsNotAVersion()
    {
        var id = await Notes.SaveAsync(null, "tab", "Same", "text", "alice");
        await Notes.SaveAsync(id, "tab", "Same", "text", "alice");
        await Notes.SaveAsync(id, "tab", "Same", "text");

        Assert.Empty(await Notes.VersionsAsync(id));
    }

    [Fact]
    public async Task OnlyTheNewestVersionsAreKept()
    {
        var id = await Notes.SaveAsync(null, "tab", "Busy", "v0");
        for (var i = 1; i <= NotesStore.KeepPerNote + 5; i++)
            await Notes.SaveAsync(id, "tab", "Busy", $"v{i}");

        var versions = await Notes.VersionsAsync(id);

        Assert.Equal(NotesStore.KeepPerNote, versions.Count);
        Assert.Equal($"v{NotesStore.KeepPerNote + 4}", (await Notes.VersionAsync(versions[0].Id))!.Content);
        Assert.Equal("v5", (await Notes.VersionAsync(versions[^1].Id))!.Content);
    }

    [Fact]
    public async Task RestoringAVersionIsItselfAVersion()
    {
        var id = await Notes.SaveAsync(null, "tab", "Runbook", "first", "alice");
        await Notes.SaveAsync(id, "tab", "Runbook", "second", "bob");
        var first = (await Notes.VersionsAsync(id)).Single();

        var restored = await Notes.RestoreVersionAsync(first.Id, "chris");

        Assert.NotNull(restored);
        var note = Assert.Single(await Notes.ForTabAsync("tab"));
        Assert.Equal("first", note.Content);
        Assert.Equal("chris", note.UpdatedBy);
        // What the restore replaced is kept, so the restore can be undone the same way.
        var versions = await Notes.VersionsAsync(id);
        Assert.Equal(2, versions.Count);
        Assert.Equal("second", (await Notes.VersionAsync(versions[0].Id))!.Content);

        await Notes.RestoreVersionAsync(versions[0].Id, "chris");
        Assert.Equal("second", Assert.Single(await Notes.ForTabAsync("tab")).Content);
    }

    [Fact]
    public async Task ADeletedNoteComesBackWithItsHistoryAndTicks()
    {
        var id = await Notes.SaveAsync(null, "tab", "Checklist", "- [ ] One\n", "alice");
        await Notes.SaveAsync(id, "tab", "Checklist", "- [ ] One\n- [ ] Two\n", "alice");
        var item = new Markdown().ChecklistItems("- [ ] One\n")[0];
        var ticks = _services.GetRequiredService<ChecklistStore>();
        await ticks.SetAsync(id, item, true, "alice", DateTimeOffset.Now);

        await Notes.DeleteAsync(id, "bob");

        Assert.Empty(await Notes.ForTabAsync("tab"));
        var deleted = Assert.Single(await Notes.RecentlyDeletedAsync("tab"));
        Assert.Equal(("Checklist", "bob", id), (deleted.Title, deleted.WrittenBy, deleted.NoteId));
        Assert.True(deleted.IsDeletion);
        Assert.Empty(await Notes.RecentlyDeletedAsync("another-tab"));

        await Notes.RestoreVersionAsync(deleted.Id, "bob");

        var back = Assert.Single(await Notes.ForTabAsync("tab"));
        Assert.Equal(id, back.Id);
        Assert.Equal("- [ ] One\n- [ ] Two\n", back.Content);
        Assert.Empty(await Notes.RecentlyDeletedAsync("tab"));
        // Its history is still there, and so is its tick — the same id, the same items.
        Assert.Contains(await Notes.VersionsAsync(id), v => v.Reason == NotesStore.Edited);
        Assert.Single(await ticks.TicksAsync(id));
    }

    [Fact]
    public async Task RecentlyDeletedOffersEachNoteOnceAndOnlyForAMonth()
    {
        var id = await Notes.SaveAsync(null, "tab", "Twice", "x");
        await Notes.DeleteAsync(id, "a");
        await Notes.RestoreVersionAsync((await Notes.RecentlyDeletedAsync("tab")).Single().Id, "a");
        await Notes.DeleteAsync(id, "b");

        var listed = Assert.Single(await Notes.RecentlyDeletedAsync("tab"));
        Assert.Equal("b", listed.WrittenBy);

        Assert.Empty(await Notes.RecentlyDeletedAsync("tab", DateTimeOffset.UtcNow + NotesStore.DeletedFor + TimeSpan.FromDays(1)));
    }

    [Fact]
    public async Task PruningDropsOldVersionsAndLongDeletedNotes()
    {
        var kept = await Notes.SaveAsync(null, "tab", "Kept", "a");
        await Notes.SaveAsync(kept, "tab", "Kept", "b");
        var gone = await Notes.SaveAsync(null, "tab", "Gone", "x");
        await Notes.SaveAsync(gone, "tab", "Gone", "y");
        await Notes.DeleteAsync(gone);
        await _services.GetRequiredService<ChecklistStore>().SetAsync(gone, new ChecklistItem("k:0", "k", 0, false), true, "", DateTimeOffset.Now);

        // A month and a bit on: the deleted note's history goes, the live note's stays.
        var monthOn = DateTimeOffset.UtcNow + NotesStore.DeletedFor + TimeSpan.FromDays(2);
        Assert.Equal(2, await Notes.PruneAsync(monthOn, CancellationToken.None));
        Assert.Empty(await Notes.VersionsAsync(gone));
        Assert.Single(await Notes.VersionsAsync(kept));

        // Its ticks have nothing left to come back with, so they went too.
        await using (var db = await _services.GetRequiredService<Db>().OpenAsync())
        {
            var count = db.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM note_checks";
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
        }

        // Half a year on, even the live note's old versions go.
        Assert.Equal(1, await Notes.PruneAsync(DateTimeOffset.UtcNow + NotesStore.KeepFor + TimeSpan.FromDays(1), CancellationToken.None));
        Assert.Empty(await Notes.VersionsAsync(kept));
        Assert.Single(await Notes.ForTabAsync("tab"));
    }

    [Fact]
    public async Task ANoteDeletedWithItsTabKeepsItsHistoryWhileTheUndoCanStillBringItBack()
    {
        var id = await Notes.SaveAsync(null, "tab", "On a tab", "a");
        await Notes.SaveAsync(id, "tab", "On a tab", "b");
        // How a tab's notes go: straight out of the table, no deletion recorded.
        await SqlAsync("DELETE FROM notes WHERE id = $id", ("$id", id));

        Assert.Equal(0, await Notes.PruneAsync(DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Single(await Notes.VersionsAsync(id));
    }

    // ---------- the line diff ----------

    [Fact]
    public void TheDiffKeepsSharedLinesAndMarksTheRest()
    {
        var diff = LineDiff.Compare("a\nb\nc\nd", "a\nB\nc\nd\ne");

        Assert.Equal(
            ["  a", "- b", "+ B", "  c", "  d", "+ e"],
            diff.Select(d => (d.Kind switch { DiffKind.Added => "+ ", DiffKind.Removed => "- ", _ => "  " }) + d.Text));
        Assert.Equal((2, (int?)null), (diff[1].OldLine!.Value, diff[1].NewLine));
        Assert.Equal(((int?)null, 5), (diff[5].OldLine, diff[5].NewLine!.Value));
    }

    [Fact]
    public void TheDiffOfEqualAndEmptyTexts()
    {
        Assert.All(LineDiff.Compare("x\r\ny", "x\ny"), d => Assert.Equal(DiffKind.Same, d.Kind));
        Assert.Empty(LineDiff.Compare("", ""));
        Assert.Equal([DiffKind.Added, DiffKind.Added], LineDiff.Compare(null, "a\nb").Select(d => d.Kind));
        Assert.Equal([DiffKind.Removed], LineDiff.Compare("a", "").Select(d => d.Kind));
    }

    [Fact]
    public void TheDiffFindsTheLongestRunOfSharedLines()
    {
        var diff = LineDiff.Compare("1\n2\n3\n4\n5", "0\n2\n3\n4\n6");

        Assert.Equal(3, diff.Count(d => d.Kind == DiffKind.Same));
        Assert.Equal(2, diff.Count(d => d.Kind == DiffKind.Removed));
        Assert.Equal(2, diff.Count(d => d.Kind == DiffKind.Added));
    }

    [Fact]
    public void AnEnormousRewriteIsStillATrueDiff()
    {
        var old = string.Join('\n', Enumerable.Range(0, 3000).Select(i => $"old {i}"));
        var now = string.Join('\n', Enumerable.Range(0, 3000).Select(i => $"new {i}"));

        var diff = LineDiff.Compare(old, now);

        Assert.Equal(3000, diff.Count(d => d.Kind == DiffKind.Removed));
        Assert.Equal(3000, diff.Count(d => d.Kind == DiffKind.Added));
    }

    // ---------- query plans ----------

    [Theory]
    [InlineData(NotesStore.VersionsSql, "ix_note_versions_note")]
    [InlineData(NotesStore.TrimSql, "ix_note_versions_note")]
    [InlineData(NotesStore.RecentlyDeletedSql, "ix_note_versions_deleted")]
    [InlineData(NotesStore.PruneOldSql, "ix_note_versions_kept")]
    [InlineData(NotesStore.PruneGoneSql, "ix_note_versions_kept")]
    public void HistoryQueriesUseTheirIndex(string sql, string index)
    {
        var plan = QueryPlanTests.Plan(sql);

        Assert.Contains(plan, step => step.Contains(index, StringComparison.Ordinal));
        Assert.DoesNotContain(plan, step => step.StartsWith("SCAN note_versions", StringComparison.Ordinal)
                                            || step.StartsWith("SCAN v", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(NotesStore.VersionSql)]
    [InlineData(NotesStore.KeepVersionSql)]
    [InlineData(NotesStore.ByIdSql)]
    [InlineData(ChecklistStore.ForNoteSql)]
    public void LookupsAreByKey(string sql)
    {
        var plan = QueryPlanTests.Plan(sql);

        Assert.Contains(plan, step => step.StartsWith("SEARCH", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, step => step.StartsWith("SCAN", StringComparison.Ordinal));
    }
}
