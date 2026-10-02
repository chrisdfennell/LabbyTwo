using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LabbyTwo.Core;

/// <summary>
/// The rules for the owner's own stylesheet — Appearance → Advanced → Custom CSS.
///
/// Unlike a theme, which is colour data rebuilt from parsed values (see ThemeCss), this is
/// text somebody typed, written into the page as it was typed. It is the owner's, and it
/// only reaches the owner's own pages, so the point of these rules is not to stop the owner
/// styling things — anything a selector can reach is fair game — but to keep two promises
/// the rest of the app makes:
///
/// <list type="bullet">
/// <item><b>The page stays a page.</b> The text sits inside a &lt;style&gt; element, and the
/// HTML parser ends that element at the first <c>&lt;/style</c> whatever CSS thinks of it —
/// anything after would be markup. So that sequence is refused on save and neutralised again
/// on the way out (<see cref="ForStyleElement"/>), for a row somebody edited by hand.</item>
/// <item><b>Nothing phones home.</b> LabbyTwo draws itself without reaching outside the
/// house (the one opt-in exception is a web font). A stylesheet can fetch: <c>@import</c>,
/// <c>url()</c> on a background or a font, and the bare strings image-set() takes. So
/// <c>@import</c> is refused outright, and every address must be either on this dashboard
/// (a path, like <c>/icon.svg</c>) or a <c>data:</c> URL no bigger than
/// <see cref="MaxDataUrlBytes"/>. An absolute http(s) address is refused even when it
/// names this dashboard, because the server cannot know every name it is reached by — a
/// path does the same job and cannot be wrong.</item>
/// </list>
///
/// The check reads the CSS the way a browser would before judging it: comments are dropped
/// and backslash escapes decoded, so <c>@\69mport</c> and <c>u\72l(</c> are what they are.
/// Where it cannot be sure it errs towards refusing — <c>content: "https://…"</c> is
/// refused although it would only have been text — and says why, so the fix is obvious.
///
/// Mismatched braces are a warning rather than a refusal: the browser recovers from them by
/// dropping what it cannot read, which is a broken look and not a broken page, and a half-
/// finished stylesheet is worth being able to save.
/// </summary>
public static partial class CustomCss
{
    /// <summary>The stylesheet itself, in app settings — so it is cached with them and travels with a backup.</summary>
    public const string CssKey = "custom_css";

    /// <summary>The big switch. "0" leaves the CSS saved but out of every page.</summary>
    public const string EnabledKey = "custom_css_enabled";

    /// <summary>"1" puts it on the family page too, which by default keeps the plain look.</summary>
    public const string FamilyKey = "custom_css_family";

    /// <summary>
    /// 100 KB of UTF-8. Far more than any hand-written stylesheet needs — the whole of
    /// app.css is about this size — and small enough that it costs nothing to send with
    /// every page.
    /// </summary>
    public const int MaxBytes = 100 * 1024;

    /// <summary>
    /// One data: URL — an icon, a small texture. Bigger images belong in a file served by
    /// the dashboard (a path), not inlined into every page load.
    /// </summary>
    public const int MaxDataUrlBytes = 32 * 1024;

    /// <summary>How many saves are kept to go back to.</summary>
    public const int HistoryLimit = 20;

    /// <summary>
    /// The verdict on some CSS. <see cref="Css"/> is what would be saved — the text as typed,
    /// less any NUL characters — and is only meaningful when <see cref="Ok"/>.
    /// </summary>
    public sealed record Check(string Css, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
    {
        public bool Ok => Errors.Count == 0;
        public int Bytes => Encoding.UTF8.GetByteCount(Css);
    }

    /// <summary>Checks CSS against the rules above. Never throws; an empty stylesheet is fine.</summary>
    public static Check Validate(string? css)
    {
        var text = (css ?? "").Replace("\0", "", StringComparison.Ordinal);
        var errors = new List<string>();
        var warnings = new List<string>();

        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes > MaxBytes)
        {
            errors.Add($"It is {Kb(bytes)}, and the limit is {Kb(MaxBytes)}. Large images belong in a file, linked by a path.");
            // Not worth scanning something that cannot be saved anyway.
            return new Check(text, errors, warnings);
        }

        if (text.Contains("</style", StringComparison.OrdinalIgnoreCase))
            errors.Add("It contains “</style”, which would end the style element early and turn the rest into page markup. Remove it.");

        var scan = Scan(text);
        var plain = scan.Plain;

        if (AtImport().IsMatch(plain))
            errors.Add("@import is not allowed: it fetches another stylesheet from wherever it points. Paste the rules in instead.");

        foreach (Match match in UrlFunction().Matches(plain))
        {
            var target = (match.Groups[2].Success ? match.Groups[2].Value
                : match.Groups[3].Success ? match.Groups[3].Value
                : match.Groups[4].Value).Trim();
            if (CheckAddress(target, match.Groups[1].Value.ToLowerInvariant()) is { } problem && !errors.Contains(problem))
                errors.Add(problem);
        }

        // image-set(), image() and cross-fade() take a bare string as an address.
        foreach (var literal in scan.Strings)
        {
            if (AbsoluteAddress().IsMatch(literal) && CheckAddress(literal.Trim(), "a string") is { } problem && !errors.Contains(problem))
                errors.Add(problem);
        }

        if (scan.Opens != scan.Closes || scan.ClosedTooSoon)
            warnings.Add($"The braces do not balance: {scan.Opens} “{{” and {scan.Closes} “}}”. The browser skips whatever it cannot read — check the end of the rule you changed last.");
        if (scan.OpenComment)
            warnings.Add("A comment is opened with “/*” and never closed, so everything after it is ignored.");
        if (scan.OpenString)
            warnings.Add("A quoted string runs to the end of a line without its closing quote.");

        return new Check(text, errors, warnings);
    }

    /// <summary>
    /// The CSS as it goes into a &lt;style&gt; element. Saved CSS has already been refused if
    /// it held <c>&lt;/style</c>; this is the second lock, for text that reached the database
    /// some other way. <c>&lt;\/</c> cannot end an element, and in CSS the backslash only
    /// escapes the slash.
    /// </summary>
    public static string ForStyleElement(string? css) =>
        string.IsNullOrEmpty(css) ? "" : css.Replace("</", "<\\/", StringComparison.Ordinal);

    /// <summary>A short fingerprint, so an open page can tell whether what it has is current.</summary>
    public static string Hash(string css) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(css)))[..12].ToLowerInvariant();

    private static string? CheckAddress(string target, string where)
    {
        if (target.Length == 0 || target.StartsWith('#'))
            return null;

        if (target.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var size = Encoding.UTF8.GetByteCount(target);
            return size > MaxDataUrlBytes
                ? $"A data: URL in {Describe(where)} is {Kb(size)}; each may be up to {Kb(MaxDataUrlBytes)}. Serve a bigger image as a file and link it by its path."
                : null;
        }

        // Protocol-relative, which a browser reads as another host — and "/\" is read the same way.
        if (target.StartsWith("//", StringComparison.Ordinal) || target.StartsWith("/\\", StringComparison.Ordinal)
            || target.StartsWith('\\'))
            return OutsideAddress(target, where);

        if (Scheme().IsMatch(target))
            return OutsideAddress(target, where);

        return null;
    }

    private static string OutsideAddress(string target, string where) =>
        $"{Capitalise(Describe(where))} points at “{Shorten(target)}”. Only paths on this dashboard (like /icon.svg) and small data: URLs are allowed, so the page never reaches outside your network to draw itself.";

    private static string Describe(string where) => where switch
    {
        "url" => "a url()",
        "src" => "a src()",
        _ => where,
    };

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Shorten(string text) => text.Length <= 60 ? text : text[..57] + "…";

    private static string Kb(int bytes) =>
        bytes < 1024 ? $"{bytes} bytes" : string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB");

    /// <summary>What the scanner found: the CSS as a browser would read it, and what did not add up.</summary>
    private sealed record ScanResult(string Plain, IReadOnlyList<string> Strings, int Opens, int Closes, bool ClosedTooSoon, bool OpenComment, bool OpenString);

    /// <summary>
    /// One pass over the CSS the way the browser's tokenizer sees it: comments removed (to
    /// nothing, so a comment cannot hide a word by splitting it), escapes decoded, strings
    /// collected, and braces counted only where they are braces. An unquoted url( is read
    /// raw to its closing bracket, because inside one "/*" is not a comment — reading it as
    /// one would hide the rest of the address.
    /// </summary>
    private static ScanResult Scan(string css)
    {
        var plain = new StringBuilder(css.Length);
        var strings = new List<string>();
        int opens = 0, closes = 0, depth = 0;
        bool tooSoon = false, openComment = false, openString = false;

        var i = 0;
        while (i < css.Length)
        {
            var c = css[i];

            if (c == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                var end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    openComment = true;
                    break;
                }
                i = end + 2;
                continue;
            }

            if (c is '"' or '\'')
            {
                var (content, next, closed) = ReadString(css, i);
                if (!closed)
                    openString = true;
                strings.Add(content);
                plain.Append(c).Append(content).Append(c);
                i = next;
                continue;
            }

            if (c == '\\')
            {
                var (decoded, next) = ReadEscape(css, i);
                plain.Append(decoded);
                i = next;
                continue;
            }

            if (c == '(' && EndsWithUrl(plain))
            {
                plain.Append('(');
                i++;
                var j = i;
                while (j < css.Length && char.IsWhiteSpace(css[j]))
                    j++;
                if (j < css.Length && css[j] is not ('"' or '\''))
                {
                    // Unquoted: raw to the bracket, escapes decoded.
                    while (j < css.Length && css[j] != ')')
                    {
                        if (css[j] == '\\')
                        {
                            var (decoded, next) = ReadEscape(css, j);
                            plain.Append(decoded);
                            j = next;
                        }
                        else
                        {
                            plain.Append(css[j]);
                            j++;
                        }
                    }
                    i = j;
                }
                continue;
            }

            if (c == '{')
            {
                opens++;
                depth++;
            }
            else if (c == '}')
            {
                closes++;
                if (--depth < 0)
                {
                    tooSoon = true;
                    depth = 0;
                }
            }

            plain.Append(c);
            i++;
        }

        return new ScanResult(plain.ToString(), strings, opens, closes, tooSoon, openComment, openString);
    }

    private static bool EndsWithUrl(StringBuilder plain)
    {
        if (plain.Length < 3)
            return false;
        var tail = plain.ToString(plain.Length - 3, 3);
        if (!tail.Equals("url", StringComparison.OrdinalIgnoreCase))
            return false;
        // "url(" as a function of its own, not the end of a longer name.
        return plain.Length == 3 || !IsNameChar(plain[^4]);
    }

    private static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' || c > 0x7f;

    /// <summary>A quoted string from its opening quote: its decoded content, where reading resumes, and whether it closed.</summary>
    private static (string Content, int Next, bool Closed) ReadString(string css, int start)
    {
        var quote = css[start];
        var content = new StringBuilder();
        var i = start + 1;
        while (i < css.Length)
        {
            var c = css[i];
            if (c == quote)
                return (content.ToString(), i + 1, true);
            if (c == '\n')
                return (content.ToString(), i, false);
            if (c == '\\')
            {
                // A backslash before a newline continues the string onto the next line.
                if (i + 1 < css.Length && css[i + 1] == '\n')
                {
                    i += 2;
                    continue;
                }
                var (decoded, next) = ReadEscape(css, i);
                content.Append(decoded);
                i = next;
                continue;
            }
            content.Append(c);
            i++;
        }
        return (content.ToString(), i, false);
    }

    /// <summary>
    /// A CSS escape from its backslash: up to six hex digits and one optional space, or any
    /// other single character as itself. Returns the decoded text and where reading resumes.
    /// </summary>
    private static (string Decoded, int Next) ReadEscape(string css, int start)
    {
        var i = start + 1;
        if (i >= css.Length)
            return ("", i);

        if (char.IsAsciiHexDigit(css[i]))
        {
            var end = i;
            while (end < css.Length && end - i < 6 && char.IsAsciiHexDigit(css[end]))
                end++;
            var code = int.Parse(css.AsSpan(i, end - i), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (end < css.Length && css[end] is ' ' or '\t' or '\n')
                end++;
            var decoded = code == 0 || code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF)
                ? "�"
                : char.ConvertFromUtf32(code);
            return (decoded, end);
        }

        if (css[i] == '\n')
            return ("", i + 1);

        return (css[i].ToString(), i + 1);
    }

    [GeneratedRegex(@"@import\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AtImport();

    /// <summary>url( and src( — quoted or not — and what they point at.</summary>
    [GeneratedRegex(@"(?<![\w-])(url|src)\(\s*(?:""([^""]*)""|'([^']*)'|([^)\s]*))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlFunction();

    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.\-]*:")]
    private static partial Regex Scheme();

    /// <summary>A string that is an address somewhere else: "https://…", "ftp://…" or "//…".</summary>
    [GeneratedRegex(@"^\s*(?://|/\\|[a-zA-Z][a-zA-Z0-9+.\-]*://)")]
    private static partial Regex AbsoluteAddress();
}
