using LabbyTwo.Core;
using LabbyTwo.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace LabbyTwo.Tests;

/// <summary>
/// The stores' caches against a write that lands while a load is in flight. The real
/// interleaving depends on the thread pool, so the cache is driven step by step here, and
/// the stores are checked separately for doing what the cache relies on.
/// </summary>
public sealed class VersionedCacheTests
{
    [Fact]
    public void ALoadThatFinishesAfterAnInvalidationIsReturnedButNotKept()
    {
        var cache = new VersionedCache<string>();

        var version = cache.Version;      // the load starts and reads the old row...
        cache.Invalidate();               // ...a save lands and drops the cache...
        var returned = cache.Store("stale", version);   // ...and the load finishes.

        Assert.Equal("stale", returned);
        Assert.Null(cache.Value);
    }

    [Fact]
    public void AnUninterruptedLoadIsKept()
    {
        var cache = new VersionedCache<string>();

        cache.Store("fresh", cache.Version);

        Assert.Equal("fresh", cache.Value);
    }

    [Fact]
    public void InvalidatingDropsWhatWasKept()
    {
        var cache = new VersionedCache<string>();
        cache.Store("fresh", cache.Version);

        cache.Invalidate();

        Assert.Null(cache.Value);
    }

    /// <summary>
    /// End to end on the store: many readers loading while writes land, and afterwards the
    /// cache has to agree with the database. Before the version check a reader that lost the
    /// race could leave the old name cached with nothing left to invalidate it.
    /// </summary>
    [Fact]
    public async Task AfterConcurrentReadsAndWritesTheCacheMatchesTheLastWrite()
    {
        var directory = TestHost.TempDirectory();
        var services = TestHost.ReadyHost(directory);
        try
        {
            var config = services.GetRequiredService<ConfigStore>();
            var connection = new Connection { Provider = "http", Name = "v0" };
            await config.SaveConnectionAsync(connection);

            using var stop = new CancellationTokenSource();
            var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    await config.ConnectionsAsync();
                    await config.TabsAsync();
                }
            })).ToList();

            for (var i = 1; i <= 25; i++)
            {
                await config.SaveConnectionAsync(connection with { Name = $"v{i}" });
                await config.SaveTabAsync(new Tab { Id = "t", Slug = "t", Name = $"v{i}" });
            }

            stop.Cancel();
            await Task.WhenAll(readers);

            Assert.Equal("v25", (await config.ConnectionAsync(connection.Id))!.Name);
            Assert.Equal("v25", (await config.TabAsync("t"))!.Name);
        }
        finally
        {
            TestHost.Teardown(services, directory);
        }
    }
}
