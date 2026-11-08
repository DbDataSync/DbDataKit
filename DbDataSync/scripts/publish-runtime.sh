#!/usr/bin/env bash
# The Sync runtime package for a platform (plan 28c E06): the engine (the TaskRunner and its project closure) as files a runner stores by hash, and the manifest that says how to start it.
#   scripts/publish-runtime.sh [rid] [outdir]     rid is win-x64 or linux-x64 (default linux-x64), outdir defaults to ./publish/runtime-<rid>
# Writes <outdir>/package.yaml, <outdir>/files/bin/... and <outdir>/sizes.txt, then checks the sizes (Build's scripts/runtime-sizes.sh, the one size report of the runtime packages: a file above 400 MiB fails).
# Install for a try:   dbdatarunner package install <outdir>/package.yaml <outdir>/files --channel <channel>
# Not in it, by design: the client libraries (Npgsql, SqlClient, ... are excluded from the build; a definition package carries them in its `repo/libraries`, restored at its publish), JDBC jars, customer drivers, ODBC.
# VERSION=1.2.3 stamps the build and the manifest; otherwise the version of src/DbDataSync.Cli (the release version).
set -euo pipefail
cd "$(dirname "$0")/.."
RID="${1:-linux-x64}"
OUT="${2:-publish/runtime-$RID}"
VERSION="${VERSION:-$(dotnet msbuild src/DbDataSync.Cli -getProperty:Version -nologo | tr -d '\r\n ')}"
# the manifest first: it refuses a platform that is not published, before anything is built
MANIFEST="$(scripts/runtime-manifest.sh "$RID" "$VERSION")"
rm -rf "$OUT"
mkdir -p "$OUT/files/bin"
dotnet publish src/DbDataSync.TaskRunner -c Release -r "$RID" --no-self-contained \
  -p:Version="${VERSION%%[-+]*}" -p:DebugType=none -p:DebugSymbols=false -o "$OUT/files/bin"
printf '%s\n' "$MANIFEST" > "$OUT/package.yaml"
../DbDataBuild/scripts/runtime-sizes.sh "$OUT/files" 400 | tee "$OUT/sizes.txt"
echo "The runtime package is in $OUT: package.yaml and files/."
