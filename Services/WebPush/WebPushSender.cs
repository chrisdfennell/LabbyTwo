using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LabbyTwo.Providers;
using LabbyTwo.Storage;

namespace LabbyTwo.Services.WebPush;

/// <summary>
/// What the service worker is handed. Kept small and flat: a push message holds under 4 KB
/// once encrypted, and the worker (push-sw.js) reads these names directly.
/// </summary>
/// <param name="Tag">
/// Notifications with the same tag replace each other on the device, so the recovery takes
/// the place of the outage rather than stacking beneath it.
/// </param>
/// <param name="Url">Opened when the notification is tapped, relative to the app.</param>
/// <param name="Urgent">Stays on screen until dismissed, rather than timing out.</param>
/// <param name="Renotify">Sound and vibrate again even though it replaces one already showing.</param>
/// <param name="Urgency">The RFC 8030 Urgency header: how hard the push service tries on a sleeping phone.</param>
public sealed record PushMessage(
    string Title,
    string Body,
    string? Tag = null,
    string Url = "/",
    bool Urgent = false,
    bool Renotify = false,
    string Urgency = "normal",
    TimeSpan? TimeToLive = null);

public enum PushResult
{
    Delivered,

    /// <summary>The push service says the subscription no longer exists, so it was deleted.</summary>
    Removed,

    Failed,
}

public sealed record PushOutcome(PushSubscription Device, PushResult Result, string Message);

/// <summary>
/// Sends one encrypted, VAPID-signed message to one subscription and keeps the stored list
/// honest about what the push service said: a device that has gone is deleted, a device
/// being rate-limited is left alone until it may be tried again.
/// </summary>
public sealed class WebPushSender(
    IHttpClientFactory httpFactory,
    VapidKeys keys,
    PushSubscriptionStore store,
    ILogger<WebPushSender> log)
{
    /// <summary>
    /// A cut body stays readable; a notification refused for being 5 bytes too long does not
    /// arrive at all. Well under the 4 KB ceiling so the title and JSON always fit beside it.
    /// </summary>
    public const int MaxBody = 1000;

    // Devices a push service has told us to leave alone for a while (HTTP 429). In memory,
    // because a restart is long enough for any Retry-After a push service actually sends.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _backoff = new();

    public async Task<IReadOnlyList<PushOutcome>> SendAsync(
        IEnumerable<PushSubscription> devices, PushMessage message, CancellationToken ct) =>
        await Task.WhenAll(devices.Select(device => SendAsync(device, message, ct)));

    public async Task<PushOutcome> SendAsync(PushSubscription device, PushMessage message, CancellationToken ct)
    {
        if (_backoff.TryGetValue(device.Id, out var until) && until > DateTimeOffset.UtcNow)
            return new PushOutcome(device, PushResult.Failed,
                $"Skipped: the push service asked for a pause until {until.ToLocalTime():HH:mm:ss}.");

        try
        {
            using var response = await PostAsync(device, message, ct);

            // A message too big for this push service. Everything here is sized to fit the
            // 4 KB every service promises, so this is one being stricter than the standard:
            // try once more with the body cut short rather than lose the alert altogether.
            if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge && message.Body.Length > 200)
            {
                using var retry = await PostAsync(device, message with { Body = Clip(message.Body, 200) }, ct);
                return await ConcludeAsync(device, retry, ct);
            }

            return await ConcludeAsync(device, response, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ArgumentException or FormatException or CryptographicException
                                   && !ct.IsCancellationRequested)
        {
            var text = $"Could not reach the push service: {ex.GetBaseException().Message}";
            await store.RecordAsync(device.Id, false, text, CancellationToken.None);
            return new PushOutcome(device, PushResult.Failed, text);
        }
    }

    private async Task<PushOutcome> ConcludeAsync(PushSubscription device, HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;

        if (response.IsSuccessStatusCode)
        {
            _backoff.TryRemove(device.Id, out _);
            await store.RecordAsync(device.Id, true, "", CancellationToken.None);
            return new PushOutcome(device, PushResult.Delivered, "Delivered to the push service.");
        }

        // 404 and 410 are the push service saying this subscription is gone for good — the
        // site's notifications were switched off, the browser was reset, the app removed from
        // the home screen. Sending to it again can only ever fail, so it goes.
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            await store.DeleteAsync(device.Id, CancellationToken.None);
            log.LogInformation("Push subscription {Device} has expired (HTTP {Status}) and was removed", device.Name, status);
            return new PushOutcome(device, PushResult.Removed,
                $"The push service says “{device.Name}” is no longer subscribed, so it was removed.");
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        var detail = body.Length > 0 ? " " + body[..Math.Min(body.Length, 200)].Trim() : "";

        string message;
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // Honoured rather than retried: hammering a push service that has asked for quiet
            // is how a sender's key gets blocked for everybody on this install.
            var wait = response.Headers.RetryAfter switch
            {
                { Delta: { } delta } => delta,
                { Date: { } date } => date - DateTimeOffset.UtcNow,
                _ => TimeSpan.FromMinutes(1),
            };
            wait = TimeSpan.FromSeconds(Math.Clamp(wait.TotalSeconds, 1, 3600));
            _backoff[device.Id] = DateTimeOffset.UtcNow + wait;
            message = $"The push service is rate-limiting this device; not trying again for {wait.TotalSeconds:0} seconds.";
        }
        else if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
        {
            message = "The push service refused the message as too large, even shortened.";
        }
        else if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // Almost always a subscription made with a different key — one from before a lost
            // keyring, or from another LabbyTwo on the same address — or a clock far enough
            // out that the token already looks expired.
            message = $"The push service rejected LabbyTwo's signature (HTTP {status}). Remove this device " +
                      "and press Notify this device on it again; if that does not help, check the server's clock.";
        }
        else
        {
            message = $"The push service answered HTTP {status}.{detail}";
        }

        await store.RecordAsync(device.Id, false, message, CancellationToken.None);
        return new PushOutcome(device, PushResult.Failed, message);
    }

    private async Task<HttpResponseMessage> PostAsync(PushSubscription device, PushMessage message, CancellationToken ct)
    {
        // https only. The endpoint came from a browser, and this server is about to post to
        // it — anything else is not a push service, whatever it claims.
        if (!Uri.TryCreate(device.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("The subscription's endpoint is not an https address.");

        var pair = await keys.GetAsync(ct);
        var token = Vapid.Token(pair.Key, Vapid.Audience(endpoint), Vapid.DefaultSubject,
            DateTimeOffset.UtcNow + Vapid.Lifetime);

        // A fresh key pair and salt per message, as RFC 8291 requires: reusing either would
        // let anyone watching the push service relate messages to each other.
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var body = WebPushCrypto.Encrypt(
            Payload(message),
            WebPushCrypto.FromBase64Url(device.P256dh),
            WebPushCrypto.FromBase64Url(device.Auth),
            ephemeral,
            RandomNumberGenerator.GetBytes(16));

        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentEncoding.Add("aes128gcm");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        request.Headers.TryAddWithoutValidation("Authorization", Vapid.AuthorizationHeader(token, pair.PublicKey));
        request.Headers.TryAddWithoutValidation("TTL",
            ((int)(message.TimeToLive ?? TimeSpan.FromHours(24)).TotalSeconds).ToString());
        request.Headers.TryAddWithoutValidation("Urgency", message.Urgency);
        if (Topic(message.Tag) is { } topic)
            request.Headers.TryAddWithoutValidation("Topic", topic);

        // The provider client, for its logger: an endpoint URL is a capability for that
        // device, and the default logging would write it out in full on every alert. Its
        // relaxed certificate check costs nothing here — the payload is encrypted end to end
        // to the browser, and the token names this push service and expires within hours.
        var http = httpFactory.CreateClient(ProviderHttp.ClientName);
        return await http.SendAsync(request, ct);
    }

    /// <summary>The JSON the service worker reads, cut down until it fits one message.</summary>
    public static byte[] Payload(PushMessage message)
    {
        var body = Clip(message.Body, MaxBody);
        while (true)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                title = Clip(message.Title, 200),
                body,
                tag = message.Tag,
                url = message.Url,
                urgent = message.Urgent,
                renotify = message.Renotify,
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });

            // Only a body made of multi-byte characters can still be too long here.
            if (bytes.Length <= WebPushCrypto.MaxPlaintext || body.Length == 0)
                return bytes;
            body = Clip(body, body.Length / 2);
        }
    }

    /// <summary>
    /// The Topic header lets a push service drop an undelivered message when a newer one with
    /// the same topic arrives — a phone off overnight wakes to "NAS is back", not both. The
    /// header allows 32 base64url characters, so the tag is hashed down to fit.
    /// </summary>
    public static string? Topic(string? tag) =>
        tag is { Length: > 0 }
            ? WebPushCrypto.ToBase64Url(SHA256.HashData(Encoding.UTF8.GetBytes(tag)))[..22]
            : null;

    private static string Clip(string text, int max) =>
        text.Length <= max ? text : max <= 1 ? "" : text[..(max - 1)] + "…";
}
