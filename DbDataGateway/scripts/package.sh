#!/usr/bin/env bash
# The gateway's Linux release package: dbdatagateway-v<version>-linux-x64.zip, the program files at the zip root (dist/README.md).
#   DbDataGateway/scripts/package.sh <rid> <version> [outdir]      rid: linux-x64; outdir defaults to DbDataGateway/artifacts/dist
# Framework-dependent, like the Runner's (it needs the .NET 10 runtime, `requiresDotnet` in dist.json). The Windows zip stays build/package.ps1 (self-contained, with the
# 32-bit ODBC worker); this one carries no worker: `Bitness: x86` sources, Analysis Services and Windows password sign-in are Windows-only and a Linux server refuses
# a configuration that asks for them (plan 33 SC01). The DuckDB engine of the pin (libduckdb.so) and the views runtime (apps-views/) are part of the package.
set -euo pipefail
RID="${1:?usage: package.sh <rid> <version> [outdir]}"
VERSION="${2:?usage: package.sh <rid> <version> [outdir]}"
case "$RID" in
  linux-x64) ;;
  *) echo "package.sh builds the Linux package; the Windows one is build/package.ps1 (got '$RID')" >&2; exit 1 ;;
esac
cd "$(dirname "$0")/.."
OUT="$(mkdir -p "${3:-artifacts/dist}" && cd "${3:-artifacts/dist}" && pwd)"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

../scripts/fetch-duckdb.sh "$RID"

dotnet publish src/DbDataGateway.Server -c Release -r "$RID" --self-contained false -p:BuildViewsRuntime=true \
  -p:Version="${VERSION%%-*}" -p:InformationalVersion="$VERSION" \
  -p:DebugType=none -p:DebugSymbols=false -o "$STAGE"
[ -f "$STAGE/dbdatagateway" ] || { echo "the package has no dbdatagateway" >&2; exit 1; }
[ -f "$STAGE/libduckdb.so" ] || { echo "libduckdb.so is not in the published folder: the DuckDB engine would be missing from the package" >&2; exit 1; }
[ -d "$STAGE/apps-views" ] || { echo "the package has no apps-views (the views runtime)" >&2; exit 1; }
if find "$STAGE" -iname 'DbDataGateway.Worker*' -o -iname 'dbdatagateway-worker*' | grep -q .; then
  echo "the Linux package must not carry the 32-bit worker" >&2; exit 1
fi
rm -f "$STAGE/web.config"
cp LICENSE README.md "$STAGE/"

zip="$OUT/dbdatagateway-v$VERSION-$RID.zip"
rm -f "$zip"
(cd "$STAGE" && zip -qr "$zip" .)
ls -l "$zip"
