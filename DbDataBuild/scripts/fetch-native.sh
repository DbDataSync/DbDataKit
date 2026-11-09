#!/usr/bin/env bash
# Downloads the polyglot-sql FFI library into native/<rid>/ from the pinned upstream release (scripts/polyglot.pin; docs/native-library.md says
# why this is the published build and not our own). The archive is checked against the SHA-256 recorded in this repository, not one fetched from
# the same release: every copy, from any source (the cache included), must have the hash of the pin, so a cache can neither hide a changed pin nor
# serve a changed byte.
#   scripts/fetch-native.sh [rid ...]      rids default to the current platform; `all` fetches linux-x64 and win-x64
#   scripts/fetch-native.sh --archives <dir> [rid ...]   only downloads the archives into <dir> (hash-checked, kept as they are, nothing extracted):
#                                          what CI's native-cache action stores in the Actions cache (docs/ci-lessons.md, "GitHub release downloads fail")
# Where an archive comes from, first that has it: POLYGLOT_RELEASE_BASE (a folder as file://<dir>, or an https:// address; CI points it at its cache),
# then the upstream release. A download is retried with growing waits (curl --retry-all-errors), a source that has no copy (file://) is skipped.
# Environment (for tests and offline mirrors): POLYGLOT_PIN (another pin file), POLYGLOT_DEST (another destination folder), POLYGLOT_RETRY_DELAY
# (seconds between download attempts instead of curl's growing wait), POLYGLOT_RETRIES (curl retries per attempt, default 8), POLYGLOT_CACHE_KEY
# (only named in the failure message).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PIN="${POLYGLOT_PIN:-$ROOT/scripts/polyglot.pin}"
# shellcheck source=polyglot.pin
. "$PIN"
DEST_ROOT="${POLYGLOT_DEST:-$ROOT/native}"
UPSTREAM="https://github.com/tobilg/polyglot/releases/download/v$VERSION"

ARCHIVES_ONLY=""
if [ "${1:-}" = "--archives" ]; then
  [ $# -ge 2 ] || { echo "--archives needs a folder" >&2; exit 1; }
  ARCHIVES_ONLY="$2"; shift 2
fi

# The sources, in order, without repeats.
SOURCES=()
add_source() { local s="${1%/}" x; [ -n "$s" ] || return 0; for x in ${SOURCES[@]+"${SOURCES[@]}"}; do [ "$x" = "$s" ] && return 0; done; SOURCES+=("$s"); }
add_source "${POLYGLOT_RELEASE_BASE:-}"
add_source "$UPSTREAM"

# file:///home/x -> /home/x ; file:///D:/a/x (Git Bash on Windows) -> D:/a/x
local_path() { local p="${1#file://}"; if [[ $p =~ ^/[A-Za-z]: ]]; then p="${p#/}"; fi; printf '%s' "$p"; }

if [ $# -eq 0 ]; then
  case "$(uname -s)" in
    Linux) set -- linux-x64 ;;
    MINGW*|MSYS*|CYGWIN*) set -- win-x64 ;;
    *) echo "no published library for $(uname -s); upstream's releases cover Linux, macOS and Windows, but DbDataBuild ships linux-x64 and win-x64" >&2; exit 1 ;;
  esac
elif [ "$1" = all ]; then
  set -- linux-x64 win-x64
fi

sha256() { if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }
extract() { # archive dest
  case "$1" in
    *.tar.gz) tar -xzf "$1" -C "$2" ;;
    *.zip)
      if command -v unzip >/dev/null 2>&1; then unzip -q "$1" -d "$2"
      elif command -v 7z >/dev/null 2>&1; then 7z x -y -o"$2" "$1" >/dev/null
      elif command -v python3 >/dev/null 2>&1; then python3 -c 'import sys, zipfile; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])' "$1" "$2"
      else echo "unzip, 7z or python3 is needed to unpack $1" >&2; exit 1; fi ;;
  esac
}

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
      local delay=(); [ -n "${POLYGLOT_RETRY_DELAY:-}" ] && delay=(--retry-delay "$POLYGLOT_RETRY_DELAY")
      if curl -fsSL --connect-timeout 30 --max-time 900 --retry "${POLYGLOT_RETRIES:-8}" --retry-all-errors --retry-max-time 600 ${delay[@]+"${delay[@]}"} -o "$out" "$src/$archive"; then
        if [ "$(sha256 "$out")" = "$want" ]; then return 0; fi
        echo "checksum mismatch for $archive from $src (expected $want), attempt $attempt of 3" >&2
      else
        echo "could not download $src/$archive, attempt $attempt of 3" >&2
      fi
      rm -f "$out"; sleep "${POLYGLOT_RETRY_DELAY:-5}"
    done
  done
  echo "checksum mismatch or no copy of $archive (expected $want) from any source: ${tried[*]}${POLYGLOT_CACHE_KEY:+; the Actions cache key $POLYGLOT_CACHE_KEY had no copy}" >&2
  return 1
}

TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
for rid in "$@"; do
  case "$rid" in
    linux-x64) ARCHIVE_ID=linux-x86_64;   EXT=tar.gz; LIB=libpolyglot_sql_ffi.so; WANT="$SHA256_linux_x86_64" ;;
    win-x64)   ARCHIVE_ID=windows-x86_64; EXT=zip;    LIB=polyglot_sql_ffi.dll;   WANT="$SHA256_windows_x86_64" ;;
    *) echo "unknown rid $rid" >&2; exit 1 ;;
  esac
  NAME="polyglot-sql-ffi-$ARCHIVE_ID"
  ARCHIVE="$NAME.$EXT"
  if [ -n "$ARCHIVES_ONLY" ]; then
    mkdir -p "$ARCHIVES_ONLY"
    if [ -f "$ARCHIVES_ONLY/$ARCHIVE" ] && [ "$(sha256 "$ARCHIVES_ONLY/$ARCHIVE")" = "$WANT" ]; then echo "$ARCHIVE is current in $ARCHIVES_ONLY"; continue; fi
    fetch_archive "$ARCHIVE" "$WANT" "$TMP/$ARCHIVE" || exit 1
    mv "$TMP/$ARCHIVE" "$ARCHIVES_ONLY/$ARCHIVE"
    echo "kept $ARCHIVE in $ARCHIVES_ONLY"; continue
  fi
  DEST="$DEST_ROOT/$rid"
  if [ -f "$DEST/$LIB" ] && [ "$(cat "$DEST/.polyglot-commit" 2>/dev/null)" = "$COMMIT" ]; then
    echo "native/$rid/$LIB is current (polyglot v$VERSION)"; continue
  fi
  fetch_archive "$ARCHIVE" "$WANT" "$TMP/$ARCHIVE" || exit 1
  mkdir "$TMP/$rid"; extract "$TMP/$ARCHIVE" "$TMP/$rid"
  mkdir -p "$DEST"
  install -m 755 "$TMP/$rid/$NAME/$LIB" "$DEST/$LIB"
  printf '%s\n' "$COMMIT" > "$DEST/.polyglot-commit"
  echo "Fetched native/$rid/$LIB (polyglot v$VERSION)"
done
