using LabbyTwo.Core;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace LabbyTwo.Tests;

/// <summary>
/// The smallest service provider that gives a test a real database, real data protection
/// and the real registry — so a test exercises the same code paths the app does rather
/// than a stand-in that agrees with whatever the test expects.
/// </summary>
public static class TestHost
{
    public static ServiceProvider Build(string directory)
    {
        Directory.CreateDirectory(directory);

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(directory, "keys")));
        services.AddSingleton<IHostEnvironment>(new TestEnvironment(directory));
        services.AddSingleton(Options.Create(new LabbyOptions { DatabasePath = Path.Combine(directory, "test.db") }));
        services.AddModules(typeof(Registry).Assembly, Path.Combine(directory, "plugins"),
            LoggerFactory.Create(_ => { }).CreateLogger("test"));
        services.AddSingleton<Registry>();
        services.AddSingleton<Db>();
        services.AddSingleton<AppSettingsStore>();
        services.AddSingleton<AlertRuleStore>();
        services.AddSingleton<HistoryStore>();
        services.AddSingleton<ConfigStore>();

        return services.BuildServiceProvider();
    }

    /// <summary>A store backed by a real, migrated database in <paramref name="directory"/>.</summary>
    public static ConfigStore ConfigStore(string directory)
    {
        var services = Build(directory);
        services.GetRequiredService<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();
        return services.GetRequiredService<ConfigStore>();
    }

    /// <summary>Enough of an environment for the options binding; nothing reads the rest.</summary>
    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "LabbyTwo.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class NullFileProvider : IFileProvider
    {
        public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;
        public IFileInfo GetFileInfo(string subpath) => new NotFoundFileInfo(subpath);
        public IChangeToken Watch(string filter) => NullChangeToken.Singleton;
    }
}
