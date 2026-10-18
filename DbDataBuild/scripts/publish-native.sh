#!/usr/bin/env bash
# Publishes the libraries in native/<rid>/ as the `native-<pin>` release that scripts/fetch-native.sh downloads.
# Use after scripts/build-polyglot.sh (once per rid) for a new pin; the "native" workflow does the same on GitHub's runners.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PIN="$(sed -n 's/^PIN="\([0-9a-f]\{40\}\)"$/\1/p' "$ROOT/scripts/build-polyglot.sh")"
REPO="${DBDATABUILD_REPO:-danshryock/DbDataBuild}"
TAG="native-${PIN:0:8}"
STAGE="$(mktemp -d)"; trap 'rm -rf "$STAGE"' EXIT
for pair in linux-x64:libpolyglot_sql_ffi.so win-x64:polyglot_sql_ffi.dll; do
  rid="${pair%%:*}"; lib="${pair#*:}"
  [ -f "$ROOT/native/$rid/$lib" ] || { echo "native/$rid/$lib is missing" >&2; exit 1; }
  cp "$ROOT/native/$rid/$lib" "$STAGE/$rid-$lib"
done
( cd "$STAGE" && sha256sum * > SHA256SUMS && cat SHA256SUMS )
gh release create "$TAG" "$STAGE"/* --repo "$REPO" --latest=false --title "polyglot-sql-ffi @ ${PIN:0:8}" --notes "Prebuilt polyglot-sql-ffi (tobilg/polyglot @ $PIN, features: function-catalog-duckdb, profile ffi_release) for scripts/fetch-native.sh. linux-x64 needs glibc 2.34 or newer; win-x64 is a MinGW build that imports only Windows system DLLs. MIT licensed (see THIRD-PARTY-NOTICES.md)."
