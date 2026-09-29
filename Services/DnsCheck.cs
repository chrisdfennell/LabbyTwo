using System.Diagnostics;
using System.Net;
using LabbyTwo.Core;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// Can this process turn names into addresses? Asked from the health page, and only when
/// somebody presses the button: resolving api.github.com tells whoever runs the resolver
/// that LabbyTwo is here, and the Settings page promises nothing leaves the machine until
/// it is asked to.
///
/// Two kinds of name, because they fail for different reasons and have different fixes. A
/// public name that will not resolve means the container has no working DNS at all — the
/// fault that stopped update checks. A LAN name that will not resolve means the host knows
/// it through /etc/hosts, NetBIOS or mDNS and the container inherits none of those.
/// </summary>
public sealed class DnsCheck(ConfigStore config, ChangeStore changes, ILogger<DnsCheck> log)
{
    /// <summary>The name the update check needs, and a fair stand-in for "the internet".</summary>
    public const string PublicProbe = "api.github.com";

    /// <summary>Long enough for a slow resolver, short enough that a hanging one reads as a failure.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <param name="UsedBy">The connections that name this host, so a failure says what it breaks.</param>
    public sealed record Lookup(
        string Host,
        bool IsPublic,
        bool Resolved,
        TimeSpan Took,
        IReadOnlyList<string> Addresses,
        string? Error,
        IReadOnlyList<string> UsedBy);

    public sealed record Target(string Host, bool IsPublic, IReadOnlyList<string> UsedBy);

    /// <summary>
    /// Every distinct host name the enabled connections point at, with the public probe
    /// first. IP addresses are left out — they cannot have a DNS problem — and a name is
    /// asked once however many connections share it.
    /// </summary>
    public static IReadOnlyList<Target> TargetsFor(IEnumerable<Connection> connections)
    {
        var byHost = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var connection in connections.Where(c => c.Enabled))
        {
            foreach (var key in (string[])["url", "host"])
            {
                if (ProbeError.HostOf(connection.Settings.Get(key)) is not { } host || host.Contains(' '))
                    continue;
                if (!byHost.TryGetValue(host, out var names))
                    byHost[host] = names = [];
                if (!names.Contains(connection.Name))
                    names.Add(connection.Name);
            }
        }

        var targets = new List<Target>
        {
            new(PublicProbe, true,
                byHost.Remove(PublicProbe, out var github) ? ["Update check", .. github] : ["Update check"]),
        };
        targets.AddRange(byHost
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new Target(pair.Key, ProbeError.IsPublicName(pair.Key), pair.Value)));
        return targets;
    }

    /// <summary>Resolves every target at once; the page waits for the slowest, not the sum.</summary>
    public async Task<IReadOnlyList<Lookup>> RunAsync(CancellationToken ct = default)
    {
        var targets = TargetsFor(await config.ConnectionsAsync(ct));
        var lookups = await Task.WhenAll(targets.Select(target =>
            ResolveAsync(target, (host, token) => Dns.GetHostAddressesAsync(host, token), Timeout, ct)));
        await NoteAnswersAsync(lookups, DateTimeOffset.Now, ct);
        return lookups;
    }

    /// <summary>
    /// Records a LAN name whose answer is not what it was the last time somebody checked —
    /// the NAS that moved to a new address after a router swap, the DHCP lease that went
    /// somewhere else — in the change feed. Only when the check is run, because it only
    /// runs when asked (see the class summary), and only for names on the LAN: a public
    /// name behind a CDN answers differently from one minute to the next, and a feed that
    /// said so every time would be a feed nobody read. A name that did not resolve is not
    /// an answer, so it neither records a change nor replaces the last good one.
    /// </summary>
    public async Task<IReadOnlyList<Change>> NoteAnswersAsync(IEnumerable<Lookup> lookups, DateTimeOffset at, CancellationToken ct)
    {
        var recorded = new List<Change>();
        try
        {
            foreach (var lookup in lookups.Where(l => l.Resolved && !l.IsPublic))
            {
                var key = $"dns:{lookup.Host.ToLowerInvariant()}";
                var answer = Answer(lookup.Addresses);
                var before = await changes.BaselineAsync(key, ct);
                if (before == answer)
                    continue;
                await changes.SetBaselineAsync(key, answer, ct);
                if (before is null)
                    continue;

                recorded.Add(await changes.RecordAsync(new Change(at, ChangeKinds.Dns, ChangeActions.Changed, null, lookup.Host,
                    $"{lookup.Host} now resolves to {answer.Replace(",", ", ")}",
                    $"It was {before.Replace(",", ", ")}. Used by {string.Join(", ", lookup.UsedBy)}."), ct));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not compare DNS answers with the last check");
        }
        return recorded;
    }

    /// <summary>The addresses as one comparable value: sorted, so an answer in another order is the same answer.</summary>
    public static string Answer(IEnumerable<string> addresses) =>
        string.Join(',', addresses.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// One lookup, bounded. The resolver is passed in so the timing and failure handling
    /// can be tested without a network; the deadline is enforced with WaitAsync because a
    /// name the resolver cannot answer tends to hang rather than fail.
    /// </summary>
    public static async Task<Lookup> ResolveAsync(
        Target target,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var addresses = await resolve(target.Host, deadline.Token).WaitAsync(timeout, ct);
            stopwatch.Stop();
            return addresses.Length == 0
                ? Failed($"\"{target.Host}\" resolved to no addresses.")
                : new Lookup(target.Host, target.IsPublic, true, stopwatch.Elapsed,
                    [.. addresses.Select(a => a.ToString())], null, target.UsedBy);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
                                   (ex is TimeoutException || deadline.IsCancellationRequested))
        {
            return Failed($"No answer within {timeout.TotalSeconds:0} seconds.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // ProbeError already knows how to say what a resolution failure means for a
            // public name versus a LAN one, so the advice matches what a tile would show.
            return Failed(ProbeError.Describe(ex, target.Host));
        }

        Lookup Failed(string error)
        {
            stopwatch.Stop();
            return new Lookup(target.Host, target.IsPublic, false, stopwatch.Elapsed, [], error, target.UsedBy);
        }
    }
}
