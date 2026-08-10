# Example plugins

Eight plugins that build, load and do something worth having. They are meant to be
installed as much as read: most of them fill a real gap, and between them they cover every
extension point and every rule in
[writing-an-extension.md](../docs/writing-an-extension.md).

Nothing here is compiled into LabbyTwo. They are separate projects that reference it, the
same way yours will.

| Plugin | Adds | What it is for |
|---|---|---|
| [GluetunPlugin](LabbyTwo.GluetunPlugin) | provider | Whether your VPN tunnel is up, which country it exits from, and the forwarded port. |
| [CalendarPlugin](LabbyTwo.CalendarPlugin) | provider + widget + tab kind | Any published `.ics` feed — what's on today, and a full agenda page. |
| [ChoresPlugin](LabbyTwo.ChoresPlugin) | tab kind + widget | Recurring household jobs with due dates. Stores its own data. |
| [QnapFilesPlugin](LabbyTwo.QnapFilesPlugin) | tab kind + endpoint | Browse, download and upload files on a QNAP you already monitor. |
| [PresencePlugin](LabbyTwo.PresencePlugin) | provider + widget | Who's home — pings a list of devices and charts each one. |
| [SyncthingPlugin](LabbyTwo.SyncthingPlugin) | provider | A Syncthing daemon: devices connected, data moved, uptime. |
| [PaperlessPlugin](LabbyTwo.PaperlessPlugin) | provider + widget | Paperless-ngx document count, inbox backlog, and what was just filed. |
| [DashyImportPlugin](LabbyTwo.DashyImportPlugin) | importer | Reads Dashy's `conf.yml` so you can migrate from it. |
| [ExamplePlugin](LabbyTwo.ExamplePlugin) | provider | Free space on a path. The smallest complete thing — start here if you are writing one. |

## Build and install

```bash
cd examples/LabbyTwo.GluetunPlugin
dotnet build -c Release
```

With Docker, `data/plugins` is inside the named volume, so copy the DLL in and restart:

```bash
docker cp bin/Release/net10.0/LabbyTwo.GluetunPlugin.dll labbytwo-labbytwo-1:/app/data/plugins/
docker compose restart labbytwo
```

**Settings** then lists every provider, widget, tab kind, importer and endpoint the build
can see, plus the reason for anything that failed to load. A plugin that does not appear there did
not load, and that page says why.

Plugins are scanned once, at startup. Installing or updating one needs a restart.

---

## Gluetun — because a VPN fails quietly

Point it at Gluetun's control server (`http://gluetun:8000`). It reports the tunnel state,
the public IP and country it is exiting from, and the forwarded port.

Worth having because of *how* a VPN fails. When gluetun drops, the containers sharing its
network namespace do not go down — they go silent. qBittorrent still answers, still says
it is running, and simply stops moving bytes. Nothing on a dashboard shows that unless
something is watching the tunnel itself.

Two optional settings turn "up" into "up and correct":

- **Expected country** fails the probe when the tunnel reconnects somewhere you did not
  intend.
- **Expect a forwarded port** makes a zero a failure rather than a fact.

A failed probe still records its metrics, so `vpn_up = 0` is charted rather than leaving a
gap exactly when something went wrong.

## Calendar — one URL, no OAuth

An `.ics` feed is just a file over HTTP, which makes a calendar one of the easiest useful
things to add: Google, Nextcloud, iCloud, bin collections, a fixture list.

It ships all three renderable extension points: a **provider** (events today, what's next,
and a stale feed as a real failure worth alerting on), a **widget** grouped by day with
"Today" and "Tomorrow" rather than dates, and an **Agenda tab kind** with a card per day.

[`Ics.cs`](LabbyTwo.CalendarPlugin/Ics.cs) is the interesting file. It handles the parts of
RFC 5545 that real calendars actually contain — line folding, all-day events, `TZID` zones,
escaped commas in titles, and recurrence with `INTERVAL`, `COUNT`, `UNTIL` and the
Monday-and-Wednesday form of `BYDAY`. It is a pure function from text to occurrences, so
the awkward cases can be checked with a string and no network.

> The feed URL is a secret — anyone with the link can read the calendar. It is stored with
> the same encryption as any other password field.

## Chores — a plugin that owns its data

Everything else in LabbyTwo watches something. This one *is* the thing, which is why it is
worth having as an example: a tab kind to manage the list, a widget showing only what is
due, and one table of its own.

It uses the host's database, injected as `Db`, and creates `plugin_chores` itself with
`CREATE TABLE IF NOT EXISTS` on first use — LabbyTwo's migrations know nothing about plugin
tables. Using the same file rather than one of its own means the chores are inside every
backup and every "Download database" without anyone thinking about it.

Ticking off a repeating chore sets the next due date **from today**, not from when it was
supposed to be done — otherwise a chore you are three weeks late on stays three weeks late
forever.

## NAS files — the one that needed a real URL

A page for browsing the QNAP you already added as a connection: shares, folders, sizes,
download, and — when the tab is not left read-only — upload, rename, create folder and
delete.

It is the example of the fifth extension point, and of why that point exists. The tab kind
renders the listing; `QnapFilesEndpoints` serves the file at `/ext/qnap-files/download`,
because a Blazor component cannot hand the browser three gigabytes of video. The endpoint
passes the browser's `Range` header up to QTS and its `206` and `Content-Range` back down,
which is what makes seeking in a video and resuming an interrupted download work at all.

Two things it shows that are easy to get wrong:

- **Borrow the provider's session, do not open your own.** File Station and QTS's
  management CGI take the same sid. `QnapProvider.SessionIdAsync` hands it over, so the
  plugin does not log in a second time against the same account — which is what would
  expire the session the health monitor is using.
- **QTS's multipart parser is fussy.** It needs a `Content-Length`, a quoted `filename`
  with no RFC 5987 `filename*`, and `Content-Disposition` as the part's first header.
  `MultipartFormDataContent` breaks all three, and QTS then answers *success* while writing
  nothing. `UploadAsync` spools the whole envelope to a temp file and sends it verbatim.

The tab is **read only by default**. A dashboard is a thing people leave open on a tablet
in the kitchen, and the gap between a bad tap and a deleted share should be a setting
somebody turned on deliberately. Beyond that the QNAP account is the boundary: the listing
can only ever show what that account can already see, so give it one with the access you
actually want reachable.

## Presence — who's home

Pings a list of devices every sweep. Each device becomes its own metric, so "was anyone in
on Tuesday afternoon" is a chart rather than a guess, and you can alert on one particular
phone arriving or leaving.

This is the example of a provider whose metrics are decided by the *user* rather than the
code: `MetricsFor(connection)` reads the configured list, which is what puts each device in
the chart and alert pickers by name instead of leaving people to type `home_kitchen_tablet`
from memory.

Nobody being home is an answer, not a failure. Reporting it as one would make the uptime
figure mean "somebody was in" and fire a down-alert every time the house is empty.

## Syncthing, Paperless-ngx, Dashy, disk space

The other four are smaller and more obviously templates.

**Syncthing** is the shape almost every HTTP provider takes: an API key, two calls,
metrics, a suggested alert rule, and errors turned into sentences. Copy this one.

**Paperless-ngx** is a provider and a widget in one DLL. Three things it shows that a
provider alone cannot: the project needs `Microsoft.NET.Sdk.Razor`, a plugin needs its own
`_Imports.razor`, and a widget can `@inject` its own provider because every provider is
registered under its concrete type as well as the interface.

**Dashy** is the fourth extension point and the easiest to get right — a pure function from
a file to an `ImportPlan`, with no database access, so it is unit-testable with a string.
It also answers "how do I use a library the host already has?": reference the package with
`PrivateAssets="all" ExcludeAssets="runtime"` at the host's exact version, so you compile
against it without dropping a second copy of the DLL beside your plugin.

**Disk space** is one file with no dependencies. It makes the point that "up" means
*reachable* and nothing else: a nearly-full disk is not a failed probe, because reporting
it as one to borrow the alerting would make every uptime figure lie.

---

## Things every one of these does

Patterns worth copying, all of them learned the hard way:

- **A probe never throws.** It returns `ProbeResult.Down` with a sentence someone can act
  on. That text is what shows on a tile at 2am.
- **A widget never polls.** The host's monitor already does, and every widget redraws when
  it lands. A widget with its own timer multiplies load on the far end by the number of
  cards on the page.
- **A widget never throws either.** An unhandled exception in one card takes down the whole
  Blazor circuit, and the rest of the dashboard goes with it.
- **Expensive fetches are cached per connection.** The calendar downloads a whole file; the
  provider caches it for five minutes so the widget, the agenda page and the probe share
  one download.
- **Use the injected `IHttpClientFactory`.** A `new HttpClient()` per probe exhausts
  sockets.

## A warning worth repeating

Plugin code is not sandboxed. It runs with the full permissions of the LabbyTwo process,
which can read the database and the data-protection keyring that decrypts every stored
credential. Install plugins you would trust with your passwords, because that is what you
are doing.
