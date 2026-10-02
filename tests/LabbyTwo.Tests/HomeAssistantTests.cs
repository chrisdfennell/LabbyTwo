using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Tests;

/// <summary>
/// The discovery plan on its own: which configs and states a lab turns into, with no
/// broker. Everything about the shape Home Assistant reads — topics, unique ids, device
/// grouping, units and classes — is decided here, so it is pinned here.
/// </summary>
public sealed class HomeAssistantPlanTests
{
    private static readonly DateTimeOffset Since = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    internal static HaSettings On(int port = 1883) => HaSettings.Off with { Enabled = true, Host = "127.0.0.1", Port = port };

    internal static HaConnectionView Conn(string id, string name, bool? up = true, IReadOnlyList<HaMetricView>? metrics = null,
        IReadOnlyList<ProviderAction>? actions = null) =>
        new(id, name, "QNAP NAS", up, Since, Since.AddMinutes(5), up == false ? "Connection refused" : "OK", false, false,
            metrics ?? [], actions ?? []);

    internal static HaLab Lab(params HaConnectionView[] connections) => HaLab.Empty with
    {
        Version = "1.12.0",
        Connections = connections,
        Summary = $"{connections.Count(c => c.IsUp == true)} up, {connections.Count(c => c.IsUp == false)} down",
        AnyDown = connections.Any(c => c.IsUp == false),
    };

    private static JsonElement Config(HaPlan plan, string objectId) =>
        JsonDocument.Parse(plan.Entities.Single(e => e.ObjectId == objectId).Config).RootElement;

    [Fact]
    public void AConnectionIsADeviceWithAConnectivitySensorUnderTheHub()
    {
        var plan = HaPlan.Build(On(), Lab(Conn("abc123", "NAS")));
        var entity = plan.Entities.Single(e => e.ObjectId == "c_abc123_status");

        Assert.Equal("binary_sensor", entity.Component);
        Assert.Equal("homeassistant/binary_sensor/labbytwo/c_abc123_status/config", entity.ConfigTopic);
        Assert.Equal("labbytwo_c_abc123_status", entity.UniqueId);

        var config = Config(plan, "c_abc123_status");
        Assert.Equal("labbytwo_c_abc123_status", config.GetProperty("unique_id").GetString());
        Assert.Equal("connectivity", config.GetProperty("device_class").GetString());
        Assert.Equal("labbytwo/conn/abc123/status", config.GetProperty("state_topic").GetString());
        Assert.Equal("labbytwo/conn/abc123/attributes", config.GetProperty("json_attributes_topic").GetString());
        Assert.Equal("labbytwo/status", config.GetProperty("availability_topic").GetString());

        var device = config.GetProperty("device");
        Assert.Equal("labbytwo_conn_abc123", device.GetProperty("identifiers")[0].GetString());
        Assert.Equal("NAS", device.GetProperty("name").GetString());
        Assert.Equal("LabbyTwo", device.GetProperty("manufacturer").GetString());
        Assert.Equal("QNAP NAS", device.GetProperty("model").GetString());
        Assert.Equal("labbytwo_hub", device.GetProperty("via_device").GetString());

        Assert.Equal("ON", plan.States["labbytwo/conn/abc123/status"]);
        var attributes = JsonDocument.Parse(plan.States["labbytwo/conn/abc123/attributes"]).RootElement;
        Assert.Equal("OK", attributes.GetProperty("message").GetString());
        Assert.False(attributes.GetProperty("maintenance").GetBoolean());
        Assert.False(attributes.GetProperty("silenced").GetBoolean());
    }

    [Fact]
    public void TheHubHasTheLabWideSensors()
    {
        var lab = Lab(Conn("a", "NAS"), Conn("b", "Plex", up: false)) with
        {
            AnyAlertFiring = true,
            Maintenance = new Maintenance(true, Since.AddHours(1)),
            BackupLate = true,
            OpenIncidents = 2,
        };
        var plan = HaPlan.Build(On(), lab);

        string[] hub = ["hub_summary", "hub_any_down", "hub_any_alert", "hub_maintenance", "hub_blind", "hub_backup_late", "hub_incident_open", "hub_incidents"];
        foreach (var objectId in hub)
        {
            var device = Config(plan, objectId).GetProperty("device");
            Assert.Equal("labbytwo_hub", device.GetProperty("identifiers")[0].GetString());
            Assert.Equal("LabbyTwo", device.GetProperty("name").GetString());
        }

        Assert.Equal("1 up, 1 down", plan.States["labbytwo/hub/summary"]);
        Assert.Equal("ON", plan.States["labbytwo/hub/any_down"]);
        Assert.Equal("ON", plan.States["labbytwo/hub/any_alert"]);
        Assert.Equal("ON", plan.States["labbytwo/hub/maintenance"]);
        Assert.Equal("OFF", plan.States["labbytwo/hub/blind"]);
        Assert.Equal("ON", plan.States["labbytwo/hub/backup_late"]);
        Assert.Equal("ON", plan.States["labbytwo/hub/incident_open"]);
        Assert.Equal("2", plan.States["labbytwo/hub/incidents"]);
        Assert.Equal("OFF", plan.States["labbytwo/conn/b/status"]);

        // No buttons unless asked for.
        Assert.DoesNotContain(plan.Entities, e => e.Component == "button");
    }

    [Fact]
    public void RenamingAConnectionKeepsItsIdsAndTopics()
    {
        var before = HaPlan.Build(On(), Lab(Conn("abc123", "NAS")));
        var after = HaPlan.Build(On(), Lab(Conn("abc123", "Big NAS")));

        Assert.Equal(before.Entities.Select(e => (e.UniqueId, e.ConfigTopic)), after.Entities.Select(e => (e.UniqueId, e.ConfigTopic)));
        Assert.Equal("Big NAS", Config(after, "c_abc123_status").GetProperty("device").GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("temp_c", "°C", "temperature", "°C", "measurement")]
    [InlineData("temp_f", "°F", "temperature", "°F", "measurement")]
    [InlineData("humidity", "%", "humidity", "%", "measurement")]
    [InlineData("battery_percent", "%", "battery", "%", "measurement")]
    [InlineData("cpu_percent", "%", null, "%", "measurement")]
    [InlineData("power_watts", " W", "power", "W", "measurement")]
    [InlineData("energy_kwh", " kWh", "energy", "kWh", "total_increasing")]
    [InlineData("download_mbps", " Mbps", "data_rate", "Mbit/s", "measurement")]
    [InlineData("latency_ms", " ms", "duration", "ms", "measurement")]
    [InlineData("uptime_days", " days", "duration", "d", "measurement")]
    [InlineData("used_gb", " GB", "data_size", "GB", "measurement")]
    [InlineData("pressure", " inHg", "pressure", "inHg", "measurement")]
    [InlineData("containers", "", null, null, "measurement")]
    public void MetricsCarryHomeAssistantsUnitsAndClasses(string key, string unit, string? deviceClass, string? haUnit, string stateClass)
    {
        var settings = On() with { Readings = HaReadings.All };
        var plan = HaPlan.Build(settings, Lab(Conn("n1", "NAS", metrics: [new(new MetricSpec(key, "Reading", unit, 1), 21.5)])));
        var config = Config(plan, $"c_n1_m_{key}");

        Assert.Equal(deviceClass, config.TryGetProperty("device_class", out var dc) ? dc.GetString() : null);
        Assert.Equal(haUnit, config.TryGetProperty("unit_of_measurement", out var u) ? u.GetString() : null);
        Assert.Equal(stateClass, config.GetProperty("state_class").GetString());
        Assert.Equal(1, config.GetProperty("suggested_display_precision").GetInt32());
        Assert.Equal("labbytwo_conn_n1", config.GetProperty("device").GetProperty("identifiers")[0].GetString());

        // In the stored unit: HA converts a temperature for display, LabbyTwo does not.
        Assert.Equal("21.5", plan.States[$"labbytwo/conn/n1/metric/{key}"]);
    }

    [Fact]
    public void AVolumeMetricIsMadeSafeForATopic()
    {
        var plan = HaPlan.Build(On() with { Readings = HaReadings.All },
            Lab(Conn("n1", "NAS", metrics: [new(new MetricSpec("disk_percent:vol2", "Volume 2", "%"), 40)])));
        Assert.Contains(plan.Entities, e => e.ObjectId == "c_n1_m_disk_percent_vol2");
        Assert.Equal("40", plan.States["labbytwo/conn/n1/metric/disk_percent_vol2"]);
    }

    [Fact]
    public void OnlyTheChosenConnectionsAndMetricsArePublished()
    {
        var temp = new HaMetricView(new MetricSpec("temp_c", "Temperature", "°C", 1), 40);
        var cpu = new HaMetricView(new MetricSpec("cpu_percent", "CPU", "%"), 12);
        var lab = Lab(Conn("a", "NAS", metrics: [temp, cpu]), Conn("b", "Plex", metrics: [temp]));

        var statusOnly = HaPlan.Build(On() with { AllConnections = false, Connections = new HashSet<string> { "a" } }, lab);
        Assert.Contains(statusOnly.Entities, e => e.ObjectId == "c_a_status");
        Assert.DoesNotContain(statusOnly.Entities, e => e.ObjectId.StartsWith("c_b_", StringComparison.Ordinal));
        Assert.DoesNotContain(statusOnly.Entities, e => e.ObjectId.Contains("_m_", StringComparison.Ordinal));

        var chosen = HaPlan.Build(On() with
        {
            Readings = HaReadings.Chosen,
            Metrics = new HashSet<string> { HaSettings.MetricChoice("a", "temp_c") },
        }, lab);
        Assert.Contains(chosen.Entities, e => e.ObjectId == "c_a_m_temp_c");
        Assert.DoesNotContain(chosen.Entities, e => e.ObjectId == "c_a_m_cpu_percent");
        Assert.DoesNotContain(chosen.Entities, e => e.ObjectId == "c_b_m_temp_c");
    }

    [Fact]
    public void ACheckingConnectionIsUnknownRatherThanAGuess()
    {
        var plan = HaPlan.Build(On(), Lab(Conn("a", "NAS", up: null)));
        Assert.Equal("None", plan.States["labbytwo/conn/a/status"]);
    }

    [Fact]
    public void ButtonsAppearOnlyWhenTickedAndPointAtSetTopics()
    {
        var wake = new ProviderAction("wake", "Wake on LAN");
        var lab = Lab(Conn("a", "NAS", actions: [wake]));

        Assert.DoesNotContain(HaPlan.Build(On(), lab).Entities, e => e.Component == "button");

        var plan = HaPlan.Build(On() with { MaintenanceButtons = true, ActionButtons = true }, lab);
        Assert.Equal("labbytwo/hub/maintenance_start/set", Config(plan, "hub_maintenance_start").GetProperty("command_topic").GetString());
        Assert.Equal("labbytwo/hub/maintenance_end/set", Config(plan, "hub_maintenance_end").GetProperty("command_topic").GetString());
        var button = Config(plan, "c_a_a_wake");
        Assert.Equal("labbytwo/conn/a/action/wake/set", button.GetProperty("command_topic").GetString());
        Assert.Equal("labbytwo_conn_a", button.GetProperty("device").GetProperty("identifiers")[0].GetString());
    }

    [Fact]
    public void OnlySafeQuestionFreeActionsMayBeButtons()
    {
        var nas = new Connection { Id = "a", Name = "NAS", Provider = "qnap" };
        Assert.Null(HaControls.Refusal(new ProviderAction("wake", "Wake on LAN"), nas));
        Assert.NotNull(HaControls.Refusal(new ProviderAction("shutdown", "Shut down") { Dangerous = true }, nas));
        Assert.NotNull(HaControls.Refusal(new ProviderAction("pause", "Pause") { Fields = [new FieldSpec("minutes", "Minutes")] }, nas));
        Assert.NotNull(HaControls.Refusal(new ProviderAction("cycle", "Power cycle") { Disrupts = TimeSpan.FromMinutes(1) }, nas));
        Assert.NotNull(HaControls.Refusal(new ProviderAction("restart", "Restart"), nas with { Provider = "docker" }));
    }

    [Fact]
    public void LeftoverConfigsAreKeptOnlyWhileTheyBelong()
    {
        var lab = Lab(Conn("a", "NAS"));
        var statusOnly = HaPlan.Build(On(), lab);
        Assert.True(statusOnly.Keeps("homeassistant/binary_sensor/labbytwo/c_a_status/config"));
        Assert.False(statusOnly.Keeps("homeassistant/binary_sensor/labbytwo/c_gone_status/config"));
        Assert.False(statusOnly.Keeps("homeassistant/sensor/labbytwo/c_a_m_temp_c/config"));

        // Every metric published: a metric not reported yet after a restart is kept, for a
        // connection that is still published — and only for one.
        var all = HaPlan.Build(On() with { Readings = HaReadings.All }, lab);
        Assert.True(all.Keeps("homeassistant/sensor/labbytwo/c_a_m_temp_c/config"));
        Assert.False(all.Keeps("homeassistant/sensor/labbytwo/c_gone_m_temp_c/config"));
    }

    [Fact]
    public void CommandsAreReadFromTheirTopics()
    {
        var s = On();
        Assert.Equal(HaCommandKind.MaintenanceStart, HaTopics.ParseCommand(s, "labbytwo/hub/maintenance_start/set")!.Kind);
        Assert.Equal(HaCommandKind.MaintenanceEnd, HaTopics.ParseCommand(s, "labbytwo/hub/maintenance_end/set")!.Kind);
        var action = HaTopics.ParseCommand(s, "labbytwo/conn/abc/action/wake/set")!;
        Assert.Equal((HaCommandKind.Action, "abc", "wake"), (action.Kind, action.ConnectionId, action.ActionId));
        Assert.Null(HaTopics.ParseCommand(s, "labbytwo/hub/summary"));
        Assert.Null(HaTopics.ParseCommand(s, "labbytwo/hub/reboot_everything/set"));
        Assert.Null(HaTopics.ParseCommand(s, "other/hub/maintenance_start/set"));
    }

    [Fact]
    public void BadTopicsAndAMissingBrokerAreSaidInWords()
    {
        Assert.Null(On().Problem());
        Assert.NotNull((On() with { Host = "" }).Problem());
        Assert.NotNull((On() with { BaseTopic = "lab/#" }).Problem());
        Assert.NotNull((On() with { DiscoveryPrefix = "home assistant" }).Problem());
        Assert.Equal("labbytwo", (On() with { BaseTopic = "/labbytwo/" }).Base);
        Assert.Equal("house_lab", (On() with { BaseTopic = "house/lab" }).Node);
    }

    [Fact]
    public void LastCheckedIsAddedAsSentButNotCompared()
    {
        var plan = HaPlan.Build(On(), Lab(Conn("a", "NAS")));
        const string topic = "labbytwo/conn/a/attributes";
        Assert.DoesNotContain("last_checked", plan.States[topic]);
        var sent = JsonDocument.Parse(HaPlan.WithLastChecked(plan.States[topic], plan.LastChecked[topic])).RootElement;
        Assert.Equal(Since.AddMinutes(5), sent.GetProperty("last_checked").GetDateTimeOffset());
    }

    [Fact]
    public void TheChangeFeedKnowsTheHomeAssistantKind()
    {
        Assert.Contains(ChangeKinds.All, k => k.Key == ChangeKinds.HomeAssistant);
        Assert.Equal(ChangeKinds.HomeAssistant, ChangeKinds.Parse("ha"));
    }
}
