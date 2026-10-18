#!/usr/bin/env bash
# An MCP bundle (.mcpb: a zip with a manifest and the executable) for hosts that install servers with one click.
#   scripts/pack-mcpb.sh [rid] [dir with the published executable] [outdir]
# rid defaults to linux-x64 (win-x64 gives a bundle for Windows); the executable comes from scripts/publish.sh (default dir publish/<rid>); VERSION=1.2.3 stamps the manifest (default 0.0.0-dev).
# The bundle is read-only: it starts `dbdatabuild ui mcp` without --allow-writes and --allow-apply, and asks for the project folder. `npx @anthropic-ai/mcpb validate` checks the manifest.
set -euo pipefail
cd "$(dirname "$0")/.."
RID="${1:-linux-x64}"
EXE_DIR="${2:-publish/$RID}"
OUT="${3:-.}"
VERSION="${VERSION:-0.0.0-dev}"
case "$RID" in linux-*) PLATFORM=linux; EXE=dbdatabuild ;; win-*) PLATFORM=win32; EXE=dbdatabuild.exe ;; osx-*) PLATFORM=darwin; EXE=dbdatabuild ;; *) echo "unknown rid $RID" >&2; exit 1 ;; esac
[ -f "$EXE_DIR/$EXE" ] || { echo "$EXE_DIR/$EXE is missing: run scripts/publish.sh $RID first." >&2; exit 1; }
STAGE="$(mktemp -d)"; trap 'rm -rf "$STAGE"' EXIT
mkdir "$STAGE/server"
sed -e "s/@VERSION@/${VERSION#v}/" -e "s/@PLATFORM@/$PLATFORM/" packaging/mcpb/manifest.json > "$STAGE/manifest.json"
cp "$EXE_DIR/$EXE" "$STAGE/server/$EXE"; chmod +x "$STAGE/server/$EXE"
cp LICENSE THIRD-PARTY-NOTICES.md "$STAGE/"
NAME="dbdatabuild-${VERSION#v}-$RID.mcpb"
mkdir -p "$OUT"; OUT="$(cd "$OUT" && pwd)"; rm -f "$OUT/$NAME"
if command -v zip >/dev/null; then (cd "$STAGE" && zip -qr "$OUT/$NAME" .); else (cd "$STAGE" && 7z a -tzip "$OUT/$NAME" . >/dev/null); fi
echo "$OUT/$NAME"
