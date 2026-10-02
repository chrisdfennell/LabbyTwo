using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LabbyTwo.Core;

/// <summary>What a step sends as its body.</summary>
public enum StepBodyKind
{
    None,

    /// <summary>One <c>name=value</c> per line, sent as an HTML form would send it.</summary>
    Form,

    /// <summary>JSON text. Variables put into it are JSON-escaped, so a password with a quote in it cannot break it.</summary>
    Json,

    /// <summary>Anything else — the XML a WebDAV PROPFIND wants, say. Set the Content-Type header yourself.</summary>
    Raw,
}

/// <summary>One thing a step's response has to be for the step to pass.</summary>
public enum StepAssertKind
{
    /// <summary><see cref="StepAssertion.Value"/> is "200", "200-299", "2xx" or a comma list of those.</summary>
    Status,
    BodyContains,
    BodyNotContains,

    /// <summary>A .NET regular expression, run with a time limit so a bad pattern cannot hang the sweep.</summary>
    BodyMatches,

    /// <summary><see cref="StepAssertion.Target"/> is a dotted path with [n] indexes, the same as the JSON API provider's.</summary>
    JsonEquals,
    JsonExists,

    /// <summary><see cref="StepAssertion.Value"/> is milliseconds.</summary>
    TimeUnder,
    HeaderPresent,
    HeaderEquals,
}

/// <summary>Where a value saved for later steps comes from.</summary>
public enum StepExtractFrom
{
    /// <summary>A dotted path into a JSON body.</summary>
    Json,

    /// <summary>A regular expression over the body: its first group if it has one, otherwise the whole match.</summary>
    Regex,

    /// <summary>A response header, by name.</summary>
    Header,

    /// <summary>A cookie the run's cookie jar is holding, by name.</summary>
    Cookie,
}

/// <summary>
/// One HTTP request in a multi-step check, and what its answer must look like. A mutable
/// class rather than a record because the editor binds straight to it; what is stored is
/// its JSON, in the connection's <c>steps</c> setting.
/// </summary>
public sealed class CheckStep
{
    public string Name { get; set; } = "";
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = "";

    /// <summary>One per line as <c>Name: value</c>, the same shape every other provider's header box takes.</summary>
    public string Headers { get; set; } = "";

    public StepBodyKind BodyKind { get; set; }
    public string Body { get; set; } = "";

    /// <summary>
    /// Optional HTTP Basic credentials. A field of its own because Basic wants the pair
    /// base64-encoded, which is not something anybody should be asked to do by hand — and a
    /// password typed into a header box as base64 is a password nobody can mask.
    /// </summary>
    public string BasicUser { get; set; } = "";
    public string BasicPassword { get; set; } = "";

    /// <summary>Seconds; zero means the check's own per-step default.</summary>
    public int TimeoutSeconds { get; set; }

    public List<StepAssertion> Checks { get; set; } = [];
    public List<StepExtraction> Save { get; set; } = [];

    /// <summary>What a person calls it in a sentence: its name, or "Step n" when it has none.</summary>
    public string Title(int index) => Name.Trim() is { Length: > 0 } name ? name : $"Step {index + 1}";
}

/// <summary>One assertion. <see cref="Target"/> is a JSON path or a header name for the kinds that need one.</summary>
public sealed class StepAssertion
{
    public StepAssertKind Kind { get; set; }
    public string Target { get; set; } = "";
    public string Value { get; set; } = "";

    /// <summary>Whether this kind reads <see cref="Target"/> — so the editor only shows the box when it means something.</summary>
    public static bool HasTarget(StepAssertKind kind) =>
        kind is StepAssertKind.JsonEquals or StepAssertKind.JsonExists or StepAssertKind.HeaderPresent or StepAssertKind.HeaderEquals;

    /// <summary>Whether this kind reads <see cref="Value"/>.</summary>
    public static bool HasValue(StepAssertKind kind) =>
        kind is not (StepAssertKind.JsonExists or StepAssertKind.HeaderPresent);
}

/// <summary>A value pulled out of a response into a named variable, for <c>${name}</c> in later steps.</summary>
public sealed class StepExtraction
{
    public string Name { get; set; } = "";
    public StepExtractFrom From { get; set; }
    public string Expression { get; set; } = "";

    /// <summary>
    /// Masked wherever results are shown, like a secret. On by default because what people
    /// extract is mostly a session token or a CSRF value — a credential in all but name —
    /// and a value somebody wants to see can be un-hidden with one tick.
    /// </summary>
    public bool Hide { get; set; } = true;
}

/// <summary>A response as the assertions see it: already read, already capped.</summary>
public sealed class StepResponse(
    int status, string reason, TimeSpan elapsed, IReadOnlyList<KeyValuePair<string, string>> headers, string body, bool truncated)
{
    public int Status { get; } = status;
    public string Reason { get; } = reason;
    public TimeSpan Elapsed { get; } = elapsed;
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; } = headers;
    public string Body { get; } = body;

    /// <summary>The body was longer than the cap and only its start was read.</summary>
    public bool Truncated { get; } = truncated;

    private bool _parsed;
    private JsonElement? _json;

    /// <summary>The body as JSON, parsed once however many assertions ask; null when it is not JSON.</summary>
    public JsonElement? Json
    {
        get
        {
            if (_parsed)
                return _json;
            _parsed = true;
            try
            {
                using var document = JsonDocument.Parse(Body);
                _json = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                _json = null;
            }
            return _json;
        }
    }

    public string? Header(string name)
    {
        foreach (var (key, value) in Headers)
        {
            if (string.Equals(key, name.Trim(), StringComparison.OrdinalIgnoreCase))
                return value;
        }
        return null;
    }
}

/// <summary>
/// The variables a run has: what earlier steps saved, and the check's secrets. Built-ins
/// are deliberately few. <c>${env:…}</c> is refused rather than unsupported — a dashboard
/// that can be talked into reading its host's environment is a dashboard that can be
/// talked into reading its own keys.
/// </summary>
public sealed class StepVariables(IReadOnlyDictionary<string, string> secrets)
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> Secrets { get; } = secrets;

    public void Set(string name, string value) => _values[name.Trim()] = value;

    public bool TryGet(string name, out string value) => _values.TryGetValue(name.Trim(), out value!);
}

/// <summary>A reference that could not be filled in, with the sentence that says why.</summary>
public sealed class StepVariableException(string message) : Exception(message);

/// <summary>
/// Takes known strings out of anything about to be shown or logged. Every secret goes in,
/// in each of the spellings it could appear in — as typed, URL-encoded and JSON-escaped —
/// because a login error page that echoes the form back has it URL-decoded, and an API
/// that echoes a request has it JSON-escaped.
/// </summary>
public sealed class StepMasker
{
    public const string Mask = "••••";

    /// <summary>Shorter than this and masking would eat ordinary text — a one-letter secret is not worth unreadable results.</summary>
    public const int MinimumLength = 3;

    private readonly List<string> _needles = [];

    public void Add(string? secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length < MinimumLength)
            return;
        foreach (var spelling in new[]
                 {
                     secret, Uri.EscapeDataString(secret), WebUtility.UrlEncode(secret),
                     JsonEncodedText.Encode(secret).ToString(), WebUtility.HtmlEncode(secret),
                 })
        {
            if (!_needles.Contains(spelling, StringComparer.Ordinal))
                _needles.Add(spelling);
        }
        // Longest first, so a secret that contains another is masked whole.
        _needles.Sort((a, b) => b.Length.CompareTo(a.Length));
    }

    public string Apply(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? "";
        foreach (var needle in _needles)
            text = text.Replace(needle, Mask, StringComparison.Ordinal);
        return text;
    }
}

/// <summary>
/// The parts of a multi-step check that do not touch the network: reading and writing the
/// stored steps, filling in <c>${variables}</c>, judging a response, saying what went
/// wrong in a sentence. Kept apart from the HTTP so every rule is a fast test.
/// </summary>
public static partial class StepCheck
{
    /// <summary>More than this and it is no longer a check, it is a crawler.</summary>
    public const int MaxSteps = 20;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>The stored steps. Anything unreadable comes back as no steps rather than an exception: the editor can fix it, a crash could not.</summary>
    public static List<CheckStep> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<CheckStep>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string Serialize(IEnumerable<CheckStep> steps) => JsonSerializer.Serialize(steps, Json);

    /// <summary>The secrets as stored — one JSON object of name to value, encrypted as a whole by ConfigStore.</summary>
    public static Dictionary<string, string> ParseSecrets(string? json)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
            return result;
        try
        {
            foreach (var (name, value) in JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [])
            {
                if (name.Trim().Length > 0)
                    result[name.Trim()] = value ?? "";
            }
        }
        catch (JsonException)
        {
            // An unreadable blob is the same as none: every ${secret:…} will say it is missing.
        }
        return result;
    }

    public static string SerializeSecrets(IReadOnlyDictionary<string, string> secrets) =>
        JsonSerializer.Serialize(secrets.Where(s => s.Key.Trim().Length > 0)
            .ToDictionary(s => s.Key.Trim(), s => s.Value));

    // ---- variables -------------------------------------------------------------------

    [GeneratedRegex(@"\$\$\{|\$\{([^{}]*)\}")]
    private static partial Regex ReferencePattern();

    /// <summary>
    /// Fills in <c>${name}</c> and <c>${secret:name}</c>. <c>$${</c> is a literal <c>${</c>
    /// for the rare body that needs one. <c>${…}</c> rather than <c>{{…}}</c> because the
    /// latter is what the Markdown shortcodes use, and a check pasted into a note should not
    /// turn into a widget.
    /// </summary>
    /// <param name="encode">Applied to each value put in — JSON escaping for a JSON body, say.</param>
    /// <exception cref="StepVariableException">A reference that cannot be filled in.</exception>
    public static string Substitute(string? template, StepVariables variables, Func<string, string>? encode = null)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains('$'))
            return template ?? "";

        return ReferencePattern().Replace(template, match =>
        {
            if (match.Value == "$${")
                return "${";
            var reference = match.Groups[1].Value.Trim();
            var value = Resolve(reference, variables);
            return encode is null ? value : encode(value);
        });
    }

    /// <summary>Every <c>${…}</c> in a piece of text, for the editor to warn about ones nothing sets.</summary>
    public static IEnumerable<string> References(string? template)
    {
        if (string.IsNullOrEmpty(template))
            yield break;
        foreach (Match match in ReferencePattern().Matches(template))
        {
            if (match.Value != "$${")
                yield return match.Groups[1].Value.Trim();
        }
    }

    private static string Resolve(string reference, StepVariables variables)
    {
        var colon = reference.IndexOf(':');
        if (colon >= 0)
        {
            var scope = reference[..colon].Trim().ToLowerInvariant();
            var name = reference[(colon + 1)..].Trim();
            return scope switch
            {
                "secret" => variables.Secrets.TryGetValue(name, out var secret) && secret.Length > 0
                    ? secret
                    : throw new StepVariableException(
                        $"uses ${{secret:{name}}}, which is not one of this check's secrets — add it under Secrets"),
                "env" => throw new StepVariableException(
                    $"uses ${{{reference}}}, but a check cannot read LabbyTwo's environment — put the value in a secret instead"),
                _ => throw new StepVariableException(
                    $"uses ${{{reference}}}, and \"{scope}:\" is not something a check understands — only ${{secret:name}} and the names of saved values are"),
            };
        }

        if (reference.Length == 0)
            throw new StepVariableException("has an empty ${} in it");
        return variables.TryGet(reference, out var value)
            ? value
            : throw new StepVariableException($"uses ${{{reference}}}, which no earlier step saved");
    }

    // ---- assertions ------------------------------------------------------------------

    /// <summary>
    /// Whether a response passes one assertion. Null when it does; otherwise the failure as
    /// "expected … — …", ready to follow "Step 2 'Open the file': ".
    /// </summary>
    /// <param name="value">The assertion's value with variables already filled in.</param>
    public static string? Evaluate(StepAssertion assertion, string value, StepResponse response)
    {
        var target = assertion.Target.Trim();
        switch (assertion.Kind)
        {
            case StepAssertKind.Status:
                return StatusMatches(value, response.Status) is { } matches
                    ? matches ? null : $"expected HTTP {Quote(value, false)} — got {response.Status}"
                    : $"expected HTTP {Quote(value, false)}, but that is not a status this understands — write 200, 200-299, 2xx, or a comma list";

            case StepAssertKind.BodyContains:
                return response.Body.Contains(value, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : $"expected the body to contain {Quote(value)} — it didn't{TruncatedNote(response)}";

            case StepAssertKind.BodyNotContains:
                return response.Body.Contains(value, StringComparison.OrdinalIgnoreCase)
                    ? $"expected the body not to contain {Quote(value)} — it did"
                    : null;

            case StepAssertKind.BodyMatches:
                try
                {
                    return new Regex(value, RegexOptions.None, RegexTimeout).IsMatch(response.Body)
                        ? null
                        : $"expected the body to match /{Shorten(value)}/ — it didn't{TruncatedNote(response)}";
                }
                catch (ArgumentException)
                {
                    return $"expected the body to match /{Shorten(value)}/, but that is not a valid regular expression";
                }
                catch (RegexMatchTimeoutException)
                {
                    return $"expected the body to match /{Shorten(value)}/, but the pattern took too long to run — simplify it";
                }

            case StepAssertKind.JsonEquals:
            case StepAssertKind.JsonExists:
            {
                if (response.Json is not { } root)
                    return $"expected JSON with {Quote(target)} in it — the body was not JSON{TruncatedNote(response)}";
                var found = ResolvePath(root, target);
                if (assertion.Kind == StepAssertKind.JsonExists)
                    return found is null ? $"expected the JSON to have {Quote(target)} — it didn't" : null;
                if (found is null)
                    return $"expected {Quote(target)} to be {Quote(value)} — the JSON had no {Quote(target)}";
                return JsonEquals(found.Value, value)
                    ? null
                    : $"expected {Quote(target)} to be {Quote(value)} — it was {Quote(Describe(found.Value))}";
            }

            case StepAssertKind.TimeUnder:
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var limit))
                    return $"expected a response within {Quote(value)} ms, but that is not a number";
                return response.Elapsed.TotalMilliseconds < limit
                    ? null
                    : $"expected a response within {limit:0} ms — it took {response.Elapsed.TotalMilliseconds:0} ms";

            case StepAssertKind.HeaderPresent:
                return response.Header(target) is null ? $"expected a {Quote(target)} header — there wasn't one" : null;

            case StepAssertKind.HeaderEquals:
                return response.Header(target) switch
                {
                    null => $"expected the {Quote(target)} header to be {Quote(value)} — there wasn't one",
                    var actual when string.Equals(actual.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase) => null,
                    var actual => $"expected the {Quote(target)} header to be {Quote(value)} — it was {Quote(actual)}",
                };

            default:
                return $"has a check this version does not understand ({assertion.Kind})";
        }
    }

    /// <summary>
    /// What a step with no status assertion of its own has to answer: anything below 400.
    /// A login that redirects and a page that loads both pass; a 404 or a 500 does not —
    /// "every assertion passed" should never mean "the server said it was broken, and
    /// nobody asked".
    /// </summary>
    public static string? ImplicitStatus(StepResponse response) =>
        response.Status < 400 ? null : $"expected a response below 400 — got HTTP {response.Status}";

    /// <summary>"200", "2xx", "200-299", "200, 302". Null when it cannot be read at all.</summary>
    public static bool? StatusMatches(string spec, int status)
    {
        var any = false;
        foreach (var raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            any = true;
            var part = raw.Replace('–', '-');
            if (part.Length == 3 && part.EndsWith("xx", StringComparison.OrdinalIgnoreCase) && char.IsDigit(part[0]))
            {
                if (status / 100 == part[0] - '0')
                    return true;
                continue;
            }
            var dash = part.IndexOf('-');
            if (dash > 0 && int.TryParse(part[..dash].Trim(), out var low) && int.TryParse(part[(dash + 1)..].Trim(), out var high))
            {
                if (status >= low && status <= high)
                    return true;
                continue;
            }
            if (int.TryParse(part, out var exact))
            {
                if (status == exact)
                    return true;
                continue;
            }
            return null;
        }
        return any ? false : null;
    }

    /// <summary>The same deliberately small path language the JSON API provider uses: dots and [n].</summary>
    public static JsonElement? ResolvePath(JsonElement root, string path) =>
        path.Trim().Length == 0 ? root : JsonPath.Resolve(root, path.Trim());

    private static bool JsonEquals(JsonElement element, string expected)
    {
        var text = expected.Trim();
        return element.ValueKind switch
        {
            JsonValueKind.String => string.Equals(element.GetString(), expected, StringComparison.Ordinal) ||
                                    string.Equals(element.GetString()?.Trim(), text, StringComparison.OrdinalIgnoreCase),
            JsonValueKind.Number => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? element.GetDouble() == number
                : element.GetRawText() == text,
            JsonValueKind.True => text.Equals("true", StringComparison.OrdinalIgnoreCase),
            JsonValueKind.False => text.Equals("false", StringComparison.OrdinalIgnoreCase),
            JsonValueKind.Null => text.Equals("null", StringComparison.OrdinalIgnoreCase),
            _ => element.GetRawText() == text,
        };
    }

    private static string Describe(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => $"a list of {element.GetArrayLength()}",
        _ => element.GetRawText(),
    };

    // ---- extraction ------------------------------------------------------------------

    /// <summary>The value an extraction finds, or null when it finds nothing.</summary>
    /// <param name="cookie">Looks a cookie up by name in the run's jar.</param>
    public static string? Extract(StepExtraction extraction, StepResponse response, Func<string, string?> cookie)
    {
        var expression = extraction.Expression.Trim();
        switch (extraction.From)
        {
            case StepExtractFrom.Json:
                if (response.Json is not { } root || ResolvePath(root, expression) is not { } found)
                    return null;
                return found.ValueKind switch
                {
                    JsonValueKind.String => found.GetString(),
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    _ => found.GetRawText(),
                };

            case StepExtractFrom.Regex:
                try
                {
                    var match = new Regex(extraction.Expression, RegexOptions.None, RegexTimeout).Match(response.Body);
                    if (!match.Success)
                        return null;
                    return match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
                }
                catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
                {
                    return null;
                }

            case StepExtractFrom.Header:
                return response.Header(expression);

            case StepExtractFrom.Cookie:
                return cookie(expression);

            default:
                return null;
        }
    }

    /// <summary>"JSON path access_token" — where a missing value was looked for, for the failure sentence.</summary>
    public static string DescribeSource(StepExtraction extraction) => extraction.From switch
    {
        StepExtractFrom.Json => $"JSON path {Quote(extraction.Expression)}",
        StepExtractFrom.Regex => $"pattern /{Shorten(extraction.Expression)}/",
        StepExtractFrom.Header => $"header {Quote(extraction.Expression)}",
        StepExtractFrom.Cookie => $"cookie {Quote(extraction.Expression)}",
        _ => extraction.From.ToString(),
    };

    // ---- naming ----------------------------------------------------------------------

    /// <summary>
    /// The metric key for one step's time: <c>step_ms:</c> and the step's name squashed to
    /// letters, digits and underscores, so it is a key a chart and an alert rule can both
    /// hold. Two steps with the same name get a number after the second.
    /// </summary>
    public static IReadOnlyList<string> StepMetricKeys(IReadOnlyList<CheckStep> steps)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var keys = new List<string>(steps.Count);
        for (var i = 0; i < steps.Count; i++)
        {
            var slug = Slug(steps[i].Title(i));
            var key = $"step_ms:{slug}";
            for (var n = 2; !used.Add(key); n++)
                key = $"step_ms:{slug}_{n}";
            keys.Add(key);
        }
        return keys;
    }

    private static string Slug(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
                builder.Append(c);
            else if (builder.Length > 0 && builder[^1] != '_')
                builder.Append('_');
        }
        var slug = builder.ToString().Trim('_');
        return slug.Length == 0 ? "step" : slug;
    }

    /// <summary>What an assertion checks, in the words the editor shows beside it.</summary>
    public static string Label(StepAssertKind kind) => kind switch
    {
        StepAssertKind.Status => "Status code is",
        StepAssertKind.BodyContains => "Body contains",
        StepAssertKind.BodyNotContains => "Body does not contain",
        StepAssertKind.BodyMatches => "Body matches pattern",
        StepAssertKind.JsonEquals => "JSON value equals",
        StepAssertKind.JsonExists => "JSON value exists",
        StepAssertKind.TimeUnder => "Answers within (ms)",
        StepAssertKind.HeaderPresent => "Header is present",
        StepAssertKind.HeaderEquals => "Header equals",
        _ => kind.ToString(),
    };

    public static string Label(StepExtractFrom from) => from switch
    {
        StepExtractFrom.Json => "JSON path",
        StepExtractFrom.Regex => "Pattern in the body",
        StepExtractFrom.Header => "Response header",
        StepExtractFrom.Cookie => "Cookie",
        _ => from.ToString(),
    };

    internal static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(500);

    private static string TruncatedNote(StepResponse response) =>
        response.Truncated ? " (only the first part of a very large response was read)" : "";

    private static string Quote(string text, bool quoted = true) =>
        quoted ? $"'{Shorten(text)}'" : Shorten(text);

    private static string Shorten(string text) =>
        text.Length <= 80 ? text : text[..77] + "…";

    // ---- starting points -------------------------------------------------------------

    /// <summary>
    /// Examples to start from — each is only steps, filled into the editor and then the
    /// person's to change. Nothing anywhere behaves differently because a check began as
    /// one of these; they exist because a blank list of steps is a hard place to start.
    /// </summary>
    public static IReadOnlyList<StepCheckTemplate> Templates { get; } =
    [
        new("login-form", "Log in to a form and check a page",
            "Loads the login page, picks the CSRF token out of it, posts the form with a password kept as a secret " +
            "named \"password\", then opens a page that only a logged-in user sees. The session cookie carries " +
            "over by itself. Change the addresses, field names and the text to look for.",
            ["password"],
            [
                new CheckStep
                {
                    Name = "Open the login page", Url = "https://app.example.lan/login",
                    Checks = [new() { Kind = StepAssertKind.Status, Value = "200" }],
                    Save = [new() { Name = "csrf", From = StepExtractFrom.Regex, Expression = "name=\"csrf_token\" value=\"([^\"]+)\"" }],
                },
                new CheckStep
                {
                    Name = "Log in", Method = "POST", Url = "https://app.example.lan/login",
                    BodyKind = StepBodyKind.Form,
                    Body = "username=monitor\npassword=${secret:password}\ncsrf_token=${csrf}",
                    Checks =
                    [
                        new() { Kind = StepAssertKind.Status, Value = "200-399" },
                        new() { Kind = StepAssertKind.BodyNotContains, Value = "Invalid password" },
                    ],
                },
                new CheckStep
                {
                    Name = "Open the dashboard", Url = "https://app.example.lan/dashboard",
                    Checks =
                    [
                        new() { Kind = StepAssertKind.Status, Value = "200" },
                        new() { Kind = StepAssertKind.BodyContains, Value = "Log out" },
                    ],
                },
            ]),
        new("api-token", "Call an API with a token and check JSON",
            "Swaps a username and a password (a secret named \"password\") for a token, then calls the API with it " +
            "as a Bearer header and checks a field in the answer.",
            ["password"],
            [
                new CheckStep
                {
                    Name = "Get a token", Method = "POST", Url = "https://api.example.lan/auth/token",
                    BodyKind = StepBodyKind.Json,
                    Body = "{\n  \"username\": \"monitor\",\n  \"password\": \"${secret:password}\"\n}",
                    Checks = [new() { Kind = StepAssertKind.Status, Value = "200" }],
                    Save = [new() { Name = "token", From = StepExtractFrom.Json, Expression = "access_token" }],
                },
                new CheckStep
                {
                    Name = "Check the status", Url = "https://api.example.lan/status",
                    Headers = "Authorization: Bearer ${token}\nAccept: application/json",
                    Checks =
                    [
                        new() { Kind = StepAssertKind.Status, Value = "200" },
                        new() { Kind = StepAssertKind.JsonEquals, Target = "status", Value = "ok" },
                        new() { Kind = StepAssertKind.TimeUnder, Value = "2000" },
                    ],
                },
            ]),
        new("nextcloud", "Nextcloud: log in and list files",
            "Checks Nextcloud says it is installed and not in maintenance, then lists your files over WebDAV with an " +
            "app password (Settings → Security → Devices & sessions) kept as a secret named \"app_password\". " +
            "Change the address and the user name in both places.",
            ["app_password"],
            [
                new CheckStep
                {
                    Name = "Server status", Url = "https://cloud.example.lan/status.php",
                    Checks =
                    [
                        new() { Kind = StepAssertKind.Status, Value = "200" },
                        new() { Kind = StepAssertKind.JsonEquals, Target = "installed", Value = "true" },
                        new() { Kind = StepAssertKind.JsonEquals, Target = "maintenance", Value = "false" },
                    ],
                },
                new CheckStep
                {
                    Name = "List files", Method = "PROPFIND", Url = "https://cloud.example.lan/remote.php/dav/files/monitor/",
                    Headers = "Depth: 1\nContent-Type: application/xml",
                    BodyKind = StepBodyKind.Raw,
                    Body = "<?xml version=\"1.0\"?>\n<d:propfind xmlns:d=\"DAV:\"><d:prop><d:displayname/></d:prop></d:propfind>",
                    BasicUser = "monitor", BasicPassword = "${secret:app_password}",
                    Checks =
                    [
                        new() { Kind = StepAssertKind.Status, Value = "207" },
                        new() { Kind = StepAssertKind.BodyContains, Value = "multistatus" },
                    ],
                },
            ]),
    ];
}

/// <summary>A starting point in the editor's "Start from an example" list.</summary>
/// <param name="Secrets">The secret names its steps refer to, so the editor can say which to add.</param>
public sealed record StepCheckTemplate(
    string Id, string Name, string Description, IReadOnlyList<string> Secrets, IReadOnlyList<CheckStep> Steps)
{
    /// <summary>A fresh copy to edit, so changing the steps never changes the example.</summary>
    public List<CheckStep> Copy() => StepCheck.Parse(StepCheck.Serialize(Steps));
}
