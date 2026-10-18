#!/usr/bin/env bash
# Runs the unit tests with Windows semantics on Linux, under Wine: the Windows .NET runtime and xunit's console runner run the test assembly, which loads the win-x64
# native libraries (DuckDB, the polyglot DLL). Catches what differs on Windows (line endings, path separators, file sharing, native loading) without a Windows machine.
#   scripts/test-windows-wine.sh [xunit.console arguments, e.g. -method "Namespace.Class.Method"]
# TEST_PROJECT=DbDataBuild.Tests.Conformance runs the real-engine suite instead (engines up and their env set, as for `dotnet test`: the Wine process reaches them on localhost).
# Needs: wine, a MinGW-built native/win-x64/polyglot_sql_ffi.dll (TARGET_RID=win-x64 scripts/build-polyglot.sh), network for the first run (the Windows .NET runtime and
# the xunit runner are downloaded into .build/wine-tools). Known limits, all of Wine or of this harness, not of the product:
#   - single-file .NET apps do not start under Wine 9 ("Incorrect alignment"), so this runs the tests, not the published executable (publish without PublishSingleFile runs);
#   - nothing here talks to a database, and the terminal interface is not exercised.
set -euo pipefail
cd "$(dirname "$0")/.."
TOOLS=".build/wine-tools"
mkdir -p "$TOOLS"
[ -f native/win-x64/polyglot_sql_ffi.dll ] || { echo "native/win-x64/polyglot_sql_ffi.dll is missing: TARGET_RID=win-x64 scripts/build-polyglot.sh" >&2; exit 1; }
command -v wine >/dev/null || { echo "wine is not installed" >&2; exit 1; }

if [ ! -f "$TOOLS/dotnet/dotnet.exe" ]; then
  V=$(curl -fsSL https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json | python3 -c "import json,sys; print(json.load(sys.stdin)['latest-runtime'])")
  curl -fsSL --retry 5 "https://builds.dotnet.microsoft.com/dotnet/Runtime/$V/dotnet-runtime-$V-win-x64.zip" -o "$TOOLS/runtime.zip"
  python3 -c "import zipfile; zipfile.ZipFile('$TOOLS/runtime.zip').extractall('$TOOLS/dotnet')"
fi
if [ ! -d "$TOOLS/xunit" ]; then
  curl -fsSL --retry 5 https://www.nuget.org/api/v2/package/xunit.runner.console/2.9.3 -o "$TOOLS/xunit.nupkg"
  python3 -c "import zipfile; zipfile.ZipFile('$TOOLS/xunit.nupkg').extractall('$TOOLS/xunit')"
fi

PROJECT="${TEST_PROJECT:-DbDataBuild.Tests.Unit}"
dotnet build "tests/$PROJECT" >/dev/null
OUT="tests/$PROJECT/bin/wine"        # inside the repository tree: the tests find the repository root by walking up from their own folder
rm -rf "$OUT"
cp -r "tests/$PROJECT/bin/Debug/net10.0" "$OUT"
cp -rf "$TOOLS/xunit/tools/net6.0/"* "$OUT/"
cp "$OUT"/runtimes/win-x64/native/*.dll "$OUT/"     # the runner's load context does not probe runtimes/<rid>/native
# nor runtimes/win/lib: SqlClient ships a reference assembly at the top and the real Windows one there ("not supported on this platform" otherwise)
for lib in "$OUT"/runtimes/win/lib/*/; do cp -f "$lib"*.dll "$OUT/"; done

export WINEPREFIX="$PWD/$TOOLS/prefix" WINEDEBUG=-all DISPLAY= DOTNET_ROLL_FORWARD=Major
cd "$OUT"
wine "$OLDPWD/$TOOLS/dotnet/dotnet.exe" xunit.console.dll "$PROJECT.dll" -noshadow -parallel none "$@"
