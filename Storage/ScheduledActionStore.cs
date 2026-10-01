using LabbyTwo.Core;

namespace LabbyTwo.Storage;

/// <summary>
/// Scheduled actions and the short history of what each one did.
///
/// The actions are a handful of rows asked about every minute, so they are read once and
/// kept in memory, written through — the job that runs them does no database read at all on
/// a minute when nothing is due. The history is only read by the page, one action at a time,
/// and trimmed to <see cref="HistoryLength"/> rows per action as each run is written, so it
/// never grows however long the install runs.
/// </summary>
public sealed class ScheduledActionStore(Db db)
{
    /// <summary>Runs kept per action. Enough to see a pattern — "fails every other Sunday" — and no more.</summary>
    public const int HistoryLength = 20;

    // Versioned so a load cannot store actions from before a save that had already
    // invalidated them — see VersionedCache.
    private readonly VersionedCache<List<ScheduledAction>> _cache = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IReadOnlyList<ScheduledAction> _last = [];

    /// <summary>Raised after a save or a delete — not after a run moves <see cref="ScheduledAction.CoveredUntil"/>.</summary>
    public event Action? Changed;

    /// <summary>The actions as last read, without touching the database. Empty before the first read.</summary>
    public IReadOnlyList<ScheduledAction> Current => _cache.Value ?? _last;

    public async Task<IReadOnlyList<ScheduledAction>> AllAsync(CancellationToken ct = default)
    {
        if (_cache.Value is { } cached)
            return cached;

        await _lock.WaitAsync(ct);
        try
        {
            if (_cache.Value is { } loaded)
                return loaded;

            var version = _cache.Version;
            await using var connection = await db.OpenAsync(ct);
            var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT id, name, enabled, target, target_connection_id, container, verb, action_id,
                       schedule_kind, days, times, interval_minutes, day_of_month, cron,
                       run_in_maintenance, allow_protected, notify_on_failure, channel_id, covered_until
                FROM scheduled_actions ORDER BY name, id
                """;
            var list = new List<ScheduledAction>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new ScheduledAction
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    Enabled = reader.GetInt64(2) != 0,
                    Target = ScheduledAction.ParseTarget(reader.GetString(3)),
                    TargetConnectionId = reader.GetString(4),
                    Container = reader.GetString(5),
                    Verb = ScheduledAction.ParseVerb(reader.GetString(6)),
                    ActionId = reader.GetString(7),
                    Schedule = new ActionSchedule
                    {
                        Kind = ActionSchedule.ParseKind(reader.GetString(8)),
                        Days = MuteWindow.ParseDays(reader.GetString(9)),
                        Times = ActionSchedule.ParseTimes(reader.GetString(10)),
                        IntervalMinutes = reader.GetInt32(11),
                        DayOfMonth = reader.GetInt32(12),
                        Cron = reader.GetString(13),
                    },
                    RunInMaintenance = reader.GetInt64(14) != 0,
                    AllowProtected = reader.GetInt64(15) != 0,
                    NotifyOnFailure = reader.GetInt64(16) != 0,
                    ChannelId = reader.GetString(17),
                    CoveredUntil = reader.IsDBNull(18) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(18)),
                });
            }
            _last = list;
            return _cache.Store(list, version);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ScheduledAction?> GetAsync(string id, CancellationToken ct = default) =>
        (await AllAsync(ct)).FirstOrDefault(a => a.Id == id);

    /// <summary>
    /// Saves an action as it is, <see cref="ScheduledAction.CoveredUntil"/> included. The
    /// service sets that to "now" before calling, so an edited schedule starts from the
    /// moment it was saved rather than catching up on runs the old one never had.
    /// </summary>
    public async Task SaveAsync(ScheduledAction action, CancellationToken ct = default)
    {
        var schedule = action.Schedule;
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO scheduled_actions
                (id, name, enabled, target, target_connection_id, container, verb, action_id,
                 schedule_kind, days, times, interval_minutes, day_of_month, cron,
                 run_in_maintenance, allow_protected, notify_on_failure, channel_id, covered_until)
            VALUES ($id, $name, $enabled, $target, $connection, $container, $verb, $action,
                    $kind, $days, $times, $interval, $dom, $cron,
                    $maintenance, $protected, $notify, $channel, $covered)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name, enabled = excluded.enabled, target = excluded.target,
                target_connection_id = excluded.target_connection_id, container = excluded.container,
                verb = excluded.verb, action_id = excluded.action_id, schedule_kind = excluded.schedule_kind,
                days = excluded.days, times = excluded.times, interval_minutes = excluded.interval_minutes,
                day_of_month = excluded.day_of_month, cron = excluded.cron,
                run_in_maintenance = excluded.run_in_maintenance, allow_protected = excluded.allow_protected,
                notify_on_failure = excluded.notify_on_failure, channel_id = excluded.channel_id,
                covered_until = excluded.covered_until
            """;
        cmd.Parameters.AddWithValue("$id", action.Id);
        cmd.Parameters.AddWithValue("$name", action.Name.Trim());
        cmd.Parameters.AddWithValue("$enabled", action.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$target", ScheduledAction.StoredTarget(action.Target));
        cmd.Parameters.AddWithValue("$connection", action.TargetConnectionId);
        cmd.Parameters.AddWithValue("$container", action.Container.Trim());
        cmd.Parameters.AddWithValue("$verb", ScheduledAction.StoredVerb(action.Verb));
        cmd.Parameters.AddWithValue("$action", action.ActionId);
        cmd.Parameters.AddWithValue("$kind", ActionSchedule.StoredKind(schedule.Kind));
        cmd.Parameters.AddWithValue("$days", MuteWindow.StoredDays(schedule.Days));
        cmd.Parameters.AddWithValue("$times", ActionSchedule.StoredTimes(schedule.Times));
        cmd.Parameters.AddWithValue("$interval", schedule.IntervalMinutes);
        cmd.Parameters.AddWithValue("$dom", schedule.DayOfMonth);
        cmd.Parameters.AddWithValue("$cron", schedule.Cron.Trim());
        cmd.Parameters.AddWithValue("$maintenance", action.RunInMaintenance ? 1 : 0);
        cmd.Parameters.AddWithValue("$protected", action.AllowProtected ? 1 : 0);
        cmd.Parameters.AddWithValue("$notify", action.NotifyOnFailure ? 1 : 0);
        cmd.Parameters.AddWithValue("$channel", action.ChannelId);
        cmd.Parameters.AddWithValue("$covered", action.CoveredUntil is { } covered ? covered.ToUnixTimeSeconds() : DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
        await ReloadAsync(ct);
    }

    /// <summary>The action and its history, both.</summary>
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM scheduled_actions WHERE id = $id;
            DELETE FROM scheduled_runs WHERE action_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
        await ReloadAsync(ct);
    }

    /// <summary>
    /// Moves one action's <see cref="ScheduledAction.CoveredUntil"/> on in the database,
    /// without announcing a change or touching the cached list: nothing about the action a
    /// person set up has changed, and a page redrawing every minute for it would be noise.
    /// The service that calls this keeps the newer value in memory itself, and a cached copy
    /// that lags is only ever older — which the service resolves by taking the later of the two.
    /// </summary>
    public async Task SetCoveredAsync(string id, DateTimeOffset until, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = SetCoveredSql;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$covered", until.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The update behind <see cref="SetCoveredAsync"/>; public for the query-plan test.</summary>
    public const string SetCoveredSql = "UPDATE scheduled_actions SET covered_until = $covered WHERE id = $id";

    private async Task ReloadAsync(CancellationToken ct)
    {
        _cache.Invalidate();
        await AllAsync(ct);
        Changed?.Invoke();
    }

    // ---- history --------------------------------------------------------------------

    /// <summary>The read behind <see cref="RunsAsync"/>: a backwards walk of one action's range of the index.</summary>
    public const string RunsSql = """
        SELECT id, action_id, ts, trigger, outcome, message, duration_ms FROM scheduled_runs
        WHERE action_id = $action ORDER BY id DESC LIMIT $limit
        """;

    /// <summary>
    /// The trim after each insert: everything of this action's older than its newest
    /// <see cref="HistoryLength"/>. Both halves are ranges on the one index.
    /// </summary>
    public const string TrimSql = """
        DELETE FROM scheduled_runs WHERE action_id = $action AND id < (
            SELECT MIN(id) FROM (
                SELECT id FROM scheduled_runs WHERE action_id = $action ORDER BY id DESC LIMIT $keep))
        """;

    /// <summary>Writes one run and trims the action's history to its newest <see cref="HistoryLength"/>.</summary>
    public async Task<ScheduledRun> RecordRunAsync(ScheduledRun run, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var transaction = connection.BeginTransaction();
        var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO scheduled_runs (action_id, ts, trigger, outcome, message, duration_ms)
            VALUES ($action, $ts, $trigger, $outcome, $message, $duration)
            RETURNING id
            """;
        insert.Parameters.AddWithValue("$action", run.ActionId);
        insert.Parameters.AddWithValue("$ts", run.At.ToUnixTimeSeconds());
        insert.Parameters.AddWithValue("$trigger", run.Trigger);
        insert.Parameters.AddWithValue("$outcome", run.Outcome);
        insert.Parameters.AddWithValue("$message", run.Message);
        insert.Parameters.AddWithValue("$duration", (long)run.Duration.TotalMilliseconds);
        var id = Convert.ToInt64(await insert.ExecuteScalarAsync(ct));

        var trim = connection.CreateCommand();
        trim.Transaction = transaction;
        trim.CommandText = TrimSql;
        trim.Parameters.AddWithValue("$action", run.ActionId);
        trim.Parameters.AddWithValue("$keep", HistoryLength);
        await trim.ExecuteNonQueryAsync(ct);

        await transaction.CommitAsync(ct);
        return run with { Id = id };
    }

    /// <summary>One action's runs, newest first.</summary>
    public async Task<IReadOnlyList<ScheduledRun>> RunsAsync(string actionId, int limit = HistoryLength, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = RunsSql;
        cmd.Parameters.AddWithValue("$action", actionId);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, HistoryLength));
        var list = new List<ScheduledRun>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new ScheduledRun(
                reader.GetString(1),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)).ToLocalTime(),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                TimeSpan.FromMilliseconds(reader.GetInt64(6)))
            {
                Id = reader.GetInt64(0),
            });
        }
        return list;
    }
}
