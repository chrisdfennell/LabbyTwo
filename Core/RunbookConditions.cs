using System.Globalization;
using System.Text.RegularExpressions;

namespace LabbyTwo.Core;

/// <summary>
/// The condition of an <c>{{if …}}</c> or <c>{{elif …}}</c>: one test, or tests joined with
/// <c>and</c>, <c>or</c>, <c>not</c> and brackets. Reading it is text work and lives here;
/// what each test says about the lab right now is answered by <c>RunbookFacts</c> from
/// memory, and handed back in through <see cref="Decide"/>.
/// </summary>
public abstract record RunbookExpr
{
    /// <summary>Every test in the condition, in the order written, repeats included.</summary>
    public abstract IEnumerable<RunbookCondition> Tests();

    /// <summary>Whether the condition holds, given the answer to each test.</summary>
    public abstract bool Holds(Func<RunbookCondition, bool> test);

    /// <summary>
    /// Whether the condition holds, asking <paramref name="test"/> about each test once.
    ///
    /// Every test is asked, not just as many as decide the answer. Stopping early would be
    /// quicker by a dictionary lookup and would make a typo come and go: in
    /// <c>maintenance or down: Plexx</c> the misspelt name would be pointed out only while
    /// nobody was doing maintenance, which is exactly when nobody is reading the page. A
    /// test that names nothing makes the whole condition unanswerable, and it says so.
    /// </summary>
    public (bool Holds, string? Problem) Decide(Func<RunbookCondition, (bool Holds, string? Problem)> test)
    {
        var answers = new Dictionary<RunbookCondition, bool>();
        string? problem = null;
        foreach (var each in Tests())
        {
            if (answers.ContainsKey(each))
                continue;
            var (holds, broken) = test(each);
            problem ??= broken;
            answers[each] = holds;
        }
        return problem is not null ? (false, problem) : (Holds(c => answers[c]), null);
    }
}

/// <summary>One test on its own: <c>down: NAS</c>.</summary>
public sealed record RunbookAtom(RunbookCondition Condition) : RunbookExpr
{
    public override IEnumerable<RunbookCondition> Tests() => [Condition];

    public override bool Holds(Func<RunbookCondition, bool> test) => test(Condition);
}

/// <summary><c>not …</c>.</summary>
public sealed record RunbookNot(RunbookExpr Operand) : RunbookExpr
{
    public override IEnumerable<RunbookCondition> Tests() => Operand.Tests();

    public override bool Holds(Func<RunbookCondition, bool> test) => !Operand.Holds(test);
}

/// <summary><c>… and …</c>.</summary>
public sealed record RunbookAnd(RunbookExpr Left, RunbookExpr Right) : RunbookExpr
{
    public override IEnumerable<RunbookCondition> Tests() => Left.Tests().Concat(Right.Tests());

    public override bool Holds(Func<RunbookCondition, bool> test) => Left.Holds(test) && Right.Holds(test);
}

/// <summary><c>… or …</c>.</summary>
public sealed record RunbookOr(RunbookExpr Left, RunbookExpr Right) : RunbookExpr
{
    public override IEnumerable<RunbookCondition> Tests() => Left.Tests().Concat(Right.Tests());

    public override bool Holds(Func<RunbookCondition, bool> test) => Left.Holds(test) || Right.Holds(test);
}

public static partial class Runbook
{
    /// <summary>
    /// How deep brackets and <c>not</c>s may go in one condition. Nobody writes ten; a
    /// thousand is a paste gone wrong, and a reason to stop rather than to recurse.
    /// </summary>
    public const int MaxConditionDepth = 16;

    /// <summary>
    /// Reads a whole condition — one test, or several joined with <c>and</c>, <c>or</c>,
    /// <c>not</c> and brackets. <c>not</c> binds tightest, then <c>and</c>, then
    /// <c>or</c>, as in every language that has them: <c>a or b and c</c> is
    /// <c>a or (b and c)</c>. Null, with the reason, when it cannot be read.
    ///
    /// A condition that was already one of the forms <see cref="ParseCondition"/> reads,
    /// and has no joining word or bracket in it, is read by exactly that, so every runbook
    /// written before this grammar existed means what it always did — including a name
    /// that happens to have "and" in it, <c>down: Sonarr and Radarr</c>, which the new
    /// reading would split. Such a name joined to something else goes in quotes.
    /// </summary>
    public static RunbookExpr? ParseExpression(string text, out string? problem)
    {
        var legacy = ParseCondition(text, out var legacyProblem);
        var joined = HasJoins(text);
        if (legacy is not null && !joined && !NamesTab(text))
        {
            problem = null;
            return new RunbookAtom(legacy);
        }

        var expression = new ConditionReader(text).Read(out var readProblem);
        if (expression is not null)
        {
            problem = null;
            return expression;
        }
        if (legacy is not null)
        {
            problem = null;
            return new RunbookAtom(legacy);
        }

        // The old messages for the old forms, word for word; the new reader's for anything
        // that only it could have meant.
        problem = !joined && !StartsWithNewWord(text) ? legacyProblem : readProblem;
        return null;
    }

    /// <summary>
    /// Whether text after <c>if</c> could be one of ours. Anything with a colon is taken, so
    /// a mistake in it is pointed out; anything else only if it reads — <c>maintenance</c>,
    /// <c>not blind</c> — so Go's <c>{{if .Ready}}</c> or <c>{{if not .Ready}}</c> in a pasted
    /// template stays the text it was.
    /// </summary>
    public static bool LooksLikeCondition(string text) =>
        text.Contains(':') || IsWholeLabCondition(text) || ParseExpression(text, out _) is not null;

    /// <summary>Outside quotes: a bracket, or one of the words and / or / not / between standing alone.</summary>
    private static bool HasJoins(string text)
    {
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '\\')
                    i++;
                else if (c == '"')
                    quoted = false;
                continue;
            }
            if (c == '"')
            {
                quoted = true;
                continue;
            }
            if (c is '(' or ')')
                return true;
            if (char.IsLetter(c) && (i == 0 || !char.IsLetterOrDigit(text[i - 1])))
            {
                var end = i;
                while (end < text.Length && char.IsLetter(text[end]))
                    end++;
                if (text[i..end].ToLowerInvariant() is "and" or "or" or "not" or "between")
                    return true;
                i = end - 1;
            }
        }
        return false;
    }

    /// <summary><c>down: tab Media</c> or <c>up: tab "Media"</c>, which the old reading took for a connection called "tab Media".</summary>
    private static bool NamesTab(string text) => TabTarget().IsMatch(text);

    [GeneratedRegex(@"^\s*(?:down|up)\s*:\s*tab\s+\S", RegexOptions.IgnoreCase)]
    private static partial Regex TabTarget();

    private static bool StartsWithNewWord(string text) => NewWord().IsMatch(text);

    [GeneratedRegex(@"^\s*(?:alerts?|any\s+alerts?|maintenance|blind|backups?|incidents?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NewWord();

    /// <summary>
    /// A small recursive-descent reader over the condition's text. Names are read the way
    /// the shortcode grammar reads them — quoted or not, slashes between a connection and
    /// its metric — so a name is written the same in <c>{{if down: …}}</c> as in
    /// <c>{{status: …}}</c>. An unquoted name runs until a joining word or a closing
    /// bracket; one that contains either goes in quotes.
    /// </summary>
    private sealed partial class ConditionReader(string text)
    {
        private int _at;
        private int _depth;
        private int _brackets;
        private string? _problem;

        public RunbookExpr? Read(out string? problem)
        {
            problem = null;
            SkipSpace();
            if (_at >= text.Length)
            {
                problem = "Say what to check: {{if down: NAS}}.";
                return null;
            }
            var expression = Or();
            if (expression is not null)
            {
                SkipSpace();
                if (_at < text.Length)
                {
                    expression = null;
                    _problem ??= text[_at] == ')'
                        ? "A closing bracket with no opening one before it."
                        : $"“{text[_at..].Trim()}” is left over — join two conditions with and or or.";
                }
            }
            problem = expression is null ? _problem ?? $"“{text.Trim()}” is not a condition. Use {Forms}." : null;
            return expression;
        }

        private RunbookExpr? Fail(string problem)
        {
            _problem ??= problem;
            return null;
        }

        private RunbookExpr? Or()
        {
            var left = And();
            while (left is not null && Word("or"))
            {
                var right = And();
                if (right is null)
                    return null;
                left = new RunbookOr(left, right);
            }
            return left;
        }

        private RunbookExpr? And()
        {
            var left = Unary();
            while (left is not null && Word("and"))
            {
                var right = Unary();
                if (right is null)
                    return null;
                left = new RunbookAnd(left, right);
            }
            return left;
        }

        private RunbookExpr? Unary()
        {
            if (++_depth > MaxConditionDepth)
                return Fail($"Brackets and nots more than {MaxConditionDepth} deep are not read.");
            try
            {
                SkipSpace();
                if (_at >= text.Length)
                    return Fail("Something is missing at the end — a condition after and, or, not or a bracket.");

                if (text[_at] == '(')
                {
                    _at++;
                    _brackets++;
                    var inner = Or();
                    if (inner is null)
                        return null;
                    SkipSpace();
                    if (_at >= text.Length || text[_at] != ')')
                        return Fail("A bracket is opened and never closed.");
                    _at++;
                    _brackets--;
                    return inner;
                }

                if (Word("not"))
                {
                    var operand = Unary();
                    return operand is null ? null : new RunbookNot(operand);
                }

                return Test();
            }
            finally
            {
                _depth--;
            }
        }

        // ---------- one test ----------

        [GeneratedRegex(@"\G(?<head>down|up|alert\s+on|alerts?|backups?\s+late|metric)\s*:", RegexOptions.IgnoreCase)]
        private static partial Regex Head();

        [GeneratedRegex(@"\G(?<word>any\s+down|all\s+up|any\s+alerts?|maintenance|blind|incidents?\s+open|backups?\s+late)(?=\s|\)|$)", RegexOptions.IgnoreCase)]
        private static partial Regex Phrase();

        [GeneratedRegex(@"\G\s*(?<num>[-+]?(?:\d+(?:\.\d*)?|\.\d+))")]
        private static partial Regex Number();

        [GeneratedRegex(@"^tab\s+(?<name>.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
        private static partial Regex TabName();

        private RunbookExpr? Test()
        {
            var head = Head().Match(text, _at);
            if (head.Success)
            {
                _at += head.Length;
                var word = string.Join(' ', head.Groups["head"].Value.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
                return word switch
                {
                    "down" or "up" => UpOrDown(word),
                    "alert" or "alerts" => Named(RunbookTest.Alert, "Say which alert rule: {{if alert: \"Disk almost full\"}}."),
                    "alert on" => Named(RunbookTest.AlertOn, "Say which connection: {{if alert on: NAS}}."),
                    "metric" => Metric(),
                    _ => Named(RunbookTest.BackupLate, "Say which backup: {{if backup late: Photos}}, or leave the colon off for any."),
                };
            }

            var phrase = Phrase().Match(text, _at);
            if (phrase.Success && !FollowedByColon(_at + phrase.Length))
            {
                _at += phrase.Length;
                var words = string.Join(' ', phrase.Groups["word"].Value.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                return new RunbookAtom(new RunbookCondition(words switch
                {
                    "any down" => RunbookTest.AnyDown,
                    "all up" => RunbookTest.AllUp,
                    "any alert" or "any alerts" => RunbookTest.AnyAlert,
                    "maintenance" => RunbookTest.Maintenance,
                    "blind" => RunbookTest.Blind,
                    "incident open" or "incidents open" => RunbookTest.IncidentOpen,
                    _ => RunbookTest.BackupLate,
                }));
            }

            var unknown = Argument();
            return Fail(unknown.Length == 0
                ? $"Something is missing — a condition, such as {Forms}."
                : $"“{unknown}” is not a condition. Use {Forms}.");
        }

        private bool FollowedByColon(int at)
        {
            while (at < text.Length && char.IsWhiteSpace(text[at]))
                at++;
            return at < text.Length && text[at] == ':';
        }

        private RunbookExpr? UpOrDown(string word)
        {
            var raw = Argument();
            if (TabName().Match(raw) is { Success: true } tab)
            {
                var name = OnePart(tab.Groups["name"].Value);
                if (name is null)
                    return Fail("Name one tab: {{if down: tab \"Media\"}}.");
                return new RunbookAtom(new RunbookCondition(word == "down" ? RunbookTest.DownTab : RunbookTest.UpTab, name));
            }
            var parts = Parts(raw);
            if (parts.Count != 1)
            {
                return Fail(parts.Count == 0
                    ? $"Say which connection: {{{{if {word}: NAS}}}}."
                    : "One connection per test; a name with a slash in it goes in quotes.");
            }
            return new RunbookAtom(new RunbookCondition(word == "down" ? RunbookTest.Down : RunbookTest.Up, parts[0]));
        }

        private RunbookExpr? Named(RunbookTest test, string missing)
        {
            var raw = Argument();
            if (raw.Length == 0)
                return Fail(missing);
            var name = OnePart(raw);
            return name is null
                ? Fail("One name per test; a name with a slash in it goes in quotes.")
                : new RunbookAtom(new RunbookCondition(test, name));
        }

        private RunbookExpr? Metric()
        {
            var operatorAt = FindComparison(out var op, out var opLength);
            if (operatorAt < 0)
                return Fail("Compare the metric with a number: {{if metric: NAS / disk_percent > 90}}. Use > >= < <= = != or between 40 and 60.");

            var parts = Parts(text[_at..operatorAt]);
            if (parts.Count != 2)
                return Fail("Name a connection and a metric: {{if metric: NAS / disk_percent > 90}}.");
            _at = operatorAt + opLength;

            if (ReadNumber() is not { } value)
                return Fail($"Compare the metric with a number: “{op}” needs a number after it.");

            if (op != "between")
            {
                var unit = Argument();
                return new RunbookAtom(new RunbookCondition(RunbookTest.Metric, parts[0], parts[1], op == "=" ? "==" : op, value, unit));
            }

            var lowUnit = Argument();
            if (!Word("and") || ReadNumber() is not { } high)
                return Fail("Write a range as between 40 and 60, with a unit after either number if it needs one.");
            var highUnit = Argument();
            if (lowUnit.Length > 0 && highUnit.Length > 0
                && !string.Equals(lowUnit.Replace(" ", ""), highUnit.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
            {
                return Fail($"Write both ends of the range in one unit, not {lowUnit} and {highUnit}.");
            }
            // "between 60 and 40" can only mean one thing.
            var (low, top) = value <= high ? (value, high) : (high, value);
            return new RunbookAtom(new RunbookCondition(RunbookTest.Metric, parts[0], parts[1], "between", low,
                lowUnit.Length > 0 ? lowUnit : highUnit, top));
        }

        /// <summary>
        /// Where the comparison is, outside quotes: an operator, or the word "between". The
        /// first one found is the comparison, as in the original reading, so the metric's
        /// name may not contain one unquoted.
        /// </summary>
        private int FindComparison(out string op, out int length)
        {
            var quoted = false;
            for (var i = _at; i < text.Length; i++)
            {
                var c = text[i];
                if (quoted)
                {
                    if (c == '\\')
                        i++;
                    else if (c == '"')
                        quoted = false;
                    continue;
                }
                if (c == '"')
                {
                    quoted = true;
                    continue;
                }
                foreach (var candidate in Operators)
                {
                    if (string.CompareOrdinal(text, i, candidate, 0, candidate.Length) == 0)
                    {
                        op = candidate;
                        length = candidate.Length;
                        return i;
                    }
                }
                if (IsWordAt(i, "between"))
                {
                    op = "between";
                    length = "between".Length;
                    return i;
                }
            }
            op = "";
            length = 0;
            return -1;
        }

        /// <summary>Longest first, so "&gt;=" is not read as "&gt;" followed by "=".</summary>
        private static readonly string[] Operators = [">=", "<=", "==", "!=", ">", "<", "="];

        private double? ReadNumber()
        {
            var match = Number().Match(text, _at);
            if (!match.Success)
                return null;
            _at += match.Length;
            return double.Parse(match.Groups["num"].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The text from here to the next joining word, the closing bracket of an open one,
        /// or the end — skipping anything quoted. Trimmed.
        /// </summary>
        private string Argument()
        {
            SkipSpace();
            var start = _at;
            var quoted = false;
            var i = _at;
            for (; i < text.Length; i++)
            {
                var c = text[i];
                if (quoted)
                {
                    if (c == '\\')
                        i++;
                    else if (c == '"')
                        quoted = false;
                    continue;
                }
                if (c == '"')
                {
                    quoted = true;
                    continue;
                }
                if (c == ')' && _brackets > 0)
                    break;
                if ((i == start || char.IsWhiteSpace(text[i - 1])) && (IsWordAt(i, "and") || IsWordAt(i, "or")))
                    break;
            }
            _at = Math.Min(i, text.Length);
            return text[start.._at].Trim();
        }

        /// <summary>The joining word, standing alone, at <see cref="_at"/> (after spaces) — consumed if so.</summary>
        private bool Word(string word)
        {
            SkipSpace();
            if (!IsWordAt(_at, word))
                return false;
            _at += word.Length;
            return true;
        }

        /// <summary>
        /// <paramref name="word"/> at <paramref name="at"/>, as a word: nothing letter-like
        /// right before or after it, so "android" is not "and" and "notify" is not "not".
        /// </summary>
        private bool IsWordAt(int at, string word)
        {
            if (at < 0 || at + word.Length > text.Length)
                return false;
            if (string.Compare(text, at, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0)
                return false;
            if (at > 0 && (char.IsLetterOrDigit(text[at - 1]) || text[at - 1] is '_' or '"'))
                return false;
            var after = at + word.Length;
            return after == text.Length || char.IsWhiteSpace(text[after]) || text[after] is '(' or ')';
        }

        private void SkipSpace()
        {
            while (_at < text.Length && char.IsWhiteSpace(text[_at]))
                _at++;
        }

        /// <summary>The parts of a name, read with the shortcode grammar.</summary>
        private static IReadOnlyList<string> Parts(string raw) =>
            raw.Trim().Length == 0 ? [] : Shortcodes.Parse("{{x:" + raw + "}}")?.Target ?? [];

        private static string? OnePart(string raw) => Parts(raw) is [var only] ? only : null;
    }
}
