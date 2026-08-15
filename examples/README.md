# Example plugins

Thirteen plugins that build, load and do something worth having. They are meant to be
installed as much as read: most of them fill a real gap, and between them they cover every
extension point and every rule in
[writing-an-extension.md](../docs/writing-an-extension.md).

Nothing here is compiled into LabbyTwo. They are separate projects that reference it, the
same way yours will.

| Plugin | Adds | What it is for |
|---|---|---|
| [TerminalPlugin](LabbyTwo.TerminalPlugin) | provider + widget + tab kind + endpoint | A real shell in the dashboard — SSH to a host, or into a running container. |
| [GluetunPlugin](LabbyTwo.GluetunPlugin) | provider | Whether your VPN tunnel is up, which country it exits from, and the forwarded port. |
| [CalendarPlugin](LabbyTwo.CalendarPlugin) | provider + widget + tab kind | Any published `.ics` feed — what's on today, and a full agenda page. |
| [GoogleCalendarPlugin](LabbyTwo.GoogleCalendarPlugin) | provider + widget + tab kind + endpoint | A Google calendar you can write to — month, week and list views, and adding events. |
| [ChoresPlugin](LabbyTwo.ChoresPlugin) | tab kind + widget | Recurring household jobs with due dates. Stores its own data. |
| [RenewalsPlugin](LabbyTwo.RenewalsPlugin) | tab kind + widget + provider + job | Domains, certificates, subscriptions — expiry dates that can raise an alert, and certificates that watch themselves. |
| [DropPlugin](LabbyTwo.DropPlugin) | tab kind + endpoint + job | A shared shelf for files and text between devices, cleared on a timer. |
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

**One of them has dependencies of its own.** The Terminal plugin uses SSH.NET, which
LabbyTwo does not ship, and there is no NuGet restore at runtime — so copy *everything* its
build produced, not only its own DLL:

```bash
docker cp bin/Release/net10.0/. labbytwo-labbytwo-1:/app/data/plugins/
```

---

## Terminal — a shell, in the dashboard

Half of what anyone does with a home lab ends in a shell. This puts one on a tab: pick a
machine or a running container on the left, and there it is — a real pty, so `htop`, `vim`
and `tmux` work rather than nearly working.

It is here because it is the example of the extension points *combining*. A provider holds
the credentials, a tab kind renders the page, a widget puts one shell on a dashboard, and an
endpoint carries the bytes — and no other arrangement of those four would work:

- **The endpoint is a WebSocket**, which is not something LabbyTwo set up. The host never
  calls `UseWebSockets()`, and a plugin cannot add middleware to a pipeline that is already
  built. It can build a pipeline of its own though — `routes.CreateApplicationBuilder()`
  gives one scoped to a single endpoint, which is the same trick SignalR uses to make
  Blazor work without the host calling it either.
- **The page is served whole and framed**, rather than rendered by the component. Blazor
  owns the DOM inside a component and will discard on the next diff anything JavaScript put
  there — and a terminal is nothing but DOM that JavaScript put there. A frame also gets
  the keyboard to itself, which a terminal rather needs.
- **xterm.js is vendored**, not fetched from a CDN. A dashboard for a home lab is routinely
  what you open when the internet is the broken part. It is served out of the assembly as
  an embedded resource, so installing the plugin is still copying DLLs.

### The two ends

**SSH** is an ordinary connection — host, username, and a password or a key file — so the
credentials are encrypted with everything else's and the machine appears on the status page
next to the services running on it. The probe is a real login that also reads `/proc`, so
load, memory and uptime are chartable and alertable without a second integration.

It probes every five minutes rather than every thirty seconds, and that is the point of
`MinimumInterval`: a probe here is a *login*, and a login is a line in `auth.log`. Asking
2,880 times a day turns the one file you read after a break-in into noise.

The key is a **path**, not a box to paste a key into. LabbyTwo's encrypted fields are one
line and a PEM is not — and a key that stays a file on your disk is a key that is not in the
database at all. Mount it read-only and point at it.

Host keys are trust-on-first-use, done by hand and in the open. Leave the fingerprint field
empty and the first key is accepted and *printed by Test connection*; paste it back in and a
key that changes afterwards stops the connection dead, with a message that names the new
fingerprint and says what the two explanations are.

**Containers** need nothing new: it reads the socket the Docker provider already uses.
`docker exec` is the one thing here that cannot be done with an `HttpClient` — Docker
answers `/exec/{id}/start` with `101 UPGRADED` and hands the connection over as a raw duplex
stream, and `HttpClient` has nowhere to give you that socket back. So the request is written
by hand and the headers read off the stream until the blank line, after which the same
stream is the terminal.

The default shell is worth reading before you change it:

```
/bin/sh -c 'command -v bash >/dev/null 2>&1 && exec bash || exec sh'
```

The obvious spelling — `exec bash || exec sh` — does not work. POSIX says a non-interactive
shell exits when `exec` cannot find the command, so on an image without bash the `||` is
never reached and the terminal opens and closes again in the same instant. `command -v`
tests without replacing the process.

### What stops it being a hole in your dashboard

This is the most dangerous thing in `examples/`, and it is built accordingly.

- **It will not open at all without a login.** LabbyTwo's password being optional is right
  for a dashboard and not right for this: without one, a terminal is a root shell for
  everyone on the LAN. There is no setting to turn the requirement off — the page and the
  socket both refuse and say why.
- **The tab is the boundary, not the list.** The picker on the page is a convenience; the
  target arrives in a query string, and anyone who can open the page can edit it. So every
  attach carries the id of the tab or card it came from, the policy is read back out of the
  database, and *that* decides. A tab narrowed to one container really is narrowed to one
  container.
- **It closes itself.** Thirty minutes idle by default. A forgotten browser tab is a live
  shell, and the tablet on the kitchen wall is exactly where one gets forgotten.
- **Every session is logged at `Information`** with who opened it, on what, and for how
  long — legible in `docker compose logs` without anyone having turned anything on first.

None of that changes the fact that a plugin runs unsandboxed and this one deliberately hands
out shells. It is the example to read before writing anything that acts rather than watches.

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

## Google Calendar — the one you can write to

The ICS plugin above reads a published feed, which is enough for bin collections and
fixture lists. It cannot ever write: a feed is a file Google publishes, so nothing typed
into a dashboard travels back up it. This one uses the Calendar API, so an event added on
the wall tablet is on everyone's phone a second later, and an event added on a phone is
here immediately rather than whenever Google's feed cache catches up.

The page is a real calendar — **month**, **week** and **list**, with add, edit and delete.
Clicking an empty day starts an event on it; clicking an event opens it.

The interesting part is connecting, because **Google will not redirect OAuth to a LAN
address**: a redirect URI must be https or loopback, and `http://192.168.1.50:5150/…` is
refused outright. So the plugin supports both shapes:

- **No https?** Register `http://127.0.0.1:5150/oauth2callback` on the OAuth client. Google
  bounces the browser to a dead address, you copy the whole thing out of the address bar,
  and paste it into the page — it takes the `code=` out for you. One paste, once ever.
- **Have https?** Register `https://your-host/ext/google-calendar/callback` instead, and
  `GoogleCalendarEndpoints` completes the exchange itself. That is the endpoint extension
  point doing the one job a component cannot: being somewhere another site can redirect to.

Either way the refresh token is written back through `ConfigStore`, so it is encrypted at
rest like any other password field.

Two details worth stealing if you write anything against Google:

- **`access_type=offline` and `prompt=consent` together**, or you get an access token with
  no refresh token and the connection dies silently an hour later.
- **All-day ends are exclusive.** A one-day event on the 4th is `start 2026-08-04, end
  2026-08-05`. The conversion lives in one place here, because doing it at each call site
  is how every all-day event ends up a day long in the wrong direction.

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

## Renewals — a date that can page you

Domains, TLS certificates, subscriptions, warranties. A page of them, soonest first, red
once they are past.

The reason it is here rather than being a fourth thing like Chores is the second half: it
ships a **provider as well as a tab kind**. A tab kind cannot alert — alert rules are
written against a connection's metrics, and a page is not a connection — so the provider's
probe reads the plugin's own table and reports `days_until_next` and `overdue`. Add the
connection and the whole existing machinery applies: charts, thresholds, and a message
through whichever channel you set up. That pairing is the pattern for any plugin holding
data that is worth being told about, and without it a renewals page is a thing you
remember to check after the certificate has already expired.

### Certificates watch themselves

A renewal can carry a **TLS host**. A background job then opens a connection to it four
times a day, reads the certificate being presented, and writes its expiry onto the row —
so the date is observed rather than remembered, and a certificate renewed by Caddy or
acme.sh moves the row by itself with nothing to tick off.

LabbyTwo deliberately does not *renew* anything. That means ACME: an account key, a
challenge answered on port 80 or through your DNS provider's API, a fresh private key —
and then installing the result wherever the certificate is actually served, which a
dashboard cannot do. Holding that pile of credentials to produce a certificate it could
not install is a poor trade, and Caddy, Traefik and acme.sh already do it properly, next
to the thing being served. Pair this with an alert rule on `days_until_next` and a
webhook, and LabbyTwo asks while the thing that owns the certificate acts.

What it does catch is the failure the ACME clients cannot: **renewed but never reloaded**.
The file on disk is new, the process is still serving the old one. Checking from the
outside is the only way that is visible, and it is the most common way "automatic renewal"
silently isn't.

A failed check leaves the last known date alone rather than blanking it — "I cannot reach
the host" and "this expires today" are different things to say, and only one of them
should turn a row red.

One deliberate difference from Chores: renewing rolls the date forward from **when it was
due**, not from today. A domain paid three days late still renews on its anniversary, and
counting from today would walk the date a little further every year until it drifted into
a different month. Chores does the opposite, on purpose, because a chore you are three
weeks late on should not stay three weeks late for ever.

## Drop — a shelf both devices can reach

Put a file or some text here on the laptop; pick it up on the phone. The thing everyone
improvises with emails to themselves.

It is the example of the sixth extension point. Three pieces in one DLL, each doing the
job only it can:

- The **tab kind** takes the upload and lists what is there.
- The **endpoint** at `/ext/drop/download` hands the bytes back, with Range support, so a
  video on the shelf seeks instead of downloading whole.
- The **background job** clears expired items hourly and at startup. Before
  `IBackgroundJob` existed, nothing would ever have swept the shelf unless somebody
  happened to open the page — the disk would just fill.

The metadata lives in the host's database and the bytes in a folder beside it, so the list
is in every backup without putting three gigabytes of video into a SQLite row the
dashboard reads on every page load. The purge also removes orphaned files — bytes with no
row, left by a crash between the two writes — but only if they are more than ten minutes
old, so it cannot delete an upload that is still in flight.

> Everything on the shelf is readable by anyone who can open the dashboard. It is a
> convenience, not a vault.

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
