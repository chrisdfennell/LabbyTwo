# Starting, watching and stopping a published LabbyTwo from a CI script. Shared by
# smoke-boot.sh and perf-bigdb.sh, which ask different questions of the same process and
# need the same things when an answer is wrong: the app's log, a stack dump, and a
# deadline on every wait.
#
# Sourced, not run. The caller sets these first:
#   label          what the ::error:: line calls the test ("Smoke test")
#   base           http://127.0.0.1:<port>
#   work           the temp folder everything below lives in
#   app            the published app
#   database       the SQLite file
#   plugins        the plugin folder (may be empty)
#   log            where the app's output goes
#   probe_seconds  Labby__ProbeSeconds
#   boot_seconds   how long startup may take before it counts as hung
#   page_seconds   how long one page may take
#
# A caller may define on_fail, which runs before the log is printed. The performance
# script uses it to print its timing table, so a failed run still says how far it got.

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
  echo "::error::$label: $*"
  if declare -F on_fail >/dev/null; then on_fail || true; fi
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

# Published rather than run from bin, because published is what the image runs — and
# because a plain build serves its scripts and styles only in Development, so a browser
# would get a page with no blazor.web.js and nothing would ever draw. --no-build: this
# checks the build CI already made rather than making another.
publish_app() {
  say "Publishing the Release build"
  dotnet publish LabbyTwo.csproj --configuration Release --no-build --nologo -v quiet -o "$(native "$app")" > "$work/publish.log" 2>&1 \
    || { cat "$work/publish.log"; fail "publishing failed. Run 'dotnet build -c Release' first."; }
}

# Chrome and Node are on every GitHub runner; locally either may be missing, and the
# callers decide whether that skips a step or fails it.
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

# A Python that really runs, for when there is no sqlite3. Asked rather than trusted
# because on Windows "python3" is often the Store's placeholder, which is on the PATH,
# prints an advert and exits.
python_with_sqlite() {
  local candidate
  for candidate in python3 python; do
    if command -v "$candidate" >/dev/null 2>&1 && "$candidate" -c 'import sqlite3' >/dev/null 2>&1; then
      printf '%s' "$candidate"
      return
    fi
  done
  return 1
}

# SQL on stdin, run against the database. sqlite3 where there is one — every GitHub
# runner — and Python's sqlite3 module where there is not, which is most Windows machines.
sql() {
  if command -v sqlite3 >/dev/null 2>&1; then
    sqlite3 "$(native "$database")"
  else
    local py
    py="$(python_with_sqlite)" || fail "seeding needs sqlite3 or python."
    "$py" -c 'import sqlite3, sys; c = sqlite3.connect(sys.argv[1]); c.executescript(sys.stdin.read()); c.commit()' \
      "$(native "$database")"
  fi
}
