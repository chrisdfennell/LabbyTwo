using System.Diagnostics;
using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// LabbyTwo watching itself: every thirty seconds it looks at what it already knows about
/// its own health — the monitor's bookkeeping, the background jobs' last runs, whether its
/// history writes are getting through, how fast the database grows, the free space beside
/// it, the slow-query log — and tells you, through the ordinary alert channels, when one of
/// them goes wrong and again when it comes right. The rules are <see cref="SelfWatchRules"/>;
/// the once-and-once bookkeeping is <see cref="SelfWatchLedger"/>.
///
/// <para>A timer of its own rather than the monitor's sweep, because two of the things it
/// watches for are the sweep being stuck and the monitor having stopped — neither of which
/// would ever call it. And nothing it reads is in the database: when the database is what is
/// wrong, the watcher must still be able to say so.</para>
///
/// <para>Notices are held like any other alert's — by maintenance, by a mute window that
/// covers everything, and by quiet hours in either mode, as a late backup is: LabbyTwo's own
/// trouble is worth knowing about at breakfast, not at three in the morning — and sent when
/// the hold lifts if the problem is still there. Which checks run is chosen on the health
/// page, and the current ones are listed there as findings.</para>
/// </summary>
public sealed class SelfWatch(
    HealthMonitor monitor,
    BackgroundJobRunner jobs,
    HistoryStore history,
    StorageManager storage,
    Db db,
    ConfigStore config,
    AppSettingsStore settings,
    MuteWindowStore mutes,
    AlertService alerts,
    ILogger<SelfWatch> log) : BackgroundService
{
    /// <summary>The settings row listing the checks somebody turned off: "disk,db-growth".</summary>
    public const string OffKey = "self_watch_off";

    /// <summary>The settings row keeping what has been announced, so a restart neither repeats nor forgets it.</summary>
    public const string StateKey = "self_watch_state";

    /// <summary>What mute windows see this as: an alert about a connection called "labbytwo".</summary>
    public const string ConnectionId = "labbytwo";

    public static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    private readonly SelfWatchLedger _ledger = new();
    private readonly SemaphoreSlim _pass = new(1, 1);
    private IReadOnlyList<SelfReading> _readings = [];
    private string _saved = "";
    private bool _loaded;

    /// <summary>
    /// The zone quiet hours and mute windows are read in. Settable so a test can use one it
    /// knows; the app uses the alert service's.
    /// </summary>
    public TimeZoneInfo? Zone { get; set; }

    /// <summary>The last round's verdicts, for the health page. From memory.</summary>
    public IReadOnlyList<SelfReading> Readings => _readings;

    /// <summary>What is current — started, or still waiting out its sustain time — for the health page.</summary>
    public IReadOnlyCollection<SelfAlert> Current => _ledger.Alerts;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every);
        do
        {
            try
            {
                await PassAsync(DateTimeOffset.Now, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "LabbyTwo could not check on itself this time");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>The checks somebody turned off, from the cached settings.</summary>
    public async Task<IReadOnlySet<SelfCheck>> DisabledAsync(CancellationToken ct = default) =>
        Disabled((await settings.AllAsync(ct)).Get(OffKey));

    public static IReadOnlySet<SelfCheck> Disabled(string stored) =>
        stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SelfWatchRules.Parse).OfType<SelfCheck>().ToHashSet();

    public async Task SaveDisabledAsync(IEnumerable<SelfCheck> off, CancellationToken ct = default)
    {
        await settings.SaveAsync(OffKey, string.Join(',', off.Distinct().Select(SelfWatchRules.Stored)), ct);
        await PassAsync(DateTimeOffset.Now, ct);
    }

    /// <summary>What the rules are handed: memory, the file system, nothing else.</summary>
    public SelfFacts Facts(DateTimeOffset now, IReadOnlyDictionary<string, string> names)
    {
        var directory = Path.GetDirectoryName(db.FilePath)!;
        var (free, total) = HistoryStore.Space(directory);
        SlowQueryFacts? slow = null;
        if (db.Queries is { } queries)
        {
            var (count, spent) = queries.SlowSince(now - SelfWatchRules.SlowWindow);
            slow = new SlowQueryFacts(count, spent, queries.Threshold, SelfWatchRules.SlowWindow);
        }

        using var process = Process.GetCurrentProcess();
        return new SelfFacts(
            now,
            now - new DateTimeOffset(process.StartTime),
            monitor.Status,
            jobs.Runs,
            history.Writes.Status(now),
            storage.Growth(now),
            free is { } f && total is { } t ? new DiskFacts(f, t, db.SizeBytes) : null,
            slow,
            names);
    }

    /// <summary>One round: judge, keep the books, send what is owed. Public, with the clock as a parameter, for tests.</summary>
    public async Task PassAsync(DateTimeOffset now, CancellationToken ct) =>
        await PassAsync(now, null, ct);

    /// <summary>A round judged on the facts given rather than the live ones — for tests.</summary>
    public async Task PassAsync(DateTimeOffset now, SelfFacts? facts, CancellationToken ct)
    {
        await _pass.WaitAsync(ct);
        try
        {
            var bag = await settings.AllAsync(ct);
            if (!_loaded)
            {
                _ledger.Load(bag.Get(StateKey));
                _saved = _ledger.Save();
                _loaded = true;
                await storage.LoadAsync(ct);
            }

            if (facts is null)
            {
                var names = (await config.ConnectionsAsync(ct)).ToDictionary(c => c.Id, c => c.Name);
                facts = Facts(now, names);
            }

            var off = Disabled(bag.Get(OffKey));
            _readings = SelfWatchRules.Evaluate(facts, _ledger.FiringKeys);
            _ledger.Step(now, _readings, check => !off.Contains(check));
            await DeliverAsync(now, bag, ct);
            await SaveAsync(ct);
        }
        finally
        {
            _pass.Release();
        }
    }

    private async Task DeliverAsync(DateTimeOffset now, SettingsBag bag, CancellationToken ct)
    {
        var owed = _ledger.OwedStarts.Count + _ledger.OwedClears.Count;
        if (owed == 0)
            return;

        if (await HeldAsync(now, bag, ct) is { } reason)
        {
            log.LogDebug("{Count} notice(s) about LabbyTwo itself waiting: {Reason}", owed, reason);
            return;
        }

        foreach (var alert in _ledger.OwedStarts)
        {
            log.LogWarning("{Title}: {Body}", alert.Title, alert.Body);
            var sent = await alerts.BroadcastAsync(new Alert(AlertLevel.Down, alert.Title, alert.Body)
            {
                Tag = $"labbytwo:{alert.Key}",
                Link = alert.Check == SelfCheck.DatabaseGrowth ? "settings/storage" : "settings/health",
            }, null, now, ct);
            if (sent > 0)
                _ledger.Delivered(alert.Key);
        }

        foreach (var notice in _ledger.OwedClears)
        {
            log.LogInformation("{Title}: {Body}", notice.Title, notice.Body);
            // Sent whether or not a channel took it: its start was delivered, and a recovery
            // that finds no channel now has nowhere better to wait for.
            await alerts.BroadcastAsync(new Alert(AlertLevel.Up, notice.Title, notice.Body)
            {
                Tag = $"labbytwo:{notice.Key}",
                Link = "settings/health",
            }, null, now, ct);
            _ledger.Delivered(notice);
        }
    }

    /// <summary>
    /// Why notices about LabbyTwo itself should wait, or null: maintenance, a mute window
    /// covering everything (or one naming this, should one ever be able to), or quiet hours
    /// in either mode.
    /// </summary>
    public async Task<string?> HeldAsync(DateTimeOffset now, SettingsBag bag, CancellationToken ct)
    {
        var zone = Zone ?? alerts.Zone;
        if (Maintenance.From(bag, now) is { On: true } maintenance)
            return maintenance.Reason;
        if (MuteWindow.Muting(await mutes.AllAsync(ct), null, ConnectionId, now, zone) is { } window)
            return $"muted by {window.Name}";
        if (AlertPolicy.From(bag).IsQuiet(now, zone))
            return "quiet hours";
        return null;
    }

    /// <summary>Keeps the books across a restart — only when they changed, so a quiet install writes nothing.</summary>
    private async Task SaveAsync(CancellationToken ct)
    {
        var state = _ledger.Save();
        if (state == _saved)
            return;
        try
        {
            await settings.SaveAsync(StateKey, state, ct);
            _saved = state;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Most likely the very database trouble being reported. Tried again next round.
            log.LogDebug(ex, "Could not keep what LabbyTwo has said about itself");
        }
    }
}
