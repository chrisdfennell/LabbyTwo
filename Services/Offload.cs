namespace LabbyTwo.Services;

/// <summary>
/// Runs a database read on the thread pool, a few at a time, so that a page asking for
/// history never does the work on the thread that draws it.
///
/// Microsoft.Data.Sqlite's async methods are synchronous underneath: awaiting
/// <see cref="Storage.HistoryStore.SamplesAsync"/> from a component runs the whole query
/// on the circuit's render thread, and the await only "completes" once the rows are in.
/// Nothing on that circuit draws in the meantime — which on a large database was a
/// dashboard that never appeared. Going through <see cref="Task.Run(Func{Task})"/> makes the
/// await genuinely asynchronous, so the page draws its loading state first and fills in.
///
/// The gate is the other half. A dashboard of forty chart cards would otherwise start
/// forty SQLite scans at once, every one holding a pool thread and a connection, all
/// competing with the monitor's own writes. Four at a time keeps a busy page moving
/// without flooding either. The latest-readings warm-up has its own gate
/// (<see cref="LatestReadings"/>), so a queue of charts can never hold up the numbers.
/// </summary>
public sealed class Offload
{
    public const int DefaultConcurrency = 4;

    private readonly SemaphoreSlim _gate;
    private readonly ILogger _log;

    // Set for the duration of gated work. A read that itself calls Run — a series helper
    // used from inside another offloaded read — would otherwise wait for a slot while
    // holding one, and four of those at once would wait on each other for ever.
    private static readonly AsyncLocal<Offload?> Inside = new();

    public Offload(ILogger<Offload> log) : this(DefaultConcurrency, log)
    {
    }

    public Offload(int concurrency, ILogger log)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);
        _gate = new SemaphoreSlim(concurrency, concurrency);
        _log = log;
    }

    /// <summary>How many more reads could start right now. For tests and diagnostics.</summary>
    public int Available => _gate.CurrentCount;

    /// <summary>
    /// Runs <paramref name="work"/> on the thread pool once a slot is free. Never runs any
    /// of it on the caller's thread, even when a slot is free straight away: that is the
    /// entire point. Cancelling while still queued gives the slot to nobody and throws
    /// <see cref="OperationCanceledException"/>; exceptions from the work come back
    /// through the task as they would from a direct call.
    /// </summary>
    public Task<T> Run<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default)
    {
        if (ReferenceEquals(Inside.Value, this))
            return work(ct);

        return Task.Run(async () =>
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                Inside.Value = this;
                return await work(ct).ConfigureAwait(false);
            }
            finally
            {
                Inside.Value = null;
                _gate.Release();
            }
        }, ct);
    }

    /// <summary>
    /// A loader for one part of one component. See <see cref="BackgroundLoad"/>; the
    /// arguments are the component's own <c>InvokeAsync</c>, <c>StateHasChanged</c> and
    /// <c>RendererInfo.IsInteractive</c>.
    /// </summary>
    public BackgroundLoad Loader(Func<Action, Task> invoke, Action redraw, bool interactive = true) =>
        new(invoke, redraw, _log, interactive);
}
