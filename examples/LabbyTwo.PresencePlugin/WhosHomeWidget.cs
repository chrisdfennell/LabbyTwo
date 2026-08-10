using LabbyTwo.Core;

namespace LabbyTwo.PresencePlugin;

public sealed class WhosHomeWidget : IWidgetType
{
    public string Type => "whos-home";
    public string DisplayName => "Who's home";
    public string Icon => "🏠";
    public string Description => "Each watched device with a dot for whether it is on the network.";
    public IReadOnlyList<string> ProviderTypes => ["presence"];
    public int DefaultWidth => 3;

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("home_first", "Sort the ones who are home to the top", FieldKind.Bool, Default: "true"),
    ];

    public Type Component => typeof(WhosHome);
}
