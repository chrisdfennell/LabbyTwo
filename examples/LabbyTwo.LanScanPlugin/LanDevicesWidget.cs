using LabbyTwo.Core;

namespace LabbyTwo.LanScanPlugin;

public sealed class LanDevicesWidget : IWidgetType
{
    public string Type => "lan-devices";
    public string DisplayName => "Network — devices";
    public string Icon => "🛰️";
    public string Description => "What answered on the last sweep, with names, response times and open ports.";
    public IReadOnlyList<string> ProviderTypes => ["lanscan"];
    public int DefaultWidth => 4;

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("limit", "Devices to show", FieldKind.Number, Default: "12"),
        new("show_ports", "Show open ports", FieldKind.Bool, Default: "true"),
    ];

    public Type Component => typeof(LanDevices);
}

/// <summary>
/// A page for the whole network, rather than the handful of rows a card has room for.
///
/// The card and the tab answer different questions. A card on a dashboard is a glance —
/// is anything obviously wrong. This is what you open when the glance said something, so
/// it shows every device, every column, and a filter, and it does not compete for space.
/// </summary>
public sealed class NetworkTabKind : ITabKind
{
    public string Kind => "network";
    public string DisplayName => "Network";
    public string Icon => "🛰️";

    public string Description =>
        "Every device the last sweep found, with names, hardware addresses and open ports, and a filter.";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("connection", "Network scan", FieldKind.Connection,
            Help: "Leave blank to use the only one you have.")
            { ProviderFilter = "lanscan" },
    ];

    public Type Component => typeof(NetworkTab);
}
