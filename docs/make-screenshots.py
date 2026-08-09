#!/usr/bin/env python3
"""Capture the README screenshots.

Builds a throwaway database with a plausible home lab in it, starts LabbyTwo against
that database, and drives headless Chrome over the pages. Everything it invents is
RFC 5737 documentation addresses and made-up service names — no real host of anyone's
ends up in a committed image.

    python docs/make-screenshots.py

Needs Chrome or Edge on PATH (or in the usual Windows locations) and a built LabbyTwo.
"""

from __future__ import annotations

import json
import math
import os
import shutil
import signal
import sqlite3
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SHOTS = ROOT / "docs" / "images"
PORT = 5177
BASE = f"http://127.0.0.1:{PORT}"

# Wide enough for the 12-column grid to be a grid rather than a stack.
VIEWPORT = (1440, 1000)


# Pointed at this instance's own health endpoint: real probes, real response times, and
# no dependency on anything outside the machine running the shoot.
ALIVE = json.dumps({"url": f"{BASE}/healthz"})


def new_id() -> str:
    return uuid.uuid4().hex[:12]


def seed(db_path: Path) -> dict[str, str]:
    """Write a demo dashboard straight into SQLite, so no clicking is needed."""
    schema = sqlite3.connect(db_path)
    cur = schema.cursor()

    tabs = [
        (new_id(), "home", "Home", "\U0001f3e0", "grid", 0, 1, json.dumps({"subtitle": "Everything at a glance"})),
        (new_id(), "media", "Media", "\U0001f3ac", "grid", 1, 1, "{}"),
        (new_id(), "status", "Status", "\U0001f4f6", "status", 2, 1, json.dumps({"days": "30"})),
        (new_id(), "runbooks", "Runbooks", "\U0001f4dd", "notes", 3, 1, "{}"),
        (new_id(), "weather", "Weather", "🌦️", "grid", 4, 1, "{}"),
    ]
    cur.executemany(
        "INSERT INTO tabs (id, slug, name, icon, kind, sort, enabled, settings) VALUES (?,?,?,?,?,?,?,?)",
        tabs,
    )
    home, media = tabs[0][0], tabs[1][0]
    weather_tab = tabs[4][0]

    # RFC 5737 / RFC 3849 documentation ranges: safe to publish, obviously not real.
    connections = [
        (new_id(), "http", "Proxmox", "🖧", ALIVE),
        (new_id(), "http", "TrueNAS", "🗄", ALIVE),
        (new_id(), "http", "Jellyfin", "🎞", ALIVE),
        (new_id(), "http", "AdGuard Home", "🛡", ALIVE),
        # Deliberately unreachable: a dashboard where nothing is ever down shows only
        # half of what the thing does.
        (new_id(), "http", "UPS", "🔋", json.dumps({"url": "http://198.51.100.9", "timeout": "2"})),
        (new_id(), "http", "Grafana", "📈", ALIVE),
        # Never probed successfully — the readings below are written straight into the
        # metrics history, which is all the weather cards read.
        (new_id(), "ambient", "Weather station", "🌦️",
         json.dumps({"api_key": "demo", "app_key": "demo"})),
    ]
    for index, (cid, provider, name, icon, settings) in enumerate(connections):
        cur.execute(
            "INSERT INTO connections (id, provider, name, icon, enabled, sort, settings, alerts) "
            "VALUES (?,?,?,?,1,?,?,1)",
            (cid, provider, name, icon, index, settings),
        )

    WEATHER = connections[-1][0]

    widgets = [
        (home, "greeting", "", 6, None, json.dumps({"name": "Chris", "show_date": "true"})),
        (home, "clock", "", 3, None, json.dumps({"show_date": "true"})),
        (home, "status-summary", "Services", 3, None, "{}"),
        (home, "search", "", 12, None, json.dumps({"engine": "duckduckgo"})),
        (home, "service-tile", "", 3, connections[0][0], "{}"),
        (home, "service-tile", "", 3, connections[1][0], "{}"),
        (home, "service-tile", "", 3, connections[2][0], "{}"),
        (home, "service-tile", "", 3, connections[3][0], "{}"),
        (home, "chart", "Response time", 8, connections[0][0],
         json.dumps({"metric": "latency_ms", "hours": "24"})),
        (home, "active-alerts", "Alerts", 4, None, "{}"),
        (home, "links", "Bookmarks", 4, None, json.dumps({
            "links": json.dumps([
                {"Icon": "", "Name": "Proxmox", "Url": "https://198.51.100.5:8006"},
                {"Icon": "", "Name": "TrueNAS", "Url": "https://198.51.100.6"},
                {"Icon": "", "Name": "Grafana", "Url": "http://198.51.100.10:3000"},
            ]),
            "new_tab": "true",
            "favicons": "false",
        })),
        (home, "markdown", "Notes", 4, None, json.dumps({
            "content": "### This week\n\n- Replace the UPS battery\n- Move backups to the new pool\n"
        })),
        (home, "metric", "", 4, connections[0][0],
         json.dumps({"metric": "latency_ms", "show_sparkline": "true"})),
        (media, "service-tile", "", 4, connections[2][0], "{}"),
        (weather_tab, "weather-today", "", 4, WEATHER, json.dumps({"units": "imperial"})),
        (weather_tab, "wind-compass", "", 3, WEATHER, json.dumps({"units": "imperial"})),
        (weather_tab, "indoor-outdoor", "", 5, WEATHER, json.dumps({"units": "imperial"})),
        (weather_tab, "weather", "Conditions", 5, WEATHER, json.dumps({"units": "imperial"})),
        (weather_tab, "radar", "Radar", 7, None,
         json.dumps({"source": "rainviewer", "latitude": "39.74", "longitude": "-104.98",
                     "zoom": "7", "height": "360"})),
    ]
    for index, (tab, wtype, title, width, conn, settings) in enumerate(widgets):
        cur.execute(
            "INSERT INTO widgets (id, tab_id, type, title, connection_id, sort, width, settings) "
            "VALUES (?,?,?,?,?,?,?,?)",
            (new_id(), tab, wtype, title, conn, index, width, settings),
        )

    cur.execute(
        "INSERT INTO notes (id, tab_id, title, content, sort, updated_at) VALUES (?,?,?,?,?,?)",
        (new_id(), tabs[3][0], "Restoring a backup",
         "1. Stop the service\n2. `zfs rollback tank/data@nightly`\n3. Start it again\n", 0,
         int(time.time())),
    )

    rules = [
        (new_id(), "", None, "disk_percent", "above", 90.0, 85.0, 10, 1),
        (new_id(), "", None, "battery_percent", "below", 40.0, 60.0, 0, 1),
        # Low on purpose: these probes are local and answer in single-digit milliseconds,
        # and a screenshot of the alerting should show it actually alerting.
        (new_id(), "Response time creeping up", connections[0][0], "latency_ms", "above", 2.0, 1.0, 0, 1),
    ]
    cur.executemany(
        "INSERT INTO alert_rules (id, name, connection_id, metric, comparison, threshold, "
        "clear_threshold, for_minutes, enabled) VALUES (?,?,?,?,?,?,?,?,?)",
        rules,
    )

    cur.execute(
        "INSERT INTO app_settings (key, value) VALUES ('public_status_token', 'demo-token') "
        "ON CONFLICT(key) DO UPDATE SET value = excluded.value"
    )

    # A little history so the chart and the uptime bars are not empty.
    now = int(time.time())
    samples = []
    for index, (cid, *_) in enumerate(connections):
        base = 25 + index * 9
        value = float(base)
        for step, minute in enumerate(range(24 * 60, 0, -5)):
            ts = now - minute * 60
            # A slow drift plus a deterministic wobble, so it looks measured rather than
            # generated, and looks the same every time the shoot is re-run.
            drift = math.sin(step / 26.0) * base * 0.35
            wobble = math.sin(step * 1.9) * base * 0.08
            value = max(3.0, base + drift + wobble)
            samples.append((cid, "latency_ms", ts, round(value, 2)))
    weather_now = {
        "temp_outdoor_c": 7.4, "temp_indoor_c": 20.8, "feels_like_c": 4.9, "dew_point_c": 3.1,
        "humidity": 76, "humidity_indoor": 41, "wind_mph": 11.5, "gust_mph": 21.3,
        "wind_dir": 293, "pressure_inhg": 29.94, "rain_in": 0.12, "uv_index": 2,
    }
    for metric, value in weather_now.items():
        for minute in range(0, 24 * 60, 15):
            drift = math.sin(minute / 90.0) * (abs(value) * 0.18)
            samples.append((WEATHER, metric, now - minute * 60, round(value + drift, 2)))

    cur.executemany("INSERT INTO samples (connection_id, metric, ts, value) VALUES (?,?,?,?)", samples)

    events = []
    for cid, *_ in connections:
        events.append((cid, now - 29 * 86400, 1, "OK"))
    # One outage so the status page has something to show.
    events.append((connections[4][0], now - 3 * 86400, 0, "Connection refused"))
    events.append((connections[4][0], now - 3 * 86400 + 1800, 1, "OK"))
    cur.executemany(
        "INSERT INTO status_events (connection_id, ts, is_up, message) VALUES (?,?,?,?)", events
    )

    schema.commit()
    schema.close()
    return {"home": "home", "status": "status"}


def find_browser() -> str:
    for name in ("chrome", "chromium", "msedge"):
        found = shutil.which(name)
        if found:
            return found
    candidates = [
        r"C:\Program Files\Google\Chrome\Application\chrome.exe",
        r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        "/usr/bin/google-chrome",
        "/usr/bin/chromium",
    ]
    for path in candidates:
        if Path(path).exists():
            return path
    sys.exit("No Chrome or Edge found. Install one, or put it on PATH.")


def wait_for_health(timeout: int = 60) -> None:
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(f"{BASE}/healthz", timeout=2) as response:
                if response.status == 200:
                    return
        except (urllib.error.URLError, OSError):
            time.sleep(1)
    sys.exit("LabbyTwo did not become healthy in time.")


def shoot(browser: str, url: str, out: Path, height: int = VIEWPORT[1]) -> None:
    out.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as profile:
        subprocess.run(
            [
                browser,
                "--headless=new",
                "--disable-gpu",
                "--hide-scrollbars",
                "--force-device-scale-factor=2",   # crisp on a retina display
                f"--window-size={VIEWPORT[0]},{height}",
                f"--user-data-dir={profile}",
                # Blazor Server paints after the circuit connects, so give it a moment.
                "--virtual-time-budget=6000",
                f"--screenshot={out}",
                url,
            ],
            check=True,
            capture_output=True,
        )
    print(f"  {out.relative_to(ROOT)}  ({out.stat().st_size // 1024} KB)")


def main() -> None:
    data = Path(tempfile.mkdtemp(prefix="labby-shots-"))
    db = data / "shots.db"

    print("Creating the schema…")
    boot = subprocess.Popen(
        [
            "dotnet", "run", "--no-build", "--project", str(ROOT / "LabbyTwo.csproj"),
        ],
        cwd=ROOT,
        env={
            **os.environ,
            "ASPNETCORE_URLS": BASE,
            "Labby__DatabasePath": str(db),
            "Labby__PluginPath": str(data / "plugins"),
            "ASPNETCORE_ENVIRONMENT": "Development",
        },
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    try:
        wait_for_health()
    finally:
        boot.terminate()
        boot.wait(timeout=30)

    print("Seeding a demo lab…")
    seed(db)

    print("Starting LabbyTwo…")
    app = subprocess.Popen(
        ["dotnet", "run", "--no-build", "--project", str(ROOT / "LabbyTwo.csproj")],
        cwd=ROOT,
        env={
            **os.environ,
            "ASPNETCORE_URLS": BASE,
            "Labby__DatabasePath": str(db),
            "Labby__PluginPath": str(data / "plugins"),
            "ASPNETCORE_ENVIRONMENT": "Development",
            "Labby__ProbeSeconds": "5",
        },
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )

    try:
        wait_for_health()
        print("Waiting for a probe sweep…")
        time.sleep(12)
        browser = find_browser()
        print(f"Using {browser}")

        shots = [
            ("dashboard-dark.png", f"{BASE}/t/home", 1100),
            ("status.png", f"{BASE}/t/status", 1000),
            ("alerts.png", f"{BASE}/settings/alerts", 760),
            ("appearance.png", f"{BASE}/settings/appearance", 760),
            ("import.png", f"{BASE}/settings/import", 760),
            ("public-status.png", f"{BASE}/status/demo-token", 900),
            ("weather.png", f"{BASE}/t/weather", 900),
        ]
        for name, url, height in shots:
            shoot(browser, url, SHOTS / name, height)

        # Light mode, for the side-by-side in the README.
        with sqlite3.connect(db) as light:
            light.execute(
                "INSERT INTO app_settings (key, value) VALUES ('theme','light') "
                "ON CONFLICT(key) DO UPDATE SET value = excluded.value"
            )
        # The theme is read when the document is rendered, and AppSettingsStore caches it,
        # so the app has to be restarted for the change to show.
        app.terminate()
        app.wait(timeout=30)
        app = subprocess.Popen(
            ["dotnet", "run", "--no-build", "--project", str(ROOT / "LabbyTwo.csproj")],
            cwd=ROOT,
            env={
                **os.environ,
                "ASPNETCORE_URLS": BASE,
                "Labby__DatabasePath": str(db),
                "Labby__PluginPath": str(data / "plugins"),
                "ASPNETCORE_ENVIRONMENT": "Development",
                "Labby__ProbeSeconds": "5",
            },
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
        wait_for_health()
        time.sleep(12)
        shoot(browser, f"{BASE}/t/home", SHOTS / "dashboard-light.png", 1100)

    finally:
        app.terminate()
        try:
            app.wait(timeout=30)
        except subprocess.TimeoutExpired:
            app.send_signal(signal.SIGKILL)
        shutil.rmtree(data, ignore_errors=True)

    print("Done.")


if __name__ == "__main__":
    main()
