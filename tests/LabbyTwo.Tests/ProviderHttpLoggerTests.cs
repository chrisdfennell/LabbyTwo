using System.Net;
using LabbyTwo.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LabbyTwo.Tests;

/// <summary>
/// The provider clients' request log. For IFTTT, webhooks and anything taking a key as a
/// parameter the URL is the credential, and the factory's default logging wrote it out on
/// every request.
/// </summary>
public sealed class ProviderHttpLoggerTests
{
    [Theory]
    [InlineData("https://maker.ifttt.com/trigger/alert/with/key/SECRETKEY", "https://maker.ifttt.com")]
    [InlineData("http://pihole.lan:8080/admin/api.php?auth=SECRETKEY", "http://pihole.lan:8080")]
    [InlineData("https://discord.com/api/webhooks/123/SECRETKEY", "https://discord.com")]
    public void OnlyTheOriginSurvives(string url, string expected)
    {
        var redacted = ProviderHttpLogger.Redact(new Uri(url));

        Assert.Equal(expected, redacted);
        Assert.DoesNotContain("SECRETKEY", redacted);
    }

    /// <summary>
    /// Through the real factory, wired the way Program.cs wires it: whatever the pipeline
    /// logs about a request, the key in its URL is not in it.
    /// </summary>
    [Fact]
    public async Task NothingThePipelineLogsContainsThePath()
    {
        var sink = new ListLogger();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(sink));
        services.AddHttpClient(ProviderHttp.ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new AnswerHandler())
            .RemoveAllLoggers()
            .AddLogger(sp => new ProviderHttpLogger(sp.GetRequiredService<ILogger<ProviderHttpLogger>>()));

        await using var provider = services.BuildServiceProvider();
        var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ProviderHttp.ClientName);

        using var response = await http.PostAsync("https://maker.ifttt.com/trigger/x/with/key/SECRETKEY", null);

        Assert.Contains(sink.Lines, line => line.Contains("maker.ifttt.com") && line.Contains("200"));
        Assert.DoesNotContain(sink.Lines, line => line.Contains("SECRETKEY"));
    }

    private sealed class AnswerHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private sealed class ListLogger : ILoggerProvider, ILogger
    {
        public List<string> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Lines)
                Lines.Add(formatter(state, exception));
        }

        public void Dispose()
        {
        }
    }
}
