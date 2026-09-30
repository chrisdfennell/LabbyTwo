using LabbyTwo.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LabbyTwo.Tests;

/// <summary>
/// What every connection is opened with, the samples index an upgrade ends up with, and the
/// slow-query log — the three things behind "LabbyTwo is slow on my NAS" that no timing test
/// on a fast disk would ever notice.
/// </summary>
public sealed class DatabaseSettingsTests : IDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ListLogger _logs = new();
    private ServiceProvider? _services;

    private Db Start(Action<LabbyOptions>? configure = null)
    {
        _services = new ServiceCollection()
            .AddLogging(logging => logging.AddProvider(_logs).SetMinimumLevel(LogLevel.Information))
            .AddTestStorage(_directory, configure)
            .BuildServiceProvider();
        return _services.GetRequiredService<Db>();
    }

    public void Dispose()
    {
        if (_services is not null)
            TestHost.Teardown(_services, _directory);
    }

    private static async Task<long> PragmaAsync(SqliteConnection connection, string name)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA {name}";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Every_connection_syncs_at_checkpoints_and_waits_its_turn_for_the_lock()
    {
        // Per connection, not per file: set once when the schema was made, as busy_timeout
        // used to be, it covered that one connection and none of the ones the stores open.
        var db = Start();
        for (var i = 0; i < 3; i++)
        {
            await using var connection = await db.OpenAsync();
            Assert.Equal(1, await PragmaAsync(connection, "synchronous")); // NORMAL
            Assert.Equal(10000, await PragmaAsync(connection, "busy_timeout"));
            Assert.Equal("wal", (await new SqliteCommand("PRAGMA journal_mode", connection).ExecuteScalarAsync())?.ToString());
        }
    }

    [Fact]
    public async Task An_upgraded_database_swaps_the_old_samples_index_for_the_covering_one_and_keeps_every_row()
    {
        var db = Start();
        await db.EnsureSchemaAsync();

        // Put it back the way a v1.10 database has it: the old index, stamped before migration 30.
        await using (var connection = new SqliteConnection(db.ConnectionString))
        {
            await connection.OpenAsync();
            var cmd = connection.CreateCommand();
            cmd.CommandText = """
                DROP INDEX ix_samples_series;
                CREATE INDEX ix_samples_lookup ON samples (connection_id, metric, ts);
                INSERT INTO samples (connection_id, metric, ts, value) VALUES ('nas', 'cpu', 1, 5), ('nas', 'cpu', 2, 6), ('nas', 'disk', 1, 70);
                PRAGMA user_version = 29;
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var reopened = new Db(_services!.GetRequiredService<Microsoft.Extensions.Options.IOptions<LabbyOptions>>(),
            _services!.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>(),
            _services!.GetRequiredService<ILogger<Db>>());
        await using (var connection = await reopened.OpenAsync())
        {
            var names = new List<string>();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'samples'";
            await using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                    names.Add(reader.GetString(0));
            }
            Assert.Equal(["ix_samples_series"], names);

            var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM samples";
            Assert.Equal(3L, await count.ExecuteScalarAsync());
        }

        // It said so first, since on a big database the rebuild is a pause worth explaining.
        Assert.Contains(_logs.Lines, line => line.Contains("Rebuilding the history index", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_query_log_is_off_unless_asked_for()
    {
        var db = Start();
        await using var connection = await db.OpenAsync();
        await SlowQueryAsync(connection);
        Assert.DoesNotContain(_logs.Lines, line => line.Contains("Slow query", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_query_log_names_a_slow_statement_and_who_ran_it_and_sums_up_what_read_the_most()
    {
        var db = Start(options =>
        {
            options.SlowQueryMs = 1;
            options.SlowQuerySummaryMinutes = 0.0001;
        });
        await using (var connection = await db.OpenAsync())
            await SlowQueryAsync(connection);

        var slow = Assert.Single(_logs.Lines, line => line.Contains("Slow query", StringComparison.Ordinal));
        Assert.Contains("WITH RECURSIVE n(i)", slow, StringComparison.Ordinal);
        Assert.Contains(nameof(DatabaseSettingsTests), slow, StringComparison.Ordinal);

        // The summary comes with the first statement after its interval has passed.
        await Task.Delay(50);
        await using (var connection = await db.OpenAsync())
            await new SqliteCommand("SELECT 1", connection).ExecuteScalarAsync();
        Assert.Contains(_logs.Lines, line => line.StartsWith("SQLite read", StringComparison.Ordinal)
                                             && line.Contains("WITH RECURSIVE n(i)", StringComparison.Ordinal));
    }

    /// <summary>Long enough to cross a one-millisecond threshold on any machine.</summary>
    private static async Task SlowQueryAsync(SqliteConnection connection)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 300000) SELECT SUM(i) FROM n";
        await cmd.ExecuteScalarAsync();
    }

    private sealed class ListLogger : ILoggerProvider, ILogger
    {
        private readonly List<string> _lines = [];

        public List<string> Lines
        {
            get
            {
                lock (_lines)
                    return [.. _lines];
            }
        }

        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_lines)
                _lines.Add(formatter(state, exception));
        }

        public void Dispose()
        {
        }
    }
}
