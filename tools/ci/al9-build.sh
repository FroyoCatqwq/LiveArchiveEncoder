#!/usr/bin/env bash
# Build and unit-test the v8 solution inside an AlmaLinux 9 container.
#
# Why a container: the release targets glibc 2.28 (AlmaLinux 8), but the host
# that has the build machine on it should not be the only thing that ever
# compiles the code. Running the same build on 2.34 catches the case where a
# dependency silently raises the floor -- which is exactly how the SQLite
# native library broke the history database on RHEL-family 8.
#
# One-time setup on the host:
#
#   docker volume create lae-al9-home
#   docker run -d --name lae-al9 \
#     -v "$PWD":/src:ro -v lae-al9-home:/home/ci almalinux:9 sleep infinity
#   docker cp tools/ci/al9-setup.sh lae-al9:/tmp/ && docker exec lae-al9 bash /tmp/al9-setup.sh
#
# Then, from the repository root:
#
#   docker cp tools/ci/al9-build.sh lae-al9:/tmp/ && docker exec lae-al9 bash /tmp/al9-build.sh test
#
# The source tree is mounted read-only and every build output goes to the
# /home/ci volume, so the host checkout stays clean and the host's obj/bin
# (built against a different glibc) can never be reused by accident.
set -euo pipefail

export DOTNET_ROOT=/home/ci/dotnet11
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_HOME=/home/ci/.dotnet
export NUGET_PACKAGES=/home/ci/.nuget
export DOTNET_CLI_UI_LANGUAGE=en
export DOTNET_NOLOGO=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export ARTIFACTS=/home/ci/artifacts

mkdir -p "$DOTNET_CLI_HOME" "$NUGET_PACKAGES" "$ARTIFACTS"

# The mount point doubles as the working copy: /src is the host checkout.
repo="${LAE_SRC:-/src}"
cd "$repo"

echo "== glibc =="
/lib64/ld-linux-x86-64.so.2 --version | head -1
echo "== sdk =="
dotnet --version

echo "== build =="
dotnet build LiveArchiveEncoder.V8.slnx -c Release -v q -p:ArtifactsPath="$ARTIFACTS"

if [ "${1:-}" = "test" ]; then
  echo "== test =="
  dotnet test --solution LiveArchiveEncoder.V8.slnx -c Release \
    -p:ArtifactsPath="$ARTIFACTS" --no-build 2>&1 | tail -n 12
fi

echo "AL9 BUILD: OK"
