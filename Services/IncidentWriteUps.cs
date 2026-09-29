using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>Where an incident's write-up is, for the link beside it.</summary>
/// <param name="TabSlug">The notes tab it is on; null if that tab is gone, which leaves a title and no link.</param>
public sealed record WriteUpLink(long IncidentId, string NoteId, string Title, string? TabSlug)
{
    /// <summary>The note on its tab.</summary>
    public string? Url => TabSlug is null ? null : $"t/{Uri.EscapeDataString(TabSlug)}#note-{NoteId}";

    /// <summary>The note on its tab with its editor open — where a new write-up is sent.</summary>
    public string? EditUrl => TabSlug is null ? null : $"t/{Uri.EscapeDataString(TabSlug)}?edit={Uri.EscapeDataString(NoteId)}";
}

/// <summary>
/// Writes an incident up as a note (see <see cref="IncidentWriteUp"/> for what goes in it)
/// and links the two: the note links to the incident in its first line, and the incident
/// keeps the note's id to show "Write-up:" beside it.
///
/// Write-ups go on a notes tab of their own, made the first time one is written and found
/// again by a setting on it rather than by its name, so renaming it or moving it in the nav
/// changes nothing. It is an ordinary notes tab: the same editor, the same live Markdown,
/// deleted like any other — after which the next write-up makes a new one.
/// </summary>
public sealed class IncidentWriteUps(
    IncidentStore incidents,
    ChangeStore changes,
    NotesStore notes,
    ConfigStore config,
    ActionRunner actions,
    ProbableCauses causes)
{
    /// <summary>The setting that marks the notes tab write-ups go on.</summary>
    public const string TabSetting = "incident-writeups";

    /// <summary>What the tab is called when it is made.</summary>
    public const string TabName = "Incident write-ups";

    /// <summary>
    /// The write-up for an incident: the one already written if it still exists, otherwise
    /// a new one. Never two for one incident — pressing the button twice opens the first.
    /// </summary>
    public async Task<WriteUpLink> CreateAsync(Incident incident, CancellationToken ct = default)
    {
        if (incident.WriteUpNoteId is not null && (await LinksAsync([incident], ct)).GetValueOrDefault(incident.Id) is { } link)
            return link;

        var tab = await TabAsync(ct);
        var now = DateTimeOffset.Now;
        var timeline = await changes.QueryAsync(new ChangeQuery(
            IncidentRules.TimelineFrom(incident), IncidentRules.TimelineTo(incident, now), Limit: 300), ct);
        var explained = await causes.ExplainAsync(incident, ct);

        var connections = new Dictionary<string, WriteUpConnection>();
        foreach (var connection in await config.ConnectionsAsync(ct))
        {
            if (incident.Members.All(m => m.ConnectionId != connection.Id))
                continue;
            connections[connection.Id] = new WriteUpConnection(connection.Name, RestartAction(actions.ActionsFor(connection)));
        }

        var note = IncidentWriteUp.Build(incident, explained, timeline, connections, now);
        var id = await notes.SaveAsync(null, tab.Id, note.Title, note.Markdown, ct);
        await incidents.SetWriteUpAsync(incident.Id, id, ct);
        return new WriteUpLink(incident.Id, id, note.Title, tab.Slug);
    }

    /// <summary>
    /// The action worth a button in a runbook: a restart, since that is what somebody
    /// reaches for first, and only one that needs nothing typed into it and is not marked
    /// dangerous. Null for a connection with none.
    /// </summary>
    public static string? RestartAction(IEnumerable<ProviderAction> available) =>
        available
            .Where(a => !a.Dangerous && a.Fields.Count == 0)
            .FirstOrDefault(a => a.Id.Contains("restart", StringComparison.OrdinalIgnoreCase)
                                 || a.Label.Contains("restart", StringComparison.OrdinalIgnoreCase))
            ?.Id;

    /// <summary>
    /// The write-ups of the incidents that have one, by incident id. A write-up whose note
    /// has been deleted is left out, so the incident offers to write one again.
    /// </summary>
    public async Task<IReadOnlyDictionary<long, WriteUpLink>> LinksAsync(IEnumerable<Incident> list, CancellationToken ct = default)
    {
        var wanted = list.Where(i => i.WriteUpNoteId is { Length: > 0 }).ToList();
        if (wanted.Count == 0)
            return new Dictionary<long, WriteUpLink>();

        var found = (await notes.ByIdsAsync([.. wanted.Select(i => i.WriteUpNoteId!)], ct)).ToDictionary(n => n.Id);
        var result = new Dictionary<long, WriteUpLink>();
        foreach (var incident in wanted)
        {
            if (!found.TryGetValue(incident.WriteUpNoteId!, out var note))
                continue;
            var tab = await config.TabAsync(note.TabId, ct);
            result[incident.Id] = new WriteUpLink(incident.Id, note.Id,
                note.Title is { Length: > 0 } title ? title : "Untitled", tab?.Slug);
        }
        return result;
    }

    /// <summary>The write-ups tab, made at the end of the nav if there is none.</summary>
    private async Task<Tab> TabAsync(CancellationToken ct)
    {
        var tabs = await config.TabsAsync(ct);
        if (tabs.FirstOrDefault(t => t.Kind == TabKinds.Notes && t.Settings.GetBool(TabSetting)) is { } known)
            return known;

        var settings = new SettingsBag { [TabSetting] = "true" };
        var tab = new Tab
        {
            Name = TabName,
            Icon = "📝",
            Kind = TabKinds.Notes,
            Slug = await config.UniqueSlugAsync(TabName, ct: ct),
            Sort = tabs.Count == 0 ? 0 : tabs.Max(t => t.Sort) + 1,
            Settings = settings,
        };
        await config.SaveTabAsync(tab, ct);
        return tab;
    }
}
