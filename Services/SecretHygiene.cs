using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>What one check found, across every Docker host.</summary>
/// <param name="At">When it ran.</param>
/// <param name="Containers">How many containers were read.</param>
/// <param name="Findings">Every finding, ignored ones included and marked, worst first.</param>
/// <param name="Problems">Hosts that could not be read, and why — a socket proxy refusing
/// inspect, a host that is down — in words.</param>
public sealed record SecretReport(
    DateTimeOffset At,
    int Containers,
    IReadOnlyList<SecretFinding> Findings,
    IReadOnlyList<string> Problems)
{
    public IReadOnlyList<SecretFinding> Active => [.. Findings.Where(f => !f.Ignored)];

    public int IgnoredCount => Findings.Count(f => f.Ignored);

    /// <summary>"3 containers have 5 plain-text secrets", or that none do.</summary>
    public string Summary
    {
        get
        {
            var active = Active;
            if (active.Count == 0)
                return Containers == 1 ? "No plain-text secrets in the one container checked." :
                    $"No plain-text secrets in the {Containers} containers checked.";
            var containers = active.Select(f => f.Container).Distinct(StringComparer.Ordinal).Count();
            return $"{containers} container{(containers == 1 ? " has" : "s have")} {active.Count} plain-text secret{(active.Count == 1 ? "" : "s")}.";
        }
    }
}

/// <summary>
/// Checks every container's settings for passwords, tokens and keys written in plain text
/// (see <see cref="SecretScan"/> for what counts), when somebody presses "Check now", once a
/// day if they turned that on, and — if they asked — once for a container that has just
/// been recreated, so a new high-severity one is said once, in the change feed and through
/// the alert channels.
///
/// Never on a sweep. The container list is the one the Docker probe already fetched
/// (<see cref="DockerContainers.SharedListAsync"/>), and the inspects are four at a time per
/// host, so a check of forty containers on a NAS is forty small requests a day at most.
///
/// <b>The values go nowhere.</b> They are read into a <see cref="SecretInput"/>, judged, and
/// dropped; what is kept — in memory for the page, in the change feed, in the log — is a
/// <see cref="SecretFinding"/>: container, name, kind, severity and length. The only things
/// stored are the ignore list (container and variable names) and the set of high findings
/// already announced (the same), so the database never holds a value either.
/// </summary>
public sealed class SecretHygiene(
    ConfigStore config,
    AppSettingsStore settings,
    ChangeStore changes,
    ILogger<SecretHygiene> log,
    AlertService? alerts = null) : IHostedService
{
    /// <summary>Check once a day. Off unless switched on.</summary>
    public const string DailyKey = "secrets_daily";

    /// <summary>Check stopped containers as well as running ones.</summary>
    public const string StoppedKey = "secrets_stopped";

    /// <summary>Say so once when a recreated container has a new high-severity secret.</summary>
    public const string NotifyKey = "secrets_notify";

    /// <summary>The ignore list: a JSON array of "container|NAME".</summary>
    public const string IgnoredKey = "secrets_ignored";

    /// <summary>When the last full check finished, for the daily one.</summary>
    public const string LastRunKey = "secrets_last_run";

    /// <summary>The change baseline holding the high findings already seen, so each is announced once.</summary>
    public const string SeenKey = "secrets:high";

    /// <summary>Inspects at once per host. Enough to be quick, few enough not to be noticed by a NAS.</summary>
    public const int Concurrency = 4;

    private readonly SemaphoreSlim _checking = new(1, 1);
    private readonly SemaphoreSlim _noting = new(1, 1);

    /// <summary>The last check, or null before the first since LabbyTwo started.</summary>
    public SecretReport? Latest { get; private set; }

    /// <summary>Raised when <see cref="Latest"/> changes — a check, an ignore — so an open page redraws.</summary>
    public event Action? Updated;

    public Task StartAsync(CancellationToken ct)
    {
        changes.Recorded += OnRecorded;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        changes.Recorded -= OnRecorded;
        return Task.CompletedTask;
    }

    // ---- the check -------------------------------------------------------------------

    /// <summary>Reads every Docker host's containers and judges them. Only one runs at a time.</summary>
    public async Task<SecretReport> CheckAsync(CancellationToken ct = default)
    {
        await _checking.WaitAsync(ct);
        try
        {
            var bag = await settings.AllAsync(ct);
            var stopped = bag.GetBool(StoppedKey);
            var ignored = SecretScan.ParseIgnored(bag.Get(IgnoredKey));

            var inputs = new List<SecretInput>();
            var problems = new List<string>();
            foreach (var connection in await DockerConnectionsAsync(ct))
            {
                var (endpoint, timeout) = Address(connection);
                try
                {
                    var rows = await DockerContainers.SharedListAsync(endpoint, timeout, ct);
                    var chosen = rows.Where(r => stopped || !r.IsStopped).ToList();
                    inputs.AddRange(await ReadAllAsync(endpoint, timeout, chosen, ct));
                }
                catch (DockerProxyDeniedException)
                {
                    problems.Add($"{connection.Name}: the socket proxy would not let LabbyTwo read containers' settings. " +
                                 "Set CONTAINERS=1 on it — the same flag the Containers tab needs.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    problems.Add($"{connection.Name}: {DockerContainers.Explain(ex, endpoint)}");
                }
            }

            var findings = Order(SecretScan.FindAll(inputs).Select(f => f with { Ignored = ignored.Contains(f.Key) }));
            var report = new SecretReport(DateTimeOffset.Now, inputs.Count, findings, problems);

            // Names and counts only. Never the finding's advice either: it is generic, but
            // keeping the log line to numbers is the easiest way to be sure.
            log.LogInformation("Secret check: {Active} plain-text secret(s) in {Containers} container(s), {Ignored} ignored",
                report.Active.Count, report.Containers, report.IgnoredCount);

            await RememberSeenAsync(inputs.Select(i => i.Container), findings, ct);
            await settings.SaveAsync(LastRunKey, report.At.ToString("O"), ct);

            Latest = report;
            Updated?.Invoke();
            return report;
        }
        finally
        {
            _checking.Release();
        }
    }

    /// <summary>
    /// Inspects the chosen containers, a few at a time. A container removed between the list
    /// and its inspect is simply not there; a socket proxy refusing is the whole host's
    /// problem, so it stops the rest.
    /// </summary>
    private static async Task<IReadOnlyList<SecretInput>> ReadAllAsync(
        string endpoint, TimeSpan timeout, IReadOnlyList<ContainerRow> rows, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(Concurrency, Concurrency);
        var read = await Task.WhenAll(rows.Select(async row =>
        {
            await gate.WaitAsync(ct);
            try
            {
                return await ReadOneAsync(endpoint, timeout, row.Id, ct);
            }
            finally
            {
                gate.Release();
            }
        }));
        return [.. read.OfType<SecretInput>()];
    }

    private static async Task<SecretInput?> ReadOneAsync(string endpoint, TimeSpan timeout, string idOrName, CancellationToken ct)
    {
        try
        {
            var inspect = await DockerSocket.GetAsync(endpoint, timeout, $"/containers/{Uri.EscapeDataString(idOrName)}/json", ct);
            return SecretScan.Read(inspect);
        }
        catch (InvalidOperationException ex) when (ex is not DockerProxyDeniedException)
        {
            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Worst first, then by container and name, so the same check reads the same way twice.</summary>
    private static List<SecretFinding> Order(IEnumerable<SecretFinding> findings) =>
    [
        .. findings.OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Container, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Name, StringComparer.Ordinal),
    ];

    // ---- ignoring --------------------------------------------------------------------

    /// <summary>
    /// Ignores a finding, or stops ignoring it. Remembered by container and name, so it stays
    /// ignored across recreates and restarts — "I moved it to .env" is still true tomorrow.
    /// </summary>
    public async Task SetIgnoredAsync(SecretFinding finding, bool ignore, CancellationToken ct = default)
    {
        var current = new HashSet<string>(SecretScan.ParseIgnored(await settings.GetAsync(IgnoredKey, "", ct)), StringComparer.Ordinal);
        if (ignore)
            current.Add(finding.Key);
        else
            current.Remove(finding.Key);
        await settings.SaveAsync(IgnoredKey, SecretScan.FormatIgnored(current), ct);

        if (Latest is { } report)
        {
            Latest = report with
            {
                Findings = [.. report.Findings.Select(f => f.Key == finding.Key ? f with { Ignored = ignore } : f)],
            };
            Updated?.Invoke();
        }
    }

    // ---- the daily check ----------------------------------------------------------------

    /// <summary>Whether a daily check is due: switched on, and a day since the last one.</summary>
    public static bool DailyDue(SettingsBag bag, DateTimeOffset now) =>
        bag.GetBool(DailyKey) &&
        (!DateTimeOffset.TryParse(bag.Get(LastRunKey), System.Globalization.CultureInfo.InvariantCulture,
             System.Globalization.DateTimeStyles.RoundtripKind, out var last) ||
         now - last >= TimeSpan.FromDays(1) || now < last);

    // ---- after a recreate --------------------------------------------------------------

    private void OnRecorded(Change change)
    {
        if (change.Kind != ChangeKinds.Container ||
            change.Action is not (ChangeActions.Created or ChangeActions.Recreated) ||
            change.ConnectionId is null || change.Subject.Length == 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await NoteRecreatedAsync(change.ConnectionId, change.Subject, change.At, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogDebug(ex, "Could not check the recreated container {Container} for plain-text secrets", change.Subject);
            }
        });
    }

    /// <summary>
    /// A container was just created or recreated: if notifications are on, inspect it alone
    /// and announce any high-severity finding not seen before and not ignored, once.
    /// Public so the path from "recreated" to the feed entry runs in a test without a sweep.
    /// </summary>
    /// <returns>The changes recorded — one per new finding, usually none.</returns>
    public async Task<IReadOnlyList<Change>> NoteRecreatedAsync(
        string connectionId, string container, DateTimeOffset at, CancellationToken ct)
    {
        var bag = await settings.AllAsync(ct);
        if (!bag.GetBool(NotifyKey))
            return [];
        if (await config.ConnectionAsync(connectionId, ct) is not { } connection ||
            !string.Equals(connection.Provider, "docker", StringComparison.OrdinalIgnoreCase))
            return [];

        var (endpoint, timeout) = Address(connection);
        SecretInput? input;
        try
        {
            input = await ReadOneAsync(endpoint, timeout, container, ct);
        }
        catch (DockerProxyDeniedException)
        {
            return [];
        }
        if (input is null)
            return [];

        var ignored = SecretScan.ParseIgnored(bag.Get(IgnoredKey));
        var high = SecretScan.Find(input)
            .Where(f => f.Severity == SecretSeverity.High && !ignored.Contains(f.Key))
            .ToList();

        await _noting.WaitAsync(ct);
        try
        {
            var seen = SecretScan.ParseIgnored(await changes.BaselineAsync(SeenKey, ct)).ToHashSet(StringComparer.Ordinal);
            var fresh = high.Where(f => !seen.Contains(f.Key)).ToList();
            if (fresh.Count == 0)
                return [];

            foreach (var finding in fresh)
                seen.Add(finding.Key);
            await changes.SetBaselineAsync(SeenKey, SecretScan.FormatIgnored(seen), ct);

            var recorded = new List<Change>();
            foreach (var finding in fresh)
            {
                var entry = AnnouncementFor(connectionId, finding, at);
                recorded.Add(await changes.RecordAsync(entry, ct));
                if (alerts is not null)
                {
                    try
                    {
                        await alerts.BroadcastAsync(new Alert(AlertLevel.Info, entry.Title, entry.Detail), ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log.LogWarning(ex, "Could not send the notice about {Container}", finding.Container);
                    }
                }
            }
            log.LogInformation("{Container} has {Count} new plain-text secret(s) after being recreated", container, fresh.Count);
            return recorded;
        }
        finally
        {
            _noting.Release();
        }
    }

    /// <summary>"gluetun now has OPENVPN_PASSWORD in plain text" — and nothing about what it is.</summary>
    public static Change AnnouncementFor(string connectionId, SecretFinding finding, DateTimeOffset at) =>
        new(at, ChangeKinds.Container, ChangeActions.Appeared, connectionId, finding.Container,
            $"{finding.Container} now has {finding.Name} in plain text",
            $"A {finding.Kind} in its {finding.Where}. Settings → Secrets in containers says how to move it out of the compose file.");

    /// <summary>
    /// After a full check, the high findings to treat as already known: what this check saw,
    /// plus what was known about containers it did not read (a host that was down). So a
    /// secret that was removed and later comes back is announced again, which is right.
    /// </summary>
    private async Task RememberSeenAsync(IEnumerable<string> read, IReadOnlyList<SecretFinding> findings, CancellationToken ct)
    {
        await _noting.WaitAsync(ct);
        try
        {
            var readSet = new HashSet<string>(read, StringComparer.Ordinal);
            var before = SecretScan.ParseIgnored(await changes.BaselineAsync(SeenKey, ct));
            var kept = before.Where(key => !readSet.Contains(key[..Math.Max(0, key.IndexOf('|'))]));
            var now = findings.Where(f => f.Severity == SecretSeverity.High).Select(f => f.Key);
            var after = SecretScan.FormatIgnored(kept.Concat(now));
            if (after != SecretScan.FormatIgnored(before))
                await changes.SetBaselineAsync(SeenKey, after, ct);
        }
        finally
        {
            _noting.Release();
        }
    }

    // ---- helpers -----------------------------------------------------------------------

    private async Task<IReadOnlyList<Connection>> DockerConnectionsAsync(CancellationToken ct) =>
    [
        .. (await config.ConnectionsAsync(ct))
            .Where(c => c.Enabled && string.Equals(c.Provider, "docker", StringComparison.OrdinalIgnoreCase)),
    ];

    private static (string Endpoint, TimeSpan Timeout) Address(Connection connection) =>
        (connection.Settings.Get("endpoint", DockerSocket.DefaultEndpoint),
         TimeSpan.FromSeconds(Math.Clamp(connection.Settings.GetInt("timeout", 10), 1, 120)));
}

/// <summary>
/// The daily secret check, when somebody switched it on. Wakes hourly and usually does
/// nothing: the runner counts intervals from process start, so a day-long interval would
/// never fire on a container that restarts every night, and when the last check ran is
/// kept in settings for the same reason.
/// </summary>
public sealed class SecretHygieneJob(SecretHygiene hygiene, AppSettingsStore settings) : IBackgroundJob
{
    public string Name => "secret-check";

    public TimeSpan Interval => TimeSpan.FromHours(1);

    public async Task RunAsync(CancellationToken ct)
    {
        if (SecretHygiene.DailyDue(await settings.AllAsync(ct), DateTimeOffset.Now))
            await hygiene.CheckAsync(ct);
    }
}
