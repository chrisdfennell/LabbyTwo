namespace LabbyTwo.Core;

public enum FieldKind
{
    Text,
    Password,
    Number,
    Bool,
    Url,
    Select,
    Textarea,
    /// <summary>Free text that will be rendered as Markdown, so the form can point out mistakes in it.</summary>
    Markdown,
    Icon,
    /// <summary>A repeating list of name/url rows, edited as one control (bookmarks, hosts…).</summary>
    LinkList,
    /// <summary>
    /// The name of a metric on the bound connection. Rendered as a text box with a
    /// dropdown of what the provider declares and what history has actually seen — a
    /// suggestion, not a restriction, because a provider can report more than it declares.
    /// </summary>
    Metric,
}

/// <summary>
/// One input in a generated form. Providers, widgets and tab kinds describe their
/// configuration as a list of these and never write a form of their own — <c>SettingsForm</c>
/// renders any list, so a new integration is one file and no UI work.
/// </summary>
public sealed record FieldSpec(
    string Key,
    string Label,
    FieldKind Kind = FieldKind.Text,
    string? Placeholder = null,
    string? Help = null,
    bool Required = false,
    string? Default = null,
    IReadOnlyList<SelectOption>? Options = null)
{
    /// <summary>Password fields are encrypted at rest and never rendered back to the browser.</summary>
    public bool IsSecret => Kind == FieldKind.Password;
}

public sealed record SelectOption(string Value, string Label);
