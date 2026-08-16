using System.Reflection;
using System.Runtime.Loader;

namespace LabbyTwo.Core;

/// <summary>
/// Where one extension came from, so Settings can show a plugin author whether their DLL
/// actually loaded and what it contributed.
/// </summary>
/// <param name="Name">Assembly simple name — "LabbyTwo" for the built-ins.</param>
public sealed record ModuleInfo(
    string Name,
    string Version,
    string? Path,
    bool IsPlugin,
    IReadOnlyList<string> Providers,
    IReadOnlyList<string> Widgets,
    IReadOnlyList<string> TabKinds,
    IReadOnlyList<string> Importers,
    IReadOnlyList<string> Endpoints,
    IReadOnlyList<string> Jobs)
{
    public int TypeCount =>
        Providers.Count + Widgets.Count + TabKinds.Count + Importers.Count + Endpoints.Count + Jobs.Count;
}

/// <summary>A DLL in the plugins folder that could not be loaded, kept so the UI can say why.</summary>
public sealed record ModuleFailure(string Path, string Reason);

/// <summary>What discovery found, injected as a singleton and rendered on the Settings page.</summary>
public sealed class ModuleCatalog
{
    public List<ModuleInfo> Modules { get; } = [];
    public List<ModuleFailure> Failures { get; } = [];

    /// <summary>Absolute path scanned for plugin DLLs, shown in the UI so the folder is findable.</summary>
    public string PluginDirectory { get; init; } = "";

    public IEnumerable<ModuleInfo> Plugins => Modules.Where(m => m.IsPlugin);

    /// <summary>
    /// The running build's own version, so a plugin's can be held up against it. Empty on
    /// a build nobody stamped, which is every local <c>dotnet run</c> — and the reason
    /// nothing here treats a blank as a mismatch.
    /// </summary>
    public string HostVersion { get; init; } = "";

    /// <summary>
    /// Whether a plugin says it was built for a different LabbyTwo than this one.
    ///
    /// This exists because the failure it predicts is so unhelpful on its own. A plugin
    /// compiled against an older host half-loads: discovery keeps the types that still
    /// resolve and drops the rest, so a tab kind can appear in the picker and then throw
    /// when somebody opens it. "Built for v1.1.0, this is v1.3.0" is a sentence someone
    /// can act on; "could not load type" three screens later is not.
    /// </summary>
    public bool BuiltForAnother(ModuleInfo plugin) =>
        plugin.IsPlugin
        && HostVersion.Length > 0
        && plugin.Version.Length > 0
        && !string.Equals(plugin.Version, HostVersion, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the folder was there to scan. False is the single most common reason a
    /// plugin "does nothing": the DLL went next to the compose file, or onto the host,
    /// rather than into the volume the container reads.
    /// </summary>
    public bool PluginDirectoryExists { get; init; }

    /// <summary>How many .dll files were seen, whatever became of them.</summary>
    public int DllsFound { get; set; }
}

/// <summary>
/// Finds and registers extensions. Two sources, one mechanism: the app's own assembly,
/// and any DLL dropped into the plugins folder. In both cases a concrete public class
/// implementing one of the extension interfaces is registered — there is no list to keep
/// in sync, which is the point.
/// </summary>
public static class Modules
{
    /// <summary>The interfaces a module can contribute implementations of.</summary>
    private static readonly Type[] ExtensionPoints =
    [
        typeof(IConnectionProvider),
        typeof(IWidgetType),
        typeof(ITabKind),
        typeof(IDashboardImporter),
        typeof(IEndpointExtension),
        typeof(IBackgroundJob),
    ];

    /// <summary>
    /// Loads every DLL in <paramref name="pluginDirectory"/> and registers the extensions
    /// it and the host assembly declare. Plugins are loaded into the default context so
    /// their reference to LabbyTwo resolves to the assembly already running — a plugin
    /// with its own private copy would produce types that look identical and satisfy no
    /// interface check.
    /// </summary>
    public static ModuleCatalog AddModules(
        this IServiceCollection services,
        Assembly hostAssembly,
        string pluginDirectory,
        ILogger? log = null)
    {
        var catalog = new ModuleCatalog
        {
            HostVersion = Stamp(hostAssembly),
            PluginDirectory = pluginDirectory,
            PluginDirectoryExists = Directory.Exists(pluginDirectory),
        };

        foreach (var assembly in LoadAssemblies(hostAssembly, pluginDirectory, catalog, log))
            Register(services, assembly, assembly != hostAssembly, catalog, log);

        services.AddSingleton(catalog);
        return catalog;
    }

    /// <summary>
    /// What an assembly says it is. The release workflow stamps the app and every plugin
    /// it publishes with the same version, which is what makes the two comparable at all —
    /// the assembly *reference* cannot do this job, because it stays 1.0.0.0 whatever the
    /// build was called.
    ///
    /// The "+sha" suffix a source-linked build appends is dropped: it makes two identical
    /// versions look different, which would turn the mismatch warning into noise.
    /// </summary>
    /// <summary>
    /// The version an assembly says it was built as — "v1.3.6" on anything the release
    /// workflow stamped, and empty on a local build nobody did.
    ///
    /// Public because the plugin updater needs the same answer before discovery has run, and
    /// two pieces of code deciding independently what version this is would eventually
    /// disagree about whether a plugin matches.
    /// </summary>
    public static string Stamp(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? assembly.GetName().Version?.ToString()
            ?? "";

    private static IEnumerable<Assembly> LoadAssemblies(
        Assembly hostAssembly, string pluginDirectory, ModuleCatalog catalog, ILogger? log)
    {
        // The host first, so a plugin registering the same key overrides it rather than
        // being overridden — see Registry's last-wins rule.
        yield return hostAssembly;

        if (!Directory.Exists(pluginDirectory))
        {
            // Said out loud. Silence here is indistinguishable from "your plugin is
            // broken", and the folder not existing is the likelier of the two by far.
            log?.LogInformation(
                "No plugin folder at {Path}, so nothing was scanned. Create it and restart to load plugins.",
                pluginDirectory);
            yield break;
        }

        var found = Directory.EnumerateFiles(pluginDirectory, "*.dll").OrderBy(p => p).ToList();
        catalog.DllsFound = found.Count;

        log?.LogInformation("Scanning {Path}: {Count} DLL(s) found.", pluginDirectory, found.Count);

        foreach (var path in found)
        {
            Assembly? assembly = null;
            try
            {
                // A plugin shipped alongside its own copy of a framework or LabbyTwo
                // assembly must not shadow the running one; LoadFromAssemblyPath on the
                // default context reuses whatever is already loaded by that name.
                assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));
            }
            catch (Exception ex)
            {
                catalog.Failures.Add(new ModuleFailure(path, ex.GetBaseException().Message));
                log?.LogError(ex, "Plugin {Path} could not be loaded", path);
            }

            if (assembly is not null)
                yield return assembly;
        }
    }

    private static void Register(
        IServiceCollection services, Assembly assembly, bool isPlugin, ModuleCatalog catalog, ILogger? log)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // A plugin built against a different LabbyTwo can still have usable types;
            // take what loaded and report the rest rather than dropping the whole DLL.
            types = [.. ex.Types.OfType<Type>()];
            var reason = ex.LoaderExceptions.OfType<Exception>().FirstOrDefault()?.Message ?? ex.Message;

            // Naming the cause rather than only the symptom. This is almost always one
            // thing — a plugin built against a different LabbyTwo — and the loader's own
            // message says "could not load type", which reads like a corrupt file and
            // sends people to rebuild the plugin when the host is what moved.
            var built = Stamp(assembly);
            var mismatch = built.Length > 0 && catalog.HostVersion.Length > 0 && built != catalog.HostVersion
                ? $" It was built for LabbyTwo {built} and this is {catalog.HostVersion}, which is the usual " +
                  "cause: get the copy of this plugin published for this version, or rebuild it against it."
                : " That normally means it was built against a different version of LabbyTwo.";

            catalog.Failures.Add(new ModuleFailure(assembly.Location,
                $"Partly loaded, so some of what it declares is missing — {reason}.{mismatch}"));
            log?.LogWarning(ex, "Some types in {Assembly} could not be loaded", assembly.FullName);
        }

        List<string> providers = [], widgets = [], tabKinds = [], importers = [], endpoints = [], jobs = [];

        foreach (var type in types)
        {
            if (!type.IsClass || type.IsAbstract || !type.IsPublic)
                continue;

            var implemented = ExtensionPoints.Where(point => point.IsAssignableFrom(type)).ToList();
            if (implemented.Count == 0)
                continue;

            // One instance shared between the concrete type and every interface it
            // satisfies, so a provider that caches its last reading (the weather widget
            // relies on this) sees the same object the monitor probed.
            services.AddSingleton(type);

            foreach (var point in implemented)
            {
                services.AddSingleton(point, sp => sp.GetRequiredService(type));

                var names = point == typeof(IConnectionProvider) ? providers
                    : point == typeof(IWidgetType) ? widgets
                    : point == typeof(ITabKind) ? tabKinds
                    : point == typeof(IDashboardImporter) ? importers
                    : point == typeof(IEndpointExtension) ? endpoints
                    : jobs;
                names.Add(type.Name);
            }
        }

        if (providers.Count + widgets.Count + tabKinds.Count + importers.Count
            + endpoints.Count + jobs.Count == 0 && isPlugin)
        {
            catalog.Failures.Add(new ModuleFailure(assembly.Location,
                "Loaded, but declares no providers, widgets, tab kinds, importers, endpoints or jobs."));
            return;
        }

        var name = assembly.GetName();
        catalog.Modules.Add(new ModuleInfo(
            name.Name ?? "unknown",
            Stamp(assembly),
            isPlugin ? assembly.Location : null,
            isPlugin,
            providers, widgets, tabKinds, importers, endpoints, jobs));
    }
}
