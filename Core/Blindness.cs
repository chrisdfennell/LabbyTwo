namespace LabbyTwo.Core;

/// <summary>Why LabbyTwo cannot see the lab, when it cannot.</summary>
public enum BlindCause
{
    None,

    /// <summary>Name lookups are failing inside LabbyTwo's container — EAI_AGAIN, "could not resolve".</summary>
    Dns,

    /// <summary>Probes are running into the monitor's own deadline and being abandoned.</summary>
    Abandoned,

    /// <summary>The Docker socket on the machine LabbyTwo runs on is not answering.</summary>
    Docker,

    /// <summary>LabbyTwo's own database is refusing writes — "database is locked".</summary>
    Database,

    /// <summary>A sweep took many times longer than it should: LabbyTwo itself is struggling.</summary>
    Stalled,
}

/// <summary>What one probe in a sweep came to, for <see cref="BlindnessRules.Assess"/>.</summary>
public sealed record ProbeOutcome(string ConnectionId, bool Ok, ProbeFailure Failure);

/// <summary>
/// Whether LabbyTwo can see the lab right now. While <see cref="Impaired"/>, what a probe
/// failure says is about LabbyTwo, not about the service it was asking — so nothing is
/// turned red, nobody is paged, no incident is opened, nothing is restarted and no update
/// is judged on it. See <see cref="BlindnessRules"/>.
/// </summary>
/// <param name="Since">When it started, while impaired.</param>
/// <param name="Blind">How many probes in the last sweep failed in a way that is LabbyTwo's own.</param>
/// <param name="Probed">How many probes the last sweep made.</param>
/// <param name="ClearSweeps">Sweeps in a row that looked fine while still impaired — it
/// takes <see cref="BlindnessRules.SweepsToClear"/> to end it, so one lucky sweep in the
/// middle of a DNS outage does not end it and start it again.</param>
/// <param name="LastSweep">How long the previous sweep took, for the <see cref="BlindCause.Stalled"/> sentence.</param>
public sealed record Blindness(
    bool Impaired,
    BlindCause Cause,
    DateTimeOffset? Since,
    int Blind,
    int Probed,
    int ClearSweeps = 0,
    TimeSpan? LastSweep = null)
{
    public static readonly Blindness Clear = new(false, BlindCause.None, null, 0, 0);

    /// <summary>How long it has lasted so far; zero when it is not happening.</summary>
    public TimeSpan For(DateTimeOffset now) => Impaired && Since is { } since && now > since ? now - since : TimeSpan.Zero;

    /// <summary>What is wrong, in words that finish "LabbyTwo can't see the lab right now: …".</summary>
    public string Reason => Cause switch
    {
        BlindCause.Dns => "DNS lookups are failing inside its container",
        BlindCause.Abandoned => "its checks are not getting answers in time, so the machine it runs on may be overloaded",
        BlindCause.Docker => "the Docker socket on the machine it runs on is not answering",
        BlindCause.Database => "its own database is locked",
        BlindCause.Stalled => LastSweep is { } took
            ? $"its checks are running far behind — the last round took {Words(took)} instead of seconds"
            : "its checks are running far behind",
        _ => "",
    };

    /// <summary>The banner: what is wrong, and that it is not the services.</summary>
    public string Headline => $"LabbyTwo can't see the lab right now: {Reason} — this is not your services.";

    /// <summary>
    /// What the banner says under the headline: what LabbyTwo is and is not doing about it,
    /// and what usually fixes it.
    /// </summary>
    public string Detail
    {
        get
        {
            var share = Probed > 0 && Blind > 0 ? $"{Blind} of {Probed} checks in the last round could not see their service. " : "";
            var fix = Cause switch
            {
                BlindCause.Dns => "Usually Docker's own DNS inside the container has stopped answering — often during or after " +
                                  "many containers are recreated at once. Giving LabbyTwo a DNS server with \"dns:\" in " +
                                  "docker-compose.yml makes it independent of that; restarting LabbyTwo's container clears it.",
                BlindCause.Docker => "The Docker daemon is busy or hung. It usually recovers by itself; if not, look at what " +
                                     "is holding it — an update helper that never finished is a common one.",
                BlindCause.Database => "Something is holding LabbyTwo's database. It usually clears by itself.",
                _ => "It usually clears by itself.",
            };
            return $"{share}Until it clears, every service keeps its last known state, and nothing is alerted on, opened " +
                   $"as an incident, self-healed or rolled back. {fix}";
        }
    }

    private static string Words(TimeSpan span) => span.TotalMinutes >= 2
        ? $"{span.TotalMinutes:0} minutes"
        : $"{span.TotalSeconds:0} seconds";
}

/// <summary>
/// How LabbyTwo decides it cannot see, rather than that the lab is down. Pure: handed the
/// last state, one sweep's outcomes and how long the previous sweep took, it says what the
/// state is now — so every threshold is a test rather than a night of false alarms.
///
/// The case that forced it: a NAS recreating fifteen containers at once, after which
/// Docker's embedded DNS inside LabbyTwo's container stopped answering for eleven hours.
/// Every probe by name failed with EAI_AGAIN, the ones by address were abandoned at the
/// deadline, the Docker socket timed out — and every one of them was reported as that
/// service being down, paged, opened as an incident and, the next morning, used to roll
/// back two containers that were fine.
///
/// <list type="bullet">
/// <item><b>It starts</b> when, in one sweep, at least <see cref="MinimumBlind"/> probes and
/// at least <see cref="Share"/> of all of them failed in a way that is LabbyTwo's own —
/// DNS, the probe deadline, the local Docker socket, the local database (see
/// <see cref="ProbeFailures.IsBlind"/>); or when the Docker socket on this machine does not
/// answer at all; or when the previous sweep took longer than <see cref="StallLimit"/>.
/// One name that does not resolve is a real misconfiguration and is reported as one; half
/// the lab failing to resolve in the same thirty seconds is not.</item>
/// <item><b>It ends</b> after <see cref="SweepsToClear"/> sweeps in a row with none of that.</item>
/// <item>It is judged before any probe of the sweep is recorded, so the sweep that
/// discovers it is already held.</item>
/// </list>
/// </summary>
public static class BlindnessRules
{
    /// <summary>The fewest failures of LabbyTwo's own that can start it. Below this, one bad name is one bad name.</summary>
    public const int MinimumBlind = 4;

    /// <summary>The share of a sweep's probes that must have failed that way.</summary>
    public const double Share = 0.5;

    /// <summary>Clean sweeps in a row that end it.</summary>
    public const int SweepsToClear = 2;

    /// <summary>
    /// How long it must last before somebody is told, once. Shorter blind spells — the
    /// minute Docker takes to recreate a stack — are worth a line in the feed and no more.
    /// </summary>
    public static readonly TimeSpan NotifyAfter = TimeSpan.FromMinutes(15);

    /// <summary>A sweep this slow means LabbyTwo itself is struggling: four periods, and never under two minutes.</summary>
    public static TimeSpan StallLimit(TimeSpan period) =>
        period * 4 > TimeSpan.FromMinutes(2) ? period * 4 : TimeSpan.FromMinutes(2);

    /// <param name="previous">The state before this sweep.</param>
    /// <param name="sweep">What every probe in this sweep came to.</param>
    /// <param name="lastSweep">How long the previous sweep took, or null for the first.</param>
    /// <param name="period">How often a sweep is meant to start.</param>
    public static Blindness Assess(
        Blindness previous, IReadOnlyCollection<ProbeOutcome> sweep, TimeSpan? lastSweep, TimeSpan period, DateTimeOffset now)
    {
        var blind = sweep.Count(o => !o.Ok && o.Failure.IsBlind());
        var cause = CauseOf(sweep, blind, lastSweep, period);

        if (cause != BlindCause.None)
        {
            return previous.Impaired
                ? previous with { Cause = cause, Blind = blind, Probed = sweep.Count, ClearSweeps = 0, LastSweep = lastSweep }
                : new Blindness(true, cause, now, blind, sweep.Count, 0, lastSweep);
        }

        if (!previous.Impaired)
            return Blindness.Clear;

        var clear = previous.ClearSweeps + 1;
        return clear >= SweepsToClear
            ? Blindness.Clear
            : previous with { Blind = blind, Probed = sweep.Count, ClearSweeps = clear };
    }

    private static BlindCause CauseOf(IReadOnlyCollection<ProbeOutcome> sweep, int blind, TimeSpan? lastSweep, TimeSpan period)
    {
        // The socket is on this machine: nothing between here and there to blame but Docker.
        if (sweep.Any(o => !o.Ok && o.Failure == ProbeFailure.DockerSocket))
            return BlindCause.Docker;

        if (blind >= MinimumBlind && blind >= sweep.Count * Share)
        {
            // Named after the commonest, ties going to the one most likely to be the root:
            // a DNS outage also makes probes run into the deadline, not the other way round.
            return sweep
                .Where(o => !o.Ok && o.Failure.IsBlind())
                .GroupBy(o => o.Failure)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key switch
                {
                    ProbeFailure.Dns => 0,
                    ProbeFailure.Database => 1,
                    ProbeFailure.DockerSocket => 2,
                    _ => 3,
                })
                .First().Key switch
            {
                ProbeFailure.Dns => BlindCause.Dns,
                ProbeFailure.Database => BlindCause.Database,
                ProbeFailure.DockerSocket => BlindCause.Docker,
                _ => BlindCause.Abandoned,
            };
        }

        if (lastSweep is { } took && took > StallLimit(period))
            return BlindCause.Stalled;

        return BlindCause.None;
    }
}
