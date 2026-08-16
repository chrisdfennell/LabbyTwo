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
