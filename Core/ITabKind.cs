namespace LabbyTwo.Core;

/// <summary>
/// A kind of page in the nav — the third extension point, and deliberately shaped like
/// the other two. "grid" holds widgets, "embed" is an iframe, "notes" is markdown; a new
/// kind is one class implementing this interface, discovered the same way a provider or
/// a widget is.
/// </summary>
public interface ITabKind
{
    /// <summary>Stable key stored on the tab row, e.g. "grid". Never change it.</summary>
    string Kind { get; }

    string DisplayName { get; }
    string Icon { get; }
    string Description { get; }

    /// <summary>Settings this kind adds to the tab editor's generated form.</summary>
    IReadOnlyList<FieldSpec> Fields => [];

    /// <summary>Component rendered for this kind. Receives the tab as a parameter named "Tab".</summary>
    Type Component { get; }
}

/// <summary>The keys of the kinds that ship in the box, for code that needs to name one.</summary>
public static class TabKinds
{
    public const string Grid = "grid";
    public const string Embed = "embed";
    public const string Notes = "notes";
    public const string Status = "status";
}
