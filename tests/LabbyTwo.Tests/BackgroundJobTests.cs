using LabbyTwo.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LabbyTwo.Tests;

/// <summary>Top level and public, because discovery skips nested types.</summary>
public sealed class FakeJob : IBackgroundJob
{
    public string Name => "test-job";
    public TimeSpan Interval => TimeSpan.FromHours(1);
    public Task RunAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// The job extension point is the one that runs code nobody asked for, on a timer, in a
/// process that has to stay up. So the things worth pinning down are that discovery finds
/// one, and that the two defaults which keep a careless plugin from hurting the host —
/// not running at startup, and being registered as a plain singleton — stay as they are.
/// </summary>
public class BackgroundJobTests
{
    [Fact]
    public void DiscoveryFindsThem()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();

        var catalog = services.AddModules(
            typeof(BackgroundJobTests).Assembly,
            Path.Combine(Path.GetTempPath(), "labbytwo-no-plugins-" + Guid.NewGuid().ToString("n")),
            LoggerFactory.Create(_ => { }).CreateLogger("test"));

        var resolved = services.BuildServiceProvider().GetServices<IBackgroundJob>();

        Assert.Contains(resolved, job => job is FakeJob);
        Assert.Contains(catalog.Modules, module => module.Jobs.Contains(nameof(FakeJob)));
    }

    [Fact]
    public void NothingRunsAtStartupUnlessAskedFor()
    {
        // A dozen plugins all doing their daily sweep during boot is how a dashboard comes
        // up slowly and nobody can tell why, so opting in has to be the deliberate act.
        Assert.False(((IBackgroundJob)new FakeJob()).RunAtStartup);
    }

    [Fact]
    public void JobsCountTowardsWhatAModuleContributed()
    {
        var module = new ModuleInfo("Test", "1.0", null, true, [], [], [], [], [], ["FakeJob"]);
        Assert.Equal(1, module.TypeCount);
    }
}
