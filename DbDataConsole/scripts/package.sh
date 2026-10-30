#!/usr/bin/env bash
# The console's release package for one platform: dbdatakit-v<version>-<rid>.zip, the program files at the zip root (dist/README.md).
#   DbDataConsole/scripts/package.sh <rid> <version> [outdir]      rid: win-x64 or linux-x64; outdir defaults to DbDataConsole/artifacts/dist
# Framework-dependent (it needs the .NET 10 runtime, `requiresDotnet` in dist.json), so every platform builds on one Linux machine. The web app is built
# into wwwroot by the publish unless SKIP_WEB=1 says DbDataKit/web/console/dist is already built.
set -euo pipefail
RID="${1:?usage: package.sh <rid> <version> [outdir]}"
VERSION="${2:?usage: package.sh <rid> <version> [outdir]}"
cd "$(dirname "$0")/.."
OUT="$(mkdir -p "${3:-artifacts/dist}" && cd "${3:-artifacts/dist}" && pwd)"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

dotnet publish src/DbDataConsole.Server -c Release -r "$RID" --self-contained false \
  ${SKIP_WEB:+-p:SkipWebBuild=true} -p:Version="${VERSION%%-*}" -p:InformationalVersion="$VERSION" \
  -p:DebugType=none -p:DebugSymbols=false -o "$STAGE"
if [ -n "${SKIP_WEB:-}" ]; then
  cp -r ../DbDataKit/web/console/dist "$STAGE/wwwroot"
fi
[ -f "$STAGE/wwwroot/index.html" ] || { echo "the package has no web app (wwwroot/index.html)" >&2; exit 1; }
rm -f "$STAGE/web.config"
cp ../LICENSE README.md "$STAGE/"

zip="$OUT/dbdatakit-v$VERSION-$RID.zip"
rm -f "$zip"
(cd "$STAGE" && zip -qr "$zip" .)
ls -l "$zip"
