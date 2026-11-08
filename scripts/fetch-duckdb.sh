#!/usr/bin/env bash
# Downloads the DuckDB engine named in duckdb.pin (repository root) into .duckdb/<rid>/. Both the archive and the library inside it are
# checked against the SHA-256 recorded in the pin, so a changed byte anywhere fails.
#   scripts/fetch-duckdb.sh [rid ...]      rids: linux-x64 linux-arm64 win-x64 osx-arm64 osx-x64; default the current platform; `all` for every one
#   scripts/fetch-duckdb.sh --archives <dir> [rid ...]   only downloads the archives into <dir> (hash-checked, kept as they are, nothing extracted):
#                                          what CI's duckdb-cache action stores in the Actions cache (docs/ci-lessons.md, "GitHub release downloads fail")
# Where an archive comes from, first that has it, each hash-checked against the pin: DUCKDB_RELEASE_BASE (a folder as file://<dir>, or an https://
# address; CI points it at its cache), the pin's BASE, then DUCKDB_RELEASE_BASE_FALLBACK or the pin's optional BASE_FALLBACK. A download is retried
# with growing waits (curl --retry-all-errors), a source that has no copy (file://) is skipped.
# Environment (for tests and offline mirrors): DUCKDB_PIN (another pin file), DUCKDB_DEST (another destination folder), DUCKDB_RETRY_DELAY (seconds
# between download attempts instead of curl's growing wait; 0 is curl's own default), DUCKDB_RETRIES (curl retries per attempt, default 8), DUCKDB_CACHE_KEY (only named in the failure message).
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
ARCHIVES_ONLY=""
if [ "${1:-}" = "--archives" ]; then
  [ $# -ge 2 ] || { echo "--archives needs a folder" >&2; exit 1; }
  ARCHIVES_ONLY="$2"; shift 2
fi

# The sources, in order, without repeats; the optional pin keys are read leniently.
SOURCES=()
add_source() { local s="${1%/}" x; [ -n "$s" ] || return 0; for x in ${SOURCES[@]+"${SOURCES[@]}"}; do [ "$x" = "$s" ] && return 0; done; SOURCES+=("$s"); }
add_source "${DUCKDB_RELEASE_BASE:-}"
add_source "$(pin BASE)"
add_source "${DUCKDB_RELEASE_BASE_FALLBACK:-$(grep -E '^BASE_FALLBACK=' "$PIN" | head -n 1 | cut -d= -f2- || true)}"

# file:///home/x -> /home/x ; file:///D:/a/x (Git Bash on Windows) -> D:/a/x
local_path() { local p="${1#file://}"; if [[ $p =~ ^/[A-Za-z]: ]]; then p="${p#/}"; fi; printf '%s' "$p"; }

# fetch_archive <archive> <sha256> <destination file>: the first source whose copy has the pinned hash
fetch_archive() {
  local archive="$1" want="$2" out="$3" src attempt tried=()
  for src in "${SOURCES[@]}"; do
    tried+=("$src/$archive")
    if [[ $src == file://* ]]; then
      local from; from="$(local_path "$src")/$archive"
      if [ ! -f "$from" ]; then echo "no $archive in $src" >&2; continue; fi
      cp "$from" "$out"
      if [ "$(sha256 "$out")" = "$want" ]; then return 0; fi
      echo "checksum mismatch for $archive in $src (expected $want)" >&2; rm -f "$out"; continue
    fi
    for attempt in 1 2 3; do
      local delay=(); [ -n "${DUCKDB_RETRY_DELAY:-}" ] && delay=(--retry-delay "$DUCKDB_RETRY_DELAY")
      if curl -fsSL --connect-timeout 30 --max-time 900 --retry "${DUCKDB_RETRIES:-8}" --retry-all-errors --retry-max-time 600 ${delay[@]+"${delay[@]}"} -o "$out" "$src/$archive"; then
        if [ "$(sha256 "$out")" = "$want" ]; then return 0; fi
        echo "checksum mismatch for $archive from $src (expected $want), attempt $attempt of 3" >&2
      else
        echo "could not download $src/$archive, attempt $attempt of 3" >&2
      fi
      rm -f "$out"; sleep "${DUCKDB_RETRY_DELAY:-5}"
    done
  done
  echo "checksum mismatch or no copy of $archive (expected $want) from any source: ${tried[*]}${DUCKDB_CACHE_KEY:+; the Actions cache key $DUCKDB_CACHE_KEY had no copy}" >&2
  return 1
}

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
  if [ -n "$ARCHIVES_ONLY" ]; then
    mkdir -p "$ARCHIVES_ONLY"
    if [ -f "$ARCHIVES_ONLY/$archive" ] && [ "$(sha256 "$ARCHIVES_ONLY/$archive")" = "$archive_sha" ]; then echo "$archive is current in $ARCHIVES_ONLY"; continue; fi
    fetch_archive "$archive" "$archive_sha" "$TMP/$archive" || exit 1
    mv "$TMP/$archive" "$ARCHIVES_ONLY/$archive"
    echo "kept $archive in $ARCHIVES_ONLY"; continue
  fi
  dest="$DEST_ROOT/$rid"
  if [ -f "$dest/$lib" ] && [ "$(cat "$dest/.duckdb-version" 2>/dev/null)" = "$VERSION $lib_sha" ] && [ "$(sha256 "$dest/$lib")" = "$lib_sha" ]; then
    echo ".duckdb/$rid/$lib is current (DuckDB $VERSION)"; continue
  fi
  fetch_archive "$archive" "$archive_sha" "$TMP/$archive" || exit 1
  mkdir "$TMP/$rid"; tar -xzf "$TMP/$archive" -C "$TMP/$rid" "$lib"
  [ "$(sha256 "$TMP/$rid/$lib")" = "$lib_sha" ] || { echo "checksum mismatch for $lib inside $archive (expected $lib_sha)" >&2; exit 1; }
  mkdir -p "$dest"
  install -m 755 "$TMP/$rid/$lib" "$dest/$lib"
  printf '%s %s' "$VERSION" "$lib_sha" > "$dest/.duckdb-version"
  echo "fetched .duckdb/$rid/$lib (DuckDB $VERSION)"
done
