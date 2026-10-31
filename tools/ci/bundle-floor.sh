#!/usr/bin/env bash
# What glibc does the shipped single file actually need?
#
#   docker exec lae-al9 bash /tmp/bundle-floor.sh /home/ci/stage/LiveArchiveEncoder
#
# The exe itself only asks for 2.27. The honest number comes from the native
# libraries bundled inside it: the single-file host extracts them into
# DOTNET_BUNDLE_EXTRACT_BASE_DIR on first run, so we can ask the real file.
set -euo pipefail
bin="${1:-/home/ci/stage/LiveArchiveEncoder}"
ext=$(mktemp -d)

DOTNET_BUNDLE_EXTRACT_BASE_DIR="$ext" "$bin" --version >/dev/null

echo "extracted:"
find "$ext" -name '*.so*' -printf '%f\n' | sort -u

echo "floors:"
for so in $(find "$ext" -name '*.so*'); do
  printf '%-28s %s\n' "$(basename "$so")" \
    "$(objdump -T "$so" 2>/dev/null | grep -o 'GLIBC_[0-9.]*' | sort -Vu | tail -1)"
done

rm -rf "$ext"
