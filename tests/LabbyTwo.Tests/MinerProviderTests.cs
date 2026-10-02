using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using LabbyTwo.Core;
using LabbyTwo.Providers;

namespace LabbyTwo.Tests;

/// <summary>
/// The Bitcoin miner provider against a fake miner on loopback, answering the two shapes of
/// <c>/api/system/info</c>: NMMiner's (exactly what a real one on firmware v2.0.02 sent, with
/// a made-up full-length payout address) and AxeOS's flat one. Nothing leaves the machine.
/// </summary>
public sealed class MinerProviderTests
{
    /// <summary>A plausible bech32 address that belongs to nobody. Long enough to be masked.</summary>
    public const string Wallet = "bc1q8kuzgpw3v7xq9ys0m4lq2d6cn5rkh8tfe7jupmdssa";

    public const string MaskedWorker = "bc1q8k…dssa.nmminer1";

    public static string NmMinerPayload(double accepted = 16398, double rejected = 119, double uptime = 167916,
        double blocks = 0) => $$$"""
        {"identity":{"hwModel":"NMMiner","hostName":"cyd2.8_68c361","fwVersion":"v2.0.02","rssi":-51},
         "miner":{"hashRate":0.000000000,"sAccepted":{{{accepted}}},"sRejected":{{{rejected}}},"uptimeSeconds":{{{uptime}}},"uptimeEver":8613620,
          "networkDiff":"132.8T","poolDiff":"0.0020 ","lastDiff":"0.0038 ","bestDiffSession":"306.59 ","bestDiffEver":"827.47 ",
          "blkhits":{{{blocks}}},"freeHeap":63344,"minFreeHeap":28872},
         "stratum":{"url":"solobtc.nmminer.com:3333","user":"{{{Wallet}}}.nmminer1"},
         "temps":{"vcore":null,"asic":null},"storage":{"fsTotal":393216,"fsUsed":217088}}
        """;

    public const string BitaxeWallet = "bc1qx9t2l7d0c4m8v6n3k5j7h9g2f4d6s8a0q1w3e5";

    public const string BitaxePayload = $$"""
        {"power":14.2,"voltage":5100,"current":2800,"temp":58.5,"vrTemp":49,"hashRate":512.34,
         "bestDiff":"4.29G","bestSessionDiff":"12.1M","poolDifficulty":1024,"sharesAccepted":3200,"sharesRejected":12,
         "uptimeSeconds":86400,"ASICModel":"BM1366","stratumURL":"public-pool.io","stratumPort":21496,
         "stratumUser":"{{BitaxeWallet}}.bitaxe1","version":"v2.4.1","hostname":"bitaxe","wifiRSSI":-62,
         "frequency":485,"coreVoltage":1200,"fanrpm":4200,"fanspeed":60,"freeHeap":180000}
        """;

    private static Connection Miner(string address, bool showWorker = false) => new()
    {
        Provider = "miner",
        Name = "NMMiner 1",
        Settings = new SettingsBag { ["address"] = address, ["show_worker"] = showWorker ? "true" : "false" },
    };

    private static MinerReading Reading(string json)
    {
        using var document = JsonDocument.Parse(json);
        return MinerReading.Parse(document.RootElement)!;
    }

    // ---- NMMiner -------------------------------------------------------------------------

    [Fact]
    public async Task An_nmminer_is_read_with_its_padded_and_suffixed_difficulties()
    {
        using var miner = new FakeMiner(NmMinerPayload());
        var provider = new MinerProvider(new RealFactory());

        var result = await provider.ProbeAsync(Miner(miner.Address), CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        var m = result.Metrics!;
        Assert.Equal(0, m["hashrate_ghs"]);
        Assert.Equal(16398, m["shares_accepted"]);
        Assert.Equal(119, m["shares_rejected"]);
        Assert.Equal(119.0 / (16398 + 119) * 100, m["reject_percent"], 6);
        Assert.Equal(306.59, m["best_diff_session"], 6);
        Assert.Equal(827.47, m["best_diff_ever"], 6);
        Assert.Equal(132.8e12, m["network_diff"], 0);
        Assert.Equal(0.002, m["pool_diff"], 9);
        Assert.Equal(0, m["block_hits"]);
        Assert.Equal(167916 / 3600.0, m["uptime_hours"], 6);
        Assert.Equal(-51, m["wifi_rssi"]);
        Assert.Equal(63344 / 1024.0, m["free_heap_kb"], 6);
        Assert.True(m.ContainsKey("latency_ms"));

        // A rate needs two readings; the first only remembers the counters.
        Assert.False(m.ContainsKey("shares_per_hour"));
        Assert.False(m.ContainsKey("est_hashrate_ghs"));
        Assert.False(m.ContainsKey("new_blocks"));
        // NMMiner reports its temperatures as null: no reading, not zero degrees.
        Assert.False(m.ContainsKey("temp_c"));
        Assert.False(m.ContainsKey("power_w"));

        var d = result.Details!;
        Assert.Equal("NMMiner", d["Model"]);
        Assert.Equal("v2.0.02", d["Firmware"]);
        Assert.Equal("cyd2.8_68c361", d["Hostname"]);
        Assert.Equal("solobtc.nmminer.com:3333", d["Pool"]);
        Assert.Equal(MaskedWorker, d["Worker"]);
        Assert.Equal("NMMiner API", d["Speaks"]);
        Assert.StartsWith("NMMiner", result.Message);
        Assert.Contains("16398 shares accepted", result.Message);
    }

    [Fact]
    public async Task The_payout_address_is_masked_everywhere_unless_asked_for()
    {
        using var miner = new FakeMiner(NmMinerPayload());
        var provider = new MinerProvider(new RealFactory());
        var connection = Miner(miner.Address);

        // Twice, so the message with a rate in it is checked as well as the first one.
        var first = await provider.ProbeAsync(connection, CancellationToken.None);
        var second = await provider.ProbeAsync(connection, CancellationToken.None);

        foreach (var result in new[] { first, second })
        {
            Assert.DoesNotContain(Wallet, result.Message);
            Assert.DoesNotContain(result.Details!.Values, v => v.Contains(Wallet, StringComparison.Ordinal));
            Assert.DoesNotContain(result.Details!.Keys, k => k.Contains(Wallet, StringComparison.Ordinal));
        }

        var shown = await provider.ProbeAsync(Miner(miner.Address, showWorker: true), CancellationToken.None);
        Assert.Equal($"{Wallet}.nmminer1", shown.Details!["Worker"]);
    }

    [Fact]
    public async Task Broken_json_says_so_without_quoting_the_answer()
    {
        // Cut off mid-way, after the address — JsonException would quote the bytes it choked on.
        var truncated = NmMinerPayload()[..(NmMinerPayload().IndexOf(Wallet, StringComparison.Ordinal) + Wallet.Length + 12)];
        using var miner = new FakeMiner(truncated);

        var result = await new MinerProvider(new RealFactory()).ProbeAsync(Miner(miner.Address), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("valid JSON", result.Message);
        Assert.DoesNotContain(Wallet, result.Message);
    }

    [Theory]
    [InlineData("""{"status":"ok","uptime":12}""", "not in a shape")]
    [InlineData("<html><body>gzip would be here</body></html>", "valid JSON")]
    [InlineData("[1,2,3]", "not in a shape")]
    public async Task Something_that_is_not_a_miner_is_down(string body, string expected)
    {
        using var miner = new FakeMiner(body);

        var result = await new MinerProvider(new RealFactory()).ProbeAsync(Miner(miner.Address), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains(expected, result.Message);
    }

    [Fact]
    public async Task A_404_and_nothing_listening_are_both_down()
    {
        using var other = new FakeMiner(NmMinerPayload(), status: HttpStatusCode.NotFound);
        var provider = new MinerProvider(new RealFactory());

        var notFound = await provider.ProbeAsync(Miner(other.Address), CancellationToken.None);
        Assert.False(notFound.Ok);
        Assert.Contains("HTTP 404", notFound.Message);

        LoopbackListener.Start(out var port).Close();
        var nothing = await provider.ProbeAsync(Miner($"127.0.0.1:{port}"), CancellationToken.None);
        Assert.False(nothing.Ok);

        var blank = await provider.ProbeAsync(Miner(""), CancellationToken.None);
        Assert.False(blank.Ok);
        Assert.Contains("No address", blank.Message);
    }

    [Theory]
    [InlineData("192.168.86.34", "http://192.168.86.34")]
    [InlineData("192.168.86.34/", "http://192.168.86.34")]
    [InlineData("http://192.168.86.34/api/system/info", "http://192.168.86.34")]
    [InlineData("https://bitaxe.lan:8443/", "https://bitaxe.lan:8443")]
    [InlineData("  ", "")]
    public void Any_address_a_person_types_means_the_miner(string written, string expected) =>
        Assert.Equal(expected, MinerProvider.BaseUrl(written));

    // ---- AxeOS ---------------------------------------------------------------------------

    [Fact]
    public async Task A_bitaxe_is_recognised_by_its_shape_and_reports_its_hardware()
    {
        using var miner = new FakeMiner(BitaxePayload);
        var provider = new MinerProvider(new RealFactory());
        var connection = Miner(miner.Address);

        var result = await provider.ProbeAsync(connection, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        var m = result.Metrics!;
        Assert.Equal(512.34, m["hashrate_ghs"], 6);
        Assert.Equal(3200, m["shares_accepted"]);
        Assert.Equal(12, m["shares_rejected"]);
        Assert.Equal(4.29e9, m["best_diff_ever"], 0);
        Assert.Equal(12.1e6, m["best_diff_session"], 0);
        Assert.Equal(1024, m["pool_diff"]);
        Assert.Equal(58.5, m["temp_c"], 6);
        Assert.Equal(49, m["vr_temp_c"]);
        Assert.Equal(14.2, m["power_w"], 6);
        Assert.Equal(4200, m["fan_rpm"]);
        Assert.Equal(-62, m["wifi_rssi"]);
        Assert.Equal(24, m["uptime_hours"], 6);

        Assert.Equal("Bitaxe · BM1366", result.Details!["Model"]);
        Assert.Equal("public-pool.io:21496", result.Details["Pool"]);
        Assert.Equal("bc1qx9…w3e5.bitaxe1", result.Details["Worker"]);
        Assert.Equal("AxeOS API", result.Details["Speaks"]);
        Assert.DoesNotContain(BitaxeWallet, result.Message);
        Assert.Contains("512 GH/s", result.Message);
    }

    [Fact]
    public async Task Restart_is_offered_only_once_a_probe_has_seen_axeos()
    {
        using var bitaxe = new FakeMiner(BitaxePayload);
        using var nmminer = new FakeMiner(NmMinerPayload());
        var provider = new MinerProvider(new RealFactory());
        var axe = Miner(bitaxe.Address);
        var nm = Miner(nmminer.Address);

        // Nothing before a probe: it is not known what either one is.
        Assert.Empty(provider.ActionsFor(axe));

        await provider.ProbeAsync(axe, CancellationToken.None);
        await provider.ProbeAsync(nm, CancellationToken.None);

        var restart = Assert.Single(provider.ActionsFor(axe));
        Assert.Equal("restart", restart.Id);
        Assert.NotNull(restart.Disrupts);
        Assert.True(restart.NeedsConfirming);
        Assert.Empty(provider.ActionsFor(nm));

        var done = await provider.RunActionAsync(axe, restart, new SettingsBag(), CancellationToken.None);
        Assert.True(done.Ok, done.Message);
        Assert.Contains("POST /api/system/restart", bitaxe.Requests);

        var refused = await provider.RunActionAsync(nm, restart, new SettingsBag(), CancellationToken.None);
        Assert.False(refused.Ok);
        Assert.DoesNotContain(nmminer.Requests, r => r.StartsWith("POST", StringComparison.Ordinal));
    }

    // ---- rates ---------------------------------------------------------------------------

    [Fact]
    public void Shares_become_a_rate_and_an_estimated_hashrate_and_a_reboot_starts_again()
    {
        var provider = new MinerProvider(new RealFactory());
        var connection = Miner("192.168.86.34");
        var at = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

        var first = provider.Read(connection, Reading(NmMinerPayload(accepted: 1000, uptime: 5000)), TimeSpan.Zero, at);
        Assert.False(first.Metrics!.ContainsKey("shares_per_hour"));

        // Sixty shares in ten minutes at difficulty 0.002.
        var second = provider.Read(connection, Reading(NmMinerPayload(accepted: 1060, uptime: 5600)), TimeSpan.Zero, at.AddMinutes(10));
        Assert.Equal(360, second.Metrics!["shares_per_hour"], 6);
        var expected = 60 / 600.0 * 0.002 * 4294967296 / 1e9;   // ≈ 0.000859 GH/s, 859 KH/s
        Assert.Equal(expected, second.Metrics["est_hashrate_ghs"], 12);
        Assert.Equal("859 KH/s", MinerUnits.Hashrate(second.Metrics["est_hashrate_ghs"]));
        // The estimate leads the tile when the miner's own figure is zero.
        Assert.Contains("≈859 KH/s", second.Message);
        Assert.Contains("360 shares/h", second.Message);

        // Rebooted: the counter and the uptime both went back. No rate rather than a negative one.
        var rebooted = provider.Read(connection, Reading(NmMinerPayload(accepted: 4, uptime: 40)), TimeSpan.Zero, at.AddMinutes(11));
        Assert.False(rebooted.Metrics!.ContainsKey("shares_per_hour"));
        Assert.False(rebooted.Metrics.ContainsKey("est_hashrate_ghs"));

        // And counting resumes from the reboot.
        var after = provider.Read(connection, Reading(NmMinerPayload(accepted: 34, uptime: 340)), TimeSpan.Zero, at.AddMinutes(16));
        Assert.Equal(360, after.Metrics!["shares_per_hour"], 6);
    }

    [Fact]
    public void The_rate_looks_back_about_fifteen_minutes_not_to_the_first_probe_ever()
    {
        var trail = new ShareTrail();
        var at = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

        // An hour at 600/h, then ten quiet minutes.
        for (var minute = 0; minute <= 60; minute++)
            trail.Add(new ShareSample(at.AddMinutes(minute), minute * 10, minute * 60, null), 0.002);
        for (var minute = 61; minute <= 70; minute++)
            trail.Add(new ShareSample(at.AddMinutes(minute), 600, minute * 60, null), 0.002);

        var (rates, _) = trail.Add(new ShareSample(at.AddMinutes(71), 600, 71 * 60, null), 0.002);

        // The window starts at minute 56: 40 shares in 15 minutes is 160/h. Since the first
        // probe ever it would have been 507/h and hidden that the miner had nearly stopped.
        Assert.Equal(160, rates!.SharesPerHour, 6);
    }

    [Fact]
    public void A_quiet_window_reads_zero_and_trips_the_no_shares_rule()
    {
        var trail = new ShareTrail();
        var at = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        trail.Add(new ShareSample(at, 500, 100, null), 0.002);
        var (rates, _) = trail.Add(new ShareSample(at.AddMinutes(20), 500, 1300, null), 0.002);

        Assert.Equal(0, rates!.SharesPerHour);
        Assert.Equal(0, rates.EstimatedGhs);
        Assert.True(Rule("No shares accepted for 30 min").IsBreaching(rates.SharesPerHour));
    }

    [Fact]
    public void No_pool_difficulty_means_no_estimate_rather_than_a_wrong_one()
    {
        var at = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var rates = ShareTrail.Rate(new ShareSample(at, 0, null, null), new ShareSample(at.AddMinutes(1), 6, null, null), null);

        Assert.Equal(360, rates!.SharesPerHour, 6);
        Assert.Null(rates.EstimatedGhs);
        // Too close together to be a rate at all.
        Assert.Null(ShareTrail.Rate(new ShareSample(at, 0, null, null), new ShareSample(at, 6, null, null), 1));
    }

    [Fact]
    public void A_found_block_is_reported_once()
    {
        var provider = new MinerProvider(new RealFactory());
        var connection = Miner("192.168.86.34");
        var at = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var rule = Rule("Block found!");

        var first = provider.Read(connection, Reading(NmMinerPayload(accepted: 10, uptime: 100)), TimeSpan.Zero, at);
        var quiet = provider.Read(connection, Reading(NmMinerPayload(accepted: 20, uptime: 160)), TimeSpan.Zero, at.AddMinutes(1));
        var found = provider.Read(connection, Reading(NmMinerPayload(accepted: 30, uptime: 220, blocks: 1)), TimeSpan.Zero, at.AddMinutes(2));
        var after = provider.Read(connection, Reading(NmMinerPayload(accepted: 40, uptime: 280, blocks: 1)), TimeSpan.Zero, at.AddMinutes(3));

        Assert.False(first.Metrics!.ContainsKey("new_blocks"));
        Assert.False(rule.IsBreaching(quiet.Metrics!["new_blocks"]));
        Assert.True(rule.IsBreaching(found.Metrics!["new_blocks"]));
        Assert.False(rule.IsBreaching(after.Metrics!["new_blocks"]));
        Assert.Equal(1, after.Metrics["block_hits"]);
    }

    [Fact]
    public async Task Two_probes_of_a_live_miner_produce_a_rate()
    {
        var clock = new SteppingClock(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
        using var miner = new FakeMiner(NmMinerPayload(accepted: 100, uptime: 1000));
        var provider = new MinerProvider(new RealFactory(), clock);
        var connection = Miner(miner.Address);

        await provider.ProbeAsync(connection, CancellationToken.None);
        clock.Now += TimeSpan.FromMinutes(5);
        miner.Body = NmMinerPayload(accepted: 130, uptime: 1300);
        var result = await provider.ProbeAsync(connection, CancellationToken.None);

        Assert.Equal(360, result.Metrics!["shares_per_hour"], 6);
        Assert.True(result.Metrics["est_hashrate_ghs"] > 0);
    }

    // ---- units ---------------------------------------------------------------------------

    [Theory]
    [InlineData("306.59 ", 306.59)]
    [InlineData("827.47 ", 827.47)]
    [InlineData("0.0020 ", 0.002)]
    [InlineData("132.8T", 132.8e12)]
    [InlineData("1.2M", 1.2e6)]
    [InlineData("4.29G", 4.29e9)]
    [InlineData("12k", 12e3)]
    [InlineData("12 K", 12e3)]
    [InlineData("1.5P", 1.5e15)]
    [InlineData("2E", 2e18)]
    [InlineData("42", 42)]
    public void Difficulties_are_read_however_a_miner_writes_them(string written, double expected) =>
        Assert.Equal(expected, MinerUnits.ParseDifficulty(written)!.Value, expected * 1e-12);

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    [InlineData("T")]
    [InlineData("lots")]
    [InlineData("NaN")]
    public void Anything_else_is_not_a_difficulty(string? written) => Assert.Null(MinerUnits.ParseDifficulty(written));

    [Theory]
    [InlineData(0, "0 H/s")]
    [InlineData(-1, "0 H/s")]
    [InlineData(0.000000001, "1 H/s")]
    [InlineData(0.000000512, "512 H/s")]
    [InlineData(0.00084, "840 KH/s")]
    [InlineData(0.0012, "1.2 MH/s")]
    [InlineData(0.5, "500 MH/s")]
    [InlineData(1.234, "1.23 GH/s")]
    [InlineData(51.27, "51.3 GH/s")]
    [InlineData(512.34, "512 GH/s")]
    [InlineData(1210, "1.21 TH/s")]
    [InlineData(2.5e6, "2.5 PH/s")]
    public void Hashrates_pick_their_own_unit(double gigahashes, string expected) =>
        Assert.Equal(expected, MinerUnits.Hashrate(gigahashes));

    [Theory]
    [InlineData(132.8e12, "132.8T")]
    [InlineData(827.47, "827.47")]
    [InlineData(0.002, "0.002")]
    [InlineData(4.29e9, "4.29G")]
    [InlineData(1500, "1.5K")]
    [InlineData(0, "0")]
    public void Difficulties_are_written_the_way_miners_write_them(double value, string expected) =>
        Assert.Equal(expected, MinerUnits.FormatDifficulty(value));

    [Fact]
    public void Every_tile_and_table_formats_a_hashrate_and_a_difficulty_the_same_way()
    {
        var provider = new MinerProvider(new RealFactory());
        var est = provider.Metrics.Single(m => m.Key == "est_hashrate_ghs");
        var best = provider.Metrics.Single(m => m.Key == "best_diff_ever");

        // Through the one formatter every card, chart and alert line uses — whatever the
        // decimals a widget asks for, which would otherwise print 0.00084 GH/s as "0.00".
        Assert.Equal("840 KH/s", Units.Format(est, 0.00084, Units.Preferences.Default));
        Assert.Equal("840 KH/s", Units.Format(est, 0.00084, Units.Preferences.Default, 1));
        Assert.Equal("132.8T", Units.Format(best, 132.8e12, Units.Preferences.Default));
    }

    // ---- masking -------------------------------------------------------------------------

    [Theory]
    [InlineData(Wallet + ".nmminer1", MaskedWorker)]
    [InlineData(Wallet, "bc1q8k…dssa")]
    [InlineData("chris.worker1", "chris.worker1")]
    [InlineData("", "")]
    public void A_payout_address_is_shortened_and_the_worker_name_kept(string user, string expected) =>
        Assert.Equal(expected, MinerProvider.MaskWorker(user));

    // ---- rules ---------------------------------------------------------------------------

    private static AlertRule Rule(string name) =>
        new MinerProvider(new RealFactory()).SuggestedRules.Single(r => r.Name == name).ForConnection("c");

    [Theory]
    [InlineData("No shares accepted for 30 min", 350, false)]   // the real NMMiner: ~350/h
    [InlineData("No shares accepted for 30 min", 4, false)]     // one share in a fifteen-minute window
    [InlineData("No shares accepted for 30 min", 0.5, false)]   // on the line: strict
    [InlineData("No shares accepted for 30 min", 0, true)]
    [InlineData("Rejected shares high", 0.72, false)]
    [InlineData("Rejected shares high", 5, false)]
    [InlineData("Rejected shares high", 5.1, true)]
    [InlineData("Weak WiFi", -51, false)]
    [InlineData("Weak WiFi", -80, false)]
    [InlineData("Weak WiFi", -81, true)]
    [InlineData("Running hot", 58.5, false)]
    [InlineData("Running hot", 70, false)]
    [InlineData("Running hot", 71, true)]
    [InlineData("Block found!", 0, false)]
    [InlineData("Block found!", 1, true)]
    public void Suggested_rules_leave_a_healthy_miner_alone(string name, double value, bool fires)
    {
        var rule = Rule(name);
        Assert.Equal(fires, rule.IsBreaching(value));
        Assert.Null(rule.Problem());
    }

    [Fact]
    public void Every_suggested_rule_watches_a_metric_the_provider_declares()
    {
        var provider = new MinerProvider(new RealFactory());
        var declared = provider.Metrics.Select(m => m.Key).ToHashSet();
        Assert.All(provider.SuggestedRules, rule => Assert.Contains(rule.Metric, declared));
    }

    // ---- scan ----------------------------------------------------------------------------

    [Fact]
    public void A_scan_covers_one_slash_24_at_most()
    {
        var hosts = MinerScan.Hosts("192.168.86.0/24");
        Assert.Equal(254, hosts.Count);
        Assert.Equal("192.168.86.1", hosts[0]);
        Assert.Equal("192.168.86.254", hosts[^1]);

        Assert.Equal(hosts, MinerScan.Hosts("192.168.86.34/24"));
        Assert.Equal(hosts, MinerScan.Hosts("192.168.86"));
        Assert.Equal(hosts, MinerScan.Hosts(" 192.168.86.34 "));
        Assert.Equal(["192.168.86.33", "192.168.86.34"], MinerScan.Hosts("192.168.86.34/30"));
        Assert.Equal(["192.168.86.34"], MinerScan.Hosts("192.168.86.34/32"));

        Assert.Throws<FormatException>(() => MinerScan.Hosts("192.168.0.0/16"));
        Assert.Throws<FormatException>(() => MinerScan.Hosts("my network"));
        Assert.Throws<FormatException>(() => MinerScan.Hosts("192.168.86.0/abc"));
        Assert.Throws<FormatException>(() => MinerScan.Hosts("fd00::/120"));
    }

    [Fact]
    public async Task A_scan_finds_the_miners_and_ignores_everything_else()
    {
        using var nmminer = new FakeMiner(NmMinerPayload());
        using var bitaxe = new FakeMiner(BitaxePayload);
        using var printer = new FakeMiner("<html>printer</html>");
        using var web = new FakeMiner("{}", status: HttpStatusCode.NotFound);
        LoopbackListener.Start(out var closed).Close();

        string[] subnet = [web.Authority, nmminer.Authority, $"127.0.0.1:{closed}", printer.Authority, bitaxe.Authority];
        var progress = new List<int>();
        using var http = new HttpClient();

        var found = await MinerScan.ScanAsync(http, subnet, progress: new SyncProgress(progress.Add));

        Assert.Equal([nmminer.Authority, bitaxe.Authority], found.Select(f => f.Address));
        Assert.Equal(MinerSchema.NMMiner, found[0].Schema);
        Assert.Equal("cyd2.8_68c361", found[0].Hostname);
        Assert.Equal(MinerSchema.AxeOS, found[1].Schema);
        Assert.Equal("Bitaxe · BM1366", found[1].Model);
        Assert.Equal(subnet.Length, progress.Max());
    }

    [Fact]
    public async Task A_scan_asks_at_most_sixteen_at_a_time_and_gives_up_on_slow_ones()
    {
        using var slow = new FakeMiner(NmMinerPayload(), delay: TimeSpan.FromMilliseconds(150));
        using var stuck = new FakeMiner(NmMinerPayload(), delay: TimeSpan.FromSeconds(5));
        var subnet = Enumerable.Repeat(slow.Authority, 48).Append(stuck.Authority).ToList();
        using var http = new HttpClient();

        var started = DateTime.UtcNow;
        var found = await MinerScan.ScanAsync(http, subnet, timeout: TimeSpan.FromSeconds(1));

        Assert.Equal(48, found.Count);
        Assert.InRange(slow.MostAtOnce, 1, MinerScan.DefaultConcurrency);
        // The stuck one is abandoned at the one-second limit, not waited for.
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(4));
    }

    // ---- fakes ---------------------------------------------------------------------------

    private sealed class RealFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(10) };
    }

    private sealed class SteppingClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Progress that reports on the calling thread, so the test sees every count.</summary>
    private sealed class SyncProgress(Action<int> report) : IProgress<int>
    {
        private readonly Lock _gate = new();
        public void Report(int value)
        {
            lock (_gate)
                report(value);
        }
    }

    /// <summary>A miner's web server on a loopback port: /api/system/info and /api/system/restart.</summary>
    internal sealed class FakeMiner : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly HttpStatusCode _status;
        private readonly TimeSpan _delay;
        private int _current;
        private int _most;

        public string Authority { get; }
        public string Address => $"http://{Authority}";
        public volatile string Body;
        public ConcurrentQueue<string> Requests { get; } = new();
        public int MostAtOnce => _most;

        public FakeMiner(string body, HttpStatusCode status = HttpStatusCode.OK, TimeSpan? delay = null)
        {
            Body = body;
            _status = status;
            _delay = delay ?? TimeSpan.Zero;
            _listener = LoopbackListener.Start(out var port);
            Authority = $"127.0.0.1:{port}";
            _ = Task.Run(ServeAsync);
        }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception) when (_stop.IsCancellationRequested || !_listener.IsListening)
                {
                    return;
                }
                _ = Task.Run(() => AnswerAsync(context));
            }
        }

        private async Task AnswerAsync(HttpListenerContext context)
        {
            var now = Interlocked.Increment(ref _current);
            int seen;
            while (now > (seen = Volatile.Read(ref _most)) && Interlocked.CompareExchange(ref _most, now, seen) != seen)
            {
            }

            try
            {
                Requests.Enqueue($"{context.Request.HttpMethod} {context.Request.Url!.AbsolutePath}");
                if (_delay > TimeSpan.Zero)
                    await Task.Delay(_delay, _stop.Token);

                var (status, body) = context.Request.Url.AbsolutePath switch
                {
                    "/api/system/info" => (_status, Body),
                    "/api/system/restart" when context.Request.HttpMethod == "POST" => (HttpStatusCode.OK, "{}"),
                    _ => (HttpStatusCode.NotFound, ""),
                };
                var bytes = Encoding.UTF8.GetBytes(body);
                context.Response.StatusCode = (int)status;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
            catch (Exception)
            {
                // The client gave up (a scan's timeout) or the test is over.
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
        }
    }
}
