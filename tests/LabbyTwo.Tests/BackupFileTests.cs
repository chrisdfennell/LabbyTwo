using LabbyTwo.Core;
using LabbyTwo.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace LabbyTwo.Tests;

/// <summary>
/// A backup is a file handed to somebody else the moment it is written — streamed and
/// deleted by /api/backup, pruned by the nightly job. A connection pool holding it open
/// behind the scenes broke both on Windows, where an open file cannot be deleted.
/// </summary>
public sealed class BackupFileTests : IDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;

    public BackupFileTests() => _services = TestHost.ReadyHost(_directory);

    public void Dispose() => TestHost.Teardown(_services, _directory);

    [Fact]
    public async Task ABackupIsReleasedAsSoonAsItIsWritten()
    {
        await _services.GetRequiredService<ConfigStore>().SaveConnectionAsync(
            new Connection { Provider = "http", Name = "Router" });

        var path = Path.Combine(_directory, "copy.db");
        await _services.GetRequiredService<Db>().BackupToAsync(path);

        // What /api/backup does next, less the sharing it allows: nobody else may have it.
        await using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.True(exclusive.Length > 0);

        // And what the nightly prune does.
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task TheCopyIsAWorkingDatabase()
    {
        await _services.GetRequiredService<ConfigStore>().SaveConnectionAsync(
            new Connection { Provider = "http", Name = "Router" });

        var path = Path.Combine(_directory, "copy.db");
        await _services.GetRequiredService<Db>().BackupToAsync(path);

        await using (var copy = new SqliteConnection(
                         new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            await copy.OpenAsync();
            var cmd = copy.CreateCommand();
            cmd.CommandText = "SELECT name FROM connections";
            Assert.Equal("Router", await cmd.ExecuteScalarAsync());
        }

        File.Delete(path);
    }
}
