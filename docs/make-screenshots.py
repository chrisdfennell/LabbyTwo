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

import base64
import json
import math
import os
import shutil
import signal
import socket
import sqlite3
import struct
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

# Downtown Denver. Somewhere public and obviously not anybody's house — the weather
# providers below really do call Open-Meteo and the NWS, so whatever goes here ends up
# in a committed image.
LAT, LON = "39.7392", "-104.9903"
PLACE = "Denver, Colorado"


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
        # The tab kind that arranges the whole weather page for you, rather than a grid
        # somebody assembled card by card. Worth its own shot because it is the one screen
        # that shows the forecast, the warnings, air quality and the station together.
        (new_id(), "outside", "Outside", "🌤️", "weather-station", 5, 1,
         json.dumps({"hourly_hours": "12", "radar": "true", "radar_source": "rainviewer",
                     "radar_zoom": "7"})),
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
        # These three genuinely probe: Open-Meteo and the NWS need no key, so the forecast,
        # hourly strip and air-quality cards show real numbers rather than invented ones.
        # A public location on purpose — the shoot must not leak where anybody lives.
        (new_id(), "forecast", "Forecast", "🌤️",
         json.dumps({"latitude": LAT, "longitude": LON, "days": "7"})),
        (new_id(), "air-quality", "Air quality", "🌬️",
         json.dumps({"latitude": LAT, "longitude": LON, "scale": "us"})),
        (new_id(), "nws", "Weather warnings", "⚠️",
         json.dumps({"latitude": LAT, "longitude": LON, "min_severity": "all"})),
        # Somewhere for the alerts to go, so the Alerts page is not mostly a banner saying
        # there is nowhere for the alerts to go. A channel is never probed and stays out of
        # the up/down counts, and .invalid is reserved by RFC 2606 — it cannot resolve, so
        # the shoot cannot post anybody's demo alert at a real host.
        (new_id(), "webhook", "Notifications", "🔔",
         json.dumps({"url": "https://hooks.example.invalid/labbytwo"})),
    ]
    for index, (cid, provider, name, icon, settings) in enumerate(connections):
        cur.execute(
            "INSERT INTO connections (id, provider, name, icon, enabled, sort, settings, alerts) "
            "VALUES (?,?,?,?,1,?,?,1)",
            (cid, provider, name, icon, index, settings),
        )

    # Named rather than indexed from the end: three providers now sit behind the station,
    # and an off-by-one here shows up as an empty card rather than an error.
    WEATHER = connections[6][0]
    FORECAST = connections[7][0]
    AIR = connections[8][0]
    WARNINGS = connections[9][0]
    PROXMOX, TRUENAS, JELLYFIN, ADGUARD, UPS, GRAFANA = (c[0] for c in connections[:6])

    widgets = [
        (home, "greeting", "", 6, None, json.dumps({"name": "Chris", "show_date": "true"})),
        (home, "clock", "", 3, None, json.dumps({"show_date": "true"})),
        (home, "status-summary", "Services", 3, None, "{}"),
        (home, "search", "", 12, None, json.dumps({"engine": "duckduckgo"})),
        (home, "service-tile", "", 3, PROXMOX, "{}"),
        (home, "service-tile", "", 3, TRUENAS, "{}"),
        (home, "service-tile", "", 3, JELLYFIN, "{}"),
        (home, "service-tile", "", 3, ADGUARD, "{}"),
        # The row of numbers-with-a-shape: a gauge reads at a glance in a way a bare
        # figure does not, and the warning mark is the whole point of the card.
        (home, "gauge", "Pool used", 3, TRUENAS,
         json.dumps({"metric": "disk_percent", "max": "100", "warn": "80"})),
        (home, "gauge", "CPU", 3, PROXMOX,
         json.dumps({"metric": "cpu_percent", "max": "100", "warn": "75"})),
        (home, "uptime", "", 3, PROXMOX, json.dumps({"days": "30", "show_days": "true"})),
        (home, "aggregate", "", 3, None, json.dumps({
            "metric": "disk_free_gb", "aggregate": "sum", "suffix": " GB",
            "decimals": "0", "show_parts": "true",
        })),
        (home, "chart", "Response time", 8, PROXMOX,
         json.dumps({"metric": "latency_ms", "hours": "24"})),
        (home, "active-alerts", "Alerts", 4, None, "{}"),
        # Bound to nothing on purpose: one metric gathered from every connection that
        # reports it, which is the thing a per-connection chart cannot do.
        (home, "compare-chart", "CPU across the lab", 6, None,
         json.dumps({"metric": "cpu_percent", "hours": "24", "limit": "6", "height": "140"})),
        (home, "changes", "Recent changes", 6, None,
         json.dumps({"hours": "168", "limit": "6", "down_only": "false"})),
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
        (home, "action", "", 4, None, json.dumps({
            "label": "Run the nightly backup", "url": "http://198.51.100.10:5678/webhook/backup",
            "method": "POST", "ask_first": "true", "style": "secondary",
        })),
        (media, "service-tile", "", 4, JELLYFIN, "{}"),

        # The weather grid tab: the four station cards it always had, plus the four that
        # arrived with the forecast, warnings and air-quality providers.
        (weather_tab, "weather-warnings", "", 12, WARNINGS, "{}"),
        (weather_tab, "weather-today", "", 4, WEATHER, json.dumps({"units": "imperial"})),
        (weather_tab, "wind-compass", "", 3, WEATHER, json.dumps({"units": "imperial"})),
        (weather_tab, "indoor-outdoor", "", 5, WEATHER, json.dumps({"units": "imperial"})),
        (weather_tab, "forecast", "", 8, FORECAST, json.dumps({"days": "7"})),
        (weather_tab, "air-quality", "", 4, AIR, "{}"),
        (weather_tab, "forecast-hourly", "", 12, FORECAST, "{}"),
        (weather_tab, "weather", "Conditions", 5, WEATHER, json.dumps({"units": "imperial"})),
        (weather_tab, "radar", "Radar", 7, None,
         json.dumps({"source": "rainviewer", "latitude": LAT, "longitude": LON,
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

    cur.executemany(
        "INSERT INTO app_settings (key, value) VALUES (?,?) "
        "ON CONFLICT(key) DO UPDATE SET value = excluded.value",
        [
            ("public_status_token", "demo-token"),
            # One location for the whole install; the forecast, warnings, air quality and
            # radar all fall back to it. Set here so the Settings shot shows it filled in.
            ("home_lat", LAT),
            ("home_lon", LON),
            ("home_place", PLACE),
        ],
    )

    # A little history so the chart and the uptime bars are not empty.
    now = int(time.time())
    samples = []

    def series(cid: str, metric: str, base: float, swing: float = 0.35, floor: float = 3.0,
               step_minutes: int = 5) -> None:
        """A slow drift plus a deterministic wobble, so it looks measured rather than
        generated, and looks the same every time the shoot is re-run."""
        for step, minute in enumerate(range(24 * 60, 0, -step_minutes)):
            drift = math.sin(step / 26.0) * base * swing
            wobble = math.sin(step * 1.9) * base * 0.08
            samples.append((cid, metric, now - minute * 60,
                            round(max(floor, base + drift + wobble), 2)))

    # Only the six HTTP connections get invented history. The weather providers really
    # probe, so writing numbers under them would sit alongside their measured ones.
    for index, cid in enumerate((PROXMOX, TRUENAS, JELLYFIN, ADGUARD, UPS, GRAFANA)):
        series(cid, "latency_ms", 25 + index * 9)

    # CPU on four hosts, so the compare chart has something to compare. Different bases
    # and a different phase each, or the lines sit on top of one another.
    for index, cid in enumerate((PROXMOX, TRUENAS, JELLYFIN, GRAFANA)):
        series(cid, "cpu_percent", 18 + index * 11, swing=0.45, floor=1.0, step_minutes=10)

    # Under the gauge's 80% mark but close enough that the mark is doing something.
    series(TRUENAS, "disk_percent", 78.0, swing=0.03, floor=1.0, step_minutes=30)
    series(PROXMOX, "memory_percent", 61.0, swing=0.12, floor=1.0, step_minutes=30)

    # Two connections reporting it, which is what makes the total-across-connections
    # card show a total rather than a number with one thing behind it.
    series(TRUENAS, "disk_free_gb", 4120.0, swing=0.02, floor=1.0, step_minutes=60)
    series(PROXMOX, "disk_free_gb", 880.0, swing=0.04, floor=1.0, step_minutes=60)
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


def app_command() -> list[str]:
    """How to start LabbyTwo.

    Runs the built DLL rather than `dotnet run`, because `dotnet run` re-evaluates the
    project through MSBuild on every start — which on a slow machine takes longer than the
    health timeout, and this script starts the app three times. Build first:

        dotnet build

    """
    candidates = sorted(
        ROOT.glob("bin/*/net*/LabbyTwo.dll"),
        key=lambda p: p.stat().st_mtime,
        reverse=True,
    )
    if not candidates:
        sys.exit("No built LabbyTwo.dll under bin/. Run `dotnet build` first.")
    return ["dotnet", str(candidates[0])]


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


# Every card is a grey placeholder until the Blazor circuit connects and fills it in —
# see the prerender comment in GridTab.razor. Chrome's own --screenshot flag decides when
# to fire from --virtual-time-budget, which races ahead of a real websocket, so it
# captures the shimmer every time and still exits 0. Hence driving the browser properly
# and waiting for the placeholders to go.
READY = """
    document.readyState === 'complete'
    && document.querySelectorAll('.widget-skeleton').length === 0
"""


class _Ws:
    """The smallest websocket client that can carry DevTools traffic.

    Stdlib only, on purpose: this script has never needed anything installed, and a
    screenshot tool that requires a pip install is a screenshot tool nobody re-runs.
    """

    def __init__(self, url: str) -> None:
        hostport, _, path = url.split("://", 1)[1].partition("/")
        host, _, port = hostport.partition(":")
        self.sock = socket.create_connection((host, int(port)), timeout=60)
        key = base64.b64encode(os.urandom(16)).decode()
        self.sock.sendall(
            f"GET /{path} HTTP/1.1\r\nHost: {hostport}\r\nUpgrade: websocket\r\n"
            f"Connection: Upgrade\r\nSec-WebSocket-Key: {key}\r\n"
            "Sec-WebSocket-Version: 13\r\n\r\n".encode()
        )
        self._buf = b""
        while b"\r\n\r\n" not in self._buf:
            self._buf += self._must_recv()
        head, _, self._buf = self._buf.partition(b"\r\n\r\n")
        if b" 101 " not in head.split(b"\r\n")[0] + b" ":
            raise RuntimeError(f"DevTools refused the upgrade: {head.splitlines()[0]!r}")

    def _must_recv(self) -> bytes:
        chunk = self.sock.recv(1 << 16)
        if not chunk:
            raise RuntimeError("DevTools closed the connection")
        return chunk

    def _read(self, n: int) -> bytes:
        while len(self._buf) < n:
            self._buf += self._must_recv()
        out, self._buf = self._buf[:n], self._buf[n:]
        return out

    def send(self, text: str) -> None:
        payload = text.encode()
        header = bytearray([0x81])
        size = len(payload)
        if size < 126:
            header.append(0x80 | size)
        elif size < 1 << 16:
            header.append(0x80 | 126)
            header += struct.pack(">H", size)
        else:
            header.append(0x80 | 127)
            header += struct.pack(">Q", size)
        mask = os.urandom(4)
        header += mask
        self.sock.sendall(bytes(header) + bytes(b ^ mask[i % 4] for i, b in enumerate(payload)))

    def recv(self) -> str:
        while True:
            first, second = self._read(2)
            size = second & 0x7F
            if size == 126:
                size = struct.unpack(">H", self._read(2))[0]
            elif size == 127:
                size = struct.unpack(">Q", self._read(8))[0]
            data = self._read(size)
            opcode = first & 0x0F
            if opcode == 0x8:
                raise RuntimeError("DevTools closed the connection")
            if opcode in (0x1, 0x2):
                return data.decode()
            # A ping or a continuation we do not need; keep reading.

    def close(self) -> None:
        try:
            self.sock.close()
        except OSError:
            pass


class Devtools:
    """Headless Chrome, driven rather than fired-and-forgotten."""

    def __init__(self, browser: str) -> None:
        self.profile = tempfile.mkdtemp(prefix="labby-shot-profile-")
        with socket.socket() as probe:          # a port nobody else is on
            probe.bind(("127.0.0.1", 0))
            self.port = probe.getsockname()[1]
        self.proc = subprocess.Popen(
            [
                browser,
                "--headless=new",
                "--disable-gpu",
                "--hide-scrollbars",
                f"--window-size={VIEWPORT[0]},{VIEWPORT[1]}",
                f"--user-data-dir={self.profile}",
                f"--remote-debugging-port={self.port}",
                "about:blank",
            ],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
        self.ws = _Ws(self._page_target())
        self._id = 0

    def _page_target(self, timeout: int = 30) -> str:
        deadline = time.time() + timeout
        while time.time() < deadline:
            try:
                with urllib.request.urlopen(
                    f"http://127.0.0.1:{self.port}/json/list", timeout=2
                ) as response:
                    for target in json.load(response):
                        if target.get("type") == "page" and target.get("webSocketDebuggerUrl"):
                            return target["webSocketDebuggerUrl"]
            except (urllib.error.URLError, OSError, json.JSONDecodeError):
                pass
            time.sleep(0.5)
        sys.exit("Chrome never opened a DevTools page target.")

    def call(self, method: str, **params) -> dict:
        self._id += 1
        self.ws.send(json.dumps({"id": self._id, "method": method, "params": params}))
        while True:
            message = json.loads(self.ws.recv())
            if message.get("id") != self._id:
                continue                        # an event; nothing here subscribes to any
            if "error" in message:
                raise RuntimeError(f"{method}: {message['error']}")
            return message.get("result", {})

    def shoot(self, url: str, out: Path, settle: float = 2.5, timeout: int = 60) -> None:
        out.parent.mkdir(parents=True, exist_ok=True)
        # Twice, on purpose. The first pass is only to find out how tall the page is; the
        # viewport is then grown to fit the whole thing and the page loaded again, so
        # everything renders once at its final size.
        #
        # Both halves of that matter. An iframe below the fold — the radar, on a long page
        # — is lazy and never starts loading while it is off screen, so capturing beyond
        # the viewport alone leaves a blank box where the map should be. And resizing after
        # the map has drawn leaves it half tiled, because the embed does not re-tile on a
        # resize. Load it at the final size and neither happens.
        self.call("Page.navigate", url=url)
        ready = self._settle(timeout, min(settle, 3))
        self.call(
            "Emulation.setDeviceMetricsOverride",
            width=VIEWPORT[0], height=int(self._content()["height"]) + 200,
            deviceScaleFactor=1, mobile=False,
        )
        self.call("Page.reload")
        ready = self._settle(timeout, settle) and ready

        # The whole page, rather than a height guessed per shot and re-guessed every time
        # a card is added.
        size = self._content()
        shot = self.call(
            "Page.captureScreenshot",
            format="png",
            captureBeyondViewport=True,
            clip={
                "x": 0, "y": 0,
                "width": size["width"],
                "height": size["height"],
                "scale": 2,                     # crisp on a retina display
            },
        )
        self.call("Emulation.clearDeviceMetricsOverride")
        out.write_bytes(base64.b64decode(shot["data"]))
        note = "" if ready else "  [!] still had placeholders on it"
        print(f"  {out.relative_to(ROOT)}  ({out.stat().st_size // 1024} KB){note}")

    def _settle(self, timeout: int, pause: float) -> bool:
        """Wait for the placeholders to go, then let the slow tiles land."""
        deadline = time.time() + timeout
        ready = False
        while time.time() < deadline:
            result = self.call("Runtime.evaluate", expression=READY, returnByValue=True)
            if result.get("result", {}).get("value"):
                ready = True
                break
            time.sleep(0.5)
        time.sleep(pause)
        return ready

    def _content(self) -> dict:
        metrics = self.call("Page.getLayoutMetrics")
        return metrics.get("cssContentSize") or metrics["contentSize"]

    def close(self) -> None:
        self.ws.close()
        self.proc.terminate()
        try:
            self.proc.wait(timeout=20)
        except subprocess.TimeoutExpired:
            self.proc.kill()
        shutil.rmtree(self.profile, ignore_errors=True)


def main() -> None:
    data = Path(tempfile.mkdtemp(prefix="labby-shots-"))
    db = data / "shots.db"

    launch = app_command()

    print("Creating the schema…")
    boot = subprocess.Popen(
        launch,
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
        launch,
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

        # The radar is somebody else's map in an iframe and takes far longer to draw than
        # anything LabbyTwo renders itself, so the two pages carrying one wait longer.
        # Without this they capture "Fetching map data…", or an empty grey box.
        shots = [
            ("dashboard-dark.png", f"{BASE}/t/home", 3),
            ("status.png", f"{BASE}/t/status", 3),
            ("alerts.png", f"{BASE}/settings/alerts", 3),
            ("appearance.png", f"{BASE}/settings/appearance", 2),
            ("import.png", f"{BASE}/settings/import", 2),
            ("public-status.png", f"{BASE}/status/demo-token", 3),
            ("weather.png", f"{BASE}/t/weather", 20),
            # The weather-station tab kind: the whole page, arranged rather than assembled.
            ("weather-station.png", f"{BASE}/t/outside", 20),
        ]
        chrome = Devtools(browser)
        try:
            for name, url, settle in shots:
                chrome.shoot(url, SHOTS / name, settle=settle)
        finally:
            chrome.close()

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
            launch,
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
        chrome = Devtools(browser)
        try:
            chrome.shoot(f"{BASE}/t/home", SHOTS / "dashboard-light.png", settle=3)
        finally:
            chrome.close()

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
