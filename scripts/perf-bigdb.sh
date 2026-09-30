#!/usr/bin/env bash
#
# Starts the published app on a database the size of a real install's, times the pages
# that read history, and fails if any of them is slow.
#
#   dotnet build -c Release          (the app and the tests)
#   bash scripts/perf-bigdb.sh
#
# Twice in one week the dashboard hung on somebody's NAS while being perfectly quick on
# every machine it was written on. The latest-value query ranked a connection's whole
# history to find one row per metric, so the dashboard never loaded; and the metric list
# used SELECT DISTINCT over the same rows, so the alert rules page never opened. Both were
# fine on a database with a few thousand rows, which is all smoke-boot.sh seeds, and all
# a developer's laptop ever has. Neither was a crash, so nothing but a clock would notice.
#
# So this builds the database a busy install really has — a week of raw samples for two
# dozen connections at a 30-second probe, a half-year of hourly summaries behind them,
# months of up/down history and a few alert rules — and asks for the pages that read it,
# each against a budget. The budgets are generous, several times what a healthy build
# takes on a CI runner, because the point is to catch a query that grew with the table,
# and those are not 20% slower: they are ten or a hundred times slower, or never finish.
#
# Pages alone are not enough, though. Cards read the latest values from memory now, and
# on a runner's fast disk the DISTINCT that hung a NAS adds a third of a second to a page
# that takes one anyway. So the app is then stopped and BigDatabaseTimings, in the test
# project, times the history queries on their own against the same file — where the
# difference between the fixed query and the broken one is a hundredfold — and runs the
# history rollup over the extra day of raw samples seeded for it, which the app would
# not get round to for a quarter of an hour.
#
# A timing table is printed whatever happens. On failure the app's log and a stack dump
# of every thread follow it, as in smoke-boot.sh.
#
# Settings, all optional:
#   PERF_PORT           port to listen on (a random one from 20000 up)
#   PERF_CONNECTIONS    connections with a full history (24)
#   PERF_RAW_DAYS       the raw retention the app runs with (7, its default); one day
#                       more than this is seeded, for the rollup to fold
#   PERF_INTERVAL       seconds between raw samples (30, the default probe interval)
#   PERF_HOURLY_DAYS    days of hourly summaries, counting back from now (180)
#   PERF_SLACK          multiply every budget by this, for a slow machine (1)
#   PERF_STEP_SECONDS   how long any one step may take before it counts as hung (60)
#   PERF_KEEP=1         keep the temp folder (database, log) afterwards

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

port="${PERF_PORT:-$((20000 + RANDOM % 20000))}"
connections="${PERF_CONNECTIONS:-24}"
raw_days="${PERF_RAW_DAYS:-7}"
interval="${PERF_INTERVAL:-30}"
hourly_days="${PERF_HOURLY_DAYS:-180}"
slack="${PERF_SLACK:-1}"
# Past a budget the run carries on and fails at the end, so one slow page does not hide
# the others. Past this it stops there and then: the app is stuck, and the stack dump is
# worth more taken now than after every later step has waited out its own deadline.
boot_seconds="${PERF_STEP_SECONDS:-60}"
page_seconds="${PERF_STEP_SECONDS:-60}"
probe_seconds=5
base="http://127.0.0.1:$port"
# The app's own setting, so the retention it rolls up by is the one the seed assumed.
export Labby__RetentionDays="$raw_days"

work="$(mktemp -d)"
plugins="$work/plugins"
database="$work/labbytwo.db"
log="$work/app.log"
app="$work/app"
label="Performance test"

# shellcheck source=lib/app.sh
. "$root/scripts/lib/app.sh"

cleanup() {
  stop_app
  if [ "${PERF_KEEP:-}" = "1" ]; then
    say "Kept $work"
  else
    rm -rf "$work"
  fi
}
trap cleanup EXIT

now_ms() { date +%s%3N; }

# One value from the database: sqlite3 where there is one, Python where there is not.
sql_value() {
  if command -v sqlite3 >/dev/null 2>&1; then
    sqlite3 "$(native "$database")" "$1"
  else
    local py
    py="$(python_with_sqlite)" || fail "reading the database needs sqlite3 or python."
    "$py" -c 'import sqlite3, sys; print(sqlite3.connect(sys.argv[1]).execute(sys.argv[2]).fetchone()[0])' \
      "$(native "$database")" "$1"
  fi
}

# ---- Timings ---------------------------------------------------------------------------

rows=()
over=0

# name, milliseconds taken, budget in milliseconds before PERF_SLACK
record() {
  local budget verdict
  budget="$(awk -v b="$3" -v s="$slack" 'BEGIN { printf "%d", b * s }')"
  if [ "$2" -le "$budget" ]; then
    verdict="ok"
  else
    verdict="OVER"
    over=$((over + 1))
    echo "::error::$label: $1 took $2 ms; its budget is $budget ms."
  fi
  rows+=("$1|$2|$budget|$verdict")
  say "$1: $2 ms (budget $budget ms) $verdict"
}

print_table() {
  [ "${#rows[@]}" -gt 0 ] || return 0
  echo
  printf '%-44s %10s %10s  %s\n' "Step" "Took (ms)" "Budget" ""
  printf '%-44s %10s %10s  %s\n' "----" "---------" "------" ""
  local row name took budget verdict
  for row in "${rows[@]}"; do
    IFS='|' read -r name took budget verdict <<< "$row"
    printf '%-44s %10s %10s  %s\n' "$name" "$took" "$budget" "$verdict"
  done
  echo
}

on_fail() { print_table; }

# A server-rendered page. Prerendering runs the page's OnInitializedAsync, so for the alert
# rules page this is the metric list for every connection, read before the first byte.
timed_get() {
  local name="$1" path="$2" expect="$3" budget="$4" body="$work/page.html" code started took
  started="$(now_ms)"
  code="$(curl -sS -L -o "$body" -w '%{http_code}' --max-time "$page_seconds" "$base$path" || true)"
  took=$(($(now_ms) - started))
  [ "$code" = "200" ] || fail "GET $path returned '${code:-nothing}' within ${page_seconds}s, not 200."
  grep -q -- "$expect" "$body" || fail "GET $path answered, but without \"$expect\" in it."
  if grep -q 'class="card-failed"' "$body"; then
    fail "GET $path rendered, but a card on it failed."
  fi
  record "$name" "$took" "$budget"
}

# A page in a real browser, with its circuit connected — which is where the cards draw
# and where the interactive half of a page does its reading. Extra arguments go to
# smoke-render.mjs. What the page said when it got there is left in $browser_said.
browser_said=""
timed_browser() {
  local name="$1" path="$2" budget="$3" result took
  shift 3
  browser_said=""
  if [ -z "${browser:-}" ]; then
    say "$name: skipped, no Chrome or no Node here. Set SMOKE_BROWSER to run it."
    return
  fi
  if ! result="$(node scripts/smoke-render.mjs "$browser" "$base$path" "$(native "$work/browser")" "$page_seconds" --time "$@" 2>&1)"; then
    printf '%s\n' "$result" >&2
    fail "$name did not finish within ${page_seconds}s (above)."
  fi
  took="$(printf '%s\n' "$result" | sed -n 's/^elapsed_ms=//p')"
  browser_said="$(printf '%s\n' "$result" | grep -v '^elapsed_ms=' | head -1)"
  say "$name: $browser_said"
  record "$name" "$took" "$budget"
}

browser=""
if found="$(find_browser)" && command -v node >/dev/null 2>&1; then
  browser="$found"
elif [ "${CI:-}" = "true" ]; then
  fail "timing pages in a browser needs Chrome and Node, and one of them is missing."
fi

# ---- The app, and an empty database for it to create ------------------------------------

publish_app
mkdir -p "$plugins"

if curl -s -o /dev/null --max-time 2 "$base/"; then
  fail "something is already answering on port $port. Set PERF_PORT to a free one."
fi

# The schema is whatever the migrations in Storage/Db.cs make of it, never a copy here
# that could drift from them.
start_app
wait_healthy
stop_app

# ---- Seed ------------------------------------------------------------------------------
#
# Generated inside SQLite by recursive CTEs, a few statements for millions of rows: one
# INSERT per row from here would take longer than the rest of the job put together.
#
# The raw rows go in in time order, every series interleaved, because that is how a real
# install writes them — one sweep at a time — and it decides how scattered one series is
# across the table's pages, which is exactly what makes a query that walks a series slow.
# The index is dropped for the load and built once afterwards from its own definition,
# which is the same index in a fraction of the time.

now="$(date +%s)"
hour=3600
# Raw samples go back a day further than the retention, and the summaries stop where they
# start: an install whose rollup has a day to catch up on, which the pages must cope with
# and BigDatabaseTimings then folds.
raw_start=$(( (now - (raw_days + 1) * 86400) / hour * hour ))
hourly_start=$(( (now - hourly_days * 86400) / hour * hour ))
per_hour=$(( hour / interval ))

index_sql="$(sql_value "SELECT sql FROM sqlite_master WHERE name = 'ix_samples_series'")"
[ -n "$index_sql" ] || fail "the first start did not create ix_samples_series, so the schema is not what this expects."

has_kind="$(sql_value "SELECT COUNT(*) FROM pragma_table_info('alert_rules') WHERE name = 'kind'")"

say "Seeding $connections connections: $((raw_days + 1)) days raw every ${interval}s, ${hourly_days} days hourly"

{
  cat <<SQL
PRAGMA journal_mode = DELETE;
PRAGMA synchronous = OFF;
PRAGMA cache_size = -262144;
BEGIN;

CREATE TEMP TABLE perf_connection (id TEXT, n INTEGER);
INSERT INTO perf_connection
  WITH RECURSIVE c(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM c WHERE n < $connections)
  SELECT printf('perf-%02d', n), n FROM c;

-- What a server reports, roughly: each one's base and how far it wanders. Values follow
-- the hour of day a little, so the "unusual for the time" baselines have a shape to learn.
CREATE TEMP TABLE perf_metric (name TEXT, base REAL, spread REAL);
INSERT INTO perf_metric VALUES
  ('latency_ms', 20, 40), ('cpu_percent', 10, 50), ('mem_percent', 50, 20),
  ('disk_percent', 40, 0), ('load_1', 0.5, 3), ('net_rx_mbps', 5, 200),
  ('net_tx_mbps', 2, 50), ('temp_c', 38, 20), ('disk_free_gb', 1500, 0), ('uptime_s', 0, 0);

-- Every connection probes LabbyTwo itself, so each one is up, answers quickly and keeps
-- adding samples on top of the seeded ones while the pages are being timed.
INSERT INTO connections (id, provider, name, sort, settings)
  SELECT id, 'http', 'Server ' || n, n, '{"url":"$base/healthz","expect_status":"2xx"}' FROM perf_connection;

-- Speed results arrive hours apart and on a different card. Pointed at a closed port, so
-- its probe fails at once rather than waiting on anything.
INSERT INTO connections (id, provider, name, sort, settings)
  VALUES ('perf-speed', 'speedtest-tracker', 'Internet', 100, '{"url":"http://127.0.0.1:9"}');

DROP INDEX ix_samples_series;

INSERT INTO samples (connection_id, metric, ts, value)
  WITH RECURSIVE t(ts) AS (SELECT $raw_start UNION ALL SELECT ts + $interval FROM t WHERE ts + $interval < $now)
  SELECT c.id, m.name, t.ts,
         CASE m.name
           WHEN 'disk_percent' THEN 40 + c.n + (t.ts - $hourly_start) * 2e-6
           WHEN 'disk_free_gb' THEN 1500 - c.n * 20 - (t.ts - $hourly_start) * 4e-5
           WHEN 'uptime_s' THEN t.ts - $hourly_start
           ELSE m.base + m.spread * (0.5 * ((t.ts / 3600) % 24) / 24.0 + 0.5 * (random() & 1023) / 1024.0)
         END
  FROM t CROSS JOIN perf_connection AS c CROSS JOIN perf_metric AS m;

INSERT INTO samples (connection_id, metric, ts, value)
  WITH RECURSIVE t(ts) AS (SELECT $raw_start UNION ALL SELECT ts + 3600 FROM t WHERE ts + 3600 < $now),
       m(name, base, spread) AS (VALUES ('download_mbps', 400, 500), ('upload_mbps', 30, 20),
                                        ('ping_ms', 8, 10), ('jitter_ms', 1, 4))
  SELECT 'perf-speed', m.name, t.ts, m.base + m.spread * (random() & 1023) / 1024.0
  FROM t CROSS JOIN m;

$index_sql;

-- The hours the rollup has already folded, in key order since the table is its key.
INSERT INTO samples_hourly (connection_id, metric, hour_ts, min, max, avg, count, last_ts, last_value)
  WITH RECURSIVE h(hour) AS (SELECT $hourly_start UNION ALL SELECT hour + 3600 FROM h WHERE hour + 3600 < $raw_start)
  SELECT id, name, hour, v - spread * 0.2, v + spread * 0.2, v, $per_hour, hour + 3600 - $interval, v
  FROM (SELECT c.id, m.name, m.spread, h.hour,
               CASE m.name
                 WHEN 'disk_percent' THEN 40 + c.n + (h.hour - $hourly_start) * 2e-6
                 WHEN 'disk_free_gb' THEN 1500 - c.n * 20 - (h.hour - $hourly_start) * 4e-5
                 WHEN 'uptime_s' THEN h.hour - $hourly_start
                 ELSE m.base + m.spread * (0.5 * ((h.hour / 3600) % 24) / 24.0 + 0.5 * (random() & 1023) / 1024.0)
               END AS v
        FROM perf_connection AS c CROSS JOIN perf_metric AS m CROSS JOIN h);

INSERT INTO samples_hourly (connection_id, metric, hour_ts, min, max, avg, count, last_ts, last_value)
  WITH RECURSIVE h(hour) AS (SELECT $hourly_start UNION ALL SELECT hour + 3600 FROM h WHERE hour + 3600 < $raw_start),
       m(name, base, spread) AS (VALUES ('download_mbps', 400, 500), ('ping_ms', 8, 10), ('jitter_ms', 1, 4),
                                        ('upload_mbps', 30, 20))
  SELECT 'perf-speed', name, hour, v, v, v, 1, hour + 1800, v
  FROM (SELECT m.name, h.hour, m.base + m.spread * (random() & 1023) / 1024.0 AS v FROM m CROSS JOIN h);

-- Months of outages: every connection drops for a few minutes every six hours or so,
-- staggered, and is up again before now. The first event is when monitoring began.
INSERT INTO status_events (connection_id, ts, is_up, message)
  SELECT id, $hourly_start, 1, 'seeded' FROM perf_connection;
INSERT INTO status_events (connection_id, ts, is_up, message)
  WITH RECURSIVE k(k) AS (SELECT 0 UNION ALL SELECT k + 1 FROM k WHERE k < $(( hourly_days * 8 )))
  SELECT c.id,
         $hourly_start + 600 + (k.k / 2) * (21600 + c.n * 60) + (k.k % 2) * (120 + c.n * 10),
         k.k % 2,
         CASE k.k % 2 WHEN 0 THEN 'Connection refused' ELSE 'HTTP 200' END
  FROM perf_connection AS c CROSS JOIN k
  WHERE $hourly_start + 600 + (k.k / 2) * (21600 + c.n * 60) < $now - 7200
  ORDER BY 2;

-- Rules of both kinds, pinned and not: an unpinned rule reads its metric on every
-- connection, which is the expensive case.
SQL

  if [ "$has_kind" = "1" ]; then
    cat <<'SQL'
INSERT INTO alert_rules (id, name, connection_id, metric, comparison, threshold, for_minutes, kind, unusual_by) VALUES
  ('perf-rule-1', 'CPU pinned', NULL, 'cpu_percent', 'above', 95, 10, 'threshold', NULL),
  ('perf-rule-2', 'Disk nearly full', NULL, 'disk_percent', 'above', 90, 0, 'threshold', NULL),
  ('perf-rule-3', 'Slow answers', 'perf-01', 'latency_ms', 'above', 500, 5, 'threshold', NULL),
  ('perf-rule-4', 'Memory', NULL, 'mem_percent', 'above', 95, 5, 'threshold', NULL),
  ('perf-rule-5', 'Slow internet', 'perf-speed', 'download_mbps', 'below', 100, 0, 'threshold', NULL),
  ('perf-rule-6', 'Download less than half its usual', 'perf-speed', 'download_mbps', 'below', 50, 0, 'unusual', 'percent'),
  ('perf-rule-7', 'Hotter than usual', NULL, 'temp_c', 'above', 4, 15, 'unusual', 'spread');
SQL
  else
    cat <<'SQL'
INSERT INTO alert_rules (id, name, connection_id, metric, comparison, threshold, for_minutes) VALUES
  ('perf-rule-1', 'CPU pinned', NULL, 'cpu_percent', 'above', 95, 10),
  ('perf-rule-2', 'Disk nearly full', NULL, 'disk_percent', 'above', 90, 0),
  ('perf-rule-3', 'Slow answers', 'perf-01', 'latency_ms', 'above', 500, 5),
  ('perf-rule-4', 'Memory', NULL, 'mem_percent', 'above', 95, 5),
  ('perf-rule-5', 'Slow internet', 'perf-speed', 'download_mbps', 'below', 100, 0);
SQL
  fi

  echo "INSERT INTO tabs (id, slug, name, kind, sort) VALUES ('perf-grid', 'perf', 'History', 'grid', 0);"
  echo "INSERT INTO tabs (id, slug, name, kind, sort) VALUES ('perf-status', 'status', 'Everything', 'status', 1);"

  # Every card here reads history, most of them a month of it: the tab someone builds
  # once they have had LabbyTwo long enough to have a big database.
  order=0
  widget() { # type, title, connection id or empty, settings json
    local connection="NULL"
    [ -n "$3" ] && connection="'$3'"
    echo "INSERT INTO widgets (id, tab_id, type, title, connection_id, sort, width, settings)
            VALUES ('perf-w-$order', 'perf-grid', '$1', '$2', $connection, $order, 4, '$4');"
    order=$((order + 1))
  }
  widget status-summary "Services" "" "{}"
  widget aggregate "Average CPU" "" '{"metric":"cpu_percent","aggregate":"avg"}'
  widget aggregate "Free space" "" '{"metric":"disk_free_gb","aggregate":"sum"}'
  widget chart "CPU, a week" perf-01 '{"metric":"cpu_percent","hours":"168"}'
  widget chart "Response, a month" perf-02 '{"metric":"latency_ms","compare":"cpu_percent","hours":"720"}'
  widget chart "Disk, a month" perf-03 '{"metric":"disk_percent","hours":"720"}'
  widget chart "Temperature, a week" perf-04 '{"metric":"temp_c","hours":"168"}'
  widget speedtest "" perf-speed '{"hours":"720"}'
  widget gauge "" perf-05 '{"metric":"disk_percent","warn":"85"}'
  widget metrics-table "" perf-06 '{}'
  widget readings-table "" perf-07 '{"hours":"24","open":"true"}'
  widget uptime "" perf-08 '{"days":"30"}'
  widget running-out "Running out" "" '{}'
  widget metric "" perf-09 '{"metric":"cpu_percent"}'
  widget changes "" "" '{"hours":"168"}'
  for n in 10 11 12 13; do
    widget service-tile "" "perf-$n" '{}'
  done

  echo "COMMIT;"
  echo "PRAGMA journal_mode = WAL;"
} > "$work/seed.sql"

seed_started="$(now_ms)"
sql < "$work/seed.sql" > /dev/null || fail "seeding the database failed."
seed_ms=$(($(now_ms) - seed_started))

raw_rows="$(sql_value "SELECT COUNT(*) FROM samples")"
hourly_rows="$(sql_value "SELECT COUNT(*) FROM samples_hourly")"
event_rows="$(sql_value "SELECT COUNT(*) FROM status_events")"
say "Seeded $raw_rows raw samples, $hourly_rows hourly summaries and $event_rows status events in $((seed_ms / 1000))s;" \
  "the database is $(( $(wc -c < "$database") / 1048576 )) MB"

# ---- Timed ------------------------------------------------------------------------------
#
# In the order somebody meets them: the app starting on the big file, the dashboard, then
# the settings pages that once never opened.

boot_started="$(now_ms)"
start_app
wait_healthy
record "Start to /healthz" $(($(now_ms) - boot_started)) 30000

timed_get "GET / (the first tab, server-rendered)" / "History" 5000
timed_browser "Every card on the History tab drawn" /t/perf 10000
timed_get "GET /t/status (uptime of everything)" /t/status "Everything" 5000

# Prerendered, so this is the rule list and every connection's metric names, read on the
# server before the first byte: the page that hung.
timed_get "GET /settings/alerts" /settings/alerts "Alert rules" 5000

# And interactive: the rules listed, and the editor's metric suggestions filled in — the
# list that came from SELECT DISTINCT. The button opens the editor; the suggestions are
# already loaded by then, for every connection, since a new rule is not pinned to one.
timed_browser "Alert rules usable, editor's metrics listed" /settings/alerts 5000 \
  --click-text "New rule" \
  --until "(() => {
    const rules = document.querySelectorAll('table tbody tr').length;
    const metrics = document.querySelectorAll('#rule-metric-options option').length;
    return rules > 0 && metrics > 0 ? rules + ' rules listed, ' + metrics + ' metrics offered' : '';
  })()"

timed_get "GET /settings/health" /settings/health "Time the dashboard query" 5000

# The health page's own stopwatch on the latest-value query, once per connection. Cards
# read those values from memory now, so a slow query no longer holds up the dashboard;
# it shows as a cache that takes minutes to warm, and here. Two rows: how long the page
# took to answer the button, and what the stopwatch said, which is the one with room
# between a fixed query (milliseconds) and the ranking that hung (seconds).
timed_browser "Health page: time the dashboard query" /settings/health 10000 \
  --click-text "Time the dashboard query" \
  --until "(() => {
    const text = [...document.querySelectorAll('dd')].map(d => d.textContent.trim().replace(/\\s+/g, ' '))
      .find(t => / for \\d+ connection\\(s\\)/.test(t));
    const said = text && text.match(/^([\\d.,]+) (ms|s|min|h) for/);
    if (!said) return '';
    const unit = { ms: 1, s: 1000, min: 60000, h: 3600000 }[said[2]];
    return 'query_ms=' + Math.round(parseFloat(said[1].replace(',', '.')) * unit) + ' ' + text;
  })()"
if [ -n "$browser_said" ]; then
  record "Dashboard query, by the health page's clock" "$(printf '%s' "$browser_said" | sed -n 's/^query_ms=\([0-9]*\).*/\1/p')" 1000
fi

# ---- The log ---------------------------------------------------------------------------
#
# A big database is also where a busy timeout or a job that gives up shows first, and
# neither would slow a page down enough to see.
problems="$(grep -E "[Uu]nhandled exception|replaced with a notice|(^|[0-9] )(crit|fail): " "$log" || true)"
if [ -n "$problems" ]; then
  printf '%s\n' "$problems" >&2
  fail "the log reports problems (above)."
fi

stop_app

# ---- The queries on their own, and the rollup ------------------------------------------
#
# With the app stopped, so nothing else is writing while the rollup holds the lock. See
# tests/LabbyTwo.Tests/BigDatabaseTimings.cs for what is timed and why it is there.

say "Timing the history queries and the rollup directly"
timings="$work/timings.txt"
harness=(dotnet test tests/LabbyTwo.Tests/LabbyTwo.Tests.csproj --configuration Release --no-build --nologo
         --filter "FullyQualifiedName~BigDatabaseTimings")
command -v timeout >/dev/null 2>&1 && harness=(timeout 600 "${harness[@]}")
LABBY_PERF_DB="$(native "$database")" LABBY_PERF_TIMINGS="$(native "$timings")" LABBY_PERF_RAW_DAYS="$raw_days" \
  "${harness[@]}" > "$work/harness.log" 2>&1 \
  || { cat "$work/harness.log"; fail "timing the queries failed or took over ten minutes. Build the tests in Release first."; }
# A filter that matched nothing, or a test that skipped itself, still exits 0.
[ -s "$timings" ] || { cat "$work/harness.log"; fail "BigDatabaseTimings did not run, so the queries went untimed."; }

while IFS='|' read -r key took detail; do
  case "$key" in
    metrics)      name="Query: every connection's metric names"; budget=100 ;;
    latest)       name="Query: every connection's latest readings"; budget=100 ;;
    # A week of raw rows per series, grouped by hour. About 130 ms from the covering index;
    # 1,600 ms warm, and 80 MB of reads per series cold, when each row was a table lookup.
    chart30)      name="Query: a 30-day chart for every connection"; budget=1000 ;;
    uptime30)     name="Query: 30-day uptime for every connection"; budget=1000 ;;
    restore)      name="Query: what restore reads at startup"; budget=1000 ;;
    rollup)       name="Rollup: a day behind, folded"; budget=120000 ;;
    rollup-batch) name="Rollup: longest hold on the write lock"; budget=2000 ;;
    *)            name="$key"; budget=1000 ;;
  esac
  say "$name — $detail"
  record "$name" "$took" "$budget"
done < "$timings"

if [ "$over" -gt 0 ]; then
  fail "$over step(s) went over budget on a database of $raw_rows raw samples (table above)."
fi
print_table
say "Everything stayed within budget on $raw_rows raw samples and $hourly_rows hourly summaries."

