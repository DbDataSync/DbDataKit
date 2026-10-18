#!/usr/bin/env bash
# Downloads the prebuilt polyglot library for the pinned commit from this repository's `native-<pin>` release into native/<rid>/, so nothing
# has to compile Rust (about 12 minutes) on every CI run or developer machine. The files are checked against the release's SHA256SUMS.
#   scripts/fetch-native.sh [rid ...]      rids default to the current platform; `all` fetches linux-x64 and win-x64
# The pin is read from scripts/build-polyglot.sh (one source of truth; a unit test ties it to PolyglotNative.PinnedCommit).
# Publishing a library for a new pin: scripts/publish-native.sh, or the "native" workflow.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PIN="$(sed -n 's/^PIN="\([0-9a-f]\{40\}\)"$/\1/p' "$ROOT/scripts/build-polyglot.sh")"
[ -n "$PIN" ] || { echo "cannot read PIN from scripts/build-polyglot.sh" >&2; exit 1; }
REPO="${DBDATABUILD_REPO:-danshryock/DbDataBuild}"
TAG="native-${PIN:0:8}"

if [ $# -eq 0 ]; then
  case "$(uname -s)" in
    Linux) set -- linux-x64 ;;
    MINGW*|MSYS*|CYGWIN*) set -- win-x64 ;;
    *) echo "no prebuilt library for $(uname -s); build it with scripts/build-polyglot.sh" >&2; exit 1 ;;
  esac
elif [ "$1" = all ]; then
  set -- linux-x64 win-x64
fi

TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
get() { # asset -> file
  if command -v gh >/dev/null 2>&1 && { [ -n "${GH_TOKEN:-}" ] || gh auth status >/dev/null 2>&1; }; then
    gh release download "$TAG" --repo "$REPO" --pattern "$1" --dir "$TMP" --clobber
  else
    curl -fsSL --retry 5 --retry-delay 3 -o "$TMP/$1" "https://github.com/$REPO/releases/download/$TAG/$1"
  fi
}
get SHA256SUMS

for rid in "$@"; do
  case "$rid" in
    linux-x64) LIB=libpolyglot_sql_ffi.so ;;
    win-x64)   LIB=polyglot_sql_ffi.dll ;;
    *) echo "unknown rid $rid" >&2; exit 1 ;;
  esac
  ASSET="$rid-$LIB"
  want="$(awk -v a="$ASSET" '$2 == a { print $1 }' "$TMP/SHA256SUMS")"
  [ -n "$want" ] || { echo "$ASSET is not in $TAG's SHA256SUMS" >&2; exit 1; }
  if [ -f "$ROOT/native/$rid/$LIB" ] && [ "$(sha256sum "$ROOT/native/$rid/$LIB" | cut -d' ' -f1)" = "$want" ]; then
    echo "native/$rid/$LIB is current ($TAG)"; continue
  fi
  get "$ASSET"
  [ "$(sha256sum "$TMP/$ASSET" | cut -d' ' -f1)" = "$want" ] || { echo "checksum mismatch for $ASSET" >&2; exit 1; }
  mkdir -p "$ROOT/native/$rid"
  install -m 755 "$TMP/$ASSET" "$ROOT/native/$rid/$LIB"
  echo "Fetched native/$rid/$LIB ($TAG)"
done
