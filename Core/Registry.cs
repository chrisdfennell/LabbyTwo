namespace LabbyTwo.Core;

/// <summary>
/// The single place the app asks "what can I add?". Everything registered in DI —
/// whether it was compiled into the app or loaded from a plugin DLL — is discoverable
/// here, so the UI never carries a hardcoded list of integrations.
/// </summary>
public sealed class Registry(
    IEnumerable<IConnectionProvider> providers,
    IEnumerable<IWidgetType> widgets,
    IEnumerable<ITabKind> tabKinds,
    ModuleCatalog? catalog = null)
{
    private readonly Dictionary<string, IConnectionProvider> _providers =
        Deduplicate(providers, p => p.Type, p => p.Fields, catalog);

    private readonly Dictionary<string, IWidgetType> _widgets =
        Deduplicate(widgets, w => w.Type, w => w.Fields, catalog);

    private readonly Dictionary<string, ITabKind> _tabKinds =
        Deduplicate(tabKinds, k => k.Kind, k => k.Fields, catalog);

    /// <summary>
    /// Last registration wins on a key collision. A plugin is registered after the
    /// built-ins, so shipping a "qnap" provider of your own replaces the bundled one
    /// rather than crashing the app at startup with a duplicate-key exception.
    ///
    /// Each extension is also asked to describe itself once, here, where the answer can be
    /// thrown away. Anything that cannot manage that is left out and reported on the
    /// Settings page — because those properties are read on the path that loads every
    /// connection, so one plugin throwing there used to take down every page in the app,
    /// not merely its own. A plugin's mistakes should cost you the plugin.
    /// </summary>
    private static Dictionary<string, T> Deduplicate<T>(
        IEnumerable<T> items,
        Func<T, string> key,
        Func<T, IReadOnlyList<FieldSpec>> describe,
        ModuleCatalog? catalog)
    {
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            string id;
            try
            {
                id = key(item);

                // Touch the settings it claims to have. This is where a half-updated
                // plugin — built against a different LabbyTwo — actually fails.
                _ = describe(item).Count;
            }
            catch (Exception ex)
            {
                var type = item?.GetType();
                catalog?.Failures.Add(new ModuleFailure(
                    type?.Assembly.Location is { Length: > 0 } path ? path : type?.FullName ?? "unknown",
                    $"{type?.Name} could not describe itself, so it was left out: {ex.GetBaseException().Message}. " +
                    "Rebuild the plugin against this version of LabbyTwo."));
                continue;
            }

            result[id] = item;
        }

        return result;
    }

    public IReadOnlyList<IConnectionProvider> Providers =>
        [.. _providers.Values.OrderBy(p => p.Category).ThenBy(p => p.DisplayName)];

    public IReadOnlyList<IWidgetType> Widgets =>
        [.. _widgets.Values.OrderBy(w => w.DisplayName)];

    public IReadOnlyList<ITabKind> TabKinds =>
        [.. _tabKinds.Values.OrderBy(k => k.DisplayName)];

    public IConnectionProvider? Provider(string? type) =>
        type is not null && _providers.TryGetValue(type, out var provider) ? provider : null;

    public IWidgetType? WidgetType(string? type) =>
        type is not null && _widgets.TryGetValue(type, out var widget) ? widget : null;

    public ITabKind? TabKind(string? kind) =>
        kind is not null && _tabKinds.TryGetValue(kind, out var tabKind) ? tabKind : null;

    /// <summary>Widgets that can bind to a given provider, plus the connection-free ones.</summary>
    public IReadOnlyList<IWidgetType> WidgetsFor(string providerType) =>
        [.. Widgets.Where(w => !w.NeedsConnection || Accepts(w, providerType))];

    /// <summary>
    /// A widget declaring "*" works with any probed connection (a tile, a chart); one
    /// naming providers works only with those.
    /// </summary>
    public static bool Accepts(IWidgetType widget, string providerType) =>
        widget.ProviderTypes.Contains("*") ||
        widget.ProviderTypes.Contains(providerType, StringComparer.OrdinalIgnoreCase);

    // ---------- Metrics ----------

    /// <summary>
    /// How to label and format one metric on one connection. Asks the provider first,
    /// falls back to the well-known names, and finally humanises the key — so a widget
    /// never has to know which provider it is looking at, and a provider nobody has
    /// written yet still renders sensibly.
    /// </summary>
    public MetricSpec Metric(Connection? connection, string key)
    {
        if (connection is not null && Provider(connection.Provider) is { } provider)
        {
            var declared = provider.MetricsFor(connection)
                .FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));
            if (declared is not null)
                return declared;
        }
        return MetricSpec.Fallback(key);
    }

    /// <summary>
    /// Everything a connection is expected to report, for the metric dropdown in the
    /// widget editor. Merged with what history has actually seen by the caller, because
    /// a provider can report more than it declares.
    /// </summary>
    public IReadOnlyList<MetricSpec> MetricsFor(Connection? connection)
    {
        if (connection is null || Provider(connection.Provider) is not { } provider)
            return [];
        return provider.MetricsFor(connection);
    }
}
