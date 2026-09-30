using LabbyTwo.Core;
using LabbyTwo.Services;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LabbyTwo.Tests;

/// <summary>
/// Scheduled actions end to end: a real store, alert service, change feed and mute windows,
/// a recording alert channel, and a fake target that counts how often it was asked to
/// "restart plex" — and can be told to refuse, fail, or hang until released. Time is passed
/// in and the zone is UTC, so a Sunday at four costs no waiting.
/// </summary>
public sealed class ScheduledActionTests : IDisposable
{
    private readonly string _directory = TestHost.TempDirectory();
    private readonly RecordingChannel _channel = new();
    private readonly FakePlans _plans = new();
    private readonly ActionProvider _provider = new();
    private ServiceProvider _services;

    private Connection _docker = null!;
    private Connection _email = null!;
    private Connection _push = null!;

    /// <summary>Monday 2 February 2026, 00:00 UTC.</summary>
    private static readonly DateTimeOffset Midnight = DateTimeOffset.Parse("2026-02-02T00:00:00Z");

    private sealed class RecordingChannel : IAlertChannel
    {
        public string Type => "recording";
        public string DisplayName => "Recording channel";
        public string Icon => "📼";
        public string Description => "Test double.";
        public IReadOnlyList<FieldSpec> Fields => [];
        public List<(string Channel, Alert Alert)> Sent { get; } = [];

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
            Task.FromResult(ProbeResult.Up(TimeSpan.Zero));

        public Task SendAsync(Connection channel, Alert alert, CancellationToken ct)
        {
            lock (Sent)
                Sent.Add((channel.Name, alert));
            return Task.CompletedTask;
        }
    }

    /// <summary>A connection with three buttons: a harmless one, one that asks a question, and a dangerous one.</summary>
    private sealed class ActionProvider : IConnectionProvider
    {
        public string Type => "buttons";
        public string DisplayName => "Buttons";
        public string Icon => "🔘";
        public string Description => "Test double.";
        public IReadOnlyList<FieldSpec> Fields => [];

        public IReadOnlyList<ProviderAction> Actions =>
        [
            new("backup", "Run backup") { Confirms = false },
            new("disable", "Disable blocking") { Fields = [new FieldSpec("minutes", "Minutes", Required: true)] },
            new("shutdown", "Shut down") { Dangerous = true },
        ];

        public Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct) =>
            Task.FromResult(ProbeResult.Up(TimeSpan.Zero));
    }

    /// <summary>Restarts nothing; counts. Can refuse like a guardrail, fail like Docker, or hang until released.</summary>
    private sealed class FakePlans : IScheduledActionPlans
    {
        public List<string> Ran { get; } = [];
        public string? Refuse { get; set; }
        public bool Fail { get; set; }
        public string? SaveRefusal { get; set; }
        public TaskCompletionSource? Hold { get; set; }

        public Task<string> DescribeAsync(ScheduledAction action, CancellationToken ct) =>
            Task.FromResult($"restart {action.Container}");

        public Task<RemediationPlan> PrepareAsync(ScheduledAction action, CancellationToken ct)
        {
            var name = action.Container;
            if (Refuse is { } why)
                return Task.FromResult(RemediationPlan.Refuse($"restart {name}", $"Restarted {name}", why));
            return Task.FromResult(new RemediationPlan($"restart {name}", $"Restarted {name}", null, async _ =>
            {
                lock (Ran)
                    Ran.Add(name);
                if (Hold is { } hold)
                    await hold.Task;
                return Fail ? ActionResult.Failed("No such container: plex.") : ActionResult.Done($"Restarted {name}.");
            }));
        }

        public Task<string?> SaveProblemAsync(ScheduledAction action, CancellationToken ct) => Task.FromResult(SaveRefusal);

        public int Count
        {
            get
            {
                lock (Ran)
                    return Ran.Count;
            }
        }
    }

    public ScheduledActionTests()
    {
        _services = Build();
    }

    private ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddTestStorage(_directory);
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keys")));
        services.AddHttpClient();
        services.AddSingleton<IConnectionProvider>(_channel);
        services.AddSingleton<IConnectionProvider>(_provider);
        services.AddSingleton<IEnumerable<IWidgetType>>([]);
        services.AddSingleton<IEnumerable<ITabKind>>([]);
        services.AddSingleton<Registry>();
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<AlertRuleStore>();
        services.AddSingleton<HistoryStore>();
        services.AddSingleton<HealthMonitor>();
        services.AddSingleton<AlertService>();
        services.AddSingleton<ChangeStore>();
        services.AddSingleton<ActionRunner>();
        services.AddSingleton<RemediationActions>();
        services.AddSingleton<ScheduledActionPlans>();
        services.AddSingleton<ScheduledActionStore>();
        services.AddSingleton<IScheduledActionPlans>(_plans);
        services.AddSingleton<ScheduledActions>();
        var built = services.BuildServiceProvider();
        built.GetRequiredService<AlertService>().Zone = TimeZoneInfo.Utc;
        return built;
    }

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private ScheduledActions Scheduled => Get<ScheduledActions>();

    public void Dispose() => TestHost.Teardown(_services, _directory);

    /// <summary>Everything in memory gone, the database kept — LabbyTwo stopped and started again.</summary>
    private async Task RestartAsync()
    {
        var connectionString = Get<Db>().ConnectionString;
        await _services.DisposeAsync();
        using (var pooled = new SqliteConnection(connectionString))
            SqliteConnection.ClearPool(pooled);
        _services = Build();
    }

    private async Task SetUpConnectionsAsync()
    {
        _docker = new Connection { Provider = "buttons", Name = "Docker" };
        _email = new Connection { Provider = "recording", Name = "Email" };
        _push = new Connection { Provider = "recording", Name = "Push" };
        await Get<ConfigStore>().SaveConnectionAsync(_docker);
        await Get<ConfigStore>().SaveConnectionAsync(_email);
        await Get<ConfigStore>().SaveConnectionAsync(_push);
    }

    /// <summary>"Restart plex every day at 04:00", saved at <paramref name="savedAt"/>.</summary>
    private async Task<ScheduledAction> SaveAsync(Func<ScheduledAction, ScheduledAction>? change = null, DateTimeOffset? savedAt = null)
    {
        if (_docker is null)
            await SetUpConnectionsAsync();
        var action = new ScheduledAction
        {
            Name = "Restart plex",
            TargetConnectionId = _docker!.Id,
            Container = "plex",
            Schedule = new ActionSchedule { Kind = ScheduleKind.Weekly, Days = MuteWindow.Week, Times = [new TimeOnly(4, 0)] },
        };
        action = change?.Invoke(action) ?? action;
        await Scheduled.SaveAsync(action, savedAt ?? Midnight);
        return action;
    }

    private async Task TickAsync(DateTimeOffset at)
    {
        await Scheduled.TickAsync(at, CancellationToken.None);
        await Scheduled.WhenIdleAsync();
    }

    private async Task<IReadOnlyList<Change>> FeedAsync() =>
        // Wide open: "Run now" records at the real time, not the test's.
        (await Get<ChangeStore>().QueryAsync(new ChangeQuery(DateTimeOffset.UnixEpoch, new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero), [ChangeKinds.Scheduled])))
            .OrderBy(c => c.Id).ToList();

    private Task<IReadOnlyList<ScheduledRun>> HistoryAsync(ScheduledAction action) => Get<ScheduledActionStore>().RunsAsync(action.Id);

    private List<(string Channel, Alert Alert)> Sent
    {
        get
        {
            lock (_channel.Sent)
                return [.. _channel.Sent];
        }
    }

    // ---------- Running on time ----------

    [Fact]
    public async Task ItRunsWhenDueOnceAndSaysSoInTheFeedAndTheHistory()
    {
        var action = await SaveAsync();

        await TickAsync(Midnight.AddHours(3).AddMinutes(59));
        Assert.Equal(0, _plans.Count);

        await TickAsync(Midnight.AddHours(4));
        await TickAsync(Midnight.AddHours(4).AddMinutes(1));
        await TickAsync(Midnight.AddHours(4).AddMinutes(2));
        Assert.Equal(1, _plans.Count);

        var change = Assert.Single(await FeedAsync());
        Assert.Equal(ChangeActions.Completed, change.Action);
        Assert.Equal(_docker.Id, change.ConnectionId);
        Assert.Equal(action.Id, change.Subject);
        Assert.Equal("Restarted plex on schedule", change.Title);

        var run = Assert.Single(await HistoryAsync(action));
        Assert.Equal(ScheduledOutcomes.Ok, run.Outcome);
        Assert.Equal(ScheduledTriggers.Schedule, run.Trigger);

        // And again the next day, and only then.
        await TickAsync(Midnight.AddDays(1).AddHours(3));
        Assert.Equal(1, _plans.Count);
        await TickAsync(Midnight.AddDays(1).AddHours(4));
        Assert.Equal(2, _plans.Count);
    }

    [Fact]
    public async Task AQuietMinuteDoesNotTouchTheDatabase()
    {
        await SaveAsync();
        await TickAsync(Midnight.AddHours(1));

        // Take the tables away. A tick that read anything would now throw.
        await using (var connection = await Get<Db>().OpenAsync())
        {
            var drop = connection.CreateCommand();
            drop.CommandText = "DROP TABLE scheduled_runs; DROP TABLE scheduled_actions; DROP TABLE app_settings;";
            await drop.ExecuteNonQueryAsync();
        }

        await TickAsync(Midnight.AddHours(2));
        await TickAsync(Midnight.AddHours(3).AddMinutes(59));
        Assert.Equal(0, _plans.Count);
    }

    [Fact]
    public async Task EditingAnActionStartsItsScheduleFromTheEdit()
    {
        // Saved at 04:30, for 04:00: the 04:00 that has just gone is not "missed".
        await SaveAsync(savedAt: Midnight.AddHours(4).AddMinutes(30));
        await TickAsync(Midnight.AddHours(4).AddMinutes(31));
        Assert.Equal(0, _plans.Count);
        Assert.Empty(await FeedAsync());
    }

    // ---------- While LabbyTwo was down ----------

    [Fact]
    public async Task AMissedRunWithinTheGraceRunsOnceWhenLabbyTwoComesBack()
    {
        var action = await SaveAsync();
        await TickAsync(Midnight.AddHours(1));

        // Down from 01:00 Monday to 04:30 Wednesday: two 04:00s missed, one within the hour.
        await RestartAsync();
        await TickAsync(Midnight.AddDays(2).AddHours(4).AddMinutes(30));
        await TickAsync(Midnight.AddDays(2).AddHours(4).AddMinutes(31));

        Assert.Equal(1, _plans.Count);
        var run = Assert.Single(await HistoryAsync(action));
        Assert.Equal(ScheduledTriggers.CatchUp, run.Trigger);
        Assert.Contains("catching up", Assert.Single(await FeedAsync()).Title);
    }

    [Fact]
    public async Task AMissedRunOutsideTheGraceIsRecordedAndNotRun()
    {
        var action = await SaveAsync();

        await RestartAsync();
        await TickAsync(Midnight.AddHours(6));
        Assert.Equal(0, _plans.Count);

        var missed = Assert.Single(await HistoryAsync(action));
        Assert.Equal(ScheduledOutcomes.Skipped, missed.Outcome);
        Assert.Contains("too late", missed.Message);
        Assert.Equal(ChangeActions.Skipped, Assert.Single(await FeedAsync()).Action);

        // Recorded once, not every minute after, and the next day runs as normal.
        await TickAsync(Midnight.AddHours(7));
        Assert.Single(await HistoryAsync(action));
        await TickAsync(Midnight.AddDays(1).AddHours(4));
        Assert.Equal(1, _plans.Count);
    }

    [Fact]
    public async Task TheGracePeriodIsASetting()
    {
        await Get<AppSettingsStore>().SaveAsync(new ScheduledSettings(true, 180).ToSettings());
        await SaveAsync();

        await RestartAsync();
        await TickAsync(Midnight.AddHours(6));
        Assert.Equal(1, _plans.Count);
    }

    [Fact]
    public async Task ARunInProgressWhenLabbyTwoStopsIsNotRunAgain()
    {
        await SaveAsync();
        _plans.Hold = new TaskCompletionSource();
        await Scheduled.TickAsync(Midnight.AddHours(4), CancellationToken.None);

        // Stopped half way through the restart.
        _plans.Hold.SetResult();
        await Scheduled.WhenIdleAsync();
        await RestartAsync();
        await TickAsync(Midnight.AddHours(4).AddMinutes(2));

        Assert.Equal(1, _plans.Count);
    }

    // ---------- Never twice at once ----------

    [Fact]
    public async Task ARunStillGoingMakesTheNextOneASkipNotASecondRun()
    {
        var action = await SaveAsync(a => a with { Schedule = new ActionSchedule { Kind = ScheduleKind.Interval, IntervalMinutes = 5 } });
        _plans.Hold = new TaskCompletionSource();

        await Scheduled.TickAsync(Midnight.AddMinutes(5), CancellationToken.None);
        await Scheduled.TickAsync(Midnight.AddMinutes(10), CancellationToken.None);
        Assert.True(Scheduled.IsRunning(action.Id));

        var pressed = await Scheduled.RunNowAsync(action.Id);
        Assert.Equal(ScheduledOutcomes.Skipped, pressed.Outcome);
        Assert.Contains("already running", pressed.Message);

        _plans.Hold.SetResult();
        await Scheduled.WhenIdleAsync();

        Assert.Equal(1, _plans.Count);
        var history = await HistoryAsync(action);
        Assert.Equal([ScheduledOutcomes.Ok, ScheduledOutcomes.Skipped], history.Select(r => r.Outcome));
        Assert.Contains("still going", history[1].Message);
    }

    // ---------- Guardrails ----------

    [Fact]
    public async Task MaintenanceHoldsItUnlessItOptsIn()
    {
        var held = await SaveAsync();
        await Get<AppSettingsStore>().SaveAsync(Maintenance.Key, Maintenance.Value(TimeSpan.FromHours(2), Midnight.AddHours(3)));

        await TickAsync(Midnight.AddHours(4));
        Assert.Equal(0, _plans.Count);
        var skip = Assert.Single(await HistoryAsync(held));
        Assert.Equal(ScheduledOutcomes.Skipped, skip.Outcome);
        Assert.Contains("Held back", skip.Message);

        await SaveAsync(a => a with { Id = held.Id, RunInMaintenance = true }, Midnight.AddHours(4).AddMinutes(1));
        await TickAsync(Midnight.AddDays(1).AddHours(4).AddMinutes(-10));
        await Get<AppSettingsStore>().SaveAsync(Maintenance.Key, Maintenance.Value(TimeSpan.FromHours(2), Midnight.AddDays(1).AddHours(3)));
        await TickAsync(Midnight.AddDays(1).AddHours(4));
        Assert.Equal(1, _plans.Count);
    }

    [Fact]
    public async Task ARefusalIsAFailureThatIsRecordedAndSent()
    {
        await SetUpConnectionsAsync();
        var action = await SaveAsync();
        _plans.Refuse = "“labbytwo” is the container LabbyTwo itself runs in, which it never restarts by itself.";

        await TickAsync(Midnight.AddHours(4));

        Assert.Equal(0, _plans.Count);
        var run = Assert.Single(await HistoryAsync(action));
        Assert.Equal(ScheduledOutcomes.Failed, run.Outcome);
        Assert.Contains("never restarts", run.Message);

        var change = Assert.Single(await FeedAsync());
        Assert.Equal(ChangeActions.Failed, change.Action);
        Assert.Equal("Could not restart plex on schedule", change.Title);

        // Every channel, since the action names none.
        Assert.Equal(2, Sent.Count);
        Assert.All(Sent, s => Assert.StartsWith("Scheduled action failed · Restart plex", s.Alert.Title));
        Assert.Contains("never restarts", Sent[0].Alert.Body);
    }

    [Fact]
    public async Task AFailureGoesToTheChosenChannelOnly()
    {
        await SetUpConnectionsAsync();
        await SaveAsync(a => a with { ChannelId = _push.Id });
        _plans.Fail = true;

        await TickAsync(Midnight.AddHours(4));

        Assert.Equal(1, _plans.Count);
        var sent = Assert.Single(Sent);
        Assert.Equal("Push", sent.Channel);
        Assert.Equal(AlertLevel.Info, sent.Alert.Level);
        Assert.Contains("No such container", sent.Alert.Body);
    }

    [Fact]
    public async Task AFailureIsNotSentWhenTheActionDoesNotAskOrAMuteWindowOrQuietHoursHoldIt()
    {
        await SetUpConnectionsAsync();
        _plans.Fail = true;

        // Does not ask.
        await SaveAsync(a => a with { NotifyOnFailure = false });
        await TickAsync(Midnight.AddHours(4));
        Assert.Empty(Sent);

        // Muted: a window over the connection it acts on, 01:00–05:00.
        await Get<ScheduledActionStore>().DeleteAsync((await Get<ScheduledActionStore>().AllAsync())[0].Id);
        await SaveAsync(savedAt: Midnight.AddDays(1));
        await Get<MuteWindowStore>().SaveAsync(new MuteWindow
        {
            Name = "Night work",
            Days = MuteWindow.Week,
            Start = new TimeOnly(1, 0),
            End = new TimeOnly(5, 0),
            Scope = MuteScope.Connections,
            Targets = [_docker.Id],
        });
        await TickAsync(Midnight.AddDays(1).AddHours(4));
        Assert.Empty(Sent);

        // Quiet hours hold it too: a failed restart is news for the morning, not a reason to wake.
        foreach (var window in await Get<MuteWindowStore>().AllAsync())
            await Get<MuteWindowStore>().DeleteAsync(window.Id);
        await Get<AppSettingsStore>().SaveAsync(new Dictionary<string, string>
        {
            [AlertPolicy.FromKey] = "23:00", [AlertPolicy.ToKey] = "07:00", [AlertPolicy.ModeKey] = AlertPolicy.DownOnly,
        });
        await TickAsync(Midnight.AddDays(2).AddHours(4));
        Assert.Empty(Sent);

        // Every one of them is still in the feed.
        Assert.Equal(3, (await FeedAsync()).Count(c => c.Action == ChangeActions.Failed));
    }

    [Fact]
    public async Task SwitchedOffNothingRunsAndSwitchingBackOnRunsNoBacklog()
    {
        await SaveAsync();
        await Get<AppSettingsStore>().SaveAsync(new ScheduledSettings(false, 60).ToSettings());

        await TickAsync(Midnight.AddHours(4));
        Assert.Equal(0, _plans.Count);
        Assert.Empty(await FeedAsync());

        await Get<AppSettingsStore>().SaveAsync(new ScheduledSettings(true, 60).ToSettings());
        await TickAsync(Midnight.AddHours(4).AddMinutes(10));
        Assert.Equal(0, _plans.Count);
        await TickAsync(Midnight.AddDays(1).AddHours(4));
        Assert.Equal(1, _plans.Count);
    }

    [Fact]
    public async Task RunNowIgnoresMaintenanceAndLeavesTheScheduleAlone()
    {
        var action = await SaveAsync();
        await Get<AppSettingsStore>().SaveAsync(Maintenance.Key, Maintenance.Indefinite);

        var run = await Scheduled.RunNowAsync(action.Id);
        Assert.True(run.Ok);
        Assert.Equal(ScheduledTriggers.Manual, run.Trigger);
        Assert.Equal(1, _plans.Count);
        Assert.EndsWith("(Run now)", Assert.Single(await FeedAsync()).Title);

        // A manual failure is on the screen of the person who pressed it; it is not sent.
        _plans.Fail = true;
        Assert.False((await Scheduled.RunNowAsync(action.Id)).Ok);
        Assert.Empty(Sent);

        // The 04:00 still comes when it was going to.
        await Get<AppSettingsStore>().SaveAsync(Maintenance.Key, Maintenance.Cleared);
        _plans.Fail = false;
        await TickAsync(Midnight.AddHours(4));
        Assert.Equal(3, _plans.Count);
    }

    [Fact]
    public async Task WhatCannotBeSavedSaysWhy()
    {
        await SetUpConnectionsAsync();
        var problem = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SaveAsync(a => a with { Schedule = new ActionSchedule { Kind = ScheduleKind.Interval, IntervalMinutes = 1 } }));
        Assert.Contains("too often", problem.Message);

        _plans.SaveRefusal = "“Disable blocking” on Pi-hole asks for something before it runs, and nobody is there to answer.";
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => SaveAsync());
        Assert.Contains("nobody is there", refused.Message);
        Assert.Empty(await Get<ScheduledActionStore>().AllAsync());
    }

    /// <summary>The real preparation, for the guardrails it borrows from self-healing.</summary>
    [Fact]
    public async Task ActionsThatAskOrAreDangerousCannotBeScheduledWithoutTheOptIn()
    {
        await SetUpConnectionsAsync();
        var plans = Get<ScheduledActionPlans>();
        ScheduledAction Button(string id, bool optIn = false) => new()
        {
            Name = "Button",
            Target = ScheduledTarget.ProviderAction,
            TargetConnectionId = _docker.Id,
            ActionId = id,
            AllowProtected = optIn,
            Schedule = new ActionSchedule { Kind = ScheduleKind.Interval, IntervalMinutes = 60 },
        };

        Assert.Null(await plans.SaveProblemAsync(Button("backup"), CancellationToken.None));
        Assert.Contains("asks for something", await plans.SaveProblemAsync(Button("disable", optIn: true), CancellationToken.None));
        Assert.Contains("dangerous", await plans.SaveProblemAsync(Button("shutdown"), CancellationToken.None));
        Assert.Null(await plans.SaveProblemAsync(Button("shutdown", optIn: true), CancellationToken.None));

        // And at run time, the same refusal from the same code.
        var plan = await plans.PrepareAsync(Button("shutdown"), CancellationToken.None);
        Assert.Null(plan.Run);
        Assert.Contains("dangerous", plan.Refused);
    }

    [Fact]
    public async Task AContainerOnAConnectionThatIsNotDockerIsRefused()
    {
        await SetUpConnectionsAsync();
        var plan = await Get<ScheduledActionPlans>().PrepareAsync(new ScheduledAction
        {
            Name = "Stop plex",
            TargetConnectionId = _docker.Id,
            Container = "plex",
            Verb = ContainerVerb.Stop,
        }, CancellationToken.None);

        Assert.Null(plan.Run);
        Assert.Equal("stop plex", plan.Doing);
        Assert.Contains("no longer exists", plan.Refused);
    }

    [Fact]
    public void LabbyTwosOwnContainerIsNeverStoppedAndProtectedOnesNeedTheOptIn()
    {
        var self = new ContainerRow("abcdef012345" + new string('0', 52), "labbytwo", "img", "sha256:1", "running", "Up",
            DateTimeOffset.UnixEpoch, [], new Dictionary<string, string>());
        var tunnel = new ContainerRow("123456789abc" + new string('0', 52), "cloudflared", "img", "sha256:1", "running", "Up",
            DateTimeOffset.UnixEpoch, [], new Dictionary<string, string>());

        var own = RemediationGuards.ContainerRefusal(self, "abcdef012345", [], true, "stops", "stopped", "this scheduled action");
        Assert.Contains("never stops by itself", own);

        var guarded = RemediationGuards.ContainerRefusal(tunnel, "elsewhere", ["cloudflared"], false, "stops", "stopped", "this scheduled action");
        Assert.Contains("on this scheduled action if you really want it stopped automatically", guarded);
        Assert.Null(RemediationGuards.ContainerRefusal(tunnel, "elsewhere", ["cloudflared"], true, "stops", "stopped", "this scheduled action"));
    }

    // ---------- History ----------

    [Fact]
    public async Task EachActionKeepsItsLastTwentyRuns()
    {
        var store = Get<ScheduledActionStore>();
        for (var i = 0; i < 25; i++)
            await store.RecordRunAsync(new ScheduledRun("a", Midnight.AddMinutes(i), ScheduledTriggers.Schedule, ScheduledOutcomes.Ok, $"run {i}", TimeSpan.Zero));
        await store.RecordRunAsync(new ScheduledRun("b", Midnight, ScheduledTriggers.Schedule, ScheduledOutcomes.Ok, "other", TimeSpan.Zero));

        var runs = await store.RunsAsync("a");
        Assert.Equal(ScheduledActionStore.HistoryLength, runs.Count);
        Assert.Equal("run 24", runs[0].Message);
        Assert.Equal("run 5", runs[^1].Message);
        Assert.Single(await store.RunsAsync("b"));
    }

    // ---------- Export ----------

    [Fact]
    public async Task ScheduledActionsTravelInABackupAndStartAfreshWhenRestored()
    {
        var action = await SaveAsync(a => a with
        {
            Schedule = new ActionSchedule { Kind = ScheduleKind.Cron, Cron = "0 4 * * 0" },
            Verb = ContainerVerb.Stop,
            RunInMaintenance = true,
            AllowProtected = true,
            NotifyOnFailure = false,
            ChannelId = _push.Id,
        });
        await TickAsync(Midnight.AddHours(1));
        await Get<ScheduledActionStore>().RecordRunAsync(new ScheduledRun(action.Id, Midnight, ScheduledTriggers.Manual, ScheduledOutcomes.Ok, "", TimeSpan.Zero));

        var transfer = new ConfigTransfer(Get<ConfigStore>(), Get<AlertRuleStore>(), Get<Registry>(), null, null, Get<ScheduledActionStore>());
        var json = await transfer.ExportAsync(includeSecrets: false);
        Assert.Contains("0 4 * * 0", json);

        await Get<ScheduledActionStore>().DeleteAsync(action.Id);
        var before = DateTimeOffset.Now.AddSeconds(-1);
        var result = await transfer.ImportAsync(json);

        Assert.Equal(1, result.ScheduledActions);
        var restored = Assert.Single(await Get<ScheduledActionStore>().AllAsync());
        Assert.Equal(action with { CoveredUntil = restored.CoveredUntil }, restored with { Schedule = action.Schedule });
        Assert.Equal(ScheduleKind.Cron, restored.Schedule.Kind);
        Assert.Equal("0 4 * * 0", restored.Schedule.Cron);
        Assert.True(restored.CoveredUntil >= before);
        Assert.Empty(await HistoryAsync(action));
    }

    [Fact]
    public async Task OneWhoseConnectionIsMissingArrivesSwitchedOff()
    {
        var action = await SaveAsync();
        var transfer = new ConfigTransfer(Get<ConfigStore>(), Get<AlertRuleStore>(), Get<Registry>(), null, null, Get<ScheduledActionStore>());
        var json = await transfer.ExportAsync(includeSecrets: false);

        await Get<ScheduledActionStore>().DeleteAsync(action.Id);
        await Get<ConfigStore>().DeleteConnectionAsync(_docker.Id);
        var trimmed = json.Replace($"\"id\": \"{_docker.Id}\"", "\"id\": \"gone\"");
        var result = await transfer.ImportAsync(trimmed);

        Assert.Contains(result.Warnings, w => w.Contains("switched off"));
        Assert.False(Assert.Single(await Get<ScheduledActionStore>().AllAsync()).Enabled);
    }
}
