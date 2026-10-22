#!/usr/bin/env bash
# Downloads the DuckDB engine named in duckdb.pin (repository root) into .duckdb/<rid>/. Both the archive and the library inside it are
# checked against the SHA-256 recorded in the pin, so a changed byte anywhere fails.
#   scripts/fetch-duckdb.sh [rid ...]      rids: linux-x64 linux-arm64 win-x64 osx-arm64 osx-x64; default the current platform; `all` for every one
# Environment (for tests and offline mirrors): DUCKDB_PIN (another pin file), DUCKDB_RELEASE_BASE (another download base, https:// or file://),
# DUCKDB_DEST (another destination folder).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PIN="${DUCKDB_PIN:-$ROOT/duckdb.pin}"
DEST_ROOT="${DUCKDB_DEST:-$ROOT/.duckdb}"
ALL_RIDS="linux-x64 linux-arm64 win-x64 osx-arm64 osx-x64"

pin() { # KEY -> value, from the pin file only
  local line
  line="$(grep -E "^$1=" "$PIN" | head -n 1)" || { echo "$PIN has no $1" >&2; exit 1; }
  printf '%s' "${line#*=}"
}
sha256() { if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }

VERSION="$(pin VERSION)"
BASE="${DUCKDB_RELEASE_BASE:-$(pin BASE)}"

if [ $# -eq 0 ]; then
  case "$(uname -s)/$(uname -m)" in
    Linux/x86_64) set -- linux-x64 ;;
    Linux/aarch64|Linux/arm64) set -- linux-arm64 ;;
    Darwin/arm64) set -- osx-arm64 ;;
    Darwin/x86_64) set -- osx-x64 ;;
    MINGW*|MSYS*|CYGWIN*) set -- win-x64 ;;
    *) echo "no DuckDB library is pinned for $(uname -s)/$(uname -m); name a rid: $ALL_RIDS" >&2; exit 1 ;;
  esac
elif [ "$1" = all ]; then
  # shellcheck disable=SC2086
  set -- $ALL_RIDS
fi

TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
for rid in "$@"; do
  key="${rid//-/_}"
  case " $ALL_RIDS " in *" $rid "*) ;; *) echo "unknown rid $rid (known: $ALL_RIDS)" >&2; exit 1 ;; esac
  archive="$(pin "ARCHIVE_$key")"; archive_sha="$(pin "ARCHIVE_SHA256_$key")"
  lib="$(pin "LIB_$key")"; lib_sha="$(pin "LIB_SHA256_$key")"
  dest="$DEST_ROOT/$rid"
  if [ -f "$dest/$lib" ] && [ "$(cat "$dest/.duckdb-version" 2>/dev/null)" = "$VERSION $lib_sha" ] && [ "$(sha256 "$dest/$lib")" = "$lib_sha" ]; then
    echo ".duckdb/$rid/$lib is current (DuckDB $VERSION)"; continue
  fi
  curl -fsSL --retry 5 --retry-delay 3 -o "$TMP/$archive" "$BASE/$archive"
  [ "$(sha256 "$TMP/$archive")" = "$archive_sha" ] || { echo "checksum mismatch for $archive (expected $archive_sha)" >&2; exit 1; }
  mkdir "$TMP/$rid"; tar -xzf "$TMP/$archive" -C "$TMP/$rid" "$lib"
  [ "$(sha256 "$TMP/$rid/$lib")" = "$lib_sha" ] || { echo "checksum mismatch for $lib inside $archive (expected $lib_sha)" >&2; exit 1; }
  mkdir -p "$dest"
  install -m 755 "$TMP/$rid/$lib" "$dest/$lib"
  printf '%s %s' "$VERSION" "$lib_sha" > "$dest/.duckdb-version"
  echo "fetched .duckdb/$rid/$lib (DuckDB $VERSION)"
done
