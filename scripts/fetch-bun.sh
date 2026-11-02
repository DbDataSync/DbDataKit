#!/usr/bin/env bash
# Downloads the Bun named in bun.pin (repository root) into .bun/<rid>/. Both the archive and the program inside it are checked against the SHA-256
# recorded in the pin, so a changed byte anywhere fails.
#   scripts/fetch-bun.sh [rid ...]      rids: linux-x64 linux-arm64 win-x64 osx-arm64 osx-x64; default the current platform; `all` for every one
# Environment (for tests and offline mirrors): BUN_PIN (another pin file), BUN_RELEASE_BASE (another download base, https:// or file://), BUN_DEST (another destination folder).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PIN="${BUN_PIN:-$ROOT/bun.pin}"
DEST_ROOT="${BUN_DEST:-$ROOT/.bun}"
ALL_RIDS="linux-x64 linux-arm64 win-x64 osx-arm64 osx-x64"

pin() { # KEY -> value, from the pin file only
  local line
  line="$(grep -E "^$1=" "$PIN" | head -n 1)" || { echo "$PIN has no $1" >&2; exit 1; }
  printf '%s' "${line#*=}"
}
sha256() { if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }

VERSION="$(pin VERSION)"
BASE="${BUN_RELEASE_BASE:-$(pin BASE)}"

if [ $# -eq 0 ]; then
  case "$(uname -s)/$(uname -m)" in
    Linux/x86_64) set -- linux-x64 ;;
    Linux/aarch64|Linux/arm64) set -- linux-arm64 ;;
    Darwin/arm64) set -- osx-arm64 ;;
    Darwin/x86_64) set -- osx-x64 ;;
    MINGW*|MSYS*|CYGWIN*) set -- win-x64 ;;
    *) echo "no Bun is pinned for $(uname -s)/$(uname -m); name a rid: $ALL_RIDS" >&2; exit 1 ;;
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
  if [ -f "$dest/$bin" ] && [ "$(cat "$dest/.bun-version" 2>/dev/null)" = "$VERSION $bin_sha" ] && [ "$(sha256 "$dest/$bin")" = "$bin_sha" ]; then
    echo ".bun/$rid/$bin is current (Bun $VERSION)"; continue
  fi
  curl -fsSL --retry 5 --retry-delay 3 -o "$TMP/$archive" "$BASE/$archive"
  [ "$(sha256 "$TMP/$archive")" = "$archive_sha" ] || { echo "checksum mismatch for $archive (expected $archive_sha)" >&2; exit 1; }
  mkdir "$TMP/$rid"; unzip -q -j "$TMP/$archive" "*/$bin" -d "$TMP/$rid"
  [ "$(sha256 "$TMP/$rid/$bin")" = "$bin_sha" ] || { echo "checksum mismatch for $bin inside $archive (expected $bin_sha)" >&2; exit 1; }
  mkdir -p "$dest"
  install -m 755 "$TMP/$rid/$bin" "$dest/$bin"
  printf '%s %s' "$VERSION" "$bin_sha" > "$dest/.bun-version"
  echo "fetched .bun/$rid/$bin (Bun $VERSION)"
done
