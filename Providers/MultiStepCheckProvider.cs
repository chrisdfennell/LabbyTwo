using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

/// <summary>
/// "Is it actually working", rather than "does the port answer". A web app can serve its
/// login page perfectly while the database behind it is gone, a reverse proxy can answer
/// 200 with its own error page, and a file share can let you log in and then list nothing.
/// The <see cref="HttpEndpointProvider"/> sees none of that. This walks the path a person
/// would — log in, open the thing, check it is there — and is only up when every step is.
///
/// <para><b>Its own HTTP stack, not the shared provider client.</b> The shared client
/// ignores certificates (right for a quick probe of a LAN box, wrong for a check whose
/// point is "would this work for me"), keeps one cookie container for every provider in the
/// app (wrong for a login, whose session must neither leak into nor come from anything
/// else) and follows redirects where nobody sees the cookies set along the way. So the two
/// handlers here verify certificates unless a check says otherwise, keep no cookies of
/// their own — every run gets a fresh jar — and never follow a redirect themselves, so each
/// hop's Set-Cookie lands in the jar and each hop's address is checked against what a check
/// may reach.</para>
///
/// <para><b>What a check may reach.</b> http and https only. Never a link-local address —
/// 169.254.169.254 is where every cloud's instance metadata (and its credentials) lives, and
/// a dashboard that can be pointed at it is a dashboard that hands them out. Checked on the
/// address actually connected to, after DNS, so a name that resolves there is refused as
/// surely as the literal.</para>
///
/// <para><b>Within the sweep.</b> The whole run has a budget (thirty seconds by default,
/// never more than <see cref="MaxBudget"/>) that stays inside the monitor's forty-second
/// probe deadline, so a slow step reports itself rather than being abandoned; and each
/// response is read only up to <see cref="MaxBodyBytes"/>.</para>
/// </summary>
public sealed class MultiStepCheckProvider : IConnectionProvider, IDisposable
{
    /// <summary>The most of one response body a step reads. Enough for any page worth asserting on, and a cap on what a misconfigured step can pull into memory every sweep.</summary>
    public const int MaxBodyBytes = 1024 * 1024;

    /// <summary>How much of a body "Run now" shows. A sample to write assertions against, not a viewer.</summary>
    public const int SampleBytes = 2048;

    public const int MaxRedirects = 5;

    /// <summary>The longest a whole check may run: inside the monitor's forty-second deadline, with room to say why it stopped.</summary>
    public static readonly TimeSpan MaxBudget = TimeSpan.FromSeconds(35);

    /// <summary>Request headers whose values are credentials whatever they hold, so never shown.</summary>
    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie", "X-Api-Key", "X-Auth-Token",
    };

    private readonly ProviderHttpLogger _requestLog;
    private readonly Lazy<SocketsHttpHandler> _verified;
    private readonly Lazy<SocketsHttpHandler> _unverified;

    public MultiStepCheckProvider(ILogger<ProviderHttpLogger> log)
    {
        _requestLog = new ProviderHttpLogger(log);
        _verified = new(() => CreateHandler(ignoreCertificate: false));
        _unverified = new(() => CreateHandler(ignoreCertificate: true));
    }

    /// <summary>
    /// How names are looked up. Replaceable so a test can make DNS fail the way a broken
    /// container resolver does, without needing a broken container resolver.
    /// </summary>
    public Func<string, CancellationToken, Task<IPAddress[]>> Resolve { get; set; } =
        (host, ct) => Dns.GetHostAddressesAsync(host, ct);

    public string Type => "steps";
    public string DisplayName => "Multi-step check";
    public string Icon => "🧭";
    public string Category => "General";
    public string Description =>
        "Walks a web app the way a person would — log in, open a page, call an API — and is only up when every step " +
        "answers the way you said it should. For \"is it actually working\", not \"does the port answer\".";

    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("steps", "Steps", FieldKind.StepList, Required: true,
            Help: "Run in order, sharing one cookie jar, so a login step's session carries on to the next. " +
                  "Use ${name} for a value an earlier step saved and ${secret:name} for one of the secrets below."),
        new("secrets", "Secrets", FieldKind.SecretList,
            Help: "Passwords and keys the steps use as ${secret:name}. Encrypted like every other password, never shown " +
                  "again once saved, and masked in results and logs."),
        new("open_url", "Link opens", FieldKind.Url, "https://app.example.lan",
            Help: "Optional. Where the tile goes when clicked."),
        new("ignore_tls", "Ignore certificate errors", FieldKind.Bool, Default: "false",
            Help: "Off by default: a check of whether something works should notice a certificate that has expired. " +
                  "Turn on for a self-signed certificate you have chosen to trust."),
        new("follow_redirects", "Follow redirects", FieldKind.Bool, Default: "true",
            Help: "Off to assert on the redirect itself — that a login answers 302 with a Location header, say."),
        new("step_timeout", "Each step may take (seconds)", FieldKind.Number, Default: "10",
            Help: "Unless the step sets its own.") { Advanced = true },
        new("budget", "The whole check may take (seconds)", FieldKind.Number, Default: "30",
            Help: $"At most {MaxBudget.TotalSeconds:0}, so it always finishes inside the monitor's own deadline.") { Advanced = true },
    ];

    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("total_ms", "Whole check", " ms"),
        new("steps_passed", "Steps passed", "", 0),
        new("failed_step", "Failed at step", "", 0),
    ];

    /// <summary>The fixed three, and one <c>step_ms:…</c> per step, named after the step so a chart says which.</summary>
    public IReadOnlyList<MetricSpec> MetricsFor(Connection connection)
    {
        var steps = StepCheck.Parse(connection.Settings.Get("steps"));
        var keys = StepCheck.StepMetricKeys(steps);
        return [.. Metrics, .. keys.Select((key, i) => new MetricSpec(key, $"Step {i + 1}: {steps[i].Title(i)}", " ms"))];
    }

    public IReadOnlyList<SuggestedRule> SuggestedRules =>
    [
        new("Getting slow", "total_ms", Comparison.Above, 10000, ClearThreshold: 5000, ForMinutes: 10,
            Why: "Every step still passing but the whole walk taking ten seconds is the app a person gives up on."),
    ];

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var run = await RunAsync(connection.Settings, trace: false, ct);
        return run.ToProbeResult();
    }

    /// <summary>
    /// One run of the check. <paramref name="trace"/> is the editor's "Run now": it keeps
    /// each step's request and response summary — headers and the start of the body, masked
    /// — which a probe has no use for and should not pay for.
    /// </summary>
    public async Task<StepCheckRun> RunAsync(SettingsBag settings, bool trace, CancellationToken ct)
    {
        var steps = StepCheck.Parse(settings.Get("steps"));
        var secrets = StepCheck.ParseSecrets(settings.Get("secrets"));
        var masker = new StepMasker();
        foreach (var secret in secrets.Values)
            masker.Add(secret);

        var stopwatch = Stopwatch.StartNew();
        var outcomes = new List<StepOutcome>();
        if (steps.Count == 0)
            return new StepCheckRun(false, "No steps yet — add at least one.", TimeSpan.Zero, outcomes, 0, steps);
        if (steps.Count > StepCheck.MaxSteps)
            return new StepCheckRun(false, $"A check can have at most {StepCheck.MaxSteps} steps; this one has {steps.Count}.",
                TimeSpan.Zero, outcomes, 0, steps);

        var budget = TimeSpan.FromSeconds(Math.Clamp(settings.GetInt("budget", 30), 1, (int)MaxBudget.TotalSeconds));
        var stepDefault = Math.Clamp(settings.GetInt("step_timeout", 10), 1, (int)budget.TotalSeconds);
        var followRedirects = settings.GetBool("follow_redirects", true);
        var handler = settings.GetBool("ignore_tls") ? _unverified.Value : _verified.Value;
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);

        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgetCts.CancelAfter(budget);

        var variables = new StepVariables(secrets);
        var jar = new CookieContainer();
        var failedAt = 0;
        string? failure = null;

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var title = step.Title(i);
            var prefix = $"Step {i + 1} '{title}'";
            var outcome = new StepOutcome(i, title, step.Method.Trim().ToUpperInvariant());
            outcomes.Add(outcome);

            if (failedAt > 0)
            {
                outcome.Skipped = true;
                continue;
            }

            var timeout = TimeSpan.FromSeconds(step.TimeoutSeconds > 0 ? Math.Clamp(step.TimeoutSeconds, 1, (int)budget.TotalSeconds) : stepDefault);
            using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(budgetCts.Token);
            stepCts.CancelAfter(timeout);
            var started = stopwatch.Elapsed;
            string? target = null;

            try
            {
                var request = Prepare(step, variables, outcome, masker, trace);
                target = request.Uri.ToString();
                outcome.Url = masker.Apply(target);

                var response = await SendAsync(invoker, request, jar, followRedirects, outcome, masker, trace, stepCts.Token);
                outcome.Status = response.Status;
                outcome.Elapsed = response.Elapsed;

                var problem = Judge(step, response, variables, jar, masker, outcome, trace);
                if (problem is not null)
                {
                    failedAt = i + 1;
                    failure = $"{prefix}: {problem} (HTTP {response.Status}, {response.Elapsed.TotalMilliseconds:0} ms)";
                    outcome.Failure = problem;
                }
                else
                {
                    outcome.Passed = true;
                }
            }
            catch (StepVariableException ex)
            {
                failedAt = i + 1;
                failure = $"{prefix} {ex.Message}.";
                outcome.Failure = ex.Message;
            }
            catch (StepRefusedException ex)
            {
                failedAt = i + 1;
                failure = $"{prefix}: {ex.Message}";
                outcome.Failure = ex.Message;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex.GetBaseException() is StepRefusedException refused)
            {
                failedAt = i + 1;
                failure = $"{prefix}: {refused.Message}";
                outcome.Failure = refused.Message;
            }
            catch (Exception ex)
            {
                failedAt = i + 1;
                outcome.Elapsed = stopwatch.Elapsed - started;
                // Described by ProbeError so the monitor hears what kind of failure it was:
                // a step that cannot resolve its host is a DNS failure, and many of those at
                // once mean LabbyTwo is blind, not that every multi-step check broke.
                var described = ProbeError.Describe(ex, target);
                var reason = ex is OperationCanceledException || ex.GetBaseException() is OperationCanceledException or TimeoutException
                    ? budgetCts.IsCancellationRequested
                        ? $"the whole check ran out of its {budget.TotalSeconds:0} s budget. {described}"
                        : $"no answer within {timeout.TotalSeconds:0} s. {described}"
                    : described;
                failure = $"{prefix}: {reason}";
                outcome.Failure = reason;
            }
        }

        stopwatch.Stop();
        var ok = failedAt == 0;
        var message = ok
            ? steps.Count == 1 ? "The step passed." : $"All {steps.Count} steps passed."
            : masker.Apply(failure);
        foreach (var outcome in outcomes)
            outcome.Failure = outcome.Failure is null ? null : masker.Apply(outcome.Failure);
        return new StepCheckRun(ok, message, stopwatch.Elapsed, outcomes, failedAt, steps);
    }

    // ---- building the request --------------------------------------------------------

    private sealed record PreparedRequest(
        HttpMethod Method, Uri Uri, IReadOnlyList<(string Name, string Value)> Headers, byte[]? Body, string? ContentType);

    private static PreparedRequest Prepare(CheckStep step, StepVariables variables, StepOutcome outcome, StepMasker masker, bool trace)
    {
        var rawUrl = StepCheck.Substitute(step.Url.Trim(), variables);
        if (rawUrl.Length == 0)
            throw new StepRefusedException("no address is set.");
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
            throw new StepRefusedException($"\"{masker.Apply(rawUrl)}\" is not an address this can use — it needs http:// or https:// and a host.");
        Guard(uri);

        var methodName = step.Method.Trim().ToUpperInvariant();
        if (methodName.Length == 0)
            methodName = "GET";
        if (!methodName.All(c => char.IsAsciiLetterUpper(c) || c == '-'))
            throw new StepRefusedException($"\"{step.Method}\" is not an HTTP method.");

        var headers = new List<(string, string)>();
        foreach (var line in step.Headers.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
                continue;
            var name = line[..separator].Trim();
            // A value carrying a newline would be a second header nobody wrote.
            var value = StepCheck.Substitute(line[(separator + 1)..].Trim(), variables).Replace("\r", "").Replace("\n", "");
            headers.Add((name, value));
        }

        if (step.BasicUser.Trim().Length > 0 || step.BasicPassword.Length > 0)
        {
            var user = StepCheck.Substitute(step.BasicUser.Trim(), variables);
            var password = StepCheck.Substitute(step.BasicPassword, variables);
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
            masker.Add(token);
            headers.RemoveAll(h => h.Item1.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
            headers.Add(("Authorization", $"Basic {token}"));
        }

        byte[]? body = null;
        string? contentType = null;
        switch (step.BodyKind)
        {
            case StepBodyKind.Form:
            {
                var pairs = new List<string>();
                foreach (var line in step.Body.Split('\n'))
                {
                    var trimmed = line.TrimEnd('\r');
                    if (trimmed.Trim().Length == 0)
                        continue;
                    var eq = trimmed.IndexOf('=');
                    var key = eq < 0 ? trimmed.Trim() : trimmed[..eq].Trim();
                    var value = eq < 0 ? "" : trimmed[(eq + 1)..];
                    pairs.Add($"{WebUtility.UrlEncode(StepCheck.Substitute(key, variables))}={WebUtility.UrlEncode(StepCheck.Substitute(value, variables))}");
                }
                body = Encoding.UTF8.GetBytes(string.Join('&', pairs));
                contentType = "application/x-www-form-urlencoded";
                break;
            }
            case StepBodyKind.Json:
                body = Encoding.UTF8.GetBytes(StepCheck.Substitute(step.Body, variables, v => JsonEncodedText.Encode(v).ToString()));
                contentType = "application/json";
                break;
            case StepBodyKind.Raw:
                body = Encoding.UTF8.GetBytes(StepCheck.Substitute(step.Body, variables));
                contentType = "text/plain; charset=utf-8";
                break;
        }

        var explicitType = headers.FindLast(h => h.Item1.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));
        if (explicitType != default)
        {
            contentType = explicitType.Item2;
            headers.RemoveAll(h => h.Item1.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));
        }

        if (trace)
        {
            outcome.RequestHeaders = [.. headers.Select(h => (h.Item1, ShowHeader(h.Item1, h.Item2, masker)))];
            if (body is not null && contentType is not null)
                outcome.RequestHeaders.Add(("Content-Type", contentType));
        }

        return new PreparedRequest(new HttpMethod(methodName), uri, headers, body, contentType);
    }

    /// <summary>Refuses an address a check must never reach, before anything is sent.</summary>
    private static void Guard(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new StepRefusedException($"only http:// and https:// addresses can be checked, not {uri.Scheme}:.");
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal) && IsBlocked(literal))
            throw new StepRefusedException(BlockedSentence(uri.Host, literal));
    }

    /// <summary>
    /// Link-local, in either family, and the IPv6 address AWS also serves metadata on.
    /// Nothing a home lab runs lives there on purpose, and the cloud metadata service — the
    /// one thing that reliably does — must never be a check's target.
    /// </summary>
    public static bool IsBlocked(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 169 && bytes[1] == 254;
        }
        return address.IsIPv6LinkLocal || address.Equals(IPAddress.Parse("fd00:ec2::254"));
    }

    private static string BlockedSentence(string host, IPAddress address) =>
        (host.Trim('[', ']') == address.ToString()
            ? $"{address} is a link-local address"
            : $"{host} resolves to {address}, a link-local address")
        + " — that is where cloud servers keep their metadata and credentials, so checks are not allowed to reach it.";

    // ---- sending ---------------------------------------------------------------------

    private async Task<StepResponse> SendAsync(
        HttpMessageInvoker invoker, PreparedRequest prepared, CookieContainer jar, bool followRedirects,
        StepOutcome outcome, StepMasker masker, bool trace, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var method = prepared.Method;
        var uri = prepared.Uri;
        var body = prepared.Body;
        var headers = prepared.Headers;

        for (var hop = 0; ; hop++)
        {
            Guard(uri);
            using var request = new HttpRequestMessage(method, uri);
            var userCookie = "";
            foreach (var (name, value) in headers)
            {
                if (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                    userCookie = value;
                else
                    request.Headers.TryAddWithoutValidation(name, value);
            }
            var cookies = string.Join("; ", new[] { jar.GetCookieHeader(uri), userCookie }.Where(c => c.Length > 0));
            if (cookies.Length > 0)
                request.Headers.TryAddWithoutValidation("Cookie", cookies);
            if (body is not null)
            {
                request.Content = new ByteArrayContent(body);
                if (prepared.ContentType is { } type)
                    request.Content.Headers.TryAddWithoutValidation("Content-Type", type);
            }

            var sent = Stopwatch.StartNew();
            HttpResponseMessage response;
            try
            {
                response = await invoker.SendAsync(request, ct);
            }
            catch (Exception ex)
            {
                _requestLog.LogRequestFailed(null, request, null, ex, sent.Elapsed);
                throw;
            }

            using (response)
            {
                _requestLog.LogRequestStop(null, request, response, sent.Elapsed);
                if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
                {
                    foreach (var setCookie in setCookies)
                    {
                        try
                        {
                            jar.SetCookies(uri, setCookie);
                        }
                        catch (CookieException)
                        {
                            // A malformed cookie is the server's problem; the step carries on without it.
                        }
                    }
                }

                var code = (int)response.StatusCode;
                if (followRedirects && code is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
                {
                    if (hop >= MaxRedirects)
                        throw new StepRefusedException($"redirected more than {MaxRedirects} times — it may be going round in a loop.");
                    var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    if (trace)
                        outcome.Redirects.Add($"{code} → {masker.Apply(next.ToString())}");
                    // What browsers do: 303 always becomes a GET, and so do 301 and 302 after
                    // a POST; 307 and 308 repeat the request exactly.
                    if (code == 303 || (code is 301 or 302 && method == HttpMethod.Post))
                    {
                        method = HttpMethod.Get;
                        body = null;
                    }
                    // Credentials typed for one host are not handed to another it points at.
                    if (!string.Equals(next.Authority, uri.Authority, StringComparison.OrdinalIgnoreCase))
                        headers = [.. headers.Where(h => !h.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))];
                    uri = next;
                    continue;
                }

                var (text, truncated) = await ReadCappedAsync(response.Content, ct);
                stopwatch.Stop();

                var all = new List<KeyValuePair<string, string>>();
                foreach (var header in response.Headers.Concat(response.Content.Headers))
                    all.Add(new(header.Key, string.Join(", ", header.Value)));

                if (trace)
                {
                    outcome.ResponseHeaders = [.. all.Select(h => (h.Key, ShowHeader(h.Key, h.Value, masker)))];
                    var sample = text.Length > SampleBytes ? text[..SampleBytes] : text;
                    outcome.BodySample = masker.Apply(sample);
                    outcome.BodyLength = text.Length;
                    outcome.Truncated = truncated;
                    outcome.Reason = response.ReasonPhrase ?? "";
                }

                return new StepResponse(code, response.ReasonPhrase ?? "", stopwatch.Elapsed, all, text, truncated);
            }
        }
    }

    /// <summary>Reads at most <see cref="MaxBodyBytes"/>, however much the server would send.</summary>
    private static async Task<(string Text, bool Truncated)> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxBodyBytes + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct);
            if (n == 0)
                break;
            read += n;
        }
        var truncated = read > MaxBodyBytes;
        var length = Math.Min(read, MaxBodyBytes);

        var encoding = Encoding.UTF8;
        if (content.Headers.ContentType?.CharSet is { Length: > 0 } charset)
        {
            try
            {
                encoding = Encoding.GetEncoding(charset.Trim('"'));
            }
            catch (ArgumentException)
            {
                // An unknown charset is read as UTF-8, which is what it almost always is anyway.
            }
        }
        return (encoding.GetString(buffer, 0, length), truncated);
    }

    // ---- judging ---------------------------------------------------------------------

    /// <summary>The first thing wrong with a response, or null — then saves what later steps need.</summary>
    private static string? Judge(
        CheckStep step, StepResponse response, StepVariables variables, CookieContainer jar,
        StepMasker masker, StepOutcome outcome, bool trace)
    {
        if (!step.Checks.Any(c => c.Kind == StepAssertKind.Status) && StepCheck.ImplicitStatus(response) is { } implicitFailure)
            return implicitFailure;

        foreach (var check in step.Checks)
        {
            var value = StepCheck.Substitute(check.Value, variables);
            if (StepCheck.Evaluate(check, value, response) is { } failed)
                return failed;
        }

        foreach (var extraction in step.Save)
        {
            var name = extraction.Name.Trim();
            if (name.Length == 0)
                continue;
            var found = StepCheck.Extract(extraction, response, cookie => FindCookie(jar, cookie));
            if (found is null)
                return $"couldn't find a value for '{name}' ({StepCheck.DescribeSource(extraction)}) in the response";
            if (extraction.Hide)
                masker.Add(found);
            variables.Set(name, found);
            if (trace)
                outcome.Saved.Add(extraction.Hide
                    ? $"{name}: found ({found.Length} characters, hidden)"
                    : $"{name} = {Shorten(found)}");
        }
        return null;
    }

    private static string? FindCookie(CookieContainer jar, string name) =>
        jar.GetAllCookies()
            .Where(c => string.Equals(c.Name, name.Trim(), StringComparison.Ordinal) && !c.Expired)
            .OrderByDescending(c => c.TimeStamp)
            .FirstOrDefault()?.Value;

    private static string ShowHeader(string name, string value, StepMasker masker) =>
        SensitiveHeaders.Contains(name) ? StepMasker.Mask : masker.Apply(value);

    private static string Shorten(string text) => text.Length <= 60 ? text : text[..57] + "…";

    // ---- the handlers ----------------------------------------------------------------

    private SocketsHttpHandler CreateHandler(bool ignoreCertificate)
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            // A proxy would be what the connect callback connects to, and the address check
            // would be checking the proxy.
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = ConnectAsync,
        };
        if (ignoreCertificate)
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        return handler;
    }

    /// <summary>
    /// Resolves, refuses a blocked address, then connects. Done here rather than by looking
    /// at the URL because a URL only names a host; this is the address actually dialled.
    /// </summary>
    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
            ? [literal]
            : await Resolve(host, ct);
        if (addresses.Length == 0)
            throw new SocketException((int)SocketError.HostNotFound);
        if (addresses.FirstOrDefault(IsBlocked) is { } blocked)
            throw new StepRefusedException(BlockedSentence(host, blocked));

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_verified.IsValueCreated)
            _verified.Value.Dispose();
        if (_unverified.IsValueCreated)
            _unverified.Value.Dispose();
    }
}

/// <summary>A step that was not sent because it must not be: a blocked address, a scheme that is not HTTP, a redirect loop.</summary>
public sealed class StepRefusedException(string message) : Exception(message);

/// <summary>What one step came to. The trace fields are only filled for "Run now".</summary>
public sealed class StepOutcome(int index, string name, string method)
{
    public int Index { get; } = index;
    public string Name { get; } = name;
    public string Method { get; } = method;
    public string Url { get; set; } = "";
    public int? Status { get; set; }
    public string Reason { get; set; } = "";
    public TimeSpan Elapsed { get; set; }
    public bool Passed { get; set; }

    /// <summary>Not run, because an earlier step failed.</summary>
    public bool Skipped { get; set; }

    /// <summary>"expected … — …", without the step's name in front.</summary>
    public string? Failure { get; set; }

    public List<(string Name, string Value)> RequestHeaders { get; set; } = [];
    public List<(string Name, string Value)> ResponseHeaders { get; set; } = [];
    public List<string> Redirects { get; } = [];
    public List<string> Saved { get; } = [];
    public string BodySample { get; set; } = "";
    public int BodyLength { get; set; }
    public bool Truncated { get; set; }

    /// <summary>One line for <see cref="ProbeResult.Details"/>: a status and a time, never a body, a cookie or a secret.</summary>
    public string Summary => Skipped
        ? "not run"
        : $"{(Status is { } code ? $"HTTP {code}" : "no answer")} in {Elapsed.TotalMilliseconds:0} ms — {(Passed ? "passed" : "failed")}";
}

/// <summary>A whole run: every step's outcome, and the sentence for the tile.</summary>
/// <param name="FailedStep">1-based; 0 when every step passed (or none ran because the check is empty).</param>
public sealed record StepCheckRun(
    bool Ok, string Message, TimeSpan Duration, IReadOnlyList<StepOutcome> Steps, int FailedStep, IReadOnlyList<CheckStep> Plan)
{
    /// <summary>
    /// As the monitor records it. Metrics go out on a failure too — <c>failed_step</c> is
    /// most useful exactly then, and an alert rule on "failed at step 3" needs a sample.
    /// </summary>
    public ProbeResult ToProbeResult()
    {
        var metrics = new Dictionary<string, double>
        {
            ["total_ms"] = Duration.TotalMilliseconds,
            ["steps_passed"] = Steps.Count(s => s.Passed),
            ["failed_step"] = FailedStep,
        };
        var keys = StepCheck.StepMetricKeys(Plan);
        foreach (var step in Steps.Where(s => !s.Skipped && s.Index < keys.Count))
            metrics[keys[step.Index]] = step.Elapsed.TotalMilliseconds;

        var details = new Dictionary<string, string>();
        foreach (var step in Steps)
            details[$"Step {step.Index + 1} · {step.Name}"] = step.Summary;

        return new ProbeResult(Ok, Message, Duration, Steps.Count == 0 ? null : metrics, details.Count == 0 ? null : details);
    }
}
