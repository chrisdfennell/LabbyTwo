using LabbyTwo.Core;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LabbyTwo.Tests;

/// <summary>
/// A provider that needs ConfigStore — the status page plugin's does, for its buttons —
/// must not stop LabbyTwo starting. The registry is built from every provider, so if
/// ConfigStore takes the registry in its constructor the two need each other. Providers are
/// registered through factories, which hides that cycle from the container: rather than
/// refusing to start, v1.6.2 hung after "Scanning plugins" whenever that plugin was
/// installed.
/// </summary>
public sealed class ProviderCycleTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "labbytwo-cycle-" + Guid.NewGuid().ToString("n"));

    public sealed class NeedsTheStore(ConfigStore config) : IConnectionProvider
    {
        public ConfigStore Config { get; } = config;
        public string Type => "needs-the-store";
        public string DisplayName => "Needs the store";
        public string Icon => "🔁";
        public string Description => "";
        public IReadOnlyList<FieldSpec> Fields => [];

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
            Task.FromResult(ProbeResult.Up(TimeSpan.Zero));
    }

    private sealed class Env(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "LabbyTwo.Tests";
        public string ContentRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    [Fact]
    public async Task A_provider_that_needs_the_config_store_does_not_deadlock_the_registry()
    {
        Directory.CreateDirectory(_directory);

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        services.AddSingleton<IHostEnvironment>(new Env(_directory));
        services.AddSingleton(Options.Create(new LabbyOptions { DatabasePath = Path.Combine(_directory, "t.db") }));

        // The same shape Modules registers plugin types in: the concrete type, and the
        // interface as a factory pointing at it. The factory is what hides the cycle.
        services.AddSingleton<NeedsTheStore>();
        services.AddSingleton<IConnectionProvider>(sp => sp.GetRequiredService<NeedsTheStore>());
        services.AddSingleton<IEnumerable<IWidgetType>>([]);
        services.AddSingleton<IEnumerable<ITabKind>>([]);
        services.AddSingleton<Registry>();
        services.AddSingleton<Db>();
        services.AddSingleton<ConfigStore>();

        using var provider = services.BuildServiceProvider();

        // On a thread of its own with a deadline, because the failure is a hang, and a test
        // that hangs reports nothing at all.
        var resolve = Task.Run(() => provider.GetRequiredService<Registry>());
        var finished = await Task.WhenAny(resolve, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(resolve, finished);
        var registry = await resolve;
        Assert.IsType<NeedsTheStore>(registry.Provider("needs-the-store"));

        // And the store still reaches the registry once everything exists: saving a
        // connection looks up its provider's secret fields.
        var store = provider.GetRequiredService<NeedsTheStore>().Config;
        await store.SaveConnectionAsync(new Connection { Id = "c1", Provider = "needs-the-store", Name = "x" });
        Assert.Single(await store.ConnectionsAsync());
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
    }
}
