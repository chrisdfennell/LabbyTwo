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

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

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
builder.Services.AddHttpClient(ProviderHttp.ClientName)
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(ProviderHandler);

// The same client with the clock taken off, for the few things that move files rather
// than JSON. Thirty seconds is right for a probe and wrong for a four-gigabyte download:
// the caller's CancellationToken — the browser hanging up — is the real bound there.
builder.Services.AddHttpClient(ProviderHttp.TransferClientName)
    .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(ProviderHandler);

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
    builder.Services.AddModules(typeof(Program).Assembly, pluginDirectory,
        startupLogging.CreateLogger("Modules"));
}

builder.Services.AddSingleton<Registry>();

builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<AppSettingsStore>();
builder.Services.AddSingleton<AlertRuleStore>();
builder.Services.AddSingleton<ConfigStore>();
builder.Services.AddSingleton<HistoryStore>();
builder.Services.AddSingleton<NotesStore>();
builder.Services.AddSingleton<Markdown>();
builder.Services.AddSingleton<Seeder>();
builder.Services.AddSingleton<ConfigTransfer>();
builder.Services.AddSingleton<FaviconService>();
builder.Services.AddSingleton<UpdateChecker>();
builder.Services.AddSingleton<SelfUpdater>();
builder.Services.AddSingleton<DashboardImportService>();

// Not a provider — it answers a question the Settings page asks once, rather than
// something to monitor — so it is registered by hand rather than discovered.
builder.Services.AddSingleton<Geocoder>();

builder.Services.AddSingleton<HealthMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HealthMonitor>());
builder.Services.AddSingleton<AlertService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AlertService>());
builder.Services.AddSingleton<MetricAlertService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MetricAlertService>());

// Anything a module contributed as an IBackgroundJob. One runner for all of them, so a
// plugin's nightly tidy-up cannot hang startup or take the process down with it.
builder.Services.AddSingleton<BackgroundJobRunner>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BackgroundJobRunner>());

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

// Behind a TLS-terminating reverse proxy, honour its scheme and host headers so cookies
// and redirects behave.
builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
{
    forwarded.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                                 | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    // Home lab: the proxy is on the LAN, not at a fixed address.
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
});

var app = builder.Build();

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

// Liveness probe for Docker and monitoring; always anonymous.
app.MapGet("/healthz", () => Results.Text("ok")).AllowAnonymous();

// Bookmark icons. Fetched by the server so a LAN-only service still gets an icon on a
// phone, and cached so a dashboard of thirty links is not thirty requests per refresh.
var favicon = app.MapGet("/api/favicon", async (FaviconService icons, string url, CancellationToken ct) =>
{
    var icon = await icons.GetAsync(url, ct);
    if (!icon.Found)
        return Results.NotFound();
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

if (authEnabled)
{
    export.RequireAuthorization();
    backup.RequireAuthorization();
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

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
