namespace LabbyTwo.Core;

/// <summary>
/// Converts a stored reading into whichever units the user reads in.
///
/// Values are always *stored* in one canonical unit per metric, whatever the provider's
/// own API used — temperatures in Celsius, wind in mph, pressure in inHg. That is what
/// makes history comparable and lets an alert rule written today still mean the same
/// thing after someone flips this setting. Only display and input convert.
/// </summary>
public static class Units
{
    public const string Metric = "metric";
    public const string Imperial = "imperial";

    public static IReadOnlyList<SelectOption> Options =>
    [
        new(Imperial, "Imperial — °F, mph, inHg"),
        new(Metric, "Metric — °C, km/h, hPa"),
    ];

    private static bool IsMetric(string? system) =>
        string.Equals(system, Metric, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The stored value and unit, expressed in the chosen system. Units not on this list
    /// — percentages, milliseconds, counts, gigabytes — mean the same everywhere and pass
    /// straight through.
    /// </summary>
    public static (double Value, string Unit) Display(double value, string unit, string? system)
    {
        var metric = IsMetric(system);

        // The pass-through branches return the unit they were given rather than a literal,
        // so the spacing a metric declares survives — a hardcoded "mph" here silently
        // turned every " mph" into "0mph" no matter what the provider asked for.
        return unit.Trim() switch
        {
            "°C" => metric ? (value, unit) : (value * 9 / 5 + 32, "°F"),
            "mph" => metric ? (value * 1.60934, " km/h") : (value, unit),
            "inHg" => metric ? (value * 33.8639, " hPa") : (value, unit),
            "in" => metric ? (value * 25.4, " mm") : (value, unit),
            _ => (value, unit),
        };
    }

    /// <summary>
    /// The inverse: what a number typed in the chosen system should be stored as. Without
    /// this, someone shown °F would type 90 and quietly save a 90°C threshold.
    /// </summary>
    public static double Store(double displayed, string unit, string? system)
    {
        var metric = IsMetric(system);
        return unit.Trim() switch
        {
            "°C" => metric ? displayed : (displayed - 32) * 5 / 9,
            "mph" => metric ? displayed / 1.60934 : displayed,
            "inHg" => metric ? displayed / 33.8639 : displayed,
            "in" => metric ? displayed / 25.4 : displayed,
            _ => displayed,
        };
    }

    /// <summary>Whether this metric reads differently in the two systems, so the UI can label it.</summary>
    public static bool IsConvertible(string unit) =>
        unit.Trim() is "°C" or "mph" or "inHg" or "in";

    /// <summary>A reading formatted for display, converted and with the right unit attached.</summary>
    public static string Format(MetricSpec spec, double value, string? system, int? decimals = null)
    {
        var (converted, unit) = Display(value, spec.Unit, system);

        // A Fahrenheit reading wants no more precision than the Celsius one it came from,
        // and a converted km/h value wants no less.
        return converted.ToString($"F{decimals ?? spec.Decimals}") + unit;
    }

    /// <summary>The unit label alone, for a form field sitting next to a number box.</summary>
    public static string LabelFor(MetricSpec spec, string? system) =>
        Display(0, spec.Unit, system).Unit.Trim();
}
