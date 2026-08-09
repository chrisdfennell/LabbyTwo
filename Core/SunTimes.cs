namespace LabbyTwo.Core;

/// <summary>
/// Sunrise and sunset from a date and a position, using NOAA's solar-position algorithm.
///
/// Computed rather than fetched: it needs no API key, no network and no third party
/// learning where you live, and it is accurate to about a minute — far better than a
/// dashboard needs. Positions inside the polar circles genuinely have days with neither
/// a sunrise nor a sunset, so both are nullable and that is a real answer, not a failure.
/// </summary>
public static class SunTimes
{
    /// <param name="latitude">Degrees north, negative for south.</param>
    /// <param name="longitude">Degrees east, negative for west.</param>
    public sealed record Day(DateTimeOffset? Sunrise, DateTimeOffset? Sunset)
    {
        /// <summary>Null when the sun does not rise or set that day.</summary>
        public TimeSpan? Daylight => Sunrise is { } up && Sunset is { } down && down > up ? down - up : null;

        public bool PolarDay { get; init; }
        public bool PolarNight { get; init; }
    }

    // The centre of the sun is 90.833° from vertical at sunrise: 90° plus refraction in the
    // atmosphere and half the sun's own width. This is the standard value NOAA publishes.
    private const double ZenithDegrees = 90.833;

    public static Day For(DateOnly date, double latitude, double longitude, TimeSpan utcOffset)
    {
        var dayOfYear = date.DayOfYear;

        // Fractional year, in radians, taken at midday — the equation of time barely moves
        // across a single day, so one evaluation is enough for both events.
        var gamma = 2 * Math.PI / DaysIn(date.Year) * (dayOfYear - 1 + 0.5);

        var equationOfTime = 229.18 * (0.000075
            + 0.001868 * Math.Cos(gamma)
            - 0.032077 * Math.Sin(gamma)
            - 0.014615 * Math.Cos(2 * gamma)
            - 0.040849 * Math.Sin(2 * gamma));

        var declination = 0.006918
            - 0.399912 * Math.Cos(gamma)
            + 0.070257 * Math.Sin(gamma)
            - 0.006758 * Math.Cos(2 * gamma)
            + 0.000907 * Math.Sin(2 * gamma)
            - 0.002697 * Math.Cos(3 * gamma)
            + 0.001480 * Math.Sin(3 * gamma);

        var latitudeRadians = Radians(latitude);

        var cosHourAngle =
            Math.Cos(Radians(ZenithDegrees)) / (Math.Cos(latitudeRadians) * Math.Cos(declination))
            - Math.Tan(latitudeRadians) * Math.Tan(declination);

        // Out of range means the sun never reaches that angle: it is up all day, or never
        // comes up at all. Which one depends on the sign.
        if (cosHourAngle > 1)
            return new Day(null, null) { PolarNight = true };
        if (cosHourAngle < -1)
            return new Day(null, null) { PolarDay = true };

        var hourAngle = Degrees(Math.Acos(cosHourAngle));

        var sunriseMinutesUtc = 720 - 4 * (longitude + hourAngle) - equationOfTime;
        var sunsetMinutesUtc = 720 - 4 * (longitude - hourAngle) - equationOfTime;

        return new Day(
            AtMinutes(date, sunriseMinutesUtc, utcOffset),
            AtMinutes(date, sunsetMinutesUtc, utcOffset));
    }

    /// <summary>
    /// Minutes past midnight UTC on that date, expressed in the requested offset. The
    /// value can fall outside 0–1440 at extreme longitudes, which is why it is added as a
    /// duration rather than clamped into a time of day.
    /// </summary>
    private static DateTimeOffset AtMinutes(DateOnly date, double minutesUtc, TimeSpan utcOffset)
    {
        var midnightUtc = new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.Zero);
        return midnightUtc.AddMinutes(minutesUtc).ToOffset(utcOffset);
    }

    private static int DaysIn(int year) => DateTime.IsLeapYear(year) ? 366 : 365;

    private static double Radians(double degrees) => degrees * Math.PI / 180;

    private static double Degrees(double radians) => radians * 180 / Math.PI;
}
