# Writing an extension

LabbyTwo has five extension points. Each one is an interface, each one is found by
scanning for classes rather than from a list, and each one works identically whether you
are editing LabbyTwo itself or shipping a plugin DLL that somebody drops into their
`data/plugins` folder.

| Interface | What it adds | Lives in |
|---|---|---|
| `IConnectionProvider` | something LabbyTwo can talk to and monitor | `Providers/` |
| `IWidgetType` | a card that can go on a dashboard tab | `Components/Widgets/` |
| `ITabKind` | a whole kind of page in the nav | `Components/Pages/Kinds/` |
| `IDashboardImporter` | a config format it can read from another dashboard | `Services/Import/` |
| `IEndpointExtension` | routes the server answers itself, outside the Blazor circuit | anywhere |

There is no registration step. `Program.cs` scans the assembly, finds every public
non-abstract class implementing one of these, and registers it as a singleton. Add the
file, rebuild, and it is in the picker.

---

## A provider, start to finish

A provider is one class. It says what settings it needs, and it knows how to make one
round trip. Everything else — the add-connection form, the Test button, the health
monitor, uptime history, alerting, charts — is written against the interface and needs no
changes.

Here is a complete, working provider for [Uptime Kuma](https://github.com/louislam/uptime-kuma)'s
metrics endpoint.

```csharp
using System.Diagnostics;
using LabbyTwo.Core;

namespace LabbyTwo.Providers;

public sealed class UptimeKumaProvider(IHttpClientFactory httpFactory) : IConnectionProvider
{
    // Stored on every connection row that uses this provider. Never change it after
    // release — an existing database refers to it by name.
    public string Type => "uptime-kuma";

    public string DisplayName => "Uptime Kuma";
    public string Icon => "🐨";
    public string Category => "Monitoring";
    public string Description => "Monitor count and reachability from an Uptime Kuma instance.";

    // These become a form. No Razor required — SettingsForm renders any list of these.
    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("url", "Base URL", FieldKind.Url, "http://192.168.1.20:3001", Required: true),

        // FieldKind.Password is encrypted at rest and never rendered back to the browser.
        new("api_key", "API key", FieldKind.Password,
            Help: "Settings → API Keys inside Uptime Kuma."),
    ];

    // How the numbers below should read. Only declare what is specific to you —
    // latency_ms and friends are already known, and an undeclared metric still gets a
    // sensible label from its name.
    public IReadOnlyList<MetricSpec> Metrics =>
    [
        new("monitors_up", "Monitors up"),
        new("monitors_down", "Monitors down"),
    ];

    public async Task<ProbeResult> ProbeAsync(Connection connection, CancellationToken ct)
    {
        var url = connection.Settings.Get("url").TrimEnd('/');
        if (url.Length == 0)
            return ProbeResult.Down(TimeSpan.Zero, "No URL configured.");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            // Always use this named client: it tolerates the self-signed certificates
            // that are normal on a LAN, so a certificate warning never reads as "down".
            var http = httpFactory.CreateClient(ProviderHttp.ClientName);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{url}/metrics");
            if (connection.Settings.Get("api_key") is { Length: > 0 } key)
            {
                var basic = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($":{key}"));
                request.Headers.TryAddWithoutValidation("Authorization", $"Basic {basic}");
            }

            using var response = await http.SendAsync(request, ct);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
                return ProbeResult.Down(stopwatch.Elapsed, $"HTTP {(int)response.StatusCode}");

            var body = await response.Content.ReadAsStringAsync(ct);
            var up = body.Split('\n').Count(l => l.StartsWith("monitor_status") && l.EndsWith(" 1"));
            var down = body.Split('\n').Count(l => l.StartsWith("monitor_status") && l.EndsWith(" 0"));

            // Whatever you put in here gets recorded to history and becomes chartable,
            // alertable and tile-able with no chart-side knowledge of Uptime Kuma.
            return ProbeResult.Up(stopwatch.Elapsed, $"{up} up, {down} down",
                new Dictionary<string, double>
                {
                    ["monitors_up"] = up,
                    ["monitors_down"] = down,
                    ["latency_ms"] = stopwatch.Elapsed.TotalMilliseconds,
                });
        }
        catch (Exception ex)
        {
            // Never throw out of a probe. A failure is a result, not an exception — the
            // monitor turns this into a red tile and an alert with this exact message.
            stopwatch.Stop();
            return ProbeResult.Down(stopwatch.Elapsed, ex.GetBaseException().Message);
        }
    }
}
```

Drop that file into `Providers/`, rebuild, and Uptime Kuma is in the add-connection
picker with a generated form, a Test button, monitoring, uptime history, alerts and
charts.

### Things worth knowing

- **Report, don't judge.** `Up` means reachable. Whether 91% disk usage is a problem is the
  user's call, made with an alert rule against the metric you reported — a provider that
  returns `Down` because a number is high makes the tile, the uptime percentage and the
  status page all wrong about it.
- **`ProbeAsync` is both the Test button and the monitor.** A green test therefore means
  monitoring really works, which is the whole reason they share a code path. Do not
  special-case one.
- **Return, do not throw.** `ProbeResult.Down(elapsed, message)` — the message is what the
  user sees on the tile and in the alert, so make it the thing they need to fix.
- **A provider is a singleton.** Cache the last reading on it if a widget needs richer
  data than metrics can carry; `AmbientWeatherProvider` and `PlexProvider` both do this.
- **`IsMonitored => false`** for something that exists to be used rather than watched.
- **Implement `IAlertChannel` instead** and your provider becomes somewhere notifications
  are *sent*, with the same generated form and encrypted storage. See `WebhookProvider`.
- **Suggest the rules that matter.** `SuggestedRules` offers ready-made alert rules on the
  Alerts page — you know a UPS on battery is news and the user does not. Give each a
  `Why`. They are offers, never created automatically.

```csharp
public IReadOnlyList<SuggestedRule> SuggestedRules =>
[
    new("Monitors down", "monitors_down", Comparison.Above, 0, ForMinutes: 5,
        Why: "Something Uptime Kuma watches has been failing for five minutes."),
];
```

- **Fall back to history for a live reading.** A card that only reads
  `HealthMonitor.State` is blank for a whole probe interval after a restart.
  `HistoryStore.LatestAsync` gives the last recorded value of every metric; every card
  that shows a current reading uses it.

---

## A widget

Two pieces: a descriptor and a component.

```csharp
// Components/Widgets/WidgetTypes.cs
public sealed class KumaSummaryWidget : IWidgetType
{
    public string Type => "kuma-summary";
    public string DisplayName => "Uptime Kuma summary";
    public string Icon => "🐨";
    public string Description => "How many monitors are up.";

    // Which providers this can bind to. AnyProvider.Types is the "*" wildcard for
    // widgets that work with anything probed; omit the property entirely for a widget
    // that needs no connection at all.
    public IReadOnlyList<string> ProviderTypes => ["uptime-kuma"];

    public int DefaultWidth => 3;
    public IReadOnlyList<FieldSpec> Fields => [];
    public Type Component => typeof(KumaSummary);
}
```

```razor
@* Components/Widgets/KumaSummary.razor *@
@inject HealthMonitor Health

<div class="metric-value">@_up<span class="metric-unit">up</span></div>

@code {
    // The parameter is always called Context and always this type.
    [Parameter, EditorRequired] public WidgetContext Context { get; set; } = default!;

    private double _up;

    protected override void OnParametersSet()
    {
        var state = Context.Connection is null ? null : Health.State(Context.Connection.Id);
        _up = state?.Metrics.GetValueOrDefault("monitors_up") ?? 0;
    }
}
```

The picker will now offer it, greyed out with a reason until an Uptime Kuma connection
exists.

Use `FieldKind.Metric` for any field naming a metric — it renders a box with a dropdown of
what the bound connection declares and what history has actually recorded.

---

## A tab kind

Same shape again: a descriptor pointing at a component that takes a `Tab` parameter.

```csharp
public sealed class KanbanTabKind : ITabKind
{
    public string Kind => "kanban";
    public string DisplayName => "Kanban";
    public string Icon => "📋";
    public string Description => "Cards in columns.";
    public IReadOnlyList<FieldSpec> Fields =>
    [
        new("columns", "Columns", FieldKind.Text, Default: "To do, Doing, Done"),
    ];
    public Type Component => typeof(KanbanTab);
}
```

```razor
@code {
    [Parameter, EditorRequired] public Tab Tab { get; set; } = default!;
}
```

---

## An importer

Importers are deliberately pure: a file in, a plan out, nothing touching the database. That
makes them trivial to unit-test, and it means the id allocation, slug collision handling
and connection binding are written once in `DashboardImportService` rather than in each
format.

```csharp
public sealed class FlameImporter : IDashboardImporter
{
    public string Key => "flame";
    public string DisplayName => "Flame";
    public string Icon => "🔥";
    public string Description => "Flame's exported JSON.";
    public IReadOnlyList<string> Extensions => [".json"];

    // Cheap, and must not throw — a detector that blows up on a file it does not
    // understand would stop the other importers being asked.
    public bool CanHandle(ImportSource source) =>
        source.Extension == ".json" && source.Text.Contains("\"bookmarks\"");

    public ImportPlan Read(ImportSource source)
    {
        // Throw FormatException with a message a user can act on.
        var tab = new ImportedTab("Flame", "🔥");
        tab.Widgets.Add(new ImportedWidget("links", "Apps", 3,
            new SettingsBag { ["links"] = LinkRow.Serialize(rows) }));
        return new ImportPlan { Tabs = { tab } };
    }
}
```

Connections are referenced by a local string, not an id — `ImportedWidget(…,
ConnectionRef: "media/sonarr")` binds to the `ImportedConnection` with that `Ref` once
both are written.

---

## An endpoint

The other four extension points all end in HTML. This one is for the times that is not
enough: handing the browser a file, taking an upload that should not travel through the
Blazor circuit, or a link somebody opens on a phone without logging in. A component cannot
do any of those, because none of them are a render.

```csharp
public sealed class SnapshotEndpoints(ConfigStore config) : IEndpointExtension
{
    // Also the URL segment your routes live under. Never change it after release.
    public string Key => "camera";

    public void Map(IEndpointRouteBuilder routes) =>
        // Answers at /ext/camera/snapshot
        routes.MapGet("/snapshot", async (string connection, HttpContext context, CancellationToken ct) =>
        {
            var camera = await config.ConnectionAsync(connection, ct);
            if (camera is null)
                return Results.NotFound();

            context.Response.ContentType = "image/jpeg";
            // …stream it…
            return Results.Empty;
        });
}
```

### Things worth knowing

- **Everything is mapped under `/ext/{Key}`.** That is what stops a plugin from claiming
  `/login`, or colliding with the next plugin, and it makes a URL say which extension
  answered it. A key that would not survive being a URL segment — a slash, a route brace —
  is refused and reported on the Settings page rather than mapped.
- **Login applies by default.** On an install with a password, an extension's routes
  require it like every other page. Override `RequiresAuthorization => false` only for
  something that genuinely has to answer without one, like a share link — and then make the
  token in the link the thing that authorises it.
- **Throwing takes you out, not the app.** If `Map` throws, that extension's routes are
  skipped and the reason appears under Settings → Plugins. The dashboard still starts.
- **Pass Range headers through.** If you are streaming a file from somewhere else, copy the
  request's `Range` header up and the `206`, `Content-Range` and `Accept-Ranges` back down.
  That pair is the difference between a video that seeks and a video that has to be
  downloaded whole before it plays.
- **Use `ProviderHttp.TransferClientName` for files.** The ordinary provider client times
  out after 30 seconds, which is right for a probe and wrong for a four-gigabyte download.
  The transfer client has no timeout and takes its bound from the request's
  `CancellationToken` — the browser hanging up.
- **A minimal API endpoint is not a Razor component.** It has no circuit, no
  `StateHasChanged`, and no antiforgery token unless you ask for one; a `POST` that binds
  form data needs `.DisableAntiforgery()` or a token of its own.

`QnapFilesPlugin` in [`examples/`](../examples) is the worked version: a tab kind that
lists a NAS folder, and an endpoint next to it that serves the file the listing links to.

---

## Shipping it as a plugin

Nothing above needs to live in the LabbyTwo repository. A plugin is an ordinary class
library that references LabbyTwo and is copied into the plugins folder.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <!-- Private and no copy: the host already has these at runtime, and shipping your
         own copies is what breaks type identity. -->
    <ProjectReference Include="..\LabbyTwo\LabbyTwo.csproj" Private="false" ExcludeAssets="runtime" />
  </ItemGroup>
</Project>
```

**If your plugin ships a widget**, swap the SDK for `Microsoft.NET.Sdk.Razor` — the plain
one will not compile a `.razor` file into the assembly — and add an `_Imports.razor` of
your own, because the host's does not reach into your project:

```razor
@using Microsoft.AspNetCore.Components
@using LabbyTwo.Core
```

**To use a library the host already ships** — YamlDotNet, Markdig — reference the package
at the host's exact version with `PrivateAssets="all" ExcludeAssets="runtime"`, so you
compile against it without dropping a second copy of the DLL beside your plugin:

```xml
<PackageReference Include="YamlDotNet" Version="18.1.0" PrivateAssets="all" ExcludeAssets="runtime" />
```

A package the host does *not* ship is the opposite case: copy it into the plugins folder
yourself, because there is no NuGet restore at runtime.

```bash
dotnet build -c Release
cp bin/Release/net10.0/MyLabbyPlugin.dll /path/to/labbytwo-data/plugins/
docker compose restart labbytwo
```

Settings → Plugins lists what loaded, what it contributed, and the reason for anything
that did not.

## Eight that work

[`examples/`](../examples) has eight plugins that build and run, covering every extension
point on this page. Start from whichever is closest to what you are writing:

| If you are writing | Read |
|---|---|
| A provider for an HTTP API | `SyncthingPlugin` — API key, metrics, a suggested rule, errors as sentences |
| A provider with no dependencies | `ExamplePlugin` — one file, free space on a path |
| A provider whose metrics depend on its settings | `PresencePlugin` — `MetricsFor(connection)` |
| A provider *and* a widget | `PaperlessPlugin` — the Razor SDK, `_Imports.razor`, injecting your own provider |
| A tab kind | `CalendarPlugin` — a provider, a widget and an agenda page from one feed |
| Something that stores its own data | `ChoresPlugin` — its own table in the host's database |
| An importer | `DashyImportPlugin` — a pure function, and using a library the host ships |
| Something that serves files | `QnapFilesPlugin` — a tab kind and an endpoint, downloads with Range |

### The rules

- **Scanned once, at startup.** Installing or updating a plugin needs a restart.
- **Reference LabbyTwo, don't bundle it.** Plugins load into the default assembly load
  context so their reference resolves to the host's already-running assembly. A plugin
  carrying its own copy would produce types that look identical and satisfy no interface
  check. That is what `Private="false"` above prevents.
- **Third-party dependencies go in the plugins folder too.** There is no NuGet restore at
  runtime; ship the DLLs you need beside your own.
- **A key collision means the plugin wins.** Plugins are registered after the built-ins
  and last registration wins, so a plugin declaring `Type => "qnap"` replaces the bundled
  QNAP provider rather than crashing the app. Use a distinctive key unless replacing
  something is what you meant.
- **Plugin code is not sandboxed.** It runs with the full permissions of the LabbyTwo
  process, which can read the database and the data-protection keyring. Install plugins
  you would trust with your credentials, because that is what you are doing.

---

## Testing an extension

The extension points were shaped so that the interesting parts need no running app.
`tests/LabbyTwo.Tests` has examples for each:

```csharp
[Fact]
public void MyImporterReadsAGroup()
{
    var source = new ImportSource("config.yml", Encoding.UTF8.GetBytes(yaml));
    var plan = new MyImporter().Read(source);
    Assert.Single(plan.Tabs);
}
```

`ModuleDiscoveryTests` builds the real DI container and asserts that everything is found,
that no two extensions share a key, and that every widget points at a real component —
worth running after adding anything, since a typo in a `Type` string is otherwise silent.
It also pins the list of built-in providers by name, so adding one deliberately fails that
test until you add yours to the list; that is the reminder to update the README table too.

`MetricAlertServiceTests` shows the pattern for anything that needs a live probe: a fake
provider whose reading the test sets, a recording alert channel, and time passed in as a
parameter so a ten-minute sustain window costs no wall-clock time.

```bash
dotnet test
```
