using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LabbyTwo.Core;

/// <summary>
/// A price that applies at certain times of the week instead of the ordinary one — "peak,
/// weekdays 16:00 to 21:00", "off-peak, every night 23:00 to 07:00".
///
/// <see cref="Days"/> are the days the window <em>starts</em> on. A window that ends earlier
/// in the day than it starts runs past midnight into the next day, so "Friday 23:00 to
/// 07:00" covers Saturday's small hours too — which is how a tariff sheet means it, and why
/// a night rate needs no second window for the morning half. A window whose start and end
/// are the same covers the whole of each of its days (a weekend rate).
///
/// Times are wall-clock times in the lab's own zone, so the window moves with the clocks:
/// 16:00 is 16:00 in summer and in winter, the way the electricity company counts it.
/// </summary>
public sealed record TimeOfUseWindow(string Name, double Price, IReadOnlyList<DayOfWeek> Days, TimeOnly Start, TimeOnly End)
{
    /// <summary>Every day of the week, for a window that does not care which day it is.</summary>
    public static readonly IReadOnlyList<DayOfWeek> EveryDay =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    ];

    /// <summary>Whether this window's price applies at a local wall-clock time.</summary>
    public bool Covers(DateTime local)
    {
        var time = TimeOnly.FromDateTime(local);
        var day = local.DayOfWeek;

        if (Start == End)
            return Days.Contains(day);

        if (Start < End)
            return Days.Contains(day) && time >= Start && time < End;

        // Past midnight: the evening part belongs to today, the morning part to the day
        // the window started — yesterday.
        var yesterday = (DayOfWeek)(((int)day + 6) % 7);
        return (Days.Contains(day) && time >= Start) || (Days.Contains(yesterday) && time < End);
    }
}

/// <summary>
/// What electricity costs: a price per kWh, optionally different at certain times of the
/// week, and a fixed monthly charge.
///
/// The fixed charge is carried so the projected bill can include it, and deliberately never
/// divided between devices: it is paid whether the NAS is plugged in or not, so adding a
/// share of it to the NAS's cost would make unplugging the NAS look like it saves money it
/// does not.
/// </summary>
public sealed record PowerTariff(double PricePerKwh, string Currency, double MonthlyFee, IReadOnlyList<TimeOfUseWindow> Windows)
{
    public const string PriceKey = "power_price";
    public const string CurrencyKey = "power_currency";
    public const string MonthlyFeeKey = "power_monthly_fee";
    public const string WindowsKey = "power_tou";

    /// <summary>Fifteen cents a kWh, in dollars — near enough the US average to be a sensible start.</summary>
    public static PowerTariff Default { get; } = new(0.15, "$", 0, []);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed record WindowDto(string? Name, double Price, List<DayOfWeek>? Days, string? Start, string? End);

    public static PowerTariff From(SettingsBag settings) => new(
        Number(settings.Get(PriceKey), Default.PricePerKwh),
        settings.Get(CurrencyKey, Default.Currency).Trim() is { Length: > 0 } currency ? currency : Default.Currency,
        Number(settings.Get(MonthlyFeeKey), 0),
        ParseWindows(settings.Get(WindowsKey)));

    public Dictionary<string, string> ToSettings() => new()
    {
        [PriceKey] = PricePerKwh.ToString(CultureInfo.InvariantCulture),
        [CurrencyKey] = Currency,
        [MonthlyFeeKey] = MonthlyFee.ToString(CultureInfo.InvariantCulture),
        [WindowsKey] = JsonSerializer.Serialize(Windows.Select(w => new WindowDto(
            w.Name, w.Price, [.. w.Days],
            w.Start.ToString("HH:mm", CultureInfo.InvariantCulture),
            w.End.ToString("HH:mm", CultureInfo.InvariantCulture))), Json),
    };

    /// <summary>
    /// The saved windows, skipping any that do not read back — a hand-edited setting should
    /// cost that one window, not the whole tariff.
    /// </summary>
    public static IReadOnlyList<TimeOfUseWindow> ParseWindows(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            var windows = new List<TimeOfUseWindow>();
            foreach (var dto in JsonSerializer.Deserialize<List<WindowDto>>(json, Json) ?? [])
            {
                if (!TimeOnly.TryParse(dto.Start, CultureInfo.InvariantCulture, out var start)
                    || !TimeOnly.TryParse(dto.End, CultureInfo.InvariantCulture, out var end)
                    || dto.Price < 0 || double.IsNaN(dto.Price))
                    continue;
                windows.Add(new TimeOfUseWindow(dto.Name?.Trim() ?? "", dto.Price,
                    dto.Days is { Count: > 0 } days ? [.. days.Distinct()] : TimeOfUseWindow.EveryDay, start, end));
            }
            return windows;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The price per kWh at one instant: the first window that covers it, else the ordinary
    /// price. First rather than cheapest or dearest, so overlapping windows behave the way
    /// the list reads — put the exception above the rule.
    /// </summary>
    public double PriceAt(DateTimeOffset instant, TimeZoneInfo zone)
    {
        if (Windows.Count == 0)
            return PricePerKwh;
        var local = TimeZoneInfo.ConvertTime(instant, zone).DateTime;
        foreach (var window in Windows)
        {
            if (window.Covers(local))
                return window.Price;
        }
        return PricePerKwh;
    }

    /// <summary>
    /// The price for one UTC hour of energy: the average of the price at each quarter of it.
    /// Energy is kept per hour, so this is as fine as pricing gets — a window starting at
    /// 16:30 charges half the 16:00 hour at each rate, which is right when the hour's use
    /// was even and close when it was not. Quarters rather than the hour's midpoint so a zone
    /// half an hour off UTC (India, Newfoundland) still lands its windows correctly.
    /// </summary>
    public double PriceForHour(long hourStartUnix, TimeZoneInfo zone)
    {
        if (Windows.Count == 0)
            return PricePerKwh;
        var start = DateTimeOffset.FromUnixTimeSeconds(hourStartUnix);
        double sum = 0;
        for (var quarter = 0; quarter < 4; quarter++)
            sum += PriceAt(start.AddMinutes(7.5 + quarter * 15), zone);
        return sum / 4;
    }

    /// <summary>
    /// An amount of money as the reader writes it: a one-character symbol in front ("$1.24",
    /// "€1.24"), anything longer after, with a space ("1.24 kr", "1.24 CHF"). Two decimals,
    /// and a sliver that would round to nothing reads as "under" rather than as free.
    /// </summary>
    public string Money(double amount) => FormatMoney(amount, Currency);

    public static string FormatMoney(double amount, string currency)
    {
        string Write(double value) => currency.Length <= 1
            ? currency + value.ToString("0.00", CultureInfo.InvariantCulture)
            : value.ToString("0.00", CultureInfo.InvariantCulture) + " " + currency;

        if (amount > 0 && amount < 0.005)
            return "under " + Write(0.01);
        return Write(amount);
    }

    /// <summary>A number the settings form wrote, read the same whatever the server's culture.</summary>
    public static double Number(string? text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= 0 && double.IsFinite(value)
            ? value
            : fallback;
}
