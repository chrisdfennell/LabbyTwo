namespace LabbyTwo.Core;

/// <summary>
/// A card that can be placed on a grid tab. Like providers, a widget describes its own
/// settings and points at a Blazor component; the tab page renders it with
/// <c>DynamicComponent</c> and knows nothing about any particular widget.
/// </summary>
public interface IWidgetType
{
    /// <summary>Stable key stored on the widget row, e.g. "service-tile".</summary>
    string Type { get; }

    string DisplayName { get; }
    string Icon { get; }
    string Description { get; }

    /// <summary>
    /// Provider types this widget can bind to. Empty means it needs no connection
    /// (a note, a bookmark list); the widget picker filters on this, so a user can
    /// only build combinations that actually work.
    /// </summary>
    IReadOnlyList<string> ProviderTypes => [];

    bool NeedsConnection => ProviderTypes.Count > 0;

    IReadOnlyList<FieldSpec> Fields => [];

    /// <summary>Default width in grid columns (of 12) when first placed.</summary>
    int DefaultWidth => 4;

    /// <summary>Component rendered for this widget. Receives a <see cref="WidgetContext"/> parameter named "Context".</summary>
    Type Component { get; }
}

/// <summary>Everything a widget component needs, passed as a single parameter.</summary>
public sealed record WidgetContext(Widget Widget, Connection? Connection);
