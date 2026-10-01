namespace LabbyTwo.Core;

/// <summary>
/// One earlier version of some Markdown a person wrote — a note's, or a Markdown card's —
/// as the history view (TextHistory) shows it. The stores keep their own richer rows
/// (<see cref="Storage.NotesStore.NoteVersion"/>, <see cref="Storage.WidgetHistoryStore.WidgetVersion"/>);
/// this is the part the view needs, so one view serves both and they cannot drift apart.
/// </summary>
/// <param name="Title">What the note or card was called then.</param>
/// <param name="Content">The text; empty in a listing, which never reads it.</param>
/// <param name="Size">Characters of text, so a listing can say how big each was without reading it.</param>
/// <param name="WrittenAt">When this text was saved; null when that was before anybody recorded it.</param>
/// <param name="WrittenBy">Who saved it — for a deletion, who deleted it — or empty.</param>
/// <param name="KeptAt">When it was replaced, or deleted.</param>
/// <param name="KeptBy">Who replaced it, when known; empty otherwise.</param>
public sealed record TextVersion(
    long Id, string Title, string Content, int Size,
    DateTimeOffset? WrittenAt, string WrittenBy, DateTimeOffset KeptAt, string KeptBy, bool IsDeletion);
