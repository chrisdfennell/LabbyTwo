using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LabbyTwo.Tests;

/// <summary>
/// A sweep waits for every probe before it records any of them, so one probe that never
/// answers used to freeze the status of every connection. The monitor now gives each probe
/// a deadline of its own, and holds to it even for a provider that ignores its token.
/// </summary>
public sealed class ProbeDeadlineTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "labbytwo-deadline-" + Guid.NewGuid().ToString("n"));

    private readonly ServiceProvider _services;

    private sealed class NeverAnswers : IConnectionProvider
    {
        public string Type => "never";
        public string DisplayName => "Never answers";
        public string Icon => "🕳";
        public string Description => "";
        public IReadOnlyList<FieldSpec> Fields => [];

        // Deliberately deaf to the token: the worst case a plugin can hand the monitor.
        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
            new TaskCompletionSource<ProbeResult>().Task;
    }

    public ProbeDeadlineTests()
    {
        Directory.CreateDirectory(_directory);

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        services.AddTestStorage(_directory);
        services.AddSingleton<IConnectionProvider>(new NeverAnswers());
        services.AddSingleton<IEnumerable<IWidgetType>>([]);
        services.AddSingleton<IEnumerable<ITabKind>>([]);
        services.AddSingleton<Registry>();
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<HistoryStore>();
        services.AddSingleton<HealthMonitor>();
        _services = services.BuildServiceProvider();
    }

    [Fact]
    public async Task A_probe_that_never_answers_is_reported_down_at_the_deadline()
    {
        var monitor = _services.GetRequiredService<HealthMonitor>();
        monitor.ProbeDeadline = TimeSpan.FromMilliseconds(200);

        var probe = monitor.ProbeAsync(new Connection { Provider = "never", Name = "Hung" }, CancellationToken.None);
        var finished = await Task.WhenAny(probe, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(probe, finished);
        var result = await probe;
        Assert.False(result.Ok);
        Assert.Contains("No answer within", result.Message);
    }

    public void Dispose() => TestHost.Teardown(_services, _directory);
}
