using System.Security.Cryptography;
using System.Text;

namespace LabbyTwo.Core;

/// <summary>
/// One <c>- [ ] item</c> in a note, as the rendered view knows it.
/// </summary>
/// <param name="Key">
/// What its tick is stored under: a hash of the item's words and which occurrence of those
/// words it is (see <see cref="Checklists.Key"/>). Stable across edits to every other line,
/// which is the whole point — a tick must not move to a different item because somebody
/// added a step above it.
/// </param>
/// <param name="Text">The item's words as written, shortcodes and all, after the box.</param>
/// <param name="Position">Its place among the note's items, counting from zero; the
/// fallback for finding a tick again once the words have been edited.</param>
/// <param name="TickedInSource"><c>- [x]</c> in the text itself: always ticked, and not
/// something a click can undo, because the note says so.</param>
public sealed record ChecklistItem(string Key, string Text, int Position, bool TickedInSource);

/// <summary>Somebody ticked an item: who, and when.</summary>
public sealed record ChecklistTick(string Key, string Text, int Position, string By, DateTimeOffset At);

/// <summary>
/// Checklists that remember. A task list in a note — <c>- [ ] Stopped Plex</c> — is drawn
/// as boxes that can be ticked from the note itself, and the ticks are kept apart from the
/// note's text.
///
/// Apart, because the text is what version history keeps and what an edit changes. Writing
/// "[x]" into it on every click would make every tick a new version, would fight with
/// whoever has the editor open, and would mean "reset the checklist" was an edit. So the
/// source keeps its <c>[ ]</c>, the ticks live in their own table keyed by item, and the
/// rendered view lays one over the other. An <c>[x]</c> written in the source still means
/// what it always did: ticked, for good.
///
/// Pure text work here; the storage is <see cref="Storage.ChecklistStore"/> and the
/// drawing is LiveChecklistItem.
/// </summary>
public static class Checklists
{
    /// <summary>
    /// The kind of the stand-in <see cref="Shortcode"/> a checkbox becomes while a note is
    /// prepared. Brackets, so nothing anybody types can parse as it: a written kind is
    /// letters and dashes (see <see cref="Shortcodes.Parse"/>), so <c>{{task: …}}</c> stays
    /// the unknown shortcode it is rather than drawing a box.
    /// </summary>
    public const string Kind = "[task]";

    /// <summary>Options the stand-in carries; never written by a person.</summary>
    public const string KeyOption = "key";
    public const string TextOption = "text";
    public const string PositionOption = "position";
    public const string TickedOption = "ticked";

    /// <summary>
    /// The words an item is known by: whitespace runs folded to one space and the ends
    /// trimmed, so re-indenting a list or a trailing space does not lose its tick.
    /// </summary>
    public static string Normalise(string? text)
    {
        var builder = new StringBuilder();
        var space = false;
        foreach (var c in (text ?? "").Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }
            if (space && builder.Length > 0)
                builder.Append(' ');
            space = false;
            builder.Append(c);
        }
        return builder.ToString();
    }

    /// <summary>
    /// The key an item's tick is stored under: sixteen hex digits of a SHA-256 of its words,
    /// then which occurrence of those words it is. Two "Check the logs" items in one note
    /// are two items, ":0" and ":1"; adding a third elsewhere never renumbers the first two
    /// unless it is added above them with the very same words.
    /// </summary>
    public static string Key(string text, int occurrence)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Normalise(text)));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8)) + ":" + occurrence.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The stand-in shortcode a checkbox is drawn from.</summary>
    public static Shortcode ToShortcode(ChecklistItem item) => new(
        Kind,
        [],
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [KeyOption] = item.Key,
            [TextOption] = item.Text,
            [PositionOption] = item.Position.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [TickedOption] = item.TickedInSource ? "true" : "",
        },
        // Unique in the note, since Shortcode compares by its source.
        $"[{(item.TickedInSource ? "x" : " ")}] {item.Key}");

    /// <summary>The item a stand-in shortcode stands for; null for anything else.</summary>
    public static ChecklistItem? FromShortcode(Shortcode? code)
    {
        if (code is null || code.Kind != Kind)
            return null;
        _ = int.TryParse(code.Option(PositionOption), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var position);
        return new ChecklistItem(code.Option(KeyOption), code.Option(TextOption), position, code.Option(TickedOption) == "true");
    }

    /// <summary>
    /// Whether text could have a task list in it — a cheap look before anything is parsed,
    /// for the notes page to decide whether a note gets a "Reset checklist" button.
    /// </summary>
    public static bool MayHaveItems(string? markdown) =>
        markdown is not null && (markdown.Contains("[ ]", StringComparison.Ordinal)
                                 || markdown.Contains("[x]", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Which stored ticks belong to which of the note's items now, after an edit.
    ///
    /// An item whose key is still there keeps its tick — the usual case, since an edit
    /// elsewhere changes no item's words. A tick whose key is gone was on an item that was
    /// reworded or removed; if an item with no tick of its own now sits at the same
    /// position, it is taken to be the same step reworded ("Stop Plex" → "Stop Plex and
    /// Sonarr") and the tick moves to it. Anything left was on a step that is gone, and is
    /// dropped.
    /// </summary>
    /// <returns>Ticks to keep, under their items' current keys and words.</returns>
    public static IReadOnlyList<ChecklistTick> Reconcile(IReadOnlyList<ChecklistItem> items, IReadOnlyCollection<ChecklistTick> ticks)
    {
        var byKey = ticks.GroupBy(t => t.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var kept = new List<ChecklistTick>();
        var unticked = new List<ChecklistItem>();
        var currentKeys = new HashSet<string>(items.Select(i => i.Key), StringComparer.Ordinal);

        foreach (var item in items)
        {
            if (byKey.TryGetValue(item.Key, out var tick))
                kept.Add(tick with { Text = item.Text, Position = item.Position });
            else
                unticked.Add(item);
        }

        var orphans = byKey.Values.Where(t => !currentKeys.Contains(t.Key)).ToList();
        foreach (var item in unticked)
        {
            // Ticked in the source already: a stored tick would add nothing.
            if (item.TickedInSource)
                continue;
            var orphan = orphans.FirstOrDefault(t => t.Position == item.Position);
            if (orphan is null)
                continue;
            orphans.Remove(orphan);
            kept.Add(orphan with { Key = item.Key, Text = item.Text, Position = item.Position });
        }
        return kept;
    }
}

/// <summary>
/// Hands out keys to a note's items in the order they are written, across every piece the
/// note is cut into — so the same words in an <c>{{if}}</c> and its <c>{{else}}</c> are two
/// items, and the count does not start again in each section.
/// </summary>
public sealed class ChecklistCounter
{
    private readonly Dictionary<string, int> _seen = new(StringComparer.Ordinal);
    private readonly List<ChecklistItem> _items = [];

    /// <summary>Every item handed out so far, in order.</summary>
    public IReadOnlyList<ChecklistItem> Items => _items;

    public ChecklistItem Next(string text, bool tickedInSource)
    {
        var words = Checklists.Normalise(text);
        _seen.TryGetValue(words, out var occurrence);
        _seen[words] = occurrence + 1;
        var item = new ChecklistItem(Checklists.Key(words, occurrence), words, _items.Count, tickedInSource);
        _items.Add(item);
        return item;
    }
}
