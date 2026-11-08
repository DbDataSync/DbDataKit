#!/usr/bin/env bash
# Downloads the bundled font set named in fonts.pin (repository root) into .fonts/<family>/. Each archive, every font file taken from it and the licence text are
# checked against the SHA-256 recorded in the pin, so a changed byte anywhere fails. The licence is installed beside the files as OFL.txt.
#   scripts/fetch-fonts.sh [family ...]      families: those named by FAMILIES in the pin (noto-sans noto-serif noto-sans-mono); default and `all`: every one
# Fonts are the same on every platform, so there are no rids.
# Environment (for tests and offline mirrors): FONTS_PIN (another pin file), FONTS_RELEASE_BASE (another download base, https:// or file://), FONTS_DEST (another destination folder).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PIN="${FONTS_PIN:-$ROOT/fonts.pin}"
DEST_ROOT="${FONTS_DEST:-$ROOT/.fonts}"

pin() { # KEY -> value, from the pin file only
  local line
  line="$(grep -E "^$1=" "$PIN" | head -n 1)" || { echo "$PIN has no $1" >&2; exit 1; }
  printf '%s' "${line#*=}" | tr -d '\r'
}
sha256() { if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }

VERSION="$(pin VERSION)"
BASE="${FONTS_RELEASE_BASE:-$(pin BASE)}"
ALL_FAMILIES="$(pin FAMILIES)"
if [ $# -eq 0 ] || [ "$1" = all ]; then
  # shellcheck disable=SC2086
  set -- $ALL_FAMILIES
fi

files_of() { grep -E "^FILE=$1\|" "$PIN" | tr -d '\r' | sed 's/^FILE=//' || true; } # family|path|sha256 lines

TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
for family in "$@"; do
  case " $ALL_FAMILIES " in *" $family "*) ;; *) echo "unknown family $family (known: $ALL_FAMILIES)" >&2; exit 1 ;; esac
  key="${family//-/_}"
  archive="$(pin "ARCHIVE_$key")"; archive_sha="$(pin "ARCHIVE_SHA256_$key")"
  license_path="$(pin "LICENSE_FILE_$key")"; license_sha="$(pin "LICENSE_SHA256_$key")"
  dest="$DEST_ROOT/$family"
  stamp="$VERSION $archive_sha"
  current=1
  if [ "$(cat "$dest/.fonts-version" 2>/dev/null)" != "$stamp" ] || [ ! -f "$dest/OFL.txt" ] || [ "$(sha256 "$dest/OFL.txt")" != "$license_sha" ]; then current=0; fi
  if [ "$current" = 1 ]; then
    while IFS='|' read -r _ path sha; do
      [ -f "$dest/$(basename "$path")" ] && [ "$(sha256 "$dest/$(basename "$path")")" = "$sha" ] || { current=0; break; }
    done < <(files_of "$family")
  fi
  if [ "$current" = 1 ]; then echo ".fonts/$family is current (fonts $VERSION)"; continue; fi
  file="$TMP/$family.tgz"
  curl -fsSL --retry 5 --retry-delay 3 -o "$file" "$BASE/$archive"
  [ "$(sha256 "$file")" = "$archive_sha" ] || { echo "checksum mismatch for $archive (expected $archive_sha)" >&2; exit 1; }
  mkdir "$TMP/$family"; tar -xzf "$file" -C "$TMP/$family"; rm -f "$file"
  [ -f "$TMP/$family/$license_path" ] && [ "$(sha256 "$TMP/$family/$license_path")" = "$license_sha" ] || { echo "checksum mismatch for $license_path inside $archive (expected $license_sha)" >&2; exit 1; }
  mkdir "$TMP/$family.out"; count=0
  while IFS='|' read -r _ path sha; do
    [ -f "$TMP/$family/$path" ] && [ "$(sha256 "$TMP/$family/$path")" = "$sha" ] || { echo "checksum mismatch for $path inside $archive (expected $sha)" >&2; exit 1; }
    cp "$TMP/$family/$path" "$TMP/$family.out/$(basename "$path")"; count=$((count + 1))
  done < <(files_of "$family")
  [ "$count" -gt 0 ] || { echo "$PIN lists no files for $family" >&2; exit 1; }
  cp "$TMP/$family/$license_path" "$TMP/$family.out/OFL.txt"
  printf '%s' "$stamp" > "$TMP/$family.out/.fonts-version"
  rm -rf "$dest"; mkdir -p "$DEST_ROOT"
  mv "$TMP/$family.out" "$dest"
  echo "fetched .fonts/$family ($count files, fonts $VERSION)"
done
