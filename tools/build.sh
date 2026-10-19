#!/usr/bin/env bash
# Builds the encoder on Linux / macOS.
#
#   tools/build.sh              JIT publish into publish/jit
#   tools/build.sh --aot        additionally publish NativeAOT (needs clang,
#                               zlib-dev and the ILCompiler packages)
#   tools/build.sh --no-jit     skip the framework-dependent publish
#   tools/build.sh --tfm net11.0  target .NET 11 instead of the default net10.0
#
# DOTNET overrides the dotnet executable. Useful when the SDK only exists
# through a shim manager (mise/asdf) that a non-interactive shell never puts on
# PATH, e.g.  DOTNET="$HOME/dot" tools/build.sh
#
# Counterpart of tools/build.ps1. Two flavours only -- JIT and AOT. There used
# to be a third on Windows (-p:IlcInstructionSet=avx2) and it was dropped in
# v7.8.1: raising the AOT instruction-set baseline made no measurable
# difference because the encoding happens in FFmpeg, which runtime-dispatches
# its own SIMD.
#
# The default net10.0 output is published with RollForward=Major, so one
# archive covers both .NET 10 and .NET 11 machines.
#
# NativeAOT on Linux additionally needs:
#   sudo apt install clang zlib1g-dev        (Debian/Ubuntu)
#   sudo dnf install clang zlib-devel        (Fedora)
set -euo pipefail

AOT=0
NO_JIT=0
CONFIGURATION="Release"
RID="linux-x64"
TFM="net10.0"
DOTNET="${DOTNET:-dotnet}"

while [ $# -gt 0 ]; do
  case "$1" in
    --aot)     AOT=1 ;;
    --no-jit)  NO_JIT=1 ;;
    --debug)   CONFIGURATION="Debug" ;;
    --rid)     RID="${2:?--rid needs a value}"; shift ;;
    --tfm)     TFM="${2:?--tfm needs a value}"; shift ;;
    -h|--help) sed -n '2,30p' "$0"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_root/src/LiveArchiveEncoder/LiveArchiveEncoder.csproj"

if [ "$NO_JIT" -eq 0 ]; then
  echo "== JIT publish ($CONFIGURATION, $TFM) =="
  "$DOTNET" publish "$project" -c "$CONFIGURATION" -p:LaeTfm="$TFM" -o "$repo_root/publish/jit"
  chmod +x "$repo_root/publish/jit/LiveArchiveEncoder" 2>/dev/null || true
  echo "  -> publish/jit/LiveArchiveEncoder"
fi

if [ "$AOT" -eq 1 ]; then
  echo "== NativeAOT publish ($RID, $TFM) =="
  "$DOTNET" publish "$project" -c "$CONFIGURATION" -r "$RID" -p:LaeTfm="$TFM" \
    -p:PublishAot=true -o "$repo_root/publish/aot"
  echo "  -> publish/aot/LiveArchiveEncoder"
fi
