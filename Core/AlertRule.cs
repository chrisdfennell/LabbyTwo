namespace LabbyTwo.Core;

public enum Comparison
{
    Above,
    Below,
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
    public AlertRule ForConnection(string connectionId) => new()
    {
        Name = Name,
        ConnectionId = connectionId,
        Metric = Metric,
        Comparison = Comparison,
        Threshold = Threshold,
        ClearThreshold = ClearThreshold,
        ForMinutes = ForMinutes,
    };

    public string ComparisonWord => Comparison == Comparison.Above ? "above" : "below";

    /// <summary>Whether an existing rule already covers this, so it stops being offered.</summary>
    public bool IsCoveredBy(AlertRule rule, string connectionId) =>
        (rule.ConnectionId is null || rule.ConnectionId == connectionId)
        && string.Equals(rule.Metric, Metric, StringComparison.OrdinalIgnoreCase)
        && rule.Comparison == Comparison;
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

    public double ClearsAt => ClearThreshold ?? Threshold;

    public bool IsBreaching(double value) =>
        Comparison == Comparison.Above ? value >= Threshold : value <= Threshold;

    /// <summary>
    /// Deliberately not just <c>!IsBreaching</c>: between the two thresholds the rule
    /// holds whatever state it is in, which is the whole point of hysteresis.
    /// </summary>
    public bool IsCleared(double value) =>
        Comparison == Comparison.Above ? value < ClearsAt : value > ClearsAt;

    public string ComparisonWord => Comparison == Comparison.Above ? "above" : "below";

    /// <summary>A readable name for a rule the user did not name.</summary>
    public string Describe(string metricLabel, string? connectionName)
    {
        if (Name is { Length: > 0 })
            return Name;

        // Empty, not just null: callers look a name up by id and get "" when the rule is
        // not pinned to a connection, which would otherwise render a dangling separator.
        var who = string.IsNullOrWhiteSpace(connectionName) ? "Any connection" : connectionName;
        return $"{who} · {metricLabel} {ComparisonWord} {Threshold:0.##}";
    }
}
