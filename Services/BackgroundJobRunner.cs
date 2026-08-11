using System.Collections.Concurrent;
using System.Diagnostics;
using LabbyTwo.Core;

namespace LabbyTwo.Services;

/// <summary>What happened last time a job ran, so Settings can show it rather than the log alone.</summary>
/// <param name="At">When the run finished. Null means it has not run yet.</param>
public sealed record JobRun(string Name, DateTimeOffset? At, TimeSpan Duration, bool Ok, string Message);

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
            _runs[job.Name] = new JobRun(job.Name, null, TimeSpan.Zero, true, "Not run yet");

        await Task.WhenAll(registered.Select(job => LoopAsync(job, stoppingToken)));
    }

    private async Task LoopAsync(IBackgroundJob job, CancellationToken stoppingToken)
    {
        var interval = job.Interval < Floor ? Floor : job.Interval;

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

    private async Task RunOnceAsync(IBackgroundJob job, CancellationToken stoppingToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await job.RunAsync(stoppingToken);
            stopwatch.Stop();
            _runs[job.Name] = new JobRun(job.Name, DateTimeOffset.Now, stopwatch.Elapsed, true, "OK");
            log.LogDebug("Job {Job} ran in {Ms} ms", job.Name, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down, not a failure.
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var message = ex.GetBaseException().Message;
            _runs[job.Name] = new JobRun(job.Name, DateTimeOffset.Now, stopwatch.Elapsed, false, message);
            log.LogError(ex, "Background job {Job} failed", job.Name);
        }
    }
}
