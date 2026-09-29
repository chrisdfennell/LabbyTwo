using LabbyTwo.Core;
using Microsoft.Extensions.DependencyInjection;

namespace LabbyTwo.Tests;

/// <summary>
/// A plugin's own libraries sit in the plugin folder beside it — SSH.NET and BouncyCastle
/// beside the terminal plugin. They declare nothing because they are not plugins, and
/// reporting them as failures put a permanent warning on every install that ran one.
/// </summary>
public sealed class PluginLibraryTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "labbytwo-libraries-" + Guid.NewGuid().ToString("n"));

    [Fact]
    public void A_library_beside_a_plugin_is_not_reported_as_a_broken_plugin()
    {
        Directory.CreateDirectory(_directory);

        // Any assembly that does not reference LabbyTwo stands in for SSH.NET here. This
        // one is already loaded by the test run, so loading it again changes nothing.
        var library = typeof(Xunit.Assert).Assembly.Location;
        File.Copy(library, Path.Combine(_directory, Path.GetFileName(library)));

        var catalog = new ServiceCollection().AddModules(typeof(Modules).Assembly, _directory);

        Assert.Equal(1, catalog.DllsFound);
        Assert.Empty(catalog.Failures);

        // Nor listed as a plugin, where its own version (xunit's, here; 2026.0.0.1 for
        // SSH.NET) was compared with LabbyTwo's and reported as built for another version.
        Assert.DoesNotContain(catalog.Modules, module => module.IsPlugin);
        Assert.DoesNotContain(catalog.Modules, catalog.BuiltForAnother);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
