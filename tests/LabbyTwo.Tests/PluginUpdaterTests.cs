using LabbyTwo.Services;

namespace LabbyTwo.Tests;

/// <summary>
/// The plugin updater's refusals, which are the important half.
///
/// It runs in the startup path and rewrites files in the data volume, so every way it can
/// decide *not* to act is load-bearing: a wrong guess here replaces a working plugin, or
/// stops the dashboard booting. The happy path needs GitHub and is checked by running it
/// against the real release.
/// </summary>
public sealed class PluginUpdaterTests : IDisposable
{
    private readonly string _directory = TestHost.TempDirectory();

    public PluginUpdaterTests() => Directory.CreateDirectory(_directory);

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

    /// <summary>
    /// Every local <c>dotnet run</c> is unstamped. Matching "no version" against the newest
    /// release would replace working plugins on a developer's machine with whatever shipped
    /// last, which is the opposite of what anybody wants from a build they are editing.
    /// </summary>
    [Fact]
    public async Task AnUnstampedBuildDoesNothing()
    {
        File.WriteAllText(Path.Combine(_directory, "LabbyTwo.ChoresPlugin.dll"), "not really a dll");

        var result = await PluginUpdater.UpdateAsync(_directory, "", new HttpClient());

        Assert.Empty(result.Updated);
        Assert.Contains("not stamped", result.Reason);
    }

    [Fact]
    public async Task NoPluginsFolderIsNotAnError()
    {
        var result = await PluginUpdater.UpdateAsync(
            Path.Combine(_directory, "nowhere"), "v1.3.6", new HttpClient());

        Assert.Empty(result.Updated);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public async Task AnEmptyFolderIsNotAnError()
    {
        var result = await PluginUpdater.UpdateAsync(_directory, "v1.3.6", new HttpClient());

        Assert.Empty(result.Updated);
        Assert.Contains("No plugins", result.Reason);
    }

    /// <summary>
    /// The one that keeps this from being an installer. A release carries a zip for every
    /// bundled plugin; only the ones already on disk may be replaced, or turning this on
    /// would silently install thirteen things nobody asked for.
    /// </summary>
    [Fact]
    public async Task APluginThatIsNotInstalledIsNotDownloaded()
    {
        // Nothing installed, and a real release with thirteen assets in it.
        var result = await PluginUpdater.UpdateAsync(_directory, "v1.3.6", new HttpClient());

        Assert.Empty(result.Updated);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    /// <summary>
    /// A plugin somebody else wrote has no asset named after it, so it is left alone without
    /// this needing a list of what may be touched.
    /// </summary>
    [Fact]
    public async Task AThirdPartyPluginIsLeftExactlyWhereItIs()
    {
        var mine = Path.Combine(_directory, "SomebodyElses.Plugin.dll");
        File.WriteAllText(mine, "mine");

        await PluginUpdater.UpdateAsync(_directory, "v1.3.6", new HttpClient());

        Assert.Equal("mine", File.ReadAllText(mine));
    }

    [Fact]
    public async Task AReleaseThatDoesNotExistIsReportedRatherThanThrown()
    {
        File.WriteAllText(Path.Combine(_directory, "LabbyTwo.ChoresPlugin.dll"), "x");

        var result = await PluginUpdater.UpdateAsync(_directory, "v0.0.0-nope", new HttpClient());

        Assert.Empty(result.Updated);
        Assert.NotNull(result.Reason);
    }
}
