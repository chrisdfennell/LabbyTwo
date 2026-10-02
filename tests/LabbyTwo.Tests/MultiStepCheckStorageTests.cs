using LabbyTwo.Core;
using LabbyTwo.Providers;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabbyTwo.Tests;

/// <summary>
/// A multi-step check's secrets through the real storage and the real monitor: encrypted in
/// the database like any other password, decrypted for the probe, and the probe run on the
/// monitor's own path.
/// </summary>
public sealed class MultiStepCheckStorageTests : IDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;

    public MultiStepCheckStorageTests()
    {
        Directory.CreateDirectory(_directory);
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        services.AddTestStorage(_directory);
        services.AddSingleton<IConnectionProvider>(new MultiStepCheckProvider(NullLogger<ProviderHttpLogger>.Instance));
        services.AddSingleton<IEnumerable<IWidgetType>>([]);
        services.AddSingleton<IEnumerable<ITabKind>>([]);
        services.AddSingleton<Registry>();
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<HistoryStore>();
        services.AddSingleton<HealthMonitor>();
        _services = services.BuildServiceProvider();
        _services.GetRequiredService<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => TestHost.Teardown(_services, _directory);

    [Fact]
    public async Task Secrets_are_stored_encrypted_and_the_monitor_probes_with_them_decrypted()
    {
        const string secret = "correct-horse-battery";
        using var app = new FakeApp((context, _) => context.Request.Headers["X-Key"] == secret
            ? FakeApp.WriteAsync(context, "welcome")
            : FakeApp.WriteAsync(context, "who are you", 401));
        var connection = new Connection
        {
            Provider = "steps",
            Name = "App",
            Settings = new SettingsBag
            {
                ["steps"] = StepCheck.Serialize([new CheckStep { Name = "Home", Url = $"{app.Base}/", Headers = "X-Key: ${secret:key}" }]),
                ["secrets"] = StepCheck.SerializeSecrets(new Dictionary<string, string> { ["key"] = secret }),
            },
        };
        var config = _services.GetRequiredService<ConfigStore>();
        await config.SaveConnectionAsync(connection);

        await using (var db = new SqliteConnection($"Data Source={Path.Combine(_directory, "test.db")};Pooling=False"))
        {
            await db.OpenAsync();
            var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT settings FROM connections WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", connection.Id);
            var raw = (string)(await cmd.ExecuteScalarAsync())!;
            Assert.StartsWith("enc:", SettingsBag.FromJson(raw)["secrets"]);
            Assert.DoesNotContain(secret, raw);
        }

        var saved = await config.ConnectionAsync(connection.Id);
        Assert.Equal(secret, StepCheck.ParseSecrets(saved!.Settings.Get("secrets"))["key"]);

        var result = await _services.GetRequiredService<HealthMonitor>().ProbeAsync(saved, CancellationToken.None);
        Assert.True(result.Ok, result.Message);
    }
}
