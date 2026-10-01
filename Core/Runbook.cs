using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LabbyTwo.Core;

/// <summary>What a <c>{{if …}}</c> asks about.</summary>
public enum RunbookTest
{
    /// <summary><c>down: NAS</c> — that connection's last verdict is down.</summary>
    Down,

    /// <summary><c>up: NAS</c> — that connection's last verdict is up.</summary>
    Up,

    /// <summary><c>any down</c> — at least one monitored connection is down.</summary>
    AnyDown,

    /// <summary><c>all up</c> — every monitored connection is up, none still checking.</summary>
    AllUp,

    /// <summary><c>metric: NAS / disk_percent &gt; 90</c> — a reading compared with a number, or <c>between 40 and 60</c>.</summary>
    Metric,

    /// <summary><c>down: tab "Media"</c> — at least one monitored connection on that tab is down.</summary>
    DownTab,

    /// <summary><c>up: tab "Media"</c> — every monitored connection on that tab is up, none still checking.</summary>
    UpTab,

    /// <summary><c>alert: "Disk almost full"</c> — that alert rule is firing, on any connection.</summary>
    Alert,

    /// <summary><c>any alert</c> — any alert rule is firing.</summary>
    AnyAlert,

    /// <summary><c>alert on: NAS</c> — any alert rule is firing on that connection.</summary>
    AlertOn,

    /// <summary><c>maintenance</c> — every alert is being held because somebody said they are working on the lab.</summary>
    Maintenance,

    /// <summary><c>blind</c> — LabbyTwo cannot see the lab (its own DNS, Docker or database), so failures are held.</summary>
    Blind,

    /// <summary><c>backup late</c>, or <c>backup late: Photos</c> for one — a backup is late or has never been made.</summary>
    BackupLate,

    /// <summary><c>incident open</c> — an incident is still going on.</summary>
    IncidentOpen,
}

/// <summary>
/// The condition of one <c>{{if …}}</c>, as written. Reading it is text work and lives here;
/// deciding whether it holds needs the monitor and lives in <c>RunbookFacts</c>.
/// </summary>
/// <param name="Connection">
/// The connection named — or, for the tests that name something else, the tab
/// (<see cref="RunbookTest.DownTab"/>), the alert rule (<see cref="RunbookTest.Alert"/>) or
/// the backup (<see cref="RunbookTest.BackupLate"/>, empty for "any"). Empty for the
/// whole-lab tests. One field rather than four, because each test names exactly one thing
/// and every message about it says "No … called “that”".
/// </param>
/// <param name="Metric">The metric's key or label, for <see cref="RunbookTest.Metric"/>.</param>
/// <param name="Operator">One of <c>&gt; &gt;= &lt; &lt;= == !=</c>, or <c>between</c>. A single <c>=</c> is read as <c>==</c>.</param>
/// <param name="Value">The number compared against, as written — in <paramref name="Unit"/> when there is one, otherwise the metric's stored unit.</param>
/// <param name="Unit">
/// A unit written after the number, if any: the metric's own, or another of the same kind
/// (°F for a metric stored in °C), which the number is converted from before comparing.
/// For <c>between</c>, the unit of both ends.
/// </param>
/// <param name="High">The upper end of <c>between</c>; <paramref name="Value"/> is the lower. Both are included.</param>
public sealed record RunbookCondition(
    RunbookTest Test,
    string Connection = "",
    string Metric = "",
    string Operator = "",
    double Value = 0,
    string Unit = "",
    double High = 0)
{
    /// <summary>Whether <paramref name="reading"/> passes the comparison.</summary>
    public bool Compare(double reading) => Operator switch
    {
        ">" => reading > Value,
        ">=" => reading >= Value,
        "<" => reading < Value,
        "<=" => reading <= Value,
        // Readings are doubles that went through a provider's arithmetic, so "== 0" on a
        // count must not fail over the last bit of a float.
        "==" => Math.Abs(reading - Value) < 1e-9,
        "!=" => Math.Abs(reading - Value) >= 1e-9,
        // Inclusive at both ends, the way people say it: "between 40 and 60" includes 60.
        "between" => reading >= Value - 1e-9 && reading <= High + 1e-9,
        _ => false,
    };
}

/// <summary>A piece of a runbook: Markdown, a section shown only sometimes, or a note about a mistake.</summary>
public abstract record RunbookPart;

/// <summary>Markdown to render as it is, shortcodes and all.</summary>
public sealed record RunbookText(string Markdown) : RunbookPart;

/// <summary>A mistake in the structure — an <c>{{end}}</c> with nothing to end — shown where it was.</summary>
public sealed record RunbookProblem(string Message) : RunbookPart;

/// <summary>
/// <c>{{if …}}</c> … <c>{{else}}</c> … <c>{{end}}</c>. An <c>{{elif …}}</c> is read as an
/// <c>{{if}}</c> that is the whole of the one before's "otherwise" part, which is what it
/// means, so nothing after the parser has to know else-if exists.
/// </summary>
/// <param name="Source">The <c>{{if …}}</c> (or <c>{{elif …}}</c>) line as written, for messages.</param>
/// <param name="Expression">Null when it could not be read; <paramref name="Problem"/> says why.</param>
/// <param name="Problem">Why neither branch can be shown. Drawn as a "?" in their place.</param>
/// <param name="Notes">
/// Mistakes inside the section (a second <c>{{else}}</c>) that must be visible whichever
/// branch is showing — a note inside a hidden branch would be a note nobody sees.
/// </param>
public sealed record RunbookIf(
    string Source,
    RunbookExpr? Expression,
    string? Problem,
    IReadOnlyList<RunbookPart> Then,
    IReadOnlyList<RunbookPart> Else,
    IReadOnlyList<string> Notes) : RunbookPart
{
    /// <summary>The single test, when the condition is one test and not a combination of them.</summary>
    public RunbookCondition? Condition => (Expression as RunbookAtom)?.Condition;
}

/// <summary>
/// <c>{{details: Title}}</c> … <c>{{end}}</c>: a part folded away under a title until
/// somebody opens it — the long restart procedure under the short "what to check first".
/// </summary>
/// <param name="Source">The <c>{{details: …}}</c> line as written, for messages.</param>
/// <param name="Title">What the fold says when it is closed. Text, never markup.</param>
/// <param name="Open">Whether it starts open, from <c>open=true</c>.</param>
public sealed record RunbookDetails(string Source, string Title, bool Open, IReadOnlyList<RunbookPart> Body) : RunbookPart;

/// <summary>
/// Turns Markdown with <c>{{if …}}</c> / <c>{{else}}</c> / <c>{{end}}</c> lines into
/// sections, before any of it is rendered.
///
/// Before, because the alternative — render it all and hide what does not apply — puts the
/// hidden branch in the page anyway, where it can be read, searched and copied; and because
/// Markdown has no idea these lines are special, so a section cut out of the renderer's HTML
/// could end halfway through a list. Cutting the source instead means each piece is complete
/// Markdown on its own, which is also the rule for writing them: a condition goes around
/// whole paragraphs, lists and tables, never through the middle of one.
///
/// Nothing here throws, and nothing is hidden by a typo: an <c>{{if}}</c> never closed shows
/// everything after it with a note saying so, and an <c>{{end}}</c> or <c>{{else}}</c> with
/// nothing to belong to is a note where it stands.
///
/// <c>{{details: …}}</c> is cut out the same way and for the same reason — a fold has to
/// hold whole blocks — and shares <c>{{end}}</c> with <c>{{if}}</c>. An <c>{{end}}</c> closes
/// whichever of the two was opened last, the way a closing bracket does, so an
/// <c>{{if}}</c> inside a fold and a fold inside an <c>{{if}}</c> both read as written.
/// </summary>
public static partial class Runbook
{
    /// <summary>
    /// How deep sections may nest. Three is a runbook with a case inside a case inside a
    /// case; eight is either a generated page or a mistake, and either way not a reason to
    /// recurse without limit.
    /// </summary>
    public const int MaxDepth = 8;

    /// <summary>The conditions, in the words the "?" notes and the editor use.</summary>
    public const string Forms = "down: NAS, up: NAS, any down, all up, metric: NAS / disk_percent > 90, down: tab \"Media\", alert: \"Disk almost full\", any alert, alert on: NAS, maintenance, blind, backup late, incident open — joined with and, or, not and brackets";

    // Up to three spaces, as Markdown allows before a heading; four would be code.
    [GeneratedRegex(@"^ {0,3}\{\{")]
    private static partial Regex DirectiveStart();

    [GeneratedRegex(@"^(?<lhs>.+?)\s*(?<op>>=|<=|==|!=|>|<)\s*(?<num>[-+]?(?:\d+(?:\.\d*)?|\.\d+))\s*(?<unit>\S.*?)?\s*$")]
    private static partial Regex MetricComparison();

    /// <summary>"any down" or "all up", spacing and case forgiven.</summary>
    public static bool IsWholeLabCondition(string text) => WholeLab(text) is not null;

    private static RunbookTest? WholeLab(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant() switch
        {
            "any down" => RunbookTest.AnyDown,
            "all up" => RunbookTest.AllUp,
            _ => null,
        };

    /// <summary>A cheap look for anything that could be a section line, so plain notes skip the work.</summary>
    public static bool MayHaveSections(string? markdown) =>
        markdown is not null && markdown.Contains("{{", StringComparison.Ordinal) && SectionWord().IsMatch(markdown);

    [GeneratedRegex(@"\{\{\s*(?:if\s|elif\s|details\s*:|else\s*\}\}|end\s*\}\})", RegexOptions.IgnoreCase)]
    private static partial Regex SectionWord();

    /// <summary>
    /// Reads the condition written after <c>if</c>. Null, with the reason, when it is not
    /// one of the forms in <see cref="Forms"/>.
    /// </summary>
    public static RunbookCondition? ParseCondition(string text, out string? problem)
    {
        problem = null;
        var condition = text.Trim();
        if (WholeLab(condition) is { } whole)
            return new RunbookCondition(whole);

        var colon = condition.IndexOf(':');
        var word = colon < 0 ? "" : condition[..colon].Trim().ToLowerInvariant();
        var rest = colon < 0 ? "" : condition[(colon + 1)..];

        switch (word)
        {
            case "down" or "up":
            {
                // Read with the shortcode grammar, so a name is quoted or not exactly as it
                // would be in {{status: …}}.
                var parts = Shortcodes.Parse("{{x:" + rest + "}}")?.Target ?? [];
                if (parts.Count != 1)
                {
                    problem = parts.Count == 0
                        ? $"Say which connection: {{{{if {word}: NAS}}}}."
                        : $"One connection per condition; a name with a slash in it goes in quotes.";
                    return null;
                }
                return new RunbookCondition(word == "down" ? RunbookTest.Down : RunbookTest.Up, parts[0]);
            }

            case "metric":
            {
                var match = MetricComparison().Match(rest);
                if (!match.Success)
                {
                    problem = "Compare the metric with a number: {{if metric: NAS / disk_percent > 90}}. Use > >= < <= == or !=.";
                    return null;
                }
                var parts = Shortcodes.Parse("{{x:" + match.Groups["lhs"].Value + "}}")?.Target ?? [];
                if (parts.Count != 2)
                {
                    problem = "Name a connection and a metric: {{if metric: NAS / disk_percent > 90}}.";
                    return null;
                }
                var value = double.Parse(match.Groups["num"].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
                return new RunbookCondition(RunbookTest.Metric, parts[0], parts[1], match.Groups["op"].Value, value,
                    match.Groups["unit"].Value.Trim());
            }

            default:
                problem = $"“{condition}” is not a condition. Use {Forms}.";
                return null;
        }
    }

    /// <summary>
    /// The sections of <paramref name="markdown"/>. <paramref name="code"/> is where its code
    /// spans and blocks are, as [start, end) offsets: a section line inside a fence is an
    /// example, not an instruction.
    /// </summary>
    public static IReadOnlyList<RunbookPart> Parse(string markdown, IReadOnlyList<(int Start, int End)> code)
    {
        var root = new List<RunbookPart>();
        var open = new Stack<Section>();
        var text = new StringBuilder();

        List<RunbookPart> Current() => open.Count == 0 ? root : open.Peek().Branch;

        void Flush()
        {
            if (text.Length == 0)
                return;
            var chunk = text.ToString();
            text.Clear();
            if (!string.IsNullOrWhiteSpace(chunk))
                Current().Add(new RunbookText(chunk));
        }

        var at = 0;
        var lineNumber = 0;
        while (at < markdown.Length)
        {
            lineNumber++;
            var newline = markdown.IndexOf('\n', at);
            var end = newline < 0 ? markdown.Length : newline + 1;
            var line = markdown[at..end];

            if (Directive(line, at, code) is { } directive)
            {
                Flush();
                switch (directive.Kind)
                {
                    case "if":
                    {
                        var source = directive.Source.Trim();
                        string? problem;
                        RunbookExpr? condition;
                        if (Depth(open) >= MaxDepth)
                        {
                            condition = null;
                            problem = $"Sections nested more than {MaxDepth} deep are not shown. ({source})";
                        }
                        else
                        {
                            condition = ParseExpression(directive.Part(0), out problem);
                            if (problem is not null)
                                problem += $" ({source})";
                        }
                        open.Push(Section.If(source, condition, problem, lineNumber));
                        break;
                    }

                    case "elif":
                    {
                        // "Otherwise, if …": the section so far takes everything from here as
                        // its otherwise part, and that part is a new section of its own —
                        // marked as a link in the chain, so the one {{end}} closes them all.
                        var source = directive.Source.Trim();
                        if (open.Count == 0)
                        {
                            root.Add(new RunbookProblem($"{source} on line {lineNumber} has no {{{{if …}}}} above it."));
                            break;
                        }
                        var owner = open.Peek();
                        if (owner.IsDetails)
                        {
                            Current().Add(new RunbookProblem(
                                $"{source} on line {lineNumber} is inside {owner.Source}, which has no “otherwise” — close the fold with {{{{end}}}} first."));
                            break;
                        }
                        if (owner.InElse)
                        {
                            // After the {{else}} there is no "otherwise" left to narrow. Said on
                            // the section, which shows whichever part is drawn.
                            owner.Notes.Add($"{source} on line {lineNumber} comes after {{{{else}}}}, so it is ignored; put it before the {{{{else}}}}.");
                            break;
                        }
                        if (owner.Links >= MaxChain)
                        {
                            owner.Notes.Add($"More than {MaxChain} {{{{elif}}}}s in one section; {source} on line {lineNumber} and everything after it are the “otherwise” part.");
                            owner.InElse = true;
                            break;
                        }

                        var condition = ParseExpression(directive.Part(0), out var problem);
                        if (problem is not null)
                            problem += $" ({source})";
                        owner.InElse = true;
                        open.Push(Section.If(source, condition, problem, lineNumber, chained: true, links: owner.Links + 1));
                        break;
                    }

                    case "details":
                    {
                        var source = directive.Source.Trim();
                        var title = string.Join(" / ", directive.Target);
                        // Too deep is not a reason to hide anything: a fold is only a
                        // convenience, so its contents are shown unfolded with a note.
                        var problem = Depth(open) >= MaxDepth
                            ? $"Sections nested more than {MaxDepth} deep are not folded. ({source})"
                            : null;
                        open.Push(Section.Details(source, title.Length > 0 ? title : "Details",
                            IsYes(directive.Option("open")), problem, lineNumber));
                        break;
                    }

                    case "else":
                        if (open.Count == 0)
                            root.Add(new RunbookProblem($"{{{{else}}}} on line {lineNumber} has no {{{{if …}}}} above it."));
                        else if (open.Peek().IsDetails)
                            // Said inside the fold, where it was written. Taking it as the
                            // "otherwise" of an {{if}} further out would quietly move the
                            // fold's end to wherever that guess put it.
                            Current().Add(new RunbookProblem(
                                $"{{{{else}}}} on line {lineNumber} is inside {open.Peek().Source}, which has no “otherwise” — close the fold with {{{{end}}}} first."));
                        else if (open.Peek().InElse)
                            open.Peek().Notes.Add($"A second {{{{else}}}} on line {lineNumber}; everything after the first is the “otherwise” part.");
                        else
                            open.Peek().InElse = true;
                        break;

                    case "end":
                        if (open.Count == 0)
                        {
                            root.Add(new RunbookProblem($"{{{{end}}}} on line {lineNumber} has no {{{{if …}}}} or {{{{details: …}}}} to end."));
                        }
                        else
                        {
                            var closed = open.Pop();
                            Current().AddRange(closed.ToParts());
                            // An {{elif}} is closed by the same {{end}} as the {{if}} it
                            // continues, so the whole chain is closed here, innermost first.
                            while (closed.Chained && open.Count > 0)
                            {
                                closed = open.Pop();
                                Current().AddRange(closed.ToParts());
                            }
                        }
                        break;
                }
            }
            else
            {
                text.Append(line);
            }
            at = end;
        }
        Flush();

        // Never closed: shown whole, with a note, rather than hiding the rest of the page
        // behind a condition the author did not finish writing.
        while (open.Count > 0)
        {
            var unclosed = open.Pop();
            var parent = Current();
            // A link of an {{elif}} chain is not separately unclosed: the {{if}} that started
            // the chain says so once, and everything in every link follows it.
            if (!unclosed.Chained)
            {
                parent.Add(new RunbookProblem(
                    $"{unclosed.Source} on line {unclosed.Line} is never closed with {{{{end}}}}, so everything after it is shown."));
            }
            parent.AddRange(unclosed.Then);
            parent.AddRange(unclosed.Else);
        }

        return root;
    }

    /// <summary>
    /// How many <c>{{elif}}</c>s one section may have. Each is drawn as a section inside the
    /// last one's "otherwise", so a chain is a nesting too — but one a person writes on
    /// purpose, a case per line, so it is not held to <see cref="MaxDepth"/>.
    /// </summary>
    public const int MaxChain = 32;

    /// <summary>How deeply nested the next section would be, counting an <c>{{elif}}</c> chain as the one section it is.</summary>
    private static int Depth(Stack<Section> open) => open.Count(s => !s.Chained);

    /// <summary>"true", "yes", "1", "on" or "open", as an option that switches something on.</summary>
    public static bool IsYes(string value) =>
        value.Trim().ToLowerInvariant() is "true" or "yes" or "1" or "open" or "on";

    /// <summary>
    /// The shortcode a line consists of, if it is a section line: one of if / details / else / end,
    /// alone on the line apart from spaces, not indented into code, not inside a code block.
    /// </summary>
    private static Shortcode? Directive(string line, int offset, IReadOnlyList<(int Start, int End)> code)
    {
        if (!DirectiveStart().IsMatch(line))
            return null;
        var trimmed = line.Trim();
        var found = Shortcodes.Find(trimmed);
        if (found is not [{ Index: 0 } only] || only.Length != trimmed.Length || !only.Code.IsStructure)
            return null;
        var start = offset + line.IndexOf("{{", StringComparison.Ordinal);
        if (code.Any(r => start < r.End && r.Start <= start))
            return null;
        return only.Code;
    }

    /// <summary>An <c>{{if}}</c> or a <c>{{details}}</c> still waiting for its <c>{{end}}</c>.</summary>
    private sealed class Section
    {
        private RunbookExpr? _condition;
        private string? _problem;
        private string _title = "";
        private bool _open;

        private Section(string source, int line, bool isDetails)
        {
            Source = source;
            Line = line;
            IsDetails = isDetails;
        }

        public static Section If(string source, RunbookExpr? condition, string? problem, int line, bool chained = false, int links = 0) =>
            new(source, line, isDetails: false) { _condition = condition, _problem = problem, Chained = chained, Links = links };

        public static Section Details(string source, string title, bool open, string? problem, int line) =>
            new(source, line, isDetails: true) { _title = title, _open = open, _problem = problem };

        public string Source { get; }
        public int Line { get; }
        public bool IsDetails { get; }

        /// <summary>Opened by an <c>{{elif}}</c>: the "otherwise" of the section under it, closed by the same <c>{{end}}</c>.</summary>
        public bool Chained { get; private init; }

        /// <summary>How many <c>{{elif}}</c>s came before this one in its chain.</summary>
        public int Links { get; private init; }

        public List<RunbookPart> Then { get; } = [];
        public List<RunbookPart> Else { get; } = [];
        public List<string> Notes { get; } = [];
        public bool InElse { get; set; }
        public List<RunbookPart> Branch => InElse ? Else : Then;

        /// <summary>
        /// What the closed section becomes in its parent: one part, except a fold nested too
        /// deep to fold, which is its note followed by its contents.
        /// </summary>
        public IEnumerable<RunbookPart> ToParts()
        {
            if (!IsDetails)
                return [new RunbookIf(Source, _condition, _problem, Then, Else, Notes)];
            if (_problem is not null)
                return [new RunbookProblem(_problem), .. Then];
            return [new RunbookDetails(Source, _title, _open, Then)];
        }
    }
}
