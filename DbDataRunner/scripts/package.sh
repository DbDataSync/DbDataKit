#!/usr/bin/env bash
# The runner's release package for one platform: dbdatarunner-v<version>-<rid>.zip, the program files at the zip root (dist/README.md).
#   DbDataRunner/scripts/package.sh <rid> <version> [outdir]      rid: win-x64 or linux-x64; outdir defaults to DbDataRunner/artifacts/dist
# Framework-dependent (it needs the .NET 10 runtime, `requiresDotnet` in dist.json), so every platform builds on one Linux machine. The task host the runner starts for a
# .NET package is published beside the program, in taskhost/ (the server project's targets do it); it is started through `dotnet` (its .dll), so no apphost of the build
# machine's kind is needed. Plan 28a R27.
set -euo pipefail
RID="${1:?usage: package.sh <rid> <version> [outdir]}"
VERSION="${2:?usage: package.sh <rid> <version> [outdir]}"
cd "$(dirname "$0")/.."
OUT="$(mkdir -p "${3:-artifacts/dist}" && cd "${3:-artifacts/dist}" && pwd)"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

dotnet publish src/DbDataRunner.Server -c Release -r "$RID" --self-contained false \
  -p:Version="${VERSION%%-*}" -p:InformationalVersion="$VERSION" \
  -p:DebugType=none -p:DebugSymbols=false -o "$STAGE"
[ -f "$STAGE/taskhost/dbdatakit-taskhost.dll" ] || { echo "the package has no task host (taskhost/dbdatakit-taskhost.dll)" >&2; exit 1; }
case "$RID" in
  win-*) [ -f "$STAGE/dbdatarunner.exe" ] || { echo "the package has no dbdatarunner.exe" >&2; exit 1; } ;;
  *)     [ -f "$STAGE/dbdatarunner" ] || { echo "the package has no dbdatarunner" >&2; exit 1; } ;;
esac
rm -f "$STAGE/web.config"
cp ../LICENSE README.md "$STAGE/"

zip="$OUT/dbdatarunner-v$VERSION-$RID.zip"
rm -f "$zip"
(cd "$STAGE" && zip -qr "$zip" .)
ls -l "$zip"
