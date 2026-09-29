using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LabbyTwo.Core;
using LabbyTwo.Providers;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.DataProtection;

namespace LabbyTwo.Services;

/// <summary>Which kind of outside service a heartbeat address belongs to, and so how to talk to it.</summary>
public enum HeartbeatKind
{
    /// <summary>Worked out from the address — see <see cref="OutsideAlarm.Detect"/>.</summary>
    Auto,

    /// <summary>healthchecks.io or a self-hosted Healthchecks: a POST with a body, and <c>/fail</c> for bad news.</summary>
    Healthchecks,

    /// <summary>An Uptime Kuma push monitor: <c>?status=up|down&amp;msg=…</c> on the push URL.</summary>
    UptimeKuma,

    /// <summary>
    /// Anything else that counts GETs — Better Stack heartbeats, Cronitor, a cron-job
    /// monitor of your own. It has no way to say "bad", so bad news is silence.
    /// </summary>
    Plain,
}

/// <summary>
/// What the user chose, as stored. <see cref="Url"/> is the only secret: for every service
/// this supports, the address alone is the credential — anybody holding it can report the
/// check as fine.
/// </summary>
public sealed record OutsideAlarmSettings(string Url, HeartbeatKind Kind, int IntervalSeconds, bool IncludeSummary)
{
    public const int DefaultIntervalSeconds = 60;

    /// <summary>
    /// The background runner's floor. Anything shorter would be rounded up anyway, and the
    /// services this talks to measure grace periods in minutes.
    /// </summary>
    public const int MinimumIntervalSeconds = 60;

    public const int MaximumIntervalSeconds = 3600;

    public static OutsideAlarmSettings Off => new("", HeartbeatKind.Auto, DefaultIntervalSeconds, true);

    public bool IsConfigured => Url.Length > 0;

    public TimeSpan Interval =>
        TimeSpan.FromSeconds(Math.Clamp(IntervalSeconds, MinimumIntervalSeconds, MaximumIntervalSeconds));

    /// <summary>The kind actually used: <see cref="HeartbeatKind.Auto"/> resolved from the address.</summary>
    public HeartbeatKind EffectiveKind => Kind == HeartbeatKind.Auto ? OutsideAlarm.Detect(Url) : Kind;
}

/// <summary>What to tell the outside service this time, if anything.</summary>
public enum HeartbeatSignal
{
    /// <summary>LabbyTwo is watching things properly.</summary>
    Up,

    /// <summary>LabbyTwo is running but its monitoring is not — the alarm should go off now.</summary>
    Fail,

    /// <summary>Nothing to say yet: the monitor has only just started and has not swept.</summary>
    None,
}

/// <summary>The monitor's health, reduced to what the heartbeat needs.</summary>
/// <param name="Reason">A sentence for the outside service's log and the Settings page.</param>
public sealed record MonitoringVerdict(HeartbeatSignal Signal, string Reason);

/// <summary>What happened the last time a ping was attempted. In memory only.</summary>
public sealed record OutsideAlarmStatus
{
    public DateTimeOffset? LastAttemptAt { get; init; }
    public bool LastOk { get; init; }
    public HeartbeatSignal LastSignal { get; init; } = HeartbeatSignal.None;
    public string LastMessage { get; init; } = "";

    public DateTimeOffset? LastSuccessAt { get; init; }

    /// <summary>The most recent failure, kept after a later success so an intermittent one is still visible.</summary>
    public string? LastError { get; init; }

    public DateTimeOffset? LastErrorAt { get; init; }

    public int ConsecutiveFailures { get; init; }

    /// <summary>Why the last scheduled tick sent what it did — including "nothing".</summary>
    public string Verdict { get; init; } = "";
}

/// <summary>
/// The dead man's switch. Every alert LabbyTwo can send depends on LabbyTwo being up and
/// the house having internet — which is exactly what a power cut, a tripped breaker or a
/// dead router takes away, all at once and without a word. The only thing that can notice
/// silence is something outside the house, so this sends a heartbeat to one on a timer and
/// leaves the alarming to it: Healthchecks, an Uptime Kuma push monitor on a VPS or a
/// friend's server, or anything else that expects a URL to be hit every so often.
///
/// A settings section rather than a connection, deliberately. A connection is something
/// LabbyTwo polls and draws: it would be a tile, count towards "12 up", and — the real
/// problem — send a "down" notice through the alert channels every time the internet
/// dropped, which is the one moment those channels cannot deliver and the one alert nobody
/// needs twice. This talks outwards only, never through the alert channels, and its
/// failures go to the log and to the Settings page.
///
/// The ping is only as good as what it vouches for, so it is not sent just because the
/// process is alive. It says "up" only when the monitor's sweeps are actually completing
/// (see <see cref="Judge"/>); a LabbyTwo whose monitoring has hung sends <c>/fail</c> where
/// the service understands one, and nothing at all where it does not, so a hung LabbyTwo
/// trips the alarm the same way a dark house does.
///
/// Nothing is sent until somebody pastes an address in. The address is the credential, so
/// it is stored encrypted with the same keyring as connection passwords and shown masked.
/// </summary>
public sealed class OutsideAlarm(
    AppSettingsStore settings,
    IDataProtectionProvider protection,
    IHttpClientFactory httpFactory,
    HealthMonitor monitor,
    ILogger<OutsideAlarm> log) : IBackgroundJob
{
    public const string UrlKey = "outside_alarm_url";
    public const string KindKey = "outside_alarm_kind";
    public const string IntervalKey = "outside_alarm_interval";
    public const string SummaryKey = "outside_alarm_summary";

    /// <summary>
    /// Per request. Well inside the one-minute tick, so a service that has stopped answering
    /// cannot make pings pile up behind each other.
    /// </summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private const string SecretPrefix = "enc:";

    private readonly IDataProtector _protector = protection.CreateProtector("LabbyTwo.OutsideAlarm");
    private readonly DateTimeOffset _created = DateTimeOffset.Now;
    private readonly SemaphoreSlim _sending = new(1, 1);
    private OutsideAlarmStatus _status = new();

    // ---- The job ------------------------------------------------------------------------

    public string Name => "outside-alarm";

    /// <summary>
    /// Every minute, whatever the chosen interval: the runner counts from process start and
    /// reads this once, so a longer interval is decided per tick in <see cref="TickAsync"/>
    /// instead, and changing it on the Settings page needs no restart.
    /// </summary>
    public TimeSpan Interval => TimeSpan.FromMinutes(1);

    /// <summary>Raised after every attempt so an open Settings page can redraw.</summary>
    public event Action? Changed;

    public OutsideAlarmStatus Status => Volatile.Read(ref _status);

    /// <summary>
    /// Never throws for a failed ping. The runner would log each one as an error and mark
    /// the job failed every minute for as long as the internet is out — which is precisely
    /// when the outside service is already doing the alarming.
    /// </summary>
    public Task RunAsync(CancellationToken ct) =>
        TickAsync(DateTimeOffset.Now, monitor.Status, monitor.Snapshot, ct);

    /// <summary>
    /// One tick: work out whether a ping is due and what it should say, and send it. The
    /// clock and the monitor's state are parameters so a test can play out a hung sweep or
    /// an hour of ticks without either.
    /// </summary>
    /// <returns>What was sent, or <see cref="HeartbeatSignal.None"/> if nothing was.</returns>
    public async Task<HeartbeatSignal> TickAsync(
        DateTimeOffset now, MonitorStatus status, IReadOnlyCollection<HealthMonitor.ProbeState> states, CancellationToken ct)
    {
        var chosen = await SettingsAsync(ct);
        if (!chosen.IsConfigured)
            return HeartbeatSignal.None;

        // Ten seconds of slack, for the same reason the monitor has some: ticks land a
        // little late, and without it a two-minute interval checked every minute would
        // miss by a hair and slip to three.
        if (Status.LastAttemptAt is { } last && now >= last && now - last < chosen.Interval - TimeSpan.FromSeconds(10))
            return HeartbeatSignal.None;

        var verdict = Judge(status, now, monitor.RestoreDeadline, _created);
        if (verdict.Signal == HeartbeatSignal.None)
        {
            Record(s => s with { Verdict = verdict.Reason });
            return HeartbeatSignal.None;
        }

        if (verdict.Signal == HeartbeatSignal.Fail && chosen.EffectiveKind == HeartbeatKind.Plain)
        {
            // A plain heartbeat has no way to say "bad". Staying quiet is how to say it:
            // the service's own timeout goes off, as it would for a dark house.
            Record(s => s with { Verdict = verdict.Reason + " Not pinging, so the outside service raises the alarm." });
            return HeartbeatSignal.None;
        }

        var body = verdict.Signal == HeartbeatSignal.Fail
            ? verdict.Reason
            : chosen.IncludeSummary ? Summary(states) : "";

        await SendAsync(chosen, verdict.Signal, body, now, verdict.Reason, ct);
        return verdict.Signal;
    }

    /// <summary>
    /// The Settings page's button. Sends right away, whatever the schedule, with the
    /// address in the form if one is there — so it can be tried before it is saved.
    /// </summary>
    /// <returns>A sentence saying what happened.</returns>
    public async Task<(bool Ok, string Message)> TestAsync(OutsideAlarmSettings? unsaved, CancellationToken ct)
    {
        var chosen = unsaved is { IsConfigured: true } ? unsaved : await SettingsAsync(ct);
        if (!chosen.IsConfigured)
            return (false, "Paste a ping address first.");

        var now = DateTimeOffset.Now;
        var verdict = Judge(monitor.Status, now, monitor.RestoreDeadline, _created);

        // A test is somebody checking the address works, so it is an "up" unless the monitor
        // is genuinely stuck — in which case sending up would be the one wrong answer.
        var signal = verdict.Signal == HeartbeatSignal.Fail ? HeartbeatSignal.Fail : HeartbeatSignal.Up;
        if (signal == HeartbeatSignal.Fail && chosen.EffectiveKind == HeartbeatKind.Plain)
            return (false, $"Not sent: {verdict.Reason}");

        var body = signal == HeartbeatSignal.Fail
            ? verdict.Reason
            : "Test ping from LabbyTwo's Settings page." + (chosen.IncludeSummary ? " " + Summary(monitor.Snapshot) : "");

        var (ok, message) = await SendAsync(chosen, signal, body, now, "Test ping.", ct);
        return (ok, ok ? $"Sent to {Mask(chosen.Url)} — {message}" : message);
    }

    private async Task<(bool Ok, string Message)> SendAsync(
        OutsideAlarmSettings chosen, HeartbeatSignal signal, string body, DateTimeOffset now, string verdict, CancellationToken ct)
    {
        await _sending.WaitAsync(ct);
        try
        {
            var (ok, message) = await PingAsync(chosen, signal, body, ct);
            var failures = ok ? 0 : Status.ConsecutiveFailures + 1;

            // Once when it starts failing and once when it recovers, not every minute: a
            // house with no internet would otherwise fill the log with the same line.
            if (!ok && failures == 1)
                log.LogWarning("Outside alarm: could not ping {Url}: {Message}", Mask(chosen.Url), message);
            else if (!ok)
                log.LogDebug("Outside alarm: still cannot ping {Url} ({Failures} in a row): {Message}",
                    Mask(chosen.Url), failures, message);
            else if (Status.ConsecutiveFailures > 0)
                log.LogInformation("Outside alarm: pinging {Url} again after {Failures} failure(s)",
                    Mask(chosen.Url), Status.ConsecutiveFailures);

            Record(s => s with
            {
                LastAttemptAt = now,
                LastOk = ok,
                LastSignal = signal,
                LastMessage = message,
                LastSuccessAt = ok ? now : s.LastSuccessAt,
                LastError = ok ? s.LastError : message,
                LastErrorAt = ok ? s.LastErrorAt : now,
                ConsecutiveFailures = failures,
                Verdict = verdict,
            });
            return (ok, message);
        }
        finally
        {
            _sending.Release();
        }
    }

    private void Record(Func<OutsideAlarmStatus, OutsideAlarmStatus> change)
    {
        Volatile.Write(ref _status, change(Status));
        Changed?.Invoke();
    }

    /// <summary>One request, turned into ok-or-a-reason. Never throws except on shutdown.</summary>
    private async Task<(bool Ok, string Message)> PingAsync(
        OutsideAlarmSettings chosen, HeartbeatSignal signal, string body, CancellationToken ct)
    {
        HttpRequestMessage request;
        try
        {
            request = BuildRequest(chosen, signal, body);
        }
        catch (UriFormatException)
        {
            return (false, "That is not a web address LabbyTwo can send to.");
        }

        using (request)
        using (var window = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            window.CancelAfter(RequestTimeout);
            try
            {
                var http = httpFactory.CreateClient(ProviderHttp.ClientName);
                using var response = await http.SendAsync(request, window.Token);
                var text = await response.Content.ReadAsStringAsync(window.Token);
                return Interpret(chosen.EffectiveKind, response.StatusCode, text);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return (false, $"No answer within {RequestTimeout.TotalSeconds:0} seconds.");
            }
            catch (HttpRequestException ex)
            {
                // Deliberately not ex.ToString(): some handlers put the full address, token
                // and all, into the message of the inner exception.
                return (false, ex.HttpRequestError switch
                {
                    HttpRequestError.NameResolutionError => "Could not look up the service's name — is the internet down?",
                    HttpRequestError.ConnectionError => "Could not connect to the service.",
                    HttpRequestError.SecureConnectionError => "The secure connection to the service failed.",
                    _ => "The request failed before the service answered.",
                });
            }
        }
    }

    /// <summary>
    /// Whether the service took it. Healthchecks answers plain "OK"; Uptime Kuma always
    /// answers JSON with an <c>ok</c> flag and a reason, sometimes with a 200 even when it
    /// refused, so its flag is believed over the status code.
    /// </summary>
    public static (bool Ok, string Message) Interpret(HeartbeatKind kind, HttpStatusCode code, string body)
    {
        var said = body.Trim();
        if (said.Length > 200)
            said = said[..200] + "…";

        if (kind == HeartbeatKind.UptimeKuma && said.StartsWith('{'))
        {
            try
            {
                using var json = JsonDocument.Parse(said);
                if (json.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    var msg = json.RootElement.TryGetProperty("msg", out var m) && m.ValueKind == JsonValueKind.String
                        ? m.GetString() : null;
                    return ok.GetBoolean()
                        ? (true, "Uptime Kuma accepted it.")
                        : (false, $"Uptime Kuma refused it: {msg ?? "no reason given"}. Is the push monitor active?");
                }
            }
            catch (JsonException)
            {
                // Fall through to the status code.
            }
        }

        if ((int)code is >= 200 and < 300)
        {
            // Healthchecks answers 200 "OK (not found)" on older versions for an unknown
            // check — accepted, and ignored. Worth saying rather than calling it a success.
            if (kind == HeartbeatKind.Healthchecks && said.Contains("not found", StringComparison.OrdinalIgnoreCase))
                return (false, "The service answered, but does not know this check. Copy the ping URL again.");
            return (true, $"HTTP {(int)code}{(said.Length is > 0 and <= 40 ? $" {said}" : "")}");
        }

        return (false, code switch
        {
            HttpStatusCode.NotFound => "HTTP 404 — the service does not know this address. Copy the ping URL again.",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => $"HTTP {(int)code} — the service refused it.",
            HttpStatusCode.TooManyRequests => "HTTP 429 — pinging too often for the service's rate limit.",
            _ => $"HTTP {(int)code}{(said.Length > 0 ? $": {said}" : "")}",
        });
    }

    // ---- Deciding what to say -------------------------------------------------------------

    /// <summary>
    /// Whether the monitor is doing its job, by the same thresholds as the health page
    /// (<see cref="SystemHealth.Assess"/>). Pure, so every threshold can be tested with a
    /// made-up status and a made-up clock.
    ///
    /// "Starting" sends nothing rather than "up": the first sweep has not happened, so
    /// there is nothing to vouch for yet, and the outside service's grace period is far
    /// longer than a start-up.
    /// </summary>
    /// <param name="since">When this process started waiting for the monitor — the stand-in for "since start-up".</param>
    public static MonitoringVerdict Judge(MonitorStatus m, DateTimeOffset now, TimeSpan restoreDeadline, DateTimeOffset since)
    {
        var period = m.SweepPeriod;
        var firstSweepGrace = restoreDeadline + TimeSpan.FromSeconds(2) + period * 2;

        if (m.StartedAt is null)
            return now - since > firstSweepGrace
                ? new(HeartbeatSignal.Fail, "LabbyTwo is running, but its monitor never started.")
                : new(HeartbeatSignal.None, "Starting up — the monitor has not started yet.");

        if (m.IsSweepStuck(now))
            return new(HeartbeatSignal.Fail,
                $"LabbyTwo's monitoring is stuck: a sweep has been running for {SystemHealth.Seconds(m.RunningFor(now)!.Value)}, " +
                $"and should take well under {SystemHealth.Seconds(period)}.");

        if (m.LastSweepFinished is null)
        {
            // Either none has started, or the first is running but not yet stuck.
            var waited = now - m.StartedAt.Value;
            return waited > firstSweepGrace && m.CurrentSweepStarted is null
                ? new(HeartbeatSignal.Fail,
                    $"LabbyTwo's monitor has not finished a single sweep in the {SystemHealth.Seconds(waited)} since it started.")
                : new(HeartbeatSignal.None, "Starting up — the first sweep has not finished yet.");
        }

        if (m.CurrentSweepStarted is null && now - m.LastSweepFinished.Value > period * 3)
            return new(HeartbeatSignal.Fail,
                $"LabbyTwo's monitor has stopped: the last sweep finished {SystemHealth.Seconds(now - m.LastSweepFinished.Value)} ago, " +
                $"and one should start every {SystemHealth.Seconds(period)}.");

        return new(HeartbeatSignal.Up, "Monitoring is running normally.");
    }

    /// <summary>
    /// "12 up, 1 down" — counts only. No names, no addresses, nothing from a probe's message:
    /// this leaves the house, and the outside service's log is not somewhere to describe
    /// what is on the network.
    /// </summary>
    public static string Summary(IReadOnlyCollection<HealthMonitor.ProbeState> states)
    {
        var up = states.Count(s => s.IsUp == true);
        var down = states.Count(s => s.IsUp == false);
        var checking = states.Count(s => s.IsUp is null);

        var parts = new List<string> { $"{up} up", $"{down} down" };
        if (checking > 0)
            parts.Add($"{checking} checking");
        return string.Join(", ", parts);
    }

    // ---- Addresses -------------------------------------------------------------------------

    /// <summary>
    /// Which service an address belongs to. Uptime Kuma's push URLs always have
    /// <c>/api/push/</c>; Healthchecks' are on hc-ping.com or, self-hosted, under
    /// <c>/ping/</c>. Anything else is treated as a plain heartbeat, which is the safe
    /// guess — a GET is what every one of them accepts.
    /// </summary>
    public static HeartbeatKind Detect(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return HeartbeatKind.Plain;

        var path = uri.AbsolutePath;
        if (path.Contains("/api/push/", StringComparison.OrdinalIgnoreCase))
            return HeartbeatKind.UptimeKuma;
        if (uri.Host.Equals("hc-ping.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".hc-ping.com", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/ping/", StringComparison.OrdinalIgnoreCase))
            return HeartbeatKind.Healthchecks;
        return HeartbeatKind.Plain;
    }

    /// <summary>Whether an address could be pinged at all. http or https, absolute, no more.</summary>
    public static string? Problem(string url)
    {
        if (url.Trim().Length == 0)
            return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return "Paste the whole ping address, starting with https://.";
        return null;
    }

    /// <summary>
    /// The request for one ping. Public so the shapes each service expects are pinned down
    /// by tests rather than by reading a README.
    /// </summary>
    public static HttpRequestMessage BuildRequest(OutsideAlarmSettings chosen, HeartbeatSignal signal, string body)
    {
        var uri = new Uri(chosen.Url.Trim(), UriKind.Absolute);
        switch (chosen.EffectiveKind)
        {
            case HeartbeatKind.Healthchecks:
            {
                // Whatever was pasted — the plain URL, or one already ending /fail, /start or
                // an exit code — the base is the check, and the signal is added here.
                var builder = new UriBuilder(uri);
                var path = builder.Path.TrimEnd('/');
                var lastSlash = path.LastIndexOf('/');
                var tail = lastSlash >= 0 ? path[(lastSlash + 1)..] : "";
                if (tail is "fail" or "start" or "log" || (tail.Length is > 0 and <= 3 && tail.All(char.IsAsciiDigit)))
                    path = path[..lastSlash];
                builder.Path = signal == HeartbeatSignal.Fail ? path + "/fail" : path;

                // A body is what Healthchecks shows beside each ping in its log, which is
                // where "why did it fail" belongs. Without one, a GET is the documented form.
                if (body.Length == 0)
                    return new HttpRequestMessage(HttpMethod.Get, builder.Uri);
                return new HttpRequestMessage(HttpMethod.Post, builder.Uri)
                {
                    Content = new StringContent(body, Encoding.UTF8, "text/plain"),
                };
            }

            case HeartbeatKind.UptimeKuma:
            {
                // Kuma's push page hands out the URL with ?status=up&msg=OK&ping= already on
                // it. Those are replaced rather than added to; anything else is kept.
                var kept = (uri.Query.TrimStart('?').Length == 0 ? [] : uri.Query.TrimStart('?').Split('&'))
                    .Where(pair => pair.Length > 0)
                    .Where(pair => pair.Split('=')[0].ToLowerInvariant() is not ("status" or "msg" or "ping"));
                var message = body.Length > 0 ? body : "OK";
                var query = string.Join('&', kept.Concat(
                [
                    "status=" + (signal == HeartbeatSignal.Fail ? "down" : "up"),
                    "msg=" + Uri.EscapeDataString(message.Length > 250 ? message[..250] : message),
                    "ping=",
                ]));
                var builder = new UriBuilder(uri) { Query = query };
                return new HttpRequestMessage(HttpMethod.Get, builder.Uri);
            }

            default:
                // Nothing added: the address is the whole contract, and plenty of these
                // services reject a query string they do not recognise.
                return new HttpRequestMessage(HttpMethod.Get, uri);
        }
    }

    /// <summary>
    /// The address with its token hidden, for the page and the log. The host stays — which
    /// service it goes to is not a secret, and it is how somebody recognises their own —
    /// and so do the fixed words of each service's path. Every other segment is dots and its
    /// last four characters, enough to tell two checks apart, and a query is dropped outright.
    /// </summary>
    public static string Mask(string url)
    {
        if (url.Trim().Length == 0)
            return "";
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return "••••";

        string[] fixedWords = ["ping", "api", "push", "fail", "start", "v1", "v2", "v3", "heartbeat", "heartbeats"];
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => fixedWords.Contains(segment.ToLowerInvariant())
                ? segment
                : segment.Length >= 12 ? "••••" + segment[^4..] : "••••");

        var port = uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
        var path = string.Join('/', segments);
        return $"{uri.Scheme}://{uri.Host}{port}/{path}{(uri.Query.Length > 1 ? "?…" : "")}";
    }

    // ---- Settings ---------------------------------------------------------------------------

    /// <summary>What is saved, with the address decrypted. One that no longer decrypts reads as not set.</summary>
    public async Task<OutsideAlarmSettings> SettingsAsync(CancellationToken ct = default)
    {
        var bag = await settings.AllAsync(ct);
        return new OutsideAlarmSettings(
            Unprotect(bag.Get(UrlKey)),
            Enum.TryParse<HeartbeatKind>(bag.Get(KindKey), ignoreCase: true, out var kind) ? kind : HeartbeatKind.Auto,
            int.TryParse(bag.Get(IntervalKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                ? seconds : OutsideAlarmSettings.DefaultIntervalSeconds,
            bag.Get(SummaryKey, "true") != "false");
    }

    /// <summary>Saves the lot. A blank address switches the alarm off; nothing else is sent after that.</summary>
    public async Task SaveAsync(OutsideAlarmSettings chosen, CancellationToken ct = default)
    {
        await settings.SaveAsync(new Dictionary<string, string>
        {
            [UrlKey] = Protect(chosen.Url.Trim()),
            [KindKey] = chosen.Kind.ToString(),
            [IntervalKey] = Math.Clamp(chosen.IntervalSeconds, OutsideAlarmSettings.MinimumIntervalSeconds,
                OutsideAlarmSettings.MaximumIntervalSeconds).ToString(CultureInfo.InvariantCulture),
            [SummaryKey] = chosen.IncludeSummary ? "true" : "false",
        }, ct);

        // A new address starts a new history: its first ping should go on the next tick,
        // not wait out the old one's interval, and the old one's errors are not its errors.
        Record(_ => new OutsideAlarmStatus());
    }

    private string Protect(string value) =>
        value.Length == 0 ? "" : SecretPrefix + _protector.Protect(value);

    private string Unprotect(string value)
    {
        if (!value.StartsWith(SecretPrefix, StringComparison.Ordinal))
            return "";
        try
        {
            return _protector.Unprotect(value[SecretPrefix.Length..]);
        }
        catch (CryptographicException)
        {
            return "";
        }
    }
}
