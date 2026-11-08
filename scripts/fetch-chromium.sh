#!/usr/bin/env bash
# Downloads the Chromium (Chrome for Testing) named in chromium.pin (repository root) into .chromium/<rid>/. Both the archive and the executable inside it
# are checked against the SHA-256 recorded in the pin, so a changed byte anywhere fails. The whole archive is installed (the browser needs its sibling files).
#   scripts/fetch-chromium.sh [rid ...]      rids: linux-x64 win-x64; default the current platform; `all` for every one
# Environment (for tests and offline mirrors): CHROMIUM_PIN (another pin file), CHROMIUM_RELEASE_BASE (another download base, https:// or file://),
# CHROMIUM_DEST (another destination folder).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PIN="${CHROMIUM_PIN:-$ROOT/chromium.pin}"
DEST_ROOT="${CHROMIUM_DEST:-$ROOT/.chromium}"
ALL_RIDS="linux-x64 win-x64"

pin() { # KEY -> value, from the pin file only
  local line
  line="$(grep -E "^$1=" "$PIN" | head -n 1)" || { echo "$PIN has no $1" >&2; exit 1; }
  printf '%s' "${line#*=}" | tr -d '\r'
}
sha256() { if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }

VERSION="$(pin VERSION)"
BASE="${CHROMIUM_RELEASE_BASE:-$(pin BASE)}"

if [ $# -eq 0 ]; then
  case "$(uname -s)/$(uname -m)" in
    Linux/x86_64) set -- linux-x64 ;;
    MINGW*|MSYS*|CYGWIN*) set -- win-x64 ;;
    *) echo "no Chromium is pinned for $(uname -s)/$(uname -m); name a rid: $ALL_RIDS" >&2; exit 1 ;;
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
  bin="$(pin "BIN_$key")"; bin_sha="$(pin "BIN_SHA256_$key")"
  dest="$DEST_ROOT/$rid"
  if [ -f "$dest/$bin" ] && [ "$(cat "$dest/.chromium-version" 2>/dev/null)" = "$VERSION $bin_sha" ] && [ "$(sha256 "$dest/$bin")" = "$bin_sha" ]; then
    echo ".chromium/$rid/$bin is current (Chromium $VERSION)"; continue
  fi
  file="$TMP/$(basename "$archive")"
  curl -fsSL --retry 5 --retry-delay 3 -o "$file" "$BASE/$archive"
  [ "$(sha256 "$file")" = "$archive_sha" ] || { echo "checksum mismatch for $archive (expected $archive_sha)" >&2; exit 1; }
  mkdir "$TMP/$rid"; unzip -q "$file" -d "$TMP/$rid"; rm -f "$file"
  [ -f "$TMP/$rid/$bin" ] && [ "$(sha256 "$TMP/$rid/$bin")" = "$bin_sha" ] || { echo "checksum mismatch for $bin inside $archive (expected $bin_sha)" >&2; exit 1; }
  rm -rf "$dest"; mkdir -p "$DEST_ROOT"
  mv "$TMP/$rid" "$dest"
  chmod 755 "$dest/$bin"
  printf '%s %s' "$VERSION" "$bin_sha" > "$dest/.chromium-version"
  echo "fetched .chromium/$rid/$bin (Chromium $VERSION)"
done
