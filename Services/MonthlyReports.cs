using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>Where a month's report is, for the link on the Settings page.</summary>
/// <param name="TabSlug">The notes tab it is on.</param>
/// <param name="Replaced">True when an earlier report of the same month was written over.</param>
public sealed record MonthlyReportLink(DateOnly Month, string NoteId, string Title, string TabSlug, bool Replaced)
{
    /// <summary>The note on its tab.</summary>
    public string Url => $"t/{Uri.EscapeDataString(TabSlug)}#note-{NoteId}";
}

/// <summary>
/// Writes a month's report as a note on the "Monthly reports" tab, the way
/// <see cref="IncidentWriteUps"/> writes an incident up: a notes tab of its own, made the
/// first time and found again by a setting on it rather than by its name, so renaming or
/// moving it changes nothing. An ordinary notes tab — the same editor, the same live
/// Markdown, so the report's chart draws there like any other.
///
/// One note per month, found by its title. Made again by hand, it is written over — the
/// button is somebody asking for the report as it stands now. Made again by the schedule
/// (a restart that lost the "done" mark, say), an existing one is left alone, since whoever
/// made it by hand may have written in it since.
/// </summary>
public sealed class MonthlyReports(MonthlyReportGatherer gatherer, NotesStore notes, ConfigStore config)
{
    /// <summary>The setting that marks the notes tab reports go on.</summary>
    public const string TabSetting = "monthly-reports";

    /// <summary>What the tab is called when it is made.</summary>
    public const string TabName = "Monthly reports";

    /// <summary>
    /// A month's report worked out but not written anywhere, for the preview. On the thread
    /// pool: gathering is a few hundred queries, and SQLite's async is synchronous underneath.
    /// </summary>
    public Task<MonthlyReportNote> PreviewAsync(DateOnly month, TimeZoneInfo zone, CancellationToken ct = default) =>
        Task.Run(async () => MonthlyReport.Build(await gatherer.GatherAsync(month, zone, ct)), ct);

    /// <summary>
    /// Works a month's report out and writes it as a note. With <paramref name="replace"/>
    /// false, an existing report of that month is kept and returned as it is, and nothing is
    /// gathered at all.
    /// </summary>
    public Task<(MonthlyReportNote? Note, MonthlyReportLink Link)> MakeAsync(
        DateOnly month, TimeZoneInfo zone, bool replace, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            var tab = await TabAsync(ct);
            var title = $"Monthly report · {MonthlySchedule.Name(month)}";
            var existing = (await notes.ForTabAsync(tab.Id, ct)).FirstOrDefault(n => n.Title == title);
            if (existing is not null && !replace)
                return ((MonthlyReportNote?)null, new MonthlyReportLink(month, existing.Id, existing.Title, tab.Slug, false));

            var note = MonthlyReport.Build(await gatherer.GatherAsync(month, zone, ct));
            var id = await notes.SaveAsync(existing?.Id, tab.Id, note.Title, note.Markdown, ct);
            return ((MonthlyReportNote?)note, new MonthlyReportLink(month, id, note.Title, tab.Slug, existing is not null));
        }, ct);

    /// <summary>The reports tab, made at the end of the nav if there is none.</summary>
    private async Task<Tab> TabAsync(CancellationToken ct)
    {
        var tabs = await config.TabsAsync(ct);
        if (tabs.FirstOrDefault(t => t.Kind == TabKinds.Notes && t.Settings.GetBool(TabSetting)) is { } known)
            return known;

        var tab = new Tab
        {
            Name = TabName,
            Icon = "🗓️",
            Kind = TabKinds.Notes,
            Slug = await config.UniqueSlugAsync(TabName, ct: ct),
            Sort = tabs.Count == 0 ? 0 : tabs.Max(t => t.Sort) + 1,
            Settings = new SettingsBag { [TabSetting] = "true" },
        };
        await config.SaveTabAsync(tab, ct);
        return tab;
    }
}
