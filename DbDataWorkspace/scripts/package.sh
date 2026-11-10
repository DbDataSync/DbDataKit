#!/usr/bin/env bash
# The server's release package for one platform: dbdataworkspace-v<version>-<rid>.zip, the program files at the zip root (dist/README.md).
#   DbDataWorkspace/scripts/package.sh <rid> <version> [outdir]      rid: win-x64 or linux-x64; outdir defaults to DbDataWorkspace/artifacts/dist
# Framework-dependent (it needs the .NET 10 runtime, `requiresDotnet` in dist.json), so every platform builds on one Linux machine. Plan 33 SC17.
set -euo pipefail
RID="${1:?usage: package.sh <rid> <version> [outdir]}"
VERSION="${2:?usage: package.sh <rid> <version> [outdir]}"
cd "$(dirname "$0")/.."
OUT="$(mkdir -p "${3:-artifacts/dist}" && cd "${3:-artifacts/dist}" && pwd)"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

dotnet publish src/DbDataWorkspace.Server -c Release -r "$RID" --self-contained false \
  -p:Version="${VERSION%%-*}" -p:InformationalVersion="$VERSION" \
  -p:DebugType=none -p:DebugSymbols=false -o "$STAGE"
case "$RID" in
  win-*) [ -f "$STAGE/dbdataworkspace.exe" ] || { echo "the package has no dbdataworkspace.exe" >&2; exit 1; } ;;
  *)     [ -f "$STAGE/dbdataworkspace" ] || { echo "the package has no dbdataworkspace" >&2; exit 1; } ;;
esac
rm -f "$STAGE/web.config"
cp ../LICENSE README.md "$STAGE/"

zip="$OUT/dbdataworkspace-v$VERSION-$RID.zip"
rm -f "$zip"
(cd "$STAGE" && zip -qr "$zip" .)
ls -l "$zip"
