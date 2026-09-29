using LabbyTwo.Components;
using LabbyTwo.Core;
using LabbyTwo.Storage;
using LabbyTwo.Providers;
using LabbyTwo.Services;
using LabbyTwo.Services.Import;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// A dashboard is left open for days, mostly in a background tab or on a screen that
// sleeps, and the defaults are tuned for a form someone is filling in. Browsers slow a
// hidden tab's timers to about once a minute, so the client's 15-second keep-alive can
// arrive late: the server waits two minutes before calling it gone instead of thirty
// seconds. And a laptop lid closed over lunch should resume the same session rather than
// reload, so a dropped circuit is kept for half an hour instead of three minutes.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(circuit =>
        circuit.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(30))
    .AddHubOptions(hub => hub.ClientTimeoutInterval = TimeSpan.FromMinutes(2));

builder.Services.Configure<LabbyOptions>(builder.Configuration.GetSection(LabbyOptions.SectionName));
var options = builder.Configuration.GetSection(LabbyOptions.SectionName).Get<LabbyOptions>() ?? new LabbyOptions();

// Keys live beside the database so a backup of the data volume can still decrypt the
// credentials inside it. Losing the keyring only costs re-entering passwords.
var dataDirectory = Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath, builder.Environment.ContentRootPath))!;
Directory.CreateDirectory(dataDirectory);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")))
    .SetApplicationName("LabbyTwo");

// One HTTP client for every provider. Home lab services routinely use self-signed
// certificates, and a certificate complaint must not be reported as "the service is down".
//
// The factory's own request logging is swapped for one that leaves the path and query out
// of the log. For IFTTT, webhooks and anything taking a key as a URL parameter, those are
// where the credential is — see ProviderHttpLogger for why this and not a quieter level.
builder.Services.AddHttpClient(ProviderHttp.ClientName)
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(ProviderHandler)
    .RemoveAllLoggers()
    .AddLogger(ProviderLogger);

// The same client with the clock taken off, for the few things that move files rather
// than JSON. Thirty seconds is right for a probe and wrong for a four-gigabyte download:
// the caller's CancellationToken — the browser hanging up — is the real bound there.
builder.Services.AddHttpClient(ProviderHttp.TransferClientName)
    .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(ProviderHandler)
    .RemoveAllLoggers()
    .AddLogger(ProviderLogger);

static ProviderHttpLogger ProviderLogger(IServiceProvider services) =>
    new(services.GetRequiredService<ILogger<ProviderHttpLogger>>());

static SocketsHttpHandler ProviderHandler() => new()
{
    SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
    AllowAutoRedirect = true,
    MaxAutomaticRedirections = 5,
};

// ---- Extension points -------------------------------------------------------------
// Nothing is listed here. Every public class implementing IConnectionProvider,
// IWidgetType, ITabKind, IDashboardImporter, IEndpointExtension or IBackgroundJob is found by reflection —
// in this assembly and in any DLL dropped into the plugins folder — so adding an
// integration is one file and no registration, whether you are editing LabbyTwo or
// shipping a plugin for it.

var pluginDirectory = Path.GetFullPath(options.PluginPath, builder.Environment.ContentRootPath);

// Discovery runs before the host exists, so it needs a logger of its own — a plugin that
// fails to load must say so on the console, not only on the Settings page.
using (var startupLogging = LoggerFactory.Create(logging => logging.AddConsole()))
{
    var startupLog = startupLogging.CreateLogger("Modules");

    // Before discovery, not after, and this is the whole reason it is here rather than in a
    // background job: replacing a DLL that is already loaded does nothing until the next
    // restart. Updating afterwards would leave every plugin stale for the entire life of the
    // container that updated them — which, with Watchtower restarting things unattended, is
    // every container. A few seconds here means one restart is enough.
    //
    // Off unless asked for, and it cannot fail the boot: UpdateAsync never throws, and the
    // timeout is what stops a slow GitHub from holding the dashboard down.
    if (await PluginAutoUpdate.EnabledAsync(options, builder.Environment))
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        using var window = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        var result = await PluginUpdater.UpdateAsync(
            pluginDirectory, Modules.Stamp(typeof(Program).Assembly), http, startupLog, window.Token);

        if (result.Updated.Count > 0)
            startupLog.LogInformation("Updated {Count} plugin(s): {Names}", result.Updated.Count, string.Join(", ", result.Updated));
        else if (result.Reason is { Length: > 0 } reason)
            startupLog.LogInformation("Plugins not updated: {Reason}", reason);
    }

    builder.Services.AddModules(typeof(Program).Assembly, pluginDirectory, startupLog);
}

builder.Services.AddSingleton<Registry>();

builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<AppSettingsStore>();
builder.Services.AddSingleton<DisplayUnits>();
builder.Services.AddSingleton<AlertRuleStore>();
builder.Services.AddSingleton<MuteWindowStore>();
builder.Services.AddSingleton<FiringAlertStore>();
builder.Services.AddSingleton<ConfigStore>();
builder.Services.AddSingleton<HistoryStore>();
builder.Services.AddSingleton<LatestReadings>();
builder.Services.AddSingleton<Offload>();
builder.Services.AddSingleton<SharedSeries>();
builder.Services.AddSingleton<NotesStore>();
builder.Services.AddSingleton<FontStore>();
builder.Services.AddSingleton<Markdown>();
builder.Services.AddSingleton<Seeder>();
builder.Services.AddSingleton<ConfigTransfer>();
builder.Services.AddSingleton<ShareTransfer>();
builder.Services.AddSingleton<TemplateStore>();
builder.Services.AddSingleton<TabTemplates>();
builder.Services.AddSingleton<FaviconService>();
builder.Services.AddSingleton<UpdateChecker>();
builder.Services.AddSingleton<SelfUpdater>();

// Whether each container's registry has published something newer. Asked only when
// somebody presses "Check for updates", or on the schedule if they chose one; the job that
// runs the schedule is an IBackgroundJob and is discovered.
builder.Services.AddSingleton<ImageRegistry>();
builder.Services.AddSingleton<ContainerUpdates>();
builder.Services.AddSingleton<DashboardImportService>();

// Copies the nightly backup off this machine. Not discovered: the backup job calls it.
builder.Services.AddSingleton<LabbyTwo.Services.Offsite.OffsiteSettingsStore>();
builder.Services.AddSingleton<LabbyTwo.Services.Offsite.OffsiteBackups>();

// The only scoped pair in here, and deliberately. An undo offer is one person's last
// action rather than a fact about the installation: as a singleton it would survive across
// browser sessions and, with auth switched on, offer your deletion to somebody else.
// Scoped means one circuit, which is exactly the span a "you just did this" bar should
// live for. Deletions is scoped only because it holds the UndoService.
builder.Services.AddScoped<UndoService>();
builder.Services.AddScoped<Deletions>();

// Not a provider — it answers a question the Settings page asks once, rather than
// something to monitor — so it is registered by hand rather than discovered.
builder.Services.AddSingleton<Geocoder>();

// One place every action button goes through, so the timeout, the log line and the
// silence that stops a reboot you asked for from paging you happen once rather than in
// every card that grows a button.
builder.Services.AddSingleton<ActionRunner>();

// Gathers every Media and Downloads connection for the media tab. Registered by hand
// rather than discovered: it answers a page's question, it is not itself an extension.
builder.Services.AddSingleton<MediaStack>();

// What the weekly summary says. The job that sends it is an IBackgroundJob and is
// discovered; this is the part it and the Settings preview share.
builder.Services.AddSingleton<WeeklySummaryGatherer>();

// Web Push: the devices that asked for notifications, this install's signing key, and the
// sender the Browser push channel and the devices page share.
builder.Services.AddSingleton<PushSubscriptionStore>();
builder.Services.AddSingleton<LabbyTwo.Services.WebPush.VapidKeys>();
builder.Services.AddSingleton<LabbyTwo.Services.WebPush.WebPushSender>();

builder.Services.AddSingleton<HealthMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HealthMonitor>());
builder.Services.AddSingleton<AlertService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AlertService>());
builder.Services.AddSingleton<MetricAlertService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MetricAlertService>());

// The "what changed" feed and the incidents built from it. The stores are ordinary
// singletons a plugin can ask for — a plugin that notices something changing records it
// with ChangeStore.RecordAsync. The watcher listens to the monitor and the evaluator and
// writes the feed; the tracker listens to the feed and groups outages into incidents.
builder.Services.AddSingleton<ChangeStore>();
builder.Services.AddSingleton<IncidentStore>();
builder.Services.AddSingleton<ChangeWatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ChangeWatcher>());
builder.Services.AddSingleton<IncidentTracker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IncidentTracker>());
// "Probably caused by" for each incident, read from the feed and the lab's shape, and the
// one-click write-up that turns an incident into a note.
builder.Services.AddSingleton<ProbableCauses>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProbableCauses>());
builder.Services.AddSingleton<IncidentWriteUps>();

// Anything a module contributed as an IBackgroundJob. One runner for all of them, so a
// plugin's nightly tidy-up cannot hang startup or take the process down with it.
builder.Services.AddSingleton<BackgroundJobRunner>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BackgroundJobRunner>());

// LabbyTwo's own health: what the monitor and the jobs have been doing, for the health
// page and /api/health/details. Reads what the services above already keep in memory.
builder.Services.AddSingleton<SystemHealth>();
builder.Services.AddSingleton<DnsCheck>();

// Login is opt-in: setting a password turns it on, otherwise LabbyTwo stays open on a
// trusted LAN, which is how most home labs actually run.
var authEnabled = options.Auth.Enabled;

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(cookie =>
    {
        cookie.Cookie.Name = "labbytwo.auth";
        cookie.LoginPath = "/login";
        cookie.ExpireTimeSpan = TimeSpan.FromDays(30);
        cookie.SlidingExpiration = true;
    });
// A fallback policy rather than RequireAuthorization() on the Blazor endpoint: the
// endpoint covers every page including /login, so requiring auth there sent the login
// page to itself forever. A fallback applies only to endpoints that carry no policy of
// their own, which lets [AllowAnonymous] on a page opt back out.
builder.Services.AddAuthorization(auth =>
{
    if (authEnabled)
        auth.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});
builder.Services.AddCascadingAuthenticationState();

// Guessing the password is slowed per client address and for the install as a whole. See
// LoginThrottle for the numbers and why they live in memory.
builder.Services.AddSingleton<LoginThrottle>();

// Behind a TLS-terminating reverse proxy, honour its scheme and client-address headers —
// but only from the proxies named in Labby:Proxy:TrustedProxies. The client address is
// what the login throttle counts against, and a header believed from anybody would let
// an attacker choose a new one for every guess.
var unreadableProxyEntries = ForwardedHeadersSetup.Apply(new ForwardedHeadersOptions(), options.Proxy);
builder.Services.Configure<ForwardedHeadersOptions>(forwarded => ForwardedHeadersSetup.Apply(forwarded, options.Proxy));

var app = builder.Build();

if (unreadableProxyEntries.Count > 0)
    app.Logger.LogWarning("Ignored {Entries} in Labby:Proxy:TrustedProxies: not an address or a CIDR range",
        string.Join(", ", unreadableProxyEntries));

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// Create the schema before the first request rather than on it, so a cold start doesn't
// race the probe loop for the file.
await app.Services.GetRequiredService<Db>().EnsureSchemaAsync();

// Built now rather than by the first card that asks, so it is already listening when the
// monitor's first sweep lands and that sweep's readings are in memory for the first page.
app.Services.GetRequiredService<LatestReadings>();

// The units every card draws its readings in, read before the first page rather than by
// it, so nobody who reads in Celsius sees a page of Fahrenheit flash past first.
await app.Services.GetRequiredService<DisplayUnits>().RefreshAsync();

// Liveness probe for Docker and monitoring; always anonymous.
app.MapGet("/healthz", () => Results.Text("ok")).AllowAnonymous();

// Bookmark icons. Fetched by the server so a LAN-only service still gets an icon on a
// phone, and cached so a dashboard of thirty links is not thirty requests per refresh.
var favicon = app.MapGet("/api/favicon", async (FaviconService icons, string url, CancellationToken ct) =>
{
    // A site with no icon still gets a picture rather than a 404: the bookmark's <img> has a
    // fixed size, and a 404 in it is a broken-image frame, not the alt text.
    var icon = await icons.GetAsync(url, ct);
    if (!icon.Found)
        icon = FaviconService.Icon.Placeholder;
    return Results.File(icon.Bytes, icon.ContentType);
});

// Downloads run outside the Blazor circuit, so they are minimal-API endpoints the
// Settings page simply links to.
var export = app.MapGet("/api/export", async (ConfigTransfer transfer, CancellationToken ct, bool secrets = false) =>
{
    var json = await transfer.ExportAsync(secrets, ct);
    var suffix = secrets ? "-with-secrets" : "";
    return Results.File(System.Text.Encoding.UTF8.GetBytes(json), "application/json",
        $"labbytwo-config{suffix}-{DateTimeOffset.Now:yyyy-MM-dd}.json");
});

// One tab, or one card, as a file to hand somebody. Downloads rather than component
// renders for the same reason the whole-config export is: a browser saving a file is not
// something a Blazor circuit can do.
var shareTab = app.MapGet("/api/share/tab", async (ShareTransfer share, string id, CancellationToken ct) =>
{
    try
    {
        var (json, name) = await share.ExportTabAsync(id, ct);
        return Results.File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", name);
    }
    catch (InvalidOperationException ex)
    {
        return Results.NotFound(ex.Message);
    }
});

var shareWidget = app.MapGet("/api/share/widget", async (ShareTransfer share, string id, CancellationToken ct) =>
{
    try
    {
        var (json, name) = await share.ExportWidgetAsync(id, ct);
        return Results.File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", name);
    }
    catch (InvalidOperationException ex)
    {
        return Results.NotFound(ex.Message);
    }
});

// A saved template as a file, for the same reason: it is a shared-tab file with a name.
var shareTemplate = app.MapGet("/api/share/template", async (TabTemplates templates, string id, CancellationToken ct) =>
{
    try
    {
        var (json, name) = await templates.ExportAsync(id, ct);
        return Results.File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", name);
    }
    catch (InvalidOperationException ex)
    {
        return Results.NotFound(ex.Message);
    }
});

var backup = app.MapGet("/api/backup", async (Db db, CancellationToken ct) =>
{
    var temp = Path.Combine(Path.GetTempPath(), $"labbytwo-{Guid.NewGuid():N}.db");
    await db.BackupToAsync(temp, ct);
    // DeleteOnClose so the copy cannot pile up in temp if a download is abandoned.
    var stream = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
        FileOptions.DeleteOnClose | FileOptions.Asynchronous);
    return Results.Stream(stream, "application/octet-stream",
        fileDownloadName: $"labbytwo-{DateTimeOffset.Now:yyyy-MM-dd}.db");
});

// The health page as JSON, for a script or another monitor to read. Separate from
// /healthz on purpose: that one is Docker's liveness check and must stay a cheap,
// anonymous "ok", while this one names plugins, paths and connections. Nothing here
// touches the network — the DNS check stays behind the button on the page.
var healthJson = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
{
    // "TimedOut" rather than 3: this is read by people and shell scripts, not a client library.
    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    WriteIndented = true,
};
var healthDetails = app.MapGet("/api/health/details", async (SystemHealth health, CancellationToken ct) =>
    Results.Json(await health.ReportAsync(ct), healthJson));

if (authEnabled)
{
    healthDetails.RequireAuthorization();
    export.RequireAuthorization();
    backup.RequireAuthorization();
    shareTab.RequireAuthorization();
    shareWidget.RequireAuthorization();
    shareTemplate.RequireAuthorization();
    // The icon endpoint fetches a URL the caller supplies. That is the same reach a
    // connection already has, but it should not be available to an unauthenticated
    // caller on an install that has a login.
    favicon.RequireAuthorization();
}

app.MapPost("/logout", async (HttpContext context, IAntiforgery antiforgery) =>
{
    // The endpoint binds no form data, so the middleware would not validate this for us.
    if (!await antiforgery.IsRequestValidAsync(context))
        return Results.BadRequest();
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.LocalRedirect("/login");
});

// Routes contributed by extensions, the app's own and any plugin's. A component can
// render a listing; only an endpoint can hand the browser a file with Range honoured, so
// a video seeks and a cancelled download resumes. Each one gets its own group under
// /ext/{key}, which is what stops a plugin claiming /login or colliding with the next
// plugin, and makes a URL say which extension answered it.
var catalog = app.Services.GetRequiredService<ModuleCatalog>();
foreach (var extension in app.Services.GetServices<IEndpointExtension>())
{
    var type = extension.GetType();
    var source = type.Assembly.Location is { Length: > 0 } location ? location : type.FullName!;

    if (!ExtensionRoutes.IsValidKey(extension.Key))
    {
        catalog.Failures.Add(new ModuleFailure(source,
            $"{type.Name} asked for the endpoint key \"{extension.Key}\", which is not usable as a " +
            "URL segment (letters, digits, dashes and underscores). Its routes were not mapped."));
        continue;
    }

    try
    {
        var group = app.MapGroup(ExtensionRoutes.PathFor(extension.Key));

        // The fallback policy already covers anything carrying no policy of its own, but
        // say it here anyway: an extension should not become reachable because somebody
        // changed how the fallback is configured. AllowAnonymous is the deliberate
        // opt-out a share link needs.
        if (!extension.RequiresAuthorization)
            group.AllowAnonymous();
        else if (authEnabled)
            group.RequireAuthorization();

        extension.Map(group);
    }
    catch (Exception ex)
    {
        // One plugin's bad route must not be the reason the whole dashboard fails to
        // start — the same bargain a DLL that will not load already gets.
        catalog.Failures.Add(new ModuleFailure(source,
            $"{type.Name} could not map its endpoints: {ex.GetBaseException().Message}"));
        app.Logger.LogError(ex, "Endpoints from {Extension} could not be mapped", type.FullName);
    }
}

// Stylesheets and scripts are not secrets, and the login page cannot render without
// them — the fallback policy would otherwise apply here too.
app.MapStaticAssets().AllowAnonymous();

// An uploaded typeface, served from the data volume rather than from wwwroot — wwwroot is
// part of the image and is replaced on every update, so a font left there would vanish the
// first time a new version was pulled. Anonymous for the same reason as the stylesheets: the
// login page has to be able to draw itself.
{
    var fonts = app.Services.GetRequiredService<FontStore>();
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(fonts.Directory),
        RequestPath = FontStore.Route,
        // Fixed names in a fixed directory, and only ever four extensions — but the
        // provider is pointed at a directory somebody can write to, so it is told exactly
        // what it may serve rather than being left to guess from the extension.
        ContentTypeProvider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider(
            new Dictionary<string, string>
            {
                [".woff2"] = "font/woff2",
                [".woff"] = "font/woff",
                [".ttf"] = "font/ttf",
                [".otf"] = "font/otf",
            }),
        ServeUnknownFileTypes = false,
    });
}

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
