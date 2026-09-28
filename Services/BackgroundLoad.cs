namespace LabbyTwo.Services;

/// <summary>
/// One component's "go and get this, and draw it when it comes", done so the component
/// never waits for the database while it is being drawn.
///
/// A card used to load its history from OnParametersSetAsync. Because SQLite completes
/// synchronously, that await never yielded: the circuit sat inside the query, and neither
/// that card nor any other on the page drew until it came back. Here the load is started
/// and not awaited by the lifecycle method, so the card renders straight away with
/// whatever it had (its loading state, the first time) and redraws when the answer lands.
///
/// Not awaiting it is what makes a handful of things this class's job rather than the
/// framework's:
/// <list type="bullet">
/// <item>Staleness. Parameters can change while a load is running — the weather tab's
/// range buttons, a widget edited in place — and the older answer must not overwrite the
/// newer one when it happens to arrive second. Each load is numbered and only the latest
/// is applied; the older one is cancelled.</item>
/// <item>Exceptions. A fault after the first render reaches no error boundary; unobserved
/// it is lost, and thrown from a render callback it ends the circuit. They are logged
/// here, the card keeps whatever it last showed, and <see cref="Failed"/> says so.</item>
/// <item>Disposal. A load that finishes after the card has gone must not ask a disposed
/// component to render.</item>
/// <item>Repetition. Parents re-render on every sweep and hand the card its parameters
/// again. A load keyed the same as the last one is not started again; a refresh asked
/// for while one is running is run once, after it, rather than queued up behind it or
/// used to cancel it — cancelling would mean a query slower than the sweep interval never
/// finished at all.</item>
/// </list>
///
/// The fetch runs on the thread pool, so it must capture what it needs (ids, windows)
/// before it starts rather than reading the component's parameters. The apply step runs
/// on the component's own dispatcher, so it can set fields freely.
/// </summary>
public sealed class BackgroundLoad : IDisposable
{
    private readonly Func<Action, Task> _invoke;
    private readonly Action _redraw;
    private readonly ILogger _log;
    private readonly bool _interactive;
    private readonly object _sync = new();

    private object? _key;
    private bool _hasKey;
    private int _version;
    private bool _running;
    private Func<Task>? _again;
    private CancellationTokenSource? _cancel;
    private Task _current = Task.CompletedTask;
    private bool _disposed;

    /// <param name="invoke">The component's InvokeAsync, to get back onto its dispatcher.</param>
    /// <param name="redraw">The component's StateHasChanged.</param>
    /// <param name="log">Where failed loads are reported.</param>
    /// <param name="interactive">
    /// False while the component is being prerendered. Prerendering waits for nothing
    /// this class starts, and the component it would fill is thrown away when the circuit
    /// starts and loads for itself — so a prerender load is a query nobody sees. Skipped;
    /// the prerendered page shows the loading state instead.
    /// </param>
    public BackgroundLoad(Func<Action, Task> invoke, Action redraw, ILogger log, bool interactive = true)
    {
        _invoke = invoke;
        _redraw = redraw;
        _log = log;
        _interactive = interactive;
    }

    /// <summary>
    /// True once a load has finished, whether or not it worked. Until then a card shows
    /// "loading" rather than "nothing recorded", which would be a claim it cannot make yet.
    /// </summary>
    public bool Ready { get; private set; }

    /// <summary>True when the most recent load threw. Cleared by the next one that works.</summary>
    public bool Failed { get; private set; }

    /// <summary>True while a load is running.</summary>
    public bool Loading
    {
        get
        {
            lock (_sync)
                return _running;
        }
    }

    /// <summary>
    /// Starts loading <paramref name="key"/>, unless the last load started was for the
    /// same key — then it is either running or done, and either way its answer stands.
    /// Returns a task that completes when this load has been applied or dropped; it never
    /// faults, and components normally discard it.
    /// </summary>
    public Task Load<T>(object key, Func<CancellationToken, Task<T>> fetch, Action<T> apply) =>
        Start(key, fetch, apply, refresh: false);

    /// <summary>
    /// Loads <paramref name="key"/> again even if it is what is showing — new data has
    /// been recorded. If a load of the same key is running, one more runs after it.
    /// </summary>
    public Task Refresh<T>(object key, Func<CancellationToken, Task<T>> fetch, Action<T> apply) =>
        Start(key, fetch, apply, refresh: true);

    private Task Start<T>(object key, Func<CancellationToken, Task<T>> fetch, Action<T> apply, bool refresh)
    {
        lock (_sync)
        {
            if (_disposed || !_interactive)
                return Task.CompletedTask;

            var same = _hasKey && Equals(_key, key);
            if (same && !refresh)
                return _current;

            if (same && _running)
            {
                // The running load may have read the database before whatever prompted
                // this was written, so its answer is not enough — but cutting it off
                // would throw away most of the work. One more, after it; the latest
                // request's delegates win, since they carry the latest parameters.
                _again = () => Start(key, fetch, apply, refresh: true);
                return _current;
            }

            // Cancelled, not disposed: the superseded load may still be looking at its
            // token, and a source with no timer holds nothing that needs releasing.
            _cancel?.Cancel();
            _cancel = new CancellationTokenSource();

            _key = key;
            _hasKey = true;
            _running = true;
            _again = null;
            var version = ++_version;
            return _current = RunAsync(version, _cancel.Token, fetch, apply);
        }
    }

    private async Task RunAsync<T>(int version, CancellationToken ct, Func<CancellationToken, Task<T>> fetch, Action<T> apply)
    {
        T result = default!;
        Exception? failure = null;
        try
        {
            // Task.Run even though the fetch is async, because its first stretch — up to
            // the first await that really yields — would otherwise run right here, and
            // with SQLite that first stretch is the query.
            result = await Task.Run(() => fetch(ct), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Superseded or disposed. Whoever cancelled it owns the state now.
            return;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        Func<Task>? again;
        lock (_sync)
        {
            if (_disposed || version != _version)
                return;
            _running = false;
            again = _again;
            _again = null;
        }

        try
        {
            await _invoke(() =>
            {
                // Checked again on the dispatcher: a newer load or a disposal can have
                // landed between the check above and this running.
                lock (_sync)
                {
                    if (_disposed || version != _version)
                        return;
                }

                if (failure is null)
                {
                    apply(result);
                    Failed = false;
                }
                else
                {
                    _log.LogWarning(failure, "A background load failed; the card keeps what it had");
                    Failed = true;
                }

                Ready = true;
                _redraw();
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The apply step threw, or the circuit went away between the check and the
            // redraw. Either way it is one card's problem, and there is nobody to throw to.
            _log.LogDebug(ex, "Could not apply a background load");
        }

        if (again is not null)
            await again().ConfigureAwait(false);
    }

    /// <summary>
    /// Drops whatever is running and forgets the key, for when there is nothing to load
    /// any more — a form whose connection was cleared must not have the previous
    /// connection's answer arrive and fill it in.
    /// </summary>
    public void Cancel()
    {
        lock (_sync)
        {
            _cancel?.Cancel();
            _cancel = null;
            _again = null;
            _hasKey = false;
            _key = null;
            _running = false;
            _version++;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _again = null;
            _cancel?.Cancel();
            _cancel = null;
        }
    }
}
