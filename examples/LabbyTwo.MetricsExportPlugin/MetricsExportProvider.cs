using LabbyTwo.Core;

namespace LabbyTwo.MetricsExportPlugin;

/// <summary>
/// Not something to talk to — something to configure. The endpoint next door needs a token
/// and a metric prefix, and this is where they live.
///
/// A connection rather than a config file or a settings page of its own, for the reason
/// <see cref="IAlertChannel"/> gives: a thing you configure gets the generated form, the
/// encrypted secret storage and the picker entry that every other configurable thing in
/// LabbyTwo already has. Adding a parallel settings system to hold two strings would be
/// the worse trade twice over.
///
/// Add more than one and each token works, which is how you hand Grafana Cloud its own
/// credential and revoke it later without disturbing the Prometheus in the rack.
/// </summary>
public sealed class MetricsExportProvider : IConnectionProvider
{
    public const string ProviderType = "metrics-export";

    public string Type => ProviderType;
    public string DisplayName => "Prometheus export";
    public string Icon => "📤";
    public string Category => "Monitoring";

    public string Description =>
        "Publishes everything LabbyTwo measures at /ext/metrics, in Prometheus' text format. " +
        "Point Prometheus at it and every probe the dashboard already runs becomes a series you can graph and keep.";

    /// <summary>Nothing to poll. This exists to be scraped, not watched.</summary>
    public bool IsMonitored => false;

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("token", "Scrape token", FieldKind.Password, Required: true,
            Help: "Prometheus sends this as a bearer token, so the endpoint can answer without a login. " +
                  "Generate one with: openssl rand -hex 24"),

        new("prefix", "Metric prefix", FieldKind.Text, "labby", Default: "labby",
            Help: "What every exported series is called before the metric name — labby_cpu_percent, " +
                  "labby_up. Change it only if something else in your Prometheus already owns that prefix.")
        {
            Advanced = true,
        },

        new("include_disabled", "Include disabled connections", FieldKind.Bool, Default: "false",
            Help: "Off by default. A connection you switched off is one you decided to stop caring about, " +
                  "and exporting it only gives Prometheus something to alert on.")
        {
            Advanced = true,
        },
    ];

    public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
        => Task.FromResult(connection.Settings.Get("token").Length >= 16
            ? ProbeResult.Up(TimeSpan.Zero, $"Scrape {ExtensionRoutes.PathFor(MetricsEndpoints.RouteKey)} with this token.")
            : ProbeResult.Down(TimeSpan.Zero,
                "A scrape token of at least 16 characters is needed — this endpoint answers without a login, " +
                "so the token is the whole of the door."));
}
