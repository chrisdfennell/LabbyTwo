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

    // ---- converting -------------------------------------------------------------------

    private enum Quantity { None, Temperature, Wind, Pressure, Rain }

    /// <summary>
    /// How many of each unit make one of its quantity's stored unit — one mph, one inHg, one
    /// inch. Everything but temperature is a plain scale, so converting between any two
    /// units of the same quantity is one division and one multiplication, and a pair nobody
    /// wrote a line for (knots to km/h) cannot be missing. Temperature's entry is only the
    /// size of a degree, for converting a change; readings go through Celsius because the
    /// scales also have different zeros.
    /// </summary>
    private static readonly Dictionary<string, (Quantity Quantity, double PerStored)> Scales = new(StringComparer.Ordinal)
    {
        [Celsius] = (Quantity.Temperature, 1),
        [Fahrenheit] = (Quantity.Temperature, 1.8),
        [Kelvin] = (Quantity.Temperature, 1),

        [Mph] = (Quantity.Wind, 1),
        [Kmh] = (Quantity.Wind, 1.609344),
        [Ms] = (Quantity.Wind, 0.44704),
        [Knots] = (Quantity.Wind, 0.8689762),

        [InHg] = (Quantity.Pressure, 1),
        [HPa] = (Quantity.Pressure, 33.863886),
        [Mbar] = (Quantity.Pressure, 33.863886),
        [MmHg] = (Quantity.Pressure, 25.4),
        [KPa] = (Quantity.Pressure, 3.3863886),

        [Inches] = (Quantity.Rain, 1),
        [Mm] = (Quantity.Rain, 25.4),
    };

    /// <summary>
    /// What people actually type after a number or in <c>unit=</c>, mapped to the symbol
    /// used everywhere else. Case is forgiven because nobody means "MPH" as anything else;
    /// "ms" is deliberately absent, because on this dashboard it is milliseconds.
    /// </summary>
    private static readonly Dictionary<string, string> Written = new(StringComparer.OrdinalIgnoreCase)
    {
        ["°C"] = Celsius, ["ºC"] = Celsius, ["C"] = Celsius, ["degC"] = Celsius, ["celsius"] = Celsius,
        ["°F"] = Fahrenheit, ["ºF"] = Fahrenheit, ["F"] = Fahrenheit, ["degF"] = Fahrenheit, ["fahrenheit"] = Fahrenheit,
        ["K"] = Kelvin, ["kelvin"] = Kelvin,
        ["mph"] = Mph,
        ["km/h"] = Kmh, ["kmh"] = Kmh, ["kph"] = Kmh,
        ["m/s"] = Ms,
        ["kn"] = Knots, ["kt"] = Knots, ["kts"] = Knots, ["knots"] = Knots,
        ["inHg"] = InHg,
        ["hPa"] = HPa,
        ["mbar"] = Mbar, ["mb"] = Mbar,
        ["mmHg"] = MmHg,
        ["kPa"] = KPa,
        ["in"] = Inches, ["inch"] = Inches, ["inches"] = Inches,
        ["mm"] = Mm,
    };

    /// <summary>
    /// The unit a metric's own unit is, when it is one this class converts from. Kelvin is
    /// left out on purpose: it is offered as something to read temperatures in, but a
    /// metric whose unit happens to be "K" is far more likely to be counting thousands of
    /// something than measuring heat, and turning it into Fahrenheit would be nonsense.
    /// </summary>
    private static string? Source(string unit)
    {
        var trimmed = unit.Trim();
        return trimmed != Kelvin && Scales.ContainsKey(trimmed) ? trimmed : null;
    }

    private static Quantity QuantityOf(string unit) =>
        Scales.TryGetValue(unit.Trim(), out var scale) ? scale.Quantity : Quantity.None;

    /// <summary>The unit somebody asked for, as its symbol, or null when it is not one this knows.</summary>
    public static string? Parse(string? written) =>
        written is null ? null : Written.GetValueOrDefault(written.Trim());

    /// <summary>What the reader chose for this unit's quantity, or null when there is no choice to make.</summary>
    private static string? Chosen(string unit, Preferences prefs) => QuantityOf(unit) switch
    {
        Quantity.Temperature => prefs.Temperature,
        Quantity.Wind => prefs.Wind,
        Quantity.Pressure => prefs.Pressure,
        Quantity.Rain => prefs.Rain,
        _ => null,
    };

    /// <summary>
    /// A reading in one unit expressed in another of the same quantity — 50 °C in °F, 3 in
    /// in mm. Null when the two do not measure the same thing (°C to mph), which is how a
    /// caller tells a unit it should convert to from a label somebody wanted to see.
    /// </summary>
    public static double? Convert(double value, string from, string to)
    {
        from = from.Trim();
        to = to.Trim();
        if (!Scales.TryGetValue(from, out var source) || !Scales.TryGetValue(to, out var target)
            || source.Quantity != target.Quantity)
            return null;
        if (from == to)
            return value;
        if (source.Quantity == Quantity.Temperature)
            return FromCelsius(ToCelsius(value, from), to);
        return value / source.PerStored * target.PerStored;
    }

    /// <summary>
    /// A difference rather than a reading — "3 °C warmer", "+2 in a week". Only the size of
    /// the unit matters for a change: a 5 °C rise is a 9 °F rise, not a 41 °F one.
    /// </summary>
    public static double? ConvertChange(double change, string from, string to) =>
        Scales.TryGetValue(from.Trim(), out var source) && Scales.TryGetValue(to.Trim(), out var target)
        && source.Quantity == target.Quantity
            ? change / source.PerStored * target.PerStored
            : null;

    private static double ToCelsius(double value, string unit) => unit switch
    {
        Fahrenheit => (value - 32) * 5 / 9,
        Kelvin => value - 273.15,
        _ => value,
    };

    private static double FromCelsius(double celsius, string unit) => unit switch
    {
        Fahrenheit => celsius * 9 / 5 + 32,
        Kelvin => celsius + 273.15,
        _ => celsius,
    };

    /// <summary>
    /// The label a converted value carries. A temperature keeps whatever spacing the metric
    /// declared ("21°C" becomes "70°F", never "70 °F"); every other unit is a word and gets a
    /// space, since "40km/h" reads as a typo.
    /// </summary>
    private static string Label(string declared, string target) =>
        QuantityOf(target) == Quantity.Temperature
            ? (declared.StartsWith(' ') ? " " : "") + target
            : " " + target;

    /// <summary>
    /// The stored value and unit, expressed as the user asked. Units not handled here —
    /// percentages, milliseconds, counts, gigabytes — mean the same everywhere and pass
    /// straight through.
    ///
    /// Every place a reading is drawn comes through here (usually by way of
    /// <see cref="Format(MetricSpec, double, Preferences, int?)"/>), so the tile, the chart,
    /// the sentence in a runbook and the alert all agree about what 60 °C is called.
    /// </summary>
    public static (double Value, string Unit) Display(double value, string unit, Preferences prefs)
    {
        // A value already in the chosen unit returns the unit it was given rather than a
        // literal, so the spacing a metric declares survives — a hardcoded "mph" here once
        // silently turned every " mph" into "0mph" no matter what the provider asked for.
        if (Source(unit) is not { } from || Chosen(unit, prefs) is not { } to || from == to)
            return (value, unit);

        return (Convert(value, from, to)!.Value, Label(unit, to));
    }

    /// <summary>
    /// The same, with whatever the person wrote in place of the unit — a card's "suffix", a
    /// shortcode's <c>unit=</c>. A unit of the same quantity (°C on a temperature, km/h on
    /// a wind speed) is a request to show it in that unit, whatever the setting says. Anything
    /// else is a label: the number still follows the setting, with their words after it.
    /// </summary>
    public static (double Value, string Unit) Display(double value, string unit, Preferences prefs, string? written)
    {
        if (written is null)
            return Display(value, unit, prefs);

        if (Source(unit) is { } from && Parse(written) is { } asked && Convert(value, from, asked) is { } converted)
            return (converted, Label(unit, asked));

        return (Display(value, unit, prefs).Value, written);
    }

    /// <summary>
    /// Whether <paramref name="written"/> names a unit a reading in <paramref name="unit"/>
    /// can be shown in — "°C" for a temperature — rather than a label to put after it.
    /// </summary>
    public static bool ConvertsTo(string unit, string? written) =>
        Source(unit) is { } from && Parse(written) is { } asked && Convert(0, from, asked) is not null;

    /// <summary>
    /// A change in a stored reading — a week's growth, the gap between inside and outside —
    /// in the reader's units. See <see cref="ConvertChange"/> for why this is not
    /// <see cref="Display(double, string, Preferences)"/>.
    /// </summary>
    public static (double Value, string Unit) DisplayChange(double change, string unit, Preferences prefs)
    {
        if (Source(unit) is not { } from || Chosen(unit, prefs) is not { } to || from == to)
            return (change, unit);

        return (ConvertChange(change, from, to)!.Value, Label(unit, to));
    }

    /// <summary>
    /// The inverse: what a number typed in the chosen units should be stored as. Without
    /// this, somebody shown °F would type 90 and quietly save a 90°C threshold.
    /// </summary>
    public static double Store(double displayed, string unit, Preferences prefs) =>
        Source(unit) is { } stored && Chosen(unit, prefs) is { } chosen
            ? Convert(displayed, chosen, stored) ?? displayed
            : displayed;

    /// <summary>Whether this metric reads differently depending on the choice, so the UI can label it.</summary>
    public static bool IsConvertible(string unit) => Source(unit) is not null;

    /// <summary>A reading formatted for display, converted and with the right unit attached.</summary>
    public static string Format(MetricSpec spec, double value, Preferences prefs, int? decimals = null) =>
        Format(value, spec.Unit, decimals ?? spec.Decimals, prefs);

    /// <summary>
    /// A reading formatted for display. The decimals are the metric's own whatever it is
    /// shown in: a Fahrenheit reading wants no more precision than the Celsius one it came
    /// from, and a converted km/h value wants no less.
    /// </summary>
    public static string Format(double value, string unit, int decimals, Preferences prefs)
    {
        var (converted, shown) = Display(value, unit, prefs);
        return converted.ToString($"F{decimals}") + shown;
    }

    /// <summary>The unit label alone, for a form field sitting next to a number box.</summary>
    public static string LabelFor(MetricSpec spec, Preferences prefs) =>
        Display(0, spec.Unit, prefs).Unit.Trim();
}
