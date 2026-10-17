# Builds the encoder.
#
#   .\build.ps1                       JIT (framework-dependent) build only
#   .\build.ps1 -Aot                  additionally publish a NativeAOT exe
#   .\build.ps1 -Aot -AotAvx2         JIT + AOT + AOT with -p:IlcInstructionSet=avx2
#   .\build.ps1 -Aot -NoJit           NativeAOT only
#
# -NoJit skips the framework-dependent publish; the other two switches decide
# which NativeAOT flavours land in publish\aot and publish\aot-avx2.
#
# About the AVX2 flavour
# ----------------------
# NativeAOT compiles to the SSE2 x86-64 baseline unless told otherwise, which
# leaves Vector<byte>.Count at 16 and Avx2.IsSupported false. -AotAvx2 raises the
# baseline at the cost of requiring an AVX2 CPU. Note this only affects the C#
# side: the actual encoding happens in FFmpeg, which runtime-dispatches its own
# SIMD, so the measurable difference on encode throughput is nil (measured
# 2026-10-17). Treat it as an opt-in experiment, not a speed switch.
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
#   .\build.ps1 -Aot -WindowsSdkLibVersion 10.0.22621.0
#
param(
    [switch]$Aot,
    [switch]$AotAvx2,
    [switch]$NoJit,
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$WindowsSdkLibVersion = ""
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\src\LiveArchiveEncoder\LiveArchiveEncoder.csproj"
$project = [System.IO.Path]::GetFullPath($project)

function Invoke-Build {
    param([string[]]$Extra)
    $allArgs = @("publish", $project, "-c", $Configuration) + $Extra
    & dotnet @allArgs
    if (-not $?) { throw "dotnet publish failed" }
}

if (-not $NoJit) {
    Write-Host "== JIT build ($Configuration) ==" -ForegroundColor Cyan
    Invoke-Build @("-o", (Join-Path $PSScriptRoot "..\publish\jit"))
    Write-Host "  -> publish\jit\LiveArchiveEncoder.exe"
}

if ($Aot) {
    Write-Host "== NativeAOT publish ($RuntimeIdentifier) ==" -ForegroundColor Cyan
    $extra = @("-r", $RuntimeIdentifier, "-p:PublishAot=true",
               "-o", (Join-Path $PSScriptRoot "..\publish\aot"))
    if ($WindowsSdkLibVersion -ne "") {
        $extra += "-p:WindowsSdkLibVersion=$WindowsSdkLibVersion"
    }
    Invoke-Build $extra
    Write-Host "  -> publish\aot\LiveArchiveEncoder.exe"
}

if ($AotAvx2) {
    Write-Host "== NativeAOT publish with AVX2 baseline ($RuntimeIdentifier) ==" -ForegroundColor Cyan
    $extra = @("-r", $RuntimeIdentifier, "-p:PublishAot=true", "-p:IlcInstructionSet=avx2",
               "-o", (Join-Path $PSScriptRoot "..\publish\aot-avx2"))
    if ($WindowsSdkLibVersion -ne "") {
        $extra += "-p:WindowsSdkLibVersion=$WindowsSdkLibVersion"
    }
    Invoke-Build $extra
    Write-Host "  -> publish\aot-avx2\LiveArchiveEncoder.exe"
}
