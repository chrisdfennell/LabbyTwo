using System.Collections.Concurrent;
using System.Diagnostics;
using LabbyTwo.Core;

namespace LabbyTwo.Services;

/// <summary>What happened last time a job ran, so Settings can show it rather than the log alone.</summary>
/// <param name="At">When the run finished. Null means it has not run yet.</param>
public sealed record JobRun(string Name, DateTimeOffset? At, TimeSpan Duration, bool Ok, string Message)
{
    /// <summary>How often it runs, after the one-minute floor.</summary>
    public TimeSpan Interval { get; init; }

    /// <summary>When the next run is due. Null while one is running, or before the runner has started.</summary>
    public DateTimeOffset? NextDue { get; init; }

    /// <summary>Set while a run is in progress, so a job that never returns shows as one rather than as idle.</summary>
    public DateTimeOffset? RunningSince { get; init; }

    /// <summary>
    /// The most recent failure, kept after a later success. A job that fails every other
    /// night otherwise looks fine every morning.
    /// </summary>
    public string? LastError { get; init; }

    public DateTimeOffset? LastErrorAt { get; init; }

    /// <summary>
    /// Runs in a row that have failed, back to zero on the first that works. What tells a job
    /// that is broken from one that met a network blip once.
    /// </summary>
    public int ConsecutiveFailures { get; init; }
}

/// <summary>
/// Runs every <see cref="IBackgroundJob"/> on its own schedule. One hosted service for all
/// of them, because the alternative — each plugin registering its own — is how a plugin
/// gets to hang startup or take the process down with an unobserved exception.
///
/// Each job runs in its own loop, so a slow one delays only itself, and every run is
/// wrapped: a throw is recorded and the job is tried again next interval rather than
/// killing the loop.
/// </summary>
public sealed class BackgroundJobRunner(
    IEnumerable<IBackgroundJob> jobs, ILogger<BackgroundJobRunner> log) : BackgroundService
{
    private static readonly TimeSpan Floor = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, JobRun> _runs = new();

    /// <summary>The last outcome of each job, newest first, for the Settings page.</summary>
    public IReadOnlyList<JobRun> Runs =>
        [.. _runs.Values.OrderByDescending(run => run.At ?? DateTimeOffset.MinValue)];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var registered = jobs.ToList();
        if (registered.Count == 0)
            return;

        log.LogInformation("Running {Count} background job(s): {Names}",
            registered.Count, string.Join(", ", registered.Select(job => job.Name)));

        foreach (var job in registered)
        {
            var interval = IntervalOf(job);
            _runs[job.Name] = new JobRun(job.Name, null, TimeSpan.Zero, true, "Not run yet")
            {
                Interval = interval,
                NextDue = job.RunAtStartup ? DateTimeOffset.Now : DateTimeOffset.Now + interval,
            };
        }

        await Task.WhenAll(registered.Select(job => LoopAsync(job, stoppingToken)));
    }

    private async Task LoopAsync(IBackgroundJob job, CancellationToken stoppingToken)
    {
        var interval = IntervalOf(job);

        // Yield first: without it, a job whose RunAtStartup is true would run inline here
        // and hold up every other job's first tick.
        await Task.Yield();

        if (!job.RunAtStartup)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(job, stoppingToken);
            Update(job, run => run with { NextDue = DateTimeOffset.Now + interval });

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static TimeSpan IntervalOf(IBackgroundJob job) => job.Interval < Floor ? Floor : job.Interval;

    /// <summary>
    /// Swaps a job's record for a changed copy. Records rather than mutable fields, so a
    /// page reading one mid-run never sees a finish time from this run beside an error
    /// from the last.
    /// </summary>
    private void Update(IBackgroundJob job, Func<JobRun, JobRun> change) =>
        _runs.AddOrUpdate(job.Name,
            _ => change(new JobRun(job.Name, null, TimeSpan.Zero, true, "Not run yet") { Interval = IntervalOf(job) }),
            (_, run) => change(run));

    private async Task RunOnceAsync(IBackgroundJob job, CancellationToken stoppingToken)
    {
        Update(job, run => run with { RunningSince = DateTimeOffset.Now, NextDue = null });
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await job.RunAsync(stoppingToken);
            stopwatch.Stop();
            Update(job, run => run with
            {
                At = DateTimeOffset.Now, Duration = stopwatch.Elapsed, Ok = true, Message = "OK", RunningSince = null,
                ConsecutiveFailures = 0,
            });
            log.LogDebug("Job {Job} ran in {Ms} ms", job.Name, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down, not a failure.
            Update(job, run => run with { RunningSince = null });
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var message = ex.GetBaseException().Message;
            var now = DateTimeOffset.Now;
            Update(job, run => run with
            {
                At = now, Duration = stopwatch.Elapsed, Ok = false, Message = message, RunningSince = null,
                LastError = message, LastErrorAt = now, ConsecutiveFailures = run.ConsecutiveFailures + 1,
            });
            log.LogError(ex, "Background job {Job} failed", job.Name);
        }
    }
}
