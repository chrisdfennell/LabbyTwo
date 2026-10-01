using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace LabbyTwo.Tests;

/// <summary>
/// A Markdown card's history — on a dashboard or as a custom page's Markdown block, which
/// are the same widget row — against a real database: saves that change the text keep what
/// they replaced and nothing else does, the cap, restoring (itself a save, so undoable),
/// deleted cards coming back whole with their ticks, pruning, the move of #94's note ticks
/// to owner keys, and every new query read through an index.
/// </summary>
public sealed class CardHistoryTests : IDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;

    public CardHistoryTests()
    {
        _services = TestHost.Build(_directory, services =>
        {
            services.AddSingleton<NotesStore>();
            services.AddSingleton<ChecklistStore>();
            services.AddSingleton<Markdown>();
            services.AddSingleton<MarkdownChecklists>();
            services.AddSingleton<WidgetHistoryStore>();
            services.AddSingleton<UndoService>();
            services.AddSingleton<Deletions>();
        });
        Get<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => TestHost.Teardown(_services, _directory);

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private ConfigStore Config => Get<ConfigStore>();

    private WidgetHistoryStore History => Get<WidgetHistoryStore>();

    private static Widget Card(string text, string id = "card-1", string tab = "page", string type = WidgetHistoryStore.MarkdownType) => new()
    {
        Id = id,
        TabId = tab,
        Type = type,
        Title = "Roundup",
        Width = 6,
        Height = 2,
        Sort = 3,
        Settings = new SettingsBag { [WidgetHistoryStore.ContentKey] = text },
    };

    private async Task<Widget> CurrentAsync(string id = "card-1") =>
        (await Config.WidgetsAsync()).Single(w => w.Id == id);

    [Fact]
    public async Task EachSaveThatChangesTheTextKeepsWhatItReplacedWithWhoWroteIt()
    {
        await Config.SaveWidgetAsync(Card("one"), "alice");
        await Config.SaveWidgetAsync(Card("one\ntwo"), "bob");
        await Config.SaveWidgetAsync(Card("one\ntwo\nthree"), "chris");

        var versions = await History.VersionsAsync("card-1");

        Assert.Equal(2, versions.Count);
        // Newest first: what bob wrote (replaced by chris), then what alice wrote (replaced by bob).
        Assert.Equal(("bob", "chris", 7), (versions[0].WrittenBy, versions[0].KeptBy, versions[0].Size));
        Assert.Equal(("", "bob", 3), (versions[1].WrittenBy, versions[1].KeptBy, versions[1].Size));
        // Nobody recorded when alice's first text was written; bob's was written when it replaced hers.
        Assert.False(versions[1].WrittenKnown);
        Assert.Equal(versions[1].KeptAt, versions[0].WrittenAt);
        Assert.All(versions, v => Assert.Equal("", v.Content));
        Assert.Equal("one\ntwo", (await History.VersionAsync(versions[0].Id))!.Content);
    }

    [Fact]
    public async Task MovingResizingRetitlingAndOtherCardsAreNotVersions()
    {
        await Config.SaveWidgetAsync(Card("text"), "alice");
        await Config.SaveWidgetAsync(Card("text") with { Width = 12, Height = 4, Title = "Renamed" }, "bob");
        await Config.SaveWidgetAsync(Card("text"), "bob");
        await Config.SaveWidgetAsync(Card("other", id: "card-2"));
        await Config.ReorderWidgetAsync("card-1", 1);
        await Config.SaveWidgetAsync(Card("a", id: "gauge", type: "gauge"));
        await Config.SaveWidgetAsync(Card("b", id: "gauge", type: "gauge"));

        Assert.Empty(await History.VersionsAsync("card-1"));
        Assert.Empty(await History.VersionsAsync("gauge"));
    }

    [Fact]
    public async Task OnlyTheNewestFiftyAreKept()
    {
        for (var i = 0; i <= WidgetHistoryStore.KeepPer + 5; i++)
            await Config.SaveWidgetAsync(Card($"v{i}"));

        var versions = await History.VersionsAsync("card-1");
        Assert.Equal(WidgetHistoryStore.KeepPer, versions.Count);
        Assert.Equal($"v{WidgetHistoryStore.KeepPer + 4}", (await History.VersionAsync(versions[0].Id))!.Content);
    }

    [Fact]
    public async Task RestoringIsASaveSoItCanBeUndoneAndOnlyTheTextComesBack()
    {
        await Config.SaveWidgetAsync(Card("first"), "alice");
        await Config.SaveWidgetAsync(Card("second") with { Title = "Renamed", Width = 12 }, "bob");
        var first = Assert.Single(await History.VersionsAsync("card-1"));

        var restored = await History.RestoreVersionAsync(first.Id, "chris");

        Assert.NotNull(restored);
        var now = await CurrentAsync();
        Assert.Equal("first", now.Settings.Get(WidgetHistoryStore.ContentKey));
        // The card keeps the title and size it has now; those were never part of its history.
        Assert.Equal(("Renamed", 12), (now.Title, now.Width));
        var versions = await History.VersionsAsync("card-1");
        Assert.Equal(2, versions.Count);
        Assert.Equal("second", (await History.VersionAsync(versions[0].Id))!.Content);
        Assert.Equal("chris", versions[0].KeptBy);
    }

    [Fact]
    public async Task ADeletedCardComesBackWholeWithItsHistoryAndTicks()
    {
        await Config.SaveWidgetAsync(Card("- [ ] Stop Plex\n- [ ] Snapshot\n"), "alice");
        await Config.SaveWidgetAsync(Card("- [ ] Stop Plex\n- [ ] Snapshot\n- [ ] Reboot\n"), "alice");
        var owner = ChecklistOwners.Widget("card-1");
        var item = Get<Markdown>().ChecklistItems("- [ ] Stop Plex\n")[0];
        await Get<ChecklistStore>().SetAsync(owner, item, true, "alice", DateTimeOffset.Now);

        await Get<Deletions>().WidgetAsync(await CurrentAsync(), "bob");
        Assert.DoesNotContain(await Config.WidgetsAsync(), w => w.Id == "card-1");

        var deleted = Assert.Single(await History.RecentlyDeletedAsync("page"));
        Assert.Equal(("Roundup", "bob", true), (deleted.Title, deleted.KeptBy, deleted.IsDeletion));
        Assert.Empty(await History.RecentlyDeletedAsync("another-page"));

        Assert.NotNull(await History.RestoreVersionAsync(deleted.Id, "carol"));

        var back = await CurrentAsync();
        Assert.Equal(("page", "Roundup", 6, 2, 3), (back.TabId, back.Title, back.Width, back.Height, back.Sort));
        Assert.Equal("- [ ] Stop Plex\n- [ ] Snapshot\n- [ ] Reboot\n", back.Settings.Get(WidgetHistoryStore.ContentKey));
        Assert.Single(await Get<ChecklistStore>().TicksAsync(owner));
        Assert.Empty(await History.RecentlyDeletedAsync("page"));
        // Its history came back with it: the edit, and the deletion itself.
        Assert.Equal(2, (await History.VersionsAsync("card-1")).Count);
    }

    [Fact]
    public async Task ACardWhoseDeleteWasUndoneIsNotRecentlyDeleted()
    {
        await Config.SaveWidgetAsync(Card("text"));
        var card = await CurrentAsync();
        await Get<Deletions>().WidgetAsync(card);

        // What the undo does: the row goes back.
        await Config.SaveWidgetAsync(card);

        Assert.Empty(await History.RecentlyDeletedAsync("page"));
    }

    [Fact]
    public async Task SavingNewTextCarriesTheTicksAlong()
    {
        var markdown = Get<Markdown>();
        await Config.SaveWidgetAsync(Card("- [ ] Stop Plex\n- [ ] Snapshot\n- [ ] Gone soon\n"));
        var owner = ChecklistOwners.Widget("card-1");
        foreach (var item in markdown.ChecklistItems("- [ ] Stop Plex\n- [ ] Snapshot\n- [ ] Gone soon\n"))
            await Get<ChecklistStore>().SetAsync(owner, item, true, "chris", DateTimeOffset.Now);

        const string after = "Intro.\n\n- [ ] Stop Plex and Sonarr\n- [ ] Snapshot\n";
        await Config.SaveWidgetAsync(Card(after), "chris");

        var ticks = await Get<ChecklistStore>().TicksAsync(owner);
        var now = markdown.ChecklistItems(after);
        Assert.Equal(2, ticks.Count);
        Assert.Equal("Stop Plex and Sonarr", ticks[now[0].Key].Text);
        Assert.True(ticks.ContainsKey(now[1].Key));
    }

    [Fact]
    public async Task PruningDropsOldVersionsAndLongDeletedCards()
    {
        await Config.SaveWidgetAsync(Card("a", id: "kept"));
        await Config.SaveWidgetAsync(Card("b", id: "kept"));
        await Config.SaveWidgetAsync(Card("x", id: "gone"));
        await Config.SaveWidgetAsync(Card("y", id: "gone"));
        await Config.DeleteWidgetAsync("gone");
        await Get<ChecklistStore>().SetAsync(ChecklistOwners.Widget("gone"), new ChecklistItem("k:0", "k", 0, false), true, "", DateTimeOffset.Now);
        await Get<ChecklistStore>().SetAsync(ChecklistOwners.Widget("kept"), new ChecklistItem("k:0", "k", 0, false), true, "", DateTimeOffset.Now);

        // A month and a bit on: the deleted card's history and ticks go, the live card's stay.
        var monthOn = DateTimeOffset.UtcNow + WidgetHistoryStore.DeletedFor + TimeSpan.FromDays(2);
        Assert.Equal(2, await History.PruneAsync(monthOn, CancellationToken.None));
        Assert.Empty(await History.VersionsAsync("gone"));
        Assert.Single(await History.VersionsAsync("kept"));
        await using (var db = await Get<Db>().OpenAsync())
        {
            var count = db.CreateCommand();
            count.CommandText = "SELECT owner FROM checklist_ticks";
            Assert.Equal(ChecklistOwners.Widget("kept"), (string)(await count.ExecuteScalarAsync())!);
        }

        // Half a year on, even the live card's old versions go.
        Assert.Equal(1, await History.PruneAsync(DateTimeOffset.UtcNow + WidgetHistoryStore.KeepFor + TimeSpan.FromDays(1), CancellationToken.None));
        Assert.Empty(await History.VersionsAsync("kept"));
    }

    [Fact]
    public async Task NotesAndCardsKeepTheirTicksApart()
    {
        var item = new ChecklistItem("k:0", "k", 0, false);
        await Get<ChecklistStore>().SetAsync(ChecklistOwners.Note("same-id"), item, true, "a", DateTimeOffset.Now);

        Assert.Empty(await Get<ChecklistStore>().TicksAsync(ChecklistOwners.Widget("same-id")));
        Assert.Single(await Get<ChecklistStore>().TicksAsync(ChecklistOwners.Note("same-id")));

        // Pruning notes' ticks never touches a card's, and the other way round.
        await Get<ChecklistStore>().SetAsync(ChecklistOwners.Widget("no-such-card-yet"), item, true, "a", DateTimeOffset.Now);
        await Get<NotesStore>().PruneAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Single(await Get<ChecklistStore>().TicksAsync(ChecklistOwners.Widget("no-such-card-yet")));
    }

    /// <summary>
    /// A database with ticks made under #94 — note_checks, keyed by bare note id — is
    /// migrated to owner keys and every tick survives, under "note:&lt;id&gt;".
    /// </summary>
    [Fact]
    public async Task NoteTicksMadeBeforeOwnerKeysSurviveTheMigration()
    {
        await using (var db = await Get<Db>().OpenAsync())
        {
            var version = db.CreateCommand();
            version.CommandText = "PRAGMA user_version";
            var current = Convert.ToInt32(await version.ExecuteScalarAsync());

            // Back to how #94 left it: note_checks with a tick in it, and the version stamp
            // from before the move (and before card history).
            var downgrade = db.CreateCommand();
            downgrade.CommandText = $"""
                DROP TABLE checklist_ticks;
                DROP TABLE widget_versions;
                CREATE TABLE note_checks (
                    note_id   TEXT    NOT NULL,
                    item_key  TEXT    NOT NULL,
                    item_text TEXT    NOT NULL DEFAULT '',
                    position  INTEGER NOT NULL DEFAULT 0,
                    ticked_by TEXT    NOT NULL DEFAULT '',
                    ticked_at INTEGER NOT NULL,
                    PRIMARY KEY (note_id, item_key)) WITHOUT ROWID;
                INSERT INTO note_checks VALUES ('runbook', 'abc:0', 'Stopped Plex', 0, 'chris', 1700000000);
                INSERT INTO note_checks VALUES ('runbook', 'def:0', 'Took a snapshot', 1, '', 1700000100);
                PRAGMA user_version = {current - 2};
                """;
            await downgrade.ExecuteNonQueryAsync();
        }

        var reopened = new Db(Options.Create(new LabbyOptions { DatabasePath = Path.Combine(_directory, "test.db") }),
            Get<IHostEnvironment>());
        await reopened.EnsureSchemaAsync();
        var store = new ChecklistStore(reopened);

        var ticks = await store.TicksAsync(ChecklistOwners.Note("runbook"));
        Assert.Equal(2, ticks.Count);
        Assert.Equal(("Stopped Plex", "chris", 0), (ticks["abc:0"].Text, ticks["abc:0"].By, ticks["abc:0"].Position));
        Assert.Equal(1700000100, ticks["def:0"].At.ToUnixTimeSeconds());

        await using var check = await reopened.OpenAsync();
        var old = check.CreateCommand();
        old.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = 'note_checks'";
        Assert.Equal(0L, (long)(await old.ExecuteScalarAsync())!);
    }

    // ---------- query plans ----------

    [Theory]
    [InlineData(WidgetHistoryStore.VersionsSql, "ix_widget_versions_widget")]
    [InlineData(WidgetHistoryStore.TrimSql, "ix_widget_versions_widget")]
    [InlineData(WidgetHistoryStore.KeepVersionSql, "ix_widget_versions_widget")]
    [InlineData(WidgetHistoryStore.RecentlyDeletedSql, "ix_widget_versions_deleted")]
    [InlineData(WidgetHistoryStore.PruneOldSql, "ix_widget_versions_kept")]
    [InlineData(WidgetHistoryStore.PruneGoneSql, "ix_widget_versions_kept")]
    public void HistoryQueriesUseTheirIndex(string sql, string index)
    {
        var plan = QueryPlanTests.Plan(sql);

        Assert.Contains(plan, step => step.Contains(index, StringComparison.Ordinal));
        Assert.DoesNotContain(plan, step => step.StartsWith("SCAN widget_versions", StringComparison.Ordinal)
                                            || step.StartsWith("SCAN v", StringComparison.Ordinal)
                                            || step.StartsWith("SCAN w", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(WidgetHistoryStore.VersionSql)]
    [InlineData(ChecklistStore.ForOwnerSql)]
    public void LookupsAreByKey(string sql)
    {
        var plan = QueryPlanTests.Plan(sql);

        Assert.Contains(plan, step => step.StartsWith("SEARCH", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, step => step.StartsWith("SCAN", StringComparison.Ordinal));
    }

    /// <summary>The tick tidy-ups read only their own owner range, along the primary key.</summary>
    [Theory]
    [InlineData(WidgetHistoryStore.PruneTicksSql)]
    [InlineData(NotesStore.PruneTicksSql)]
    public void TickTidyUpsReadOnlyTheirOwnRange(string sql)
    {
        var plan = QueryPlanTests.Plan(sql);

        Assert.Contains(plan, step => step.StartsWith("SEARCH checklist_ticks USING PRIMARY KEY (owner>? AND owner<?)", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, step => step.StartsWith("SCAN", StringComparison.Ordinal));
    }
}
