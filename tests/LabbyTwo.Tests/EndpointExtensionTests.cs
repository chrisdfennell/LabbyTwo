using LabbyTwo.Core;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LabbyTwo.Tests;

/// <summary>
/// The endpoint extension point hands a plugin a piece of the URL space, so the two
/// things worth pinning down are that discovery finds one at all, and that a key cannot
/// reach outside the group it was given.
/// </summary>
/// <summary>
/// Top level and public on purpose: discovery skips nested types, the same way it skips
/// a plugin's internal classes, so a nested fake here would have tested nothing.
/// </summary>
public sealed class FakeEndpoints : IEndpointExtension
{
    public string Key => "test-endpoints";
    public void Map(IEndpointRouteBuilder routes) { }
}

public class EndpointExtensionTests
{

    [Fact]
    public void DiscoveryFindsThem()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();

        // Scanning this test assembly rather than the app's: the host ships no endpoint
        // extension of its own, and a test that asserted "none found" would pass just as
        // happily if discovery were broken.
        var catalog = services.AddModules(
            typeof(EndpointExtensionTests).Assembly,
            Path.Combine(Path.GetTempPath(), "labbytwo-no-plugins-" + Guid.NewGuid().ToString("n")),
            LoggerFactory.Create(_ => { }).CreateLogger("test"));

        var resolved = services.BuildServiceProvider().GetServices<IEndpointExtension>();

        Assert.Contains(resolved, extension => extension is FakeEndpoints);
        Assert.Contains(catalog.Modules, module => module.Endpoints.Contains(nameof(FakeEndpoints)));
    }

    [Fact]
    public void AuthorizationIsOnUnlessAskedFor()
    {
        // The default has to be the safe one: a plugin author who never thought about
        // logins must not accidentally publish an endpoint to the internet. Read through
        // the interface, which is where a default implementation lives.
        Assert.True(((IEndpointExtension)new FakeEndpoints()).RequiresAuthorization);
    }

    [Theory]
    [InlineData("qnap-files", true)]
    [InlineData("drop_box", true)]
    [InlineData("Files2", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("../admin", false)]
    [InlineData("files/download", false)]
    [InlineData("{catchall}", false)]
    public void KeysThatWouldEscapeTheirGroupAreRejected(string key, bool valid)
        => Assert.Equal(valid, ExtensionRoutes.IsValidKey(key));

    [Fact]
    public void RoutesLiveUnderOnePrefix()
        => Assert.Equal("/ext/qnap-files", ExtensionRoutes.PathFor("qnap-files"));
}
