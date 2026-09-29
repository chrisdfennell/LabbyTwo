using System.Globalization;

namespace LabbyTwo.Core;

/// <summary>
/// What <c>{{power}}</c> says. Pure, so the words for every period and every kind of
/// number are pinned by tests without a database.
/// </summary>
public static class PowerShortcode
{
    public enum Period { Today, Week, Month, Last30, Projected }

    public enum Show { Cost, Kwh, Watts }

    /// <summary>The period an option names, or null when it names none. The month so far when left out.</summary>
    public static Period? ParsePeriod(string? written) => (written ?? "").Trim().ToLowerInvariant() switch
    {
        "" or "month" or "this-month" or "mtd" => Period.Month,
        "today" or "day" => Period.Today,
        "week" or "7d" or "last-week" => Period.Week,
        "30d" or "last30" or "30days" or "last-30-days" => Period.Last30,
        "projected" or "projection" or "forecast" or "bill" => Period.Projected,
        _ => null,
    };

    /// <summary>What to show, or null when the option names nothing. The cost when left out.</summary>
    public static Show? ParseShow(string? written) => (written ?? "").Trim().ToLowerInvariant() switch
    {
        "" or "cost" or "money" => Show.Cost,
        "kwh" or "energy" => Show.Kwh,
        "watts" or "w" or "power" or "average" => Show.Watts,
        _ => null,
    };

    /// <summary>How the period reads in a tooltip.</summary>
    public static string Describe(Period period) => period switch
    {
        Period.Today => "today so far",
        Period.Week => "the last 7 days",
        Period.Last30 => "the last 30 days",
        Period.Projected => "this month, projected",
        _ => "this month so far",
    };

    /// <summary>A kWh amount the way the page writes it: more decimals for small ones.</summary>
    public static string Kwh(double kwh) =>
        kwh.ToString(Math.Abs(kwh) < 10 ? "0.00" : Math.Abs(kwh) < 100 ? "0.0" : "0", CultureInfo.InvariantCulture) + " kWh";

    /// <summary>Average watts, or a dash when nothing was measured to average.</summary>
    public static string Watts(double? watts) =>
        watts is { } w ? w.ToString(w < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " W" : "—";

    /// <summary>
    /// The words for one source, or for the whole lab, over a period. A projection has no
    /// average of its own, so "watts" of a projection is the last week's, which is what it
    /// was projected from.
    /// </summary>
    public static string Text(PowerTotals totals, Show show, Period period, PowerTariff tariff)
    {
        if (period == Period.Projected)
        {
            return show switch
            {
                Show.Kwh => Kwh(totals.ProjectedMonthKwh),
                Show.Watts => Watts(totals.LastWeek.AverageWatts),
                _ => tariff.Money(totals.ProjectedMonthCost),
            };
        }

        var energy = period switch
        {
            Period.Today => totals.Today,
            Period.Week => totals.LastWeek,
            Period.Last30 => totals.Last30,
            _ => totals.Month,
        };
        return show switch
        {
            Show.Kwh => Kwh(energy.Kwh),
            Show.Watts => Watts(energy.AverageWatts),
            _ => tariff.Money(energy.Cost),
        };
    }
}

/// <summary>
/// One source's figures, or the whole lab's, in the shape <see cref="PowerShortcode.Text"/>
/// reads — so the total and a single plug are phrased by the same code.
/// </summary>
public sealed record PowerTotals(
    PeriodEnergy Today, PeriodEnergy LastWeek, PeriodEnergy Month, PeriodEnergy Last30,
    double ProjectedMonthCost, double ProjectedMonthKwh)
{
    public static PowerTotals Of(PowerReport report) => new(
        report.Today, report.LastWeek, report.Month, report.Last30, report.ProjectedMonthCost, report.ProjectedMonthKwh);
}
