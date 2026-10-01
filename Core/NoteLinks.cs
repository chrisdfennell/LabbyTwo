using System.Text;

namespace LabbyTwo.Core;

/// <summary>
/// One <c>[[…]]</c> written into Markdown: a link to another note by its title.
/// </summary>
/// <param name="Index">Where it starts in the text it was found in.</param>
/// <param name="Length">Brackets included.</param>
/// <param name="Target">Everything before the <c>|</c>, as written but with its spaces tidied:
/// <c>Plex runbook</c>, <c>Runbooks / Plex runbook</c>, <c>Plex runbook#Restart</c>. Which
/// part is a page, a title or a heading is only decided against the notes that exist (see
/// <see cref="NoteGraph.Resolve"/>), because a title is allowed to contain a slash or a hash.</param>
/// <param name="Text">What to show instead of the title — the part after the <c>|</c> — or null.</param>
/// <param name="Source">Exactly what was written, brackets and all.</param>
public sealed record NoteLink(int Index, int Length, string Target, string? Text, string Source)
{
    /// <summary>
    /// The link as the placeholder machinery carries it: a <see cref="Shortcode"/> of a kind
    /// nobody can type (<see cref="NoteLinks.Kind"/>), so <c>[[…]]</c> is the only way to
    /// make one, its target the only part and its text an option.
    /// </summary>
    public Shortcode ToShortcode()
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Text is { Length: > 0 } text)
            options["text"] = text;
        return new Shortcode(NoteLinks.Kind, [Target], options, Source);
    }
}

/// <summary>
/// Finds, reads and writes <c>[[Note title]]</c> links between notes. Pure text work, like
/// <see cref="Shortcodes"/>, so every odd thing somebody types is pinned by a test.
///
/// The grammar:
/// <list type="bullet">
/// <item><c>[[</c>, a target, optionally <c>|</c> and the words to show, <c>]]</c> — all on one
/// line, with no other square bracket inside, and no space straight inside the brackets.
/// That last rule is what keeps a shell test like <c>if [[ -f x ]]</c> the text it was; a
/// link is always written <c>[[Like this]]</c>.</item>
/// <item>The target is a title (any case), optionally with the page in front —
/// <c>[[Runbooks / Plex]]</c> — and a heading behind — <c>[[Plex#Restart]]</c>. A title that
/// itself contains a slash or a hash still works: the whole target is tried as a title
/// first.</item>
/// <item>A backslash before it (<c>\[[</c>) leaves it alone, as with shortcodes; and anything in
/// a code span or code block is left alone by the renderer, so examples stay examples.</item>
/// <item>A <c>[[…]]</c> with a shortcode inside it, or inside a shortcode, is not a link: the
/// shortcode was there first, and stays live.</item>
/// </list>
/// </summary>
public static class NoteLinks
{
    /// <summary>
    /// The kind a link travels as among the shortcodes. Not letters and dashes, so
    /// <see cref="Shortcodes.Parse"/> can never produce it from anything typed.
    /// </summary>
    public const string Kind = "[[]]";

    /// <summary>Longer than any title anybody writes, short enough that a stray <c>[[</c> cannot swallow a paragraph.</summary>
    public const int MaxLength = 300;

    /// <summary>Every link in <paramref name="text"/>, in order.</summary>
    public static IReadOnlyList<NoteLink> Find(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("[[", StringComparison.Ordinal))
            return [];

        // A [[ inside a shortcode's quoted argument is part of that argument.
        var shortcodes = Shortcodes.Find(text);
        var found = new List<NoteLink>();
        var index = 0;
        while ((index = text.IndexOf("[[", index, StringComparison.Ordinal)) >= 0)
        {
            if (IsEscaped(text, index) || TryRead(text, index) is not { } link
                || shortcodes.Any(f => link.Index < f.Index + f.Length && f.Index < link.Index + link.Length))
            {
                index += 2;
                continue;
            }
            found.Add(link);
            index += link.Length;
        }
        return found;
    }

    /// <summary>
    /// The distinct targets a note links to, as the link index keeps them (see
    /// <see cref="Key"/>). Links inside code are included: the index is for "what links
    /// here", and counting an example in a code block costs at worst one extra backlink,
    /// where finding the code would mean rendering every note on every save.
    /// </summary>
    public static IReadOnlyList<string> Targets(string? text) =>
        [.. Find(text).Select(l => Key(l.Target)).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// A target as the index stores and compares it: spaces tidied and lower-cased, so
    /// <c>[[Plex  Runbook]]</c> and <c>[[plex runbook]]</c> are one row.
    /// </summary>
    public static string Key(string target) => Tidy(target).ToLowerInvariant();

    /// <summary>Runs of whitespace as one space, ends trimmed.</summary>
    public static string Tidy(string text)
    {
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }
            if (space)
                builder.Append(' ');
            space = false;
            builder.Append(c);
        }
        return builder.ToString();
    }

    /// <summary>
    /// Whether <paramref name="title"/> can be written inside <c>[[…]]</c> at all: not
    /// empty, no square brackets, no <c>|</c>, no braces that would make a shortcode, on one line.
    /// </summary>
    public static bool CanLink(string? title) => Writable(title, allowPipe: false) && Tidy(title!).Length > 0;

    /// <summary>
    /// A link written out, or null when one of the parts cannot be written inside the
    /// brackets (see <see cref="CanLink"/>). What the editor's note picker and the rename
    /// offer type for you, so the one place that has to agree with <see cref="Find"/>.
    /// </summary>
    /// <param name="page">The page to look on, for a title more than one page has. Null for none.</param>
    public static string? Write(string title, string? heading = null, string? text = null, string? page = null)
    {
        if (!CanLink(title))
            return null;
        if (heading is { Length: > 0 } && !Writable(heading, allowPipe: false))
            return null;
        if (page is { Length: > 0 } && !Writable(page, allowPipe: false))
            return null;
        if (text is { Length: > 0 } && !Writable(text, allowPipe: true))
            return null;

        var builder = new StringBuilder("[[");
        if (page is { Length: > 0 } && Tidy(page).Length > 0)
            builder.Append(Tidy(page)).Append(" / ");
        builder.Append(Tidy(title));
        if (heading is { Length: > 0 } && Tidy(heading).Length > 0)
            builder.Append('#').Append(Tidy(heading));
        if (text is { Length: > 0 } && Tidy(text).Length > 0)
            builder.Append('|').Append(Tidy(text));
        return builder.Append("]]").ToString();
    }

    /// <summary>
    /// <paramref name="markdown"/> with every link that <paramref name="target"/> says points
    /// at the renamed note written again with <paramref name="title"/>, keeping its page,
    /// heading and words. Everything else — other links, code, the rest of the text — is
    /// left byte for byte as it was. For the "update the links to it" offer after a rename.
    /// </summary>
    /// <param name="target">How a link resolves, or null when it is not one to change.</param>
    public static string Retarget(string markdown, Func<NoteLink, NoteTarget?> target, string title)
    {
        var links = Find(markdown);
        if (links.Count == 0)
            return markdown;

        var builder = new StringBuilder(markdown.Length);
        var at = 0;
        foreach (var link in links)
        {
            if (target(link) is not { Note: not null } resolved
                || Write(title, resolved.Heading, link.Text, resolved.Page) is not { } written)
                continue;
            builder.Append(markdown, at, link.Index - at).Append(written);
            at = link.Index + link.Length;
        }
        return builder.Append(markdown, at, markdown.Length - at).ToString();
    }

    private static bool Writable(string? value, bool allowPipe) =>
        value is not null
        && !value.Contains("{{", StringComparison.Ordinal)
        && !value.Contains("}}", StringComparison.Ordinal)
        && value.All(c => c is not ('[' or ']' or '\r' or '\n') && (allowPipe || c != '|'));

    /// <summary>
    /// A backslash escapes the brackets only if it is not itself escaped — the same rule
    /// as <see cref="Shortcodes"/>, and what Markdown makes of <c>\[</c>.
    /// </summary>
    private static bool IsEscaped(string text, int index)
    {
        var slashes = 0;
        for (var i = index - 1; i >= 0 && text[i] == '\\'; i--)
            slashes++;
        return slashes % 2 == 1;
    }

    private static NoteLink? TryRead(string text, int start)
    {
        var close = -1;
        for (var i = start + 2; i < text.Length && i - start <= MaxLength; i++)
        {
            var c = text[i];
            if (c is '\r' or '\n' or '[')
                return null;
            if (c == ']')
            {
                if (i + 1 < text.Length && text[i + 1] == ']')
                    close = i;
                break;
            }
        }
        if (close < 0)
            return null;

        var inner = text[(start + 2)..close];
        if (inner.Length == 0 || char.IsWhiteSpace(inner[0]) || char.IsWhiteSpace(inner[^1])
            || inner.Contains("{{", StringComparison.Ordinal) || inner.Contains("}}", StringComparison.Ordinal))
            return null;

        var pipe = inner.IndexOf('|');
        var target = Tidy(pipe < 0 ? inner : inner[..pipe]);
        var shown = pipe < 0 ? null : Tidy(inner[(pipe + 1)..]);
        if (target.Length == 0)
            return null;
        return new NoteLink(start, close + 2 - start, target, shown is { Length: > 0 } ? shown : null, text[start..(close + 2)]);
    }
}

/// <summary>Where a note can be linked to and written: a notes tab, or a notes section on a custom page.</summary>
/// <param name="ContainerId">The tab's id, or the section block's id — what a note's <c>tab_id</c> holds.</param>
/// <param name="Page">The page's name in the nav.</param>
/// <param name="Section">The section's own title, for a notes section on a custom page; null for a notes tab.</param>
/// <param name="Href">The page, relative to the base address: <c>t/runbooks</c>.</param>
public sealed record NotePlace(string ContainerId, string Page, string? Section, string Href)
{
    /// <summary>Whether a <c>[[Page / Title]]</c> names this place: its page's name or the section's title.</summary>
    public bool Answers(string name) =>
        string.Equals(Page.Trim(), name, StringComparison.OrdinalIgnoreCase)
        || (Section is { } section && string.Equals(section.Trim(), name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The address that opens a new note here with <paramref name="title"/> filled in.</summary>
    public string NewNoteHref(string title) =>
        $"{Href}?new={Uri.EscapeDataString(title)}&in={Uri.EscapeDataString(ContainerId)}";

    /// <summary>"Runbooks", or "Home › Notes" for a section.</summary>
    public string Describe() => Section is { Length: > 0 } section ? $"{Page} › {section}" : Page;
}

/// <summary>One note as links see it: no content, just where it is and what it is called.</summary>
public sealed record NoteEntry(string Id, string Title, NotePlace Place)
{
    /// <summary>"Untitled" for a note with no title, which links cannot name but backlinks can list.</summary>
    public string Shown => Title.Trim() is { Length: > 0 } title ? title : "Untitled";

    public string Href => $"{Place.Href}#note-{Id}";

    /// <summary>
    /// The note scrolled to one of its headings. The heading travels in the query rather
    /// than as the fragment, because two notes on one page can both have a "Restart"
    /// heading and the browser would always pick the first; the notes page finds it
    /// inside this note (see notes.js), and the fragment still lands on the note itself
    /// if the heading has gone.
    /// </summary>
    public string HrefTo(string? heading) => heading is { Length: > 0 }
        ? $"{Place.Href}?heading={Uri.EscapeDataString(heading)}#note-{Id}"
        : Href;
}

/// <summary>One row of the link index: note <paramref name="FromId"/> has <c>[[…]]</c> with this <see cref="NoteLinks.Key"/>.</summary>
/// <param name="ToId">The note it last resolved to, kept so a link still finds its note
/// after that note is renamed. Null until it has resolved once.</param>
public sealed record NoteLinkRow(string FromId, string Target, string? ToId);

/// <summary>What a link points at.</summary>
/// <param name="Note">Null when there is no such note.</param>
/// <param name="Title">The title as the link names it (for a missing note, the title to create).</param>
/// <param name="Heading">The heading after the <c>#</c>, if one was split off.</param>
/// <param name="Page">The page written in front, if one was split off.</param>
/// <param name="ByRememberedId">True when the title no longer matches and the link was
/// found through the id the index remembered — a note renamed since.</param>
public sealed record NoteTarget(NoteEntry? Note, string Title, string? Heading, string? Page, bool ByRememberedId = false);

/// <summary>
/// Every note's title and place, and the link index, read once and held — so drawing a
/// link is a dictionary lookup and "Linked from" is a list already made. Rebuilt whenever a
/// note or a page changes (see <c>NoteDirectory</c>), which is rare next to how often a
/// page with links on it is drawn.
/// </summary>
public sealed class NoteGraph
{
    public static NoteGraph Empty { get; } = new([], [], []);

    private readonly Dictionary<string, List<NoteEntry>> _byTitle = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NoteEntry> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string?>> _remembered = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<NoteEntry>> _backlinks = new(StringComparer.Ordinal);

    public NoteGraph(IEnumerable<NotePlace> places, IEnumerable<NoteEntry> notes, IEnumerable<NoteLinkRow> links)
    {
        Places = [.. places];
        Notes = [.. notes.OrderBy(n => n.Place.Page, StringComparer.OrdinalIgnoreCase)
            .ThenBy(n => n.Shown, StringComparer.OrdinalIgnoreCase).ThenBy(n => n.Id, StringComparer.Ordinal)];
        foreach (var note in Notes)
        {
            _byId[note.Id] = note;
            var title = NoteLinks.Tidy(note.Title);
            if (title.Length == 0)
                continue;
            if (!_byTitle.TryGetValue(title, out var list))
                _byTitle[title] = list = [];
            list.Add(note);
        }

        var rows = links.ToList();
        foreach (var row in rows)
        {
            if (!_remembered.TryGetValue(row.FromId, out var map))
                _remembered[row.FromId] = map = new(StringComparer.Ordinal);
            map[row.Target] = row.ToId;
        }

        var corrections = new List<NoteLinkRow>();
        var orphans = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!_byId.TryGetValue(row.FromId, out var from))
            {
                orphans.Add(row.FromId);
                continue;
            }
            var to = Resolve(row.Target, row.FromId).Note;
            if (to is null)
                continue;
            if (to.Id != row.ToId)
                corrections.Add(row with { ToId = to.Id });
            if (to.Id == from.Id)
                continue;
            if (!_backlinks.TryGetValue(to.Id, out var list))
                _backlinks[to.Id] = list = [];
            if (!list.Contains(from))
                list.Add(from);
        }
        foreach (var list in _backlinks.Values)
            list.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Shown, b.Shown));
        Corrections = corrections;
        Orphans = orphans;
    }

    /// <summary>Every place a note can live, notes tabs first in nav order.</summary>
    public IReadOnlyList<NotePlace> Places { get; }

    /// <summary>Every note, by page and then title.</summary>
    public IReadOnlyList<NoteEntry> Notes { get; }

    /// <summary>
    /// Index rows whose remembered note is not the one they resolve to now — a link that
    /// has found its note for the first time, or found a different one. Written back, so
    /// that the id is there to fall back on the day the note is renamed.
    /// </summary>
    public IReadOnlyList<NoteLinkRow> Corrections { get; }

    /// <summary>Notes the index has rows for that no longer exist — their whole tab was deleted.</summary>
    public IReadOnlySet<string> Orphans { get; }

    public NoteEntry? Note(string? id) => id is not null && _byId.TryGetValue(id, out var note) ? note : null;

    /// <summary>The place a note is on, by the note's id or the place's own; null for anything else (a Markdown card).</summary>
    public NotePlace? PlaceOf(string? id) =>
        Note(id)?.Place ?? Places.FirstOrDefault(p => p.ContainerId == id);

    /// <summary>The notes that link to <paramref name="noteId"/>, by title, a note never listed as linking to itself.</summary>
    public IReadOnlyList<NoteEntry> Backlinks(string noteId) =>
        _backlinks.TryGetValue(noteId, out var list) ? list : [];

    /// <summary>
    /// The notes whose links reach <paramref name="noteId"/> by its title, as opposed to by
    /// an id remembered from before a rename — the ones whose text would go stale if it
    /// were renamed now. Each with the links in it that do.
    /// </summary>
    public IReadOnlyList<NoteEntry> LinkingByTitle(string noteId) =>
        [.. Backlinks(noteId).Where(from =>
            _remembered.TryGetValue(from.Id, out var map)
            && map.Keys.Any(target => Resolve(target, from.Id) is { Note.Id: var id, ByRememberedId: false } && id == noteId))];

    /// <summary>
    /// What <paramref name="target"/>, written in the note or card <paramref name="fromId"/>,
    /// points at. In order:
    /// <list type="number">
    /// <item>the whole target as a title — so a title with a slash or hash in it works;</item>
    /// <item><c>Page / Title</c>, the page being a notes tab's name or a notes section's title;</item>
    /// <item>either of those with a <c>#Heading</c> split off the end;</item>
    /// <item>the note this link resolved to last time, if the title has stopped matching
    /// because that note was renamed.</item>
    /// </list>
    /// Two notes of the same title: the one remembered, then the one on the same page as
    /// the link, then the first by page name.
    /// </summary>
    public NoteTarget Resolve(string target, string? fromId)
    {
        var written = NoteLinks.Tidy(target);
        string? remembered = null;
        if (fromId is not null && _remembered.TryGetValue(fromId, out var map))
            map.TryGetValue(NoteLinks.Key(written), out remembered);

        if (Match(written, fromId, remembered) is { } whole)
            return whole;

        var hash = written.IndexOf('#');
        string? heading = null;
        var name = written;
        if (hash >= 0)
        {
            heading = NoteLinks.Tidy(written[(hash + 1)..]) is { Length: > 0 } h ? h : null;
            name = NoteLinks.Tidy(written[..hash]);
            if (name.Length > 0 && Match(name, fromId, remembered) is { } split)
                return split with { Heading = heading };
        }

        // Nothing by that name. The page part, if it names a page, is a page; the rest is
        // the title a new note would have.
        string? page = null;
        var title = name;
        var slash = name.IndexOf('/');
        if (slash > 0 && Places.Any(p => p.Answers(NoteLinks.Tidy(name[..slash]))))
        {
            page = NoteLinks.Tidy(name[..slash]);
            title = NoteLinks.Tidy(name[(slash + 1)..]);
        }

        if (Note(remembered) is { } renamed)
            return new NoteTarget(renamed, title, heading, page, ByRememberedId: true);
        return new NoteTarget(null, title.Length > 0 ? title : written, heading, page);
    }

    private NoteTarget? Match(string name, string? fromId, string? remembered)
    {
        if (_byTitle.TryGetValue(name, out var exact))
            return new NoteTarget(Pick(exact, fromId, remembered), name, null, null);

        // Page / Title — at every slash, since either half may contain one.
        for (var slash = name.IndexOf('/'); slash > 0; slash = name.IndexOf('/', slash + 1))
        {
            var page = NoteLinks.Tidy(name[..slash]);
            var title = NoteLinks.Tidy(name[(slash + 1)..]);
            if (title.Length == 0 || !_byTitle.TryGetValue(title, out var titled))
                continue;
            var there = titled.Where(n => n.Place.Answers(page)).ToList();
            if (there.Count > 0)
                return new NoteTarget(Pick(there, fromId, remembered), title, null, page);
        }
        return null;
    }

    private NoteEntry Pick(List<NoteEntry> candidates, string? fromId, string? remembered)
    {
        if (candidates.Count == 1)
            return candidates[0];
        if (remembered is not null && candidates.FirstOrDefault(n => n.Id == remembered) is { } known)
            return known;
        if (PlaceOf(fromId) is { } here && candidates.FirstOrDefault(n => n.Place.ContainerId == here.ContainerId) is { } near)
            return near;
        // Notes are already in page-then-title order, so the first is the first by page.
        return candidates[0];
    }
}
