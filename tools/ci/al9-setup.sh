#!/usr/bin/env bash
# One-time setup for the AlmaLinux 9 build container: system libraries the
# .NET runtime needs, then the SDK pinned by global.json into the data volume.
#
#   docker cp tools/ci/al9-setup.sh lae-al9:/tmp/ && docker exec lae-al9 bash /tmp/al9-setup.sh
#
# libicu is not optional: without it the runtime aborts before printing anything
# ("Couldn't find a valid ICU package"), and the message comes from a fail-fast
# deep inside the host, not from the command you ran.
set -euo pipefail

echo "== libicu =="
dnf install -y libicu 2>&1 | tail -4

echo "== dotnet-install =="
# The version has to be the four-part build number: the installer does not
# resolve "11.0.100-rc.3" and fails with a misleading lifecycle error.
curl -sSL -m 60 -o /tmp/di.sh https://dot.net/v1/dotnet-install.sh
bash /tmp/di.sh --version 11.0.100-rc.3.26522.148 \
  --install-dir /home/ci/dotnet11 --no-path 2>&1 | tail -5

echo "== sdk =="
DOTNET_ROOT=/home/ci/dotnet11 /home/ci/dotnet11/dotnet --version
echo "SETUP: OK"
