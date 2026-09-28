using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabbyTwo.Tests;

/// <summary>
/// The pieces that keep cards from waiting on the database while a page is drawn. What
/// matters is that the caller is never the one doing the work, and that everything the
/// framework used to take care of — ordering, errors, a component that has gone — is
/// still taken care of now that the load is not awaited.
/// </summary>
public sealed class BackgroundLoadTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>A stand-in for a component: its dispatcher runs things inline, and redraws are counted.</summary>
    private sealed class FakeComponent
    {
        public int Redraws;
        public bool Throws;

        public BackgroundLoad Loader(bool interactive = true) => new(
            action =>
            {
                if (Throws)
                    throw new ObjectDisposedException("renderer");
                action();
                return Task.CompletedTask;
            },
            () => Interlocked.Increment(ref Redraws),
            NullLogger.Instance,
            interactive);
    }

    [Fact]
    public async Task The_caller_does_not_wait_for_a_fetch_that_blocks()
    {
        // The whole point: SQLite blocks rather than yielding, and a fetch that blocks must
        // not block whoever started it. Were it run inline, Load would never return here.
        var component = new FakeComponent();
        using var load = component.Loader();
        using var release = new ManualResetEventSlim();
        var applied = 0;

        var task = load.Load("key", _ =>
        {
            release.Wait(Timeout);
            return Task.FromResult(42);
        }, value => applied = value);

        Assert.False(task.IsCompleted);
        Assert.False(load.Ready);
        Assert.True(load.Loading);

        release.Set();
        await task.WaitAsync(Timeout);

        Assert.Equal(42, applied);
        Assert.True(load.Ready);
        Assert.False(load.Loading);
        Assert.Equal(1, component.Redraws);
    }

    [Fact]
    public async Task A_superseded_load_is_not_applied_even_when_it_finishes_last()
    {
        var component = new FakeComponent();
        using var load = component.Loader();
        var slow = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = new List<string>();

        var first = load.Load("24h", _ => slow.Task, applied.Add);
        var second = load.Load("48h", _ => Task.FromResult("48h answer"), applied.Add);
        await second.WaitAsync(Timeout);

        slow.SetResult("24h answer");
        await first.WaitAsync(Timeout);

        Assert.Equal(["48h answer"], applied);
    }

    [Fact]
    public async Task A_superseded_load_is_cancelled()
    {
        var component = new FakeComponent();
        using var load = component.Loader();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = load.Load("a", async ct =>
        {
            ct.Register(() => cancelled.TrySetResult());
            started.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
            return 0;
        }, _ => { });

        // Wait until the fetch is actually running before superseding it — on the signal,
        // not a fixed pause. A 50 ms sleep passed on a fast machine and failed on a busy CI
        // runner, where the fetch had not been scheduled yet: the second load then
        // cancelled it before it ever registered, and the test waited for a cancellation
        // it could no longer see.
        await started.Task.WaitAsync(Timeout);
        _ = load.Load("b", _ => Task.FromResult(1), _ => { });

        await cancelled.Task.WaitAsync(Timeout);
        await first.WaitAsync(Timeout);
    }

    [Fact]
    public async Task The_same_key_is_not_loaded_twice()
    {
        var component = new FakeComponent();
        using var load = component.Loader();
        var fetches = 0;

        Task<int> Fetch(CancellationToken _)
        {
            Interlocked.Increment(ref fetches);
            return Task.FromResult(1);
        }

        await load.Load("key", Fetch, _ => { }).WaitAsync(Timeout);
        await load.Load("key", Fetch, _ => { }).WaitAsync(Timeout);
        await load.Load(("tuple", 1), Fetch, _ => { }).WaitAsync(Timeout);
        await load.Load(("tuple", 1), Fetch, _ => { }).WaitAsync(Timeout);

        Assert.Equal(2, fetches);
    }

    [Fact]
    public async Task A_refresh_during_a_load_runs_once_more_after_it()
    {
        var component = new FakeComponent();
        using var load = component.Loader();
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetches = 0;
        var applied = new List<int>();

        async Task<int> Fetch(CancellationToken _)
        {
            var n = Interlocked.Increment(ref fetches);
            return n == 1 ? await gate.Task : n;
        }

        var first = load.Load("key", Fetch, applied.Add);
        // Until the first fetch is really running, not a fixed pause — see
        // A_superseded_load_is_cancelled for what a pause did on a busy CI runner.
        await WaitUntil(() => Volatile.Read(ref fetches) == 1);

        // Three sweeps land while the first load is still going. The first is not
        // cancelled — a query slower than the sweep would otherwise never finish — and the
        // three become one more load, not three.
        _ = load.Refresh("key", Fetch, applied.Add);
        _ = load.Refresh("key", Fetch, applied.Add);
        _ = load.Refresh("key", Fetch, applied.Add);

        gate.SetResult(1);
        await first.WaitAsync(Timeout);

        Assert.Equal(2, fetches);
        Assert.Equal([1, 2], applied);
    }

    [Fact]
    public async Task A_failed_load_is_contained_and_reported()
    {
        var component = new FakeComponent();
        using var load = component.Loader();
        var applied = false;

        var task = load.Load<int>("key", _ => throw new InvalidOperationException("database is locked"), _ => applied = true);
        await task.WaitAsync(Timeout);

        Assert.True(task.IsCompletedSuccessfully);
        Assert.False(applied);
        Assert.True(load.Ready);
        Assert.True(load.Failed);
        Assert.Equal(1, component.Redraws);

        // And the next one that works clears it.
        await load.Refresh("key", _ => Task.FromResult(1), _ => applied = true).WaitAsync(Timeout);
        Assert.True(applied);
        Assert.False(load.Failed);
    }

    [Fact]
    public async Task An_apply_that_throws_or_a_dispatcher_that_has_gone_is_contained()
    {
        var component = new FakeComponent();
        using var load = component.Loader();

        var task = load.Load<int>("key", _ => Task.FromResult(1), _ => throw new NullReferenceException());
        await task.WaitAsync(Timeout);
        Assert.True(task.IsCompletedSuccessfully);

        component.Throws = true;
        task = load.Refresh<int>("key", _ => Task.FromResult(1), _ => { });
        await task.WaitAsync(Timeout);
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Nothing_is_applied_or_redrawn_after_dispose()
    {
        var component = new FakeComponent();
        var load = component.Loader();
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = false;

        var task = load.Load("key", _ => gate.Task, _ => applied = true);
        load.Dispose();
        gate.SetResult(1);
        await task.WaitAsync(Timeout);

        Assert.False(applied);
        Assert.Equal(0, component.Redraws);

        // And a load asked for afterwards is not started at all.
        var fetched = false;
        await load.Load("other", _ => { fetched = true; return Task.FromResult(1); }, _ => { }).WaitAsync(Timeout);
        Assert.False(fetched);
    }

    [Fact]
    public async Task Cancel_keeps_an_older_answer_from_arriving()
    {
        var component = new FakeComponent();
        using var load = component.Loader();
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = false;

        var task = load.Load("connection-a", _ => gate.Task, _ => applied = true);
        load.Cancel();
        gate.SetResult(1);
        await task.WaitAsync(Timeout);

        Assert.False(applied);
        Assert.False(load.Loading);
    }

    [Fact]
    public async Task Nothing_is_loaded_while_prerendering()
    {
        var component = new FakeComponent();
        using var load = component.Loader(interactive: false);
        var fetched = false;

        await load.Load("key", _ => { fetched = true; return Task.FromResult(1); }, _ => { }).WaitAsync(Timeout);

        Assert.False(fetched);
        Assert.False(load.Ready);
    }

    [Fact]
    public async Task Offload_never_runs_more_than_its_limit_at_once()
    {
        var offload = new Offload(2, NullLogger.Instance);
        var running = 0;
        var peak = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, 8).Select(i => offload.Run(async _ =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            // Blocking, the way SQLite does, rather than yielding.
            release.Task.Wait(Timeout);
            Interlocked.Decrement(ref running);
            await Task.Yield();
            return i;
        })).ToList();

        await WaitUntil(() => Volatile.Read(ref running) == 2);
        await Task.Delay(100);
        Assert.Equal(2, Volatile.Read(ref running));
        Assert.Equal(0, offload.Available);

        release.SetResult();
        var results = await Task.WhenAll(tasks).WaitAsync(Timeout);

        Assert.Equal(Enumerable.Range(0, 8), results);
        Assert.Equal(2, peak);
        Assert.Equal(2, offload.Available);
    }

    [Fact]
    public async Task Offload_runs_even_the_synchronous_start_off_the_callers_thread()
    {
        var offload = new Offload(1, NullLogger.Instance);
        using var release = new ManualResetEventSlim();

        var task = offload.Run(_ =>
        {
            release.Wait(Timeout);
            return Task.FromResult(1);
        });

        // Had Run started the work inline, this line would only be reached after it.
        Assert.False(task.IsCompleted);
        release.Set();
        Assert.Equal(1, await task.WaitAsync(Timeout));
    }

    [Fact]
    public async Task Offload_cancelled_while_queued_gives_up_its_place()
    {
        var offload = new Offload(1, NullLogger.Instance);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = offload.Run(_ => release.Task);
        await WaitUntil(() => offload.Available == 0);

        using var cancel = new CancellationTokenSource();
        var ran = false;
        var queued = offload.Run(_ => { ran = true; return Task.FromResult(0); }, cancel.Token);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Timeout));
        release.SetResult(1);
        await holder.WaitAsync(Timeout);

        Assert.False(ran);
        Assert.Equal(1, offload.Available);
    }

    [Fact]
    public async Task Offload_inside_offload_does_not_wait_for_itself()
    {
        // One slot, and a read that makes another offloaded read: had the inner one queued
        // for a slot, it would wait on the outer one for ever.
        var offload = new Offload(1, NullLogger.Instance);
        var result = await offload.Run(_ => offload.Run(_ => Task.FromResult(7))).WaitAsync(Timeout);
        Assert.Equal(7, result);
    }

    [Fact]
    public async Task Overlapping_identical_series_share_one_query_and_a_write_ends_the_sharing()
    {
        var directory = TestHost.TempDirectory();
        var services = TestHost.ReadyHost(directory);
        try
        {
            var history = services.GetRequiredService<HistoryStore>();
            var config = services.GetRequiredService<ConfigStore>();
            var connection = new Connection { Provider = "http", Name = "Box" };
            await config.SaveConnectionAsync(connection);
            await history.RecordAsync(connection.Id, new Dictionary<string, double> { ["latency_ms"] = 10 }, CancellationToken.None);

            // One slot, held, so every request below is queued and so overlapping.
            var offload = new Offload(1, NullLogger.Instance);
            using var series = new SharedSeries(history, offload);
            var hold = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var holder = offload.Run(_ => hold.Task);
            await WaitUntil(() => offload.Available == 0);

            var window = TimeSpan.FromHours(1);
            var a = series.SamplesAsync(connection.Id, "latency_ms", window);
            var b = series.SamplesAsync(connection.Id, "latency_ms", window);
            Assert.Equal(1, series.Running);

            // A write while they wait: whatever is running may have read before it, so a
            // request after it gets a query of its own.
            await history.RecordAsync(connection.Id, new Dictionary<string, double> { ["latency_ms"] = 20 }, CancellationToken.None);
            var c = series.SamplesAsync(connection.Id, "latency_ms", window);
            Assert.Equal(1, series.Running);

            hold.SetResult(0);
            await holder.WaitAsync(Timeout);
            var results = await Task.WhenAll(a, b, c).WaitAsync(Timeout);

            Assert.Same(results[0], results[1]);
            Assert.NotSame(results[0], results[2]);
            Assert.Equal(0, series.Running);

            // Finished queries are not kept: the next request asks the database again.
            var later = await series.SamplesAsync(connection.Id, "latency_ms", window).WaitAsync(Timeout);
            Assert.NotSame(results[2], later);
        }
        finally
        {
            TestHost.Teardown(services, directory);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value)
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current)
                return;
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition never became true");
            await Task.Delay(10);
        }
    }
}
