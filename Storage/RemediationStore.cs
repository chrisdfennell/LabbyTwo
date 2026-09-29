using System.Collections.Concurrent;
using LabbyTwo.Core;

namespace LabbyTwo.Storage;

/// <summary>
/// Self-healing's two tables: the remediations people set up, one per alert rule or
/// connection, and what each has done about the alert it is answering (see
/// <see cref="RemediationState"/>).
///
/// Both are small — a remediation per thing somebody bothered to automate, a state row per
/// thing currently broken — and both are asked about after every sweep, so each is read
/// once and then kept in memory, written through.
/// </summary>
public sealed class RemediationStore(Db db)
{
    // Versioned so a load cannot store remediations from before a save that had already
    // invalidated them — see VersionedCache.
    private readonly VersionedCache<List<Remediation>> _cache = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IReadOnlyList<Remediation> _last = [];

    private readonly ConcurrentDictionary<string, RemediationState> _states = new();
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private volatile bool _statesLoaded;

    public event Action? Changed;

    /// <summary>The remediations as last read, without touching the database. Empty before the first read.</summary>
    public IReadOnlyList<Remediation> Current => _cache.Value ?? _last;

    public async Task<IReadOnlyList<Remediation>> AllAsync(CancellationToken ct = default)
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
                SELECT trigger, enabled, after_minutes, kind, target_connection_id, container, action_id,
                       max_attempts, cooldown_minutes, check_minutes, allow_protected, if_not_fixed
                FROM remediations
                """;
            var list = new List<Remediation>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new Remediation
                {
                    Trigger = reader.GetString(0),
                    Enabled = reader.GetInt64(1) != 0,
                    AfterMinutes = reader.GetInt32(2),
                    Kind = reader.GetString(3) == "action" ? RemediationKind.ProviderAction : RemediationKind.RestartContainer,
                    TargetConnectionId = reader.GetString(4),
                    Container = reader.GetString(5),
                    ActionId = reader.GetString(6),
                    MaxAttempts = reader.GetInt32(7),
                    CooldownMinutes = reader.GetInt32(8),
                    CheckAfterMinutes = reader.GetInt32(9),
                    AllowProtected = reader.GetInt64(10) != 0,
                    IfNotFixed = reader.GetString(11) == "escalate" ? IfNotFixed.Escalate : IfNotFixed.Notify,
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

    /// <summary>The remediation for one trigger, or null when there is none.</summary>
    public async Task<Remediation?> GetAsync(string trigger, CancellationToken ct = default) =>
        (await AllAsync(ct)).FirstOrDefault(r => r.Trigger == trigger);

    public async Task SaveAsync(Remediation remediation, CancellationToken ct = default)
    {
        var value = remediation.Normalised();
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO remediations
                (trigger, enabled, after_minutes, kind, target_connection_id, container, action_id,
                 max_attempts, cooldown_minutes, check_minutes, allow_protected, if_not_fixed)
            VALUES ($trigger, $enabled, $after, $kind, $target, $container, $action, $max, $cooldown, $check, $protected, $then)
            ON CONFLICT(trigger) DO UPDATE SET
                enabled = excluded.enabled, after_minutes = excluded.after_minutes, kind = excluded.kind,
                target_connection_id = excluded.target_connection_id, container = excluded.container,
                action_id = excluded.action_id, max_attempts = excluded.max_attempts,
                cooldown_minutes = excluded.cooldown_minutes, check_minutes = excluded.check_minutes,
                allow_protected = excluded.allow_protected, if_not_fixed = excluded.if_not_fixed
            """;
        cmd.Parameters.AddWithValue("$trigger", value.Trigger);
        cmd.Parameters.AddWithValue("$enabled", value.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$after", value.AfterMinutes);
        cmd.Parameters.AddWithValue("$kind", value.Kind == RemediationKind.ProviderAction ? "action" : "restart");
        cmd.Parameters.AddWithValue("$target", value.TargetConnectionId);
        cmd.Parameters.AddWithValue("$container", value.Container);
        cmd.Parameters.AddWithValue("$action", value.ActionId);
        cmd.Parameters.AddWithValue("$max", value.MaxAttempts);
        cmd.Parameters.AddWithValue("$cooldown", value.CooldownMinutes);
        cmd.Parameters.AddWithValue("$check", value.CheckAfterMinutes);
        cmd.Parameters.AddWithValue("$protected", value.AllowProtected ? 1 : 0);
        cmd.Parameters.AddWithValue("$then", value.IfNotFixed == IfNotFixed.Escalate ? "escalate" : "notify");
        await cmd.ExecuteNonQueryAsync(ct);
        await ReloadAsync(ct);
    }

    public async Task DeleteAsync(string trigger, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM remediations WHERE trigger = $trigger";
        cmd.Parameters.AddWithValue("$trigger", trigger);
        await cmd.ExecuteNonQueryAsync(ct);
        await ReloadAsync(ct);
    }

    private async Task ReloadAsync(CancellationToken ct)
    {
        _cache.Invalidate();
        await AllAsync(ct);
        Changed?.Invoke();
    }

    // ---- state --------------------------------------------------------------------

    /// <summary>Every state, from memory. Empty until <see cref="LoadStatesAsync"/> has run.</summary>
    public IReadOnlyCollection<RemediationState> States => [.. _states.Values];

    public RemediationState? State(string alertKey) => _states.TryGetValue(alertKey, out var state) ? state : null;

    /// <summary>Reads the state table the first time it is asked, and never again.</summary>
    public async Task LoadStatesAsync(CancellationToken ct = default)
    {
        if (_statesLoaded)
            return;

        await _stateLock.WaitAsync(ct);
        try
        {
            if (_statesLoaded)
                return;

            await using var connection = await db.OpenAsync(ct);
            var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT alert_key, episode_start, attempts, last_run, check_at, did, outcome, note, gave_up
                FROM remediation_state
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var state = new RemediationState(
                    reader.GetString(0),
                    DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)),
                    reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)),
                    reader.IsDBNull(4) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4)),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.GetInt64(8) != 0);
                // TryAdd: anything written in memory before this finished is newer.
                _states.TryAdd(state.AlertKey, state);
            }
            _statesLoaded = true;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task SaveStateAsync(RemediationState state, CancellationToken ct = default)
    {
        _states[state.AlertKey] = state;

        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO remediation_state (alert_key, episode_start, attempts, last_run, check_at, did, outcome, note, gave_up)
            VALUES ($key, $start, $attempts, $last, $check, $did, $outcome, $note, $gaveup)
            ON CONFLICT(alert_key) DO UPDATE SET
                episode_start = excluded.episode_start, attempts = excluded.attempts, last_run = excluded.last_run,
                check_at = excluded.check_at, did = excluded.did, outcome = excluded.outcome, note = excluded.note,
                gave_up = excluded.gave_up
            """;
        cmd.Parameters.AddWithValue("$key", state.AlertKey);
        cmd.Parameters.AddWithValue("$start", state.EpisodeStart.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$attempts", state.Attempts);
        cmd.Parameters.AddWithValue("$last", Nullable(state.LastRunAt));
        cmd.Parameters.AddWithValue("$check", Nullable(state.CheckAt));
        cmd.Parameters.AddWithValue("$did", state.Did);
        cmd.Parameters.AddWithValue("$outcome", state.Outcome);
        cmd.Parameters.AddWithValue("$note", state.Note);
        cmd.Parameters.AddWithValue("$gaveup", state.GaveUp ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveStateAsync(string alertKey, CancellationToken ct = default)
    {
        if (!_states.TryRemove(alertKey, out _))
            return;

        await using var connection = await db.OpenAsync(ct);
        var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM remediation_state WHERE alert_key = $key";
        cmd.Parameters.AddWithValue("$key", alertKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static object Nullable(DateTimeOffset? value) =>
        value is { } at ? at.ToUnixTimeSeconds() : DBNull.Value;
}
