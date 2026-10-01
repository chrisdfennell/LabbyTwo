using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// Every note's title and page, and the <c>[[link]]</c> index, as one <see cref="NoteGraph"/>
/// held in memory — so a link drawn on a page is a dictionary lookup, and "Linked from"
/// under a note is a list already made, rather than either being a query per render.
///
/// Read once, on the thread pool through <see cref="Offload"/>, the first time anything
/// asks; dropped whenever a note is written (<see cref="NotesStore.Changed"/>) or a page is
/// renamed, moved or deleted (<see cref="ConfigStore.Changed"/>), and read again by
/// whoever asks next. Components never wait on it: they draw what <see cref="Current"/>
/// holds, start <see cref="GetAsync"/> when it holds nothing, and redraw on <see cref="Changed"/>.
///
/// Each read also writes back what it learned (<see cref="NoteGraph.Corrections"/>): which
/// note each link resolves to now. That is what lets a link survive its note being
/// renamed — the title stops matching, and the id the index remembered still does.
/// </summary>
public sealed class NoteDirectory : IDisposable
{
    private readonly NotesStore _notes;
    private readonly ConfigStore _config;
    private readonly Offload _offload;
    private readonly ILogger<NoteDirectory> _log;
    private readonly VersionedCache<NoteGraph> _cache = new();
    private readonly Lock _gate = new();
    private Task<NoteGraph>? _loading;
    private long _loadingVersion = -1;

    public NoteDirectory(NotesStore notes, ConfigStore config, Offload offload, ILogger<NoteDirectory> log)
    {
        _notes = notes;
        _config = config;
        _offload = offload;
        _log = log;
        _notes.Changed += Invalidate;
        _config.Changed += Invalidate;
    }

    /// <summary>Raised when the notes or pages changed; whoever shows links asks again.</summary>
    public event Action? Changed;

    /// <summary>The graph if it has been read and nothing has changed since; never waits.</summary>
    public NoteGraph? Current => _cache.Value;

    /// <summary>
    /// The graph, read if need be. Everyone asking while a read is under way shares it, so
    /// a page with forty links on it is one read, not forty.
    /// </summary>
    public Task<NoteGraph> GetAsync()
    {
        lock (_gate)
        {
            if (_cache.Value is { } cached)
                return Task.FromResult(cached);
            var version = _cache.Version;
            if (_loading is { } running && _loadingVersion == version && !running.IsFaulted && !running.IsCanceled)
                return running;
            _loadingVersion = version;
            return _loading = _offload.Run(_ => LoadAsync(version));
        }
    }

    private async Task<NoteGraph> LoadAsync(long version)
    {
        var indexed = await _notes.IndexMissingAsync();
        if (indexed > 0)
            _log.LogInformation("Indexed the links in {Count} notes written before notes could link to each other", indexed);

        var (headings, links) = await _notes.DirectoryAsync();
        var places = Places(await _config.TabsAsync(), await _config.WidgetsAsync());
        var byContainer = places.ToDictionary(p => p.ContainerId, StringComparer.Ordinal);
        var entries = headings
            .Where(h => byContainer.ContainsKey(h.TabId))
            .Select(h => new NoteEntry(h.Id, h.Title, byContainer[h.TabId]));
        var graph = new NoteGraph(places, entries, links);

        if (graph.Corrections.Count > 0 || graph.Orphans.Count > 0)
        {
            try
            {
                await _notes.RememberTargetsAsync(graph.Corrections, graph.Orphans);
            }
            catch (Exception ex)
            {
                // Only the fallback for a rename is lost, and the next read tries again.
                _log.LogWarning(ex, "Could not write down which notes the links point at");
            }
        }
        return _cache.Store(graph, version);
    }

    /// <summary>
    /// Where notes live: every notes tab, in nav order, then every notes section on a
    /// custom page, named by its page and its own title.
    /// </summary>
    public static IReadOnlyList<NotePlace> Places(IReadOnlyList<Tab> tabs, IReadOnlyList<Widget> widgets)
    {
        var places = tabs
            .Where(t => t.Kind == TabKinds.Notes)
            .OrderBy(t => t.Sort)
            .Select(t => new NotePlace(t.Id, t.Name, null, $"t/{Uri.EscapeDataString(t.Slug)}"))
            .ToList();
        var pages = tabs.ToDictionary(t => t.Id, StringComparer.Ordinal);
        foreach (var widget in widgets.Where(w => w.Type == PageBlocks.Section && PageBlocks.SectionKind(w.Settings) == TabKinds.Notes))
        {
            if (!pages.TryGetValue(widget.TabId, out var page))
                continue;
            places.Add(new NotePlace(widget.Id, page.Name, widget.Title is { Length: > 0 } title ? title : null,
                $"t/{Uri.EscapeDataString(page.Slug)}"));
        }
        return places;
    }

    private void Invalidate()
    {
        _cache.Invalidate();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _notes.Changed -= Invalidate;
        _config.Changed -= Invalidate;
    }
}
