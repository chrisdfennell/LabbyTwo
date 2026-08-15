using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LabbyTwo.Core;
using LabbyTwo.Providers;
using LabbyTwo.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LabbyTwo.TerminalPlugin;

/// <summary>
/// Everything the terminal needs that is not a render: the emulator's own files, the page
/// it runs in, and the socket carrying the bytes.
///
/// The page is served here rather than by the tab kind for a reason worth stating.
/// Blazor owns the DOM inside a component and will happily discard anything JavaScript
/// put there on the next diff, and a terminal is nothing but DOM that JavaScript put
/// there. Serving a whole document and framing it means the emulator has a page of its
/// own that no re-render touches, its own keyboard focus, and a lifetime that ends when
/// the frame does.
/// </summary>
public sealed class TerminalEndpoints(
    ConfigStore config,
    DockerProvider docker,
    IOptions<LabbyOptions> options,
    ILogger<TerminalEndpoints> log) : IEndpointExtension
{
    public const string RouteKey = "terminal";

    public string Key => RouteKey;

    private static string Base => ExtensionRoutes.PathFor(RouteKey);

    /// <summary>
    /// The URL a tab or a card frames. Built here so the route and its callers cannot
    /// drift apart, and so the policy id always travels with the target.
    /// </summary>
    public static string ConsoleUrl(TerminalTarget target, string? tabId = null, string? widgetId = null) =>
        $"{Base}/console?target={Uri.EscapeDataString(target.Id)}" +
        (tabId is { Length: > 0 } tab ? $"&tab={Uri.EscapeDataString(tab)}" : "") +
        (widgetId is { Length: > 0 } widget ? $"&widget={Uri.EscapeDataString(widget)}" : "");

    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/assets/{name}", Asset);
        routes.MapGet("/console", ConsoleAsync);

        // LabbyTwo never calls UseWebSockets(), and a plugin cannot add middleware to the
        // application pipeline — by the time Map runs, the pipeline is built. It can build
        // one of its own though: CreateApplicationBuilder gives a pipeline that runs for
        // this endpoint alone, so the upgrade handshake exists here and nowhere else.
        // SignalR does exactly this for its hubs, which is why Blazor works without the
        // host calling it either.
        var pipeline = routes.CreateApplicationBuilder();
        pipeline.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        pipeline.Run(AttachAsync);

        // Mapped into the group, so it inherits the login the host put on it.
        routes.Map("/attach", pipeline.Build());
    }

    // ---- The emulator's own files ---------------------------------------------------

    private static readonly Dictionary<string, string> Assets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["xterm.js"] = "text/javascript; charset=utf-8",
        ["xterm.css"] = "text/css; charset=utf-8",
        ["addon-fit.js"] = "text/javascript; charset=utf-8",
        ["console.js"] = "text/javascript; charset=utf-8",
    };

    private static IResult Asset(string name, HttpContext context)
    {
        // A dictionary rather than a path join. The name comes out of a URL, and the only
        // safe way to turn a URL segment into a file is to not do it.
        if (!Assets.TryGetValue(name, out var contentType))
            return Results.NotFound();

        var stream = typeof(TerminalEndpoints).Assembly.GetManifestResourceStream($"terminal.{name}");
        if (stream is null)
            return Results.NotFound();

        // Private: these sit behind the app's login, so no shared cache should keep them.
        context.Response.Headers.CacheControl = "private, max-age=3600";
        return Results.Stream(stream, contentType);
    }

    // ---- The page ------------------------------------------------------------------

    private async Task<IResult> ConsoleAsync(
        HttpContext context, CancellationToken ct,
        string? target = null, string? tab = null, string? widget = null)
    {
        // Framed by LabbyTwo's own pages and by nothing else.
        context.Response.Headers.ContentSecurityPolicy = "frame-ancestors 'self'";

        if (Locked is { } locked)
            return Html(Page("Terminal", null, locked));

        var resolved = await ResolveAsync(target, tab, widget, ct);
        if (resolved.Error is { } error)
            return Html(Page("Terminal", null, error));

        var attach = $"{Base}/attach?target={Uri.EscapeDataString(resolved.Target!.Id)}" +
                     (tab is { Length: > 0 } ? $"&tab={Uri.EscapeDataString(tab)}" : "") +
                     (widget is { Length: > 0 } ? $"&widget={Uri.EscapeDataString(widget)}" : "");

        return Html(Page(resolved.Title, attach, null));
    }

    private static IResult Html(string html) => Results.Content(html, "text/html; charset=utf-8");

    /// <summary>
    /// A whole document, because that is the point — see the note on the class. The theme
    /// follows the browser rather than LabbyTwo's appearance setting: a frame cannot read
    /// its parent's stylesheet, and guessing wrong in the dark is worse than following
    /// the operating system.
    /// </summary>
    private static string Page(string title, string? attachUrl, string? message)
    {
        var body = message is null
            ? $"""
                 <div id="bar">
                   <span id="dot" class="pending"></span>
                   <span id="where">{Escape(title)}</span>
                   <button id="again" hidden>Reconnect</button>
                 </div>
                 <div id="screen"></div>
                 <script src="{Base}/assets/xterm.js"></script>
                 <script src="{Base}/assets/addon-fit.js"></script>
                 <script src="{Base}/assets/console.js"></script>
               """
            : $"""<div id="refused"><p>{Escape(message)}</p></div>""";

        return $"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>{Escape(title)}</title>
              <link rel="stylesheet" href="{Base}/assets/xterm.css">
              <style>{Css}</style>
            </head>
            <body data-attach="{Escape(attachUrl ?? "")}" data-where="{Escape(title)}">
            {body}
            </body>
            </html>
            """;
    }

    private const string Css = """
        :root { color-scheme: light dark; --ink: #1c1e21; --paper: #ffffff; --edge: #d8dbe0; --quiet: #5c6370; }
        @media (prefers-color-scheme: dark) {
          :root { --ink: #d7dae0; --paper: #16181d; --edge: #2c3038; --quiet: #8b93a1; }
        }
        html, body { height: 100%; margin: 0; background: var(--paper); color: var(--ink);
          font-family: system-ui, -apple-system, "Segoe UI", sans-serif; }
        body { display: flex; flex-direction: column; }
        #bar { display: flex; align-items: center; gap: .5rem; padding: .3rem .6rem;
          border-bottom: 1px solid var(--edge); font-size: .8rem; color: var(--quiet); flex: none; }
        #dot { width: .5rem; height: .5rem; border-radius: 50%; background: #9aa0a6; flex: none; }
        #dot.live { background: #3fb950; }
        #dot.gone { background: #f85149; }
        #where { flex: 1 1 auto; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
        #again { font: inherit; padding: .1rem .5rem; border: 1px solid var(--edge);
          border-radius: .25rem; background: transparent; color: inherit; cursor: pointer; }
        #screen { flex: 1 1 auto; min-height: 0; padding: .3rem; }
        #refused { padding: 1.25rem; max-width: 46rem; line-height: 1.5; white-space: pre-wrap; }
        """;

    // ---- The socket ----------------------------------------------------------------

    private async Task AttachAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("This endpoint is a WebSocket.");
            return;
        }

        // The one refusal that happens before the handshake. Everything else is reported
        // through the socket so the terminal can show it, but an install with no login is
        // not a place to open a socket at all — see the note on Locked.
        if (Locked is { } locked)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync(locked);
            log.LogWarning("Refused a terminal attach: LabbyTwo has no login configured.");
            return;
        }

        var query = context.Request.Query;
        var resolved = await ResolveAsync(query["target"], query["tab"], query["widget"], context.RequestAborted);

        using var socket = await context.WebSockets.AcceptWebSocketAsync();

        if (resolved.Error is { } error)
        {
            await SayAsync(socket, "error", error, CancellationToken.None);
            await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "refused");
            return;
        }

        var columns = Clamp(query["cols"], 80, 20, 500);
        var rows = Clamp(query["rows"], 24, 5, 200);
        var who = context.User.Identity?.Name ?? "someone";

        ITerminalSession session;
        try
        {
            session = await OpenAsync(resolved, columns, rows, context.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Terminal to {Target} would not open for {Who}", resolved.Target!.Id, who);
            await SayAsync(socket, "error", ex.GetBaseException().Message, CancellationToken.None);
            await CloseAsync(socket, WebSocketCloseStatus.InternalServerError, "failed");
            return;
        }

        // Deliberately Information rather than Debug. A shell being opened on your NAS is
        // the single most consequential thing this dashboard can do, and it should be
        // legible in `docker compose logs` afterwards without anyone having turned
        // anything on first.
        log.LogInformation("Terminal opened on {Target} by {Who}", session.Describe, who);
        var started = DateTimeOffset.Now;

        try
        {
            await PumpAsync(socket, session, resolved.Policy!, context.RequestAborted);
        }
        finally
        {
            await session.DisposeAsync();
            log.LogInformation("Terminal on {Target} closed after {Minutes:0.0} minutes",
                session.Describe, (DateTimeOffset.Now - started).TotalMinutes);
        }
    }

    private async Task<ITerminalSession> OpenAsync(
        Resolution resolved, int columns, int rows, CancellationToken ct)
    {
        var target = resolved.Target!;
        return target.IsDocker
            ? await DockerExec.OpenAsync(resolved.Connection!, target.Container,
                resolved.Policy!.ShellOrDefault, columns, rows, ct)
            : await SshSession.OpenAsync(resolved.Connection!, columns, rows, ct);
    }

    /// <summary>
    /// Bytes both ways until one end stops. Binary frames are the terminal stream in
    /// either direction; text frames are control, and there is exactly one of those going
    /// each way, which is as much protocol as this needs.
    /// </summary>
    private async Task PumpAsync(
        WebSocket socket, ITerminalSession session, TerminalPolicy policy, CancellationToken ct)
    {
        using var idle = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, idle.Token);

        var idleAfter = policy.IdleMinutes > 0 ? TimeSpan.FromMinutes(policy.IdleMinutes) : Timeout.InfiniteTimeSpan;
        void Touch()
        {
            if (idleAfter != Timeout.InfiniteTimeSpan)
                idle.CancelAfter(idleAfter);
        }

        Touch();
        await SayAsync(socket, "ready", session.Describe, linked.Token);

        var fromSession = Task.Run(() => ToBrowserAsync(socket, session, linked.Token), CancellationToken.None);
        var fromBrowser = ToSessionAsync(socket, session, Touch, linked.Token);

        var finished = await Task.WhenAny(fromSession, fromBrowser);

        // Disposing is what stops the other one. The SSH read blocks a thread that no
        // token can interrupt, and closing the stream underneath it is the only thing
        // that returns it — which is why this is a dispose rather than a cancel.
        await session.DisposeAsync();
        linked.Cancel();

        // Only worth explaining when this end stopped first. If the browser closed the
        // socket, it knows why.
        if (finished == fromSession)
        {
            await SayAsync(socket, "ended", idle.IsCancellationRequested
                ? $"Closed after {policy.IdleMinutes} minutes with nothing typed."
                : "The session ended.", CancellationToken.None);
        }

        // Either way, finish the handshake rather than dropping the connection. A browser
        // that closed politely should not see an abnormal closure for its trouble.
        await CloseAsync(socket, WebSocketCloseStatus.NormalClosure, "ended");

        await Task.WhenAny(Task.WhenAll(SafelyAsync(fromSession), SafelyAsync(fromBrowser)),
            Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    private static async Task ToBrowserAsync(WebSocket socket, ITerminalSession session, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (!ct.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await session.ReadAsync(buffer, ct);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // The far end hung up, or this end was disposed to make it. Both are the
                // ordinary way a terminal ends, not a fault worth logging.
                return;
            }

            if (read <= 0)
                return;

            await socket.SendAsync(buffer.AsMemory(0, read), WebSocketMessageType.Binary, true, ct);
        }
    }

    private async Task ToSessionAsync(
        WebSocket socket, ITerminalSession session, Action touch, CancellationToken ct)
    {
        var buffer = new byte[8 * 1024];
        var control = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer, ct);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                return;
            }

            if (result.MessageType == WebSocketMessageType.Close)
                return;

            touch();

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                // Written straight through, fragment by fragment. A terminal is a byte
                // stream and the order is what matters, so there is nothing to reassemble.
                await session.WriteAsync(buffer.AsMemory(0, result.Count), ct);
                continue;
            }

            control.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage)
                continue;

            var text = control.ToString();
            control.Clear();
            await ControlAsync(session, text, ct);
        }
    }

    private async Task ControlAsync(ITerminalSession session, string json, CancellationToken ct)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("t", out var kind) && kind.GetString() == "resize")
            {
                var columns = Clamp(document.RootElement, "cols", 80, 20, 500);
                var rows = Clamp(document.RootElement, "rows", 24, 5, 200);
                await session.ResizeAsync(columns, rows, ct);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Nothing else sends text frames, so this is a browser extension or somebody
            // poking at it. Neither is a reason to drop a working shell.
            log.LogDebug(ex, "Ignored an unreadable terminal control message");
        }
    }

    private static async Task SayAsync(WebSocket socket, string kind, string message, CancellationToken ct)
    {
        if (socket.State != WebSocketState.Open)
            return;

        var payload = JsonSerializer.SerializeToUtf8Bytes(new { t = kind, message });
        try
        {
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, ct);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }
    }

    private static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
    {
        try
        {
            // CloseReceived as well as Open: that is the state after the browser's close
            // frame has arrived, and it is exactly when the reply is owed.
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseAsync(status, reason, CancellationToken.None);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }
    }

    private static async Task SafelyAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
        }
    }

    // ---- Who may attach to what -----------------------------------------------------

    /// <summary>
    /// The refusal that is not about this target. A terminal on an install with no
    /// password is a root shell for anyone who can reach the port, which on a home LAN is
    /// every device on it and whatever they have installed. LabbyTwo's login being
    /// optional is right for a dashboard; it is not right for this, so this plugin
    /// requires it and says so rather than shipping a setting to turn the requirement off.
    /// </summary>
    private string? Locked => options.Value.Auth.Enabled
        ? null
        : "This terminal will not open because LabbyTwo has no login.\n\n" +
          "Anyone who can reach this dashboard would get a shell on whatever it is pointed at, so set " +
          "LABBY_AUTH_PASSWORD in your .env, run `docker compose up -d`, and it will work.";

    private sealed record Resolution(
        TerminalTarget? Target, Connection? Connection, TerminalPolicy? Policy, string Title, string? Error);

    private async Task<Resolution> ResolveAsync(
        string? target, string? tabId, string? widgetId, CancellationToken ct)
    {
        static Resolution No(string reason) => new(null, null, null, "Terminal", reason);

        if (TerminalTarget.Parse(target) is not { } parsed)
            return No("That is not a target this can open.");

        // The policy is looked up rather than trusted from the query string, which is the
        // whole reason a tab or widget id travels with the target: the picker on the page
        // is a convenience, and this is the thing that actually decides.
        TerminalPolicy policy;
        if (tabId is { Length: > 0 })
        {
            var tab = await config.TabAsync(tabId, ct);
            if (tab is null || tab.Kind != TerminalTabKind.KindKey)
                return No("That terminal page no longer exists.");
            policy = TerminalPolicy.ForTab(tab.Settings);
        }
        else if (widgetId is { Length: > 0 })
        {
            var widgets = await config.WidgetsAsync(ct);
            var widget = widgets.FirstOrDefault(w => w.Id == widgetId && w.Type == TerminalWidget.TypeKey);
            if (widget is null)
                return No("That terminal card no longer exists.");
            policy = TerminalPolicy.ForWidget(widget.Settings);
        }
        else
        {
            return No("A terminal has to be opened from a page or a card, which is what decides what it may reach.");
        }

        if (policy.Refuse(parsed) is { } refused)
            return No(refused);

        var connection = await config.ConnectionAsync(parsed.ConnectionId, ct);
        if (connection is null)
            return No("That connection has been deleted.");

        var expected = parsed.IsDocker ? "docker" : SshTypeKey;
        if (!string.Equals(connection.Provider, expected, StringComparison.OrdinalIgnoreCase))
            return No($"“{connection.Name}” is not a {expected} connection.");

        if (!connection.Enabled)
            return No($"“{connection.Name}” is disabled.");

        var title = parsed.IsDocker ? $"{parsed.Container} — {connection.Name}" : connection.Name;
        return new Resolution(parsed, connection, policy, title, null);
    }

    private const string SshTypeKey = "ssh";

    /// <summary>
    /// Every container on every Docker connection this policy would let you open, for the
    /// picker. Asked at render time rather than cached: a container that has just been
    /// restarted is exactly when somebody wants a shell in it.
    /// </summary>
    public async Task<IReadOnlyList<(Connection Docker, string Container, string Status)>> ContainersAsync(
        TerminalPolicy policy, CancellationToken ct)
    {
        var found = new List<(Connection, string, string)>();
        if (!policy.AllowDocker && policy.Pinned is null)
            return found;

        foreach (var connection in await config.ConnectionsAsync(ct))
        {
            if (!connection.Enabled || !string.Equals(connection.Provider, "docker", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                foreach (var container in await docker.ContainersAsync(connection, ct))
                {
                    // Only the running ones. `docker exec` on a stopped container fails,
                    // and offering it would be offering a button that cannot work.
                    if (!container.State.Equals("running", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (policy.Allows(TerminalTarget.Of(connection, container.Name)))
                        found.Add((connection, container.Name, container.Status));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One unreachable Docker host should not empty the list for the others.
                log.LogDebug(ex, "Could not list containers on {Connection}", connection.Name);
            }
        }

        return found;
    }

    private static int Clamp(string? raw, int fallback, int low, int high) =>
        int.TryParse(raw, out var value) ? Math.Clamp(value, low, high) : fallback;

    private static int Clamp(JsonElement element, string name, int fallback, int low, int high) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed)
            ? Math.Clamp(parsed, low, high)
            : fallback;

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
