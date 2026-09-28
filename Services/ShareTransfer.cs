using System.Text.Json;
using System.Text.Json.Serialization;
using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// One tab, or one card, as a small file somebody else can use.
///
/// <see cref="ConfigTransfer"/> already moves a whole install, and deliberately upserts by
/// id: it exists so you can restore your own dashboard, where the same id meaning the same
/// row is the entire point. That is the wrong bargain for sharing. A tab from somebody
/// else's install is a *copy* — importing it twice should give you two, and it must never
/// overwrite a tab of yours that happens to share an id.
///
/// So this allocates new ids on the way in, and carries no ids on the way out.
///
/// The other half of the problem is connections. A widget binds to a connection by id, and
/// an id from another install means nothing here. They travel as a provider and a name
/// instead — enough to find the right one on the far side, and nothing that could carry a
/// credential.
/// </summary>
public sealed class ShareTransfer(ConfigStore config, Registry registry, AppSettingsStore settings)
{
    public const int CurrentVersion = 1;

    public const string TabKind = "tab";
    public const string WidgetKind = "widget";

    /// <summary>
    /// A tab saved to start new tabs from. The same file as a shared tab, plus the name,
    /// icon and description the template was given, so one travels exactly like the other
    /// — and a template file handed to the tab import simply becomes a tab.
    /// </summary>
    public const string TemplateKind = "template";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ---- the file ------------------------------------------------------------------

    public sealed record Share
    {
        public int Version { get; init; } = CurrentVersion;

        /// <summary><see cref="TabKind"/> or <see cref="WidgetKind"/>.</summary>
        public string Kind { get; init; } = TabKind;

        public string? ExportedAt { get; init; }

        /// <summary>What the dashboard it came from is called. Provenance, and nothing more.</summary>
        public string? From { get; init; }

        public SharedTab? Tab { get; init; }
        public List<SharedWidget> Widgets { get; init; } = [];

        /// <summary>Only on a <see cref="TemplateKind"/> file: what the template is called.</summary>
        public TemplateInfo? Template { get; init; }

        /// <summary>A tab file or a template file — both carry one tab and its cards.</summary>
        [JsonIgnore]
        public bool HoldsTab => Kind is TabKind or TemplateKind;
    }

    public sealed record TemplateInfo(string Name, string Icon, string Description);

    public sealed record SharedTab(
        string Slug, string Name, string Icon, string Kind,
        Dictionary<string, string> Settings,
        Dictionary<string, ConnectionRef> Connections);

    // Height comes last and defaults to zero, so a file from before custom pages still
    // reads, and every card in it is as tall as its content, which is what it was.
    public sealed record SharedWidget(
        string Type, string Title, int Sort, int Width,
        Dictionary<string, string> Settings,
        Dictionary<string, ConnectionRef> Connections,
        ConnectionRef? BoundTo,
        int Height = 0);

    /// <summary>
    /// A connection named by what it is rather than by an id that means nothing anywhere
    /// else. Carries no settings at all, so a shared tab cannot leak a password even by
    /// accident — the receiving install matches this against something it already has.
    /// </summary>
    public sealed record ConnectionRef(string Provider, string Name);

    // ---- export ---------------------------------------------------------------------

    public async Task<(string Json, string FileName)> ExportTabAsync(string tabId, CancellationToken ct = default)
    {
        var tab = await config.TabAsync(tabId, ct)
            ?? throw new InvalidOperationException("That tab no longer exists.");

        var widgets = await config.WidgetsForTabAsync(tab.Id, ct);
        var connections = await config.ConnectionsAsync(ct);

        var kind = registry.TabKind(tab.Kind);
        var (settings, refs) = Split(tab.Settings, kind?.Fields ?? [], connections);

        var share = new Share
        {
            Kind = TabKind,
            ExportedAt = DateTimeOffset.Now.ToString("O"),
            From = await DashboardNameAsync(ct),
            Tab = new SharedTab(tab.Slug, tab.Name, tab.Icon, tab.Kind, settings, refs),
            Widgets = [.. widgets.Select(w => Describe(w, connections))],
        };

        return (JsonSerializer.Serialize(share, Json), $"labbytwo-tab-{Slugify(tab.Name)}.json");
    }

    public async Task<(string Json, string FileName)> ExportWidgetAsync(string widgetId, CancellationToken ct = default)
    {
        var widgets = await config.WidgetsAsync(ct);
        var widget = widgets.FirstOrDefault(w => w.Id == widgetId)
            ?? throw new InvalidOperationException("That card no longer exists.");

        var connections = await config.ConnectionsAsync(ct);

        var share = new Share
        {
            Kind = WidgetKind,
            ExportedAt = DateTimeOffset.Now.ToString("O"),
            From = await DashboardNameAsync(ct),
            Widgets = [Describe(widget, connections)],
        };

        var name = widget.Title is { Length: > 0 } title ? title : widget.Type;
        return (JsonSerializer.Serialize(share, Json), $"labbytwo-card-{Slugify(name)}.json");
    }

    private SharedWidget Describe(Widget widget, IReadOnlyList<Connection> connections)
    {
        // A section's settings are the page it shows, weather connections and all, so its
        // fields are that page's rather than the section type's own empty list.
        var fields = PageBlocks.FieldsFor(registry, widget.Type, widget.Settings);
        var (settings, refs) = Split(widget.Settings, fields, connections);

        return new SharedWidget(
            widget.Type, widget.Title, widget.Sort, widget.Width, settings, refs,
            Reference(widget.ConnectionId, connections), widget.Height);
    }

    /// <summary>
    /// Pulls the connection ids out of a settings bag and turns them into references.
    ///
    /// A tab kind or widget can hold a connection in its *settings* as well as in its
    /// binding — the Git page does, the weather page does it four times over. Exporting
    /// those as raw ids would produce a file that looks fine and points at nothing.
    /// </summary>
    private static (Dictionary<string, string> Settings, Dictionary<string, ConnectionRef> Refs) Split(
        SettingsBag bag, IReadOnlyList<FieldSpec> fields, IReadOnlyList<Connection> connections)
    {
        var settings = new Dictionary<string, string>(bag);
        var refs = new Dictionary<string, ConnectionRef>();

        foreach (var field in fields.Where(f => f.Kind == FieldKind.Connection))
        {
            if (!settings.TryGetValue(field.Key, out var id) || id.Length == 0)
                continue;

            settings.Remove(field.Key);
            if (Reference(id, connections) is { } reference)
                refs[field.Key] = reference;
        }

        return (settings, refs);
    }

    private static ConnectionRef? Reference(string? id, IReadOnlyList<Connection> connections) =>
        id is { Length: > 0 } && connections.FirstOrDefault(c => c.Id == id) is { } found
            ? new ConnectionRef(found.Provider, found.Name)
            : null;

    // ---- copying one here ------------------------------------------------------------

    /// <summary>
    /// Another one of these. Sharing already had to answer every hard question a copy
    /// asks — new ids, a slug that does not collide, connections resolved by what they
    /// are — so duplicating is the same journey with both ends in the same install
    /// rather than a second implementation that will drift from it.
    /// </summary>
    public async Task<Result> DuplicateTabAsync(string tabId, CancellationToken ct = default)
    {
        var (json, _) = await ExportTabAsync(tabId, ct);
        var share = Read(json);

        // Named as a copy up front. Coming back to two tabs called "Media" and having to
        // open both to find out which is which is a poor reward for pressing a button.
        var copy = share with { Tab = share.Tab! with { Name = Copy(share.Tab.Name) } };

        return await ApplyAsync(await PlanAsync(copy, ct), ct);
    }

    public async Task<Result> DuplicateWidgetAsync(string widgetId, CancellationToken ct = default)
    {
        var (json, _) = await ExportWidgetAsync(widgetId, ct);
        var share = Read(json);

        // A card lands on the first dashboard, which is wrong for a duplicate: it belongs
        // beside the one it was copied from.
        var widgets = await config.WidgetsAsync(ct);
        var original = widgets.First(w => w.Id == widgetId);

        var plan = await PlanAsync(share, ct);
        return await ApplyAsync(plan, ct, original.TabId);
    }

    private static string Copy(string name) => $"{name} (copy)";

    // ---- reading one back ------------------------------------------------------------

    /// <summary>What an import would do, worked out without writing anything.</summary>
    public sealed record Plan(
        Share Share,
        string Summary,
        string? Slug,
        List<string> Notes)
    {
        public bool IsTab => Share.HoldsTab;
    }

    /// <summary>The file as text, in the same shape <see cref="Read"/> takes back.</summary>
    public static string Write(Share share) => JsonSerializer.Serialize(share, Json);

    public static Share Read(string json)
    {
        Share share;
        try
        {
            share = JsonSerializer.Deserialize<Share>(json, Json)
                ?? throw new InvalidOperationException("The file was empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"That is not a LabbyTwo tab or card file: {ex.Message}");
        }

        if (share.Version > CurrentVersion)
            throw new InvalidOperationException(
                $"That file is version {share.Version} and this LabbyTwo understands up to {CurrentVersion}. " +
                "Update LabbyTwo and try again.");

        if (share.HoldsTab && share.Tab is null)
            throw new InvalidOperationException("That file says it holds a tab but does not contain one.");

        if (!share.HoldsTab && share.Kind != WidgetKind)
            throw new InvalidOperationException($"That file holds a “{share.Kind}”, which is not something LabbyTwo can import.");

        if (share.Kind == WidgetKind && share.Widgets.Count == 0)
            throw new InvalidOperationException("That file says it holds a card but does not contain one.");

        return share;
    }

    /// <summary>
    /// Everything that will happen, before any of it does. An import that adds a tab full
    /// of cards pointed at nothing is worth knowing about while it is still a decision.
    /// </summary>
    public async Task<Plan> PlanAsync(Share share, CancellationToken ct = default)
    {
        var notes = new List<string>();
        var connections = await config.ConnectionsAsync(ct);

        string? slug = null;
        if (share.HoldsTab && share.Tab is { } tab)
        {
            slug = await config.UniqueSlugAsync(tab.Slug, null, ct);
            if (!string.Equals(slug, tab.Slug, StringComparison.OrdinalIgnoreCase))
                notes.Add($"It will be at /t/{slug} — /t/{tab.Slug} is already taken.");

            if (registry.TabKind(tab.Kind) is null)
                notes.Add($"This tab is a “{tab.Kind}” page, and nothing installed here can render one. " +
                          "It will import, and stay blank until you install whatever provides it.");

            foreach (var (key, reference) in tab.Connections)
                Note(notes, connections, reference, $"The page's “{key}” setting");
        }

        var missingTypes = new HashSet<string>();
        foreach (var widget in share.Widgets)
        {
            if (registry.WidgetType(widget.Type) is null && missingTypes.Add(widget.Type))
                notes.Add($"No card type “{widget.Type}” is installed, so that one will show as unknown.");

            if (widget.BoundTo is { } bound)
                Note(notes, connections, bound, $"The “{Name(widget)}” card");

            foreach (var (key, reference) in widget.Connections)
                Note(notes, connections, reference, $"The “{Name(widget)}” card's “{key}” setting");
        }

        var summary = share.HoldsTab
            ? $"Adds the “{share.Tab!.Name}” tab and {Count(share.Widgets.Count, "card")}."
            : $"Adds the “{Name(share.Widgets[0])}” card.";

        return new Plan(share, summary, slug, notes);
    }

    private void Note(List<string> notes, IReadOnlyList<Connection> connections, ConnectionRef reference, string what)
    {
        switch (Match(connections, reference))
        {
            case (null, var why):
                notes.Add($"{what} wanted {Describe(reference)}{why} You can point it at something after importing.");
                break;

            case ({ } found, { Length: > 0 } note) when found.Name != reference.Name:
                notes.Add($"{what} will use “{found.Name}”{note}");
                break;
        }
    }

    private string Describe(ConnectionRef reference) =>
        registry.Provider(reference.Provider) is { } provider
            ? $"a {provider.DisplayName} called “{reference.Name}”, and there is no match here."
            : $"“{reference.Name}”, which needs the “{reference.Provider}” provider — not installed here.";

    /// <summary>
    /// Finding the local equivalent of somebody else's connection. By name first, because
    /// two people who both call it "NAS" almost certainly mean their own; then by provider
    /// when there is only one candidate, because there is nothing else it could be.
    /// Never by guessing between several.
    /// </summary>
    private static (Connection? Found, string Why) Match(IReadOnlyList<Connection> connections, ConnectionRef reference)
    {
        var match = Resolve(connections, reference);
        return match.How switch
        {
            MatchKind.Exact => (match.Found, ""),
            MatchKind.OnlyOne => (match.Found, $", the only one you have — the file asked for “{reference.Name}”."),
            MatchKind.Several => (null, $" There are {match.Candidates.Count} to choose from, so none was picked."),
            _ => (null, ""),
        };
    }

    /// <summary>How a reference found its local equivalent, if it did.</summary>
    public enum MatchKind
    {
        /// <summary>Same provider, same name.</summary>
        Exact,

        /// <summary>A different name, but the only connection of that provider there is.</summary>
        OnlyOne,

        /// <summary>No name matched and several of that provider exist, so none was picked.</summary>
        Several,

        /// <summary>Nothing of that provider exists here.</summary>
        None,
    }

    /// <param name="Found">What the rule picked, or null when it would not.</param>
    /// <param name="How">Which part of the rule decided.</param>
    /// <param name="Candidates">Every connection of the reference's provider, for somebody choosing by hand.</param>
    public sealed record ConnectionMatch(Connection? Found, MatchKind How, IReadOnlyList<Connection> Candidates);

    /// <summary>
    /// <see cref="Match"/>'s rule with its working shown, for a screen that lets somebody
    /// overrule it: the candidates are what a dropdown offers when the rule would not pick.
    /// </summary>
    public static ConnectionMatch Resolve(IReadOnlyList<Connection> connections, ConnectionRef reference)
    {
        var sameProvider = connections
            .Where(c => string.Equals(c.Provider, reference.Provider, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (sameProvider.FirstOrDefault(c => string.Equals(c.Name, reference.Name, StringComparison.OrdinalIgnoreCase))
            is { } byName)
            return new ConnectionMatch(byName, MatchKind.Exact, sameProvider);

        return sameProvider.Count switch
        {
            1 => new ConnectionMatch(sameProvider[0], MatchKind.OnlyOne, sameProvider),
            0 => new ConnectionMatch(null, MatchKind.None, sameProvider),
            _ => new ConnectionMatch(null, MatchKind.Several, sameProvider),
        };
    }

    /// <summary>One connection a file needs, with everything in it that needs it.</summary>
    /// <param name="Reference">The connection as the file names it.</param>
    /// <param name="UsedBy">Phrases like "the “NAS” card", for saying what an answer affects.</param>
    /// <param name="Match">What the matching rule makes of it here.</param>
    public sealed record Binding(ConnectionRef Reference, List<string> UsedBy, ConnectionMatch Match);

    /// <summary>
    /// Every distinct connection the file asks for, matched against this install. Asked
    /// once per connection rather than once per card: a page of six cards on one NAS is
    /// one question, not six.
    /// </summary>
    public async Task<List<Binding>> BindingsAsync(Share share, CancellationToken ct = default)
    {
        var connections = await config.ConnectionsAsync(ct);
        var found = new List<Binding>();

        void Add(ConnectionRef reference, string what)
        {
            if (found.FirstOrDefault(b => Same(b.Reference, reference)) is { } existing)
            {
                if (!existing.UsedBy.Contains(what))
                    existing.UsedBy.Add(what);
                return;
            }
            found.Add(new Binding(reference, [what], Resolve(connections, reference)));
        }

        if (share.Tab is { } tab)
        {
            foreach (var (key, reference) in tab.Connections)
                Add(reference, $"the page's “{key}” setting");
        }

        foreach (var widget in share.Widgets.OrderBy(w => w.Sort))
        {
            if (widget.BoundTo is { } bound)
                Add(bound, $"the “{Name(widget)}” card");
            foreach (var (key, reference) in widget.Connections)
                Add(reference, $"the “{Name(widget)}” card's “{key}” setting");
        }

        return found;
    }

    /// <summary>The same connection by the matching rule's lights, which ignore case.</summary>
    public static bool Same(ConnectionRef a, ConnectionRef b) =>
        string.Equals(a.Provider, b.Provider, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

    // ---- writing it in ---------------------------------------------------------------

    public sealed record Result(string? TabSlug, int Widgets, List<string> Notes);

    /// <summary>
    /// New ids throughout — see the note at the top of the class. A shared tab is a copy,
    /// and importing the same file twice gives you two of them rather than silently
    /// overwriting the first.
    /// </summary>
    /// <param name="ontoTab">
    /// Where a lone card should go. Null means the first dashboard, which is the only
    /// sensible guess for a card arriving from somebody else; a duplicate passes the tab
    /// its original is on, because next to it is the whole point.
    /// </param>
    /// <param name="choices">
    /// Answers somebody gave by hand, which win over the matching rule: a connection id, or
    /// empty for "leave it unbound". A reference with no answer here is matched as usual.
    /// </param>
    public async Task<Result> ApplyAsync(
        Plan plan, CancellationToken ct = default, string? ontoTab = null,
        IReadOnlyDictionary<ConnectionRef, string>? choices = null)
    {
        var connections = await config.ConnectionsAsync(ct);
        var share = plan.Share;

        Connection? Pick(ConnectionRef reference)
        {
            foreach (var (asked, answer) in choices ?? new Dictionary<ConnectionRef, string>())
            {
                if (Same(asked, reference))
                    return connections.FirstOrDefault(c => c.Id == answer);
            }
            return Match(connections, reference).Found;
        }

        string tabId;
        string? slug = null;

        if (share.HoldsTab && share.Tab is { } shared)
        {
            var tabs = await config.TabsAsync(ct);
            slug = await config.UniqueSlugAsync(shared.Slug, null, ct);
            tabId = Ids.New();

            await config.SaveTabAsync(new Tab
            {
                Id = tabId,
                Slug = slug,
                Name = shared.Name,
                Icon = shared.Icon,
                Kind = shared.Kind,
                Sort = tabs.Count == 0 ? 0 : tabs.Max(t => t.Sort) + 1,
                Enabled = true,
                Settings = Rebuild(shared.Settings, shared.Connections, Pick),
            }, ct);
        }
        else
        {
            // A lone card needs somewhere to live. The first dashboard tab is the only
            // sensible answer, and saying so beats refusing the import.
            var tabs = await config.TabsAsync(ct);

            // A section, heading or divider is a piece of a custom page, so it goes to one
            // when there is one. A dashboard can draw it, but it is not what it was made for.
            var page = share.Widgets.Any(w => PageBlocks.IsBlock(w.Type))
                ? tabs.FirstOrDefault(t => t.Kind == Core.TabKinds.Custom)
                : null;

            var home = ontoTab is { Length: > 0 }
                ? tabs.FirstOrDefault(t => t.Id == ontoTab)
                : page ?? tabs.FirstOrDefault(t => t.Kind == Core.TabKinds.Grid);

            home ??= tabs.FirstOrDefault(t => t.Kind == Core.TabKinds.Grid)
                ?? throw new InvalidOperationException(
                    "There is no dashboard tab to put this card on. Add one first, then import it.");

            tabId = home.Id;

            // Only worth saying when it was a choice. Duplicating onto the tab you are
            // looking at needs no explanation.
            if (ontoTab is not { Length: > 0 })
                plan.Notes.Add($"Added to the “{home.Name}” tab.");
        }

        var sort = await config.NextWidgetSortAsync(tabId, ct);
        var written = 0;

        foreach (var widget in share.Widgets.OrderBy(w => w.Sort))
        {
            await config.SaveWidgetAsync(new Widget
            {
                Id = Ids.New(),
                TabId = tabId,
                Type = widget.Type,
                Title = widget.Title,
                ConnectionId = widget.BoundTo is { } bound ? Pick(bound)?.Id : null,
                Sort = sort++,
                Width = widget.Width,
                Height = PageBlocks.ClampRows(widget.Height),
                Settings = Rebuild(widget.Settings, widget.Connections, Pick),
            }, ct);
            written++;
        }

        return new Result(slug, written, plan.Notes);
    }

    /// <summary>Puts the matched connection ids back where the exporter took them from.</summary>
    private static SettingsBag Rebuild(
        Dictionary<string, string> settings,
        Dictionary<string, ConnectionRef> refs,
        Func<ConnectionRef, Connection?> pick)
    {
        var bag = new SettingsBag(settings);
        foreach (var (key, reference) in refs)
        {
            if (pick(reference) is { } found)
                bag[key] = found.Id;
        }
        return bag;
    }

    // ---- odds and ends ----------------------------------------------------------------

    /// <summary>
    /// What the dashboard this came from is called, so a file found in a downloads folder
    /// six months later says where it is from. Never fails the export over it.
    /// </summary>
    private async Task<string?> DashboardNameAsync(CancellationToken ct)
    {
        try
        {
            var name = await settings.GetAsync(Appearance.BrandKey, "", ct);
            return name.Length > 0 ? name : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Name(SharedWidget widget) =>
        widget.Title is { Length: > 0 } title ? title : widget.Type;

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    /// <summary>A filename somebody can find again, out of a name somebody typed.</summary>
    public static string Slugify(string name)
    {
        var slug = new string([.. name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')]);
        slug = string.Join('-', slug.Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length > 0 ? slug[..Math.Min(slug.Length, 40)] : "export";
    }
}
