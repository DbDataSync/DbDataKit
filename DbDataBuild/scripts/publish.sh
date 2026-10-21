#!/usr/bin/env bash
# One self-contained file for a platform: the .NET runtime, the tool, and the native libraries (polyglot, DuckDB), which the runtime
# unpacks on first start into DOTNET_BUNDLE_EXTRACT_BASE_DIR (default ~/.net).
#   scripts/publish.sh [rid] [outdir]      rid defaults to linux-x64, outdir to ./publish/<rid>
# The polyglot library for the rid must exist in native/<rid>/ (scripts/fetch-native.sh); the DuckDB engine is fetched from the repository's pin. VERSION=1.2.3 stamps the build.
set -euo pipefail
cd "$(dirname "$0")/.."
RID="${1:-linux-x64}"
OUT="${2:-publish/$RID}"
[ -d "native/$RID" ] || { echo "native/$RID is missing: run scripts/fetch-native.sh $RID first." >&2; exit 1; }
# The DuckDB engine of the pin (duckdb.pin at the repository root), which the publish bundles with the executable.
../scripts/fetch-duckdb.sh "$RID"
dotnet publish src/DbDataBuild.Cli -c Release -r "$RID" --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true \
  ${VERSION:+-p:Version=$VERSION} -p:DebugType=none -p:DebugSymbols=false -o "$OUT"
ls -l "$OUT"
