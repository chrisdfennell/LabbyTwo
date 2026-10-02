using System.Text.Json;
using System.Xml.Linq;
using LabbyTwo.Core;
using LabbyTwo.Providers;

namespace LabbyTwo.Tests;

/// <summary>
/// Who is on the shared Intel GPU: the hardware/software split read from Plex's XML and
/// Tautulli's JSON, Tdarr's workers by type, Tunarr's sessions, the sysfs reader against
/// fixture directories laid out the way each kernel driver does it, and the overlap warning.
/// The payloads are crafted to the shapes those APIs answer with — no network.
/// </summary>
public sealed class GpuViewTests : IDisposable
{
    private readonly string _directory = TestHost.TempDirectory();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception)
        {
            // A temp folder left behind is not a test failure.
        }
    }

    // ---- Plex ---------------------------------------------------------------------------

    /// <summary>Three sessions: Quick Sync full pipeline, a software fallback that still says hardware was requested, and direct play.</summary>
    public const string PlexSessions = """
        <MediaContainer size="4">
          <Video title="Dinner Party" grandparentTitle="The Office" duration="1000" viewOffset="250">
            <User title="Chris" /><Player title="Living room TV" />
            <TranscodeSession videoDecision="transcode" audioDecision="copy" transcodeHwRequested="1"
                              transcodeHwDecoding="qsv" transcodeHwEncoding="qsv" transcodeHwFullPipeline="1" />
          </Video>
          <Video title="Dune" year="2021" duration="100" viewOffset="50">
            <User title="Sam" /><Player title="iPad" />
            <TranscodeSession videoDecision="transcode" audioDecision="transcode" transcodeHwRequested="1" transcodeHwFullPipeline="0" />
          </Video>
          <Video title="Alien" year="1979" duration="100" viewOffset="10">
            <User title="Alex" /><Player title="Chrome" />
            <TranscodeSession videoDecision="transcode" audioDecision="copy" transcodeHwDecoding="vaapi" />
          </Video>
          <Video title="Heat" year="1995" duration="100" viewOffset="90">
            <User title="Jo" /><Player title="Shield" />
          </Video>
        </MediaContainer>
        """;

    [Fact]
    public void Plex_counts_a_transcode_as_hardware_only_when_the_GPU_is_actually_doing_it()
    {
        var sessions = PlexProvider.ReadSessions(XElement.Parse(PlexSessions));

        Assert.Equal([true, true, true, false], sessions.Select(s => s.Transcoding));
        // Requested is not used: Dune fell back to software and says transcodeHwRequested="1" anyway.
        Assert.Equal([true, false, true, false], sessions.Select(s => s.HardwareTranscode));
    }

    [Fact]
    public void Plex_audio_only_transcode_is_software()
    {
        var sessions = PlexProvider.ReadSessions(XElement.Parse("""
            <MediaContainer><Video title="x"><TranscodeSession videoDecision="copy" audioDecision="transcode" transcodeHwRequested="1" /></Video></MediaContainer>
            """));

        Assert.True(sessions[0].Transcoding);
        Assert.False(sessions[0].HardwareTranscode);
    }

    // ---- Tautulli -----------------------------------------------------------------------

    [Fact]
    public void Tautulli_splits_transcodes_by_its_hardware_flags_in_either_shape()
    {
        using var document = JsonDocument.Parse("""
            {"stream_count":"5","sessions":[
              {"user":"chris","transcode_decision":"transcode","stream_video_decision":"transcode",
               "transcode_hw_decoding":1,"transcode_hw_encoding":1},
              {"user":"sam","transcode_decision":"transcode","stream_video_decision":"transcode",
               "transcode_hw_decoding":"0","transcode_hw_encoding":"0"},
              {"user":"alex","transcode_decision":"transcode","stream_video_decision":"transcode",
               "transcode_hw_decoding":"1","transcode_hw_encoding":"0"},
              {"user":"jo","transcode_decision":"transcode","stream_video_decision":"copy",
               "transcode_hw_decoding":0,"transcode_hw_encoding":0},
              {"user":"kim","transcode_decision":"direct play","stream_video_decision":"direct play"},
              "garbage"
            ]}
            """);

        // Chris and Alex on the GPU; Sam fell back to software; Jo is audio only.
        Assert.Equal((2, 2), TautulliProvider.CountTranscodes(document.RootElement));
        Assert.Equal((0, 0), TautulliProvider.CountTranscodes(default));
    }

    // ---- Tdarr --------------------------------------------------------------------------

    /// <summary>A Tdarr v2 <c>/api/v2/get-nodes</c> answer: the internal node with four workers, one idle, and a second node.</summary>
    public const string TdarrNodes = """
        {
          "aBcD123": {
            "_id": "aBcD123", "nodeName": "InternalNode", "nodePaused": false,
            "workerLimits": {"healthcheckcpu": 1, "healthcheckgpu": 1, "transcodecpu": 1, "transcodegpu": 2},
            "workers": {
              "w-gpu-1": {"_id": "w-gpu-1", "workerType": "transcodegpu", "idle": false, "file": "/media/a.mkv", "percentage": 42},
              "w-gpu-2": {"_id": "w-gpu-2", "workerType": "transcodegpu", "idle": true},
              "w-cpu-1": {"_id": "w-cpu-1", "workerType": "transcodecpu", "idle": false, "file": "/media/b.mkv"},
              "w-hc-1":  {"_id": "w-hc-1",  "workerType": "healthcheckgpu", "file": "/media/c.mkv"}
            }
          },
          "eFgH456": {
            "_id": "eFgH456", "nodeName": "Laptop",
            "workers": {
              "w-hc-2": {"_id": "w-hc-2", "workerType": "healthcheckcpu", "idle": false},
              "odd": "not a worker"
            }
          },
          "broken": "not a node"
        }
        """;

    [Fact]
    public void Tdarr_counts_busy_workers_by_type_and_skips_idle_ones()
    {
        using var document = JsonDocument.Parse(TdarrNodes);

        var workers = TdarrProvider.ReadWorkers(document.RootElement);

        Assert.Equal(new TdarrProvider.Workers(Active: 4, Gpu: 2, Cpu: 2, HealthCheck: 2), workers);
    }

    [Fact]
    public void Tdarr_with_no_nodes_has_no_workers()
    {
        using var empty = JsonDocument.Parse("{}");
        using var array = JsonDocument.Parse("[]");

        Assert.Equal(new TdarrProvider.Workers(0, 0, 0, 0), TdarrProvider.ReadWorkers(empty.RootElement));
        Assert.Equal(new TdarrProvider.Workers(0, 0, 0, 0), TdarrProvider.ReadWorkers(array.RootElement));
    }

    // ---- Tunarr -------------------------------------------------------------------------

    [Fact]
    public void Tunarr_sessions_keyed_by_channel_count_only_the_ones_somebody_is_watching()
    {
        using var document = JsonDocument.Parse("""
            {
              "chan-1": [{"type": "hls", "state": "started", "numConnections": 2}],
              "chan-2": [{"type": "hls", "state": "started", "numConnections": 0}],
              "chan-3": {"type": "mpegts", "state": "started", "connections": {"a": {}, "b": {}, "c": {}}},
              "chan-4": [{"type": "hls", "state": "stopped"}, {"type": "hls", "state": "started"}]
            }
            """);

        // chan-2 is kept warm with nobody on it; chan-4's stopped session is history.
        Assert.Equal(new TunarrProvider.Sessions(Active: 3, Viewers: 6), TunarrProvider.ReadSessions(document.RootElement));
    }

    [Fact]
    public void Tunarr_sessions_as_a_bare_list_or_nothing()
    {
        using var list = JsonDocument.Parse("""[{"state":"started","connections":[{},{}]}, 7]""");
        using var empty = JsonDocument.Parse("{}");

        Assert.Equal(new TunarrProvider.Sessions(1, 2), TunarrProvider.ReadSessions(list.RootElement));
        Assert.Equal(new TunarrProvider.Sessions(0, 0), TunarrProvider.ReadSessions(empty.RootElement));
    }

    [Fact]
    public void Tunarr_hardware_acceleration_from_old_settings_or_new_configs()
    {
        using var old = JsonDocument.Parse("""{"ffmpegExecutablePath":"/usr/bin/ffmpeg","hardwareAccelerationMode":"qsv"}""");
        using var configs = JsonDocument.Parse("""[{"name":"Default","hardwareAccelerationMode":"vaapi"},{"name":"Old","hardwareAccelerationMode":"none"},{"name":"x"}]""");
        using var neither = JsonDocument.Parse("""{"ffmpegExecutablePath":"/usr/bin/ffmpeg"}""");

        Assert.Equal("qsv", TunarrProvider.ReadHardwareAcceleration(old.RootElement));
        Assert.Equal("vaapi,none", TunarrProvider.ReadHardwareAcceleration(configs.RootElement));
        Assert.Null(TunarrProvider.ReadHardwareAcceleration(neither.RootElement));
    }

    // ---- sysfs --------------------------------------------------------------------------

    private string Drm(string name) => Path.Combine(_directory, name, "class", "drm");

    private static void Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text + "\n");
    }

    [Fact]
    public void Sysfs_reads_a_legacy_i915_card()
    {
        var root = Drm("legacy");
        Write(root, "card0/gt_act_freq_mhz", "1100");
        Write(root, "card0/gt_max_freq_mhz", "1300");
        Write(root, "card0/gt_RP0_freq_mhz", "1350");
        Write(root, "card0/power/rc6_residency_ms", "123456");
        Write(root, "card0/device/uevent", "DRIVER=i915\nPCI_CLASS=30000");
        // A connector, not a card: never mistaken for one.
        Write(root, "card0-HDMI-A-1/status", "disconnected");

        var look = GpuSysfs.Look([root]);

        Assert.Equal(new GpuSample(root, "card0", "i915", 1100, 1350, 123456), look.Sample);
    }

    [Fact]
    public void Sysfs_reads_the_per_gt_and_xe_layouts()
    {
        var perGt = Drm("pergt");
        Write(perGt, "card0/gt/gt0/rps_act_freq_mhz", "450");
        Write(perGt, "card0/gt/gt0/rps_RP0_freq_mhz", "2250");
        Write(perGt, "card0/gt/gt0/rc6_residency_ms", "999");

        var xe = Drm("xe");
        Write(xe, "card1/device/tile0/gt0/freq0/act_freq", "0");
        Write(xe, "card1/device/tile0/gt0/freq0/max_freq", "1950");
        Write(xe, "card1/device/tile0/gt0/gtidle/idle_residency_ms", "5000");

        Assert.Equal(new GpuSample(perGt, "card0", "", 450, 2250, 999), GpuSysfs.Look([perGt]).Sample);
        Assert.Equal(new GpuSample(xe, "card1", "", 0, 1950, 5000), GpuSysfs.Look([xe]).Sample);
    }

    [Fact]
    public void Sysfs_with_only_a_frequency_is_a_partial_reading()
    {
        var root = Drm("partial");
        Write(root, "card0/gt_act_freq_mhz", "300");
        Write(root, "card0/gt_max_freq_mhz", "not a number");

        var sample = GpuSysfs.Look([root]).Sample;

        Assert.NotNull(sample);
        Assert.Equal(300, sample.ActualMhz);
        Assert.Null(sample.MaxMhz);
        Assert.Null(sample.SleepMs);
    }

    [Fact]
    public void Sysfs_skips_a_card_that_is_not_intel_and_honours_the_card_setting()
    {
        var root = Drm("two");
        Write(root, "card0/device/uevent", "DRIVER=amdgpu");
        Write(root, "card1/gt_act_freq_mhz", "700");
        Write(root, "card2/gt_act_freq_mhz", "900");

        Assert.Equal("card1", GpuSysfs.Look([root]).Sample?.Card);
        Assert.Equal("card2", GpuSysfs.Look([root], "card2").Sample?.Card);
    }

    [Fact]
    public void Sysfs_absent_says_so_plainly_and_falls_through_the_roots()
    {
        var missing = Path.Combine(_directory, "nowhere");
        var mounted = Drm("mounted");
        Write(mounted, "card0/gt_act_freq_mhz", "350");

        var none = GpuSysfs.Look([missing]);
        Assert.Null(none.Sample);
        Assert.Equal(GpuSysfs.NotVisible, none.Why);

        Assert.Equal(350, GpuSysfs.Look([missing, mounted]).Sample?.ActualMhz);
    }

    [Fact]
    public void Sysfs_with_cards_but_nothing_readable_explains_the_half_mount()
    {
        var root = Drm("halfmount");
        Write(root, "card0/dev", "226:0");

        var look = GpuSysfs.Look([root]);

        Assert.Null(look.Sample);
        Assert.Contains("/sys/devices", look.Why);
        Assert.EndsWith(GpuSysfs.NotVisible, look.Why);
    }

    [Theory]
    [InlineData(0, 10_000, 10_000, 0)]        // slept the whole ten seconds
    [InlineData(0, 2_500, 10_000, 75)]        // slept a quarter of it
    [InlineData(0, 0, 10_000, 100)]           // never slept
    [InlineData(0, 12_000, 10_000, 0)]        // counter ran a little ahead of the clock
    public void Busy_is_the_share_of_the_gap_spent_out_of_RC6(double before, double after, int gapMs, double expected)
    {
        var at = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, GpuSysfs.BusyPercent(before, at, after, at.AddMilliseconds(gapMs)));
    }

    [Fact]
    public void Busy_is_unknown_across_a_reset_or_too_short_a_gap()
    {
        var at = DateTimeOffset.UtcNow;
        Assert.Null(GpuSysfs.BusyPercent(5000, at, 100, at.AddSeconds(30)));
        Assert.Null(GpuSysfs.BusyPercent(0, at, 10, at.AddMilliseconds(200)));
    }

    // ---- who is on it -------------------------------------------------------------------

    private static GpuSource Source(string provider, string name, bool? up, params (string Key, double Value)[] metrics) =>
        new(provider, name, up, metrics.ToDictionary(m => m.Key, m => m.Value));

    [Fact]
    public void Tdarr_on_the_GPU_while_Plex_transcodes_on_it_is_the_overlap()
    {
        var picture = GpuUsage.From(
        [
            Source("plex", "Plex", true, ("transcodes_hw", 2), ("transcodes_sw", 1)),
            Source("tdarr", "Tdarr", true, ("gpu_workers_active", 1), ("cpu_workers_active", 1)),
            new GpuSource("tunarr", "Tunarr", true, new Dictionary<string, double> { ["active_sessions"] = 0 }),
        ]);

        Assert.True(picture.PlexAndTdarrOverlap);
        Assert.Equal(2, picture.AppsOnGpu);
        Assert.Equal(
            [
                new GpuUser("Plex", "Plex", 2, "2 hardware transcodes", "1 transcode in software", true),
                new GpuUser("Tdarr", "Tdarr", 1, "1 GPU worker", "1 CPU worker busy", true),
                new GpuUser("Tunarr", "Tunarr", 0, "nothing streaming", "", true),
            ],
            picture.Users);
        var warning = Assert.Single(picture.Warnings);
        Assert.StartsWith("Tdarr is transcoding on the GPU while Plex has 2 hardware transcodes — Plex may stutter.", warning);
    }

    [Fact]
    public void No_warning_when_they_take_turns()
    {
        Assert.Empty(GpuUsage.From(
        [
            Source("plex", "Plex", true, ("transcodes_hw", 0), ("transcodes_sw", 2)),
            Source("tdarr", "Tdarr", true, ("gpu_workers_active", 2)),
        ]).Warnings);

        Assert.False(GpuUsage.From(
        [
            Source("plex", "Plex", true, ("transcodes_hw", 1)),
            Source("tdarr", "Tdarr", true, ("gpu_workers_active", 0), ("cpu_workers_active", 3)),
        ]).PlexAndTdarrOverlap);
    }

    [Fact]
    public void A_down_connection_contributes_nothing_and_says_so()
    {
        var picture = GpuUsage.From(
        [
            Source("plex", "Plex", true, ("transcodes_hw", 1)),
            // Its last good reading had a GPU worker, but it is down: that is history.
            Source("tdarr", "Tdarr", false, ("gpu_workers_active", 1)),
            Source("tunarr", "Tunarr", null),
        ]);

        Assert.False(picture.PlexAndTdarrOverlap);
        Assert.Equal("not answering", picture.Users[1].Summary);
        Assert.Equal("checking…", picture.Users[2].Summary);
        Assert.Empty(picture.Warnings);
    }

    [Fact]
    public void Tautulli_stands_in_for_Plex_only_when_Plex_is_not_answering()
    {
        var both = GpuUsage.From(
        [
            Source("plex", "Plex", true, ("transcodes_hw", 2)),
            Source("tautulli", "Tautulli", true, ("transcodes_hw", 2)),
        ]);
        Assert.Equal(2, both.PlexHardware);
        Assert.Single(both.Users);

        var alone = GpuUsage.From([Source("tautulli", "Tautulli", true, ("transcodes_hw", 1))]);
        Assert.Equal(1, alone.PlexHardware);
        Assert.Equal("Plex", alone.Users[0].App);
    }

    [Fact]
    public void Tunarr_in_software_is_not_on_Quick_Sync_and_Tunarr_on_it_overlaps_with_Tdarr()
    {
        var software = GpuUsage.From(
        [
            new GpuSource("tunarr", "Tunarr", true, new Dictionary<string, double> { ["active_sessions"] = 2 },
                new Dictionary<string, string> { ["hw_accel"] = "none" }),
            Source("tdarr", "Tdarr", true, ("gpu_workers_active", 1)),
        ]);
        Assert.Equal(0, software.TunarrStreams);
        Assert.Equal("2 channels streaming, not on Quick Sync", software.Users[0].Summary);
        Assert.Equal("ffmpeg is set to software", software.Users[0].Note);
        Assert.Empty(software.Warnings);

        var qsv = GpuUsage.From(
        [
            new GpuSource("tunarr", "Tunarr", true, new Dictionary<string, double> { ["active_sessions"] = 1 },
                new Dictionary<string, string> { ["hw_accel"] = "qsv" }),
            Source("tdarr", "Tdarr", true, ("gpu_workers_active", 1)),
        ]);
        Assert.Equal(1, qsv.TunarrStreams);
        Assert.Contains(qsv.Warnings, w => w.Contains("Tunarr is streaming 1 channel"));
    }

    [Fact]
    public void A_flat_out_GPU_is_called_out()
    {
        Assert.Contains("95% busy", Assert.Single(GpuUsage.From([], busyPercent: 95).Warnings));
        Assert.Empty(GpuUsage.From([], busyPercent: 60).Warnings);
    }

    // ---- the provider's arithmetic ------------------------------------------------------

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    [Fact]
    public void The_provider_works_out_busy_from_two_samples_and_records_the_overlap()
    {
        var provider = new IntelGpuProvider(new NoServices());
        var connection = new Connection { Provider = IntelGpuProvider.ProviderType, Name = "GPU" };
        var at = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        GpuSource[] apps =
        [
            Source("plex", "Plex", true, ("transcodes_hw", 1)),
            Source("tdarr", "Tdarr", true, ("gpu_workers_active", 1)),
        ];

        var first = provider.Read(connection, new GpuLook(new GpuSample("/sys/class/drm", "card0", "i915", 1100, 1350, 10_000), ""), apps, at);
        Assert.True(first.Ok);
        Assert.False(first.Metrics!.ContainsKey("gpu_busy_percent"));
        Assert.Equal("measuring", first.Details!["load"]);
        Assert.Equal(1, first.Metrics["overlap"]);
        Assert.Equal(2, first.Metrics["apps_on_gpu"]);

        // Thirty seconds later it has slept six of them: 80% busy.
        var second = provider.Read(connection, new GpuLook(new GpuSample("/sys/class/drm", "card0", "i915", 1350, 1350, 16_000), ""), apps, at.AddSeconds(30));
        Assert.Equal(80, second.Metrics!["gpu_busy_percent"], 3);
        Assert.Equal(1350, second.Metrics["freq_act_mhz"]);
        Assert.StartsWith("80% busy · 1350/1350 MHz · 2 apps on Quick Sync", second.Message);
    }

    [Fact]
    public void The_provider_with_no_GPU_visible_is_still_up_and_still_counts_the_apps()
    {
        var provider = new IntelGpuProvider(new NoServices());
        var connection = new Connection { Provider = IntelGpuProvider.ProviderType, Name = "GPU" };

        var result = provider.Read(connection, new GpuLook(null, GpuSysfs.NotVisible),
            [Source("plex", "Plex", true, ("transcodes_hw", 2))], DateTimeOffset.UtcNow);

        Assert.True(result.Ok);
        Assert.Equal("Load not visible from this container · 1 app on Quick Sync", result.Message);
        Assert.Equal(GpuSysfs.NotVisible, result.Details!["host"]);
        Assert.Equal(2, result.Metrics!["plex_hw_transcodes"]);
        Assert.Equal(0, result.Metrics["overlap"]);
        Assert.False(result.Metrics.ContainsKey("freq_act_mhz"));
    }

    [Fact]
    public async Task The_probe_never_throws_with_nothing_to_read()
    {
        var provider = new IntelGpuProvider(new NoServices());
        var connection = new Connection
        {
            Provider = IntelGpuProvider.ProviderType,
            Name = "GPU",
            Settings = new SettingsBag { ["path"] = Path.Combine(_directory, "nowhere") },
        };

        var result = await provider.ProbeAsync(connection, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("no Plex, Tdarr or Tunarr connected", result.Message);
    }
}
