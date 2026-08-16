using LabbyTwo.Services;

namespace LabbyTwo.Tests;

/// <summary>
/// The happy path, against the real release. Skipped automatically when GitHub is not
/// reachable, because a test that fails on a train is a test people learn to ignore.
/// </summary>
public class PluginUpdaterLiveTests
{
    [Fact]
    public async Task ItReplacesABundledPluginAndLeavesTheRestAlone()
    {
        var directory = TestHost.TempDirectory();
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "LabbyTwo.ChoresPlugin.dll"), "stale");
            File.WriteAllText(Path.Combine(directory, "SomebodyElses.Plugin.dll"), "mine");

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            var result = await PluginUpdater.UpdateAsync(directory, "v1.3.6", http);

            if (result.Reason is { Length: > 0 } reason && reason.Contains("Could not reach"))
                return;   // offline; nothing to assert

            Assert.Contains("LabbyTwo.ChoresPlugin", result.Updated);

            // Replaced with something that is actually a DLL rather than the word "stale".
            var replaced = File.ReadAllBytes(Path.Combine(directory, "LabbyTwo.ChoresPlugin.dll"));
            Assert.True(replaced.Length > 1000, $"only {replaced.Length} bytes");
            Assert.Equal(0x4D, replaced[0]);   // "MZ"
            Assert.Equal(0x5A, replaced[1]);

            // And the one nobody publishes is untouched.
            Assert.Equal("mine", File.ReadAllText(Path.Combine(directory, "SomebodyElses.Plugin.dll")));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }
}
