using LabbyTwo.Core;
using LabbyTwo.Providers;
using Microsoft.Data.Sqlite;

namespace LabbyTwo.Tests;

/// <summary>
/// Energy and what it costs, as pure arithmetic: watts integrated with the gaps left out,
/// counters that reset, prices that change at night and on the day the clocks do, a month
/// projected from a week, and a plug's cost split between what is on it.
/// </summary>
public sealed class PowerCostTests
{
    /// <summary>UTC-5 in winter, UTC-4 from the second Sunday of March to the first of November.</summary>
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.CreateCustomTimeZone(
        "Test/Eastern", TimeSpan.FromHours(-5), "Test Eastern", "Test Eastern", "Test Eastern Daylight",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday)),
        ]);

    private const long Hour = 3600;

    /// <summary>Midnight UTC on 1 September 2026 — on an hour boundary, so hours are easy to name.</summary>
    private static readonly long T0 = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    private static readonly TimeSpan ThirtySeconds = TimeSpan.FromSeconds(30);

    private static List<Energy.Reading> Steady(long from, long to, double value, long every = 30)
    {
        var list = new List<Energy.Reading>();
        for (var t = from; t <= to; t += every)
            list.Add(new Energy.Reading(t, value));
        return list;
    }

    private static double Total(IReadOnlyDictionary<long, Energy.HourEnergy> hours) => hours.Values.Sum(h => h.Kwh);

    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute = 0) =>
        WeeklySchedule.At(new DateOnly(year, month, day), new TimeOnly(hour, minute), Eastern);

    // ---------- integrating watts ----------

    [Fact]
    public void A_steady_hundred_watts_for_an_hour_is_a_tenth_of_a_kilowatt_hour()
    {
        var hours = Energy.FromPower([], Steady(T0, T0 + Hour, 100), ThirtySeconds);

        var only = Assert.Single(hours);
        Assert.Equal(T0, only.Key);
        Assert.Equal(0.1, only.Value.Kwh, 6);
        Assert.Equal(3600, only.Value.CoveredSeconds, 3);
    }

    [Fact]
    public void A_six_hour_outage_adds_no_energy_rather_than_a_line_drawn_across_it()
    {
        var readings = Steady(T0, T0 + Hour, 100);
        readings.AddRange(Steady(T0 + 7 * Hour, T0 + 8 * Hour, 100));

        var hours = Energy.FromPower([], readings, ThirtySeconds);

        Assert.Equal(0.2, Total(hours), 6);
        for (var h = 1; h < 7; h++)
            Assert.False(hours.ContainsKey(T0 + h * Hour), $"hour {h} should have nothing in it");
    }

    [Fact]
    public void A_gap_shorter_than_the_limit_is_bridged_by_the_trapezoid()
    {
        // A ramp from nothing to 200 W over an hour, read at both ends only, is 100 Wh.
        var hours = Energy.FromPower([], [new(T0, 0), new(T0 + Hour, 200)], TimeSpan.FromMinutes(30));

        Assert.Equal(0.1, Total(hours), 6);
    }

    [Fact]
    public void The_gap_limit_is_three_intervals_and_never_under_five_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), Energy.MaxGap(ThirtySeconds));
        Assert.Equal(TimeSpan.FromMinutes(45), Energy.MaxGap(TimeSpan.FromMinutes(15)));

        // Six minutes apart at a 30-second probe is a gap, not a slow reading.
        Assert.Empty(Energy.FromPower([], [new(T0, 100), new(T0 + 360, 100)], ThirtySeconds));
    }

    [Fact]
    public void A_pair_across_an_hour_boundary_is_split_between_the_hours()
    {
        var hours = Energy.FromPower([], [new(T0 + 1800, 100), new(T0 + 5400, 100)], TimeSpan.FromMinutes(30));

        Assert.Equal(0.05, hours[T0].Kwh, 6);
        Assert.Equal(0.05, hours[T0 + Hour].Kwh, 6);
        Assert.Equal(1800, hours[T0].CoveredSeconds, 3);
    }

    [Fact]
    public void An_hourly_summary_counts_for_the_part_of_the_hour_its_readings_cover()
    {
        var full = new Energy.HourSummary(T0, 200, 150, 250, 120, T0 + 3590, 200);
        var half = new Energy.HourSummary(T0 + Hour, 200, 150, 250, 60, T0 + Hour + 1790, 200);

        var hours = Energy.FromPower([full, half], [], ThirtySeconds);

        Assert.Equal(0.2, hours[T0].Kwh, 6);
        Assert.Equal(0.1, hours[T0 + Hour].Kwh, 6);
        Assert.Equal(1800, hours[T0 + Hour].CoveredSeconds, 3);
    }

    [Fact]
    public void Negative_watts_are_export_and_count_as_nothing()
    {
        Assert.Equal(0, Total(Energy.FromPower([], Steady(T0, T0 + Hour, -50), ThirtySeconds)), 9);
    }

    [Fact]
    public void The_interval_is_the_median_gap_or_the_fallback_with_too_few_readings()
    {
        Energy.Reading[] readings = [new(0, 1), new(30, 1), new(60, 1), new(90, 1), new(150, 1)];
        Assert.Equal(ThirtySeconds, Energy.EstimateInterval(readings, TimeSpan.FromMinutes(9)));
        Assert.Equal(TimeSpan.FromMinutes(9), Energy.EstimateInterval([new(0, 1), new(30, 1)], TimeSpan.FromMinutes(9)));
    }

    // ---------- counters ----------

    [Fact]
    public void A_counter_that_resets_counts_what_it_read_after_the_reset()
    {
        Energy.Reading[] readings = [new(T0, 10.0), new(T0 + 900, 10.2), new(T0 + 1800, 0.1), new(T0 + 2700, 0.3)];

        // 0.2 before the reset, 0.1 since it (the counter started again from zero), 0.2 after.
        Assert.Equal(0.5, Total(Energy.FromCounter([], readings)), 6);
    }

    [Fact]
    public void A_counter_wobbling_in_its_last_digit_is_not_a_reset()
    {
        Energy.Reading[] readings = [new(T0, 10.0), new(T0 + 900, 9.999), new(T0 + 1800, 10.1)];

        Assert.Equal(0.101, Total(Energy.FromCounter([], readings)), 6);
    }

    [Fact]
    public void A_counter_keeps_the_energy_used_while_nobody_was_looking()
    {
        var hours = Energy.FromCounter([], [new(T0, 1.0), new(T0 + 6 * Hour, 2.2)]);

        Assert.Equal(6, hours.Count);
        Assert.All(hours.Values, h => Assert.Equal(0.2, h.Kwh, 6));
        Assert.All(hours.Values, h => Assert.Equal(3600, h.CoveredSeconds, 3));
    }

    [Fact]
    public void A_reset_inside_a_summarised_hour_counts_the_climb_before_it()
    {
        var before = new Energy.HourSummary(T0, 49.5, 49, 50, 120, T0 + 3599, 50);
        var reset = new Energy.HourSummary(T0 + Hour, 20, 0, 52, 120, T0 + Hour + 3599, 1);

        // 50 → 52 before the reset, 0 → 1 after it.
        Assert.Equal(3, Total(Energy.FromCounter([before, reset], [])), 6);
    }

    [Fact]
    public void A_counter_in_watt_hours_is_scaled_to_kilowatt_hours()
    {
        Assert.Equal(2, Total(Energy.FromCounter([], [new(T0, 1000), new(T0 + Hour, 3000)], scale: 0.001)), 6);
    }

    [Fact]
    public void The_counter_wins_where_it_has_hours_and_the_watts_fill_in_the_rest()
    {
        var counter = new SortedDictionary<long, Energy.HourEnergy> { [T0] = new(1.0, 3600) };
        var watts = new SortedDictionary<long, Energy.HourEnergy> { [T0] = new(0.5, 3600), [T0 + Hour] = new(0.7, 3600) };

        var merged = Energy.Prefer(counter, watts);

        Assert.Equal(1.0, merged[T0].Kwh);
        Assert.Equal(0.7, merged[T0 + Hour].Kwh);
    }

    // ---------- prices ----------

    private static readonly TimeOfUseWindow Night =
        new("Night", 0.05, TimeOfUseWindow.EveryDay, new TimeOnly(22, 0), new TimeOnly(6, 0));

    [Fact]
    public void A_night_rate_runs_past_midnight()
    {
        var tariff = new PowerTariff(0.2, "$", 0, [Night]);

        Assert.Equal(0.05, tariff.PriceAt(Local(2026, 9, 15, 23, 0), Eastern));
        Assert.Equal(0.05, tariff.PriceAt(Local(2026, 9, 16, 5, 59), Eastern));
        Assert.Equal(0.2, tariff.PriceAt(Local(2026, 9, 16, 6, 0), Eastern));
        Assert.Equal(0.2, tariff.PriceAt(Local(2026, 9, 16, 12, 0), Eastern));
    }

    [Fact]
    public void A_window_past_midnight_belongs_to_the_day_it_starts()
    {
        var friday = new TimeOfUseWindow("Friday night", 0.01, [DayOfWeek.Friday], new TimeOnly(23, 0), new TimeOnly(7, 0));

        // 18 September 2026 is a Friday.
        Assert.True(friday.Covers(new DateTime(2026, 9, 18, 23, 30, 0)));
        Assert.True(friday.Covers(new DateTime(2026, 9, 19, 3, 0, 0)));
        Assert.False(friday.Covers(new DateTime(2026, 9, 18, 3, 0, 0)));
        Assert.False(friday.Covers(new DateTime(2026, 9, 19, 23, 30, 0)));
    }

    [Fact]
    public void A_window_starting_and_ending_together_is_the_whole_day()
    {
        var weekend = new TimeOfUseWindow("Weekend", 0.08, [DayOfWeek.Saturday, DayOfWeek.Sunday], new TimeOnly(0, 0), new TimeOnly(0, 0));

        Assert.True(weekend.Covers(new DateTime(2026, 9, 20, 12, 0, 0)));
        Assert.False(weekend.Covers(new DateTime(2026, 9, 21, 12, 0, 0)));
    }

    [Fact]
    public void The_first_matching_window_wins()
    {
        var weekend = new TimeOfUseWindow("Weekend", 0.08, [DayOfWeek.Saturday, DayOfWeek.Sunday], new TimeOnly(0, 0), new TimeOnly(0, 0));
        var tariff = new PowerTariff(0.2, "$", 0, [weekend, Night]);

        Assert.Equal(0.08, tariff.PriceAt(Local(2026, 9, 19, 23, 0), Eastern));
        Assert.Equal(0.05, tariff.PriceAt(Local(2026, 9, 21, 23, 0), Eastern));
    }

    [Fact]
    public void Both_one_oclocks_on_the_night_the_clocks_go_back_are_night_rate()
    {
        var tariff = new PowerTariff(0.2, "$", 0, [Night]);

        // 1 November 2026: 01:00–02:00 happens twice, at 05:00 and 06:00 UTC.
        var first = new DateTimeOffset(2026, 11, 1, 5, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        Assert.Equal(0.05, tariff.PriceForHour(first, Eastern), 9);
        Assert.Equal(0.05, tariff.PriceForHour(first + Hour, Eastern), 9);
    }

    [Fact]
    public void A_window_starting_on_the_half_hour_charges_half_the_hour_at_each_price()
    {
        var peak = new TimeOfUseWindow("Peak", 0.3, TimeOfUseWindow.EveryDay, new TimeOnly(16, 30), new TimeOnly(21, 0));
        var tariff = new PowerTariff(0.1, "$", 0, [peak]);

        var four = new DateTimeOffset(2026, 9, 15, 16, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        Assert.Equal(0.2, tariff.PriceForHour(four, TimeZoneInfo.Utc), 9);
    }

    private static SortedDictionary<long, Energy.HourEnergy> EveryHour(DateTimeOffset from, DateTimeOffset to, double kwh)
    {
        var hours = new SortedDictionary<long, Energy.HourEnergy>();
        for (var t = from.ToUnixTimeSeconds(); t < to.ToUnixTimeSeconds(); t += Hour)
            hours[t] = new Energy.HourEnergy(kwh, 3600);
        return hours;
    }

    [Fact]
    public void The_day_the_clocks_go_back_has_twenty_five_hours_of_energy_and_the_day_they_go_forward_twenty_three()
    {
        var hours = EveryHour(new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero), 0.1);
        hours = new SortedDictionary<long, Energy.HourEnergy>(hours
            .Concat(EveryHour(new DateTimeOffset(2026, 3, 6, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero), 0.1))
            .ToDictionary());
        var tariff = new PowerTariff(0.1, "$", 0, []);

        var autumn = PowerCost.Daily(hours, tariff, Eastern, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 2));
        Assert.Equal(2.5, autumn[0].Kwh, 6);
        Assert.Equal(2.4, autumn[1].Kwh, 6);

        var spring = PowerCost.Daily(hours, tariff, Eastern, new DateOnly(2026, 3, 8), new DateOnly(2026, 3, 8));
        Assert.Equal(2.3, spring[0].Kwh, 6);
    }

    [Fact]
    public void Each_hour_is_charged_at_its_own_rate_on_a_clock_change_day()
    {
        var peak = new TimeOfUseWindow("Peak", 0.3, TimeOfUseWindow.EveryDay, new TimeOnly(16, 0), new TimeOnly(21, 0));
        var tariff = new PowerTariff(0.1, "$", 0, [peak]);
        var hours = EveryHour(new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero), 0.1);

        var day = PowerCost.Daily(hours, tariff, Eastern, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1))[0];

        // Five peak hours at 0.30 and the other twenty at 0.10.
        Assert.Equal(0.35, day.Cost, 6);
    }

    [Fact]
    public void Money_is_written_the_way_the_currency_is()
    {
        Assert.Equal("$1.24", PowerTariff.FormatMoney(1.244, "$"));
        Assert.Equal("1.20 kr", PowerTariff.FormatMoney(1.2, "kr"));
        Assert.Equal("under $0.01", PowerTariff.FormatMoney(0.001, "$"));
        Assert.Equal("$0.00", PowerTariff.FormatMoney(0, "$"));
    }

    [Fact]
    public void A_tariff_reads_back_what_was_saved()
    {
        var weekend = new TimeOfUseWindow("Weekend", 0.08, [DayOfWeek.Saturday, DayOfWeek.Sunday], new TimeOnly(0, 0), new TimeOnly(0, 0));
        var saved = new PowerTariff(0.27, "€", 12.5, [Night, weekend]);

        var read = PowerTariff.From(new SettingsBag(saved.ToSettings()));

        Assert.Equal(0.27, read.PricePerKwh);
        Assert.Equal("€", read.Currency);
        Assert.Equal(12.5, read.MonthlyFee);
        Assert.Equal(2, read.Windows.Count);
        Assert.Equal(Night.Start, read.Windows[0].Start);
        Assert.Equal(Night.End, read.Windows[0].End);
        Assert.Equal([DayOfWeek.Saturday, DayOfWeek.Sunday], read.Windows[1].Days);
    }

    [Fact]
    public void Nothing_saved_is_fifteen_cents_a_kilowatt_hour_in_dollars()
    {
        var read = PowerTariff.From(new SettingsBag());

        Assert.Equal(0.15, read.PricePerKwh);
        Assert.Equal("$", read.Currency);
        Assert.Empty(read.Windows);
        Assert.Empty(PowerTariff.ParseWindows("not json"));
    }

    // ---------- periods and the projection ----------

    [Fact]
    public void The_month_is_projected_from_the_last_weeks_rate()
    {
        var now = new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);
        var month = new PeriodEnergy(24, 2.4, 240, 240);
        var week = new PeriodEnergy(16.8, 1.68, 168, 168);

        var (cost, kwh) = PowerCost.Project(month, week, now, now.AddHours(480));

        Assert.Equal(7.2, cost, 6);
        Assert.Equal(72, kwh, 6);
    }

    [Fact]
    public void A_gap_in_the_last_week_does_not_lower_the_projection()
    {
        var now = new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);
        var month = new PeriodEnergy(24, 2.4, 240, 240);
        var halfMeasured = new PeriodEnergy(8.4, 0.84, 84, 168);

        Assert.Equal(7.2, PowerCost.Project(month, halfMeasured, now, now.AddHours(480)).Cost, 6);
    }

    [Fact]
    public void With_no_week_to_go_on_the_month_so_far_is_the_rate_and_with_nothing_it_is_all_there_is()
    {
        var now = new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero);
        var day = new PeriodEnergy(2.4, 0.24, 24, 24);

        Assert.Equal(0.24 + 0.01 * 100, PowerCost.Project(day, PeriodEnergy.Zero, now, now.AddHours(100)).Cost, 6);
        Assert.Equal(0, PowerCost.Project(PeriodEnergy.Zero, PeriodEnergy.Zero, now, now.AddHours(100)).Cost);
    }

    [Fact]
    public void A_report_adds_up_today_the_month_and_the_projection_in_the_local_zone()
    {
        var now = Local(2026, 9, 15, 12);
        var hours = EveryHour(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), now, 0.1);
        var tariff = new PowerTariff(0.15, "$", 0, []);

        var report = PowerCost.Report("k", "NAS plug", hours, false, tariff, Eastern, now, 30);

        Assert.Equal(1.2, report.Today.Kwh, 6);
        Assert.Equal(34.8, report.Month.Kwh, 6);
        Assert.Equal(1, report.Month.Coverage, 6);
        Assert.Equal(100, report.Month.AverageWatts!.Value, 6);
        Assert.Equal(72, report.ProjectedMonthKwh, 6);
        Assert.Equal(10.8, report.ProjectedMonthCost, 6);
        Assert.Equal(30, report.Days.Count);
        Assert.Equal(new DateOnly(2026, 9, 15), report.Days[^1].Date);
    }

    [Fact]
    public void A_month_with_a_gap_says_how_much_of_it_was_measured()
    {
        var now = Local(2026, 9, 11, 0);
        // Measured for the first five days only.
        var hours = EveryHour(Local(2026, 9, 1, 0), Local(2026, 9, 6, 0), 0.1);

        var month = PowerCost.Period(hours, PowerTariff.Default, Eastern, Local(2026, 9, 1, 0), now);

        Assert.Equal(0.5, month.Coverage, 6);
        Assert.Equal(100, month.AverageWatts!.Value, 6);
    }

    // ---------- attribution ----------

    private static PowerReport Report(string key, string name, double monthCost) =>
        new(key, name, PeriodEnergy.Zero, new PeriodEnergy(monthCost * 10, monthCost, 100, 100),
            PeriodEnergy.Zero, PeriodEnergy.Zero, monthCost * 2, monthCost * 20, [], false);

    [Fact]
    public void A_plugs_cost_is_split_evenly_or_by_weight_and_added_up_per_service()
    {
        var setup = new PowerSetup(
        [
            new("a", "", true, [new("NAS"), new("Plex"), new("Frigate")], false),
            new("b", "", true, [new("Plex", 3), new("Other", 1)], true),
        ]);

        var services = PowerCost.Attribute([Report("a", "NAS plug", 3), Report("b", "Desk plug", 4)], setup);

        Assert.Equal(["Plex", "Frigate", "NAS", "Other"], services.Select(s => s.Name));
        Assert.Equal(4, services[0].MonthCost, 6);
        Assert.Equal(["NAS plug", "Desk plug"], services[0].Sources);
        Assert.Equal(8, services[0].ProjectedMonthCost, 6);
        Assert.All(services.Skip(1), s => Assert.Equal(1, s.MonthCost, 6));
    }

    [Fact]
    public void All_zero_weights_split_evenly_and_a_plug_with_nothing_on_it_is_not_split()
    {
        var setup = new PowerSetup([new("a", "", true, [new("NAS", 0), new("Plex", 0)], true)]);

        var services = PowerCost.Attribute([Report("a", "NAS plug", 2), Report("b", "Desk", 5)], setup);

        Assert.Equal(2, services.Count);
        Assert.All(services, s => Assert.Equal(1, s.MonthCost, 6));
    }

    [Fact]
    public void What_is_on_a_plug_is_written_on_one_line()
    {
        var (services, weighted) = ServiceShare.Parse("NAS=2, Plex, Frigate=3,  ");
        Assert.True(weighted);
        Assert.Equal([new ServiceShare("NAS", 2), new ServiceShare("Plex", 1), new ServiceShare("Frigate", 3)], services);
        Assert.Equal("NAS=2, Plex=1, Frigate=3", ServiceShare.Format(services, weighted));

        var (even, isWeighted) = ServiceShare.Parse("NAS, Plex");
        Assert.False(isWeighted);
        Assert.Equal("NAS, Plex", ServiceShare.Format(even, isWeighted));
    }

    [Fact]
    public void The_source_settings_read_back_what_was_saved()
    {
        var setup = new PowerSetup([new("c1/watts", "NAS plug", false, [new("NAS", 2), new("Plex", 1)], true)]);

        var read = PowerSetup.From(new SettingsBag(setup.ToSettings()));

        var source = Assert.Single(read.Sources);
        Assert.Equal("NAS plug", source.Name);
        Assert.False(source.Include);
        Assert.True(source.Weighted);
        Assert.Equal(2, source.Services[0].Weight);
        Assert.Same(PowerSetup.Empty, PowerSetup.From(new SettingsBag { [PowerSetup.SourcesKey] = "{broken" }));
    }

    // ---------- which metrics are power ----------

    [Fact]
    public void A_shellys_watts_and_meter_are_one_source()
    {
        var shelly = new ShellyProvider(null!).Metrics;

        var pair = Assert.Single(PowerMetrics.Pair(shelly));
        Assert.Equal("watts", pair.Power?.Key);
        Assert.Equal("energy_kwh", pair.Counter?.Key);
        Assert.Equal(1, pair.Scale);
    }

    [Fact]
    public void Sensors_named_by_the_user_pair_up_by_name_and_a_lone_meter_is_a_source_of_its_own()
    {
        var metrics = new[] { "rack_watts", "rack_kwh", "desk_watts", "solar_kwh", "office_temp", "latency_ms" }
            .Select(MetricSpec.Fallback);

        var pairs = PowerMetrics.Pair(metrics);

        Assert.Equal(3, pairs.Count);
        Assert.Contains(pairs, p => p.Power?.Key == "rack_watts" && p.Counter?.Key == "rack_kwh");
        Assert.Contains(pairs, p => p.Power?.Key == "desk_watts" && p.Counter is null);
        Assert.Contains(pairs, p => p.Power is null && p.Counter?.Key == "solar_kwh");
    }

    [Fact]
    public void A_ups_reports_its_load_in_watts_when_it_can_be_worked_out()
    {
        Assert.True(PowerMetrics.IsPower(new NutProvider().Metrics.Single(m => m.Key == "load_watts")));
        Assert.False(PowerMetrics.IsPower(new NutProvider().Metrics.Single(m => m.Key == "load_percent")));

        Assert.Equal(230, NutProvider.LoadWatts(new Dictionary<string, string> { ["ups.realpower"] = "230", ["ups.load"] = "40" }, ""));
        Assert.Equal(360, NutProvider.LoadWatts(new Dictionary<string, string> { ["ups.load"] = "40", ["ups.realpower.nominal"] = "900" }, ""));
        Assert.Equal(300, NutProvider.LoadWatts(new Dictionary<string, string> { ["ups.load"] = "50" }, "600"));
        Assert.Null(NutProvider.LoadWatts(new Dictionary<string, string> { ["ups.load"] = "50" }, ""));
    }

    // ---------- the shortcode's words ----------

    [Fact]
    public void Power_is_a_shortcode_that_needs_no_arguments()
    {
        var bare = Shortcodes.Parse("{{power}}");
        Assert.NotNull(bare);
        Assert.Equal("power", bare.Kind);
        Assert.True(bare.IsKnown);
        Assert.False(bare.IsBlock);

        var one = Shortcodes.Parse("{{power: \"NAS plug\" period=today show=kwh}}")!;
        Assert.Equal("NAS plug", one.Part(0));
        Assert.Equal("today", one.Option("period"));

        Assert.Equal("{{power}}", Shortcodes.Write("power", []));
        var written = Shortcodes.Write("power", ["NAS plug"], [new("period", "30d")]);
        Assert.Equal("NAS plug", Shortcodes.Parse(written)!.Part(0));
    }

    [Fact]
    public void The_shortcode_shows_cost_kilowatt_hours_or_watts_for_the_period_asked()
    {
        var totals = new PowerTotals(
            new PeriodEnergy(1.2, 0.18, 12, 12), new PeriodEnergy(16.8, 2.52, 168, 168),
            new PeriodEnergy(34.8, 5.22, 348, 348), new PeriodEnergy(72, 10.8, 720, 720), 10.8, 72);

        Assert.Equal("$5.22", PowerShortcode.Text(totals, PowerShortcode.Show.Cost, PowerShortcode.Period.Month, PowerTariff.Default));
        Assert.Equal("1.20 kWh", PowerShortcode.Text(totals, PowerShortcode.Show.Kwh, PowerShortcode.Period.Today, PowerTariff.Default));
        Assert.Equal("100 W", PowerShortcode.Text(totals, PowerShortcode.Show.Watts, PowerShortcode.Period.Last30, PowerTariff.Default));
        Assert.Equal("$10.80", PowerShortcode.Text(totals, PowerShortcode.Show.Cost, PowerShortcode.Period.Projected, PowerTariff.Default));

        Assert.Equal(PowerShortcode.Period.Month, PowerShortcode.ParsePeriod(""));
        Assert.Equal(PowerShortcode.Period.Last30, PowerShortcode.ParsePeriod("30d"));
        Assert.Null(PowerShortcode.ParsePeriod("fortnight"));
        Assert.Null(PowerShortcode.ParseShow("volts"));
    }

    // ---------- the weekly summary ----------

    [Fact]
    public void The_weekly_summary_says_what_the_week_cost_and_what_cost_most()
    {
        var data = new WeeklySummaryData
        {
            From = new DateTimeOffset(2026, 9, 21, 9, 0, 0, TimeSpan.Zero),
            To = new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeSpan.Zero),
            Power = new PowerWeek(42.1, 6.32, "$", [("NAS plug", 3.1), ("UPS", 2.0), ("Desk", 1.22), ("Lamp", 0)], 27.5),
        };

        var digest = WeeklySummary.Build(data);

        Assert.Contains("⚡ Power", digest.Text);
        Assert.Contains("42.1 kWh, about $6.32 this week — on course for $27.50 this month", digest.Text);
        Assert.Contains("Most: NAS plug $3.10, UPS $2.00, Desk $1.22", digest.Text);

        Assert.DoesNotContain("⚡", WeeklySummary.Build(data with { Power = null }).Text);
        Assert.DoesNotContain("⚡", WeeklySummary.Build(data with { Power = new PowerWeek(0, 0, "$", []) }).Text);
    }

    // ---------- the queries ----------

    private static List<string> Plan(string sql)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var create = connection.CreateCommand();
        create.CommandText =
            "CREATE TABLE samples (connection_id TEXT NOT NULL, metric TEXT NOT NULL, ts INTEGER NOT NULL, value REAL NOT NULL);" +
            "CREATE INDEX ix_samples_lookup ON samples (connection_id, metric, ts);" +
            "CREATE TABLE samples_hourly (connection_id TEXT NOT NULL, metric TEXT NOT NULL, hour_ts INTEGER NOT NULL, " +
            "min REAL NOT NULL, max REAL NOT NULL, avg REAL NOT NULL, count INTEGER NOT NULL, last_ts INTEGER NOT NULL, " +
            "last_value REAL NOT NULL, PRIMARY KEY (connection_id, metric, hour_ts)) WITHOUT ROWID;" +
            "CREATE INDEX ix_samples_hourly_age ON samples_hourly (hour_ts);";
        create.ExecuteNonQuery();

        var explain = connection.CreateCommand();
        explain.CommandText = "EXPLAIN QUERY PLAN " + sql
            .Replace("$c", "'plug'").Replace("$m", "'watts'").Replace("$from", "0").Replace("$to", "1");
        var steps = new List<string>();
        using var reader = explain.ExecuteReader();
        while (reader.Read())
            steps.Add(reader.GetString(3));
        return steps;
    }

    [Fact]
    public void The_energy_reads_seek_their_series_rather_than_scanning()
    {
        var raw = Plan(Storage.HistoryStore.EnergyRawSql);
        Assert.DoesNotContain(raw, step => step.StartsWith("SCAN", StringComparison.Ordinal));
        Assert.Contains(raw, step => step.Contains("ix_samples_lookup", StringComparison.Ordinal));
        // Already in time order from the index — no sort of a week of rows.
        Assert.DoesNotContain(raw, step => step.Contains("TEMP B-TREE", StringComparison.Ordinal));

        var hourly = Plan(Storage.HistoryStore.EnergySummariesSql);
        Assert.DoesNotContain(hourly, step => step.StartsWith("SCAN", StringComparison.Ordinal));
        Assert.Contains(hourly, step => step.Contains("PRIMARY KEY", StringComparison.Ordinal));
        Assert.DoesNotContain(hourly, step => step.Contains("TEMP B-TREE", StringComparison.Ordinal));
    }
}
