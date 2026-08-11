using System.Diagnostics;
using LabbyTwo.Core;
using LabbyTwo.Storage;
// A plugin is a plain class library, so the host's implicit usings have to be named here.
using Microsoft.Extensions.Logging;

namespace LabbyTwo.RenewalsPlugin;

/// <summary>
/// Keeps watched rows in step with the certificates themselves.
///
/// LabbyTwo deliberately does not renew anything. Renewing means ACME — an account key, a
/// challenge answered on port 80 or through your DNS provider's API, a fresh private key —
/// and then installing the result wherever the certificate is actually served, which a
/// dashboard cannot do. Holding that pile of credentials to produce a certificate it could
/// not install would be a poor trade. Caddy, Traefik and acme.sh already do this properly,
/// next to the thing being served.
///
/// What a dashboard is well placed to do is notice. This reads the live certificate and
/// writes its expiry onto the row, which catches the failure the ACME clients do not: a
/// renewal that happened on disk and was never reloaded, where the file is new and the
/// process is still serving the old one.
/// </summary>
public sealed class CertificateWatchJob(Db db, ILogger<CertificateWatchJob> log) : IBackgroundJob
{
    public string Name => "renewals-tls-check";

    /// <summary>
    /// Four times a day. Certificates move rarely, so this is not about catching the
    /// change quickly — it is about the row never being more than a few hours stale, while
    /// staying nowhere near often enough to look like traffic to anyone's web server.
    /// </summary>
    public TimeSpan Interval => TimeSpan.FromHours(6);

    public bool RunAtStartup => true;

    public async Task RunAsync(CancellationToken ct)
    {
        var store = new RenewalStore(db);
        var watched = (await store.AllAsync(ct)).Where(renewal => renewal.IsWatched).ToList();

        if (watched.Count == 0)
            return;

        var failures = 0;

        // One at a time. Half a dozen TLS handshakes is nothing, and doing them in
        // sequence keeps a slow or dead host from being indistinguishable from the rest.
        foreach (var renewal in watched)
        {
            ct.ThrowIfCancellationRequested();
            await store.CheckAsync(renewal, ct);
        }

        foreach (var renewal in (await store.AllAsync(ct)).Where(r => r.IsWatched && r.CheckFailed))
        {
            failures++;
            log.LogWarning("Certificate check for {Title} ({Host}) failed: {Error}",
                renewal.Title, renewal.TlsHost, renewal.CheckError);
        }

        log.LogInformation("Checked {Count} certificate(s), {Failures} unreachable", watched.Count, failures);
    }
}

/// <summary>
/// The page: everything that expires, soonest first.
/// </summary>
public sealed class RenewalsTabKind : ITabKind
{
    public string Kind => "renewals";
    public string DisplayName => "Renewals";
    public string Icon => "⏳";
    public string Description =>
        "Domains, certificates, subscriptions and warranties — what expires when, and what is already late.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("warn_within", "Count as due within (days)", FieldKind.Number, Default: "30",
            Help: "How far ahead something is worth worrying about. 30 suits domains and bills; " +
                  "14 is better if your list is mostly certificates."),

        new("category", "Only this category", FieldKind.Text,
            Help: "Blank shows everything."),
    ];

    public Type Component => typeof(RenewalsTab);
}

/// <summary>The dashboard card: only what is due or late.</summary>
public sealed class RenewalsDueWidget : IWidgetType
{
    public string Type => "renewals-due";
    public string DisplayName => "Renewals due";
    public string Icon => "⏳";
    public string Description => "Whatever is expiring soon, with the number of days left.";

    public int DefaultWidth => 3;

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("within_days", "Show ones due within (days)", FieldKind.Number, Default: "30"),
        new("category", "Only this category", FieldKind.Text),
        new("limit", "Most to list", FieldKind.Number, Default: "5"),
    ];

    public Type Component => typeof(RenewalsDue);
}

/// <summary>
/// The half that makes this more than a page you forget to open.
///
/// A tab kind cannot alert: alert rules are written against a connection's metrics, and a
/// page is not a connection. So the plugin also ships a provider whose probe reads its own
/// table and reports the numbers — days until the next thing expires, how many are already
/// overdue — and the whole existing machinery applies: charts, rules, and a message
/// through whichever channel you set up. This pairing is the pattern for any plugin that
/// owns data worth being told about.
/// </summary>
public sealed class RenewalsProvider(Db db) : IConnectionProvider
{
    public string Type => "renewals";
    public string DisplayName => "Renewals";
    public string Icon => "⏳";
    public string Category => "Home";
    public string Description =>
        "Reports the renewals list as numbers, so an expiry can raise an alert rather than wait to be noticed. Add one; it needs no settings.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("category", "Only this category", FieldKind.Text,
            Help: "Blank counts everything. Add a second connection with a different category if you " +
                  "want separate alerts for, say, certificates and subscriptions."),
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("days_until_next", "Next renewal", " days"),
        new("overdue", "Overdue"),
        new("due_30_days", "Due within 30 days"),
        new("tracked", "Tracked"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Something expires within a fortnight", "days_until_next", Comparison.Below, 14, ForMinutes: 60,
            Why: "Long enough to renew a domain or a certificate without rushing. An hour's patience " +
                 "keeps a restart from re-alerting."),

        new("Something has already expired", "overdue", Comparison.Above, 0, ForMinutes: 60,
            Why: "This is the one that matters: a lapsed certificate or domain, still on the list, unnoticed."),
    ];

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var store = new RenewalStore(db);
            var today = DateOnly.FromDateTime(DateTime.Now);
            var category = connection.Settings.Get("category");

            var renewals = (await store.AllAsync(ct))
                .Where(renewal => category.Length == 0
                    || renewal.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
                .ToList();

            stopwatch.Stop();

            var metrics = new Dictionary<string, double>
            {
                ["tracked"] = renewals.Count,
                ["overdue"] = renewals.Count(renewal => renewal.IsOverdue(today)),
                ["due_30_days"] = renewals.Count(renewal => renewal.DaysLeft(today) is >= 0 and <= 30),
            };

            var next = renewals
                .Where(renewal => !renewal.IsOverdue(today))
                .OrderBy(renewal => renewal.Due)
                .FirstOrDefault();

            if (next is not null)
                metrics["days_until_next"] = next.DaysLeft(today);

            var overdue = (int)metrics["overdue"];
            var message = renewals.Count == 0
                ? "Nothing tracked yet"
                : overdue > 0
                    ? $"{overdue} overdue: {string.Join(", ", renewals.Where(r => r.IsOverdue(today)).Take(3).Select(r => r.Title))}"
                    : next is null
                        ? $"{renewals.Count} tracked"
                        : $"Next: {next.Title} {next.DueLabel(today)}";

            // Still "up": the list is readable. An expiry is news for an alert rule, not a
            // failed probe — reporting it as down would make the uptime figure meaningless
            // and colour the tile red for something that is working perfectly.
            return ProbeResult.Up(stopwatch.Elapsed, message, metrics);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ex.GetBaseException().Message);
        }
    }
}
