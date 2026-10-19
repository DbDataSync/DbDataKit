#!/usr/bin/env bash
# Downloads a release from GitHub and installs (or upgrades) it on Linux or macOS.
#
# One of the standard distribution files (dist/README.md). The copy attached to a release is bound to one product by
# dist/dist.py, from <Product>/dist.json; this is the template, and it refuses to run unbound.
#
# Every product's releases are in one repository, so a release is this product's only if its tag starts with <product>/v.
# 'latest' means the newest of those that is not a prerelease.
#
#   curl -fsSL https://github.com/DbDataSync/DbDataKit/releases/download/<product>/dist/install.sh | bash
#   curl -fsSL .../install.sh | bash -s -- --version 2026.10.19.1200
#   ./install.sh --dry-run --prerelease
#
# Needs: curl or wget, awk, sha256sum or shasum, and one of unzip / python3 / bsdtar.
set -euo pipefail

# >>> bound by dist/dist.py when a release is published: edit <Product>/dist.json, not this block <<<
PRODUCT=''
# <<< end bound >>>

[ -n "$PRODUCT" ] || { echo "This copy of install.sh is not bound to a product. Use the one attached to a release." >&2; exit 1; }

VERSION=latest
PRERELEASE=0
DRY_RUN=0
SYSTEM=0
NO_LINK=0
INSTALL_DIR=''
REPO="$DEFAULT_REPO"
API_BASE='https://api.github.com'

die() { echo "error: $*" >&2; exit 1; }

usage() {
  cat <<USAGE
Usage: install.sh [options]
  --version <v>       install this version instead of the latest stable one
  --prerelease        let 'latest' pick a prerelease
  --dry-run           show what would be installed, change nothing
  --system            install for all users (/opt, /usr/local/bin); needs root
  --install-dir <d>   install into <d>
  --no-link           do not put the program on the PATH
  --repo <owner/repo> --api-base <url>
USAGE
}

while [ $# -gt 0 ]; do
  case "$1" in
    --version) [ $# -ge 2 ] || die "--version needs a value"; VERSION="$2"; shift 2 ;;
    --prerelease) PRERELEASE=1; shift ;;
    --dry-run) DRY_RUN=1; shift ;;
    --system) SYSTEM=1; shift ;;
    --install-dir) [ $# -ge 2 ] || die "--install-dir needs a value"; INSTALL_DIR="$2"; shift 2 ;;
    --no-link) NO_LINK=1; shift ;;
    --repo) [ $# -ge 2 ] || die "--repo needs a value"; REPO="$2"; shift 2 ;;
    --api-base) [ $# -ge 2 ] || die "--api-base needs a value"; API_BASE="$2"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; die "unknown option $1" ;;
  esac
done

fetch() { # fetch <url> [outfile]
  if command -v curl >/dev/null 2>&1; then
    if [ $# -ge 2 ]; then curl -fsSL -H "User-Agent: $PRODUCT-installer" -o "$2" "$1"; else curl -fsSL -H "User-Agent: $PRODUCT-installer" -H 'Accept: application/vnd.github+json' "$1"; fi
  elif command -v wget >/dev/null 2>&1; then
    if [ $# -ge 2 ]; then wget -q -U "$PRODUCT-installer" -O "$2" "$1"; else wget -q -U "$PRODUCT-installer" -O - "$1"; fi
  else
    die "curl or wget is required"
  fi
}

# --- Which package ---
case "$(uname -s)-$(uname -m)" in
  Linux-x86_64) RID=linux-x64 ;;
  Linux-aarch64|Linux-arm64) RID=linux-arm64 ;;
  Darwin-x86_64) RID=osx-x64 ;;
  Darwin-arm64) RID=osx-arm64 ;;
  *) die "unsupported platform $(uname -s) $(uname -m)" ;;
esac
EXE=''
for p in $PLATFORMS; do [ "${p%%=*}" = "$RID" ] && EXE="${p#*=}"; done
[ -n "$EXE" ] || die "$DISPLAY_NAME has no $RID package"

# --- Find the release ---
TAG_PREFIX="$PRODUCT/v"
WANTED="${VERSION#"$PRODUCT"/}"; WANTED="${WANTED#v}"
LISTING=''
for page in 1 2 3 4 5; do
  json="$(fetch "$API_BASE/repos/$REPO/releases?per_page=100&page=$page")"
  # One "R<TAB>tag<TAB>prerelease<TAB>draft" line per release, then "A<TAB>url" per asset (the API's JSON is pretty-printed, newest first).
  part="$(printf '%s\n' "$json" | awk '
    function flush() { if (tag != "" && !done) print "R\t" tag "\t" pre "\t" draft; done=1 }
    /^ *"tag_name":/ { flush(); done=0; tag=$0; sub(/^[^:]*: *"/, "", tag); sub(/".*$/, "", tag); pre="false"; draft="false"; next }
    /^ *"draft":/ { draft = ($0 ~ /true/) ? "true" : "false"; next }
    /^ *"prerelease":/ { pre = ($0 ~ /true/) ? "true" : "false"; next }
    /^ *"browser_download_url":/ { flush(); u=$0; sub(/^[^:]*: *"/, "", u); sub(/".*$/, "", u); print "A\t" u; next }
    END { flush() }')"
  LISTING="$LISTING$part"$'\n'
  [ "$(printf '%s\n' "$json" | grep -c '^ *"tag_name":' || true)" -lt 100 ] && break
done

RELEASE_TAG=''
RELEASE_PRE=false
ASSETS=''
current_ok=0
while IFS=$'\t' read -r kind a b c; do
  [ -n "${kind:-}" ] || continue
  if [ "$kind" = R ]; then
    [ -z "$RELEASE_TAG" ] || break          # the chosen release's assets are complete
    current_ok=0
    case "$a" in "$TAG_PREFIX"*) ;; *) continue ;; esac
    [ "$c" = true ] && continue
    if [ "$VERSION" = latest ]; then
      [ "$b" = true ] && [ "$PRERELEASE" = 0 ] && continue
    else
      [ "$a" = "$TAG_PREFIX$WANTED" ] || continue
    fi
    RELEASE_TAG="$a"; RELEASE_PRE="$b"; current_ok=1
  elif [ "$current_ok" = 1 ]; then
    ASSETS="$ASSETS$a"$'\n'
  fi
done <<< "$LISTING"

if [ -z "$RELEASE_TAG" ]; then
  if [ "$VERSION" = latest ]; then die "No $DISPLAY_NAME release was found in $REPO (prereleases need --prerelease)."; fi
  die "No $DISPLAY_NAME release $WANTED was found in $REPO."
fi
RELEASE_VERSION="${RELEASE_TAG#"$TAG_PREFIX"}"
ZIP_NAME="$PRODUCT-v$RELEASE_VERSION-$RID.zip"
ZIP_URL="$(printf '%s' "$ASSETS" | awk -v n="/$ZIP_NAME" 'index($0, n) && substr($0, length($0) - length(n) + 1) == n { print; exit }')"
SUMS_URL="$(printf '%s' "$ASSETS" | awk '/\/SHA256SUMS$/ { print; exit }')"
[ -n "$ZIP_URL" ] && [ -n "$SUMS_URL" ] || die "Release $RELEASE_TAG has no $RID package ($ZIP_NAME)."

if [ "$DRY_RUN" = 1 ]; then
  suffix=''; [ "$RELEASE_PRE" = true ] && suffix=' [prerelease]'
  echo "$DISPLAY_NAME $RELEASE_VERSION ($RELEASE_TAG)$suffix"
  echo "  package   $ZIP_URL"
  echo "  checksums $SUMS_URL"
  exit 0
fi

# --- Where ---
if [ "$SYSTEM" = 1 ]; then
  [ "$(id -u)" = 0 ] || die "--system needs root (try sudo)."
  [ -n "$INSTALL_DIR" ] || INSTALL_DIR="/opt/$PRODUCT"
  LINK_DIR=/usr/local/bin
else
  [ -n "$INSTALL_DIR" ] || INSTALL_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/$PRODUCT"
  LINK_DIR="$HOME/.local/bin"
fi
echo "Installing $DISPLAY_NAME $RELEASE_VERSION to $INSTALL_DIR"

# --- Download and verify ---
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
fetch "$ZIP_URL" "$WORK/$ZIP_NAME"
fetch "$SUMS_URL" "$WORK/SHA256SUMS"
EXPECTED="$(awk -v n="$ZIP_NAME" '{ f=$2; sub(/\r$/, "", f); sub(/^\*/, "", f); if (f == n) { print tolower($1); exit } }' "$WORK/SHA256SUMS")"
[ -n "$EXPECTED" ] || die "SHA256SUMS does not list the package."
if command -v sha256sum >/dev/null 2>&1; then ACTUAL="$(sha256sum "$WORK/$ZIP_NAME" | awk '{ print $1 }')"
elif command -v shasum >/dev/null 2>&1; then ACTUAL="$(shasum -a 256 "$WORK/$ZIP_NAME" | awk '{ print $1 }')"
else die "sha256sum or shasum is required"; fi
[ "$ACTUAL" = "$EXPECTED" ] || die "Checksum mismatch for $ZIP_NAME: expected $EXPECTED, got $ACTUAL."

mkdir "$WORK/files"
if command -v unzip >/dev/null 2>&1; then unzip -q "$WORK/$ZIP_NAME" -d "$WORK/files"
elif command -v bsdtar >/dev/null 2>&1; then bsdtar -xf "$WORK/$ZIP_NAME" -C "$WORK/files"
elif command -v python3 >/dev/null 2>&1; then python3 -I -c 'import sys, zipfile; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])' "$WORK/$ZIP_NAME" "$WORK/files"
else die "unzip, bsdtar or python3 is required to unpack the package"; fi

# --- Copy files, keeping the administrator's configuration ---
mkdir -p "$INSTALL_DIR"
( cd "$WORK/files" && find . -type f | sed 's|^\./||' ) | while IFS= read -r rel; do
  keep=0
  for k in $PRESERVE; do [ "$k" = "$rel" ] && [ -e "$INSTALL_DIR/$rel" ] && keep=1; done
  if [ "$keep" = 1 ]; then echo "Kept your existing $rel."; continue; fi
  mkdir -p "$INSTALL_DIR/$(dirname "$rel")"
  cp -f "$WORK/files/$rel" "$INSTALL_DIR/$rel"
done
chmod +x "$INSTALL_DIR/$EXE"

# --- On the PATH ---
if [ "$NO_LINK" = 0 ]; then
  mkdir -p "$LINK_DIR"
  ln -sf "$INSTALL_DIR/$EXE" "$LINK_DIR/$EXE"
  case ":$PATH:" in *":$LINK_DIR:"*) ;; *) echo "Note: $LINK_DIR is not on your PATH; add it to use '$EXE' directly." ;; esac
fi

echo
echo "Installed $DISPLAY_NAME $RELEASE_VERSION."
if [ -n "$REQUIRES_DOTNET" ]; then
  major="${REQUIRES_DOTNET%%.*}"
  if ! { command -v dotnet >/dev/null 2>&1 && dotnet --list-runtimes 2>/dev/null | grep -q "^Microsoft\.NETCore\.App $major\."; }; then
    echo "Warning: $DISPLAY_NAME needs the .NET $REQUIRES_DOTNET runtime, which was not found. Install it from https://dotnet.microsoft.com/download" >&2
  fi
fi
[ -z "$NOTES" ] || echo "$NOTES"
