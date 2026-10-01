using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabbyTwo.Tests;

/// <summary>
/// The one thing SharedSeries does beyond sharing a query that is already running: handing a
/// caller that said it will take one an answer read a little while ago, without a query. The
/// ordinary caller must never get one — a card told to reload by a sweep has to see the sweep.
/// </summary>
public sealed class SharedSeriesTests : IDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _host;
    private readonly LoginThrottleTests.ManualClock _clock = new() { Now = DateTimeOffset.UtcNow };
    private readonly SharedSeries _series;

    public SharedSeriesTests()
    {
        _host = TestHost.ReadyHost(_directory);
        _series = new SharedSeries(_host.GetRequiredService<HistoryStore>(), new Offload(NullLogger<Offload>.Instance), _clock);
    }

    public void Dispose()
    {
        _series.Dispose();
        TestHost.Teardown(_host, _directory);
    }

    private HistoryStore History => _host.GetRequiredService<HistoryStore>();

    [Fact]
    public async Task AFreshEnoughAnswerIsHandedBackWithoutAQuery()
    {
        await History.RecordAsync("nas", new Dictionary<string, double> { ["cpu"] = 10 }, CancellationToken.None);
        var week = TimeSpan.FromDays(7);

        var first = await _series.SamplesAsync("nas", "cpu", week, TimeSpan.FromMinutes(10));
        _clock.Advance(TimeSpan.FromMinutes(5));
        var second = await _series.SamplesAsync("nas", "cpu", week, TimeSpan.FromMinutes(10));

        Assert.Same(first, second);
        Assert.Equal(1, _series.Queries);

        // Past what the caller will take, it is read again — and sees what was written since.
        await History.RecordAsync("nas", new Dictionary<string, double> { ["cpu"] = 20 }, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(6));
        var third = await _series.SamplesAsync("nas", "cpu", week, TimeSpan.FromMinutes(10));
        Assert.Equal(2, _series.Queries);
        Assert.Equal(2, third.Count);
    }

    [Fact]
    public async Task AnOrdinaryCallerNeverGetsAKeptAnswer()
    {
        await History.RecordAsync("nas", new Dictionary<string, double> { ["cpu"] = 10 }, CancellationToken.None);

        await _series.SamplesAsync("nas", "cpu", TimeSpan.FromDays(1), TimeSpan.FromMinutes(10));
        await History.RecordAsync("nas", new Dictionary<string, double> { ["cpu"] = 20 }, CancellationToken.None);
        var fresh = await _series.SamplesAsync("nas", "cpu", TimeSpan.FromDays(1));

        Assert.Equal(2, _series.Queries);
        Assert.Equal(2, fresh.Count);
    }

    [Fact]
    public async Task AFixedSpanIsKeptByItsEdges()
    {
        var now = DateTimeOffset.UtcNow;
        var from = now.AddDays(-3);
        var to = now.AddDays(-1);

        await _series.BetweenAsync("nas", "cpu", from, to, SharedSeries.LongestFresh);
        await _series.BetweenAsync("nas", "cpu", from, to, SharedSeries.LongestFresh);
        Assert.Equal(1, _series.Queries);

        await _series.BetweenAsync("nas", "cpu", from, to.AddHours(1), SharedSeries.LongestFresh);
        Assert.Equal(2, _series.Queries);

        // Nobody may ask for more than the longest, however long they say.
        _clock.Advance(SharedSeries.LongestFresh + TimeSpan.FromMinutes(1));
        await _series.BetweenAsync("nas", "cpu", from, to, TimeSpan.FromDays(30));
        Assert.Equal(3, _series.Queries);
    }
}
