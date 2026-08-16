namespace LabbyTwo.Core;

/// <summary>
/// Converts a stored reading into whichever units the user reads in.
///
/// Values are always *stored* in one canonical unit per quantity, whatever the provider's
/// own API used — temperatures in Celsius, wind in mph, pressure in inHg, rain in inches.
/// That is what makes history comparable and lets an alert rule written today still mean the
/// same thing after somebody changes this. Only display and input convert.
///
/// The choice used to be one switch with two positions. That was wrong for the people this
/// is for: a pilot wants knots and inHg, a sailor wants knots and hPa, a scientist wants
/// Kelvin, and none of them wants the other three quantities decided for them as a
/// consequence. So each quantity is chosen separately, and Metric and Imperial survive as
/// presets that set all four at once.
/// </summary>
public static class Units
{
    public const string Metric = "metric";
    public const string Imperial = "imperial";

    /// <summary>The preset dropdown: a starting point, not the whole setting.</summary>
    public static IReadOnlyList<SelectOption> Options =>
    [
        new(Imperial, "Imperial — °F, mph, inHg, in"),
        new(Metric, "Metric — °C, km/h, hPa, mm"),
    ];

    // ---- what each quantity can be shown in ------------------------------------------

    public const string Celsius = "°C";
    public const string Fahrenheit = "°F";
    public const string Kelvin = "K";

    public const string Mph = "mph";
    public const string Kmh = "km/h";
    public const string Ms = "m/s";
    public const string Knots = "kn";

    public const string InHg = "inHg";
    public const string HPa = "hPa";
    public const string Mbar = "mbar";
    public const string MmHg = "mmHg";
    public const string KPa = "kPa";

    public const string Inches = "in";
    public const string Mm = "mm";

    public static readonly (string Value, string Label)[] TemperatureUnits =
    [
        (Fahrenheit, "Fahrenheit — °F"),
        (Celsius, "Celsius — °C"),
        (Kelvin, "Kelvin — K"),
    ];

    public static readonly (string Value, string Label)[] WindUnits =
    [
        (Mph, "Miles per hour — mph"),
        (Kmh, "Kilometres per hour — km/h"),
        (Ms, "Metres per second — m/s"),
        (Knots, "Knots — kn"),
    ];

    public static readonly (string Value, string Label)[] PressureUnits =
    [
        (InHg, "Inches of mercury — inHg"),
        (HPa, "Hectopascals — hPa"),
        (Mbar, "Millibars — mbar"),
        (MmHg, "Millimetres of mercury — mmHg"),
        (KPa, "Kilopascals — kPa"),
    ];

    public static readonly (string Value, string Label)[] RainUnits =
    [
        (Inches, "Inches — in"),
        (Mm, "Millimetres — mm"),
    ];

    /// <summary>
    /// One choice per quantity. Built from the stored settings, falling back to whichever
    /// preset is set — so an install that only ever chose "metric" keeps reading in metric
    /// without anything to migrate, and starts honouring a finer choice the moment one is
    /// made.
    /// </summary>
    public sealed record Preferences(string Temperature, string Wind, string Pressure, string Rain)
    {
        public const string TemperatureKey = "unit_temperature";
        public const string WindKey = "unit_wind";
        public const string PressureKey = "unit_pressure";
        public const string RainKey = "unit_rain";

        /// <summary>The preset key, kept for the two-position control and as the fallback.</summary>
        public const string SystemKey = "units";

        public static Preferences Of(string? system) =>
            string.Equals(system, Metric, StringComparison.OrdinalIgnoreCase)
                ? new Preferences(Celsius, Kmh, HPa, Mm)
                : new Preferences(Fahrenheit, Mph, InHg, Inches);

        public static Preferences Default => Of(Imperial);

        public static Preferences From(SettingsBag settings)
        {
            var preset = Of(settings.Get(SystemKey, Imperial));

            return new Preferences(
                Pick(settings.Get(TemperatureKey), TemperatureUnits, preset.Temperature),
                Pick(settings.Get(WindKey), WindUnits, preset.Wind),
                Pick(settings.Get(PressureKey), PressureUnits, preset.Pressure),
                Pick(settings.Get(RainKey), RainUnits, preset.Rain));
        }

        /// <summary>A stored value that is not one of the offered ones is treated as unset.</summary>
        private static string Pick(string stored, (string Value, string Label)[] allowed, string fallback) =>
            allowed.Any(option => option.Value == stored) ? stored : fallback;

        /// <summary>
        /// Which preset this matches, or null when the four have been mixed. Lets the preset
        /// control say "Custom" rather than lying about being one of the two.
        /// </summary>
        public string? MatchingPreset =>
            this == Of(Imperial) ? Imperial : this == Of(Metric) ? Metric : null;
    }

    /// <summary>
    /// The stored value and unit, expressed as the user asked. Units not handled here —
    /// percentages, milliseconds, counts, gigabytes — mean the same everywhere and pass
    /// straight through.
    /// </summary>
    public static (double Value, string Unit) Display(double value, string unit, Preferences prefs)
    {
        // The pass-through branches return the unit they were given rather than a literal, so
        // the spacing a metric declares survives — a hardcoded "mph" here silently turned
        // every " mph" into "0mph" no matter what the provider asked for.
        var spacing = unit.StartsWith(' ') ? " " : "";

        return unit.Trim() switch
        {
            Celsius => prefs.Temperature switch
            {
                Celsius => (value, unit),
                Kelvin => (value + 273.15, spacing + Kelvin),
                _ => (value * 9 / 5 + 32, spacing + Fahrenheit),
            },

            Mph => prefs.Wind switch
            {
                Mph => (value, unit),
                Kmh => (value * 1.609344, " " + Kmh),
                Ms => (value * 0.44704, " " + Ms),
                _ => (value * 0.8689762, " " + Knots),
            },

            InHg => prefs.Pressure switch
            {
                InHg => (value, unit),
                HPa => (value * 33.863886, " " + HPa),
                Mbar => (value * 33.863886, " " + Mbar),
                MmHg => (value * 25.4, " " + MmHg),
                _ => (value * 3.3863886, " " + KPa),
            },

            Inches => prefs.Rain switch
            {
                Inches => (value, unit),
                _ => (value * 25.4, " " + Mm),
            },

            _ => (value, unit),
        };
    }

    /// <summary>
    /// The inverse: what a number typed in the chosen units should be stored as. Without
    /// this, somebody shown °F would type 90 and quietly save a 90°C threshold.
    /// </summary>
    public static double Store(double displayed, string unit, Preferences prefs) =>
        unit.Trim() switch
        {
            Celsius => prefs.Temperature switch
            {
                Celsius => displayed,
                Kelvin => displayed - 273.15,
                _ => (displayed - 32) * 5 / 9,
            },

            Mph => prefs.Wind switch
            {
                Mph => displayed,
                Kmh => displayed / 1.609344,
                Ms => displayed / 0.44704,
                _ => displayed / 0.8689762,
            },

            InHg => prefs.Pressure switch
            {
                InHg => displayed,
                HPa or Mbar => displayed / 33.863886,
                MmHg => displayed / 25.4,
                _ => displayed / 3.3863886,
            },

            Inches => prefs.Rain switch
            {
                Inches => displayed,
                _ => displayed / 25.4,
            },

            _ => displayed,
        };

    /// <summary>Whether this metric reads differently depending on the choice, so the UI can label it.</summary>
    public static bool IsConvertible(string unit) =>
        unit.Trim() is Celsius or Mph or InHg or Inches;

    /// <summary>A reading formatted for display, converted and with the right unit attached.</summary>
    public static string Format(MetricSpec spec, double value, Preferences prefs, int? decimals = null)
    {
        var (converted, unit) = Display(value, spec.Unit, prefs);

        // A Fahrenheit reading wants no more precision than the Celsius one it came from, and
        // a converted km/h value wants no less.
        return converted.ToString($"F{decimals ?? spec.Decimals}") + unit;
    }

    /// <summary>The unit label alone, for a form field sitting next to a number box.</summary>
    public static string LabelFor(MetricSpec spec, Preferences prefs) =>
        Display(0, spec.Unit, prefs).Unit.Trim();
}
