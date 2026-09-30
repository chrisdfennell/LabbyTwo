using System.Globalization;

namespace LabbyTwo.Core;

/// <summary>How sure a <see cref="ProbableCause"/> is. Ordered, so causes sort by it.</summary>
public enum CauseConfidence
{
    /// <summary>It happened at the right time, and nothing more ties it to the outage.</summary>
    Low,

    /// <summary>It touched something involved, shortly before — worth checking first.</summary>
    Medium,

    /// <summary>The kind of thing that takes a service down, done to that service, just before it went.</summary>
    High,
}

/// <summary>What sort of explanation a cause is. Stored nowhere; used to rank and to test.</summary>
public static class CauseKinds
{
    public const string SharedParent = "parent";
    public const string Container = "container";
    public const string CertificateExpired = "certificate-expired";
    public const string Certificate = "certificate";
    public const string Host = "host";
    public const string Dns = "dns";
    public const string SelfUpdate = "update";
    public const string Capacity = "capacity";

    /// <summary>Which of two causes equally sure of themselves is shown first: the more specific one.</summary>
    internal static int Rank(string kind) => kind switch
    {
        SharedParent => 0,
        CertificateExpired => 1,
        Container => 2,
        Certificate => 3,
        Host => 4,
        Dns => 5,
        Capacity => 6,
        SelfUpdate => 7,
        _ => 8,
    };
}

/// <summary>
/// One explanation of an incident: a sentence a person can act on, and the changes from
/// the feed that make it more than a hunch.
/// </summary>
/// <param name="Kind">One of <see cref="CauseKinds"/>.</param>
/// <param name="Sentence">Plain text — the names in it are the user's own, never markup.</param>
/// <param name="Evidence">The changes it rests on, oldest first. Empty only for a cause
/// read from the lab's shape rather than the feed.</param>
public sealed record ProbableCause(
    string Kind,
    CauseConfidence Confidence,
    string Sentence,
    IReadOnlyList<Change> Evidence);

/// <summary>
/// What <see cref="IncidentCauses"/> needs to know about one connection: its name now, what
/// it sits behind, and the host names and addresses it is reached at — which is also how
/// it is tied to a container, the way <c>ContainerSafety.ConnectionsReaching</c> ties them:
/// <c>http://plex:32400</c> is reached through the container called <c>plex</c>.
/// </summary>
/// <param name="Hosts">Every host its settings point at, compared without regard to case.</param>
public sealed record CauseConnection(
    string Id,
    string Name,
    string Provider,
    string? ParentId,
    IReadOnlyCollection<string> Hosts);

/// <summary>
/// The lab as <see cref="IncidentCauses"/> sees it: every connection, and what each sits
/// behind with loops and missing parents taken out — the same sanitising the dependency
/// map does, so the two can never disagree about what depends on what.
/// </summary>
public sealed class CauseLab
{
    public static readonly CauseLab Empty = new([]);

    private readonly Dictionary<string, CauseConnection> _byId;
    private readonly IReadOnlyDictionary<string, string?> _parents;

    public CauseLab(IEnumerable<CauseConnection> connections)
    {
        _byId = [];
        foreach (var connection in connections)
            _byId.TryAdd(connection.Id, connection);
        _parents = DependencyLayout.Layout(_byId.Values.Select(c => new DependencyInput(c.Id, c.ParentId))).Parents;
    }

    public CauseConnection? Get(string? id) => id is not null && _byId.TryGetValue(id, out var found) ? found : null;

    /// <summary>Everything <paramref name="id"/> sits behind, nearest first.</summary>
    public IEnumerable<string> Ancestors(string id)
    {
        var seen = new HashSet<string> { id };
        var current = _parents.GetValueOrDefault(id);
        while (current is not null && seen.Add(current))
        {
            yield return current;
            current = _parents.GetValueOrDefault(current);
        }
    }

    /// <summary>Every connection reached at <paramref name="host"/>.</summary>
    public IEnumerable<CauseConnection> On(string host) =>
        _byId.Values.Where(c => c.Hosts.Contains(host, StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// A capacity forecast for the metric an alert member watches — "Volume 1 is full in about
/// three weeks" — so a full disk is explained as the slow thing it is.
/// </summary>
/// <param name="What">What is filling, in words: "Disk used on NAS".</param>
public sealed record CauseForecast(string What, CapacityForecast Forecast);

/// <summary>
/// "Probably caused by": a few rules that read an incident against the change feed and the
/// shape of the lab and say what most likely set it off. No model and no network — every
/// rule is something a person would check first, written down so the check is already
/// done when the incident is opened at three in the morning.
///
/// Pure, like <see cref="IncidentRules"/>: handed the incident, the changes around it, the
/// lab and any forecasts, it returns causes, surest first. Every rule is therefore a test.
///
/// The rules, each producing a sentence and the changes it rests on:
/// <list type="bullet">
/// <item><b>A shared parent.</b> Several members sit behind one connection — directly or
/// further up — that went down no later than they did. The same judgement the alerting
/// makes when it holds a child's alert. High.</item>
/// <item><b>Something done to its container.</b> A new image, a recreate, a stop or a
/// restart of the container a failing service is reached through, within
/// <see cref="Window"/> before it went down. High, or Medium for a restart, which is as
/// often the symptom as the cause.</item>
/// <item><b>An expired certificate</b>, read from what a certificate check said when it
/// went down; <b>a certificate replaced or renewed</b> on something involved shortly
/// before.</item>
/// <item><b>One host.</b> Members that share an address, with no parent explaining them,
/// all failing within <see cref="HostSpread"/> of each other — the machine or the network
/// to it, not each service.</item>
/// <item><b>A DNS answer that moved</b>, and <b>LabbyTwo updating itself</b>, shortly
/// before — the checks themselves may have changed.</item>
/// <item><b>A capacity alert with a forecast.</b> A disk above 90% that has been filling
/// steadily for weeks is not a fault that happened today, and saying so is the useful
/// part.</item>
/// </list>
///
/// When none of them fit, the answer is nothing, and it is shown as
/// <see cref="NoObviousCause"/>. A wrong cause stated confidently sends somebody to fix the
/// wrong thing; an honest "nothing obvious" sends them to the timeline, which is right.
/// </summary>
public static class IncidentCauses
{
    /// <summary>How long before a failure a change to the same thing counts as its likely cause.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long after a failure a change may be recorded and still count as before it. Both
    /// are noticed by polling, the container list on the sweep after the one that saw the
    /// service fail, so "the container was recreated" can land a sweep late.
    /// </summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(1);

    /// <summary>How close together failures on one host must be to be the host's fault.</summary>
    public static readonly TimeSpan HostSpread = TimeSpan.FromMinutes(1);

    /// <summary>What is shown when no rule fits.</summary>
    public const string NoObviousCause = "No obvious cause";

    /// <summary>The most causes worth listing. Past this they are noise around the one that matters.</summary>
    public const int MaxCauses = 4;

    /// <summary>The first cause's sentence, or <see cref="NoObviousCause"/>.</summary>
    public static string Headline(IReadOnlyList<ProbableCause> causes) =>
        causes.Count > 0 ? causes[0].Sentence : NoObviousCause;

    /// <summary>
    /// The likely causes of <paramref name="incident"/>, surest first, at most
    /// <see cref="MaxCauses"/>; empty when nothing fits.
    /// </summary>
    /// <param name="changes">The feed around the incident — at least from
    /// <see cref="IncidentRules.ContextBefore"/> before it started until it ended. Any order.</param>
    /// <param name="lab">The connections as they are now.</param>
    /// <param name="forecasts">Capacity forecasts by member key, for alert members that watch
    /// a capacity metric. Optional.</param>
    public static IReadOnlyList<ProbableCause> Explain(
        Incident incident,
        IReadOnlyList<Change> changes,
        CauseLab lab,
        IReadOnlyDictionary<string, CauseForecast>? forecasts = null)
    {
        // First spans only: a service failing again later in the incident is still the same
        // service, and the rules below ask what took each one down in the first place.
        var members = incident.Members.Where(m => m.Span == 1).OrderBy(m => m.DownAt).ToList();
        var status = members.Where(m => m.Kind == ChangeKinds.Status && m.ConnectionId is not null).ToList();
        var feed = changes.OrderBy(c => c.At).ThenBy(c => c.Id).ToList();
        var causes = new List<ProbableCause>();

        var explained = SharedParent(status, feed, lab, causes);
        CertificateExpired(status, feed, lab, causes);
        Containers(status, feed, lab, causes);
        Certificates(incident, status, feed, lab, causes);
        SameHost(status.Where(m => !explained.Contains(m.Key)).ToList(), feed, lab, causes);
        Dns(incident, status, feed, lab, causes);
        SelfUpdate(incident, feed, causes);
        Capacity(members, feed, forecasts, causes);

        return
        [
            .. causes
                .DistinctBy(c => c.Sentence)
                .OrderByDescending(c => c.Confidence)
                .ThenBy(c => CauseKinds.Rank(c.Kind))
                .Take(MaxCauses),
        ];
    }

    // ---- a shared parent -------------------------------------------------------------

    /// <summary>
    /// The member most others sit behind, if it went down no later than they did. The
    /// furthest-up one wins by counting: the router has the NAS's dependents and the NAS
    /// too. Returns the keys of the members it explains.
    /// </summary>
    private static HashSet<string> SharedParent(
        List<IncidentMember> status, List<Change> feed, CauseLab lab, List<ProbableCause> causes)
    {
        (IncidentMember Parent, List<IncidentMember> Children)? best = null;
        foreach (var parent in status)
        {
            var children = status
                .Where(m => m.Key != parent.Key && m.DownAt >= parent.DownAt && lab.Ancestors(m.ConnectionId!).Contains(parent.ConnectionId!))
                .ToList();
            if (children.Count > 0 && (best is null || children.Count > best.Value.Children.Count))
                best = (parent, children);
        }

        if (best is not { } found)
            return [];

        var (top, dependents) = found;
        var topName = NameOf(top, lab);
        var first = dependents.All(d => d.DownAt > top.DownAt) ? "which went down first" : "which went down at the same moment";
        var sentence = dependents.Count == 1
            ? $"{NameOf(dependents[0], lab)} depends on {topName}, {first}."
            : $"{dependents.Count} services failed together; they all depend on {topName}, {first}.";

        var evidence = feed
            .Where(c => c.Kind == ChangeKinds.Status && c.Action == ChangeActions.Down && c.ConnectionId == top.ConnectionId
                        && (c.At - top.DownAt).Duration() <= Grace)
            .OrderBy(c => (c.At - top.DownAt).Duration())
            .Take(1)
            .ToList();

        causes.Add(new ProbableCause(CauseKinds.SharedParent, CauseConfidence.High, sentence, evidence));
        return [.. dependents.Select(d => d.Key)];
    }

    // ---- the service's own container ---------------------------------------------------

    private static void Containers(List<IncidentMember> status, List<Change> feed, CauseLab lab, List<ProbableCause> causes)
    {
        foreach (var member in status)
        {
            if (lab.Get(member.ConnectionId) is not { Hosts.Count: > 0 } connection)
                continue;

            var change = feed
                .Where(c => c.Kind == ChangeKinds.Container
                            && ContainerVerb(c.Action) is not null
                            && connection.Hosts.Contains(c.Subject, StringComparer.OrdinalIgnoreCase)
                            && Before(c.At, member.DownAt))
                .LastOrDefault();
            if (change is null)
                continue;

            var name = NameOf(member, lab);
            var it = string.Equals(change.Subject, name, StringComparison.OrdinalIgnoreCase) ? "it" : $"its container {change.Subject}";
            var sentence = $"{name} went down {Gap(change.At, member.DownAt)} {it} {ContainerVerb(change.Action)}.";
            var confidence = change.Action is ChangeActions.Restarted or ChangeActions.Created or ChangeActions.Started
                ? CauseConfidence.Medium
                : CauseConfidence.High;
            causes.Add(new ProbableCause(CauseKinds.Container, confidence, sentence, [change]));
        }
    }

    /// <summary>What happening to a container reads as, or null for what cannot take a service down.</summary>
    private static string? ContainerVerb(string action) => action switch
    {
        ChangeActions.Image => "got a new image",
        ChangeActions.Recreated => "was recreated",
        ChangeActions.Restarted => "restarted",
        ChangeActions.Stopped => "was stopped",
        ChangeActions.Removed => "was removed",
        ChangeActions.Paused => "was paused",
        ChangeActions.Created => "was created",
        _ => null,
    };

    // ---- certificates ------------------------------------------------------------------

    /// <summary>A certificate check that went down saying its certificate had expired.</summary>
    private static void CertificateExpired(List<IncidentMember> status, List<Change> feed, CauseLab lab, List<ProbableCause> causes)
    {
        foreach (var member in status)
        {
            var down = feed.LastOrDefault(c => c.Kind == ChangeKinds.Status && c.Action == ChangeActions.Down
                                               && c.ConnectionId == member.ConnectionId
                                               && (c.At - member.DownAt).Duration() <= Grace
                                               && c.Detail.StartsWith("Expired", StringComparison.OrdinalIgnoreCase));
            if (down is null)
                continue;
            causes.Add(new ProbableCause(CauseKinds.CertificateExpired, CauseConfidence.High,
                $"The certificate {NameOf(member, lab)} checks has expired.", [down]));
        }
    }

    /// <summary>A certificate replaced or renewed on something involved, or on the same host, just before.</summary>
    private static void Certificates(Incident incident, List<IncidentMember> status, List<Change> feed, CauseLab lab, List<ProbableCause> causes)
    {
        foreach (var change in feed.Where(c => c.Kind == ChangeKinds.Certificate && c.ConnectionId is not null
                                               && Before(c.At, incident.StartedAt)))
        {
            var certificate = lab.Get(change.ConnectionId);
            var touched = status.FirstOrDefault(m => m.ConnectionId == change.ConnectionId
                                                     || (certificate is not null && lab.Get(m.ConnectionId) is { } other
                                                         && other.Hosts.Intersect(certificate.Hosts, StringComparer.OrdinalIgnoreCase).Any()));
            if (touched is null)
                continue;

            var what = change.Action == ChangeActions.Renewed ? "renewed" : "replaced";
            var where = certificate?.Name ?? NameOf(touched, lab);
            var sentence = $"{NameOf(touched, lab)} went down {Gap(change.At, touched.DownAt)} the certificate on {where} was {what}.";
            causes.Add(new ProbableCause(CauseKinds.Certificate,
                change.Action == ChangeActions.Renewed ? CauseConfidence.Low : CauseConfidence.Medium, sentence, [change]));
        }
    }

    // ---- one host ----------------------------------------------------------------------

    /// <summary>
    /// The largest group of unexplained members sharing a host that all failed within
    /// <see cref="HostSpread"/>. Services reached by container name each have a host of
    /// their own, so this is about addresses: several things on 192.168.1.10.
    /// </summary>
    private static void SameHost(List<IncidentMember> status, List<Change> feed, CauseLab lab, List<ProbableCause> causes)
    {
        var groups = status
            .SelectMany(m => (lab.Get(m.ConnectionId)?.Hosts ?? []).Select(h => (Host: h.ToLowerInvariant(), Member: m)))
            .GroupBy(pair => pair.Host)
            .Select(g => (Host: g.Key, Members: g.Select(p => p.Member).DistinctBy(m => m.Key).OrderBy(m => m.DownAt).ToList()))
            .Where(g => g.Members.Count >= 2 && g.Members[^1].DownAt - g.Members[0].DownAt <= HostSpread)
            .OrderByDescending(g => g.Members.Count)
            .ThenBy(g => g.Members[0].DownAt)
            .ToList();
        if (groups.Count == 0)
            return;

        var (host, members) = groups[0];
        var spread = members[^1].DownAt - members[0].DownAt;
        var keys = members.Select(m => m.ConnectionId).ToHashSet();
        var everything = lab.On(host).All(c => keys.Contains(c.Id));
        var when = spread < TimeSpan.FromSeconds(2) ? "at the same moment" : $"within {Words(spread)}";
        var sentence = everything
            ? $"Everything on {host} failed {when} — likely the host or the network to it."
            : $"{members.Count} services on {host} failed {when} — likely the host or the network to it.";

        var evidence = feed
            .Where(c => c.Kind == ChangeKinds.Status && c.Action == ChangeActions.Down && keys.Contains(c.ConnectionId)
                        && c.At >= members[0].DownAt - Grace && c.At <= members[^1].DownAt + Grace)
            .ToList();
        causes.Add(new ProbableCause(CauseKinds.Host,
            everything && members.Count >= 3 ? CauseConfidence.High : CauseConfidence.Medium, sentence, evidence));
    }

    // ---- DNS and LabbyTwo itself -------------------------------------------------------

    private static void Dns(Incident incident, List<IncidentMember> status, List<Change> feed, CauseLab lab, List<ProbableCause> causes)
    {
        // DNS is only checked when somebody asks, so a moved answer is looked for across the
        // whole half hour the timeline shows rather than the usual quarter.
        foreach (var change in feed.Where(c => c.Kind == ChangeKinds.Dns
                                               && c.At >= incident.StartedAt - IncidentRules.ContextBefore
                                               && c.At <= incident.StartedAt + Grace))
        {
            var touched = status.FirstOrDefault(m => lab.Get(m.ConnectionId)?.Hosts.Contains(change.Subject, StringComparer.OrdinalIgnoreCase) == true);
            causes.Add(touched is not null
                ? new ProbableCause(CauseKinds.Dns, CauseConfidence.Medium,
                    $"{change.Subject} started resolving to different addresses {Words(NonNegative(touched.DownAt - change.At))} before {NameOf(touched, lab)} went down.",
                    [change])
                : new ProbableCause(CauseKinds.Dns, CauseConfidence.Low,
                    $"A DNS answer changed {Words(NonNegative(incident.StartedAt - change.At))} before this started: {change.Subject}.",
                    [change]));
        }
    }

    private static void SelfUpdate(Incident incident, List<Change> feed, List<ProbableCause> causes)
    {
        var update = feed.LastOrDefault(c => c.Kind == ChangeKinds.Update && Before(c.At, incident.StartedAt));
        if (update is null)
            return;
        causes.Add(new ProbableCause(CauseKinds.SelfUpdate, CauseConfidence.Medium,
            $"LabbyTwo itself was updated {Words(NonNegative(incident.StartedAt - update.At))} before this started — a new version may check things differently.",
            [update]));
    }

    // ---- capacity ----------------------------------------------------------------------

    private static void Capacity(
        List<IncidentMember> members, List<Change> feed, IReadOnlyDictionary<string, CauseForecast>? forecasts, List<ProbableCause> causes)
    {
        if (forecasts is null)
            return;
        foreach (var member in members.Where(m => m.Kind == ChangeKinds.Alert))
        {
            if (!forecasts.TryGetValue(member.Key, out var forecast)
                || forecast.Forecast.State is not (ForecastState.Filling or ForecastState.Full))
                continue;

            var fired = feed
                .Where(c => c.Kind == ChangeKinds.Alert && c.Action == ChangeActions.Firing && c.ConnectionId == member.ConnectionId
                            && (c.At - member.DownAt).Duration() <= Grace)
                .Take(1)
                .ToList();
            var rate = forecast.Forecast.State == ForecastState.Full
                ? "is at its limit"
                : $"has been rising steadily and will be {forecast.Forecast.Describe()} at this rate";
            causes.Add(new ProbableCause(CauseKinds.Capacity, CauseConfidence.Medium,
                $"Not a sudden fault: {forecast.What} {rate}. It needs room made, not a restart.", fired));
        }
    }

    // ---- words -------------------------------------------------------------------------

    /// <summary>Whether a change at <paramref name="at"/> is in the window before a failure at <paramref name="down"/>.</summary>
    private static bool Before(DateTimeOffset at, DateTimeOffset down) => at >= down - Window && at <= down + Grace;

    /// <summary>"2 minutes after", or "just as" when the two were seen together.</summary>
    private static string Gap(DateTimeOffset change, DateTimeOffset down) =>
        down - change < TimeSpan.FromSeconds(5) ? "just as" : $"{Words(down - change)} after";

    private static TimeSpan NonNegative(TimeSpan span) => span < TimeSpan.Zero ? TimeSpan.Zero : span;

    /// <summary>"40 seconds", "2 minutes", "1 hour 5 minutes" — a gap in the words of a sentence.</summary>
    public static string Words(TimeSpan span)
    {
        span = NonNegative(span);
        if (span.TotalSeconds < 60)
        {
            var seconds = (int)span.TotalSeconds;
            return seconds == 1 ? "1 second" : $"{seconds.ToString(CultureInfo.InvariantCulture)} seconds";
        }
        if (span.TotalMinutes < 60)
        {
            var minutes = (int)span.TotalMinutes;
            return minutes == 1 ? "1 minute" : $"{minutes.ToString(CultureInfo.InvariantCulture)} minutes";
        }
        return Ago.Duration(span);
    }

    /// <summary>A member's connection as it is called now, or as it was called when it failed.</summary>
    private static string NameOf(IncidentMember member, CauseLab lab) => lab.Get(member.ConnectionId)?.Name is { Length: > 0 } name ? name : member.Name;
}
