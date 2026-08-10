using LabbyTwo.Core;

namespace LabbyTwo.PaperlessPlugin;

/// <summary>
/// The descriptor half of a widget: what it is called, what it can bind to, and what
/// settings it takes. The component itself is <see cref="RecentDocuments"/>.
///
/// Splitting the two is what lets the widget picker describe every widget — including
/// this one — without rendering any of them.
/// </summary>
public sealed class RecentDocumentsWidget : IWidgetType
{
    public string Type => "paperless-recent";
    public string DisplayName => "Paperless — recent documents";
    public string Icon => "📄";
    public string Description => "The documents most recently added to Paperless, newest first.";

    // Naming the provider is what greys this out in the picker for connections it cannot
    // work with, instead of letting someone build a card that can only ever error.
    public IReadOnlyList<string> ProviderTypes => ["paperless"];

    public int DefaultWidth => 4;

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("count", "How many", FieldKind.Number, Default: "5"),
    ];

    public Type Component => typeof(RecentDocuments);
}
