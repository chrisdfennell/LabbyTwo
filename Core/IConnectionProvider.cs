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

    /// <summary>
    /// The least often this is worth asking. Zero — the default — means every sweep, which
    /// is right for anything that answers from your own network and reports a number that
    /// is true at the moment you ask.
    ///
    /// Override it for an upstream that publishes on a schedule, and especially for one
    /// that meters how often you ask. A weather forecast is recomputed hourly and a public
    /// API is entitled to be annoyed at being asked for it every thirty seconds; the
    /// answer would be the same 119 times out of 120, and the 120th would be a quota
    /// error. The monitor skips the connection until this much time has passed, so what
    /// gets recorded is a real probe taken less often rather than a cached one pretending
    /// to be fresh.
    ///
    /// The Test button ignores this and always makes the request — testing a connection
    /// that answered from a cache would be testing nothing.
    /// </summary>
    TimeSpan MinimumInterval => TimeSpan.Zero;

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

    /// <summary>
    /// Buttons this provider offers — restart the NAS, disable blocking for ten minutes.
    /// Empty for the great majority, which only watch. See <see cref="ProviderAction"/>
    /// for why an action belongs on the provider rather than on a card holding its own
    /// copy of the password.
    /// </summary>
    IReadOnlyList<ProviderAction> Actions => [];

    /// <summary>
    /// Actions for one configured instance. Override where the answer depends on how the
    /// connection was set up — a NAS with no MAC address recorded cannot be woken, and
    /// offering the button anyway only teaches people the button does not work.
    /// </summary>
    IReadOnlyList<ProviderAction> ActionsFor(Connection connection) => Actions;

    /// <summary>
    /// Runs one of them. <paramref name="input"/> holds whatever
    /// <see cref="ProviderAction.Fields"/> asked for, empty when it asked for nothing.
    /// Like <see cref="ProbeAsync"/>, prefer a failed <see cref="ActionResult"/> to an
    /// exception — the runner catches either, but only one of them can explain itself.
    /// </summary>
    Task<ActionResult> RunActionAsync(Connection connection, ProviderAction action, SettingsBag input, CancellationToken ct)
        => Task.FromResult(ActionResult.Failed($"{DisplayName} does not know how to run “{action.Id}”."));
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

