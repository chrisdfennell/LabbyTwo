namespace LabbyTwo.Core;

/// <summary>
/// How one metric should be presented. A provider declares these alongside its fields,
/// which is what keeps display knowledge out of the core: adding an integration that
/// reports <c>pool_scrub_hours</c> should not require editing a switch statement
/// somewhere else in the app.
/// </summary>
/// <param name="Key">The dictionary key the provider uses in <see cref="ProbeResult.Metrics"/>.</param>
/// <param name="Label">Human-readable name shown on tiles, charts and pickers.</param>
/// <param name="Unit">Suffix appended to the value — "%", "ms", "°C". Empty for a bare count.</param>
/// <param name="Decimals">Default decimal places. A widget's own setting still wins.</param>
public sealed record MetricSpec(string Key, string Label, string Unit = "", int Decimals = 0)
{
    /// <summary>Formats a value the way this metric wants to be read.</summary>
    public string Format(double value, int? decimals = null) =>
        value.ToString($"F{decimals ?? Decimals}") + Unit;

    /// <summary>
    /// Set on a metric that fills towards a limit — a volume's percent used, the free space
    /// left on a download disk — which is what makes it forecastable: LabbyTwo fits a trend
    /// to its recent history and says when it runs out. Null for everything else, because a
    /// CPU reading or a temperature has a trend but no "full", and a forecast of when the
    /// CPU reaches 100% would be nonsense said confidently.
    ///
    /// A property rather than a constructor parameter for the reason FieldSpec's newer
    /// settings are: plugins are compiled against the constructor, and changing it would
    /// break every one of them the moment its metrics were read.
    /// </summary>
    public CapacityLimit? Capacity { get; init; }

    /// <summary>
    /// Metric names that mean the same thing everywhere — latency, CPU, temperature.
    /// A provider gets these for free and only declares what is specific to it, so
    /// twelve providers don't each repeat the definition of <c>latency_ms</c>.
    /// </summary>
    public static readonly IReadOnlyList<MetricSpec> WellKnown =
    [
        new("latency_ms", "Response time", " ms"),
        new("rtt_ms", "Round-trip time", " ms", 1),
        new("cpu_percent", "CPU", "%"),
        new("ram_percent", "Memory", "%"),
        new("disk_percent", "Disk used", "%") { Capacity = CapacityLimit.Percent },
        new("temp_c", "Temperature", "°C", 1),
        new("humidity", "Humidity", "%"),
        new("uptime_days", "Uptime", " days", 1),
        new("battery_percent", "Battery", "%"),
        new("status_code", "HTTP status"),
    ];

    private static readonly Dictionary<string, MetricSpec> WellKnownByKey =
        WellKnown.ToDictionary(m => m.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Best effort for a metric nobody declared — a JSON API field the user invented, or
    /// a provider that reports more than it documents. Underscores become spaces and the
    /// first letter is capitalised, which reads better than the raw key and never lies
    /// about a unit.
    /// </summary>
    public static MetricSpec Fallback(string key)
    {
        if (WellKnownByKey.TryGetValue(key, out var known))
            return known;

        var words = key.Replace('_', ' ').Trim();
        var label = words.Length == 0 ? key : char.ToUpperInvariant(words[0]) + words[1..];

        // A trailing unit in the name is a strong convention among home lab APIs, and
        // honouring it means "fan_rpm" reads as "Fan 1200rpm" with nothing declared.
        foreach (var (suffix, unit, decimals) in TrailingUnits)
        {
            if (!key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;
            var trimmed = label[..^suffix.Length].TrimEnd();
            return new MetricSpec(key, trimmed.Length == 0 ? label : trimmed, unit, decimals);
        }

        return new MetricSpec(key, label);
    }

    private static readonly (string Suffix, string Unit, int Decimals)[] TrailingUnits =
    [
        ("_percent", "%", 0),
        ("_pct", "%", 0),
        ("_ms", " ms", 0),
        ("_celsius", "°C", 1),
        ("_c", "°C", 1),
        ("_f", "°F", 1),
        ("_mbps", " Mbps", 1),
        ("_kbps", " kbps", 0),
        ("_rpm", " rpm", 0),
        ("_watts", " W", 0),
        ("_volts", " V", 1),
        ("_gb", " GB", 1),
        ("_mb", " MB", 0),
        ("_bytes", " B", 0),
        ("_seconds", " s", 0),
        ("_days", " days", 1),
        ("_hours", " h", 1),
        ("_count", "", 0),
    ];
}

/// <summary>
/// Where a capacity metric runs out, and from which side it approaches.
/// </summary>
/// <param name="Full">The value that means "no room left" — 100 for a percentage used, 0 for free space.</param>
/// <param name="Rising">True when the metric climbs towards <see cref="Full"/>, false when it falls to it.</param>
public sealed record CapacityLimit(double Full, bool Rising = true)
{
    /// <summary>A percentage used, full at 100.</summary>
    public static CapacityLimit Percent { get; } = new(100);

    /// <summary>Something that is used up — free gigabytes — and runs out at zero.</summary>
    public static CapacityLimit RunsOutAtZero { get; } = new(0, Rising: false);

    /// <summary>Whether a reading is at or past the limit.</summary>
    public bool IsFull(double value) => Rising ? value >= Full : value <= Full;
}
