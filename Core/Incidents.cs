namespace LabbyTwo.Core;

/// <summary>
/// One service or alert rule caught up in an incident.
/// </summary>
/// <param name="Key">What it is, stably: <c>status:{connection}</c> or <c>alert:{rule}:{connection}</c>.</param>
/// <param name="Kind"><see cref="ChangeKinds.Status"/> or <see cref="ChangeKinds.Alert"/>.</param>
/// <param name="Name">What to call it, as it was called when it failed — a connection
/// renamed or a rule deleted later does not rewrite history.</param>
/// <param name="DownAt">When this span of it began: when it went down or fired.</param>
/// <param name="UpAt">When it came back, or null while it has not.</param>
/// <remarks>
/// One row per <em>span</em>. Something that goes down, comes back and goes down again in
/// the same incident has two: the first keeps the recovery it had. It used to be one row
/// whose recovery was wiped by the second failure, so a service that was back in 28
/// seconds was listed as "down for 14h 33m" when it failed again hours later and that
/// second outage ended — the first down time and the last up time of two different events.
/// </remarks>
public sealed record IncidentMember(
    string Key,
    string Kind,
    string? ConnectionId,
    string Name,
    DateTimeOffset DownAt,
    DateTimeOffset? UpAt)
{
    /// <summary>Which time this thing failed in the incident: 1 for the first, 2 for the next.</summary>
    public int Span { get; init; } = 1;

    public bool IsRecovered => UpAt is not null;

    public static string StatusKey(string connectionId) => $"status:{connectionId}";

    public static string AlertKey(string ruleId, string connectionId) => $"alert:{ruleId}:{connectionId}";
}

/// <summary>
/// One outage, however many things it took down: when it started, what was involved, and
/// when the last of them came back. Ten services behind a VPN dropping inside a minute is
/// one incident with ten members, not ten incidents — the same judgement the alerting
/// makes when it holds a child's alert because its parent is down.
///
/// The changes around it are not stored with it. They are in the feed already, and the
/// timeline is the feed read for the incident's window (see <see cref="IncidentRules.TimelineFrom"/>),
/// so nothing is written twice and a change recorded late still turns up.
/// </summary>
/// <param name="Id">Zero until stored.</param>
/// <param name="EndedAt">When the last member recovered; null while any has not.</param>
/// <param name="LastActivity">The newest thing that happened to it — a member failing or
/// recovering. What decides whether a new failure joins it (see <see cref="IncidentRules"/>).</param>
/// <param name="Maintenance">True when any part of it happened while maintenance mode was
/// on. Kept and marked rather than dropped: "the NAS did not come back after I rebooted it"
/// is exactly the incident worth having a record of.</param>
public sealed record Incident(
    long Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    DateTimeOffset LastActivity,
    bool Maintenance,
    IReadOnlyList<IncidentMember> Members)
{
    public bool IsOpen => EndedAt is null;

    /// <summary>
    /// The note written up about it, if there is one — see <c>IncidentWriteUp</c>. Not a
    /// positional parameter, so everything that builds an incident without one still does.
    /// </summary>
    public string? WriteUpNoteId { get; init; }

    /// <summary>How long it lasted, or has lasted so far.</summary>
    public TimeSpan Duration(DateTimeOffset now) => (EndedAt ?? now) - StartedAt;

    /// <summary>
    /// What it is called: the first thing to fail, and how many followed — "NAS", "NAS and
    /// Plex", "NAS and 4 others". The first is named because it is usually the cause.
    /// </summary>
    public string Title
    {
        get
        {
            var names = Members.OrderBy(m => m.DownAt).Select(m => m.Name).Distinct().ToList();
            return names.Count switch
            {
                0 => "Incident",
                1 => names[0],
                2 => $"{names[0]} and {names[1]}",
                _ => $"{names[0]} and {names.Count - 1} others",
            };
        }
    }

    /// <summary>The members still down, oldest first.</summary>
    public IEnumerable<IncidentMember> StillDown => Members.Where(m => !m.IsRecovered).OrderBy(m => m.DownAt);
}

/// <summary>
/// Something that may start, grow or end an incident: a service going down or coming
/// back, an alert rule firing or clearing.
/// </summary>
/// <param name="Bad">True for down or firing; false for up or cleared.</param>
/// <param name="Maintenance">Whether maintenance mode was on when it happened.</param>
public sealed record IncidentSignal(
    string Key,
    string Kind,
    string? ConnectionId,
    string Name,
    bool Bad,
    DateTimeOffset At,
    bool Maintenance = false);

/// <summary>
/// How failures are grouped into incidents. Pure: handed the incidents that could be
/// affected and one signal, it says what the incident looks like afterwards, and the
/// tracker stores that. Every rule is therefore a test rather than an outage staged by hand.
///
/// The rules:
/// <list type="number">
/// <item><b>A failure already counted is ignored.</b> Something that is down in an open
/// incident and is reported down again changes nothing.</item>
/// <item><b>A failure within <see cref="JoinWindow"/> of an incident's last failure, and
/// within <see cref="MaxGrowth"/> of its start, joins it</b> — open or recently closed. A
/// cascade takes a few sweeps to spread (the NAS, then what is stored on it, then what
/// depends on that), and a service that comes back and falls over again five minutes
/// later is the same outage. A closed incident that is joined opens again. Measured from
/// the last <em>failure</em>, not the last thing that happened: a recovery is the outage
/// ending, not spreading, and counting recoveries let an incident whose members kept
/// trickling back absorb every unrelated failure for sixteen hours. And capped from the
/// start, so however it spreads, something failing hours later is a new incident rather
/// than the tail of an old one; not simply "while any is open", so a disk that has been
/// full for a week does not swallow every unrelated outage that happens during it.</item>
/// <item><b>A member failing again in its incident is a new span of it</b> — a second row
/// with its own down and up times — so the first keeps the recovery it had.</item>
/// <item><b>Otherwise it starts a new incident.</b></item>
/// <item><b>A recovery closes its member</b> in the open incident that holds it; when every
/// member has recovered the incident ends at that moment. A recovery with no open incident
/// holding it — something that was down before incidents were being recorded — changes
/// nothing.</item>
/// <item><b>Maintenance marks, never drops.</b> If maintenance mode was on for any signal
/// that touched the incident, it is marked as having happened during maintenance.</item>
/// </list>
/// </summary>
public static class IncidentRules
{
    /// <summary>How close to an incident's last failure a new failure must be to join it.</summary>
    public static readonly TimeSpan JoinWindow = TimeSpan.FromMinutes(15);

    /// <summary>How long after an incident started a new failure may still join it.</summary>
    public static readonly TimeSpan MaxGrowth = TimeSpan.FromHours(2);

    /// <summary>When the last thing in it failed — the newest span's start.</summary>
    public static DateTimeOffset LastFailure(Incident incident) =>
        incident.Members.Count > 0 ? incident.Members.Max(m => m.DownAt) : incident.StartedAt;

    /// <summary>Whether a failure at <paramref name="at"/> belongs to this incident rather than a new one.</summary>
    public static bool CanJoin(Incident incident, DateTimeOffset at) =>
        at >= incident.StartedAt &&
        at - LastFailure(incident) <= JoinWindow &&
        at - incident.StartedAt <= MaxGrowth;

    /// <summary>
    /// How far before the start the timeline reaches back for changes. Half an hour catches
    /// the update that was pulled, the container that was recreated and the certificate that
    /// was replaced just before things went wrong — the usual suspects — without filling
    /// the timeline with the morning's routine.
    /// </summary>
    public static readonly TimeSpan ContextBefore = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How far past the end the timeline carries on, so whatever was done to fix it — the
    /// restart that brought it back — is on it too.
    /// </summary>
    public static readonly TimeSpan ContextAfter = TimeSpan.FromMinutes(5);

    /// <summary>Where an incident's timeline starts.</summary>
    public static DateTimeOffset TimelineFrom(Incident incident) => incident.StartedAt - ContextBefore;

    /// <summary>Where an incident's timeline ends: a little past its end, or now for one still open.</summary>
    public static DateTimeOffset TimelineTo(Incident incident, DateTimeOffset now) =>
        incident.EndedAt is { } ended ? Min(ended + ContextAfter, now) : now;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    /// <summary>
    /// The incident <paramref name="signal"/> creates or changes, or null when it changes
    /// nothing. A new incident comes back with an <see cref="Incident.Id"/> of zero.
    /// </summary>
    /// <param name="candidates">
    /// Every incident the signal could touch: all open ones, and any closed within
    /// <see cref="JoinWindow"/>. Anything older can be left out; it is never chosen.
    /// </param>
    public static Incident? Apply(IReadOnlyCollection<Incident> candidates, IncidentSignal signal) =>
        signal.Bad ? Fail(candidates, signal) : Recover(candidates, signal);

    private static Incident? Fail(IReadOnlyCollection<Incident> candidates, IncidentSignal signal)
    {
        if (candidates.Any(i => i.IsOpen && i.Members.Any(m => m.Key == signal.Key && !m.IsRecovered)))
            return null;

        // Among those it could join, the one it has already been part of first — a flap is
        // the same outage before it is a neighbour's — then the most recently active.
        var target = candidates
            .Where(i => CanJoin(i, signal.At))
            .OrderByDescending(i => i.Members.Any(m => m.Key == signal.Key))
            .ThenByDescending(i => i.LastActivity)
            .FirstOrDefault();

        if (target is null)
        {
            return new Incident(0, signal.At, null, signal.At, signal.Maintenance,
                [new IncidentMember(signal.Key, signal.Kind, signal.ConnectionId, signal.Name, signal.At, null)]);
        }

        var members = target.Members.ToList();
        var spans = members.Count(m => m.Key == signal.Key);
        members.Add(new IncidentMember(signal.Key, signal.Kind, signal.ConnectionId, signal.Name, signal.At, null)
        {
            Span = spans + 1,
        });

        return target with
        {
            EndedAt = null,
            LastActivity = Max(target.LastActivity, signal.At),
            Maintenance = target.Maintenance || signal.Maintenance,
            Members = members,
        };
    }

    private static Incident? Recover(IReadOnlyCollection<Incident> candidates, IncidentSignal signal)
    {
        var target = candidates
            .Where(i => i.IsOpen && i.Members.Any(m => m.Key == signal.Key && !m.IsRecovered))
            .OrderByDescending(i => i.LastActivity)
            .FirstOrDefault();
        if (target is null)
            return null;

        var members = target.Members
            .Select(m => m.Key == signal.Key && !m.IsRecovered ? m with { UpAt = Max(signal.At, m.DownAt) } : m)
            .ToList();

        return Close(target with
        {
            LastActivity = Max(target.LastActivity, signal.At),
            Maintenance = target.Maintenance || signal.Maintenance,
            Members = members,
        });
    }

    /// <summary>
    /// Closes members whose source says they are fine now, though no recovery was ever
    /// signalled — the rule was deleted, the connection removed, or LabbyTwo was restarted
    /// and the alert evaluator, which keeps its state in memory, started again from nothing
    /// and so will never say "cleared" for something it no longer knows was firing.
    /// </summary>
    /// <param name="stillBad">True if the member is still down, false if it is fine, null
    /// if there is no way to tell yet — which leaves it as it is.</param>
    /// <returns>The incident with those members closed, or null if none were.</returns>
    public static Incident? Reconcile(Incident incident, Func<IncidentMember, bool?> stillBad, DateTimeOffset now)
    {
        if (!incident.IsOpen)
            return null;

        var changed = false;
        var members = incident.Members.Select(m =>
        {
            if (m.IsRecovered || stillBad(m) != false)
                return m;
            changed = true;
            return m with { UpAt = Max(now, m.DownAt) };
        }).ToList();

        return changed ? Close(incident with { LastActivity = Max(incident.LastActivity, now), Members = members }) : null;
    }

    /// <summary>Ends an incident whose members have all recovered, at the moment the last one did.</summary>
    private static Incident Close(Incident incident) =>
        incident.Members.Count > 0 && incident.Members.All(m => m.IsRecovered)
            ? incident with { EndedAt = incident.Members.Max(m => m.UpAt!.Value) }
            : incident;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
