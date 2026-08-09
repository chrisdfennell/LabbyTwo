using System.Text.Json;
using System.Text.Json.Serialization;
using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// Exports and imports the whole configuration as JSON. Ids are preserved so the
/// widget → connection bindings survive the round trip, which is what makes a shared
/// dashboard land intact on somebody else's install.
/// </summary>
public sealed class ConfigTransfer(ConfigStore config, AlertRuleStore rules, Registry registry)
{
    // 2 added alert rules. Version 1 files still import — they simply carry none.
    public const int CurrentVersion = 2;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public sealed record Bundle
    {
        public int Version { get; init; } = CurrentVersion;
        public string? ExportedAt { get; init; }

        /// <summary>False when secrets were stripped, which is the default for anything shared.</summary>
        public bool IncludesSecrets { get; init; }

        public List<ConnectionDto> Connections { get; init; } = [];
        public List<TabDto> Tabs { get; init; } = [];
        public List<WidgetDto> Widgets { get; init; } = [];
        public List<RuleDto> Rules { get; init; } = [];
    }

    public sealed record ConnectionDto(string Id, string Provider, string Name, string Icon,
        bool Enabled, bool Alerts, int Sort, Dictionary<string, string> Settings);

    public sealed record TabDto(string Id, string Slug, string Name, string Icon, string Kind,
        int Sort, bool Enabled, Dictionary<string, string> Settings);

    public sealed record WidgetDto(string Id, string TabId, string Type, string Title,
        string? ConnectionId, int Sort, int Width, Dictionary<string, string> Settings);

    public sealed record RuleDto(string Id, string Name, string? ConnectionId, string Metric,
        string Comparison, double Threshold, double? ClearThreshold, int ForMinutes, bool Enabled);

    public sealed record ImportResult(int Connections, int Tabs, int Widgets, int Rules, List<string> Warnings);

    public async Task<string> ExportAsync(bool includeSecrets, CancellationToken ct = default)
    {
        var connections = await config.ConnectionsAsync(ct);
        var tabs = await config.TabsAsync(ct);
        var widgets = await config.WidgetsAsync(ct);
        var alertRules = await rules.AllAsync(ct);

        var bundle = new Bundle
        {
            ExportedAt = DateTimeOffset.Now.ToString("O"),
            IncludesSecrets = includeSecrets,
            Connections =
            [
                .. connections.Select(c => new ConnectionDto(c.Id, c.Provider, c.Name, c.Icon,
                    c.Enabled, c.AlertsEnabled, c.Sort, Strip(c, includeSecrets)))
            ],
            Tabs =
            [
                .. tabs.Select(t => new TabDto(t.Id, t.Slug, t.Name, t.Icon, t.Kind, t.Sort, t.Enabled,
                    new Dictionary<string, string>(t.Settings)))
            ],
            Widgets =
            [
                .. widgets.Select(w => new WidgetDto(w.Id, w.TabId, w.Type, w.Title, w.ConnectionId,
                    w.Sort, w.Width, new Dictionary<string, string>(w.Settings)))
            ],
            Rules =
            [
                .. alertRules.Select(r => new RuleDto(r.Id, r.Name, r.ConnectionId, r.Metric,
                    r.Comparison.ToString(), r.Threshold, r.ClearThreshold, r.ForMinutes, r.Enabled))
            ],
        };

        return JsonSerializer.Serialize(bundle, Json);
    }

    /// <summary>
    /// Removes every field the provider declared as a password unless the caller explicitly
    /// asked for them, so the default export is safe to paste into a forum post.
    /// </summary>
    private Dictionary<string, string> Strip(Connection connection, bool includeSecrets)
    {
        var settings = new Dictionary<string, string>(connection.Settings);
        if (includeSecrets)
            return settings;

        var provider = registry.Provider(connection.Provider);
        foreach (var field in provider?.Fields.Where(f => f.IsSecret) ?? [])
            settings.Remove(field.Key);
        return settings;
    }

    /// <summary>
    /// Upserts everything in the bundle by id. Existing rows with the same id are
    /// overwritten; anything not mentioned is left alone, so importing a dashboard adds to
    /// an install rather than wiping it.
    /// </summary>
    public async Task<ImportResult> ImportAsync(string json, CancellationToken ct = default)
    {
        Bundle bundle;
        try
        {
            bundle = JsonSerializer.Deserialize<Bundle>(json, Json)
                     ?? throw new InvalidOperationException("The file was empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"That is not a valid LabbyTwo export: {ex.Message}");
        }

        if (bundle.Version > CurrentVersion)
            throw new InvalidOperationException(
                $"This export is version {bundle.Version} and this LabbyTwo understands up to {CurrentVersion}.");

        var warnings = new List<string>();

        foreach (var dto in bundle.Connections)
        {
            if (registry.Provider(dto.Provider) is null)
            {
                warnings.Add($"Skipped “{dto.Name}” — no provider named “{dto.Provider}” is installed.");
                continue;
            }
            await config.SaveConnectionAsync(new Connection
            {
                Id = dto.Id,
                Provider = dto.Provider,
                Name = dto.Name,
                Icon = dto.Icon,
                Enabled = dto.Enabled,
                AlertsEnabled = dto.Alerts,
                Sort = dto.Sort,
                Settings = new SettingsBag(dto.Settings),
            }, ct);

            if (!bundle.IncludesSecrets && registry.Provider(dto.Provider)!.Fields.Any(f => f.IsSecret))
                warnings.Add($"“{dto.Name}” needs its credentials entered — the export did not carry them.");
        }

        // Slugs are unique in the schema, so a collision with an existing tab has to be
        // resolved before the insert rather than blowing up halfway through.
        var existingTabs = await config.TabsAsync(ct);
        foreach (var dto in bundle.Tabs)
        {
            var slug = dto.Slug;
            if (existingTabs.Any(t => t.Id != dto.Id && string.Equals(t.Slug, slug, StringComparison.OrdinalIgnoreCase)))
            {
                slug = await config.UniqueSlugAsync(dto.Slug, dto.Id, ct);
                warnings.Add($"Tab “{dto.Name}” was imported as /t/{slug} — /t/{dto.Slug} was taken.");
            }

            if (registry.TabKind(dto.Kind) is null)
                warnings.Add($"Tab “{dto.Name}” uses an unknown kind “{dto.Kind}” and will not render.");

            await config.SaveTabAsync(new Tab
            {
                Id = dto.Id,
                Slug = slug,
                Name = dto.Name,
                Icon = dto.Icon,
                Kind = dto.Kind,
                Sort = dto.Sort,
                Enabled = dto.Enabled,
                Settings = new SettingsBag(dto.Settings),
            }, ct);
        }

        var tabIds = (await config.TabsAsync(ct)).Select(t => t.Id).ToHashSet();
        var imported = 0;
        foreach (var dto in bundle.Widgets)
        {
            if (!tabIds.Contains(dto.TabId))
            {
                warnings.Add($"Skipped a {dto.Type} widget — its tab was not in the export.");
                continue;
            }
            if (registry.WidgetType(dto.Type) is null)
                warnings.Add($"Widget type “{dto.Type}” is not installed; its card will show as unknown.");

            await config.SaveWidgetAsync(new Widget
            {
                Id = dto.Id,
                TabId = dto.TabId,
                Type = dto.Type,
                Title = dto.Title,
                ConnectionId = dto.ConnectionId,
                Sort = dto.Sort,
                Width = dto.Width,
                Settings = new SettingsBag(dto.Settings),
            }, ct);
            imported++;
        }

        // Rules last: one pinned to a connection needs that connection to exist, and a
        // rule for something that was not in the bundle is reported rather than orphaned.
        var connectionIds = (await config.ConnectionsAsync(ct)).Select(c => c.Id).ToHashSet();
        var importedRules = 0;
        foreach (var dto in bundle.Rules)
        {
            if (dto.ConnectionId is { } target && !connectionIds.Contains(target))
            {
                warnings.Add($"Skipped the alert rule for “{dto.Metric}” — the connection it watches was not in the export.");
                continue;
            }

            await rules.SaveAsync(new AlertRule
            {
                Id = dto.Id,
                Name = dto.Name,
                ConnectionId = dto.ConnectionId,
                Metric = dto.Metric,
                Comparison = string.Equals(dto.Comparison, nameof(Core.Comparison.Below), StringComparison.OrdinalIgnoreCase)
                    ? Core.Comparison.Below
                    : Core.Comparison.Above,
                Threshold = dto.Threshold,
                ClearThreshold = dto.ClearThreshold,
                ForMinutes = dto.ForMinutes,
                Enabled = dto.Enabled,
            }, ct);
            importedRules++;
        }

        return new ImportResult(bundle.Connections.Count, bundle.Tabs.Count, imported, importedRules, warnings);
    }
}
