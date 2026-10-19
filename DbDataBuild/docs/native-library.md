# The native SQL library

DbDataBuild parses and transpiles SQL with [polyglot-sql](https://github.com/tobilg/polyglot), a Rust library, through its C FFI build
(`polyglot_sql_ffi`: `libpolyglot_sql_ffi.so` on Linux, `polyglot_sql_ffi.dll` on Windows), bound with P/Invoke in `DbDataBuild.Sql`. The
binding uses twelve entry points (`polyglot_parse`, `_generate`, `_transpile_with_options`, `_validate_with_options`, `_analyze_query`, `_format`,
`_output_columns`, `_dialect_list`, `_version` and the three `_free_*`); all are exported by the published Linux and Windows libraries.

## Where it comes from

**The library the polyglot project publishes, unmodified.** Each tagged release carries `polyglot-sql-ffi-<platform>` archives
(linux x86_64 and aarch64, macOS, windows x86_64) and a `checksums.sha256`. `scripts/polyglot.pin` records the release, the commit it was tagged
at, and the SHA-256 of the two archives DbDataBuild uses; `scripts/fetch-native.sh` downloads them into `native/<rid>/` and refuses an archive that
does not match the hash in this repository (not one fetched from the same release). CI, the release workflow and developers all use that script;
`native/` is not committed. The archives are large (about 150 MB each, mostly a static library DbDataBuild does not use); only the shared library
is kept.

## Why not our own build

Until October 2026 we built the library ourselves (a pinned commit, `scripts/build-polyglot.sh`, a native release in the old repository). That was
checked on 2026-10-19 against what the project publishes, and none of its three reasons holds:

| Reason for building | Finding |
|---|---|
| The pin was an untagged commit (between v0.13.1 and v0.13.2) and no release had FFI binaries for it. | Tagged releases now publish them. The pin moved to the release v0.13.3. |
| The build enabled `function-catalog-duckdb`. | That feature only feeds `validate_with_schema` with `check_types`, which DbDataBuild does not call (it imports `validate_with_options`, never the schema variant). The published Linux library is the same size (52 MB) and has every entry point the binding needs. |
| The Windows DLL was cross-built with MinGW so it imports only Windows system DLLs. | **A real difference**, accepted: the published DLL is built with MSVC and imports `VCRUNTIME140.dll`, so a Windows machine needs the Microsoft Visual C++ 2015-2022 Redistributable (x64). Most have it; a fresh install or Server Core may not. DuckDB's DLL does not need it, so this is a new requirement. `install.ps1` warns when it is missing and the Scoop manifest suggests `extras/vcredist2022`. |

Linux is otherwise identical: the same glibc floor (2.34) and the same dependencies (`libc`, `libgcc_s`).

## What moving from the old pin changed

v0.13.3 is six commits past the old pin. They change the DuckDB generator: integer division (`//`), `ORDER BY ALL` and the precedence of `%`. With the
new library the unit suite passes unchanged (2,762 tests) and the set of AST node tags (`expression-types.txt`) is identical. The engine
conformance suites run against it before a release.

## Moving to a newer release

1. In a polyglot checkout: `git rev-parse vX.Y.Z^{commit}`. Take the two archive hashes from the release's `checksums.sha256`.
2. Edit `scripts/polyglot.pin` and `PolyglotNative.PinnedCommit` (a unit test ties them together).
3. `scripts/fetch-native.sh all`, then `scripts/gen-ast-types.sh <polyglot checkout at the commit>` (rewrites the pin line of
   `expression-types.txt` and adds any new node tags).
4. Run the unit suite, regenerate goldens for anything the generator changed, and run the conformance suites.

## Not covered

* The Windows DLL is exercised by the Windows unit job in CI, not by the Wine harness (`scripts/test-windows-wine.sh`), which has not been re-run since the change.
* Only linux-x64 and win-x64 are used; upstream also publishes linux aarch64 and macOS builds, which could be added to `fetch-native.sh` with a platform.
