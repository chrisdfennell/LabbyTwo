# Example plugins

Four plugins that build, load and do something worth having. They exist to be read: each
one demonstrates a different extension point, and between them they cover every rule in
[writing-an-extension.md](../docs/writing-an-extension.md).

Nothing here is compiled into LabbyTwo. They are separate projects that reference it, the
same way yours will.

| Plugin | Extension points | Read it for |
|---|---|---|
| [ExamplePlugin](LabbyTwo.ExamplePlugin) | provider | The smallest complete thing. No HTTP, no auth — free space on a path. |
| [SyncthingPlugin](LabbyTwo.SyncthingPlugin) | provider | The shape of a real HTTP provider: API key, two calls, metrics, a suggested alert rule, and errors turned into sentences. |
| [PaperlessPlugin](LabbyTwo.PaperlessPlugin) | provider + widget | Shipping a Blazor component in a plugin, and a widget that calls its own provider for data a number cannot show. |
| [DashyImportPlugin](LabbyTwo.DashyImportPlugin) | importer | A pure function from a file to an import plan, and how to use a library the host already ships. |

## Build and install

```bash
cd examples/LabbyTwo.SyncthingPlugin
dotnet build -c Release
cp bin/Release/net10.0/LabbyTwo.SyncthingPlugin.dll /path/to/labbytwo/data/plugins/
docker compose restart labbytwo
```

With Docker, `data/plugins` is inside the named volume. The easiest way in:

```bash
docker cp bin/Release/net10.0/LabbyTwo.SyncthingPlugin.dll labbytwo-labbytwo-1:/app/data/plugins/
docker compose restart labbytwo
```

**Settings** then lists every provider, widget, tab kind and importer the build can see,
plus the reason for anything that failed to load. A plugin that does not appear there did
not load, and that page will say why.

## What each one is worth reading for

### ExamplePlugin — `DiskSpaceProvider`

One file, no dependencies. Start here. It also makes the point that "up" means *reachable*
and nothing else: a nearly-full disk is not a failed probe, because reporting it as one to
borrow the alerting would make every uptime figure lie.

### SyncthingPlugin — `SyncthingProvider`

The template for anything with an HTTP API. Worth noting:

- `IHttpClientFactory` comes from the host by constructor injection — a `new HttpClient()`
  per probe exhausts sockets.
- `FieldKind.Password` is encrypted at rest and never rendered back to the browser.
- `SuggestedRules` are *offered* on the Alerts page, never created behind anyone's back.
- The `catch` returns a `ProbeResult.Down` with a sentence, never an exception. That text
  is what a person reads on a tile at 2am.

### PaperlessPlugin — `PaperlessProvider` + `RecentDocuments.razor`

A provider and a widget in one DLL. Three things this shows that a provider alone cannot:

- **The project needs `Microsoft.NET.Sdk.Razor`.** The plain SDK will not compile a
  `.razor` file into the assembly.
- **A plugin needs its own `_Imports.razor`.** The host's does not reach into your project.
- **The widget injects its own provider.** Every provider is registered under its concrete
  type as well as the interface, so `@inject PaperlessProvider Paperless` just works — no
  service registration of your own.

The widget does not poll. The host's monitor already does, and every widget redraws when
it lands; a widget with its own timer multiplies load on the far end by the number of
cards placed.

### DashyImportPlugin — `DashyImporter`

The easiest extension point to get right, because it is a pure function: a file in, an
`ImportPlan` out, no database access. That makes it unit-testable with a string.

It also answers a question the other examples do not: **how do I use a library the host
already has?** Reference the package with `PrivateAssets="all" ExcludeAssets="runtime"` at
the host's exact version, so you compile against it without dropping a second copy of the
DLL beside your plugin. A package the host does *not* ship is the opposite case — that one
you must copy into the plugins folder yourself, because there is no NuGet restore at
runtime.

Note also that it brings its own three-line YAML helpers rather than using LabbyTwo's,
which is `internal`. The four interfaces and the types they mention are the contract;
everything else in the app may change without notice.

## A warning worth repeating

Plugin code is not sandboxed. It runs with the full permissions of the LabbyTwo process,
which can read the database and the data-protection keyring that decrypts every stored
credential. Install plugins you would trust with your passwords, because that is what you
are doing.
