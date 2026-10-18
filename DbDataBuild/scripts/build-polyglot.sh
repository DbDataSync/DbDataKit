#!/usr/bin/env bash
# Builds polyglot-sql-ffi from the pinned commit and installs it under native/<rid>/ (git-ignored).
# Pinned commit must match PolyglotNative.PinnedCommit (a test enforces this). Needs git and cargo.
set -euo pipefail
PIN="0a7a1a77da6e3d5169119e133d4d608d74a5c9e1"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="${POLYGLOT_WORKDIR:-$ROOT/.build/polyglot}"

# TARGET_RID=win-x64 on Linux cross-compiles the Windows DLL with MinGW (needs: rustup target add x86_64-pc-windows-gnu, and x86_64-w64-mingw32-gcc).
# The DLL links the runtime statically and imports only Windows system DLLs, so nothing from MinGW has to ship with it.
TARGET=""
case "${TARGET_RID:-$(uname -s)}" in
  win-x64) RID=win-x64; LIB=polyglot_sql_ffi.dll; TARGET="x86_64-pc-windows-gnu" ;;
  Linux)  RID=linux-x64;  LIB=libpolyglot_sql_ffi.so ;;
  Darwin) RID=osx-arm64;  LIB=libpolyglot_sql_ffi.dylib ;;
  MINGW*|MSYS*|CYGWIN*) RID=win-x64; LIB=polyglot_sql_ffi.dll ;;
  *) echo "unsupported OS or TARGET_RID" >&2; exit 1 ;;
esac

if [ ! -d "$WORK/.git" ]; then
  git clone --quiet https://github.com/tobilg/polyglot "$WORK"
fi
git -C "$WORK" fetch --quiet origin "$PIN" 2>/dev/null || true
git -C "$WORK" checkout --quiet "$PIN"

# function-catalog-duckdb enables DuckDB function semantic validation.
( cd "$WORK" && cargo build -p polyglot-sql-ffi --profile ffi_release --features function-catalog-duckdb --locked ${TARGET:+--target "$TARGET"} )
OUT="$WORK/target${TARGET:+/$TARGET}/ffi_release"

mkdir -p "$ROOT/native/$RID"
cp "$OUT/$LIB" "$ROOT/native/$RID/$LIB"
echo "Installed $ROOT/native/$RID/$LIB (polyglot-sql-ffi @ $PIN)"
