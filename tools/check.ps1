<#
.SYNOPSIS
    The local gate for v8. Runs what CI would run, on whatever machine you are on.

.DESCRIPTION
    v8 does not run CI (decided 2026-10-29): the milestone branch is not in any
    workflow's `branches:` list, so nothing is verified unless you run it. This
    script is the replacement.

    Three stages, cheapest first:
      1. build  - catches the whole class of errors a compiler knows about.
      2. test   - L1 contract tests + architecture guards (seconds).
      3. e2e    - real FFmpeg encodes (minutes), only with -Full.

    Run it before every commit, and with -Full before ending the day. A refactor
    that drifts for two weeks without the matrix is a refactor whose failures can
    no longer be attributed to a single change.

.PARAMETER Full
    Also run the end-to-end suite. Needs FFmpeg and takes minutes.

.PARAMETER Configuration
    Build configuration. Release by default, because that is what ships.

.EXAMPLE
    powershell -NoProfile -File tools\check.ps1

.EXAMPLE
    powershell -NoProfile -File tools\check.ps1 -Full
#>
[CmdletBinding()]
param(
    [switch]$Full,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$solution = "LiveArchiveEncoder.V8.slnx"
$failures = @()
$total = [Diagnostics.Stopwatch]::StartNew()

function Write-Stage([string]$Text) {
    Write-Host ""
    Write-Host "=== $Text ===" -ForegroundColor Cyan
}

# --- 1. build -------------------------------------------------------------
Write-Stage "1/3 build ($Configuration)"
$sw = [Diagnostics.Stopwatch]::StartNew()
dotnet build $solution -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "BUILD FAILED - nothing else can be trusted" -ForegroundColor Red
    exit 1
}
Write-Host ("build ok ({0:N1}s)" -f $sw.Elapsed.TotalSeconds) -ForegroundColor Green

# --- 2. tests -------------------------------------------------------------
Write-Stage "2/3 tests"
$sw = [Diagnostics.Stopwatch]::StartNew()
dotnet test --solution $solution -c $Configuration --no-build --nologo
if ($LASTEXITCODE -ne 0) {
    $failures += "tests"
    Write-Host "TESTS FAILED" -ForegroundColor Red
} else {
    Write-Host ("tests ok ({0:N1}s)" -f $sw.Elapsed.TotalSeconds) -ForegroundColor Green
}

# --- 3. e2e (opt-in) ------------------------------------------------------
Write-Stage "3/3 e2e"
if (-not $Full) {
    Write-Host "skipped (pass -Full to run the real-encode suite)" -ForegroundColor DarkGray
} elseif (-not (Test-Path ".venv\Scripts\python.exe")) {
    # Not a failure: the suite simply cannot run without its interpreter, and
    # pretending otherwise would make this gate untrustworthy.
    Write-Host "skipped (no .venv; see tests/e2e/README)" -ForegroundColor DarkGray
} else {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    & ".venv\Scripts\python.exe" "tools\e2e.py"
    if ($LASTEXITCODE -ne 0) {
        $failures += "e2e"
        Write-Host "E2E FAILED" -ForegroundColor Red
    } else {
        Write-Host ("e2e ok ({0:N1}s)" -f $sw.Elapsed.TotalSeconds) -ForegroundColor Green
    }
}

# --- summary --------------------------------------------------------------
Write-Host ""
if ($failures.Count -eq 0) {
    Write-Host ("CHECK PASSED ({0:N1}s)" -f $total.Elapsed.TotalSeconds) -ForegroundColor Green
    exit 0
}

Write-Host ("CHECK FAILED: {0} ({1:N1}s)" -f ($failures -join ", "), $total.Elapsed.TotalSeconds) -ForegroundColor Red
exit 1
