using System.Globalization;
using System.Text;

namespace LabbyTwo.Core;

/// <summary>A note ready to open in the editor: not saved anywhere until somebody presses Save.</summary>
public sealed record NoteDraft(string Title, string Markdown);

/// <summary>
/// What a template needs to know about one connection, worked out by the caller from the
/// registry and the action runner so this stays pure text work.
/// </summary>
/// <param name="Name">Its name now, which the shortcodes look it up by.</param>
/// <param name="Metric">The key of the metric worth a trend line — the provider's first — or null for none, which draws response time.</param>
/// <param name="MetricLabel">That metric's label, for the words beside the line.</param>
/// <param name="Actions">Buttons worth putting in a runbook: ones that need nothing typed in, safest first.</param>
/// <param name="HasWebPage">Whether <c>{{link}}</c> has somewhere to go.</param>
public sealed record TemplateConnection(
    string Name,
    string? Metric = null,
    string? MetricLabel = null,
    IReadOnlyList<string>? Actions = null,
    bool HasWebPage = false);

/// <summary>
/// The built-in "New note from template…" entries: Markdown with the chosen connections
/// written into it, ready to finish by hand.
///
/// Every name is the user's own text, so it goes in the way <see cref="IncidentWriteUp"/>
/// puts names in: as Markdown text through <see cref="IncidentWriteUp.Text"/> (so
/// <c>my_nas</c> is not half italic and a <c>|</c> does not split a table cell), and inside
/// a shortcode only through <see cref="Shortcodes.Write"/> or
/// <see cref="Shortcodes.QuoteTarget"/> — so a connection called
/// <c>NAS}} {{button: Router / reboot</c> is one oddly named connection, not a button.
///
/// Only shortcodes that exist are used; checklists are plain <c>- [ ]</c> Markdown lines.
/// </summary>
public static class NoteTemplates
{
    public const string Runbook = "runbook";
    public const string Incident = "incident";
    public const string Maintenance = "maintenance";
    public const string Overview = "overview";

    /// <summary>How many connections a template is written about.</summary>
    public enum Needs
    {
        Nothing,
        OneConnection,
        SomeConnections,
        AnIncident,
    }

    /// <summary>One built-in template, as the picker lists it.</summary>
    public sealed record BuiltIn(string Key, string Name, string Description, Needs Needs);

    public static IReadOnlyList<BuiltIn> All { get; } =
    [
        new(Runbook, "Runbook for a connection",
            "Its live status, uptime and trend; what to do while it is down, with its own buttons; a checklist to fill in.",
            Needs.OneConnection),
        new(Incident, "Incident write-up",
            "What happened, what failed, the probable cause and the timeline, from what LabbyTwo recorded.",
            Needs.AnIncident),
        new(Maintenance, "Maintenance plan",
            "Who, when and why; a checklist for before, during and after; how to roll it back.",
            Needs.SomeConnections),
        new(Overview, "Service overview",
            "Several connections in one table: status, how long, uptime and a trend line each.",
            Needs.SomeConnections),
    ];

    /// <summary>A runbook for one connection.</summary>
    public static NoteDraft RunbookFor(TemplateConnection connection)
    {
        var name = connection.Name;
        var text = IncidentWriteUp.Text(name);
        var md = new StringBuilder();

        md.Append("**").Append(text).Append("** is ").Append(Shortcodes.Write("status", [name]))
            .Append(" — since ").Append(Shortcodes.Write("since", [name]))
            .Append(", up ").Append(Shortcodes.Write("uptime", [name]))
            .Append(" of the last 30 days, checked ").Append(Shortcodes.Write("ago", [name])).AppendLine(".")
            .AppendLine();
        md.Append(Sparkline(connection)).Append(' ')
            .Append(connection.Metric is null ? "Response time" : IncidentWriteUp.Text(connection.MetricLabel ?? connection.Metric))
            .AppendLine(" over the last 24 hours.").AppendLine();
        md.AppendLine(Shortcodes.Write("uptimebar", [name])).AppendLine();

        md.AppendLine(IfDown(name));
        md.AppendLine("> [!WARNING]");
        md.Append("> **").Append(text).AppendLine(" is down.** Work down the list; the buttons are the real ones, and ask before they do anything.");
        md.AppendLine();
        var actions = connection.Actions ?? [];
        if (actions.Count > 0)
            md.AppendLine(string.Join(" ", actions.Select(a => Shortcodes.Write("button", [name, a])))).AppendLine();
        md.AppendLine("1. Is it only this, or the machine it runs on? Check what else is down:").AppendLine();
        md.AppendLine(Shortcodes.Write("down", [])).AppendLine();
        md.AppendLine("2. What changed in the two hours before:").AppendLine();
        md.AppendLine(Changes("2h", [name])).AppendLine();
        md.AppendLine("{{else}}");
        md.Append(text).AppendLine(" is up. Nothing to do.");
        md.AppendLine("{{end}}").AppendLine();

        md.AppendLine(Shortcodes.Write("details", ["If it won't come back"]));
        md.AppendLine("1. Check the machine it runs on has power and is on the network.");
        md.AppendLine("2. Read its logs for the last thing it said before it stopped.");
        md.AppendLine("3. Restart it from the machine itself, not from here.");
        md.AppendLine("4. Write down what worked under **Steps**, for next time.");
        md.AppendLine("{{end}}").AppendLine();

        md.AppendLine("## Steps").AppendLine();
        md.AppendLine("- [ ] Make sure it is really down, not just slow");
        md.AppendLine("- [ ] Try the buttons above, one at a time");
        md.AppendLine("- [ ] Check the machine it runs on");
        md.AppendLine("- [ ] Write down what fixed it").AppendLine();

        md.AppendLine("## Links").AppendLine();
        if (connection.HasWebPage)
            md.Append("- Its own page: ").AppendLine(Shortcodes.Write("link", [name]));
        md.AppendLine("- Related notes: write `[[Note title]]` to link one.");

        return new NoteDraft($"{name} runbook", md.ToString().TrimEnd() + "\n");
    }

    /// <summary>A plan for a piece of maintenance, about the connections it touches (any number, none included).</summary>
    public static NoteDraft MaintenancePlan(IReadOnlyList<TemplateConnection> connections, DateOnly when)
    {
        var md = new StringBuilder();
        md.Append("- **When:** ").Append(when.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture)).Append(", ")
            .AppendLine(Shortcodes.Write("countdown", [when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)]));
        md.AppendLine("- **Who:** *who is doing it, and who to tell*");
        md.AppendLine("- **What and why:** *what is changing, and what it fixes*");
        md.AppendLine("- **How long it will be down:** *a guess is fine*").AppendLine();

        if (connections.Count > 0)
        {
            md.AppendLine("## What it touches").AppendLine();
            md.AppendLine("| Service | Now |");
            md.AppendLine("|---|---|");
            foreach (var connection in connections)
                md.Append("| ").Append(IncidentWriteUp.Text(connection.Name)).Append(" | ").Append(Shortcodes.Write("status", [connection.Name])).AppendLine(" |");
            md.AppendLine();
        }

        md.AppendLine("## Before").AppendLine();
        md.AppendLine("- [ ] Tell everyone who uses it when, and for how long");
        md.AppendLine("- [ ] Check the last backup worked");
        md.AppendLine("- [ ] Turn on maintenance mode in Settings, so the alerts are held");
        md.AppendLine("- [ ] Write down the versions and settings it has now").AppendLine();
        md.AppendLine(Shortcodes.Write("backups", ["late"])).AppendLine();

        md.AppendLine("## Steps").AppendLine();
        md.AppendLine("- [ ] *First step*");
        md.AppendLine("- [ ] *Second step*").AppendLine();

        md.AppendLine("## Rollback").AppendLine();
        md.AppendLine("If it goes wrong, how to put it back the way it was:").AppendLine();
        md.AppendLine("1. *Put the old version or settings back*");
        md.AppendLine("2. *Restart it*");
        md.AppendLine("3. *Check it answers*").AppendLine();

        md.AppendLine("## After").AppendLine();
        md.AppendLine("- [ ] Everything it touches is back up");
        md.AppendLine("- [ ] Maintenance mode is off");
        md.AppendLine("- [ ] Tell everyone it is done").AppendLine();
        md.AppendLine("{{if all up}}");
        md.AppendLine("Everything is up.");
        md.AppendLine("{{else}}");
        md.AppendLine(Shortcodes.Write("down", []));
        md.AppendLine("{{end}}");

        if (connections.Count > 0)
        {
            md.AppendLine();
            md.AppendLine(Changes("24h", [.. connections.Select(c => c.Name)]));
        }

        var title = $"Maintenance plan · {when.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}";
        return new NoteDraft(title, md.ToString().TrimEnd() + "\n");
    }

    /// <summary>A table of several connections, one row each, live.</summary>
    public static NoteDraft ServiceOverview(IReadOnlyList<TemplateConnection> connections)
    {
        var md = new StringBuilder();
        md.AppendLine("{{if all up}}");
        md.AppendLine("> [!TIP]");
        md.AppendLine("> Everything is up.");
        md.AppendLine("{{else}}");
        md.AppendLine(Shortcodes.Write("down", []));
        md.AppendLine("{{end}}").AppendLine();

        md.AppendLine("| Service | Status | Since | Uptime, 30 days | Last 24 hours |");
        md.AppendLine("|---|---|---|---|---|");
        foreach (var connection in connections)
        {
            var name = connection.Name;
            md.Append("| ");
            md.Append(connection.HasWebPage ? Shortcodes.Write("link", [name]) : IncidentWriteUp.Text(name));
            md.Append(" | ").Append(Shortcodes.Write("status", [name]))
                .Append(" | ").Append(Shortcodes.Write("since", [name]))
                .Append(" | ").Append(Shortcodes.Write("uptime", [name]))
                .Append(" | ").Append(Sparkline(connection)).AppendLine(" |");
        }
        if (connections.Count == 0)
            md.AppendLine("| *Add a row per service* | | | | |");
        md.AppendLine();
        md.AppendLine(Shortcodes.Write("alerts", []));

        return new NoteDraft("Service overview", md.ToString().TrimEnd() + "\n");
    }

    /// <summary>
    /// <c>{{if down: Name}}</c>, the name quoted exactly as the condition reader wants it —
    /// the same way the editor's insert helper writes one.
    /// </summary>
    public static string IfDown(string name) => "{{if down: " + Shortcodes.QuoteTarget(name) + "}}";

    private static string Sparkline(TemplateConnection connection) =>
        connection.Metric is { Length: > 0 } metric
            ? Shortcodes.Write("sparkline", [connection.Name, $"{metric} 24h"])
            : Shortcodes.Write("sparkline", [connection.Name], [new("window", "24h")]);

    /// <summary>
    /// <c>{{changes}}</c> over a window, limited to these connections. <c>only=</c> takes
    /// names separated by commas, so a name with a comma in it cannot be listed and is left
    /// out, as the incident write-up does.
    /// </summary>
    private static string Changes(string last, IReadOnlyList<string> names)
    {
        var only = names.Where(n => !n.Contains(',')).ToList();
        return Shortcodes.Write("changes", [], [new("last", last), new("only", string.Join(", ", only))]);
    }
}
