using LabbyTwo.Services;

namespace LabbyTwo.Tests;

/// <summary>
/// What removing a plugin is allowed to delete.
///
/// This is the part of the install system that can lose something. Installing writes files;
/// getting that wrong means an extra DLL. Removing deletes them, and getting *that* wrong
/// means a plugin somebody still uses stops loading, with the reason three folders away.
/// </summary>
public sealed class PluginManifestTests : IDisposable
{
    private readonly string _directory = TestHost.TempDirectory();

    public PluginManifestTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Install(string name, params string[] files)
    {
        foreach (var file in files)
            File.WriteAllText(Path.Combine(_directory, file), name);

        PluginManifest.Record(_directory, name, files);
    }

    [Fact]
    public void EverythingAnArchiveBroughtGoesWithIt()
    {
        Install("LabbyTwo.TerminalPlugin", "LabbyTwo.TerminalPlugin.dll", "Renci.SshNet.dll");

        var removable = PluginManifest.RemovableFor(_directory, "LabbyTwo.TerminalPlugin");

        Assert.Contains("LabbyTwo.TerminalPlugin.dll", removable);
        Assert.Contains("Renci.SshNet.dll", removable);
    }

    /// <summary>
    /// The one that matters. Two plugins can ship the same library, and deleting it because
    /// one of them left would break the other — with a "could not load file or assembly"
    /// that says nothing about which plugin was uninstalled.
    /// </summary>
    [Fact]
    public void ADependencyAnotherPluginAlsoUsesIsLeftAlone()
    {
        Install("LabbyTwo.OnePlugin", "LabbyTwo.OnePlugin.dll", "Shared.Library.dll");
        Install("LabbyTwo.TwoPlugin", "LabbyTwo.TwoPlugin.dll", "Shared.Library.dll");

        var removable = PluginManifest.RemovableFor(_directory, "LabbyTwo.OnePlugin");

        Assert.Contains("LabbyTwo.OnePlugin.dll", removable);
        Assert.DoesNotContain("Shared.Library.dll", removable);
    }

    [Fact]
    public void OnceTheOtherOneIsGoneTheSharedLibraryCanGoToo()
    {
        Install("LabbyTwo.OnePlugin", "LabbyTwo.OnePlugin.dll", "Shared.Library.dll");
        Install("LabbyTwo.TwoPlugin", "LabbyTwo.TwoPlugin.dll", "Shared.Library.dll");

        PluginUpdater.Remove(_directory, "LabbyTwo.TwoPlugin");

        Assert.Contains("Shared.Library.dll", PluginManifest.RemovableFor(_directory, "LabbyTwo.OnePlugin"));
    }

    /// <summary>
    /// A plugin unzipped by hand, or installed before any of this existed, has nothing
    /// recorded. The file named after it is the one thing that is certainly its, so removal
    /// still works rather than refusing.
    /// </summary>
    [Fact]
    public void APluginWithNothingRecordedFallsBackToTheObviousFile()
    {
        File.WriteAllText(Path.Combine(_directory, "LabbyTwo.HandInstalled.dll"), "x");

        var removable = PluginManifest.RemovableFor(_directory, "LabbyTwo.HandInstalled");

        Assert.Equal(["LabbyTwo.HandInstalled.dll"], removable);
    }

    [Fact]
    public void SomethingNeitherRecordedNorPresentRemovesNothing()
        => Assert.Empty(PluginManifest.RemovableFor(_directory, "LabbyTwo.NotHere"));

    [Fact]
    public void RemovingDeletesTheFilesAndForgetsThem()
    {
        Install("LabbyTwo.OnePlugin", "LabbyTwo.OnePlugin.dll", "One.Extra.dll");

        var removed = PluginUpdater.Remove(_directory, "LabbyTwo.OnePlugin");

        Assert.Equal(2, removed.Count);
        Assert.False(File.Exists(Path.Combine(_directory, "LabbyTwo.OnePlugin.dll")));
        Assert.False(File.Exists(Path.Combine(_directory, "One.Extra.dll")));
        Assert.DoesNotContain("LabbyTwo.OnePlugin", PluginManifest.Read(_directory).Keys);
    }

    [Fact]
    public void ReinstallingReplacesTheRecordRatherThanAddingToIt()
    {
        Install("LabbyTwo.OnePlugin", "LabbyTwo.OnePlugin.dll", "Old.Dependency.dll");
        Install("LabbyTwo.OnePlugin", "LabbyTwo.OnePlugin.dll", "New.Dependency.dll");

        var removable = PluginManifest.RemovableFor(_directory, "LabbyTwo.OnePlugin");

        Assert.Contains("New.Dependency.dll", removable);
        // The old one is orphaned rather than tracked. An inert DLL nobody loads is a much
        // smaller problem than a manifest that grows for ever across reinstalls.
        Assert.DoesNotContain("Old.Dependency.dll", removable);
    }

    [Fact]
    public void AnUnreadableManifestIsTreatedAsEmptyRatherThanThrowing()
    {
        File.WriteAllText(Path.Combine(_directory, ".labby-plugins.json"), "{ not json");

        Assert.Empty(PluginManifest.Read(_directory));
    }
}
