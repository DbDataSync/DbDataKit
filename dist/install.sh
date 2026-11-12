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
ANNOUNCE_URL=
BUNDLE_TOKEN_FILE=
SYSTEMD_DIR=''
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
  --announce-url <u>  the console's announce address (http://console:8481). With --system (root) it writes the systemd drop-in that sets DBDATAKIT_ANNOUNCE_URL for the
                      unit named for the product (/etc/systemd/system/<product>.service.d/announce.conf); otherwise it prints the line the service needs
  --bundle-token-file <f>  with --announce-url, for a console on this machine: the bundle token file (made by `dbdatakit bundle init`); the drop-in also sets
                      DBDATAKIT_BUNDLE_TOKEN_FILE, and the console pairs with this server by itself when it announces. Give the service's account read access to the file (it is mode 600)
  --systemd-dir <d>   where the drop-in goes instead of /etc/systemd/system (writes it without --system; systemd is not asked to reload)
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
    --announce-url) [ $# -ge 2 ] || die "--announce-url needs a value"; ANNOUNCE_URL="$2"; shift 2 ;;
    --bundle-token-file) [ $# -ge 2 ] || die "--bundle-token-file needs a value"; BUNDLE_TOKEN_FILE="$2"; shift 2 ;;
    --systemd-dir) [ $# -ge 2 ] || die "--systemd-dir needs a value"; SYSTEMD_DIR="$2"; shift 2 ;;
    --repo) [ $# -ge 2 ] || die "--repo needs a value"; REPO="$2"; shift 2 ;;
    --api-base) [ $# -ge 2 ] || die "--api-base needs a value"; API_BASE="$2"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; die "unknown option $1" ;;
  esac
done

if [ -n "$ANNOUNCE_URL" ]; then
  # the address goes into a unit file: an http(s) address with nothing that systemd would read as syntax (white space, quotes, backslash, % and $)
  case "$ANNOUNCE_URL" in http://?*|https://?*) ;; *) die "--announce-url is the console's http or https announce address, such as http://console:8481." ;; esac
  case "$ANNOUNCE_URL" in *[[:space:]\"\'\\%\$]*) die "--announce-url must not contain white space, quotes, backslash, % or \$." ;; esac
fi

if [ -n "$BUNDLE_TOKEN_FILE" ]; then
  [ -n "$ANNOUNCE_URL" ] || die "--bundle-token-file goes with --announce-url: the token is what the announcement carries to a console on this machine."
  case "$BUNDLE_TOKEN_FILE" in /?*) ;; *) die "--bundle-token-file is the absolute path of the token file." ;; esac
  case "$BUNDLE_TOKEN_FILE" in *[[:space:]\"\'\\%\$]*) die "--bundle-token-file must not contain white space, quotes, backslash, % or \$." ;; esac
fi

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
  # The API's JSON may or may not be pretty-printed, so start a line at every key (a quote inside a string is escaped, so `,"` and `{"` only
  # occur between tokens).
  json="$(fetch "$API_BASE/repos/$REPO/releases?per_page=100&page=$page" | sed 's/\([,{[]\)"/\1\n"/g')"
  # One "R<TAB>tag<TAB>prerelease<TAB>draft" line per release, then "A<TAB>url" per asset (newest first).
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
if [ -n "$ANNOUNCE_URL" ]; then
  if [ "$ANNOUNCES" != 1 ]; then
    echo "Note: $DISPLAY_NAME does not announce itself to a console; --announce-url is ignored." >&2
  elif [ "$SYSTEM" = 1 ] || [ -n "$SYSTEMD_DIR" ]; then
    # The unit is named for the product (`<program> service unit` prints it); a drop-in is read whether the unit exists yet or not, and survives a new unit file.
    DROPIN_ROOT="${SYSTEMD_DIR:-/etc/systemd/system}"
    DROPIN_DIR="$DROPIN_ROOT/$PRODUCT.service.d"
    mkdir -p "$DROPIN_DIR"
    {
      echo "# Written by install.sh --announce-url: $DISPLAY_NAME announces itself to the console at this address and waits in 'dbdatakit instance pending' until an administrator confirms it."
      echo "[Service]"
      echo "Environment=\"DBDATAKIT_ANNOUNCE_URL=$ANNOUNCE_URL\""
      [ -z "$BUNDLE_TOKEN_FILE" ] || echo "Environment=\"DBDATAKIT_BUNDLE_TOKEN_FILE=$BUNDLE_TOKEN_FILE\""
    } > "$DROPIN_DIR/announce.conf"
    chmod 644 "$DROPIN_DIR/announce.conf"
    echo "Wrote $DROPIN_DIR/announce.conf: the $PRODUCT service announces itself to the console at $ANNOUNCE_URL and then waits in 'dbdatakit instance pending'."
    [ -z "$BUNDLE_TOKEN_FILE" ] || echo "It also holds the bundle token file $BUNDLE_TOKEN_FILE: a console on this machine with a bundle pairs with the service by itself (the service's account must be able to read that file)."
    if [ -z "$SYSTEMD_DIR" ] && [ -d /run/systemd/system ] && command -v systemctl >/dev/null 2>&1; then
      systemctl daemon-reload || echo "Note: systemctl daemon-reload failed; run it before starting the service." >&2
      echo "Restart the service if it is running: systemctl restart $PRODUCT"
    fi
  else
    echo "To announce to the console at $ANNOUNCE_URL (it then waits in 'dbdatakit instance pending'), run the server with DBDATAKIT_ANNOUNCE_URL=$ANNOUNCE_URL (in a systemd unit: Environment=DBDATAKIT_ANNOUNCE_URL=$ANNOUNCE_URL). A system install (--system, as root) writes that as a drop-in for the $PRODUCT service."
  fi
fi
