#!/usr/bin/env bash
# Builds the encoder on Linux / macOS.
#
#   tools/build.sh              JIT publish into publish/jit
#   tools/build.sh --aot        additionally publish NativeAOT (needs clang,
#                               zlib-dev and the ILCompiler packages)
#   tools/build.sh --no-jit     skip the framework-dependent publish
#
# DOTNET overrides the dotnet executable. Useful when the SDK only exists
# through a shim manager (mise/asdf) that a non-interactive shell never puts on
# PATH, e.g.  DOTNET="$HOME/dot" tools/build.sh
#
# Counterpart of tools/build.ps1. The one intentional difference: there is no
# AVX2 flavour here. On Windows the ILCompiler defaults to the SSE2 baseline and
# can be raised with -p:IlcInstructionSet=avx2; that knob is x86-only and made
# no measurable difference anyway (the encoding happens in FFmpeg, which
# runtime-dispatches its own SIMD).
#
# NativeAOT on Linux additionally needs:
#   sudo apt install clang zlib1g-dev        (Debian/Ubuntu)
#   sudo dnf install clang zlib-devel        (Fedora)
set -euo pipefail

AOT=0
NO_JIT=0
CONFIGURATION="Release"
RID="linux-x64"
DOTNET="${DOTNET:-dotnet}"

while [ $# -gt 0 ]; do
  case "$1" in
    --aot)     AOT=1 ;;
    --no-jit)  NO_JIT=1 ;;
    --debug)   CONFIGURATION="Debug" ;;
    --rid)     RID="${2:?--rid needs a value}"; shift ;;
    -h|--help) sed -n '2,25p' "$0"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_root/src/LiveArchiveEncoder/LiveArchiveEncoder.csproj"

if [ "$NO_JIT" -eq 0 ]; then
  echo "== JIT publish ($CONFIGURATION) =="
  "$DOTNET" publish "$project" -c "$CONFIGURATION" -o "$repo_root/publish/jit"
  echo "  -> publish/jit/LiveArchiveEncoder"
fi

if [ "$AOT" -eq 1 ]; then
  echo "== NativeAOT publish ($RID) =="
  "$DOTNET" publish "$project" -c "$CONFIGURATION" -r "$RID" \
    -p:PublishAot=true -o "$repo_root/publish/aot"
  echo "  -> publish/aot/LiveArchiveEncoder"
fi
