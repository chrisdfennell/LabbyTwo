namespace LabbyTwo.Core;

public enum Comparison
{
    Above,
    Below,

    /// <summary>
    /// Only for an <see cref="RuleKind.Unusual"/> rule — "unusually high or low". A fixed
    /// threshold has one side; a rule firing both above and below the same number would be
    /// "not equal to", which no metric wants.
    /// </summary>
    Either,
}

/// <summary>
/// An alert rule a provider thinks is worth having. Whoever wrote the integration knows
/// that 0°C matters to a weather station and that a UPS on battery is news; the user
/// should not have to work that out from a list of metric names. Offered, never created
/// automatically — an alert nobody asked for is how people learn to ignore alerts.
/// </summary>
/// <param name="Why">One line on what the rule is for, shown next to the offer.</param>
public sealed record SuggestedRule(
    string Name,
    string Metric,
    Comparison Comparison,
    double Threshold,
    double? ClearThreshold = null,
    int ForMinutes = 0,
    string Why = "")
{
    /// <summary>
    /// Set after construction rather than as another positional parameter, so a plugin
    /// compiled against the constructor it always had keeps loading.
    /// </summary>
    public RuleKind Kind { get; init; } = RuleKind.Threshold;

    /// <summary>For an <see cref="RuleKind.Unusual"/> rule, what <see cref="Threshold"/> is measured in.</summary>
    public UnusualBy UnusualBy { get; init; } = UnusualBy.Percent;

    public AlertRule ForConnection(string connectionId) => new()
    {
        Name = Name,
        ConnectionId = connectionId,
        Metric = Metric,
        Comparison = Comparison,
        Threshold = Threshold,
        ClearThreshold = ClearThreshold,
        ForMinutes = ForMinutes,
        Kind = Kind,
        UnusualBy = UnusualBy,
    };

    public string ComparisonWord => Comparison == Comparison.Above ? "above" : "below";

    /// <summary>
    /// Whether an existing rule already covers this, so it stops being offered. A fixed
    /// threshold does not cover "unusual for the hour" or the other way round: they catch
    /// different faults, and somebody with one has not thereby declined the other.
    /// </summary>
    public bool IsCoveredBy(AlertRule rule, string connectionId) =>
        (rule.ConnectionId is null || rule.ConnectionId == connectionId)
        && string.Equals(rule.Metric, Metric, StringComparison.OrdinalIgnoreCase)
        && rule.Comparison == Comparison
        && rule.Kind == Kind;
}

/// <summary>
/// "Tell me when this number goes wrong." Up/down alerting only covers a service being
/// unreachable, but the interesting failures are gradual — a volume filling, a UPS
/// draining, a temperature climbing. Every provider already reports numbers, so a rule
/// here works against any of them, including ones nobody has written yet.
/// </summary>
public sealed record AlertRule
{
    public string Id { get; init; } = Ids.New();

    /// <summary>Optional. Blank renders a description of the rule instead.</summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// Null means every connection that reports <see cref="Metric"/>. One rule for
    /// "any disk over 90%" then covers a NAS added next month with no edit.
    /// </summary>
    public string? ConnectionId { get; init; }

    public string Metric { get; init; } = "";
    public Comparison Comparison { get; init; } = Comparison.Above;
    public double Threshold { get; init; }

    /// <summary>
    /// The value it has to come back past before the alert clears. Null uses
    /// <see cref="Threshold"/>. Setting it apart from the threshold is what stops a
    /// metric hovering on the line from alerting every sweep.
    /// </summary>
    public double? ClearThreshold { get; init; }

    /// <summary>
    /// How long the condition must hold before anything is sent. Zero fires on the first
    /// sweep; a few minutes filters the spike that a backup job causes every night.
    /// </summary>
    public int ForMinutes { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Which alert channel this rule speaks through. Null means all of them, which is what
    /// every rule did before routing existed — so an upgrade changes nothing until asked.
    /// </summary>
    public string? ChannelId { get; init; }

    /// <summary>
    /// A fixed threshold, or "unusual for the time". An unusual rule keeps the same fields
    /// meaning nearly the same things — <see cref="Threshold"/> is how far from usual fires
    /// it, <see cref="ClearThreshold"/> how close it must come back to clear, and
    /// <see cref="ForMinutes"/> the same sustain window — so it runs through the one engine
    /// rather than a second one that would drift from the first.
    /// </summary>
    public RuleKind Kind { get; init; } = RuleKind.Threshold;

    /// <summary>What <see cref="Threshold"/> is measured in, for an unusual rule. Ignored otherwise.</summary>
    public UnusualBy UnusualBy { get; init; } = UnusualBy.Percent;

    public bool IsUnusual => Kind == RuleKind.Unusual;

    public double ClearsAt => ClearThreshold ?? Threshold;

    /// <summary>
    /// Strictly past the line, because that is what the editor says: "above" and "below".
    /// It used to be "at or above", and every rule whose healthy value sits exactly on its
    /// threshold fired while healthy — "a disk is failing" at 0 failing disks, "an AD
    /// service has gone" with all 4 answering, "DNS has stopped" with DNS answering — and,
    /// firing state living in memory, fired again after every restart.
    /// </summary>
    public bool IsBreaching(double value) =>
        Comparison == Comparison.Above ? value > Threshold : value < Threshold;

    /// <summary>
    /// Back on the right side of the clearing line, the line included. Deliberately not just
    /// <c>!IsBreaching</c> when a separate clear threshold is set: between the two the rule
    /// holds whatever state it is in, which is the whole point of hysteresis. With none set
    /// the two lines are the same and this is exactly the complement of breaching — so a
    /// value sitting on the threshold is cleared, never stuck "holding" a firing alert.
    /// </summary>
    public bool IsCleared(double value) =>
        Comparison == Comparison.Above ? value <= ClearsAt : value >= ClearsAt;

    /// <summary>
    /// Where an unusual rule clears when no clear threshold was set: two thirds of the way
    /// from usual out to the trigger. Clearing the moment it stops breaching would announce
    /// a recovery every time a slow evening wobbled back across the line, and waiting for it
    /// to be all the way back to usual would hold an alert through a recovery that has
    /// plainly happened.
    /// </summary>
    public double UnusualClearsAt => ClearThreshold ?? UnusualBy switch
    {
        UnusualBy.Percent => 100 + (Threshold - 100) * 2 / 3,
        _ => Threshold * 2 / 3,
    };

    /// <summary>
    /// Judges one reading. <paramref name="usual"/> is only read for an unusual rule, and
    /// null for one means there is nothing to judge against yet — as does a percentage of a
    /// usual value of zero. A null verdict is "no reading": the engine neither fires nor
    /// clears on it, exactly as for a probe that failed.
    /// </summary>
    public Verdict? Judge(double value, Usual? usual)
    {
        if (!IsUnusual)
            return IsBreaching(value) ? Verdict.Breaching : IsCleared(value) ? Verdict.Cleared : Verdict.Holding;

        if (usual is null)
            return null;

        var clear = UnusualClearsAt;
        bool breaching, cleared;
        if (UnusualBy == UnusualBy.Percent)
        {
            var percent = usual.PercentOf(value);
            if (double.IsNaN(percent))
                return null;

            // Either way is symmetric as a ratio rather than as a difference — "double or
            // half" — since a speed that halved has moved as far as one that doubled.
            (breaching, cleared) = Comparison switch
            {
                Comparison.Below => (percent <= Threshold, percent > clear),
                Comparison.Above => (percent >= Threshold, percent < clear),
                _ => (percent >= Threshold || percent <= 10000 / Threshold,
                      percent < clear && percent > 10000 / clear),
            };
        }
        else
        {
            var z = usual.SpreadsFrom(value);
            (breaching, cleared) = Comparison switch
            {
                Comparison.Below => (z <= -Threshold, z > -clear),
                Comparison.Above => (z >= Threshold, z < clear),
                _ => (Math.Abs(z) >= Threshold, Math.Abs(z) < clear),
            };
        }
        return breaching ? Verdict.Breaching : cleared ? Verdict.Cleared : Verdict.Holding;
    }

    /// <summary>
    /// Why this rule cannot be saved as it stands, or null. Every way it can be wrong ends
    /// the same way — an alert that fires and can never clear, or can never fire — which is
    /// worse than no rule, so the editor refuses rather than warns.
    /// </summary>
    public string? Problem()
    {
        if (!IsUnusual)
        {
            if (Comparison == Comparison.Either)
                return "A fixed threshold needs a side — above or below.";

            // A clear threshold on the wrong side of the trigger would mean the alert can
            // fire and never clear.
            if (ClearThreshold is { } clear)
            {
                if (Comparison == Comparison.Above && clear > Threshold)
                    return $"For an “above” rule, “clears at” must be at or below {Threshold:0.##} — otherwise it could never clear.";
                if (Comparison == Comparison.Below && clear < Threshold)
                    return $"For a “below” rule, “clears at” must be at or above {Threshold:0.##} — otherwise it could never clear.";
            }
            return null;
        }

        if (UnusualBy == UnusualBy.Spread)
        {
            if (Threshold <= 0)
                return "How far from usual has to be more than nothing, or every reading is unusual.";
            if (ClearThreshold is { } band && (band < 0 || band > Threshold))
                return $"“Clears within” must be between 0 and {Threshold:0.##} — otherwise it could never clear.";
            return null;
        }

        if (Comparison == Comparison.Below)
        {
            if (Threshold is <= 0 or >= 100)
                return "For “lower than usual”, the percentage has to be under 100 — 50 is “less than half its usual”.";
            if (ClearThreshold is { } band && (band < Threshold || band > 100))
                return $"“Clears at” must be between {Threshold:0.##}% and 100% of usual — otherwise it could never clear.";
            return null;
        }

        if (Threshold <= 100)
            return "For “higher than usual” or “either way”, the percentage has to be over 100 — 200 is “double its usual”.";
        if (ClearThreshold is { } upper && (upper < 100 || upper > Threshold))
            return $"“Clears at” must be between 100% and {Threshold:0.##}% of usual — otherwise it could never clear.";
        return null;
    }

    /// <summary>
    /// An unusual rule's condition in words — "less than half its usual", "well above
    /// usual" — which reads better in a list and a notification than "z ≥ 4" does.
    /// </summary>
    public string UnusualPhrase()
    {
        if (UnusualBy == UnusualBy.Spread)
        {
            var how = Threshold switch
            {
                < 2.5 => "a little",
                < 3.5 => "noticeably",
                < 5 => "well",
                _ => "far",
            };
            return Comparison switch
            {
                Comparison.Above => $"{how} above usual",
                Comparison.Below => $"{how} below usual",
                _ => $"{how} away from usual",
            };
        }

        return (Comparison, Threshold) switch
        {
            (Comparison.Below, 50) => "less than half its usual",
            (Comparison.Below, 25) => "less than a quarter of its usual",
            (Comparison.Below, 75) => "less than three quarters of its usual",
            (Comparison.Below, _) => $"under {Threshold:0.##}% of its usual",
            (Comparison.Above, 150) => "half as much again as usual",
            (Comparison.Above, 200) => "more than double its usual",
            (Comparison.Above, 300) => "more than triple its usual",
            (Comparison.Above, _) => $"over {Threshold:0.##}% of its usual",
            (_, 200) => "double or half its usual",
            _ => $"{Threshold / 100:0.##}× its usual either way",
        };
    }

    public string ComparisonWord => Comparison == Comparison.Above ? "above" : "below";

    /// <summary>A readable name for a rule the user did not name.</summary>
    public string Describe(string metricLabel, string? connectionName)
    {
        if (Name is { Length: > 0 })
            return Name;

        // Empty, not just null: callers look a name up by id and get "" when the rule is
        // not pinned to a connection, which would otherwise render a dangling separator.
        var who = string.IsNullOrWhiteSpace(connectionName) ? "Any connection" : connectionName;
        return IsUnusual
            ? $"{who} · {metricLabel} {UnusualPhrase()}"
            : $"{who} · {metricLabel} {ComparisonWord} {Threshold:0.##}";
    }

    /// <summary>
    /// A stored comparison, read back. Anything unrecognised is "above", which is what every
    /// rule was before "below" existed.
    /// </summary>
    public static Comparison ParseComparison(string? stored) =>
        stored?.Trim().ToLowerInvariant() switch
        {
            "below" => Comparison.Below,
            "either" => Comparison.Either,
            _ => Comparison.Above,
        };

    /// <summary>
    /// A stored kind, read back. Missing — a row or a file from before unusual rules — or
    /// unrecognised is a threshold, which is what every such rule was.
    /// </summary>
    public static RuleKind ParseKind(string? stored) =>
        string.Equals(stored?.Trim(), "unusual", StringComparison.OrdinalIgnoreCase) ? RuleKind.Unusual : RuleKind.Threshold;

    public static UnusualBy ParseUnusualBy(string? stored) =>
        string.Equals(stored?.Trim(), "spread", StringComparison.OrdinalIgnoreCase) ? UnusualBy.Spread : UnusualBy.Percent;

    /// <summary>How a kind is stored, in the database and in an export.</summary>
    public static string StoredKind(RuleKind kind) => kind == RuleKind.Unusual ? "unusual" : "threshold";

    public static string StoredUnusualBy(UnusualBy by) => by == UnusualBy.Spread ? "spread" : "percent";
}
