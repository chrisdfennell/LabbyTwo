#pragma warning disable BL0006 // The interactive test renderer is a Renderer, which is the whole point of it.
using System.Net;
using System.Net.Sockets;
using System.Text;
using LabbyTwo.Components.Pages;
using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Services.Offsite;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LabbyTwo.Tests;

/// <summary>
/// Clicking, for the render tests that have buttons to press: finds a button or link by
/// the words on it and sends it the click a browser would.
/// </summary>
internal sealed partial class InteractiveRenderer
{
    /// <summary>
    /// Clicks the first (or <paramref name="last"/>) button or link whose text contains
    /// <paramref name="text"/> and that has a click handler. Throws when there is none, so a
    /// test never passes by clicking nothing.
    /// </summary>
    public Task ClickAsync(string text, bool last = false) => Dispatcher.InvokeAsync(async () =>
    {
        var found = new List<ulong>();
        FindClicks(_root, text, found);
        if (found.Count == 0)
            throw new InvalidOperationException($"Nothing clickable says “{text}”.");
        await DispatchEventAsync(last ? found[^1] : found[0], new EventFieldInfo(), new MouseEventArgs());
    });

    private void FindClicks(int componentId, string text, List<ulong> found)
    {
        var frames = GetCurrentRenderTreeFrames(componentId);
        FindClicks(frames.Array, 0, frames.Count, text, found);
    }

    private void FindClicks(RenderTreeFrame[] frames, int start, int end, string text, List<ulong> found)
    {
        for (var i = start; i < end; i++)
        {
            var frame = frames[i];
            switch (frame.FrameType)
            {
                case RenderTreeFrameType.Element:
                {
                    var last = i + frame.ElementSubtreeLength;
                    if (frame.ElementName is "button" or "a")
                    {
                        ulong click = 0;
                        for (var a = i + 1; a < last && frames[a].FrameType == RenderTreeFrameType.Attribute; a++)
                        {
                            if (frames[a].AttributeName == "onclick" && frames[a].AttributeEventHandlerId != 0)
                                click = frames[a].AttributeEventHandlerId;
                        }
                        var words = new StringBuilder();
                        TextOf(frames, i + 1, last, words);
                        if (click != 0 && WebUtility.HtmlDecode(words.ToString()).Contains(text, StringComparison.Ordinal))
                            found.Add(click);
                    }
                    FindClicks(frames, i + 1, last, text, found);
                    i = last - 1;
                    break;
                }
                case RenderTreeFrameType.Component:
                    FindClicks(frame.ComponentId, text, found);
                    i += frame.ComponentSubtreeLength - 1;
                    break;
                case RenderTreeFrameType.Region:
                    FindClicks(frames, i + 1, i + frame.RegionSubtreeLength, text, found);
                    i += frame.RegionSubtreeLength - 1;
                    break;
            }
        }
    }

    private void TextOf(RenderTreeFrame[] frames, int start, int end, StringBuilder words)
    {
        for (var i = start; i < end; i++)
        {
            var frame = frames[i];
            switch (frame.FrameType)
            {
                case RenderTreeFrameType.Text:
                    words.Append(frame.TextContent);
                    break;
                case RenderTreeFrameType.Markup:
                    words.Append(frame.MarkupContent);
                    break;
                case RenderTreeFrameType.Component:
                {
                    var inner = GetCurrentRenderTreeFrames(frame.ComponentId);
                    TextOf(inner.Array, 0, inner.Count, words);
                    i += frame.ComponentSubtreeLength - 1;
                    break;
                }
            }
        }
    }
}

/// <summary>
/// The phone view against a real database, monitor, evaluator and alert service: each of
/// its states — all fine, something down with an alert firing, LabbyTwo unable to see,
/// maintenance — drawn by the real page; its buttons wired to what they say; and, once the
/// caches are warm, opening it reading nothing from the database at all.
/// </summary>
public sealed class PhoneViewTests : IAsyncDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly ServiceProvider _services;
    private readonly Lab _lab = new();
    private readonly RecordingChannel _channel = new();
    private InteractiveRenderer? _renderer;

    /// <summary>A lab whose probes answer as the test says, with a disk reading and two buttons.</summary>
    private sealed class Lab : IConnectionProvider
    {
        public string Type => "lab";
        public string DisplayName => "Lab";
        public string Icon => "";
        public string Description => "";
        public IReadOnlyList<FieldSpec> Fields => [];
        public IReadOnlyList<MetricSpec> Metrics => [new("disk_percent", "Disk used", "%")];

        /// <summary>Connection name → how its probe fails; absent means it answers.</summary>
        public Dictionary<string, ProbeFailure> Failing { get; } = [];

        public double Disk { get; set; } = 40;

        public List<string> Ran { get; } = [];

        public IReadOnlyList<ProviderAction> Actions =>
        [
            new("reboot", "Reboot") { Dangerous = true, ConfirmMessage = "Reboot it?" },
            new("wake", "Wake") { Confirms = false },
        ];

        public Task<ActionResult> RunActionAsync(Connection connection, ProviderAction action, SettingsBag input, CancellationToken ct)
        {
            lock (Ran)
                Ran.Add($"{connection.Name}:{action.Id}");
            return Task.FromResult(ActionResult.Done($"{action.Label} sent."));
        }

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
        {
            if (!Failing.TryGetValue(connection.Name, out var failure))
                return Task.FromResult(ProbeResult.Up(TimeSpan.FromMilliseconds(2), "OK",
                    new Dictionary<string, double> { ["disk_percent"] = Disk }));

            Exception ex = failure switch
            {
                ProbeFailure.Dns => new HttpRequestException("lookup", new SocketException((int)SocketError.TryAgain)),
                _ => new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused)),
            };
            return Task.FromResult(ProbeResult.Down(TimeSpan.FromMilliseconds(2), ProbeError.Describe(ex, $"http://{connection.Name}")));
        }
    }

    private sealed class RecordingChannel : IAlertChannel
    {
        public string Type => "recording";
        public string DisplayName => "Recording";
        public string Icon => "";
        public string Description => "";
        public IReadOnlyList<FieldSpec> Fields => [];
        public List<Alert> Sent { get; } = [];

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) => Task.FromResult(ProbeResult.Up(TimeSpan.Zero));

        public Task SendAsync(Connection channel, Alert alert, CancellationToken ct)
        {
            lock (Sent)
                Sent.Add(alert);
            return Task.CompletedTask;
        }
    }

    public PhoneViewTests()
    {
        Directory.CreateDirectory(_directory);
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddHttpClient();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        // One failed probe is down, so an outage shows in one sweep.
        services.AddTestStorage(_directory, options => options.FailuresBeforeDown = 1);
        services.AddSingleton<IConnectionProvider>(_lab);
        services.AddSingleton<IConnectionProvider>(_channel);
        services.AddSingleton<IEnumerable<IWidgetType>>([]);
        services.AddSingleton<IEnumerable<ITabKind>>([]);
        services.AddSingleton<Registry>();
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<AlertRuleStore>();
        services.AddSingleton<HistoryStore>();
        services.AddSingleton<HealthMonitor>();
        services.AddSingleton<AlertService>();
        services.AddSingleton<CapacityForecasts>();
        services.AddSingleton<MetricBaselines>();
        services.AddSingleton<MetricAlertService>();
        services.AddSingleton<ChangeStore>();
        services.AddSingleton<IncidentStore>();
        services.AddSingleton<IncidentTracker>();
        services.AddSingleton<ProbableCauses>();
        services.AddSingleton<FamilyReportStore>();
        services.AddSingleton<OffsiteSettingsStore>();
        services.AddSingleton<BackupStore>();
        services.AddSingleton<BackupProof>();
        services.AddSingleton<RemediationStore>();
        services.AddSingleton<ActionRunner>();
        services.AddSingleton<IRemediationActions, RemediationActions>();
        services.AddSingleton<Offload>();
        services.AddSingleton<DisplayUnits>();
        _services = services.BuildServiceProvider();
        Get<Db>().EnsureSchemaAsync().GetAwaiter().GetResult();
        Get<AlertService>().Zone = TimeZoneInfo.Utc;
    }

    public async ValueTask DisposeAsync()
    {
        if (_renderer is not null)
            await _renderer.DisposeAsync();
        TestHost.Teardown(_services, _directory);
    }

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private InteractiveRenderer Renderer => _renderer ??= new InteractiveRenderer(_services);

    private async Task<List<Connection>> LabAsync(params string[] names)
    {
        var connections = new List<Connection>();
        foreach (var name in names)
        {
            var connection = new Connection { Provider = "lab", Name = name };
            await Get<ConfigStore>().SaveConnectionAsync(connection);
            connections.Add(connection);
        }
        await Get<ConfigStore>().SaveConnectionAsync(new Connection { Provider = "recording", Name = "Phone" });
        return connections;
    }

    private Task OpenAsync() => Renderer.RenderAsync<PhonePage>(new Dictionary<string, object?>());

    private Task<string> SeeAsync(string expected) => Renderer.WaitForAsync(expected);

    private static string Text(string html) => WebUtility.HtmlDecode(html);

    // ---- the words ---------------------------------------------------------------------

    [Theory]
    [InlineData(24, 0, 0, 0, 0, "All 24 fine")]
    [InlineData(1, 0, 0, 0, 0, "All fine")]
    [InlineData(24, 2, 0, 1, 0, "2 down, 1 alert")]
    [InlineData(24, 0, 0, 3, 0, "3 alerts")]
    [InlineData(24, 0, 0, 0, 2, "2 things to look at")]
    [InlineData(24, 0, 4, 0, 0, "20 fine, 4 still checking")]
    [InlineData(24, 0, 24, 0, 0, "Checking…")]
    [InlineData(0, 0, 0, 0, 0, "Nothing is monitored yet")]
    public void The_line_at_the_top_says_what_is_red_first(int total, int down, int checking, int alerts, int other, string expected) =>
        Assert.Equal(expected, PhoneView.Headline(total, down, checking, alerts, other));

    [Fact]
    public void Pins_survive_being_stored_and_a_broken_line_is_skipped()
    {
        var pins = new[]
        {
            new PhonePin(PhonePinKind.Container, "dock1", "plex", "Restart Plex"),
            new PhonePin(PhonePinKind.Action, "pc", "wake"),
        };
        var stored = PhonePin.Store(pins) + "\nnonsense\ncontainer|dock1|PLEX|again";

        Assert.Equal(pins, PhonePin.ParseAll(stored));
    }

    [Theory]
    [InlineData("t/runbooks#note-abc123", "t/runbooks#note-abc123")]
    [InlineData("/t/runbooks#note-abc123", "t/runbooks#note-abc123")]
    [InlineData("https://lab.example/t/runbooks#note-abc123", "t/runbooks#note-abc123")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("//evil.example/x", null)]
    [InlineData("ftp://lab.example/t/x", null)]
    [InlineData("", null)]
    public void A_runbook_link_is_only_ever_a_path_inside_labbytwo(string written, string? expected) =>
        Assert.Equal(expected, PhoneView.RunbookLink(written));

    [Fact]
    public void The_installed_app_opens_where_it_was_asked_to()
    {
        Assert.Equal("manifest.webmanifest", PhoneView.Manifest(new SettingsBag()));
        Assert.Equal("manifest-phone.webmanifest", PhoneView.Manifest(new SettingsBag { [PhoneView.StartKey] = PhoneView.StartPhone }));

        // The second manifest is the same app opening somewhere else, not a second app.
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "wwwroot"));
        var phone = File.ReadAllText(Path.Combine(root, "manifest-phone.webmanifest"));
        var main = File.ReadAllText(Path.Combine(root, "manifest.webmanifest"));
        Assert.Contains("\"start_url\": \"/m\"", phone);
        Assert.Contains("\"id\": \"/\"", phone);
        Assert.Contains("\"id\": \"/\"", main);
    }

    // ---- the states --------------------------------------------------------------------

    [Fact]
    public async Task All_fine_is_one_line_and_everything_else_is_folded_away()
    {
        var lab = await LabAsync("NAS", "Plex", "Router");
        var tab = new Tab { Name = "Home", Slug = "home", Icon = "🏠" };
        await Get<ConfigStore>().SaveTabAsync(tab);
        await Get<ConfigStore>().SaveWidgetAsync(new Widget { TabId = tab.Id, Type = "status", ConnectionId = lab[1].Id });
        await Get<HealthMonitor>().SweepOnceAsync();

        await OpenAsync();
        var html = Text(await SeeAsync("All 3 fine"));

        Assert.Contains("Nothing needs you.", html);
        Assert.Contains("Maintenance for 1 h", html);
        Assert.DoesNotContain("can't see the lab", html);
        Assert.DoesNotContain("phone-row ", html);
        // Plex under its tab, the rest under "not on a tab".
        Assert.Contains("Home", html);
        Assert.Contains("Not on a tab", html);
        Assert.Contains("status-up", html);
    }

    [Fact]
    public async Task Something_down_and_an_alert_firing_are_rows_with_why_how_long_and_the_fix()
    {
        var lab = await LabAsync("NAS", "Plex", "Router");
        await Get<ConfigStore>().SaveConnectionAsync(lab[0] with
        {
            Settings = new SettingsBag { [PhoneView.RunbookKey] = "t/runbooks#note-nas" },
        });
        await Get<AlertRuleStore>().SaveAsync(new AlertRule
        {
            Name = "Disk nearly full", Metric = "disk_percent", Comparison = Comparison.Above, Threshold = 90,
            ConnectionId = lab[2].Id,
        });
        await Get<RemediationStore>().SaveAsync(new Remediation
        {
            Trigger = Remediation.DownTrigger(lab[0].Id),
            Kind = RemediationKind.RestartContainer,
            TargetConnectionId = lab[1].Id,
            Container = "nas-agent",
        });

        await Get<HealthMonitor>().SweepOnceAsync();
        _lab.Failing["NAS"] = ProbeFailure.Refused;
        _lab.Disk = 97;
        await Get<HealthMonitor>().SweepOnceAsync();
        await Get<MetricAlertService>().EvaluateAsync(DateTimeOffset.Now, CancellationToken.None);

        await OpenAsync();
        var html = Text(await SeeAsync("1 down, 1 alert"));

        Assert.Contains("NAS is down", html);
        Assert.Contains("refused", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("for 0s", html);
        Assert.Contains("Restart nas-agent", html);
        Assert.Contains("href=\"t/runbooks#note-nas\"", html);
        Assert.Contains("Silence 1 h", html);
        Assert.Contains("Reboot", html);
        Assert.Contains("Disk used is 97", html);
        Assert.Contains("status-down", html);
        Assert.Contains("phone-status is-red", html);
    }

    [Fact]
    public async Task When_labbytwo_cannot_see_it_says_so_and_nothing_turns_red()
    {
        var lab = await LabAsync("github", "gluetun", "cloudflare", "weather", "sonarr", "plex");
        await Get<HealthMonitor>().SweepOnceAsync();
        foreach (var name in new[] { "github", "gluetun", "cloudflare", "weather", "sonarr" })
            _lab.Failing[name] = ProbeFailure.Dns;
        await Get<HealthMonitor>().SweepOnceAsync();
        Assert.True(Get<HealthMonitor>().IsBlind);

        await OpenAsync();
        var html = Text(await SeeAsync("LabbyTwo can't see the lab right now"));

        Assert.Contains("DNS lookups are failing", html);
        Assert.Contains("this is not your services", html);
        Assert.Contains("All 6 fine", html);
        Assert.DoesNotContain("is down", html);
        Assert.Contains("can't check", html);
        Assert.Equal(6, lab.Count);
    }

    [Fact]
    public async Task Maintenance_starts_and_ends_with_one_tap_each()
    {
        await LabAsync("NAS");
        await Get<HealthMonitor>().SweepOnceAsync();
        await OpenAsync();
        await SeeAsync("Maintenance for 1 h");

        await Renderer.ClickAsync("Maintenance for 1 h");
        var html = Text(await SeeAsync("Alerts are silenced"));
        Assert.Contains("End maintenance", html);
        var window = Maintenance.From(await Get<AppSettingsStore>().AllAsync());
        Assert.True(window.On);
        Assert.InRange(window.Until!.Value - DateTimeOffset.Now, TimeSpan.FromMinutes(59), TimeSpan.FromMinutes(61));

        await Renderer.ClickAsync("End maintenance");
        await SeeAsync("Maintenance for 1 h");
        Assert.False(Maintenance.From(await Get<AppSettingsStore>().AllAsync()).On);
    }

    // ---- the buttons -------------------------------------------------------------------

    [Fact]
    public async Task A_dangerous_action_asks_first_and_a_silence_and_an_acknowledgement_do_what_they_say()
    {
        await Get<AlertService>().StartAsync(CancellationToken.None);
        var lab = await LabAsync("NAS", "Plex");
        await Get<HealthMonitor>().SweepOnceAsync();
        _lab.Failing["NAS"] = ProbeFailure.Refused;
        await Get<HealthMonitor>().SweepOnceAsync();
        Assert.NotNull(Get<AlertService>().Delivery(FiringAlert.StatusKey(lab[0].Id)));

        await OpenAsync();
        await SeeAsync("NAS is down");

        // Reboot is dangerous: the first tap only asks.
        await Renderer.ClickAsync("Reboot");
        await SeeAsync("Reboot it?");
        Assert.Empty(_lab.Ran);
        await Renderer.ClickAsync("Reboot", last: true);
        await SeeAsync("Reboot sent.");
        Assert.Equal(["NAS:reboot"], _lab.Ran);

        await Renderer.ClickAsync("I'm on it");
        await SeeAsync("You're on it");
        Assert.True(Get<AlertService>().IsAcknowledged(FiringAlert.StatusKey(lab[0].Id)));

        await Renderer.ClickAsync("Silence 1 h");
        await SeeAsync("Unsilence");
        var silenced = (await Get<ConfigStore>().ConnectionAsync(lab[0].Id))!.SilencedUntil;
        Assert.NotNull(silenced);
        Assert.InRange(silenced!.Value - DateTimeOffset.Now, TimeSpan.FromMinutes(59), TimeSpan.FromMinutes(61));
    }

    [Fact]
    public async Task A_family_report_is_listed_and_dismissed_from_the_phone()
    {
        await LabAsync("Plex");
        await Get<HealthMonitor>().SweepOnceAsync();
        await Get<FamilyReportStore>().AddAsync(new FamilyReport(0, DateTimeOffset.Now, "plex", null, "Plex", "It keeps buffering", "Sam"));

        await OpenAsync();
        await SeeAsync("Sam says Plex is broken");
        Assert.Contains("1 thing to look at", Text(await Renderer.HtmlAsync()));

        await Renderer.ClickAsync("Dismiss");
        await SeeAsync("Nothing needs you.");
        Assert.Equal(0, await Get<FamilyReportStore>().CountAsync());
    }

    [Fact]
    public async Task A_pinned_action_is_at_the_top_and_runs_from_there()
    {
        var lab = await LabAsync("PC");
        await Get<HealthMonitor>().SweepOnceAsync();
        await Get<AppSettingsStore>().SaveAsync(PhoneView.PinsKey,
            PhonePin.Store([new PhonePin(PhonePinKind.Action, lab[0].Id, "wake", "Wake the PC")]));

        await OpenAsync();
        await SeeAsync("Wake the PC");
        await Renderer.ClickAsync("Wake the PC");
        await SeeAsync("Wake sent.");
        Assert.Equal(["PC:wake"], _lab.Ran);
    }

    [Fact]
    public async Task I_am_on_it_holds_the_escalation_of_that_outage_and_not_the_next()
    {
        var alerts = Get<AlertService>();
        await alerts.StartAsync(CancellationToken.None);
        await Get<AppSettingsStore>().SaveAsync(EscalationPolicy.AfterKey, "1");
        var lab = await LabAsync("NAS");
        var key = FiringAlert.StatusKey(lab[0].Id);
        await Get<HealthMonitor>().SweepOnceAsync();
        _lab.Failing["NAS"] = ProbeFailure.Refused;
        await Get<HealthMonitor>().SweepOnceAsync();
        var sent = _channel.Sent.Count;

        Assert.True(alerts.Acknowledge(key));
        await alerts.FollowUpAsync(DateTimeOffset.Now.AddMinutes(10), CancellationToken.None);
        Assert.Equal(sent, _channel.Sent.Count);

        // Back up, then down again: a new outage nobody has said they are on.
        _lab.Failing.Clear();
        await Get<HealthMonitor>().SweepOnceAsync();
        _lab.Failing["NAS"] = ProbeFailure.Refused;
        await Get<HealthMonitor>().SweepOnceAsync();
        Assert.False(alerts.IsAcknowledged(key));
        var again = _channel.Sent.Count;
        await alerts.FollowUpAsync(DateTimeOffset.Now.AddMinutes(10), CancellationToken.None);
        Assert.Contains(_channel.Sent.Skip(again), a => a.Title.StartsWith("Still firing", StringComparison.Ordinal));
    }

    // ---- the promise -------------------------------------------------------------------

    [Fact]
    public async Task Once_the_caches_are_warm_opening_it_reads_nothing_from_the_database()
    {
        var lab = await LabAsync("NAS", "Plex", "Router");
        await Get<HealthMonitor>().SweepOnceAsync();
        _lab.Failing["NAS"] = ProbeFailure.Refused;
        await Get<HealthMonitor>().SweepOnceAsync();

        // The first visit after a start may read what is not in memory yet — the open
        // incidents, the report count, the backups before their first judgement.
        await OpenAsync();
        await SeeAsync("NAS is down");
        await Renderer.WaitForAsync(_ => Get<IncidentTracker>().Open is not null && Get<BackupProof>().Latest is not null
                                          && Get<FamilyReportStore>().KnownCount is not null);

        var before = Get<Db>().Opens;
        await using var second = new InteractiveRenderer(_services);
        await second.RenderAsync<PhonePage>(new Dictionary<string, object?>());
        var html = Text(await second.WaitForAsync("NAS is down"));
        await Task.Delay(300);

        Assert.Contains("1 down", html);
        Assert.Equal(before, Get<Db>().Opens);
        Assert.Equal(3, lab.Count);
    }
}
