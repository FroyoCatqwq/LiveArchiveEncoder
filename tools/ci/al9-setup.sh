#!/usr/bin/env bash
# One-time setup for the AlmaLinux 9 build container: system libraries the
# .NET runtime needs, the SDK pinned by global.json, and an FFmpeg that came
# from the project's own source rather than from the distribution.
#
#   docker cp tools/ci/al9-setup.sh lae-al9:/tmp/ && docker exec lae-al9 bash /tmp/al9-setup.sh
#
# libicu is not optional: without it the runtime aborts before printing anything
# ("Couldn't find a valid ICU package"), and the message comes from a fail-fast
# deep inside the host, not from the command you ran.
#
# FFmpeg comes from BtbN, which is where the program downloads it from too. A
# distribution build is a different binary with a different encoder list and a
# different set of legacy decoders, so testing against it answers a question
# nobody asked -- the suite would pass here and fail on a user's machine that
# installed the managed toolchain.
set -euo pipefail

echo "== libicu =="
dnf install -y libicu xz 2>&1 | tail -4

echo "== dotnet =="
# The version has to be the four-part build number: the installer does not
# resolve "11.0.100-rc.3" and fails with a misleading lifecycle error.
curl -sSL -m 60 -o /tmp/di.sh https://dot.net/v1/dotnet-install.sh
bash /tmp/di.sh --version 11.0.100-rc.3.26522.148 \
  --install-dir /home/ci/dotnet11 --no-path 2>&1 | tail -3
DOTNET_ROOT=/home/ci/dotnet11 /home/ci/dotnet11/dotnet --version

echo "== ffmpeg (BtbN master, gpl) =="
if [ ! -x /home/ci/ffmpeg/bin/ffmpeg ]; then
  base=https://github.com/BtbN/FFmpeg-Builds/releases/download/latest
  asset=ffmpeg-master-latest-linux64-gpl.tar.xz
  cd /tmp
  curl -sSL -m 900 -o "$asset" "$base/$asset"
  curl -sSL -m 120 -o checksums.sha256 "$base/checksums.sha256"
  # Verify before unpacking: a truncated archive unpacks into a binary that
  # fails at random places, which is a worse afternoon than a hash error. The
  # name has to stay the asset's own, because that is the name in the file.
  grep " $asset\$" checksums.sha256 | sha256sum -c -
  # Unpack whole and then take bin/: naming a member to tar needs its exact
  # spelling including the trailing slash, and getting it wrong extracts
  # nothing while still returning success.
  rm -rf /tmp/ffx /home/ci/ffmpeg && mkdir -p /tmp/ffx
  tar -xJf "$asset" -C /tmp/ffx
  mv /tmp/ffx/ffmpeg-master-latest-linux64-gpl /home/ci/ffmpeg
  rm -rf /tmp/ffx
  rm -f "$asset"
fi

/home/ci/ffmpeg/bin/ffmpeg -hide_banner -version | head -1

echo "SETUP: OK"
