using System.Reflection;
using LabbyTwo.Core;

namespace LabbyTwo.Tests;

/// <summary>
/// Plugins are separate DLLs, compiled once against whichever LabbyTwo the author had, and
/// then dropped into a folder — nobody rebuilds them because the host moved. So the types
/// they construct are a binary contract, and the compiler cannot warn about breaking it.
///
/// This is what broke a working Gluetun integration: adding a positional parameter to
/// FieldSpec deleted the constructor every existing plugin was calling, and each one
/// started throwing MissingMethodException the moment its fields were read. New settings
/// go on as init-only properties, which is additive.
/// </summary>
public class PluginCompatibilityTests
{
    /// <summary>The parameter list every plugin built before today compiled against.</summary>
    private static readonly Type[] OriginalFieldSpecParameters =
    [
        typeof(string),                       // Key
        typeof(string),                       // Label
        typeof(FieldKind),                    // Kind
        typeof(string),                       // Placeholder
        typeof(string),                       // Help
        typeof(bool),                         // Required
        typeof(string),                       // Default
        typeof(IReadOnlyList<SelectOption>),  // Options
    ];

    [Fact]
    public void TheFieldSpecConstructorPluginsCallStillExists()
    {
        var constructor = typeof(FieldSpec).GetConstructor(OriginalFieldSpecParameters);

        Assert.True(constructor is not null,
            "FieldSpec's eight-parameter constructor is gone, so every plugin DLL built before " +
            "the change now throws MissingMethodException when its fields are read. Add new " +
            "settings as init-only properties instead of positional parameters.");
    }

    [Fact]
    public void NewSettingsAreOptionalRatherThanRequired()
    {
        // Constructed the old way, then refined — which is exactly what an old plugin does,
        // minus the refinement.
        var field = new FieldSpec("connection", "NAS", FieldKind.Connection);

        Assert.Null(field.ProviderFilter);
        Assert.Equal("connection", field.Key);
    }

    [Theory]
    [InlineData(typeof(IConnectionProvider))]
    [InlineData(typeof(IWidgetType))]
    [InlineData(typeof(ITabKind))]
    [InlineData(typeof(IDashboardImporter))]
    [InlineData(typeof(IEndpointExtension))]
    [InlineData(typeof(IBackgroundJob))]
    public void EveryExtensionPointCanBeImplementedWithoutTheNewMembers(Type extensionPoint)
    {
        // A member added to an interface has to carry a default implementation, or every
        // plugin that already implements it stops compiling — and, worse, the ones already
        // built stop loading.
        var required = extensionPoint
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .OfType<MethodInfo>()
            .Where(method => method.IsAbstract)
            .Select(method => method.Name)
            .ToList();

        // The originals are allowed to be abstract; anything added since must not be.
        string[] allowed =
        [
            "get_Type", "get_DisplayName", "get_Icon", "get_Description", "get_Fields",
            "get_Component", "get_Key", "get_Kind", "get_Name", "get_Interval",
            "ProbeAsync", "SendAsync", "Read", "CanHandle", "Map", "RunAsync",
        ];

        Assert.All(required, member =>
            Assert.True(allowed.Contains(member),
                $"{extensionPoint.Name}.{member} is abstract, so every existing plugin breaks. " +
                "Give it a default implementation."));
    }
}
