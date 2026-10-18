#!/usr/bin/env bash
# Runs the unit tests against a DuckDB preview build instead of the DuckDB that DuckDB.NET ships.
#   scripts/test-duckdb-preview.sh [branch] [-- dotnet test arguments]
# TEST_PROJECT=tests/DbDataBuild.Tests.Conformance runs the real-engine suite instead (needs the engines up: scripts/test-engines.sh up, then its env).
# The branch is DuckDB's release branch (default v2.0-cyanoptera: DuckDB 2.0). The native library is downloaded from artifacts.duckdb.org into .build/duckdb-preview/
# (not committed) and copied over libduckdb in the test output, so no package changes. Preview builds move daily and are not production software: this tells you
# what a coming DuckDB release would break, nothing more. Linux x64 only so far. Results are in docs/research/duckdb-2.0/README.md.
set -euo pipefail
cd "$(dirname "$0")/.."
BRANCH=v2.0-cyanoptera
if [ $# -gt 0 ] && [ "$1" != "--" ]; then BRANCH="$1"; shift; fi
[ "${1:-}" = "--" ] && shift
case "$(uname -s)-$(uname -m)" in
  Linux-x86_64) PLATFORM=linux-amd64; RID=linux-x64; LIB=libduckdb.so ;;
  Linux-aarch64) PLATFORM=linux-arm64; RID=linux-arm64; LIB=libduckdb.so ;;
  *) echo "Only Linux is wired up here; download duckdb-shared-libs-<platform>.tar.gz from https://artifacts.duckdb.org/$BRANCH/ by hand." >&2; exit 1 ;;
esac
DIR=".build/duckdb-preview/$BRANCH"
mkdir -p "$DIR"
curl -fsSL --retry 6 --retry-delay 10 --retry-all-errors "https://artifacts.duckdb.org/$BRANCH/duckdb-shared-libs-$PLATFORM.tar.gz" -o "$DIR/libs.tar.gz"
tar xzf "$DIR/libs.tar.gz" -C "$DIR"
echo "preview library: $(strings -a "$DIR/$LIB" | grep -m1 -E '^v[0-9]+\.[0-9]+\.[0-9]+(-[a-z]+[0-9]*)?$' || echo unknown)  sha256 $(sha256sum "$DIR/$LIB" | cut -d' ' -f1)"
PROJECT="${TEST_PROJECT:-tests/DbDataBuild.Tests.Unit}"
dotnet build "$PROJECT"
cp "$DIR/$LIB" "$PROJECT/bin/Debug/net10.0/runtimes/$RID/native/$LIB"
dotnet test "$PROJECT" --no-build "$@"
