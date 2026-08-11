using LabbyTwo.Core;

namespace LabbyTwo.Tests;

/// <summary>A plugin built against a different LabbyTwo: it loads, then throws when used.</summary>
public sealed class ThrowingProvider : IConnectionProvider
{
    public string Type => "throwing";
    public string DisplayName => "Broken plugin";
    public string Icon => "💣";
    public string Description => "Throws when asked what settings it has.";

    public IReadOnlyList<FieldSpec> Fields =>
        throw new MissingMethodException("Method not found: 'Void FieldSpec..ctor'.");

    public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
        Task.FromResult(ProbeResult.Up(TimeSpan.Zero));
}

/// <summary>
/// A plugin that loads and then misbehaves used to take the whole dashboard down: provider
/// fields are read on the path that loads every connection, so one bad DLL meant "Something
/// went wrong" on every page — which is what happened on a real install while a plugin was
/// being replaced under the running process.
///
/// A plugin's mistakes should cost you the plugin.
/// </summary>
public class BrokenExtensionTests
{
    private static Registry Build(ModuleCatalog catalog, params IConnectionProvider[] providers) =>
        new(providers, [], [], catalog);

    [Fact]
    public void OneThatCannotDescribeItselfIsLeftOut()
    {
        var catalog = new ModuleCatalog();
        var registry = Build(catalog, new ThrowingProvider(), new WorkingProvider());

        Assert.Null(registry.Provider("throwing"));
        Assert.NotNull(registry.Provider("working"));
    }

    [Fact]
    public void TheOthersStillWork()
    {
        // The point of the whole exercise: everything else carries on.
        var registry = Build(new ModuleCatalog(), new ThrowingProvider(), new WorkingProvider());

        Assert.Single(registry.Providers);
        Assert.Equal("Working", registry.Providers[0].DisplayName);
    }

    [Fact]
    public void AndTheSettingsPageIsToldWhy()
    {
        var catalog = new ModuleCatalog();
        Build(catalog, new ThrowingProvider());

        var failure = Assert.Single(catalog.Failures);
        Assert.Contains("could not describe itself", failure.Reason);

        // The message has to say what to do, not only what happened.
        Assert.Contains("Rebuild the plugin", failure.Reason);
    }

    [Fact]
    public void AGoodPluginIsNotPenalisedForBeingNearOne()
    {
        var registry = Build(new ModuleCatalog(), new ThrowingProvider(), new WorkingProvider());
        Assert.Single(registry.Provider("working")!.Fields);
    }

    private sealed class WorkingProvider : IConnectionProvider
    {
        public string Type => "working";
        public string DisplayName => "Working";
        public string Icon => "✅";
        public string Description => "Fine.";
        public IReadOnlyList<FieldSpec> Fields => [new("url", "URL", FieldKind.Url)];

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
            Task.FromResult(ProbeResult.Up(TimeSpan.Zero));
    }
}
