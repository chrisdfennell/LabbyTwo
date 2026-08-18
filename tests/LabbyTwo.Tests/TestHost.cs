using LabbyTwo.Core;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
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

    /// <summary>
    /// Just enough for the providers themselves: some reach for app settings — the shared
    /// location, the unit system — so a container that only has logging and an HTTP factory
    /// can no longer construct the registry. Nothing here touches the disk unless a test
    /// actually opens the database.
    /// </summary>
    /// <param name="configure">
    /// For a test that turns one of the knobs the app reads from configuration — how many
    /// failures make a service DOWN, say — rather than registering a second
    /// <see cref="LabbyOptions"/> after this one and relying on which registration wins.
    /// </param>
    public static IServiceCollection AddTestStorage(
        this IServiceCollection services, string directory, Action<LabbyOptions>? configure = null)
    {
        var options = new LabbyOptions { DatabasePath = Path.Combine(directory, "test.db") };
        configure?.Invoke(options);

        services.AddSingleton<IHostEnvironment>(new TestEnvironment(directory));
        services.AddSingleton(Options.Create(options));
        services.AddSingleton<Db>();
        services.AddSingleton<AppSettingsStore>();
        return services;
    }

    /// <summary>A temporary directory name, for a test that needs somewhere and not what.</summary>
    public static string TempDirectory() =>
        Path.Combine(Path.GetTempPath(), "labbytwo-test-" + Guid.NewGuid().ToString("n"));

    /// <summary>
    /// Ends a test class's use of its database and takes its directory with it.
    ///
    /// The pool is emptied for this connection string alone, which is the whole point:
    /// <c>SqliteConnection.ClearAllPools</c> is process-wide, and xUnit runs test classes
    /// in parallel, so a class finishing its teardown used to dispose pooled connections
    /// another class was still reading from. That surfaced as an ObjectDisposedException
    /// in whichever test happened to be mid-query — a different one each run, which is
    /// what made it look like the machine's fault rather than ours.
    /// </summary>
    public static void Teardown(ServiceProvider services, string directory)
    {
        // Read before disposing: afterwards the provider refuses to resolve anything.
        var connectionString = services.GetService<Db>()?.ConnectionString;
        services.Dispose();

        if (connectionString is not null)
        {
            // SQLite keeps the file open behind a pooled connection, so on Windows the
            // directory below cannot be deleted until this one pool is emptied.
            using var pooled = new SqliteConnection(connectionString);
            SqliteConnection.ClearPool(pooled);
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory should not fail an otherwise passing run.
        }
    }

    /// <summary>
    /// A container whose database exists and is migrated, so a test can resolve a store
    /// and start using it. Returns the container rather than the store: handing back one
    /// service and dropping the provider on the floor leaves it undisposed for the life of
    /// the run, and with it the pooled connection holding the file open.
    /// </summary>
    public static ServiceProvider ReadyHost(string directory)
    {
        var services = Build(directory);
        services.GetRequiredService<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();
        return services;
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
