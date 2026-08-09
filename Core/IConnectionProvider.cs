namespace LabbyTwo.Core;

/// <summary>
/// Everything LabbyTwo knows how to talk to. A provider describes its own settings
/// fields and knows how to probe one instance; the rest of the app — the add-connection
/// form, the Test button, the health monitor, the charts — is written against this
/// interface alone, so adding an integration means adding one class.
/// </summary>
public interface IConnectionProvider
{
    /// <summary>Stable key stored on the connection row, e.g. "qnap". Never change it.</summary>
    string Type { get; }

    string DisplayName { get; }
    string Icon { get; }
    string Description { get; }

    /// <summary>Grouping for the "add connection" picker.</summary>
    string Category => "General";

    /// <summary>
    /// False for things that exist to be *used* rather than watched — an alert channel,
    /// say. The monitor skips them, and they stay out of the up/down counts so a webhook
    /// URL never shows up as a service that is down.
    /// </summary>
    bool IsMonitored => true;

    IReadOnlyList<FieldSpec> Fields { get; }

    /// <summary>
    /// How to label and format the numbers this provider reports. Only what is specific
    /// to it — <see cref="MetricSpec.WellKnown"/> already covers latency, CPU and the
    /// other names that mean the same thing everywhere, and anything undeclared falls
    /// back to a humanised key. Declaring a metric here is also what puts it in the
    /// widget editor's metric dropdown instead of leaving the user to type it.
    /// </summary>
    IReadOnlyList<MetricSpec> Metrics => [];

    /// <summary>
    /// Metrics for one configured instance. Overridden by providers whose metric set is
    /// decided by the user rather than the code — the JSON API provider, where the
    /// mapping is typed into a settings box.
    /// </summary>
    IReadOnlyList<MetricSpec> MetricsFor(Connection connection) => Metrics;

    /// <summary>
    /// Alert rules worth offering for this kind of thing. The person who wrote the
    /// integration knows which of its numbers actually matter; the user should not have
    /// to infer that from a list of metric names. Offered on the Alerts page, never
    /// created behind anyone's back.
    /// </summary>
    IReadOnlyList<SuggestedRule> SuggestedRules => [];

    /// <summary>
    /// One round trip. Doubles as the Test button and as the health monitor's poll, so
    /// what the user tests when adding a connection is exactly what gets monitored.
    /// Implementations should not throw — return a failed <see cref="ProbeResult"/>.
    /// </summary>
    Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct);
}

/// <summary>
/// The outcome of one probe. <see cref="Metrics"/> is the generic hook that gives every
/// provider charts for free: whatever numbers it reports get sampled into history and
/// become selectable in the chart widget without any chart-side knowledge of the source.
/// </summary>
public sealed record ProbeResult(
    bool Ok,
    string Message,
    TimeSpan Duration,
    IReadOnlyDictionary<string, double>? Metrics = null,
    IReadOnlyDictionary<string, string>? Details = null)
{
    public static ProbeResult Up(TimeSpan duration, string message = "OK",
        IReadOnlyDictionary<string, double>? metrics = null,
        IReadOnlyDictionary<string, string>? details = null)
        => new(true, message, duration, metrics, details);

    public static ProbeResult Down(TimeSpan duration, string message)
        => new(false, message, duration);
}

