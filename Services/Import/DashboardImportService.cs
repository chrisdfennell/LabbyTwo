using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services.Import;

/// <summary>
/// Picks the right importer for a file and writes what it produced. The split matters:
/// importers are pure and testable, and everything that touches the database — id
/// allocation, slug collisions, connection references — happens once, here, rather than
/// being reimplemented by each new format.
/// </summary>
public sealed class DashboardImportService(
    IEnumerable<IDashboardImporter> importers,
    ConfigStore config,
    Registry registry)
{
    public IReadOnlyList<IDashboardImporter> Importers => [.. importers.OrderBy(i => i.DisplayName)];

    public IDashboardImporter? ByKey(string? key) =>
        key is null ? null : Importers.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads a file without writing anything, so the user sees what would happen before
    /// committing. <paramref name="key"/> is null to auto-detect.
    /// </summary>
    public ImportPlan Preview(ImportSource source, string? key = null)
    {
        var importer = ByKey(key) ?? Detect(source)
            ?? throw new FormatException(
                $"Nothing recognised “{source.FileName}”. Pick the format by hand, or check it is the file the " +
                "other dashboard actually writes.");

        var plan = importer.Read(source);

        foreach (var connection in plan.Connections.Where(c => registry.Provider(c.Provider) is null).ToList())
        {
            plan.Notes.Add($"“{connection.Name}” wanted a {connection.Provider} provider, which is not installed — skipped.");
            plan.Connections.Remove(connection);
        }

        return plan;
    }

    private IDashboardImporter? Detect(ImportSource source)
    {
        foreach (var importer in Importers)
        {
            try
            {
                if (importer.CanHandle(source))
                    return importer;
            }
            catch
            {
                // A detector is a guess. One that throws on a file it does not understand
                // must not stop the others being asked.
            }
        }
        return null;
    }

    public sealed record Result(int Connections, int Tabs, int Widgets, string? FirstSlug, List<string> Notes);

    /// <summary>
    /// Writes a plan. Everything is created new — imports add to an install, they never
    /// overwrite, so running one twice gives two copies rather than a surprise.
    /// </summary>
    public async Task<Result> ApplyAsync(ImportPlan plan, CancellationToken ct = default)
    {
        var notes = new List<string>(plan.Notes);

        // Connections first: a widget can only be bound once the thing it points at has
        // a real id.
        var connectionIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sort = (await config.ConnectionsAsync(ct)).Count;

        foreach (var imported in plan.Connections)
        {
            var connection = new Connection
            {
                Provider = imported.Provider,
                Name = imported.Name,
                Icon = imported.Icon,
                Sort = sort++,
                Settings = imported.Values,
            };
            await config.SaveConnectionAsync(connection, ct);
            connectionIds[imported.Ref] = connection.Id;
        }

        var tabSort = (await config.TabsAsync(ct)).Count;
        string? firstSlug = null;
        var widgetCount = 0;

        foreach (var importedTab in plan.Tabs)
        {
            var tab = new Tab
            {
                Slug = await config.UniqueSlugAsync(importedTab.Name, ct: ct),
                Name = importedTab.Name,
                Icon = importedTab.Icon,
                Kind = importedTab.Kind,
                Sort = tabSort++,
                Settings = importedTab.Values,
            };
            await config.SaveTabAsync(tab, ct);
            firstSlug ??= tab.Slug;

            var widgetSort = 0;
            foreach (var importedWidget in importedTab.Widgets)
            {
                if (registry.WidgetType(importedWidget.Type) is null)
                {
                    notes.Add($"Skipped a “{importedWidget.Type}” widget — no such widget is installed.");
                    continue;
                }

                string? connectionId = null;
                if (importedWidget.ConnectionRef is { } reference && !connectionIds.TryGetValue(reference, out connectionId))
                {
                    notes.Add($"A widget referenced “{reference}”, which was not imported — it will need binding by hand.");
                    connectionId = null;
                }

                await config.SaveWidgetAsync(new Widget
                {
                    TabId = tab.Id,
                    Type = importedWidget.Type,
                    Title = importedWidget.Title,
                    Width = importedWidget.Width,
                    Sort = widgetSort++,
                    ConnectionId = connectionId,
                    Settings = importedWidget.Values,
                }, ct);
                widgetCount++;
            }
        }

        return new Result(plan.Connections.Count, plan.Tabs.Count, widgetCount, firstSlug, notes);
    }
}
