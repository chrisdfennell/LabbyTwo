using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LabbyTwo.Core;

/// <summary>Which readings go to Home Assistant besides each connection's up or down.</summary>
public enum HaReadings
{
    /// <summary>Up or down only — one binary sensor per connection, and the lab-wide ones.</summary>
    Status,

    /// <summary>Up or down, plus the metrics ticked one by one on the Settings page.</summary>
    Chosen,

    /// <summary>Up or down, plus every metric every published connection reports.</summary>
    All,
}

/// <summary>
/// What was chosen on the Settings page, with the password decrypted. Nothing is sent
/// anywhere while <see cref="Enabled"/> is false — LabbyTwo does not phone anywhere it was
/// not asked to, the house's own broker included.
/// </summary>
/// <param name="Connections">The ids published when <see cref="AllConnections"/> is off.</param>
/// <param name="Metrics">"connectionId|metricKey" for each metric ticked, read in <see cref="HaReadings.Chosen"/>.</param>
/// <param name="MaintenanceButtons">Offer "Start maintenance (1 hour)" and "End maintenance" as buttons.</param>
/// <param name="ActionButtons">Offer each connection's safe, question-free actions as buttons.</param>
public sealed record HaSettings(
    bool Enabled,
    string Host,
    int Port,
    bool Tls,
    string Username,
    string Password,
    string DiscoveryPrefix,
    string BaseTopic,
    bool AllConnections,
    IReadOnlySet<string> Connections,
    HaReadings Readings,
    IReadOnlySet<string> Metrics,
    bool MaintenanceButtons,
    bool ActionButtons,
    int RefreshMinutes)
{
    public const string DefaultDiscoveryPrefix = "homeassistant";
    public const string DefaultBaseTopic = "labbytwo";
    public const int DefaultPort = 1883;
    public const int DefaultRefreshMinutes = 10;

    public static HaSettings Off => new(false, "", DefaultPort, false, "", "", DefaultDiscoveryPrefix, DefaultBaseTopic,
        true, new HashSet<string>(), HaReadings.Status, new HashSet<string>(), false, false, DefaultRefreshMinutes);

    /// <summary>On, and with somewhere to send to. Enabled with no broker address is still off.</summary>
    public bool Active => Enabled && Host.Trim().Length > 0;

    public string Prefix => HaTopics.CleanTopic(DiscoveryPrefix, DefaultDiscoveryPrefix);

    public string Base => HaTopics.CleanTopic(BaseTopic, DefaultBaseTopic);

    /// <summary>
    /// Every published entity's ids start with this: the base topic made safe for an id. Two
    /// LabbyTwo installs on one broker therefore stay apart by giving each its own base topic.
    /// </summary>
    public string Node => HaTopics.Id(Base);

    public TimeSpan Refresh => TimeSpan.FromMinutes(Math.Clamp(RefreshMinutes, 1, 1440));

    public bool AnyControls => MaintenanceButtons || ActionButtons;

    /// <summary>Whether this connection is one of those published.</summary>
    public bool Publishes(string connectionId) => AllConnections || Connections.Contains(connectionId);

    /// <summary>Whether this metric goes to Home Assistant, given the connection is published at all.</summary>
    public bool PublishesMetric(string connectionId, string key) => Readings switch
    {
        HaReadings.All => true,
        HaReadings.Chosen => Metrics.Contains(MetricChoice(connectionId, key)),
        _ => false,
    };

    /// <summary>How a ticked metric is stored: "connectionId|metricKey".</summary>
    public static string MetricChoice(string connectionId, string key) => $"{connectionId}|{key}";

    /// <summary>
    /// Everything that makes the existing broker session the wrong one. The password counts
    /// by length only, so a rotated password still reconnects without a credential sitting in
    /// a string that might be logged or compared somewhere else.
    /// </summary>
    public string Fingerprint => string.Join('|', Host.Trim(), Port, Tls, Username, Password.Length, Prefix, Base, MaintenanceButtons, ActionButtons);

    /// <summary>Why the topics cannot be used, in words, or null when they can.</summary>
    public string? Problem()
    {
        if (Enabled && Host.Trim().Length == 0)
            return "Give the address of your MQTT broker — on Home Assistant OS that is usually the Home Assistant machine itself.";
        if (HaTopics.TopicProblem(DiscoveryPrefix, "discovery prefix") is { } prefix)
            return prefix;
        if (HaTopics.TopicProblem(BaseTopic, "base topic") is { } baseTopic)
            return baseTopic;
        if (Port is < 1 or > 65535)
            return "The port has to be between 1 and 65535 — 1883 for plain MQTT, 8883 for TLS.";
        return null;
    }
}

/// <summary>
/// The topics, in one place, so the publisher, the command handler and the tests cannot
/// disagree about where anything lives.
///
/// <list type="bullet">
/// <item><c>&lt;base&gt;/status</c> — online or offline: the birth message, and the will the broker sends when LabbyTwo vanishes.</item>
/// <item><c>&lt;base&gt;/conn/&lt;id&gt;/status</c>, <c>…/attributes</c>, <c>…/metric/&lt;key&gt;</c> — one connection.</item>
/// <item><c>&lt;base&gt;/hub/&lt;name&gt;</c> — the lab as a whole.</item>
/// <item><c>…/set</c> — a button pressed in Home Assistant.</item>
/// </list>
///
/// Connections are addressed by id, never by name, so renaming the NAS changes the name
/// Home Assistant shows and nothing else — not the topic, not the unique id, not the entity.
/// </summary>
public static class HaTopics
{
    public const string Online = "online";
    public const string Offline = "offline";
    public const string On = "ON";
    public const string Off = "OFF";
    public const string Press = "PRESS";

    public const string MaintenanceStart = "maintenance_start";
    public const string MaintenanceEnd = "maintenance_end";

    public static string Availability(HaSettings s) => $"{s.Base}/status";
    public static string ConnectionStatus(HaSettings s, string connectionId) => $"{s.Base}/conn/{Id(connectionId)}/status";
    public static string ConnectionAttributes(HaSettings s, string connectionId) => $"{s.Base}/conn/{Id(connectionId)}/attributes";
    public static string Metric(HaSettings s, string connectionId, string key) => $"{s.Base}/conn/{Id(connectionId)}/metric/{Id(key)}";
    public static string Hub(HaSettings s, string name) => $"{s.Base}/hub/{name}";
    public static string HubCommand(HaSettings s, string button) => $"{s.Base}/hub/{button}/set";
    public static string ActionCommand(HaSettings s, string connectionId, string actionId) => $"{s.Base}/conn/{Id(connectionId)}/action/{Id(actionId)}/set";

    /// <summary>Where Home Assistant says it has (re)started. Subscribed to so a restarted HA gets every entity again.</summary>
    public static string HomeAssistantStatus(HaSettings s) => $"{s.Prefix}/status";

    /// <summary>The discovery config for one entity: <c>&lt;prefix&gt;/&lt;component&gt;/&lt;node&gt;/&lt;object&gt;/config</c>.</summary>
    public static string Config(HaSettings s, string component, string objectId) => $"{s.Prefix}/{component}/{s.Node}/{objectId}/config";

    /// <summary>Every config this install could have published — subscribed to so the leftovers of a connection deleted while LabbyTwo was off are found and cleared.</summary>
    public static string OwnConfigs(HaSettings s) => $"{s.Prefix}/+/{s.Node}/+/config";

    /// <summary>The command filters, subscribed to only while a control is switched on.</summary>
    public static IReadOnlyList<string> CommandFilters(HaSettings s) => [$"{s.Base}/hub/+/set", $"{s.Base}/conn/+/action/+/set"];

    /// <summary>
    /// A string made safe for a topic level and an object id: letters, digits, underscore
    /// and hyphen kept, everything else an underscore. Connection ids are already like this;
    /// metric keys with a volume in them (<c>disk_percent:vol2</c>) are not.
    /// </summary>
    public static string Id(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        return builder.Length == 0 ? "_" : builder.ToString();
    }

    /// <summary>A topic as typed, trimmed of slashes at either end, or the default when blank.</summary>
    public static string CleanTopic(string? written, string fallback)
    {
        var trimmed = (written ?? "").Trim().Trim('/');
        return trimmed.Length == 0 ? fallback : trimmed;
    }

    /// <summary>Why a topic someone typed cannot be used, or null. Wildcards and spaces are the usual culprits.</summary>
    public static string? TopicProblem(string? written, string what)
    {
        var value = (written ?? "").Trim().Trim('/');
        if (value.Length == 0)
            return null;
        if (value.Any(c => c is '+' or '#' or '$' || char.IsWhiteSpace(c) || char.IsControl(c)))
            return $"The {what} cannot contain spaces, + , # or $ — those mean something else to MQTT.";
        if (value.Contains("//", StringComparison.Ordinal))
            return $"The {what} has an empty level in it (two slashes together).";
        return null;
    }

    /// <summary>What a message on a command topic asks for, or null for a topic that is not a command.</summary>
    public static HaCommand? ParseCommand(HaSettings s, string topic)
    {
        var prefix = s.Base + "/";
        if (!topic.StartsWith(prefix, StringComparison.Ordinal) || !topic.EndsWith("/set", StringComparison.Ordinal))
            return null;
        var parts = topic[prefix.Length..].Split('/');
        if (parts is ["hub", var button, "set"] && button is MaintenanceStart or MaintenanceEnd)
            return new HaCommand(button == MaintenanceStart ? HaCommandKind.MaintenanceStart : HaCommandKind.MaintenanceEnd, null, null);
        if (parts is ["conn", var connection, "action", var action, "set"] && connection.Length > 0 && action.Length > 0)
            return new HaCommand(HaCommandKind.Action, connection, action);
        return null;
    }
}

public enum HaCommandKind
{
    MaintenanceStart,
    MaintenanceEnd,
    Action,
}

/// <param name="ConnectionId">As it appeared in the topic — already made safe by <see cref="HaTopics.Id"/>.</param>
/// <param name="ActionId">As it appeared in the topic — already made safe by <see cref="HaTopics.Id"/>.</param>
public sealed record HaCommand(HaCommandKind Kind, string? ConnectionId, string? ActionId)
{
    /// <summary>One word per button, for the change feed's subject and the repeat guard.</summary>
    public string Key => Kind switch
    {
        HaCommandKind.MaintenanceStart => HaTopics.MaintenanceStart,
        HaCommandKind.MaintenanceEnd => HaTopics.MaintenanceEnd,
        _ => $"{ConnectionId}/{ActionId}",
    };
}

/// <summary>
/// The guardrails for exposing a provider action as a Home Assistant button. Stricter than
/// self-healing's, because a button in Home Assistant can be pressed by an automation
/// nobody here wrote, a voice assistant that misheard, or anyone with a login to HA:
/// <list type="bullet">
/// <item>Nothing dangerous — no opt-in unlocks it, unlike self-healing's.</item>
/// <item>Nothing that asks a question first, required or not: there is nowhere to answer.</item>
/// <item>Nothing that takes its target offline (<see cref="ProviderAction.Disrupts"/>) — a reboot by any other name.</item>
/// <item>Nothing on a Docker connection, so no container — LabbyTwo's own and the protected ones included — can be touched from HA.</item>
/// </list>
/// </summary>
public static class HaControls
{
    /// <summary>Why this action may not be a button, or null when it may.</summary>
    public static string? Refusal(ProviderAction action, Connection connection)
    {
        if (connection.Provider == "docker")
            return $"Containers on {connection.Name} cannot be controlled from Home Assistant.";
        if (action.Dangerous)
            return $"“{action.Label}” on {connection.Name} is marked dangerous, and dangerous actions are never offered to Home Assistant.";
        if (action.Fields.Count > 0)
            return $"“{action.Label}” on {connection.Name} asks for something before it runs, and nobody is there to answer.";
        if (action.Disrupts is not null)
            return $"“{action.Label}” on {connection.Name} takes it offline, so it is not offered to Home Assistant.";
        return null;
    }
}

/// <summary>How Home Assistant should treat one metric: its unit as HA spells it, and the classes that let HA chart and convert it.</summary>
public sealed record HaSensorKind(string? Unit, string? DeviceClass, string? StateClass);

/// <summary>
/// LabbyTwo's units in Home Assistant's words.
///
/// Values go out in the unit LabbyTwo stores them in, with that unit declared, and Home
/// Assistant converts for display. That is the choice for temperature in particular: a
/// sensor reporting °C into a Fahrenheit house is shown in °F by HA itself, because the
/// device class says it is a temperature — and the long-term statistics stay in one unit
/// whatever either side's display preference is later changed to. Converting here instead
/// would bake LabbyTwo's display preference into HA's history.
/// </summary>
public static class HaUnits
{
    public static HaSensorKind For(MetricSpec spec)
    {
        var unit = spec.Unit.Trim();
        var key = spec.Key.ToLowerInvariant();
        const string measurement = "measurement";

        return unit switch
        {
            "°C" or "°F" or "K" => new(unit, "temperature", measurement),
            "%" when key.Contains("humid") => new("%", "humidity", measurement),
            "%" when key.Contains("battery") => new("%", "battery", measurement),
            "%" => new("%", null, measurement),
            "W" or "kW" => new(unit, "power", measurement),
            "Wh" or "kWh" or "MWh" => new(unit, "energy", "total_increasing"),
            "V" => new("V", "voltage", measurement),
            "A" => new("A", "current", measurement),
            "ms" => new("ms", "duration", measurement),
            "s" => new("s", "duration", measurement),
            "min" => new("min", "duration", measurement),
            "h" => new("h", "duration", measurement),
            "days" or "d" => new("d", "duration", measurement),
            "bps" => new("bit/s", "data_rate", measurement),
            "kbps" => new("kbit/s", "data_rate", measurement),
            "Mbps" => new("Mbit/s", "data_rate", measurement),
            "Gbps" => new("Gbit/s", "data_rate", measurement),
            "B/s" or "kB/s" or "MB/s" or "GB/s" => new(unit, "data_rate", measurement),
            "B" or "kB" or "MB" or "GB" or "TB" => new(unit, "data_size", measurement),
            "KB" => new("kB", "data_size", measurement),
            "hPa" or "mbar" or "inHg" or "mmHg" or "kPa" => new(unit, "pressure", measurement),
            "mph" or "km/h" or "m/s" or "kn" => new(unit, "wind_speed", measurement),
            "mm" or "in" when key.Contains("rain") || key.Contains("precip") => new(unit, "precipitation", measurement),
            "W/m²" => new(unit, "irradiance", measurement),
            "µg/m³" when key.Contains("pm25") || key.Contains("pm2_5") => new(unit, "pm25", measurement),
            "µg/m³" when key.Contains("pm10") => new(unit, "pm10", measurement),
            "dBm" => new(unit, "signal_strength", measurement),
            "lx" => new(unit, "illuminance", measurement),
            "Hz" => new(unit, "frequency", measurement),
            "" => new(null, null, measurement),
            _ => new(unit, null, measurement),
        };
    }
}

/// <summary>One connection as LabbyTwo sees it right now, before anything is filtered by the settings.</summary>
/// <param name="Provider">The provider's display name — HA's "model".</param>
/// <param name="IsUp">Null until it has been checked once.</param>
/// <param name="Actions">Already filtered by <see cref="HaControls.Refusal"/>.</param>
public sealed record HaConnectionView(
    string Id,
    string Name,
    string Provider,
    bool? IsUp,
    DateTimeOffset? Since,
    DateTimeOffset? LastChecked,
    string Message,
    bool Silenced,
    bool CantCheck,
    IReadOnlyList<HaMetricView> Metrics,
    IReadOnlyList<ProviderAction> Actions);

public sealed record HaMetricView(MetricSpec Spec, double Value);

/// <summary>
/// The whole lab, read from memory: what the monitor, the alert evaluator, the incident
/// tracker and the backup checks already hold. Nothing in here came from the database on
/// the way to being published.
/// </summary>
public sealed record HaLab(
    string Version,
    IReadOnlyList<HaConnectionView> Connections,
    string Summary,
    bool AnyDown,
    bool AnyAlertFiring,
    Maintenance Maintenance,
    Blindness Blindness,
    bool BackupLate,
    int OpenIncidents)
{
    public static HaLab Empty => new("", [], "nothing monitored", false, false, Core.Maintenance.Off, Blindness.Clear, false, 0);
}

/// <summary>One discovery config: where it goes and what it says.</summary>
public sealed record HaEntity(string Component, string ObjectId, string UniqueId, string ConfigTopic, string Config);

/// <summary>
/// Everything that should be on the broker for these settings and this lab: the configs
/// and the states. The publisher compares it with what it last sent and sends only the
/// difference.
/// </summary>
public sealed class HaPlan
{
    public required IReadOnlyList<HaEntity> Entities { get; init; }
    public required IReadOnlyDictionary<string, string> States { get; init; }
    public required IReadOnlySet<string> PublishedConnections { get; init; }
    public required HaSettings Settings { get; init; }

    /// <summary>
    /// When each connection was last checked, by its attributes topic — added to the
    /// attributes as they are sent rather than kept in <see cref="States"/>, see
    /// <see cref="WithLastChecked"/>.
    /// </summary>
    public required IReadOnlyDictionary<string, DateTimeOffset?> LastChecked { get; init; }

    private HashSet<string>? _topics;

    /// <summary>
    /// Whether a discovery config found on the broker should stay. Anything this plan
    /// publishes does; so, with every metric published, does any metric config of a
    /// connection still published — straight after a restart a connection has not reported
    /// its metrics yet, and clearing them then would delete and re-create every metric
    /// entity in HA on each start.
    /// </summary>
    public bool Keeps(string configTopic)
    {
        _topics ??= [.. Entities.Select(e => e.ConfigTopic)];
        if (_topics.Contains(configTopic))
            return true;
        if (Settings.Readings != HaReadings.All)
            return false;

        var prefix = $"{Settings.Prefix}/sensor/{Settings.Node}/";
        if (!configTopic.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var objectId = configTopic[prefix.Length..].Split('/')[0];
        return PublishedConnections.Any(id => objectId.StartsWith($"c_{HaTopics.Id(id)}_m_", StringComparison.Ordinal));
    }

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    /// <summary>The plan for these settings and this lab. Pure: no clock but the lab's own times, no I/O.</summary>
    public static HaPlan Build(HaSettings s, HaLab lab)
    {
        var entities = new List<HaEntity>();
        var states = new Dictionary<string, string>(StringComparer.Ordinal);
        var published = new HashSet<string>(StringComparer.Ordinal);
        var lastChecked = new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal);
        var hubId = $"{s.Node}_hub";
        var availability = HaTopics.Availability(s);

        JsonObject Origin() => new()
        {
            ["name"] = "LabbyTwo",
            ["sw_version"] = lab.Version,
            ["support_url"] = "https://github.com/chrisdfennell/LabbyTwo",
        };

        JsonObject HubDevice() => new()
        {
            ["identifiers"] = new JsonArray(hubId),
            ["name"] = "LabbyTwo",
            ["manufacturer"] = "LabbyTwo",
            ["model"] = "Home lab dashboard",
            ["sw_version"] = lab.Version,
        };

        void Add(string component, string objectId, string name, JsonObject device, Action<JsonObject> fill)
        {
            var uniqueId = $"{s.Node}_{objectId}";
            var config = new JsonObject
            {
                ["name"] = name,
                ["unique_id"] = uniqueId,
                ["availability_topic"] = availability,
                ["payload_available"] = HaTopics.Online,
                ["payload_not_available"] = HaTopics.Offline,
            };
            fill(config);
            config["device"] = device;
            config["origin"] = Origin();
            entities.Add(new HaEntity(component, objectId, uniqueId, HaTopics.Config(s, component, objectId),
                config.ToJsonString(Compact)));
        }

        // ---- The hub: the lab as a whole ----
        void HubBinary(string name, string objectId, string label, string? deviceClass, bool value, string icon)
        {
            var topic = HaTopics.Hub(s, name);
            Add("binary_sensor", objectId, label, HubDevice(), c =>
            {
                c["state_topic"] = topic;
                if (deviceClass is not null)
                    c["device_class"] = deviceClass;
                c["icon"] = icon;
            });
            states[topic] = value ? HaTopics.On : HaTopics.Off;
        }

        var summaryTopic = HaTopics.Hub(s, "summary");
        Add("sensor", "hub_summary", "Lab summary", HubDevice(), c =>
        {
            c["state_topic"] = summaryTopic;
            c["icon"] = "mdi:server-network";
        });
        states[summaryTopic] = Truncate(lab.Summary, 250);

        HubBinary("any_down", "hub_any_down", "Anything down", "problem", lab.AnyDown, "mdi:server-off");
        HubBinary("any_alert", "hub_any_alert", "Alert firing", "problem", lab.AnyAlertFiring, "mdi:bell-alert");

        var maintenanceTopic = HaTopics.Hub(s, "maintenance");
        Add("binary_sensor", "hub_maintenance", "Maintenance", HubDevice(), c =>
        {
            c["state_topic"] = maintenanceTopic;
            c["json_attributes_topic"] = maintenanceTopic + "/attributes";
            c["icon"] = "mdi:wrench-clock";
        });
        states[maintenanceTopic] = lab.Maintenance.On ? HaTopics.On : HaTopics.Off;
        states[maintenanceTopic + "/attributes"] = Json(new JsonObject
        {
            ["until"] = lab.Maintenance.Until?.ToString("o", CultureInfo.InvariantCulture),
        });

        var blindTopic = HaTopics.Hub(s, "blind");
        Add("binary_sensor", "hub_blind", "LabbyTwo blind", HubDevice(), c =>
        {
            c["state_topic"] = blindTopic;
            c["device_class"] = "problem";
            c["json_attributes_topic"] = blindTopic + "/attributes";
            c["icon"] = "mdi:eye-off";
        });
        states[blindTopic] = lab.Blindness.Impaired ? HaTopics.On : HaTopics.Off;
        states[blindTopic + "/attributes"] = Json(new JsonObject
        {
            ["reason"] = lab.Blindness.Impaired ? lab.Blindness.Reason : null,
            ["since"] = lab.Blindness.Since?.ToString("o", CultureInfo.InvariantCulture),
        });

        HubBinary("backup_late", "hub_backup_late", "Backup late", "problem", lab.BackupLate, "mdi:backup-restore");
        HubBinary("incident_open", "hub_incident_open", "Incident open", "problem", lab.OpenIncidents > 0, "mdi:alert-octagon");

        var incidentsTopic = HaTopics.Hub(s, "incidents");
        Add("sensor", "hub_incidents", "Open incidents", HubDevice(), c =>
        {
            c["state_topic"] = incidentsTopic;
            c["state_class"] = "measurement";
            c["icon"] = "mdi:alert-octagon-outline";
        });
        states[incidentsTopic] = lab.OpenIncidents.ToString(CultureInfo.InvariantCulture);

        if (s.MaintenanceButtons)
        {
            Add("button", "hub_maintenance_start", "Start maintenance (1 hour)", HubDevice(), c =>
            {
                c["command_topic"] = HaTopics.HubCommand(s, HaTopics.MaintenanceStart);
                c["payload_press"] = HaTopics.Press;
                c["icon"] = "mdi:wrench";
            });
            Add("button", "hub_maintenance_end", "End maintenance", HubDevice(), c =>
            {
                c["command_topic"] = HaTopics.HubCommand(s, HaTopics.MaintenanceEnd);
                c["payload_press"] = HaTopics.Press;
                c["icon"] = "mdi:wrench-check";
            });
        }

        // ---- One device per connection ----
        foreach (var connection in lab.Connections.Where(c => s.Publishes(c.Id)))
        {
            published.Add(connection.Id);
            var id = HaTopics.Id(connection.Id);
            JsonObject Device() => new()
            {
                ["identifiers"] = new JsonArray($"{s.Node}_conn_{id}"),
                ["name"] = connection.Name,
                ["manufacturer"] = "LabbyTwo",
                ["model"] = connection.Provider,
                ["via_device"] = hubId,
            };

            var statusTopic = HaTopics.ConnectionStatus(s, connection.Id);
            var attributesTopic = HaTopics.ConnectionAttributes(s, connection.Id);
            Add("binary_sensor", $"c_{id}_status", "Connectivity", Device(), c =>
            {
                c["state_topic"] = statusTopic;
                c["device_class"] = "connectivity";
                c["json_attributes_topic"] = attributesTopic;
            });

            // "checking" stays unknown in HA rather than guessing; "None" is HA's word for that.
            states[statusTopic] = connection.IsUp switch { true => HaTopics.On, false => HaTopics.Off, null => "None" };
            lastChecked[attributesTopic] = connection.LastChecked;
            states[attributesTopic] = Json(new JsonObject
            {
                ["since"] = connection.Since?.ToString("o", CultureInfo.InvariantCulture),
                ["message"] = Truncate(connection.Message, 250),
                ["maintenance"] = lab.Maintenance.On,
                ["silenced"] = connection.Silenced,
                ["cant_check"] = connection.CantCheck,
            });

            foreach (var metric in connection.Metrics.Where(m => s.PublishesMetric(connection.Id, m.Spec.Key)))
            {
                var kind = HaUnits.For(metric.Spec);
                var topic = HaTopics.Metric(s, connection.Id, metric.Spec.Key);
                Add("sensor", $"c_{id}_m_{HaTopics.Id(metric.Spec.Key)}", metric.Spec.Label, Device(), c =>
                {
                    c["state_topic"] = topic;
                    if (kind.Unit is not null)
                        c["unit_of_measurement"] = kind.Unit;
                    if (kind.DeviceClass is not null)
                        c["device_class"] = kind.DeviceClass;
                    if (kind.StateClass is not null)
                        c["state_class"] = kind.StateClass;
                    c["suggested_display_precision"] = Math.Clamp(metric.Spec.Decimals, 0, 6);
                });
                states[topic] = double.IsFinite(metric.Value)
                    ? metric.Value.ToString("R", CultureInfo.InvariantCulture)
                    : "None";
            }

            if (s.ActionButtons)
            {
                foreach (var action in connection.Actions)
                {
                    Add("button", $"c_{id}_a_{HaTopics.Id(action.Id)}", action.Label, Device(), c =>
                    {
                        c["command_topic"] = HaTopics.ActionCommand(s, connection.Id, action.Id);
                        c["payload_press"] = HaTopics.Press;
                    });
                }
            }
        }

        return new HaPlan { Entities = entities, States = states, PublishedConnections = published, Settings = s, LastChecked = lastChecked };
    }

    /// <summary>
    /// The attributes as published, and as compared: "last checked" is deliberately not in
    /// them. It changes on every sweep, so including it would republish every connection
    /// every thirty seconds for a field nobody automates on — and would make "publish only
    /// what changed" mean "publish everything". It goes out separately, see
    /// <see cref="WithLastChecked"/>.
    /// </summary>
    private static string Json(JsonObject value) => value.ToJsonString(Compact);

    /// <summary>
    /// Attributes with <c>last_checked</c> added — sent when the rest of them changed, and on
    /// each full refresh, so the value in HA is never older than the refresh interval.
    /// </summary>
    public static string WithLastChecked(string attributes, DateTimeOffset? lastChecked)
    {
        if (JsonNode.Parse(attributes) is not JsonObject value)
            return attributes;
        value["last_checked"] = lastChecked?.ToString("o", CultureInfo.InvariantCulture);
        return value.ToJsonString(Compact);
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}
