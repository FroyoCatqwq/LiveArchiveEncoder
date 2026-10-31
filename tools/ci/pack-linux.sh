#!/usr/bin/env bash
# Build the Linux release package: one self-contained file, tarred.
#
#   docker exec lae-al9 bash /tmp/pack-linux.sh [version]
#
# Run this inside the AlmaLinux 9 container. The supported baseline is glibc
# 2.34 (RHEL/AlmaLinux/Rocky 9, Debian 12, Ubuntu 22.04+), so the build machine
# has to be at or below that floor -- and since 2026-10-31 the older 8.10 host
# cannot run the result any more, because the SQLite native library it bundles
# is built against 2.34.
#
# No AOT: the package is a self-contained single file. See pack-win.ps1 for why.
set -euo pipefail

export DOTNET_ROOT=/home/ci/dotnet11
export PATH="$DOTNET_ROOT:/home/ci/ffmpeg/bin:$PATH"
export DOTNET_CLI_HOME=/home/ci/.dotnet
export NUGET_PACKAGES=/home/ci/.nuget
export DOTNET_CLI_UI_LANGUAGE=en
export DOTNET_NOLOGO=1
export ARTIFACTS=/home/ci/artifacts

repo=/src
out=/home/ci/release
stage=/home/ci/stage

version="${1:-}"
if [ -z "$version" ]; then
  version=$(grep -o 'Current = "[^"]*"' "$repo/src/Lae.Core/LaeVersion.cs" | cut -d'"' -f2)
fi
echo "== version $version =="

cd "$repo"
git log --oneline -1 2>/dev/null || echo "(no git metadata in the mount)"

rm -rf "$stage" && mkdir -p "$stage" "$out"

dotnet publish src/Lae.Cli/Lae.Cli.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none \
  -p:ArtifactsPath="$ARTIFACTS" \
  -o "$stage" --nologo -v q

# One file means one file.
extra=$(find "$stage" -maxdepth 1 -type f ! -name LiveArchiveEncoder -printf '%f ')
if [ -n "$extra" ]; then
  echo "publish left extra files: $extra" >&2
  exit 1
fi

chmod +x "$stage/LiveArchiveEncoder"

echo "== smoke =="
cfg=$(mktemp -d)
reported=$(LAE_CONFIG_DIR="$cfg" "$stage/LiveArchiveEncoder" --version | tr -d '\r')
if [ "$reported" != "$version" ]; then
  echo "the binary says '$reported', expected '$version'" >&2
  exit 1
fi
LAE_CONFIG_DIR="$cfg" "$stage/LiveArchiveEncoder" history list
LAE_CONFIG_DIR="$cfg" "$stage/LiveArchiveEncoder" config path
rm -rf "$cfg"
echo "--version ok: $reported"

echo "== glibc floor of the bundle =="
# The single file carries its native libraries, so the only honest way to see
# the floor is to ask the extracted library. The runtime itself is 2.27.
objdump -T "$stage/LiveArchiveEncoder" | grep -o 'GLIBC_[0-9.]*' | sort -Vu | tail -1

echo "== package =="
tar -czf "$out/LiveArchiveEncoder-$version-linux-x64.tar.gz" -C "$stage" .
ls -l "$out"
echo "sha256 $(sha256sum "$out/LiveArchiveEncoder-$version-linux-x64.tar.gz" | cut -d' ' -f1)"
echo "PACK: OK"
