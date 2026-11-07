#!/usr/bin/env bash
# The Build runtime package for a platform (plan 28c E05): the engine as files a runner stores by hash, and the manifest that says how to start it.
#   scripts/publish-runtime.sh [rid] [outdir] [--framework-dependent]     rid is win-x64 or linux-x64 (default linux-x64), outdir defaults to ./publish/runtime-<rid>
# Writes <outdir>/package.yaml, <outdir>/files/bin/... and <outdir>/sizes.txt, then checks the sizes (scripts/runtime-sizes.sh: a file above 400 MiB fails the script). Install for a try:
#   dbdatarunner package install <outdir>/package.yaml <outdir>/files --channel <channel>
# The default layout is the single file of scripts/publish.sh (about 285 MB; it unpacks its natives on first start, into the directory the runner gives it). --framework-dependent
# is the fallback if a file is ever too big for the cache: the program and its natives as separate files, started through the dotnet host of the runner; it needs views made of copies.
# VERSION=1.2.3 stamps the build and the manifest; otherwise the version of Directory.Build.props.
set -euo pipefail
cd "$(dirname "$0")/.."
RID=linux-x64
OUT=""
FD=""
POS=0
for arg in "$@"; do
  case "$arg" in
    --framework-dependent) FD="--framework-dependent" ;;
    -*) echo "publish-runtime: unknown option '$arg'." >&2; exit 2 ;;
    *) POS=$((POS + 1)); if [ "$POS" = 1 ]; then RID="$arg"; elif [ "$POS" = 2 ]; then OUT="$arg"; else echo "publish-runtime: too many arguments." >&2; exit 2; fi ;;
  esac
done
OUT="${OUT:-publish/runtime-$RID}"
VERSION="${VERSION:-$(dotnet msbuild src/DbDataBuild.Cli -getProperty:Version -nologo | tr -d '\r\n ')}"
# the manifest first: it refuses a platform that is not published, before anything is built
MANIFEST="$(scripts/runtime-manifest.sh "$RID" "$VERSION" $FD)"
rm -rf "$OUT"
mkdir -p "$OUT/files/bin"
if [ -z "$FD" ]; then
  VERSION="$VERSION" scripts/publish.sh "$RID" "$OUT/files/bin"
else
  [ -d "native/$RID" ] || { echo "native/$RID is missing: run scripts/fetch-native.sh $RID first." >&2; exit 1; }
  ../scripts/fetch-duckdb.sh "$RID"
  dotnet publish src/DbDataBuild.Cli -c Release -r "$RID" --no-self-contained -p:PublishSingleFile=false \
    -p:Version="${VERSION}" -p:DebugType=none -p:DebugSymbols=false -o "$OUT/files/bin"
fi
printf '%s\n' "$MANIFEST" > "$OUT/package.yaml"
scripts/runtime-sizes.sh "$OUT/files" 400 | tee "$OUT/sizes.txt"
echo "The runtime package is in $OUT: package.yaml and files/."
