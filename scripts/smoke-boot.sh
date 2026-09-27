#!/usr/bin/env bash
#
# Starts the built app with every example plugin loaded and checks it actually works:
# it listens, the dashboard renders, and the monitor goes round.
#
#   dotnet build -c Release
#   for p in examples/*/; do dotnet build "$p" -c Release; done
#   bash scripts/smoke-boot.sh
#
# Unit tests could not have caught the two failures this exists for. v1.6.2 deadlocked in
# the container with the status page plugin installed — the log stopped at "Scanning
# plugins", nothing was ever listening, and no error was written anywhere. And a page
# that did synchronous SQLite work on the render thread left the dashboard spinning for
# ever while /healthz answered perfectly well. Both only show up in the real process, with
# the real plugins beside it, asked for a real page. So that is what this does, with a
# deadline on every step: a hang has to fail the build rather than hold it until the
# runner gives up six hours later.
#
# On failure it prints the app's log and, when it can, a stack dump of every thread. The
# dump is what found the deadlock: two threads each waiting on the other, in plain view.
#
# Settings, all optional:
#   SMOKE_PORT          port to listen on (a random one from 20000 up)
#   SMOKE_BOOT_SECONDS  how long startup may take before it counts as hung (60)
#   SMOKE_PAGE_SECONDS  how long one page may take (20)
#   SMOKE_SWEEP_SECONDS how long to wait for two monitor sweeps (60)
#   SMOKE_KEEP=1        keep the temp folder (database, plugins, log) afterwards

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

# Anywhere out of the way by default, so it does not meet a LabbyTwo someone is already
# running on this machine. Checked below: something else answering on the port would pass
# the health check on the app's behalf.
port="${SMOKE_PORT:-$((20000 + RANDOM % 20000))}"
boot_seconds="${SMOKE_BOOT_SECONDS:-60}"
page_seconds="${SMOKE_PAGE_SECONDS:-20}"
sweep_seconds="${SMOKE_SWEEP_SECONDS:-60}"
probe_seconds=5
base="http://127.0.0.1:$port"

work="$(mktemp -d)"
plugins="$work/plugins"
database="$work/labbytwo.db"
log="$work/app.log"
app="$work/app"
pid=""

# Git Bash on Windows hands the app /tmp/... paths it cannot open. Everywhere else this
# is the path unchanged.
native() {
  if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else printf '%s' "$1"; fi
}

say() { printf '==> %s\n' "$*"; }

running() { [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null; }

# For a failure that is the app ending, with how it ended: 134 is an abort and 137 a
# SIGKILL from outside, which point in quite different directions. Not a $(...) helper,
# because only the shell that started the app can wait for it.
died() {
  local code=0
  wait "$pid" 2>/dev/null || code=$?
  pid=""
  fail "$1 (exit status $code)."
}

stop_app() {
  if running; then
    kill "$pid" 2>/dev/null || true
    for _ in $(seq 1 20); do running || break; sleep 0.5; done
    running && kill -9 "$pid" 2>/dev/null || true
    wait "$pid" 2>/dev/null || true
  fi
  pid=""
}

cleanup() {
  stop_app
  if [ "${SMOKE_KEEP:-}" = "1" ]; then
    say "Kept $work"
  else
    rm -rf "$work"
  fi
}
trap cleanup EXIT

# Everything someone needs to work out why, in the CI log itself — the runner and its
# temp folder are gone by the time anyone reads it.
stack_dump() {
  running || { say "The app is no longer running, so there is no stack to dump."; return; }

  # Git Bash's $! is its own process number, not the one Windows gave dotnet.
  local target="$pid"
  [ -r "/proc/$pid/winpid" ] && target="$(cat "/proc/$pid/winpid")"

  export PATH="$PATH:$HOME/.dotnet/tools"
  if ! command -v dotnet-stack >/dev/null 2>&1; then
    say "Installing dotnet-stack for a stack dump"
    dotnet tool install -g dotnet-stack >/dev/null 2>&1 || { say "Could not install dotnet-stack."; return; }
  fi

  echo "::group::Stack dump of every managed thread"
  # A deadline on the dump too: a tool that hangs while diagnosing a hang helps nobody.
  if command -v timeout >/dev/null 2>&1; then
    timeout 90 dotnet-stack report -p "$target" || say "dotnet-stack did not produce a report."
  else
    dotnet-stack report -p "$target" || say "dotnet-stack did not produce a report."
  fi
  echo "::endgroup::"
}

fail() {
  echo "::error::Smoke test: $*"
  echo "::group::App log"
  cat "$log" 2>/dev/null || echo "(no log)"
  echo "::endgroup::"
  stack_dump
  exit 1
}

start_app() {
  say "Starting LabbyTwo on $base"
  # Appended, not truncated, so a failure on the second start still shows the first.
  echo "----- start $(date -u +%H:%M:%S) -----" >> "$log"
  ASPNETCORE_URLS="$base" \
  ASPNETCORE_ENVIRONMENT=Production \
  Labby__DatabasePath="$(native "$database")" \
  Labby__PluginPath="$(native "$plugins")" \
  Labby__Auth__Password="" \
  Labby__ProbeSeconds="$probe_seconds" \
  Logging__Console__FormatterName=simple \
  Logging__Console__FormatterOptions__TimestampFormat="HH:mm:ss.fff " \
    dotnet "$app/LabbyTwo.dll" --contentRoot "$(native "$app")" >> "$log" 2>&1 &
  pid=$!
}

# /healthz, which answers as soon as the server listens. The deadlock stopped it ever
# getting that far, so this is the check that would have caught v1.6.2.
wait_healthy() {
  local deadline=$((SECONDS + boot_seconds))
  while [ "$SECONDS" -lt "$deadline" ]; do
    running || died "the app exited during startup"
    if [ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 2 "$base/healthz" || true)" = "200" ]; then
      say "Healthy after $((SECONDS - deadline + boot_seconds))s"
      return
    fi
    sleep 1
  done
  fail "/healthz did not answer within ${boot_seconds}s — startup hung. The last thing the log says is where it stopped."
}

# A whole page, rendered on the server. Following the redirect matters: / sends you to the
# first tab, and the tab is where the cards — and the slow render that once hung — are.
fetch_page() {
  local path="$1" expect="$2" body="$work/page.html" code
  code="$(curl -sS -L -o "$body" -w '%{http_code}' --max-time "$page_seconds" "$base$path" || true)"
  [ "$code" = "200" ] || fail "GET $path returned '${code:-nothing}' within ${page_seconds}s, not 200."
  grep -q -- "$expect" "$body" || fail "GET $path answered, but without \"$expect\" in it."
  # CardBoundary swaps a card that threw for this notice. The page survives, which is the
  # point of it, but a card that fails on a fresh install is still a bug.
  if grep -q 'class="card-failed"' "$body"; then
    grep -o '<span>[^<]*failed to load\.</span>' "$body" | sed 's/<[^>]*>//g' >&2 || true
    fail "GET $path rendered, but a card on it failed."
  fi
  say "GET $path: 200"
}

# The cards themselves. The server's first answer is only the grid — every card a
# skeleton, on purpose, so one slow card cannot hold the page — and the cards draw once
# the browser has connected its circuit. curl never connects, so a card that throws, or a
# render that blocks the circuit the way the synchronous query did, is invisible to
# fetch_page. smoke-render.mjs drives a headless Chrome that does connect. Chrome and Node
# are on every GitHub runner, so there they are required; locally the step is skipped with
# a note when either cannot be found.
find_browser() {
  local candidate
  for candidate in "${SMOKE_BROWSER:-}" google-chrome google-chrome-stable chromium chromium-browser \
      "/c/Program Files/Google/Chrome/Application/chrome.exe" \
      "/c/Program Files (x86)/Microsoft/Edge/Application/msedge.exe" \
      "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"; do
    [ -n "$candidate" ] || continue
    if command -v "$candidate" >/dev/null 2>&1; then command -v "$candidate"; return; fi
    [ -x "$candidate" ] && { printf '%s' "$candidate"; return; }
  done
  return 1
}

render_in_browser() {
  local path="$1" browser result
  if ! browser="$(find_browser)" || ! command -v node >/dev/null 2>&1; then
    [ "${CI:-}" = "true" ] && fail "drawing the cards needs Chrome and Node, and one of them is missing."
    say "No Chrome or no Node here, so the cards on $path were not drawn. Set SMOKE_BROWSER to check them."
    return
  fi

  if result="$(node scripts/smoke-render.mjs "$browser" "$base$path" "$(native "$work/browser")" "$page_seconds" 2>&1)"; then
    say "Drew $path in a browser: $result"
  else
    printf '%s\n' "$result" >&2
    fail "the cards on $path did not all draw in a browser within ${page_seconds}s (above)."
  fi
}

sql() {
  if command -v sqlite3 >/dev/null 2>&1; then
    sqlite3 "$(native "$database")"
  else
    local py
    py="$(command -v python3 || command -v python)" || fail "seeding needs sqlite3 or python."
    "$py" -c 'import sqlite3, sys; c = sqlite3.connect(sys.argv[1]); c.executescript(sys.stdin.read()); c.commit()' \
      "$(native "$database")"
  fi
}

# The keys the example plugins register, read from their source so a new plugin is
# covered without anyone remembering to add it here. The first Type — or the constant it
# is built from — after a class declaring the interface.
declared_types() {
  local interface="$1"
  awk -v iface="$interface" '
    $0 ~ "class .*[:,] *" iface "([^A-Za-z]|$)" { want = 1; next }
    want && match($0, /(ProviderType|TypeKey) = "[^"]+"|string Type => "[^"]+"/) {
      s = substr($0, RSTART, RLENGTH); sub(/^[^"]*"/, "", s); sub(/"$/, "", s)
      print s; want = 0
    }' examples/*/*.cs | sort -u
}

# ---- The app ---------------------------------------------------------------------------
#
# Published rather than run from bin, because published is what the image runs — and
# because a plain build serves its scripts and styles only in Development, so the browser
# below would get a page with no blazor.web.js and nothing would ever draw. --no-build:
# this checks the build CI already made rather than making another.

say "Publishing the Release build"
dotnet publish LabbyTwo.csproj --configuration Release --no-build --nologo -v quiet -o "$(native "$app")" > "$work/publish.log" 2>&1 \
  || { cat "$work/publish.log"; fail "publishing failed. Run 'dotnet build -c Release' first."; }

# ---- Plugins ---------------------------------------------------------------------------

# The same set the release zips: each plugin's whole output folder, dependencies and all,
# less the symbols and the static web asset manifests. The terminal plugin is the reason —
# without SSH.NET and BouncyCastle beside it, it loads only in part.
mkdir -p "$plugins"
count=0
for plugin in examples/*/; do
  name="$(basename "$plugin")"
  output="$plugin/bin/Release/net10.0"
  [ -f "$output/$name.dll" ] || fail "$name has not been built. Build the example plugins in Release first."
  (cd "$output" && find . -type f ! -name '*.pdb' ! -name '*.staticwebassets.*' -exec cp {} "$plugins/" \;)
  count=$((count + 1))
done
say "$count plugins, $(find "$plugins" -name '*.dll' | wc -l | tr -d ' ') DLLs in $plugins"

# ---- First start: an empty install -----------------------------------------------------

if curl -s -o /dev/null --max-time 2 "$base/"; then
  fail "something is already answering on port $port. Set SMOKE_PORT to a free one."
fi

start_app
wait_healthy
fetch_page / "Welcome to LabbyTwo"
stop_app

# ---- Seed ------------------------------------------------------------------------------
#
# Straight into the database the first start created. Importing a config is a Settings
# page rather than an endpoint, so there is nothing to call from here, and rows written
# directly are exactly what the app would read after an import anyway.
#
# One connection per plugin provider, with no settings. Almost all of them are then down,
# which is fine and is the point — a probe failing is the common case and must be handled —
# and every one of them gets asked. One web service pointed at the app itself, so there is
# something that is up and a log line that proves a sweep happened. And one status event and
# a few samples for it, so the start that follows restores a known state and the charts
# have something to draw.

say "Seeding connections, a tab and widgets"
now="$(date +%s)"
{
  echo "BEGIN;"
  echo "INSERT INTO connections (id, provider, name, sort, settings) VALUES
          ('smoke-self', 'http', 'LabbyTwo itself', 0, '{\"url\":\"$base/healthz\",\"expect_status\":\"2xx\"}');"
  echo "INSERT INTO status_events (connection_id, ts, is_up, message) VALUES ('smoke-self', $((now - 3600)), 1, 'seeded');"
  for i in 1 2 3 4 5; do
    echo "INSERT INTO samples (connection_id, metric, ts, value) VALUES ('smoke-self', 'latency_ms', $((now - i * 300)), $((10 + i)));"
  done

  echo "INSERT INTO tabs (id, slug, name, kind, sort) VALUES ('smoke-grid', 'smoke', 'Smoke test', 'grid', 0);"
  echo "INSERT INTO tabs (id, slug, name, kind, sort) VALUES ('smoke-status', 'status', 'Everything', 'status', 1);"

  order=0
  widget() { # type, title, connection id or empty, settings json
    local connection="NULL"
    [ -n "$3" ] && connection="'$3'"
    echo "INSERT INTO widgets (id, tab_id, type, title, connection_id, sort, width, settings)
            VALUES ('w-$order', 'smoke-grid', '$1', '$2', $connection, $order, 4, '$4');"
    order=$((order + 1))
  }
  widget status-summary "Services" "" "{}"
  widget aggregate "Average response" "" '{"metric":"latency_ms","aggregate":"avg"}'
  widget service-tile "" smoke-self "{}"
  widget metric "" smoke-self '{"metric":"latency_ms"}'
  widget chart "" smoke-self '{"metric":"latency_ms"}'
  widget uptime "" smoke-self "{}"
  widget clock "" "" "{}"
  widget markdown "Notes" "" '{"content":"Smoke test"}'

  n=0
  for provider in $(declared_types IConnectionProvider); do
    n=$((n + 1))
    echo "INSERT INTO connections (id, provider, name, sort, settings)
            VALUES ('smoke-$provider', '$provider', 'Smoke $provider', $n, '{}');"
    widget service-tile "" "smoke-$provider" "{}"
  done

  # Each plugin's own cards, unconfigured, which is exactly how they first appear when
  # somebody adds one: they must say what they need rather than throw.
  for type in $(declared_types IWidgetType); do
    widget "$type" "" "" "{}"
  done
  echo "COMMIT;"
} > "$work/seed.sql"

# If reading the types out of the source ever stops matching, the plugins would quietly
# drop out of the test rather than fail it.
[ "$n" -gt 0 ] || fail "found no connection providers in examples/, so the plugins would go unprobed."

sql < "$work/seed.sql" || fail "seeding the database failed."
say "$(grep -c "INTO connections" "$work/seed.sql") connections, $(grep -c "INTO widgets" "$work/seed.sql") widgets"

# ---- Second start: a dashboard with things on it ---------------------------------------

start_app
wait_healthy
fetch_page / "Smoke test"
fetch_page /t/smoke "Smoke test"
fetch_page /t/status "Everything"

grep -q "Restored the last known status of" "$log" \
  || fail "the monitor did not restore the seeded status at startup."

# Two sweeps, not one: one proves the monitor started, two that it is still going round.
# The logger writes the origin and never the path, so this is the self-probe's line.
say "Waiting for two monitor sweeps"
deadline=$((SECONDS + sweep_seconds))
while :; do
  sweeps="$(grep -c "HTTP GET $base answered 200" "$log" || true)"
  [ "$sweeps" -ge 2 ] && break
  running || died "the app exited while the monitor was running"
  [ "$SECONDS" -lt "$deadline" ] || fail "only $sweeps probe(s) of the app itself in ${sweep_seconds}s, with ProbeSeconds=$probe_seconds. The monitor is stuck."
  sleep 1
done
say "$sweeps sweeps seen"

# Once more, now the monitor has written real history under the seeded rows.
fetch_page /t/smoke "Smoke test"
render_in_browser /t/smoke

# ---- The log ---------------------------------------------------------------------------
#
# A plugin that fails to load does not stop the app, on purpose — so nothing above would
# notice. Nor would an exception a request or a hosted service swallowed after logging it.

# "replaced with a notice" is CardBoundary catching a card that threw. Timestamps come
# first on each line, hence the loose anchor on the levels.
problems="$(grep -E "could not be loaded|Partly loaded|[Uu]nhandled exception|replaced with a notice|(^|[0-9] )(crit|fail): " "$log" || true)"
if [ -n "$problems" ]; then
  printf '%s\n' "$problems" >&2
  fail "the log reports problems (above)."
fi

stop_app
say "LabbyTwo starts, renders and monitors with all $count example plugins loaded."
