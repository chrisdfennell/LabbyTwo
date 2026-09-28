using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// Saving a tab to start new tabs from, and starting one.
///
/// Nothing here is a new format or a new copying routine. A template is a shared-tab file
/// kept in the database: capturing one is <see cref="ShareTransfer.ExportTabAsync"/>, and
/// starting a tab from one is the same plan-and-apply an imported tab goes through. That
/// is what keeps the promises sharing already makes — no ids, no credentials, connections
/// matched by what they are — true of templates without saying them twice. It also means
/// cards are captured whole and generically: type, title, width, order and every setting,
/// including layout a tab kind keeps in a card's settings that this class knows nothing of.
/// </summary>
public sealed class TabTemplates(TemplateStore store, ShareTransfer share, ConfigStore config, Registry registry)
{
    /// <summary>Takes a copy of a tab as it is now. Later edits to the tab do not reach it.</summary>
    public async Task<TabTemplate> SaveFromTabAsync(
        string tabId, string name, string icon, string description, CancellationToken ct = default)
    {
        var (json, _) = await share.ExportTabAsync(tabId, ct);

        var template = new TabTemplate
        {
            Name = Clean(name, "Untitled template"),
            Icon = icon.Trim(),
            Description = description.Trim(),
            Content = json,
        };
        await store.SaveAsync(template, ct);
        return template;
    }

    public async Task RenameAsync(string id, string name, string icon, string description, CancellationToken ct = default)
    {
        var template = await store.GetAsync(id, ct)
            ?? throw new InvalidOperationException("That template no longer exists.");

        await store.SaveAsync(template with
        {
            Name = Clean(name, template.Name),
            Icon = icon.Trim(),
            Description = description.Trim(),
        }, ct);
    }

    public Task DeleteAsync(string id, CancellationToken ct = default) => store.DeleteAsync(id, ct);

    /// <summary>What a template holds, read back. Throws for a row whose content is not a tab.</summary>
    public static ShareTransfer.Share Contents(TabTemplate template)
    {
        var contents = ShareTransfer.Read(template.Content);
        if (!contents.HoldsTab)
            throw new InvalidOperationException($"The “{template.Name}” template does not hold a tab.");
        return contents;
    }

    // ---- files ------------------------------------------------------------------------

    /// <summary>
    /// A template as a file to hand somebody: the shared-tab file it already is, marked as a
    /// template and carrying its name and description so it arrives as one.
    /// </summary>
    public async Task<(string Json, string FileName)> ExportAsync(string id, CancellationToken ct = default)
    {
        var template = await store.GetAsync(id, ct)
            ?? throw new InvalidOperationException("That template no longer exists.");

        var file = Contents(template) with
        {
            Kind = ShareTransfer.TemplateKind,
            ExportedAt = DateTimeOffset.Now.ToString("O"),
            Template = new ShareTransfer.TemplateInfo(template.Name, template.Icon, template.Description),
        };

        return (ShareTransfer.Write(file), $"labbytwo-template-{ShareTransfer.Slugify(template.Name)}.json");
    }

    /// <summary>
    /// Adds a template from a file. A template file keeps its name; a plain shared-tab file
    /// is accepted too and named after its tab, since it holds exactly the same thing and
    /// refusing it would only send somebody off to import it as a tab and save that.
    /// </summary>
    public async Task<TabTemplate> ImportAsync(string json, CancellationToken ct = default)
    {
        var file = ShareTransfer.Read(json);
        if (!file.HoldsTab || file.Tab is null)
            throw new InvalidOperationException("That file holds a single card, not a tab, so it cannot be a template.");

        var info = file.Template ?? new ShareTransfer.TemplateInfo(file.Tab.Name, file.Tab.Icon, "");

        // Stored as a tab file, the same as one saved here. The template's own name lives in
        // the row, and two copies of it that could disagree would be one too many.
        var content = file with { Kind = ShareTransfer.TabKind, Template = null };

        var template = new TabTemplate
        {
            Name = Clean(info.Name, "Imported template"),
            Icon = info.Icon,
            Description = info.Description,
            Content = ShareTransfer.Write(content),
        };
        await store.SaveAsync(template, ct);
        return template;
    }

    // ---- starting a tab from one --------------------------------------------------------

    /// <summary>
    /// Everything the new-tab screen needs to decide whether to ask anything first.
    /// </summary>
    /// <param name="Bindings">Each connection the template's cards want, and what matched.</param>
    /// <param name="Warnings">Things that will not work here whatever is chosen, said up front.</param>
    public sealed record Preparation(
        TabTemplate Template, ShareTransfer.Share Contents,
        List<ShareTransfer.Binding> Bindings, List<string> Warnings)
    {
        /// <summary>
        /// Whether to stop and ask. Only an exact match — same kind, same name — goes
        /// through silently; a guess, a choice or a gap is shown to the person first.
        /// </summary>
        public bool NeedsReview =>
            Warnings.Count > 0 || Bindings.Any(b => b.Match.How != ShareTransfer.MatchKind.Exact);
    }

    public async Task<Preparation> PrepareAsync(string id, CancellationToken ct = default)
    {
        var template = await store.GetAsync(id, ct)
            ?? throw new InvalidOperationException("That template no longer exists.");
        var contents = Contents(template);

        var warnings = new List<string>();
        if (registry.TabKind(contents.Tab!.Kind) is null)
            warnings.Add($"It is a “{contents.Tab.Kind}” page, and nothing installed here can show one. " +
                         "The tab will be made, and stay blank until whatever provides it is installed.");

        foreach (var type in contents.Widgets.Select(w => w.Type).Distinct())
        {
            if (registry.WidgetType(type) is null)
                warnings.Add($"No card type “{type}” is installed, so that card will show as unknown.");
        }

        return new Preparation(template, contents, await share.BindingsAsync(contents, ct), warnings);
    }

    /// <summary>
    /// Makes the tab and its cards. The slug comes from the name and is made unique, and the
    /// tab goes at the end of the nav.
    /// </summary>
    /// <param name="choices">
    /// Connections picked by hand, keyed by what the template asked for; an empty value
    /// leaves those cards unbound. Anything not answered is matched the usual way.
    /// </param>
    public async Task<ShareTransfer.Result> CreateTabAsync(
        string id, string name, string icon,
        IReadOnlyDictionary<ShareTransfer.ConnectionRef, string>? choices = null,
        CancellationToken ct = default)
    {
        var template = await store.GetAsync(id, ct)
            ?? throw new InvalidOperationException("That template no longer exists.");
        var contents = Contents(template);

        var tabName = Clean(name, template.Name);
        var shaped = contents with
        {
            Kind = ShareTransfer.TabKind,
            Tab = contents.Tab! with
            {
                Name = tabName,
                Icon = icon is { Length: > 0 } ? icon : template.Icon,
                // The slug the template was made from belongs to the tab it was made from;
                // a new tab called something else should be found at its own name.
                Slug = await config.UniqueSlugAsync(tabName, null, ct),
            },
        };

        var plan = await share.PlanAsync(shaped, ct);
        return await share.ApplyAsync(plan, ct, choices: choices);
    }

    private static string Clean(string value, string fallback) =>
        value.Trim() is { Length: > 0 } trimmed ? trimmed : fallback;
}
