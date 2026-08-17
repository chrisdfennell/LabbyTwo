using LabbyTwo.Core;

namespace LabbyTwo.RssPlugin;

/// <summary>The descriptor. The component beside it does the drawing.</summary>
public sealed class HeadlinesWidget : IWidgetType
{
    public string Type => "rss-headlines";
    public string DisplayName => "Headlines";
    public string Icon => "📰";
    public string Description => "The newest items from a feed, as links.";

    public IReadOnlyList<string> ProviderTypes => [RssProvider.ProviderType];

    public int DefaultWidth => 4;

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("count", "How many", FieldKind.Number, Default: "6"),
        new("show_age", "Show how old each item is", FieldKind.Bool, Default: "true"),
    ];

    public Type Component => typeof(Headlines);
}
