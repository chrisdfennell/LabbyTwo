using Microsoft.Extensions.Http.Logging;

namespace LabbyTwo.Providers;

/// <summary>
/// The request log for the provider clients, with the credentials taken out of it.
///
/// The factory's own logging writes the full URI of every request at Information, which is
/// the level this app runs at. For a good number of integrations the URI *is* the
/// credential: IFTTT's key is a path segment, a Discord or Slack webhook's token is the
/// path, and several services take an API key in the query string. Every alert that fired
/// and every probe of those services was writing a working key to the console, and from
/// there into <c>docker logs</c> and whatever collects them.
///
/// This replaces that logging rather than turning it down. Setting the
/// "System.Net.Http.HttpClient" category to Warning would also have stopped the leak, but it
/// throws away the one line that says a request was made, to where, and what came back —
/// which is most of what anyone debugging an integration needs — and anyone who turned the
/// category back up to see it would get the keys back with it. Here the method, the scheme,
/// host and port, the status and the time survive; the path and query, where secrets live,
/// do not. One line per request rather than the factory's four, too.
/// </summary>
public sealed class ProviderHttpLogger(ILogger<ProviderHttpLogger> log) : IHttpClientLogger
{
    /// <summary>Where a request went, without anything after the authority.</summary>
    public static string Redact(Uri? uri) =>
        uri is null ? "(no address)"
        : uri.IsAbsoluteUri ? uri.GetLeftPart(UriPartial.Authority)
        : "(relative address)";

    public object? LogRequestStart(HttpRequestMessage request) => null;

    public void LogRequestStop(object? context, HttpRequestMessage request, HttpResponseMessage response, TimeSpan elapsed) =>
        log.LogInformation("HTTP {Method} {Origin} answered {Status} in {Elapsed:0}ms",
            request.Method, Redact(request.RequestUri), (int)response.StatusCode, elapsed.TotalMilliseconds);

    public void LogRequestFailed(object? context, HttpRequestMessage request, HttpResponseMessage? response,
        Exception exception, TimeSpan elapsed)
    {
        // The message rather than the exception: an HttpRequestException's message names the
        // host and the reason, and its stack trace is the same every time. Nor is it logged
        // as a warning — the probe that made the request already reports the service as
        // down, and a service that is off for the night would otherwise fill the log.
        log.LogInformation("HTTP {Method} {Origin} failed after {Elapsed:0}ms: {Reason}",
            request.Method, Redact(request.RequestUri), elapsed.TotalMilliseconds,
            exception.GetBaseException().Message);
    }
}
