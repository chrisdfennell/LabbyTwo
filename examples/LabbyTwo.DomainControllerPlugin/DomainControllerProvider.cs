using System.Diagnostics;
using System.Net.Sockets;
using LabbyTwo.Core;

namespace LabbyTwo.DomainControllerPlugin;

/// <summary>
/// A Windows domain controller, checked the way one actually fails.
///
/// Pinging a DC tells you the box is on, which is never the interesting question. A domain
/// controller stops working while remaining perfectly reachable: its clock drifts and
/// Kerberos starts refusing tickets, or its DNS zone breaks and nothing can find it any
/// more. Both are invisible to every other kind of check and both take the whole domain
/// with them.
///
/// Agentless on purpose. Everything here is a socket against a service the DC already
/// publishes, so there is nothing to install on it and no credentials to store — which
/// matters more on a domain controller than on anything else you own.
///
/// <para><b>What this deliberately does not do.</b> It is not a shell and not a metrics
/// agent, because LabbyTwo already has both. The Terminal plugin opens a real pty over SSH
/// and works against Windows' OpenSSH Server; for CPU, memory and disk, run
/// <c>windows_exporter</c> on the DC and point the built-in Prometheus provider at it. Put
/// those on one grid tab beside this connection's tiles and the "everything about the DC in
/// one place" page is three existing pieces rather than a fourth implementation.</para>
/// </summary>
public sealed class DomainControllerProvider : IConnectionProvider
{
    public const string ProviderType = "domain-controller";

    public string Type => ProviderType;
    public string DisplayName => "Domain controller";
    public string Icon => "🏛️";
    public string Category => "Infrastructure";

    public string Description =>
        "Active Directory health without an agent: clock skew, a real DNS query for the domain's own "
        + "zone, and whether LDAP, Kerberos and SMB are answering.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("host", "Address", FieldKind.Text, "192.168.86.10", Required: true,
            Help: "The DC's IP address. A name would have to be resolved by DNS, which is one of the "
                + "things being tested — so this is an address on purpose."),

        new("domain", "Domain", FieldKind.Text, "fennell.local",
            Help: "The AD domain. Used to ask the DC for its own zone's SOA record, which is the "
                + "difference between “DNS is listening” and “DNS still works”. Leave it blank to "
                + "skip that check."),

        new("skew_seconds", "Clock skew that matters (seconds)", FieldKind.Number, Default: "300",
            Help: "Kerberos refuses a ticket more than five minutes out, so 300 is the real cliff. "
                + "The suggested alert fires well before it.")
        { Advanced = true },

        new("check_time", "Check the clock", FieldKind.Bool, Default: "true",
            Help: "Asks the DC the time over SNTP. A domain controller is the time source for every "
                + "machine joined to it, so when it drifts it takes them with it.")
        { Advanced = true },

        new("timeout", "Timeout (ms)", FieldKind.Number, Default: "2000") { Advanced = true },
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("clock_offset_seconds", "Clock offset", " s", 1),
        new("clock_skew_seconds", "Clock skew", " s", 1),
        new("dns_ok", "DNS answering for the zone"),
        new("dns_ms", "DNS query time", " ms"),
        new("ldap_open", "LDAP"),
        new("ldaps_open", "LDAPS"),
        new("kerberos_open", "Kerberos"),
        new("smb_open", "SMB"),
        new("services_up", "AD services answering"),
    ];

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Clock drifting towards a Kerberos failure", "clock_skew_seconds", Comparison.Above, 120,
            ForMinutes: 10,
            Why: "Kerberos refuses tickets past five minutes of skew, and when that happens logins fail "
                 + "across the domain with no message that mentions the clock. Two minutes is early "
                 + "enough to fix it calmly."),

        new("DNS has stopped answering for the domain", "dns_ok", Comparison.Below, 1, ForMinutes: 5,
            Why: "Domain members find a DC through DNS SRV records. A DC that cannot answer for its own "
                 + "zone is invisible to the domain while still being pingable."),

        new("An AD service has gone", "services_up", Comparison.Below, 4, ForMinutes: 5,
            Why: "One of LDAP, LDAPS, Kerberos or SMB has stopped answering. Which one is on the tile; "
                 + "any of them missing is a domain controller doing part of its job."),
    ];

    /// <summary>The ports that make a domain controller a domain controller.</summary>
    private static readonly (string Key, string Label, int Port)[] Services =
    [
        ("ldap_open", "LDAP", 389),
        ("ldaps_open", "LDAPS", 636),
        ("kerberos_open", "Kerberos", 88),
        ("smb_open", "SMB", 445),
    ];

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var host = connection.Settings.Get("host");
        if (host.Length == 0)
            return ProbeResult.Down(TimeSpan.Zero, "No address configured.");

        var timeout = Math.Clamp(connection.Settings.GetInt("timeout", 2000), 200, 30_000);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // All four at once. They are independent questions and asking them in sequence
            // would make a slow probe out of four fast ones.
            var reachable = await Task.WhenAll(
                Services.Select(async service => (service.Key, service.Label,
                    Open: await OpenAsync(host, service.Port, timeout, ct))));

            var metrics = new Dictionary<string, double>();
            foreach (var (key, _, open) in reachable)
                metrics[key] = open ? 1 : 0;

            var up = reachable.Count(service => service.Open);
            metrics["services_up"] = up;

            var missing = reachable.Where(service => !service.Open).Select(service => service.Label).ToList();

            // Nothing at all answered. That is the one case worth reporting as Down: every
            // other combination is a working machine with a problem, which is a metric and
            // an alert rule rather than a red tile.
            if (up == 0)
            {
                stopwatch.Stop();
                return ProbeResult.Down(stopwatch.Elapsed,
                    $"Nothing answered on 389, 636, 88 or 445. Either {host} is not a domain controller, "
                    + "or it is not reachable from here.");
            }

            var notes = new List<string>();

            if (connection.Settings.Get("domain") is { Length: > 0 } domain)
            {
                var dns = await DnsProbe.AskSoaAsync(host, domain, timeout, ct);
                metrics["dns_ok"] = dns.Ok ? 1 : 0;
                if (dns.Ok)
                {
                    metrics["dns_ms"] = Math.Round(dns.Milliseconds);
                    notes.Add(dns.Authoritative ? "DNS authoritative" : "DNS answered, not authoritative");
                }
                else
                {
                    notes.Add($"DNS: {(dns.Detail.Length > 0 ? dns.Detail : "no answer for the zone")}");
                }
            }

            if (connection.Settings.GetBool("check_time", true))
            {
                if (await Sntp.OffsetAsync(host, 123, timeout, ct) is { } offset)
                {
                    metrics["clock_offset_seconds"] = Math.Round(offset, 1);

                    // The absolute value as its own metric, so one alert rule covers a clock
                    // that is fast and one that is slow. Kerberos does not care which way.
                    metrics["clock_skew_seconds"] = Math.Round(Math.Abs(offset), 1);

                    var cliff = Math.Clamp(connection.Settings.GetInt("skew_seconds", 300), 30, 3600);
                    notes.Add(Math.Abs(offset) >= cliff
                        ? $"clock {Describe(offset)} — past the Kerberos limit"
                        : $"clock {Describe(offset)}");
                }
                else
                {
                    notes.Add("no answer on NTP");
                }
            }

            stopwatch.Stop();
            metrics["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds;

            var summary = missing.Count == 0
                ? "LDAP, LDAPS, Kerberos and SMB all answering"
                : $"{up} of 4 answering — no {string.Join(", ", missing)}";

            if (notes.Count > 0)
                summary += " · " + string.Join(" · ", notes);

            // Up whenever it is doing something. Whether one missing service or a two-minute
            // drift is a problem is the user's call, made with the rules above — a provider
            // that returned Down for it would put a hole in the uptime history of a machine
            // that was answering the whole time.
            return ProbeResult.Up(stopwatch.Elapsed, summary, metrics);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ex.GetBaseException().Message);
        }
    }

    /// <summary>"41s fast" reads better on a tile than "+41".</summary>
    private static string Describe(double offset) =>
        Math.Abs(offset) < 1
            ? "in step"
            : $"{Math.Abs(offset):0.#}s {(offset > 0 ? "ahead" : "behind")}";

    private static async Task<bool> OpenAsync(string host, int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(timeoutMs);

            await client.ConnectAsync(host, port, window.Token);
            return client.Connected;
        }
        catch (Exception)
        {
            return false;   // refused, filtered or timed out all mean the same thing here
        }
    }
}
