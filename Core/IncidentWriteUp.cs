using System.Globalization;
using System.Text;

namespace LabbyTwo.Core;

/// <summary>
/// What a write-up needs to know about a member's connection as it is now: its current name,
/// which the live shortcodes look it up by, and the key of an action worth a button — a
/// restart, usually — if it has one.
/// </summary>
public sealed record WriteUpConnection(string Name, string? ActionId = null);

/// <summary>A write-up ready to save as a note.</summary>
public sealed record WriteUpNote(string Title, string Markdown);

/// <summary>
/// Turns an incident into a Markdown note to finish by hand: what happened and when, what
/// failed, the probable cause, the timeline, two headings for the parts only a person knows
/// — what fixed it, and what to do next time — and, at the end, live shortcodes that make
/// it a runbook for the next time the same thing breaks.
///
/// A post-mortem nobody starts is one nobody writes, and the tedious half of one — times,
/// names, what changed — is exactly what LabbyTwo already has. So this writes that half and
/// leaves the other half as questions.
///
/// Every name and every line from the feed is the user's own text, or a service's. Written
/// into Markdown raw, <c>my_nas</c> turns half a line italic, a pipe splits a table cell,
/// and <c>{{button: …}}</c> in a container's name would become a live button in somebody's
/// runbook. So everything that is not ours is escaped (<see cref="Text"/>), and the only
/// shortcodes in the note are the ones written here through <see cref="Shortcodes.Write"/>.
/// </summary>
public static class IncidentWriteUp
{
    /// <summary>Where a write-up links back to its incident, as <c>{{incidents}}</c> does.</summary>
    public static string IncidentLink(long id) => $"incidents#incident-{id}";

    /// <summary>
    /// The note for <paramref name="incident"/>.
    /// </summary>
    /// <param name="causes">From <see cref="IncidentCauses.Explain"/>, surest first.</param>
    /// <param name="timeline">The feed around it, any order.</param>
    /// <param name="connections">Each member connection as it is now, by id. A member whose
    /// connection is gone gets no live shortcodes — they would only show "no such connection".</param>
    public static WriteUpNote Build(
        Incident incident,
        IReadOnlyList<ProbableCause> causes,
        IReadOnlyList<Change> timeline,
        IReadOnlyDictionary<string, WriteUpConnection> connections,
        DateTimeOffset now)
    {
        var started = incident.StartedAt.ToLocalTime();
        var title = $"Incident: {incident.Title} — {started.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}";
        var md = new StringBuilder();

        md.Append("> Written up from what LabbyTwo recorded about [this incident](").Append(IncidentLink(incident.Id))
            .AppendLine("). Everything here can be edited.").AppendLine();

        // ---- what happened
        md.AppendLine("## What happened").AppendLine();
        md.Append("- **Started:** ").AppendLine(Stamp(incident.StartedAt));
        md.Append("- **Ended:** ").AppendLine(incident.EndedAt is { } ended ? Stamp(ended) : "still going when this was written");
        md.Append("- **Lasted:** ").AppendLine(Ago.Duration(incident.Duration(now)) + (incident.IsOpen ? " so far" : ""));
        if (incident.Maintenance)
            md.AppendLine("- Maintenance mode was on for part of it, so its alerts were held.");
        md.AppendLine();

        // ---- what failed
        md.AppendLine("## What failed").AppendLine();
        md.AppendLine("| What | Went down | Came back | For |");
        md.AppendLine("|---|---|---|---|");
        foreach (var member in incident.Members.OrderBy(m => m.DownAt))
        {
            var what = member.Kind == ChangeKinds.Alert ? $"{Text(member.Name)} (alert)" : Text(member.Name);
            var back = member.UpAt is { } up ? Time(up) : "not yet";
            var span = Ago.Duration((member.UpAt ?? now) - member.DownAt);
            md.Append("| ").Append(what).Append(" | ").Append(Time(member.DownAt)).Append(" | ").Append(back)
                .Append(" | ").Append(span).AppendLine(" |");
        }
        md.AppendLine();

        // ---- probable cause
        md.AppendLine("## Probable cause").AppendLine();
        if (causes.Count == 0)
        {
            md.Append(IncidentCauses.NoObviousCause)
                .AppendLine(" — nothing LabbyTwo recorded ties to it. The timeline below is the place to look.");
        }
        foreach (var cause in causes)
        {
            md.Append("- ").Append(Text(cause.Sentence)).Append(" *(").Append(Confidence(cause.Confidence)).AppendLine(")*");
            foreach (var change in cause.Evidence)
                md.Append("  - ").Append(Time(change.At)).Append(" — ").AppendLine(Text(change.Title));
        }
        md.AppendLine();

        // ---- timeline
        md.AppendLine("## Timeline").AppendLine();
        var ordered = timeline.OrderBy(c => c.At).ThenBy(c => c.Id).ToList();
        if (ordered.Count == 0)
            md.AppendLine("Nothing else was recorded around it.");
        foreach (var change in ordered)
        {
            md.Append("- ").Append(Time(change.At)).Append(" (").Append(Offset(change.At - incident.StartedAt)).Append(") ")
                .Append(Text(change.Title));
            if (change.Detail.Length > 0)
                md.Append(" — ").Append(Text(change.Detail));
            md.AppendLine();
        }
        md.AppendLine();

        // ---- the parts only a person knows
        md.AppendLine("## What fixed it").AppendLine();
        md.AppendLine("*What brought it back — a restart, a rollback, waiting it out?*").AppendLine();
        md.AppendLine("## Next time").AppendLine();
        md.AppendLine("*What to check first, and what to change so it does not happen again.*").AppendLine();

        // ---- the runbook
        var live = incident.Members
            .Where(m => m.Kind == ChangeKinds.Status && m.ConnectionId is not null && connections.ContainsKey(m.ConnectionId))
            .OrderBy(m => m.DownAt)
            .Select(m => connections[m.ConnectionId!])
            .DistinctBy(c => c.Name)
            .ToList();
        if (live.Count > 0)
        {
            md.AppendLine("## Right now").AppendLine();
            foreach (var connection in live)
            {
                md.Append("- **").Append(Text(connection.Name)).Append("** is ")
                    .Append(Shortcodes.Write("status", [connection.Name]))
                    .Append(", checked ").Append(Shortcodes.Write("ago", [connection.Name]));
                if (connection.ActionId is { Length: > 0 } action)
                    md.Append(" ").Append(Shortcodes.Write("button", [connection.Name, action]));
                md.AppendLine();
            }
            md.AppendLine();

            // only= takes names separated by commas, so a name with one in it cannot be listed.
            var only = live.Select(c => c.Name).Where(n => !n.Contains(',')).ToList();
            md.AppendLine(only.Count > 0
                ? Shortcodes.Write("changes", [], [new("last", "24h"), new("only", string.Join(", ", only))])
                : Shortcodes.Write("changes", [], [new("last", "24h")]));
        }

        return new WriteUpNote(title, md.ToString().TrimEnd() + "\n");
    }

    /// <summary>
    /// Text that stays text in Markdown: emphasis, links, tables, headings and quotes
    /// escaped as the weekly summary escapes names, and braces too, so nothing in a name
    /// or a probe's message can become a live shortcode. On one line — a message with a
    /// line break in it would end the list item it sits in.
    /// </summary>
    public static string Text(string text)
    {
        var builder = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            if (c is '\r' or '\n')
            {
                builder.Append(' ');
                continue;
            }
            if (c is '\\' or '*' or '_' or '~' or '`' or '|' or '[' or ']' or '>' or '<' or '#' or '{' or '}')
                builder.Append('\\');
            builder.Append(c);
        }
        return builder.ToString();
    }

    private static string Confidence(CauseConfidence confidence) => confidence switch
    {
        CauseConfidence.High => "likely",
        CauseConfidence.Medium => "possible",
        _ => "worth a look",
    };

    private static string Stamp(DateTimeOffset at) =>
        at.ToLocalTime().ToString("ddd d MMM yyyy, HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Time(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Offset(TimeSpan offset) =>
        Math.Abs(offset.TotalSeconds) < 1 ? "start"
        : offset < TimeSpan.Zero ? $"−{Ago.Duration(-offset)}"
        : $"+{Ago.Duration(offset)}";
}
