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
#   LABBY_DIR=/opt/labbytwo  where to keep the checkout   (skips the prompt)
#   LABBY_PORT=5150          port to serve on             (default 5150)
#   LABBY_BRANCH=main        branch to track              (default main)

set -euo pipefail

REPO_URL="${LABBY_REPO:-https://github.com/chrisdfennell/LabbyTwo.git}"
DEFAULT_DIR="$HOME/labbytwo"
DIR="${LABBY_DIR:-}"
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

command -v docker >/dev/null 2>&1 || die "docker is not installed. See https://docs.docker.com/engine/install/"

# git is preferred but not required. NAS firmware — QNAP Container Station, Synology —
# ships Docker and no git at all, and that is a large part of who runs this. Without git
# we fetch a tarball instead; updates still work, they just replace the source outright.
HAVE_GIT=0
command -v git >/dev/null 2>&1 && HAVE_GIT=1

# One downloader, whichever exists. BusyBox wget takes different flags from GNU wget,
# so the calls below stick to what both understand.
DOWNLOADER=""
if command -v curl >/dev/null 2>&1; then
    DOWNLOADER="curl"
elif command -v wget >/dev/null 2>&1; then
    DOWNLOADER="wget"
fi

fetch_to() {   # fetch_to <url> <destination file>
    case "$DOWNLOADER" in
        curl) curl -fsSL "$1" -o "$2" ;;
        wget) wget -q -O "$2" "$1" ;;
        *)    return 1 ;;
    esac
}

# The tip of the branch on GitHub, for stamping a tarball install. Best effort: a failure
# here only costs the update check, so it must not stop an install.
api_commit_sha() {
    local owner_repo json sha
    owner_repo="$(printf '%s' "${REPO_URL%.git}" | sed 's|.*github.com[:/]||')"
    json="$(fetch_stdout "https://api.github.com/repos/$owner_repo/commits/$BRANCH" || true)"
    sha="$(printf '%s' "$json" | sed -n 's/.*"sha"[[:space:]]*:[[:space:]]*"\([0-9a-f]\{12\}\).*//p' | head -1)"
    printf '%s' "${sha:-dev}"
}

fetch_stdout() {   # fetch_stdout <url>
    case "$DOWNLOADER" in
        curl) curl -fsSL -H "Accept: application/vnd.github+json" "$1" ;;
        wget) wget -q -O - --header="Accept: application/vnd.github+json" "$1" ;;
        *)    return 1 ;;
    esac
}

fetch_quiet() {   # fetch_quiet <url> — for the health check; success is all we need
    case "$DOWNLOADER" in
        curl) curl -fsS "$1" >/dev/null 2>&1 ;;
        wget) wget -q -O - "$1" >/dev/null 2>&1 ;;
        *)    return 1 ;;
    esac
}

if [ "$HAVE_GIT" = "0" ]; then
    [ -n "$DOWNLOADER" ] || die "Neither git nor curl nor wget is installed, so there is no way to fetch the source.
    Install any one of them and run this again."
    command -v tar >/dev/null 2>&1 || die "git is not installed, so the source has to come as a tarball — but tar is missing too.
    Install git or tar and run this again."
fi

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

# ---- where should it live? ---------------------------------------------------------

# /dev/tty can exist and still refuse to open — Git Bash under a pipe does exactly that,
# and `read < /dev/tty` then fails mid-prompt. Test the open, not the file.
can_prompt() { { : < /dev/tty; } 2>/dev/null; }

# Accepts ~, a relative path, a Windows drive path, or something pasted with quotes.
normalise_dir() {
    local d="$1"
    # Strip quotes someone pasted round the path, and any trailing slash.
    d="${d#\"}"; d="${d%\"}"
    d="${d#\'}"; d="${d%\'}"
    d="${d%/}"

    # A drive-letter path is absolute even though it does not start with "/". Matched
    # with [[ =~ ]] rather than a case glob, because a backslash inside a bracket
    # expression is an escape and [/\] silently matches neither.
    if [[ "$d" =~ ^[A-Za-z]:[/\\] ]]; then
        if command -v cygpath >/dev/null 2>&1; then
            d="$(cygpath -u "$d")"
        else
            local drive rest
            drive="$(printf '%s' "${d:0:1}" | tr '[:upper:]' '[:lower:]')"
            rest="$(printf '%s' "${d:2}" | tr '\\' '/')"
            d="/$drive$rest"
        fi
    else
        case "$d" in
            "~")   d="$HOME" ;;
            "~/"*) d="$HOME/${d#\~/}" ;;
            /*|"") ;;
            *)     d="$PWD/$d" ;;
        esac
    fi

    printf '%s' "${d%/}"
}

# Prints why not and returns 1, so the prompt can ask again instead of giving up.
check_dir() {
    local d="$1" origin parent
    [ -z "$d" ] && { note "Give me a path."; return 1; }

    # An existing LabbyTwo lives here — checked by what is in it, not by the remote URL,
    # so a fork, a mirror, or a tarball install with no .git at all is recognised.
    if [ -f "$d/LabbyTwo.csproj" ] && [ -f "$d/docker-compose.yml" ]; then
        return 0
    fi

    if [ -d "$d/.git" ]; then
        origin="$(git -C "$d" remote get-url origin 2>/dev/null || echo "an unknown remote")"
        note "$d is a git checkout of $origin, not LabbyTwo."
        return 1
    fi

    if [ -e "$d" ]; then
        [ -d "$d" ] || { note "$d is a file, not a directory."; return 1; }

        # People download install.sh into the directory they mean to install into and
        # then answer the prompt with it. Refusing that would be pedantic.
        local leftovers
        leftovers="$(ls -A "$d" 2>/dev/null | grep -vx -e install.sh -e install.ps1 -e .env || true)"
        if [ -n "$leftovers" ]; then
            note "$d already exists and has things in it. Pick an empty or a new directory."
            return 1
        fi
        return 0
    fi

    # Does not exist yet — walk up to the nearest parent that does and see if we may write.
    parent="$(dirname "$d")"
    while [ ! -e "$parent" ] && [ "$parent" != "/" ] && [ "$parent" != "." ]; do
        parent="$(dirname "$parent")"
    done
    if [ ! -w "$parent" ]; then
        note "Cannot write to $parent. Try a path under \$HOME, or re-run with sudo."
        return 1
    fi
    return 0
}

if [ -n "$DIR" ]; then
    DIR="$(normalise_dir "$DIR")"
    check_dir "$DIR" || die "LABBY_DIR=$DIR will not work."
elif can_prompt; then
    # Read from the terminal, not stdin: if this script was piped into bash, stdin is
    # the rest of the script and a bare `read` would eat it.
    say "Where should LabbyTwo live?"
    note "It keeps the source here. Your dashboard and credentials live in a Docker volume,"
    note "not in this directory, so it is safe to put on any disk."
    while true; do
        printf '    %s[%s]%s ' "$DIM" "$DEFAULT_DIR" "$OFF" > /dev/tty
        REPLY_DIR=""
        if ! read -r REPLY_DIR < /dev/tty; then
            # End of input — they pressed Ctrl-D rather than choosing. Installing to the
            # default at that point would put it somewhere they never agreed to.
            echo
            die "No directory chosen. Re-run and answer the prompt, or set LABBY_DIR."
        fi
        [ -z "$REPLY_DIR" ] && REPLY_DIR="$DEFAULT_DIR"
        DIR="$(normalise_dir "$REPLY_DIR")"
        check_dir "$DIR" && break
    done
    echo
else
    # No terminal — a pipe, a cron job, CI. Take the default rather than hanging.
    DIR="$DEFAULT_DIR"
    note "Nothing to prompt on, so using $DIR. Set LABBY_DIR to choose."
    check_dir "$DIR" || die "$DIR will not work as an install directory."
fi

# ---- get the source ---------------------------------------------------------------

# Downloads and unpacks the branch tarball over $DIR, for hosts with no git. GitHub wraps
# the archive in a single top-level directory, hence --strip-components=1.
install_from_tarball() {
    local url tmp
    # Derive the archive URL from the clone URL so LABBY_REPO still works for forks.
    url="${REPO_URL%.git}/archive/refs/heads/$BRANCH.tar.gz"
    tmp="$(mktemp -d 2>/dev/null || printf '%s' "${TMPDIR:-/tmp}/labbytwo.$$")"
    mkdir -p "$tmp"
    # shellcheck disable=SC2064
    trap "rm -rf '$tmp'" EXIT

    note "no git here, so fetching the source as a tarball"
    fetch_to "$url" "$tmp/src.tar.gz" || die "Could not download $url"
    tar -xzf "$tmp/src.tar.gz" -C "$tmp" || die "Could not unpack the download. It may be truncated — try again."

    local root
    root="$(find "$tmp" -mindepth 1 -maxdepth 1 -type d | head -1)"
    [ -n "$root" ] || die "The archive did not contain what was expected."
    [ -f "$root/LabbyTwo.csproj" ] || die "That archive does not look like LabbyTwo."

    mkdir -p "$DIR"
    # Copy contents, not the directory. .env is not in the archive, so an existing one
    # here is left exactly where it is.
    cp -R "$root/." "$DIR/"
    rm -rf "$tmp"
    trap - EXIT
}

if [ "$HAVE_GIT" = "1" ] && [ -d "$DIR/.git" ]; then
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
elif [ "$HAVE_GIT" = "1" ] && [ ! -f "$DIR/LabbyTwo.csproj" ]; then
    say "Cloning into $DIR"
    git clone --quiet --branch "$BRANCH" "$REPO_URL" "$DIR"
else
    # Either there is no git, or there is a tarball install already here to refresh.
    if [ -f "$DIR/LabbyTwo.csproj" ]; then
        say "Updating the source in $DIR"
        note "any edits you made to the source will be overwritten; .env and your data are untouched"
    else
        say "Downloading into $DIR"
    fi
    install_from_tarball
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
    # Not `sed -i`: BusyBox's version takes no suffix, so `sed -i.bak` silently creates a
    # file named ".bak" on a NAS instead of editing in place.
    set_env() {
        sed "s|^$1=.*|$1=$2|" .env > .env.tmp && mv .env.tmp .env
    }

    if [ -n "$HOST_TZ" ]; then
        set_env TZ "$HOST_TZ"
        note "timezone set to $HOST_TZ"
    else
        note "could not detect a timezone; leaving the default. Edit TZ in .env if times look wrong."
    fi

    set_env LABBY_PORT "$PORT"
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

# Stamp the build with the commit it came from, so Settings can say whether this install
# is behind. Without it every build calls itself "dev" and can compare against nothing.
if [ "$HAVE_GIT" = "1" ] && [ -d "$DIR/.git" ]; then
    LABBYTWO_VERSION="$(git -C "$DIR" rev-parse --short=12 HEAD 2>/dev/null || echo dev)"
else
    # A tarball carries no history, so ask the API what the tip of the branch is. It was
    # downloaded moments ago, so that is what this is.
    LABBYTWO_VERSION="$(api_commit_sha)"
fi
export LABBYTWO_VERSION
note "building $LABBYTWO_VERSION"

say "Building the image — first run takes a few minutes, longer on a Raspberry Pi"
"${COMPOSE[@]}" build

say "Starting LabbyTwo"
"${COMPOSE[@]}" up -d

# ---- wait until it actually answers -----------------------------------------------

say "Waiting for it to come up"
URL="http://localhost:$PORT"
for _ in $(seq 1 60); do
    if fetch_quiet "$URL/healthz"; then
        READY=1
        break
    fi
    sleep 2
done

if [ "${READY:-0}" != "1" ] && [ -z "$DOWNLOADER" ]; then
    warn "Started, but with no curl or wget here I cannot check that it answered."
    note "Try $URL in a browser."
elif [ "${READY:-0}" != "1" ]; then
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
