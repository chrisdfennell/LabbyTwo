namespace LabbyTwo.Storage;

/// <summary>How history writes have been going lately. See <see cref="WriteHealth"/>.</summary>
/// <param name="FailedMinutes">Of the last ten minutes, how many had at least one write fail.</param>
/// <param name="Failures">Writes that failed in the last ten minutes.</param>
/// <param name="Succeeded">Writes that worked in the last ten minutes.</param>
/// <param name="Dropped">Readings not written since startup because the database was being compacted.</param>
public sealed record WriteHealthStatus(
    int FailedMinutes,
    int Failures,
    int Succeeded,
    string? LastError,
    DateTimeOffset? LastFailureAt,
    DateTimeOffset? LastSuccessAt,
    long Dropped,
    bool Compacting)
{
    public static readonly WriteHealthStatus Unknown = new(0, 0, 0, null, null, null, 0, false);
}

/// <summary>
/// Whether the monitor's writes to history are getting through — the per-sweep write, and
/// the one that fails first when something holds the database ("database is locked").
///
/// Kept in memory, in one-minute buckets, so that LabbyTwo can say its database is refusing
/// writes without asking the database. The monitor already logs each failure and carries on;
/// what nobody could see was that it had been failing for twenty minutes.
/// </summary>
public sealed class WriteHealth
{
    /// <summary>How far back the buckets go. Ten minutes are reported; the rest is slack for the clock.</summary>
    private const int Minutes = 15;

    private readonly Lock _gate = new();
    private readonly (long Minute, int Ok, int Failed)[] _buckets = new (long, int, int)[Minutes];
    private string? _lastError;
    private DateTimeOffset? _lastFailure;
    private DateTimeOffset? _lastSuccess;
    private long _dropped;
    private volatile bool _compacting;

    /// <summary>Set while a full compaction holds the database, when writes are skipped rather than queued.</summary>
    public bool Compacting
    {
        get => _compacting;
        set => _compacting = value;
    }

    public void Succeeded(DateTimeOffset now)
    {
        lock (_gate)
        {
            Bucket(now).Ok++;
            _lastSuccess = now;
        }
    }

    public void Failed(DateTimeOffset now, Exception error)
    {
        lock (_gate)
        {
            Bucket(now).Failed++;
            _lastFailure = now;
            _lastError = error.GetBaseException().Message;
        }
    }

    public void Dropped(int readings) => Interlocked.Add(ref _dropped, readings);

    public WriteHealthStatus Status(DateTimeOffset now)
    {
        var minute = now.ToUnixTimeSeconds() / 60;
        lock (_gate)
        {
            int failedMinutes = 0, failures = 0, ok = 0;
            foreach (var bucket in _buckets)
            {
                if (bucket.Minute <= minute - 10 || bucket.Minute > minute)
                    continue;
                failures += bucket.Failed;
                ok += bucket.Ok;
                if (bucket.Failed > 0)
                    failedMinutes++;
            }
            return new WriteHealthStatus(failedMinutes, failures, ok, _lastError, _lastFailure, _lastSuccess,
                Interlocked.Read(ref _dropped), _compacting);
        }
    }

    private ref (long Minute, int Ok, int Failed) Bucket(DateTimeOffset now)
    {
        var minute = now.ToUnixTimeSeconds() / 60;
        ref var bucket = ref _buckets[(int)(((minute % Minutes) + Minutes) % Minutes)];
        if (bucket.Minute != minute)
            bucket = (minute, 0, 0);
        return ref bucket;
    }
}
