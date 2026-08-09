# Example plugin

A working LabbyTwo plugin in one file, to copy from. It adds a **Disk space** provider
that reports how full a path on the LabbyTwo host is — useful in its own right for
watching the data volume, and short enough to read before writing your own.

## Build and install

```bash
dotnet build -c Release
cp bin/Release/net10.0/LabbyTwo.ExamplePlugin.dll /path/to/labbytwo-data/plugins/
docker compose restart labbytwo
```

Running LabbyTwo with `dotnet run` instead? The folder is `data/plugins` next to the
database.

Then: **Settings → Plugins** should list it, and **Connections → Add** should offer
*Disk space (example plugin)*. Point one at `/app/data` and it starts charting.

## What to notice

- **No registration.** There is no manifest, no attribute, no entry to add anywhere.
  LabbyTwo scans the DLL for classes implementing `IConnectionProvider`, `IWidgetType`,
  `ITabKind` or `IDashboardImporter`.
- **`Private="false" ExcludeAssets="runtime"`** on the project reference. This is the one
  thing that is easy to get wrong. Without it the build copies `LabbyTwo.dll` next to your
  plugin, that copy gets loaded, and its `IConnectionProvider` is a *different type* from
  the host's — so your class satisfies nothing and silently never appears. Check your
  output folder: it should contain your DLL and nothing else of ours.
- **Third-party dependencies ship with you.** There is no NuGet restore at runtime. If
  your plugin needs a library, copy that DLL into the plugins folder too.
- **Restart to pick up a change.** Scanning happens once, at startup.

The full guide, including widgets, tab kinds and importers, is in
[docs/writing-an-extension.md](../../docs/writing-an-extension.md).
