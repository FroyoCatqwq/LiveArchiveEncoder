# Builds the encoder.
#
#   tools\build.ps1                       JIT (framework-dependent) build only
#   tools\build.ps1 -Aot                  additionally publish a NativeAOT exe
#   tools\build.ps1 -Aot -NoJit           NativeAOT only
#
# Two flavours, and that is the point: JIT and AOT. v7.8.1 dropped the third one.
#
# About the AVX2 flavour that used to be here
# -------------------------------------------
# NativeAOT compiles to the SSE2 x86-64 baseline unless told otherwise, which
# leaves Vector<byte>.Count at 16 and Avx2.IsSupported false. -p:IlcInstructionSet=avx2
# raised that baseline at the cost of requiring an AVX2 CPU -- and bought
# nothing: the encoding happens in FFmpeg, which runtime-dispatches its own
# SIMD, so the measured difference on encode throughput was nil (2026-10-17).
# The switch is gone; the JIT build picks up AVX2 at runtime anyway.
#
# Target framework
# ----------------
# The default build targets net10.0 and is produced with RollForward=Major, so
# the same output runs on a machine with .NET 10 and on one that only has
# .NET 11. Pass -Tfm net11.0 for a build that targets .NET 11 explicitly.
#
# NativeAOT note
# --------------
# Publishing AOT needs link.exe plus the Windows SDK import libraries
# (advapi32.lib, ucrt.lib, ...). The ILCompiler targets find them by running
# vcvarsall.bat, whose SDK discovery goes through reg.exe. On machines where
# that lookup is unavailable the SDK directories must be supplied explicitly;
# the csproj does this through $(WindowsSdkLibRoot) / $(WindowsSdkLibVersion)
# and both can be overridden here:
#
#   tools\build.ps1 -Aot -WindowsSdkLibVersion 10.0.22621.0
#
param(
    [switch]$Aot,
    [switch]$NoJit,
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$Tfm = "net10.0",
    [string]$WindowsSdkLibVersion = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot          # tools\ -> project root
$project = Join-Path $repoRoot "src\LiveArchiveEncoder\LiveArchiveEncoder.csproj"
$project = [System.IO.Path]::GetFullPath($project)

function Invoke-Build {
    param([string[]]$Extra)
    $allArgs = @("publish", $project, "-c", $Configuration, "-p:LaeTfm=$Tfm") + $Extra
    & dotnet @allArgs
    if (-not $?) { throw "dotnet publish failed" }
}

if (-not $NoJit) {
    Write-Host "== JIT build ($Configuration, $Tfm) ==" -ForegroundColor Cyan
    Invoke-Build @("-o", (Join-Path $repoRoot "publish\jit"))
    Write-Host "  -> publish\jit\LiveArchiveEncoder.exe"
}

if ($Aot) {
    Write-Host "== NativeAOT publish ($RuntimeIdentifier, $Tfm) ==" -ForegroundColor Cyan
    $extra = @("-r", $RuntimeIdentifier, "-p:PublishAot=true",
               "-o", (Join-Path $repoRoot "publish\aot"))
    if ($WindowsSdkLibVersion -ne "") {
        $extra += "-p:WindowsSdkLibVersion=$WindowsSdkLibVersion"
    }
    Invoke-Build $extra
    Write-Host "  -> publish\aot\LiveArchiveEncoder.exe"
}
