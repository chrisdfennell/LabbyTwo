using System.Text.Json;

namespace LabbyTwo.Core;

/// <summary>
/// One bookmark. Lists of these are how <see cref="FieldKind.LinkList"/> fields are
/// stored — a JSON array in a single settings value — so a widget, an importer or a
/// plugin can all produce the same shape without going through the editor component.
/// </summary>
/// <param name="Icon">An emoji. Blank means the bookmark card fetches the site's own icon.</param>
public sealed record LinkRow(string Icon, string Name, string Url)
{
    public static List<LinkRow> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<LinkRow>>(json) ?? [];
        }
        catch (JsonException)
        {
            // A hand-edited or half-written value should empty the card, not break the page.
            return [];
        }
    }

    public static string Serialize(IEnumerable<LinkRow> rows) => JsonSerializer.Serialize(rows);
}
