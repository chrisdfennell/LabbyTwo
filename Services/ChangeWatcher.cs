using System.Globalization;
using LabbyTwo.Core;
using LabbyTwo.Providers;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// Writes the "what changed" feed from what the rest of the app already notices. It
/// detects almost nothing itself: each source is something another part of LabbyTwo was
/// already doing, and this listens.
///
/// <list type="bullet">
/// <item><b>Services up and down</b> — <see cref="HealthMonitor.StatusChanged"/>, the same
/// transitions the status history holds.</item>
/// <item><b>Alerts firing and clearing</b> — <see cref="MetricAlertService.Transitioned"/>.
/// Recorded whether or not the notification was held by maintenance or quiet hours.</item>
/// <item><b>Containers</b> — the list the Docker probe fetched this sweep, compared with the
/// last one (see <see cref="ContainerChanges"/> for why that and not Docker's event stream).</item>
/// <item><b>Certificates</b> — a certificate probe whose serial differs from last time.</item>
/// <item><b>New devices</b> — any probe reporting <c>devices_new</c> above zero, which is
/// what the network scan plugin reports; read as a metric so the host knows nothing about
/// the plugin.</item>
/// <item><b>LabbyTwo itself</b> — a version different from the one that last started.</item>
/// </list>
///
/// DNS answers are recorded by <see cref="DnsCheck"/> when somebody runs it, since it only
/// ever runs when asked. Anything else — a plugin — may call <see cref="ChangeStore.RecordAsync"/>.
///
/// What each detector saw last is kept in the database (<see cref="ChangeStore.BaselineAsync"/>)
/// and written only when it changes, so a restart compares with what was true before it
/// and a quiet sweep writes nothing.
/// </summary>
public sealed class ChangeWatcher(
    ChangeStore changes,
    ConfigStore config,
    HealthMonitor monitor,
    MetricAlertService alerts,
    ILogger<ChangeWatcher> log) : IHostedService
{
    /// <summary>The baseline key holding the version that last started.</summary>
    public const string VersionKey = "self:version";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset At, Dictionary<string, ContainerSnapshot> Containers)> _looks = [];
    private int _diffing;

    public Task StartAsync(CancellationToken ct)
    {
        monitor.StatusChanged += OnStatusChangedAsync;
        monitor.Probed += OnProbed;
        monitor.Updated += OnSweep;
        alerts.Transitioned += OnAlert;

        // Off the startup path: a slow disk must not delay the app for a line in the feed.
        _ = Task.Run(() => NoteVersionAsync(UpdateChecker.Installed, DateTimeOffset.Now, CancellationToken.None));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        monitor.StatusChanged -= OnStatusChangedAsync;
        monitor.Probed -= OnProbed;
        monitor.Updated -= OnSweep;
        alerts.Transitioned -= OnAlert;
        return Task.CompletedTask;
    }

    /// <summary>Stores a change and survives not being able to — a lost line in the feed is not worth a failed sweep.</summary>
    private async Task RecordAsync(Change change)
    {
        try
        {
            await changes.RecordAsync(change);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not record the change \"{Title}\"", change.Title);
        }
    }

    // ---- services --------------------------------------------------------------------

    private Task OnStatusChangedAsync(HealthMonitor.StatusChange change) => RecordAsync(StatusChange(change, DateTimeOffset.Now));

    /// <summary>A status transition as the feed words it.</summary>
    public static Change StatusChange(HealthMonitor.StatusChange change, DateTimeOffset at)
    {
        var name = change.Connection.Name;
        if (!change.IsUp)
            return new Change(at, ChangeKinds.Status, ChangeActions.Down, change.Connection.Id, "", $"{name} went down", change.Message);

        var detail = change.PreviousDuration is { } down ? $"After {Ago.Duration(down)} down." : "";
        return new Change(at, ChangeKinds.Status, ChangeActions.Up, change.Connection.Id, "", $"{name} came back", detail);
    }

    // ---- alerts ----------------------------------------------------------------------

    private void OnAlert(MetricAlertService.AlertTransition transition) => _ = RecordAsync(AlertChange(transition));

    /// <summary>An alert rule starting or stopping firing, as the feed words it.</summary>
    public static Change AlertChange(MetricAlertService.AlertTransition transition) => new(
        transition.At,
        ChangeKinds.Alert,
        transition.Firing ? ChangeActions.Firing : ChangeActions.Cleared,
        transition.Connection.Id,
        transition.Rule.Id,
        transition.Firing ? $"Alert fired: {transition.Name}" : $"Alert cleared: {transition.Name}",
        $"{transition.Connection.Name} read {transition.Reading}.");

    // ---- per-probe: certificates and devices -----------------------------------------

    private void OnProbed(Connection connection, HealthMonitor.ProbeState state)
    {
        if (state.Details.ContainsKey(CertificateProvider.SerialDetail))
            _ = Task.Run(() => NoteCertificateAsync(connection, state.Details, state.At, CancellationToken.None));

        if (state.Metrics.TryGetValue("devices_new", out var appeared) && appeared >= 1)
        {
            var count = (int)appeared;
            _ = RecordAsync(new Change(state.At, ChangeKinds.Device, ChangeActions.Appeared, connection.Id, "",
                count == 1 ? $"A new device on {connection.Name}" : $"{count} new devices on {connection.Name}",
                state.Message));
        }
    }

    /// <summary>
    /// Compares a certificate probe with the last one and records a renewal or a replacement.
    /// The first probe of a certificate only remembers it: there is nothing it changed from.
    /// </summary>
    public async Task NoteCertificateAsync(
        Connection connection, IReadOnlyDictionary<string, string> details, DateTimeOffset at, CancellationToken ct)
    {
        var key = $"cert:{connection.Id}";
        try
        {
            await _gate.WaitAsync(ct);
            try
            {
                var before = await changes.BaselineAsync(key, ct);
                var (baseline, change) = CertificateChange(connection, before, details, at);
                if (baseline != before)
                    await changes.SetBaselineAsync(key, baseline, ct);
                if (change is not null)
                    await changes.RecordAsync(change, ct);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not compare the certificate of {Connection} with the last one", connection.Name);
        }
    }

    /// <summary>
    /// What a certificate probe means for the feed, given what was stored last time: the
    /// baseline to keep, and the change to record if the serial moved. Later expiry is a
    /// renewal; anything else — an earlier expiry, another issuer's certificate — is a
    /// replacement, which is the one to look at.
    /// </summary>
    public static (string Baseline, Change? Change) CertificateChange(
        Connection connection, string? before, IReadOnlyDictionary<string, string> details, DateTimeOffset at)
    {
        var serial = details.GetValueOrDefault(CertificateProvider.SerialDetail, "");
        var notAfter = details.GetValueOrDefault(CertificateProvider.NotAfterDetail, "");
        var issuer = details.GetValueOrDefault(CertificateProvider.IssuerDetail, "");
        var baseline = string.Join('|', serial, notAfter, issuer);

        if (before is null || serial.Length == 0)
            return (before ?? baseline, null);

        var parts = before.Split('|');
        if (parts.Length < 3 || parts[0] == serial)
            return (baseline, null);

        var oldExpiry = ParseInstant(parts[1]);
        var newExpiry = ParseInstant(notAfter);
        var renewed = oldExpiry is { } was && newExpiry is { } now && now > was && parts[2] == issuer;

        var detail = $"From {issuer}" +
                     (newExpiry is { } expiry ? $", valid until {expiry.ToLocalTime():d MMM yyyy}." : ".");
        if (oldExpiry is { } old)
            detail += $" The last one ran until {old.ToLocalTime():d MMM yyyy}";
        if (parts[2] != issuer && parts[2].Length > 0)
            detail += oldExpiry is null ? $" The last one came from {parts[2]}." : $" and came from {parts[2]}.";
        else if (oldExpiry is not null)
            detail += ".";

        var change = new Change(at, ChangeKinds.Certificate, renewed ? ChangeActions.Renewed : ChangeActions.Replaced,
            connection.Id, "",
            renewed ? $"{connection.Name}'s certificate was renewed" : $"{connection.Name}'s certificate was replaced",
            detail);
        return (baseline, change);
    }

    private static DateTimeOffset? ParseInstant(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null;

    // ---- containers ------------------------------------------------------------------

    private void OnSweep()
    {
        // One diff at a time. A sweep landing while the last diff is still going is skipped,
        // not queued: the next one compares against the same baseline and loses nothing,
        // since the restart check measures the real time between looks.
        if (Interlocked.Exchange(ref _diffing, 1) == 1)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await DiffContainersAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Could not compare the container lists with the last sweep's");
            }
            finally
            {
                Volatile.Write(ref _diffing, 0);
            }
        });
    }

    /// <summary>
    /// Compares every Docker host the monitor just probed with the last look at it. Hosts
    /// that are down are skipped — the probe already says so, and a host that cannot be
    /// asked has not had every container removed.
    /// </summary>
    public async Task DiffContainersAsync(CancellationToken ct)
    {
        var dockers = (await config.ConnectionsAsync(ct))
            .Where(c => string.Equals(c.Provider, "docker", StringComparison.OrdinalIgnoreCase) && monitor.IsMonitored(c))
            .ToList();

        foreach (var connection in dockers)
        {
            if (monitor.State(connection.Id) is not { IsUp: true } state)
                continue;
            lock (_looks)
            {
                if (_looks.TryGetValue(connection.Id, out var last) && last.At >= state.At)
                    continue;
            }

            var endpoint = connection.Settings.Get("endpoint", DockerSocket.DefaultEndpoint);
            var timeout = TimeSpan.FromSeconds(Math.Clamp(connection.Settings.GetInt("timeout", 10), 1, 120));
            IReadOnlyList<ContainerRow> rows;
            try
            {
                // The probe put its list here moments ago, so this is normally no request at all.
                rows = await DockerContainers.SharedListAsync(endpoint, timeout, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogDebug(ex, "No container list for {Connection} this sweep", connection.Name);
                continue;
            }

            await NoteContainersAsync(connection, rows, state.At, ct);
        }

        // Forget hosts that were deleted, so a new one reusing nothing starts clean.
        lock (_looks)
        {
            foreach (var id in _looks.Keys.Where(id => dockers.All(c => c.Id != id)).ToList())
                _looks.Remove(id);
        }
    }

    /// <summary>
    /// One look at one host: compares it with the last, records what changed, and keeps it
    /// as the new baseline if anything did. Public so the whole path — first look, restart,
    /// recreate — can be run against a real database without a Docker host.
    /// </summary>
    public async Task<IReadOnlyList<ContainerChange>> NoteContainersAsync(
        Connection connection, IReadOnlyList<ContainerRow> rows, DateTimeOffset at, CancellationToken ct)
    {
        var key = $"docker:{connection.Id}";
        var now = rows
            .Select(r => (Container: new ContainerSnapshot(r.Name, r.Id, r.Image, r.ImageId, r.State), r.Status))
            .ToList();
        var snapshot = now.Select(n => n.Container).GroupBy(c => c.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        await _gate.WaitAsync(ct);
        try
        {
            (DateTimeOffset At, Dictionary<string, ContainerSnapshot> Containers)? last;
            lock (_looks)
                last = _looks.TryGetValue(connection.Id, out var look) ? look : null;

            // In this process, the last look is in memory and so is when it was. After a
            // restart it is whatever was stored, and when that was taken is unknown.
            var before = last?.Containers ?? ContainerChanges.Deserialise(await changes.BaselineAsync(key, ct));
            var since = last is { } known ? at - known.At : (TimeSpan?)null;

            lock (_looks)
                _looks[connection.Id] = (at, snapshot);

            if (before is null)
            {
                // The first look at this host: nothing to compare with, so no changes —
                // otherwise adding a Docker connection would announce forty containers "created".
                await changes.SetBaselineAsync(key, ContainerChanges.Serialise(snapshot.Values), ct);
                return [];
            }

            var found = ContainerChanges.Diff(before, now, since);
            if (!ContainerChanges.SameAs(before, snapshot.Values))
                await changes.SetBaselineAsync(key, ContainerChanges.Serialise(snapshot.Values), ct);

            foreach (var change in found)
            {
                await RecordAsync(new Change(at, ChangeKinds.Container, change.Action, connection.Id, change.Name,
                    change.Title, change.Detail));
            }
            return found;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- LabbyTwo itself -------------------------------------------------------------

    /// <summary>
    /// Records an update if this version is not the one that last started. The first start
    /// ever only remembers it. A version of "dev" — a build nobody stamped — is never
    /// compared: every such build says the same thing and none of them is an update.
    /// </summary>
    public async Task<Change?> NoteVersionAsync(string version, DateTimeOffset at, CancellationToken ct)
    {
        try
        {
            var before = await changes.BaselineAsync(VersionKey, ct);
            if (before == version)
                return null;
            await changes.SetBaselineAsync(VersionKey, version, ct);
            if (before is null || before == "dev" || version == "dev")
                return null;

            return await changes.RecordAsync(new Change(at, ChangeKinds.Update, ChangeActions.Updated, null, "labbytwo",
                $"LabbyTwo updated to {version}", $"It was {before}."), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not check whether LabbyTwo was updated since it last started");
            return null;
        }
    }
}
