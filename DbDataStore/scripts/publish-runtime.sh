#!/usr/bin/env bash
# The Store runtime package for a platform (plan 33 SC24): the dbdatastore program as files a runner stores by hash, and the manifest that says how to start it (`dbdatastore --runner`).
#   scripts/publish-runtime.sh [rid] [outdir]     rid is win-x64 or linux-x64 (default linux-x64), outdir defaults to ./publish/runtime-<rid>
# Writes <outdir>/package.yaml and <outdir>/files/bin/..., then checks the sizes (Build's scripts/runtime-sizes.sh: a file above 400 MiB fails).
# Install for a try:   dbdatarunner package install <outdir>/package.yaml <outdir>/files --channel <channel>
# VERSION=1.2.3 stamps the build and the manifest; otherwise the version of src/DbDataStore.Server.
set -euo pipefail
cd "$(dirname "$0")/.."
RID="${1:-linux-x64}"
OUT="${2:-publish/runtime-$RID}"
VERSION="${VERSION:-$(dotnet msbuild src/DbDataStore.Server -getProperty:Version -nologo | tr -d '\r\n ')}"
MANIFEST="$(scripts/runtime-manifest.sh "$RID" "$VERSION")"
rm -rf "$OUT"
mkdir -p "$OUT/files/bin"
dotnet publish src/DbDataStore.Server -c Release -r "$RID" --no-self-contained \
  -p:Version="${VERSION%%[-+]*}" -p:DebugType=none -p:DebugSymbols=false -o "$OUT/files/bin"
printf '%s\n' "$MANIFEST" > "$OUT/package.yaml"
../DbDataBuild/scripts/runtime-sizes.sh "$OUT/files" 400 | tee "$OUT/sizes.txt"
echo "The runtime package is in $OUT: package.yaml and files/."
