<#
.SYNOPSIS
    Install or update LabbyTwo on Windows with Docker Desktop.

.DESCRIPTION
    Run it again later and it updates in place: pulls, rebuilds and restarts, and never
    touches your .env or the data volume.

.EXAMPLE
    # Download and read it before running — it is somebody else's script.
    irm https://raw.githubusercontent.com/chrisdfennell/LabbyTwo/main/install.ps1 -OutFile install.ps1
    notepad install.ps1
    .\install.ps1

.EXAMPLE
    .\install.ps1 -Port 5151 -Dir D:\labbytwo
#>
[CmdletBinding()]
param(
    # Left empty so we can tell "they chose the default" from "they said nothing" and
    # know whether to ask.
    [string]$Dir    = '',
    [int]$Port      = 5150,
    [string]$Branch = 'main',
    [string]$Repo   = 'https://github.com/chrisdfennell/LabbyTwo.git'
)

$ErrorActionPreference = 'Stop'
$DefaultDir = Join-Path $HOME 'labbytwo'

function Say  { param($m) Write-Host "==> $m" -ForegroundColor White }
function Note { param($m) Write-Host "    $m" -ForegroundColor DarkGray }
function Warn { param($m) Write-Host "!   $m" -ForegroundColor Yellow }
function Die  { param($m) Write-Host "x   $m" -ForegroundColor Red; exit 1 }

# ---- what we need before we start -------------------------------------------------

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Die "git is not installed. winget install Git.Git"
}
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Die "docker is not installed. Get Docker Desktop: https://docs.docker.com/desktop/install/windows-install/"
}

docker info *>$null
if ($LASTEXITCODE -ne 0) {
    Die "Docker is installed but not responding. Start Docker Desktop and wait for the whale to settle."
}

docker compose version *>$null
if ($LASTEXITCODE -ne 0) {
    Die "Docker Compose v2 is missing. It ships with current Docker Desktop — update it."
}

# ---- where should it live? ---------------------------------------------------------

function Resolve-InstallDir {
    param([string]$Path)
    $sep = [char]0x5C   # a backslash, spelled out so no editor or tool can eat it
    $p = $Path.Trim().Trim('"', "'").TrimEnd($sep, '/')
    if ($p -eq '~')        { return $HOME }
    if ($p.StartsWith('~')) { return (Join-Path $HOME $p.Substring(1).TrimStart($sep, '/')) }
    if ([System.IO.Path]::IsPathRooted($p)) { return $p }
    if ($p) { return (Join-Path (Get-Location) $p) }
    return $p
}

# Returns $null when usable, otherwise the reason — so the prompt can ask again.
function Test-InstallDir {
    param([string]$Path)
    if (-not $Path) { return 'Give me a path.' }

    if (Test-Path (Join-Path $Path '.git')) {
        # Identified by what is in it, not by the remote URL: a fork or a mirror is
        # still LabbyTwo and its URL need not say so.
        if ((Test-Path (Join-Path $Path 'LabbyTwo.csproj')) -and
            (Test-Path (Join-Path $Path 'docker-compose.yml'))) { return $null }
        return "$Path is a git checkout of something else, not LabbyTwo."
    }

    if (Test-Path $Path) {
        if (-not (Test-Path $Path -PathType Container)) { return "$Path is a file, not a directory." }
        if (Get-ChildItem -Force $Path -ErrorAction SilentlyContinue) {
            return "$Path already exists and has things in it. Pick an empty or a new directory."
        }
        return $null
    }

    # Walk up to the nearest parent that exists and see whether we may create there.
    $parent = Split-Path $Path -Parent
    while ($parent -and -not (Test-Path $parent)) { $parent = Split-Path $parent -Parent }
    if (-not $parent) { return "Cannot work out where $Path would go." }
    try {
        $probe = Join-Path $parent ([System.IO.Path]::GetRandomFileName())
        New-Item -ItemType Directory -Path $probe -ErrorAction Stop | Out-Null
        Remove-Item $probe -Force
    } catch {
        return "Cannot write to $parent. Pick somewhere under your home folder."
    }
    return $null
}

if ($Dir) {
    $Dir = Resolve-InstallDir $Dir
    $why = Test-InstallDir $Dir
    if ($why) { Die "-Dir $Dir will not work.`n    $why" }
}
elseif ([Environment]::UserInteractive -and -not [Console]::IsInputRedirected) {
    Say 'Where should LabbyTwo live?'
    Note 'It keeps the source here. Your dashboard and credentials live in a Docker volume,'
    Note 'not in this directory, so it is safe to put on any disk.'
    while ($true) {
        $answer = Read-Host "    Path [$DefaultDir]"
        if (-not $answer) { $answer = $DefaultDir }
        $candidate = Resolve-InstallDir $answer
        $why = Test-InstallDir $candidate
        if (-not $why) { $Dir = $candidate; break }
        Note $why
    }
    Write-Host ''
}
else {
    # Nothing to prompt on — a pipe, a scheduled task, CI. Take the default, do not hang.
    $Dir = $DefaultDir
    Note "Nothing to prompt on, so using $Dir. Pass -Dir to choose."
    $why = Test-InstallDir $Dir
    if ($why) { Die $why }
}

# ---- get the source ---------------------------------------------------------------

if (Test-Path (Join-Path $Dir '.git')) {
    Say "Updating the checkout in $Dir"
    git -C $Dir remote set-url origin $Repo
    git -C $Dir fetch --quiet origin $Branch
    if ($LASTEXITCODE -ne 0) { Die "Could not reach $Repo." }

    # Refuse rather than clobber: someone may be running a patched copy on purpose.
    git -C $Dir diff --quiet
    $dirty = $LASTEXITCODE -ne 0
    git -C $Dir diff --cached --quiet
    if ($dirty -or $LASTEXITCODE -ne 0) {
        Die "$Dir has uncommitted changes. Commit, stash or discard them, then run this again."
    }

    git -C $Dir checkout --quiet $Branch
    git -C $Dir merge --quiet --ff-only "origin/$Branch"
    if ($LASTEXITCODE -ne 0) {
        Die "$Dir has local commits that are not on origin/$Branch. Sort that out and re-run."
    }
    Note (git -C $Dir log --oneline -1)
}
else {
    Say "Cloning into $Dir"
    git clone --quiet --branch $Branch $Repo $Dir
    if ($LASTEXITCODE -ne 0) { Die "Clone failed." }
}

Set-Location $Dir

# ---- configuration ----------------------------------------------------------------

$envFile = Join-Path $Dir '.env'
if (Test-Path $envFile) {
    Say "Keeping the .env you already have"
    # A pre-existing .env wins, so an update never silently moves the port.
    $existing = Select-String -Path $envFile -Pattern '^LABBY_PORT=(.+)$' | Select-Object -Last 1
    if ($existing) { $Port = [int]$existing.Matches[0].Groups[1].Value.Trim().Trim('"',"'") }
}
else {
    Say "Writing .env"
    Copy-Item .env.example $envFile

    # The container shows every timestamp in this zone; UTC on a home dashboard is a
    # small daily annoyance. Windows uses its own zone names, so map to the IANA one.
    $tz = ''
    try { $tz = (Get-TimeZone).Id } catch { }
    $ianaMap = @{
        'Eastern Standard Time'  = 'America/New_York'
        'Central Standard Time'  = 'America/Chicago'
        'Mountain Standard Time' = 'America/Denver'
        'Pacific Standard Time'  = 'America/Los_Angeles'
        'GMT Standard Time'      = 'Europe/London'
        'W. Europe Standard Time'= 'Europe/Berlin'
        'Romance Standard Time'  = 'Europe/Paris'
        'AUS Eastern Standard Time' = 'Australia/Sydney'
    }
    $iana = $ianaMap[$tz]
    $content = Get-Content $envFile
    if ($iana) {
        $content = $content -replace '^TZ=.*', "TZ=$iana"
        Note "timezone set to $iana"
    }
    else {
        Note "Could not map the Windows timezone '$tz' to an IANA name. Edit TZ in .env if times look wrong."
    }
    ($content -replace '^LABBY_PORT=.*', "LABBY_PORT=$Port") | Set-Content $envFile -Encoding utf8
}

# ---- is the port free? ------------------------------------------------------------

# Our own container holding the port is not a conflict, it is the thing being updated.
$ours = (docker compose ps -q 2>$null | Select-Object -First 1)
if (-not $ours) {
    $busy = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
    if ($busy) {
        Die "Port $Port is already in use by something else.
    Pick another and re-run:  .\install.ps1 -Port 5151
    (or edit LABBY_PORT in $envFile)"
    }
}

# ---- build and start --------------------------------------------------------------

# Stamp the build with its commit so Settings can tell whether this install is behind.
$version = (git -C $Dir rev-parse --short=12 HEAD 2>$null)
if ($LASTEXITCODE -ne 0 -or -not $version) { $version = 'dev' }
$env:LABBYTWO_VERSION = $version.Trim()
Note "building $($env:LABBYTWO_VERSION)"

Say "Building the image — the first run takes a few minutes"
docker compose build
if ($LASTEXITCODE -ne 0) { Die "The build failed. The output above says why." }

Say "Starting LabbyTwo"
docker compose up -d
if ($LASTEXITCODE -ne 0) { Die "Compose could not start it. The output above says why." }

# ---- wait until it actually answers -----------------------------------------------

Say "Waiting for it to come up"
$url = "http://localhost:$Port"
$ready = $false
foreach ($i in 1..60) {
    try {
        Invoke-WebRequest "$url/healthz" -UseBasicParsing -TimeoutSec 3 | Out-Null
        $ready = $true
        break
    } catch { Start-Sleep -Seconds 2 }
}

if (-not $ready) {
    Warn "It did not answer on $url within two minutes."
    Note "Logs:    cd $Dir; docker compose logs --tail=50"
    Note "Status:  cd $Dir; docker compose ps"
    exit 1
}

Write-Host ''
Write-Host "LabbyTwo is running at $url" -ForegroundColor Green
Write-Host ''
Note 'Open it and click "Create a starter dashboard".'
Note 'Already use Homer, Homepage or Heimdall? Settings -> Import a dashboard.'
Write-Host ''
Note "Update:  .\install.ps1"
Note "Logs:    cd $Dir; docker compose logs -f"
Note "Stop:    cd $Dir; docker compose down          (keeps your data)"
Note "Erase:   cd $Dir; docker compose down -v       (deletes everything)"
Write-Host ''

# Login is off by default. Say so plainly rather than leaving it to be discovered.
if (-not (Select-String -Path $envFile -Pattern '^LABBY_AUTH_PASSWORD=.+' -Quiet)) {
    Warn "There is no login. Anyone who can reach $url can use it, and LabbyTwo can hold"
    Note "credentials for your NAS. Fine on a trusted LAN, not fine anywhere else."
    Note "To turn it on: set LABBY_AUTH_PASSWORD in $envFile, then re-run this script."
}
