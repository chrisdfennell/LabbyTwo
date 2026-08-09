#!/usr/bin/env bash
#
# Install or update LabbyTwo.
#
#   curl -fsSL https://raw.githubusercontent.com/chrisdfennell/LabbyTwo/main/install.sh -o install.sh
#   less install.sh          # you are piping a stranger's script into a shell; read it first
#   bash install.sh
#
# Run it again later and it updates in place: it pulls, rebuilds and restarts, and never
# touches your .env or the data volume.
#
#   LABBY_DIR=/opt/labbytwo  where to keep the checkout   (default ~/labbytwo)
#   LABBY_PORT=5150          port to serve on             (default 5150)
#   LABBY_BRANCH=main        branch to track              (default main)

set -euo pipefail

REPO_URL="${LABBY_REPO:-https://github.com/chrisdfennell/LabbyTwo.git}"
DIR="${LABBY_DIR:-$HOME/labbytwo}"
BRANCH="${LABBY_BRANCH:-main}"
PORT="${LABBY_PORT:-5150}"

# Colour only when this is a terminal, so piping to a file or a log stays readable.
if [ -t 1 ]; then
    BOLD=$'\033[1m'; DIM=$'\033[2m'; RED=$'\033[31m'; GREEN=$'\033[32m'; YELLOW=$'\033[33m'; OFF=$'\033[0m'
else
    BOLD=''; DIM=''; RED=''; GREEN=''; YELLOW=''; OFF=''
fi

say()  { printf '%s\n' "${BOLD}==>${OFF} $*"; }
note() { printf '%s\n' "    ${DIM}$*${OFF}"; }
warn() { printf '%s\n' "${YELLOW}!${OFF}   $*"; }
die()  { printf '%s\n' "${RED}✗${OFF}   $*" >&2; exit 1; }

# ---- what we need before we start -------------------------------------------------

command -v git >/dev/null 2>&1 || die "git is not installed. Install it and run this again."
command -v docker >/dev/null 2>&1 || die "docker is not installed. See https://docs.docker.com/engine/install/"

if ! docker info >/dev/null 2>&1; then
    die "Docker is installed but not responding.
    Start Docker Desktop, or: sudo systemctl start docker
    On Linux, if it needs sudo: sudo usermod -aG docker \$USER, then log out and back in."
fi

# Compose v2 is a docker subcommand; v1 was a separate binary and is long dead.
if docker compose version >/dev/null 2>&1; then
    COMPOSE=(docker compose)
elif command -v docker-compose >/dev/null 2>&1; then
    warn "Using the old docker-compose v1. It works, but v2 is what this is tested against."
    COMPOSE=(docker-compose)
else
    die "Docker Compose is missing. Install the compose plugin: https://docs.docker.com/compose/install/"
fi

# ---- get the source ---------------------------------------------------------------

if [ -d "$DIR/.git" ]; then
    say "Updating the checkout in $DIR"
    git -C "$DIR" remote set-url origin "$REPO_URL"
    git -C "$DIR" fetch --quiet origin "$BRANCH"

    # Refuse rather than clobber: someone may be running a patched copy on purpose.
    if ! git -C "$DIR" diff --quiet || ! git -C "$DIR" diff --cached --quiet; then
        die "$DIR has uncommitted changes. Commit, stash or discard them, then run this again."
    fi

    git -C "$DIR" checkout --quiet "$BRANCH"
    git -C "$DIR" merge --quiet --ff-only "origin/$BRANCH" \
        || die "$DIR has local commits that are not on origin/$BRANCH. Sort that out and re-run."
    note "now at $(git -C "$DIR" log --oneline -1)"
elif [ -e "$DIR" ]; then
    die "$DIR already exists and is not a git checkout. Move it, or set LABBY_DIR to somewhere else."
else
    say "Cloning into $DIR"
    git clone --quiet --branch "$BRANCH" "$REPO_URL" "$DIR"
fi

cd "$DIR"

# ---- configuration ----------------------------------------------------------------

if [ -f .env ]; then
    say "Keeping the .env you already have"
    # A pre-existing .env wins, so an update never silently moves the port.
    PORT="$(grep -E '^LABBY_PORT=' .env | tail -1 | cut -d= -f2- | tr -d '"'"'"' \r' || true)"
    PORT="${PORT:-5150}"
else
    say "Writing .env"
    cp .env.example .env

    # The container shows every timestamp in this zone; UTC on a home dashboard is
    # a small daily annoyance, so take the host's zone if it is discoverable.
    HOST_TZ=""
    if [ -f /etc/timezone ]; then
        HOST_TZ="$(cat /etc/timezone)"
    elif [ -L /etc/localtime ]; then
        HOST_TZ="$(readlink /etc/localtime | sed 's|.*/zoneinfo/||')"
    fi
    if [ -n "$HOST_TZ" ]; then
        sed -i.bak "s|^TZ=.*|TZ=$HOST_TZ|" .env && rm -f .env.bak
        note "timezone set to $HOST_TZ"
    else
        note "could not detect a timezone; leaving the default. Edit TZ in .env if times look wrong."
    fi

    sed -i.bak "s|^LABBY_PORT=.*|LABBY_PORT=$PORT|" .env && rm -f .env.bak
fi

# ---- is the port free? ------------------------------------------------------------

# Three tools because no single one is everywhere: ss on modern Linux, lsof on macOS,
# netstat in Git Bash and on older boxes. If none is present we say so rather than
# quietly skipping the check.
port_in_use() {
    if command -v ss >/dev/null 2>&1; then
        ss -ltn 2>/dev/null | grep -qE "[:.]$1[[:space:]]"
    elif command -v lsof >/dev/null 2>&1; then
        lsof -iTCP:"$1" -sTCP:LISTEN >/dev/null 2>&1
    elif command -v netstat >/dev/null 2>&1; then
        netstat -an 2>/dev/null | grep -iE "listen" | grep -qE "[:.]$1[[:space:]]"
    else
        CANNOT_CHECK_PORT=1
        return 1
    fi
}

# Our own container holding the port is not a conflict, it is the thing being updated.
OURS="$("${COMPOSE[@]}" ps -q 2>/dev/null | head -1 || true)"
if [ -z "$OURS" ] && port_in_use "$PORT"; then
    die "Port $PORT is already in use by something else.
    Set a different one and re-run:  LABBY_PORT=5151 bash install.sh
    (or edit LABBY_PORT in $DIR/.env)"
fi

if [ "${CANNOT_CHECK_PORT:-0}" = "1" ]; then
    note "no ss, lsof or netstat here, so the port was not checked — Docker will complain if $PORT is taken"
fi

# ---- build and start --------------------------------------------------------------

say "Building the image — first run takes a few minutes, longer on a Raspberry Pi"
"${COMPOSE[@]}" build

say "Starting LabbyTwo"
"${COMPOSE[@]}" up -d

# ---- wait until it actually answers -----------------------------------------------

say "Waiting for it to come up"
URL="http://localhost:$PORT"
for _ in $(seq 1 60); do
    if curl -fsS "$URL/healthz" >/dev/null 2>&1; then
        READY=1
        break
    fi
    sleep 2
done

if [ "${READY:-0}" != "1" ]; then
    warn "It did not answer on $URL within two minutes."
    note "Logs:    cd $DIR && ${COMPOSE[*]} logs --tail=50"
    note "Status:  cd $DIR && ${COMPOSE[*]} ps"
    exit 1
fi

printf '\n%s\n\n' "${GREEN}LabbyTwo is running at ${BOLD}$URL${OFF}"
note "Open it and click “Create a starter dashboard”."
note "Already use Homer, Homepage or Heimdall? Settings → Import a dashboard."
printf '\n'
note "Update:  bash $DIR/install.sh"
note "Logs:    cd $DIR && ${COMPOSE[*]} logs -f"
note "Stop:    cd $DIR && ${COMPOSE[*]} down            (keeps your data)"
note "Erase:   cd $DIR && ${COMPOSE[*]} down -v         (deletes everything)"
printf '\n'

# Login is off by default. Say so plainly rather than leaving it to be discovered.
if ! grep -qE '^LABBY_AUTH_PASSWORD=.+' .env; then
    warn "There is no login. Anyone who can reach $URL can use it, and LabbyTwo can hold"
    note "credentials for your NAS. That is fine on a trusted LAN and not fine anywhere else."
    note "To turn it on: set LABBY_AUTH_PASSWORD in $DIR/.env, then re-run this script."
fi
