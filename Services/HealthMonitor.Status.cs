using System.Collections.Concurrent;
using LabbyTwo.Core;

namespace LabbyTwo.Services;

/// <summary>How the one-off restore at startup went.</summary>
public enum RestoreOutcome
{
    /// <summary>The monitor has not got as far as trying.</summary>
    Pending,

    Running,
    Completed,

    /// <summary>Gave up at <see cref="HealthMonitor.RestoreDeadline"/> and started monitoring without it.</summary>
    TimedOut,

    Failed,
}

/// <summary>A probe that has been asked and has not answered yet.</summary>
public sealed record ProbeInFlight(string ConnectionId, DateTimeOffset Since);

/// <summary>One connection's most recent probe, for the "slowest" list.</summary>
public sealed record ProbeTiming(string ConnectionId, TimeSpan Duration, DateTimeOffset At);

/// <summary>
/// What the monitor itself has been doing, as opposed to what it found. Immutable and
/// copied out, so a page can hold one across renders without seeing it change underneath.
/// </summary>
/// <param name="StartedAt">When the background loop began. Null means it never has.</param>
/// <param name="SweepPeriod">How often a sweep is meant to start.</param>
/// <param name="CurrentSweepStarted">Set while a sweep is running.</param>
/// <param name="LastSweepProbed">How many connections the last sweep asked; the rest were not due.</param>
public sealed record MonitorStatus(
    DateTimeOffset? StartedAt,
    TimeSpan SweepPeriod,
    RestoreOutcome Restore,
    DateTimeOffset? RestoreStarted,
    TimeSpan? RestoreDuration,
    string? RestoreError,
    int SweepsCompleted,
    DateTimeOffset? CurrentSweepStarted,
    DateTimeOffset? LastSweepStarted,
    DateTimeOffset? LastSweepFinished,
    TimeSpan? LastSweepDuration,
    int LastSweepProbed,
    string? LastSweepError,
    IReadOnlyList<ProbeInFlight> InFlight,
    IReadOnlyList<ProbeTiming> Slowest)
{
    /// <summary>Whether LabbyTwo can see the lab, as of the last sweep. Not positional, so a test that builds one without it still does.</summary>
    public Blindness Blindness { get; init; } = Blindness.Clear;

    /// <summary>
    /// How long each of the last few sweeps took, oldest first — up to
    /// <see cref="HealthMonitor.SweepsRemembered"/>. One slow sweep is a slow host; half of
    /// them running past the period is the monitor falling behind, and only a run of them
    /// can tell the two apart.
    /// </summary>
    public IReadOnlyList<TimeSpan> RecentSweeps { get; init; } = [];

    /// <summary>How long the sweep in progress has been going, or null if none is.</summary>
    public TimeSpan? RunningFor(DateTimeOffset now) => CurrentSweepStarted is { } started ? now - started : null;

    /// <summary>
    /// The red flag. Probes have their own deadline, so a healthy sweep is over well inside
    /// one period; one that has run for two means something below the probes — a database
    /// write, a status handler — is holding it, and every tile is frozen where it was.
    /// </summary>
    public bool IsSweepStuck(DateTimeOffset now) => RunningFor(now) is { } running && running > SweepPeriod * 2;
}

public sealed partial class HealthMonitor
{
    // One small lock for a handful of timestamps. Sweeps are seconds apart, so contention
    // is nil, and a lock is what makes the snapshot consistent — a start time from one
    // sweep beside a duration from another would describe a sweep that never happened.
    private readonly Lock _bookkeeping = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _inFlight = new();

    private DateTimeOffset? _startedAt;
    private RestoreOutcome _restore = RestoreOutcome.Pending;
    private DateTimeOffset? _restoreStarted;
    private TimeSpan? _restoreDuration;
    private string? _restoreError;
    private int _sweeps;
    private DateTimeOffset? _currentSweep;
    private DateTimeOffset? _lastSweepStarted;
    private DateTimeOffset? _lastSweepFinished;
    private TimeSpan? _lastSweepDuration;
    private int _pendingProbed;
    private int _lastSweepProbed;
    private string? _lastSweepError;
    private readonly Queue<TimeSpan> _recentSweeps = new();

    /// <summary>How many sweep durations <see cref="MonitorStatus.RecentSweeps"/> keeps.</summary>
    public const int SweepsRemembered = 10;

    /// <summary>
    /// What the monitor has been doing. Built from memory only, so it is safe to read on
    /// every render and from an endpoint that must answer while the database is stuck.
    /// </summary>
    public MonitorStatus Status
    {
        get
        {
            var now = DateTimeOffset.Now;
            var period = TimeSpan.FromSeconds(Math.Clamp(options.Value.ProbeSeconds, 5, 3600));
            IReadOnlyList<ProbeInFlight> inFlight =
                [.. _inFlight.Select(pair => new ProbeInFlight(pair.Key, pair.Value)).OrderBy(p => p.Since)];

            // Restored states carry a zero duration and were never measured, so they would
            // only pad the bottom of the list.
            IReadOnlyList<ProbeTiming> slowest =
            [
                .. _states.Values
                    .Where(state => state.Duration > TimeSpan.Zero)
                    .OrderByDescending(state => state.Duration)
                    .Take(8)
                    .Select(state => new ProbeTiming(state.ConnectionId, state.Duration, state.At)),
            ];

            lock (_bookkeeping)
            {
                return new MonitorStatus(
                    _startedAt, period,
                    _restore, _restoreStarted,
                    _restore == RestoreOutcome.Running && _restoreStarted is { } began ? now - began : _restoreDuration,
                    _restoreError,
                    _sweeps, _currentSweep, _lastSweepStarted, _lastSweepFinished, _lastSweepDuration,
                    _lastSweepProbed, _lastSweepError, inFlight, slowest)
                {
                    Blindness = Blindness,
                    RecentSweeps = [.. _recentSweeps],
                };
            }
        }
    }

    private void NoteStarted()
    {
        lock (_bookkeeping)
        {
            _startedAt = DateTimeOffset.Now;
            _restoreStarted = _startedAt;
            _restore = RestoreOutcome.Running;
        }
    }

    private void NoteRestored(RestoreOutcome outcome, string? error)
    {
        lock (_bookkeeping)
        {
            _restore = outcome;
            _restoreError = error;
            if (_restoreStarted is { } began)
                _restoreDuration = DateTimeOffset.Now - began;
        }
    }

    private void NoteSweepSize(int due) => Volatile.Write(ref _pendingProbed, due);

    /// <summary>
    /// A sweep with its start and finish written down. The start is recorded before any
    /// work so that a sweep which never returns is visible as one that is still running,
    /// rather than indistinguishable from one that never began.
    /// </summary>
    private async Task TimedSweepAsync(CancellationToken ct)
    {
        var started = DateTimeOffset.Now;
        lock (_bookkeeping)
            _currentSweep = started;
        Volatile.Write(ref _pendingProbed, 0);

        string? error = null;
        try
        {
            await SweepAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.GetBaseException().Message;
            throw;
        }
        finally
        {
            var finished = DateTimeOffset.Now;
            lock (_bookkeeping)
            {
                _currentSweep = null;
                _lastSweepStarted = started;
                _lastSweepFinished = finished;
                _lastSweepDuration = finished - started;
                _lastSweepProbed = Volatile.Read(ref _pendingProbed);
                _lastSweepError = error;
                _sweeps++;
                _recentSweeps.Enqueue(finished - started);
                while (_recentSweeps.Count > SweepsRemembered)
                    _recentSweeps.Dequeue();
            }
        }
    }

    /// <summary>
    /// The probe, with a note of who is being waited on. When a sweep is stuck, "which
    /// one" is the next question, and this answers it without a stack dump.
    /// </summary>
    private async Task<(Connection Connection, ProbeResult Result, ProbeFailure Failure)> TrackedProbeAsync(
        Connection connection, CancellationToken ct)
    {
        _inFlight[connection.Id] = DateTimeOffset.Now;
        try
        {
            var (result, failure) = await ProbeClassifiedAsync(connection, ct);
            return (connection, result, failure);
        }
        finally
        {
            _inFlight.TryRemove(connection.Id, out _);
        }
    }
}
