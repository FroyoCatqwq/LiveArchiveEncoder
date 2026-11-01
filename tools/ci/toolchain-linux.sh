#!/usr/bin/env bash
# Publish a self-contained single-file Linux build inside the AL9 container and
# exercise `lae toolchain install` there. Validates the .tar.xz path (the zip path
# is what Windows exercises) and the execute-bit fix-up.
set -euo pipefail

export DOTNET_ROOT=/home/ci/dotnet11
export PATH="/home/ci/dotnet11:/home/ci/ffmpeg/bin:${PATH}"
export DOTNET_CLI_UI_LANGUAGE=en
export DOTNET_NOLOGO=1
export DOTNET_CLI_HOME=/home/ci/.dotnet
export NUGET_PACKAGES=/home/ci/.nuget

cd /src

echo "== publish =="
rm -rf /tmp/tc /tmp/pubart
# /src is mounted read-only; ArtifactsPath is what moves obj/bin somewhere writable.
dotnet publish src/Lae.Cli/Lae.Cli.csproj -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none \
  -p:ArtifactsPath=/tmp/pubart -o /tmp/tc 2>&1 | tail -2
ls -l /tmp/tc

echo "== empty config =="
export LAE_CONFIG_DIR=/tmp/tc-cfg
rm -rf "$LAE_CONFIG_DIR"
/tmp/tc/LiveArchiveEncoder --version
/tmp/tc/LiveArchiveEncoder toolchain list
/tmp/tc/LiveArchiveEncoder setup || true

echo "== install (tar.xz path) =="
/tmp/tc/LiveArchiveEncoder toolchain install btbn
echo "== use =="
/tmp/tc/LiveArchiveEncoder toolchain use btbn
/tmp/tc/LiveArchiveEncoder toolchain path
/tmp/tc/LiveArchiveEncoder toolchain list
echo "== setup after =="
/tmp/tc/LiveArchiveEncoder setup

echo "== chinese refusal =="
LAE_LANG=zh /tmp/tc/LiveArchiveEncoder encode /tmp/nothing.mkv --out /tmp/nothing.mkv || true
echo "TOOLCHAIN LINUX: OK"
