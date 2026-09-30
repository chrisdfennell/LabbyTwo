using LabbyTwo.Core;
using LabbyTwo.Services.Offsite;
using LabbyTwo.Storage;
using Microsoft.Extensions.Options;

namespace LabbyTwo.Services;

/// <summary>
/// Turns the Backups page's list into dates: asks each item's source when its last backup
/// finished, judges it against how often it should happen, and — on a sweep — records what
/// changed in the feed and says so through the alert channels.
///
/// It asks nothing new of anybody. A backup provider's reading is the one the monitor
/// already took this sweep; LabbyTwo's own backup is the newest file in its backup folder
/// (which, unlike the job runner's memory, survives a restart); an off-site copy is the
/// status the off-site job already keeps. Reading the whole list is one small query and
/// some memory, which is why the page, <c>{{backups}}</c> and the weekly summary can all
/// simply ask.
///
/// Alerts follow the idioms of the rest of the app without pretending an item is a
/// connection: held by maintenance, by a silence or a down parent of the connection it is
/// about, by a mute window covering everything or that connection, and by quiet hours in
/// either mode — a late backup is worth knowing about at breakfast, not at three in the
/// morning. Something held is sent on the first sweep after the hold lifts, once; the item
/// remembers what it has already said, so nothing is repeated every sweep.
/// </summary>
public sealed class BackupProof(
    BackupStore store,
    ConfigStore config,
    Registry registry,
    HealthMonitor monitor,
    AppSettingsStore settings,
    OffsiteSettingsStore offsite,
    ChangeStore changes,
    AlertService alerts,
    MuteWindowStore mutes,
    IOptions<LabbyOptions> options,
    IHostEnvironment environment,
    ILogger<BackupProof> log)
{
    /// <summary>
    /// The zone frequencies and drills are counted in: the machine's, as everywhere else.
    /// Settable so a test can use one whose clocks change on known dates.
    /// </summary>
    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Local;

    /// <summary>One sweep at a time, so a "Mark backed up" and a sweep cannot both announce the same thing.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>What is needed to read every item's source, read once per pass rather than once per item.</summary>
    private sealed record Context(
        IReadOnlyList<Connection> Connections,
        SettingsBag Settings,
        IReadOnlyList<OffsiteDestination> Destinations,
        IReadOnlyDictionary<string, DestinationStatus> Statuses);

    /// <summary>Every item, judged at <paramref name="now"/>. Callers on a page run this through <see cref="Offload"/>.</summary>
    public async Task<IReadOnlyList<BackupRow>> RowsAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var items = await store.AllAsync(ct);
        if (items.Count == 0)
            return [];

        var context = await ContextAsync(items, ct);
        return [.. items.Select(item => BackupSchedule.Row(item, Read(item, context, now), now, Zone))];
    }

    private async Task<Context> ContextAsync(IReadOnlyList<BackupItem> items, CancellationToken ct)
    {
        var connections = await config.ConnectionsAsync(ct);
        var bag = await settings.AllAsync(ct);
        // Only read when something is proven by an off-site copy: the destinations carry
        // encrypted secrets, and unprotecting them every sweep for nothing is waste.
        if (!items.Any(i => i.Source == BackupSources.Offsite))
            return new Context(connections, bag, [], new Dictionary<string, DestinationStatus>());
        return new Context(connections, bag, await offsite.DestinationsAsync(ct), await offsite.StatusesAsync(ct));
    }

    /// <summary>What an item's source says now.</summary>
    private BackupReading Read(BackupItem item, Context context, DateTimeOffset now)
    {
        switch (item.Source)
        {
            case BackupSources.LabbyTwo:
            {
                const string what = "LabbyTwo's nightly backup";
                var enabled = context.Settings.GetBool(BackupJob.EnabledKey, true);
                var folder = BackupJob.FolderFor(context.Settings, options.Value, environment);
                try
                {
                    var newest = NewestCopy(folder);
                    return new BackupReading(newest, enabled ? null : "the nightly backup is switched off in Settings", what);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new BackupReading(null, $"{folder} could not be read: {ex.Message}", what);
                }
            }

            case BackupSources.Offsite:
            {
                if (context.Destinations.FirstOrDefault(d => d.Id == item.SourceTarget) is not { } destination)
                    return new BackupReading(null, "the off-site destination that proved it has been removed", "an off-site copy");
                var what = $"the off-site copy to {destination.Name}";
                var last = context.Statuses.GetValueOrDefault(destination.Id)?.LastSuccessAt;
                return new BackupReading(last, destination.Enabled ? null : $"{destination.Name} is switched off", what);
            }

            case BackupSources.Metric:
            {
                var metric = item.SourceMetric.Trim();
                if (context.Connections.FirstOrDefault(c => c.Id == item.SourceTarget) is not { } connection)
                    return new BackupReading(null, "the connection that proved it has been deleted", metric);
                var what = $"{connection.Name} · {metric}";
                if (!connection.Enabled)
                    return new BackupReading(null, $"{connection.Name} is disabled", what);
                if (monitor.State(connection.Id) is not { IsUp: not null } state)
                    return new BackupReading(null, $"{connection.Name} has not been checked yet", what);
                if (state.IsUp != true)
                    return new BackupReading(null, $"{connection.Name} is down: {state.Message}", what);
                if (!state.Metrics.TryGetValue(metric, out var value))
                    return new BackupReading(null, $"{connection.Name} does not report {metric}", what);
                return new BackupReading(BackupSchedule.FromMetric(metric, UnitOf(connection, metric), value, state.At), null, what);
            }

            default:
                return item.LastSuccessBy.Length > 0
                    ? new BackupReading(null, null, $"ticked by {item.LastSuccessBy}")
                    : BackupReading.Manual;
        }
    }

    /// <summary>The unit a provider declares for a metric — or for its family, for <c>hours_since_ping:restic</c>.</summary>
    private string UnitOf(Connection connection, string metric)
    {
        var specs = registry.MetricsFor(connection);
        var family = metric.Split(':')[0];
        return (specs.FirstOrDefault(m => string.Equals(m.Key, metric, StringComparison.OrdinalIgnoreCase))
                ?? specs.FirstOrDefault(m => string.Equals(m.Key, family, StringComparison.OrdinalIgnoreCase)))?.Unit ?? "";
    }

    /// <summary>When the newest nightly copy was written. Null when there is none, or no folder.</summary>
    public static DateTimeOffset? NewestCopy(string folder)
    {
        var directory = new DirectoryInfo(folder);
        if (!directory.Exists)
            return null;
        var newest = directory.EnumerateFiles(BackupJob.FilePattern).MaxBy(f => f.LastWriteTimeUtc);
        return newest is null ? null : new DateTimeOffset(newest.LastWriteTimeUtc, TimeSpan.Zero);
    }

    /// <summary>
    /// The metric connections a proof may be read from, with the readings that look like one
    /// first — for the editor's picker. From memory: the monitor's last probe of each.
    /// </summary>
    public IReadOnlyList<(string Metric, string Label)> MetricChoices(Connection connection)
    {
        var specs = registry.MetricsFor(connection);
        var reported = monitor.State(connection.Id)?.Metrics.Keys ?? (IEnumerable<string>)[];
        return
        [
            .. specs.Select(s => (s.Key, s.Label, s.Unit))
                .Concat(reported.Where(k => specs.All(s => !string.Equals(s.Key, k, StringComparison.OrdinalIgnoreCase)))
                    .Select(k => (Key: k, Label: specs.FirstOrDefault(s => s.Key == k.Split(':')[0]) is { } family
                        ? $"{family.Label} · {k[(k.IndexOf(':') + 1)..]}" : k, Unit: UnitOf(connection, k))))
                .Where(m => m.Key != "latency_ms")
                .OrderByDescending(m => BackupSchedule.LooksLikeProof(m.Key, m.Unit))
                .ThenBy(m => m.Key, StringComparer.OrdinalIgnoreCase)
                .Select(m => (m.Key, m.Label)),
        ];
    }

    public Task<IReadOnlyList<OffsiteDestination>> DestinationsAsync(CancellationToken ct = default) => offsite.DestinationsAsync(ct);

    // ---------- the editor ----------

    public async Task SaveAsync(BackupItem item, CancellationToken ct = default)
    {
        if (item.Problem() is { } problem)
            throw new InvalidOperationException(problem);
        await store.SaveAsync(item, ct);
    }

    public Task DeleteAsync(string id, CancellationToken ct = default) => store.DeleteAsync(id, ct);

    /// <summary>
    /// "Mark backed up": a manual item's only proof. Recorded in the feed, and the date is
    /// the item's from now on — the next sweep sends the "backed up again" if its lateness
    /// had been announced.
    /// </summary>
    public async Task MarkBackedUpAsync(string id, string who, DateTimeOffset at, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (await store.GetAsync(id, ct) is not { } item)
                return;
            var name = Who(who);
            await store.SaveStateAsync(item with { LastSuccess = Seconds(at), LastSuccessBy = name }, ct);
            await RecordAsync(new Change(at, ChangeKinds.Backup, ChangeActions.Completed, item.AboutConnectionId, item.Id,
                $"{item.Name} backed up", $"Ticked by {name}."), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// "Mark restore tested". Nothing is tested automatically — a restore is only proven by
    /// somebody restoring it — so this is the record that they did, and what moves the next
    /// reminder a whole cadence on.
    /// </summary>
    public async Task<RestoreTest?> MarkTestedAsync(string id, string who, string notes, DateTimeOffset at, CancellationToken ct = default)
    {
        if (await store.GetAsync(id, ct) is not { } item)
            return null;
        var test = await store.AddTestAsync(new RestoreTest(0, id, Seconds(at), Who(who), notes.Trim()), ct);
        var detail = $"By {test.Who}." + (test.Notes.Length > 0 ? " " + Shorten(test.Notes, 280) : "");
        await RecordAsync(new Change(at, ChangeKinds.Backup, ChangeActions.Tested, item.AboutConnectionId, item.Id,
            $"Restore of {item.Name} tested", detail), ct);
        return test;
    }

    public Task<IReadOnlyList<RestoreTest>> TestsAsync(string id, CancellationToken ct = default) => store.TestsAsync(id, 50, ct);

    // ---------- the sweep ----------

    /// <summary>
    /// One pass over every item: remember what the sources proved, record completions and
    /// lateness in the feed, and send whatever is due to be said. Returns the rows as judged.
    /// </summary>
    public async Task<IReadOnlyList<BackupRow>> SweepAsync(DateTimeOffset now, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var rows = await RowsAsync(now, ct);
            foreach (var row in rows)
            {
                try
                {
                    await SweepAsync(row, now, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One item that cannot be written must not stop the rest being checked.
                    log.LogWarning(ex, "Could not check the backup of {Name}", row.Item.Name);
                }
            }
            return rows;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SweepAsync(BackupRow row, DateTimeOffset now, CancellationToken ct)
    {
        var item = row.Item;
        var updated = item;

        // A newer backup than the one remembered. The first date ever seen is only
        // remembered — adding an item is not the moment its backup completed — and a date
        // that moved by less than a quarter of the interval is a reading's rounding, not a
        // new backup: an age in whole hours would otherwise "complete" every sweep.
        if (row.Reading.LastSuccess is { } proven && row.Status.LastSuccess == proven
            && (item.LastSuccess is not { } known || proven - known > Tolerance(item.Frequency)))
        {
            if (item.LastSuccess is not null)
            {
                var at = proven > now ? now : proven;
                var due = BackupSchedule.DueBy(proven, item.Frequency, item.EffectiveGraceHours, Zone);
                await RecordAsync(new Change(at, ChangeKinds.Backup, ChangeActions.Completed, item.AboutConnectionId, item.Id,
                    $"{item.Name} backed up", $"Proven by {row.Reading.Source}. Next due by {BackupSchedule.When(due, now, Zone)}."), ct);
            }
            updated = updated with { LastSuccess = Seconds(proven), LastSuccessBy = "" };
        }

        var state = row.Status.State.ToString().ToLowerInvariant();
        if (row.Status.State == BackupState.Late && item.LastState != state && row.Status.LastSuccess is { } last)
        {
            await RecordAsync(new Change(now, ChangeKinds.Backup, ChangeActions.Late, item.AboutConnectionId, item.Id,
                $"The backup of {item.Name} is late",
                $"The last one was {BackupSchedule.When(last, now, Zone)}; it was due by {BackupSchedule.When(row.Status.DueBy!.Value, now, Zone)}."), ct);
        }
        updated = updated with { LastState = state };

        var notices = BackupNotices.For(row);
        if (notices.Any)
        {
            var lateSent = notices.SendLate && await SendAsync(item, LateAlert(row, now), now, ct);
            if (notices.SendRecovered)
                await SendAsync(item, RecoveredAlert(row, now), now, ct);
            var drillSent = notices.SendDrill && await SendAsync(item, DrillAlert(row, now), now, ct);
            updated = notices.Apply(updated, row.DrillDue, lateSent, drillSent);
        }

        if (updated != item)
            await store.SaveStateAsync(updated, ct);
    }

    /// <summary>How far a proven date must move to count as a new backup.</summary>
    private static TimeSpan Tolerance(BackupFrequency frequency) => frequency switch
    {
        BackupFrequency.Hourly => TimeSpan.FromMinutes(15),
        BackupFrequency.Daily => TimeSpan.FromHours(6),
        BackupFrequency.Weekly => TimeSpan.FromHours(24),
        _ => TimeSpan.FromDays(3),
    };

    // ---------- alerts ----------

    /// <summary>The key a mute window naming rules would use, and the tag a channel groups by.</summary>
    public static string AlertKey(BackupItem item) => $"backup:{item.Id}";

    private Alert LateAlert(BackupRow row, DateTimeOffset now)
    {
        var zone = Zone;
        var body = row.Status.LastSuccess is { } last
            ? $"The last one proven by {row.Reading.Source} was {BackupSchedule.When(last, now, zone)} ({Ago.Since(last, now)}), and a " +
              $"{BackupSchedule.Describe(row.Item.Frequency)} backup was due by {BackupSchedule.When(row.Status.DueBy!.Value, now, zone)}."
            : "There is nothing to show it was ever backed up.";
        if (row.Status.Problem is { } problem)
            body += $" Also: {problem}.";
        return new Alert(AlertLevel.Down, $"The backup of {row.Item.Name} is late", body) { Tag = AlertKey(row.Item), Link = "backups" };
    }

    private Alert RecoveredAlert(BackupRow row, DateTimeOffset now) =>
        new(AlertLevel.Up, $"{row.Item.Name} is backed up again",
            row.Status.LastSuccess is { } last ? $"The newest backup is from {BackupSchedule.When(last, now, Zone)}." : "Back on time.")
        {
            Tag = AlertKey(row.Item), Link = "backups",
        };

    private Alert DrillAlert(BackupRow row, DateTimeOffset now)
    {
        var item = row.Item;
        var body = item.LastRestoreTest is { } tested
            ? $"The last restore test was {BackupSchedule.Day(tested, now, Zone)}" + (item.LastRestoreTestBy.Length > 0 ? $", by {item.LastRestoreTestBy}." : ".")
            : "It has never been restored to see whether it works.";
        body += " Restore it somewhere harmless, then press Mark restore tested on the Backups page.";
        return new Alert(AlertLevel.Info, $"Time to test restoring {item.Name}", body) { Tag = $"backup-drill:{item.Id}", Link = "backups" };
    }

    /// <summary>Sends an item's alert unless something is holding it. True when it reached a channel.</summary>
    private async Task<bool> SendAsync(BackupItem item, Alert alert, DateTimeOffset now, CancellationToken ct)
    {
        if (await HeldAsync(item, now, ct) is { } reason)
        {
            log.LogInformation("\"{Title}\" not sent yet: {Reason}", alert.Title, reason);
            return false;
        }
        return await alerts.BroadcastAsync(alert, null, now, ct) > 0;
    }

    /// <summary>
    /// Why an item's alert should wait, or null if it may go. The same holds as any other
    /// alert — maintenance, the connection's silence or a down parent, a mute window — plus
    /// quiet hours in either mode, which <see cref="AlertService.BroadcastAsync(Alert, IReadOnlyCollection{string}?, DateTimeOffset, CancellationToken)"/>
    /// would let a down alert through in "down only".
    /// </summary>
    public async Task<string?> HeldAsync(BackupItem item, DateTimeOffset now, CancellationToken ct)
    {
        var bag = await settings.AllAsync(ct);
        if (Maintenance.From(bag, now) is { On: true } maintenance)
            return maintenance.Reason;

        var about = item.AboutConnectionId is { } id ? await config.ConnectionAsync(id, ct) : null;
        if (about is not null && await alerts.SuppressedAsync(about, false, now, ct) is { } suppressed)
            return suppressed;

        if (MuteWindow.Muting(await mutes.AllAsync(ct), AlertKey(item), about?.Id ?? "", now, alerts.Zone) is { } window)
            return $"muted by {window.Name}";

        if (AlertPolicy.From(bag).IsQuiet(now, alerts.Zone))
            return "quiet hours";

        return null;
    }

    // ---------- helpers ----------

    private async Task RecordAsync(Change change, CancellationToken ct)
    {
        try
        {
            await changes.RecordAsync(change, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not record the change \"{Title}\"", change.Title);
        }
    }

    private static string Who(string who) => who.Trim() is { Length: > 0 } name ? Shorten(name, 80) : "someone";

    private static string Shorten(string text, int most) => text.Length <= most ? text : text[..(most - 1)] + "…";

    /// <summary>Whole seconds, as stored, so what is kept compares equal to what is read back.</summary>
    private static DateTimeOffset Seconds(DateTimeOffset at) => DateTimeOffset.FromUnixTimeSeconds(at.ToUnixTimeSeconds());
}
