using LabbyTwo.Core;

namespace LabbyTwo.Services;

/// <summary>
/// What the built-in note templates (<see cref="NoteTemplates"/>) are told about a
/// connection: its first metric for the trend line, the buttons worth a runbook, and
/// whether it has a web page to link to. All from memory — the registry and the provider's
/// own list of actions — so choosing a connection in the picker costs nothing.
/// </summary>
public static class NoteTemplateFacts
{
    /// <summary>More buttons than this in a "while it's down" section is a control panel, not a runbook.</summary>
    public const int MaxButtons = 4;

    public static TemplateConnection For(Connection connection, Registry registry, ActionRunner actions)
    {
        var metric = registry.MetricsFor(connection).FirstOrDefault();
        // Only actions that need nothing typed in can be a {{button}}; the safe ones first,
        // so the first button somebody reaches for is not the one that switches it off.
        var buttons = actions.ActionsFor(connection)
            .Where(a => a.Fields.Count == 0)
            .OrderBy(a => a.Dangerous)
            .Take(MaxButtons)
            .Select(a => a.Id)
            .ToList();
        return new TemplateConnection(
            connection.Name,
            metric?.Key,
            metric?.Label,
            buttons,
            ShortcodeLookup.WebAddress(connection, out _) is not null);
    }
}
