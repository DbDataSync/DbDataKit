#!/usr/bin/env bash
# Downloads the polyglot-sql FFI library into native/<rid>/ from the pinned upstream release (scripts/polyglot.pin; docs/native-library.md says
# why this is the published build and not our own). The archive is checked against the SHA-256 recorded in this repository, not one fetched from
# the same release.
#   scripts/fetch-native.sh [rid ...]      rids default to the current platform; `all` fetches linux-x64 and win-x64
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
# shellcheck source=polyglot.pin
. "$ROOT/scripts/polyglot.pin"
BASE="${POLYGLOT_RELEASE_BASE:-https://github.com/tobilg/polyglot/releases/download/v$VERSION}"

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

TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
for rid in "$@"; do
  case "$rid" in
    linux-x64) ARCHIVE_ID=linux-x86_64;   EXT=tar.gz; LIB=libpolyglot_sql_ffi.so; WANT="$SHA256_linux_x86_64" ;;
    win-x64)   ARCHIVE_ID=windows-x86_64; EXT=zip;    LIB=polyglot_sql_ffi.dll;   WANT="$SHA256_windows_x86_64" ;;
    *) echo "unknown rid $rid" >&2; exit 1 ;;
  esac
  DEST="$ROOT/native/$rid"
  if [ -f "$DEST/$LIB" ] && [ "$(cat "$DEST/.polyglot-commit" 2>/dev/null)" = "$COMMIT" ]; then
    echo "native/$rid/$LIB is current (polyglot v$VERSION)"; continue
  fi
  NAME="polyglot-sql-ffi-$ARCHIVE_ID"
  curl -fsSL --retry 5 --retry-delay 3 -o "$TMP/$NAME.$EXT" "$BASE/$NAME.$EXT"
  [ "$(sha256 "$TMP/$NAME.$EXT")" = "$WANT" ] || { echo "checksum mismatch for $NAME.$EXT (expected $WANT)" >&2; exit 1; }
  mkdir "$TMP/$rid"; extract "$TMP/$NAME.$EXT" "$TMP/$rid"
  mkdir -p "$DEST"
  install -m 755 "$TMP/$rid/$NAME/$LIB" "$DEST/$LIB"
  printf '%s\n' "$COMMIT" > "$DEST/.polyglot-commit"
  echo "Fetched native/$rid/$LIB (polyglot v$VERSION)"
done
