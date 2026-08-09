# Contributing

Thanks for looking. LabbyTwo is small on purpose and easy to add to — most contributions
are one file.

## Getting it running

```bash
git clone <your fork>
cd LabbyTwo
dotnet run
```

That is the whole setup. It creates `data/labbytwo.db` on first run and prints a URL.
There is no config file to fill in, no database to provision, and no seed data — click
**Create a starter dashboard** and you have something to look at.

```bash
dotnet test        # ~130 tests, about a second
dotnet build       # zero warnings is the standard; please keep it there
```

One trap worth knowing: run it in **Development** (the default for `dotnet run`). Outside
Development, `MapStaticAssets` serves the pre-compressed stylesheets that only
`dotnet publish` produces, so a plain build output serves *empty* CSS and the app renders
unstyled. It is not a bug in the app — Docker publishes, so the image is fine — but
`ASPNETCORE_ENVIRONMENT=Production dotnet run` will waste an hour of your life.

## The one thing to understand

Nothing about anybody's network is compiled in, and nothing is registered by hand.
There are four extension interfaces, and every public class implementing one is found by
scanning the assembly at startup:

- `IConnectionProvider` — something to talk to and monitor
- `IWidgetType` — a card on a dashboard
- `ITabKind` — a kind of page
- `IDashboardImporter` — a config format to read from another dashboard

If a change means editing a list of integrations somewhere, that is a sign the extension
point needs widening rather than the list needing an entry.

**[docs/writing-an-extension.md](docs/writing-an-extension.md)** walks through a complete
provider, widget, tab kind and importer, and how to ship one as a plugin DLL instead.

## What is most wanted

**Providers.** By a distance. Every home lab has something LabbyTwo cannot see yet —
TrueNAS, Synology, Proxmox, Unraid, Home Assistant, UniFi, Immich, Jellyfin, Frigate, a
UPS, a solar inverter, a 3D printer. One class each. If you own the hardware you are the
right person to write it, because you can test it against the real thing.

**Importers.** Anything that gets somebody off another dashboard without retyping it.

**Bugs and rough edges.** Especially anything that reads badly on a phone, in light mode,
or with a screen reader.

## House style

The existing code is the spec, but in short:

- **Comments explain why, not what.** Assume the reader can see the code. Say what would
  otherwise be a surprise — a non-obvious constraint, a decision that looks wrong until
  you know something. Most methods need none.
- **Prose in the UI.** Error messages, help text and empty states are read by someone
  trying to get something working. Say what happened and what to do about it.
- **No new dependencies without a reason.** Charts are hand-rolled SVG; the only
  JavaScript is one keyboard shortcut. That is deliberate — it keeps the image small and
  the thing auditable.
- **Never throw out of a probe.** Return `ProbeResult.Down` with a message the user can
  act on.
- **Up and down mean reachable and unreachable.** A provider reports numbers; it does not
  decide a number is bad. Returning "down" for a nearly-full disk to borrow the alerting
  makes every tile and uptime figure lie — that is what alert rules are for.
- **Secrets are `FieldKind.Password`.** They are then encrypted at rest, stripped from
  shared exports and never echoed back to the browser. Nothing else gets that treatment,
  so use it.

## Pull requests

- One thing per PR.
- Add tests for anything with logic in it. Importers and parsers especially — they are
  pure functions and there is no excuse.
- Say what you tested against. "Tested against Synology DSM 7.2" tells a reviewer far
  more than a green build does, because none of us have your hardware.
- Update the README table if you added a provider or widget.

CI builds the app, runs the tests, and builds the Docker image for amd64 and arm64. A
good share of home labs run on a Raspberry Pi, so an arm64 break is a real break.

## Reporting a bug

Include what you were connecting to and what LabbyTwo said. The exact text of a failed
probe is usually the whole diagnosis. Settings → This install has the version.

For anything security-sensitive, please open a private advisory rather than a public
issue. LabbyTwo holds credentials for people's NAS boxes.
