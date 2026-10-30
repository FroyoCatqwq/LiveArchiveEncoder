#!/usr/bin/env bash
# Measure the glibc floor and bundled SQLite version of every SQLitePCLRaw
# native package release, so a dependency bump can be argued with numbers
# instead of guessed at.
#
#   docker exec lae-al9 bash /tmp/sqlite-glibc.sh [versions...]
#
# Needs objdump (binutils, part of a normal image) and python3 (for unzip).
# Last measured 2026-10-31:
#
#   2.1.10  GLIBC_2.28  sqlite 3.46.1
#   2.1.11  GLIBC_2.28  sqlite 3.49.1   (CVE-2025-6965)
#   2.1.12  GLIBC_2.34  sqlite 3.53.3
#   2.1.13  GLIBC_2.34  sqlite 3.53.3
#   2.1.14  GLIBC_2.28  sqlite 3.54.0   <- the one we need
set -uo pipefail
cd /tmp

versions=("$@")
if [ ${#versions[@]} -eq 0 ]; then
  versions=(2.1.10 2.1.11 2.1.12 2.1.13 2.1.14)
fi

printf '%-8s %-11s %-12s %s\n' version glibc sqlite size
for v in "${versions[@]}"; do
  f="sqlitepclraw.lib.e_sqlite3.$v.nupkg"
  if [ ! -f "$f" ]; then
    curl -sSL -m 90 -o "$f" "https://www.nuget.org/api/v2/package/SQLitePCLRaw.lib.e_sqlite3/$v" \
      || { echo "$v: download failed"; continue; }
  fi
  rm -rf "x$v" && mkdir -p "x$v"
  python3 -c "import zipfile,sys;zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])" "$f" "x$v" 2>/dev/null
  so=$(find "x$v" -name 'libe_sqlite3.so' | head -1)
  if [ -z "$so" ]; then echo "$v: no linux .so"; continue; fi
  glibc=$(objdump -T "$so" | grep -o 'GLIBC_[0-9.]*' | sort -Vu | tail -1)
  sqlite=$(strings "$so" | grep -m1 -o '3\.[0-9][0-9]\.[0-9]\+' || echo '?')
  printf '%-8s %-11s %-12s %s\n' "$v" "$glibc" "$sqlite" "$(stat -c%s "$so")"
done
