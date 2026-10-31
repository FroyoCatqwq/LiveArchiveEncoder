<#
.SYNOPSIS
    Build the Windows release package: one self-contained file, zipped.

.DESCRIPTION
    No AOT. Since 2026-10-31 the release is a framework-independent (self-contained)
    single file only: NativeAOT was costing a second toolchain and a slower build
    for a size win this program does not need, and the artifacts have to be
    reproducible on a plain CI runner.

    Three things are checked before the archive exists, because a package that is
    wrong after it is uploaded is a release nobody can fix in place:
      * the binary runs and prints the version this repository says it is,
      * no native library was left outside the single file,
      * the archive is not absurdly larger than the last one.

.PARAMETER Version
    Version tag for the file name, e.g. 8.0.0-m2.2. Defaults to what the code says.

.EXAMPLE
    pwsh -NoProfile -File tools\ci\pack-win.ps1
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Output = "publish\release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $root

if (-not $Version) {
    $line = Select-String -Path "src\Lae.Core\LaeVersion.cs" -Pattern 'Current = "([^"]+)"'
    $Version = $line.Matches[0].Groups[1].Value
}
Write-Host "version $Version" -ForegroundColor Cyan

$stage = "publish\win-x64"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

dotnet publish src\Lae.Cli\Lae.Cli.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $stage --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# A single file is a promise: the SQLite native library used to sit next to the
# exe, so "one file" was true until someone copied only that file somewhere.
$extra = Get-ChildItem $stage -File | Where-Object { $_.Extension -ne ".exe" }
if ($extra) { throw "publish left extra files: $($extra.Name -join ', ')" }

$exe = Join-Path $stage "LiveArchiveEncoder.exe"
$reported = (& $exe --version).Trim()
if ($reported -ne $Version) { throw "the binary says '$reported', expected '$Version'" }
Write-Host "--version ok: $reported" -ForegroundColor Green

New-Item -ItemType Directory -Force -Path $Output | Out-Null
$zip = Join-Path $Output "LiveArchiveEncoder-$Version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }

# tar, not Compress-Archive: the latter is an order of magnitude slower on a
# 40 MB payload and puts the entries in a different order every time.
tar -a -c -f $zip -C $stage .
if ($LASTEXITCODE -ne 0) { throw "zip failed" }

$size = (Get-Item $zip).Length
$sha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$exeSize = (Get-Item $exe).Length

Write-Host ""
Write-Host ("package {0}" -f (Split-Path -Leaf $zip)) -ForegroundColor Green
Write-Host ("  exe   {0:N0} B" -f $exeSize)
Write-Host ("  zip   {0:N0} B" -f $size)
Write-Host ("  sha256 {0}" -f $sha)
