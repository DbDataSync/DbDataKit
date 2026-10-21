# Plan 13a: the slices of plan 13

Status: **proposed** (2026-10-21). This breaks [plan 13](13-suite-standardization.md) into pull requests. The workstream sections of plan
13 say *why*; this says *in what pieces*.

## Rules for every slice

- **One pull request, small.** Most slices are S (a day or two); none is meant to be more than about three days. A slice that grows is
  split before it merges, not after.
- **Done means its test passes.** The "Done when" column is the test, run in CI. A slice with no automatic test says what is checked by
  hand and why.
- **CI green and independently revertable.** Each slice merges into `main` on its own (plan 09's trunk model, rebase merge). Nothing in
  the middle of a sequence leaves `main` broken or a product unshippable.
- **Adapter first, delete second** (plan 10): a slice that replaces code keeps the old tests passing unchanged; deleting the old code is its
  own slice, or the last step of the same one when the tests already pass.
- **Behaviour changes are named** in the pull request and in the product's release notes (breaking changes are allowed, but never silent).
- **Clean break** (plan 13 section 3): no slice writes a migration, import or compatibility reader for an existing install. A slice that
  changes a file format makes the old format fail with a message naming the file, and tests that.
- **Docs in the same pull request**: the product's docs, `DESIGN.md`/progress entries where a product's own rules ask for them (Build's
  `CLAUDE.md`), and the "Status" line of the slice's row here.
- **Branch names** `suite/<id>-<short-name>` (for example `suite/f01-root-props`).
- **Decisions.** Every decision in plan 13 section 7 is made, so no slice waits on one; where a slice follows a decision, its row names it.

Sizes: **S** a day or two, **M** three days. Anything larger is split.

## For the agent implementing these

- **Read first:** plan 13 section 3 (the target shape and the clean-break rule), the workstream section of the slice, and the product's
  own `CLAUDE.md` (DbDataBuild's lists rules its tests enforce).
- **What the environment needs:** network access to GitHub releases (the DuckDB engine for K01, the polyglot library for Build), NuGet,
  Docker for the `testenv/` engines (SQL Server, PostgreSQL) and the images, and a Windows machine or the Windows CI jobs for anything
  marked Windows (DPAPI, services, ACLs, the MSI). The sandbox this plan was written in had none of the first three.
- **Per slice:** branch `suite/<id>-<short-name>` from `main`, one pull request, the "Done when" test in CI, then set the slice's status
  in this file (add `done (#<pr>)` to its row) in the same pull request.
- **When a slice is bigger than its size,** split it and add the parts here as `<id>a`, `<id>b`, rather than growing the pull request.
- **When something in the plan turns out wrong,** change plan 13 or this file in the pull request that finds it, and say so in the
  pull request description; do not work around the plan silently.

## Milestones

Slice IDs are a letter per workstream and two digits; decisions of plan 13 are `D` and one or two digits without a leading zero (`D8`, `D16`).

Each milestone is a point where something can be shown working. They follow plan 13's phases.

| Milestone | Slices | What can be shown |
| --- | --- | --- |
| **M0 Foundations** | F01-F14, K01-K12, V01-V07, S01 | one build setup and one version per package; every product on DuckDB 2.0.0; the encrypted secret store on every platform; the dotnet-script spike report |
| **M1 Engines and loaders** | S02-S10, A01-A07, B01-B03, U01-U03 | Sync loads libraries, drivers and scripts through dotnet-script; Sync's secrets in the shared store and no ClrKernel; Sync's UI builds from the shared workspace |
| **M2 Console with Sync** | T01-T09, U04-U12, C01-C07, E01, E04, M01-M06, A08-A10, A17-A18 | Sync is managed from the dbdatakit console on another machine; the new command names; Sync's scheduler on the shared packages |
| **M3 Proxy and Build managed** | P01-P10, R01-R09, M07-M11, E02-E03, E05, A11-A16, A19, A20, B04-B08, C08 | Proxy and Build are configured and operated from the console; Build refreshes run on schedule and are monitored |
| **M4 Authoring and release** | W01-W10, L01-L06 | a Build project authored, approved and applied from the console; the first suite release |
| **M5 Clean-up** | X01-X04 | the duplicated code listed in plan 13 section 6 is gone |

Inside a milestone the slices of different workstreams run in parallel; the **Needs** column is the only ordering that matters.

## F. Repository foundations (W0)

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| F01 | Root `global.json` and a root `Directory.Build.props` (net10.0, nullable, LangVersion) that each product's props imports | | S | every product and `DbDataKit/` build and test exactly as before; CI green. **Status: done** (branch `claude/monorepo-standardization-plan-2sq3l0`). The root `global.json` pins the SDK only: the SDK reads just the nearest `global.json`, and Sync and Build still test on VSTest (xunit v2), so `DbDataKit/` and `DbDataProxy/` keep their own copy that adds the Microsoft Testing Platform runner. Build's own `global.json` is gone. The two files merge when Sync and Build move to xunit v3 (F10/F11 decide) |
| F02 | Root `Directory.Packages.props`; `DbDataKit/` and Proxy pins move into it (union) | F01 | S | `dotnet list package` shows the same versions as before for both; CI green. **Status: done** (branch `claude/monorepo-standardization-plan-2sq3l0`). Four pins differed between the two and took the higher: `Hosting.Systemd`, `Hosting.WindowsServices` and `System.Security.Cryptography.ProtectedData` 10.0.0 to 10.0.13, `xunit.runner.visualstudio` 3.1.4 to 4.0.1 (all Kit side). The root file does not switch central management on, because Sync and Build find it too and are not on it yet: a product sets `ManagePackageVersionsCentrally` in its own props (Kit and Proxy now; F03 and F04 for the others). Proxy's file keeps only its `DbDataKit.*` entries |
| F03 | Build onto central package management through the root file | F02 | S | Build CI green; no `Version=` left in Build `.csproj` files. **Status: done** (branch `suite/f03-build-central-packages`). Unified to the root's versions: `Microsoft.NET.Test.Sdk` 17.x to 18.11.0, `xunit.runner.visualstudio` 2.x to 4.0.1, `xunit` 2.4.2 to 2.9.3 (its analyzers flagged 15 test sites, fixed in the unit tests) |
| F04 | Sync onto central package management, same versions as today | F02 | S | Sync CI green; no `Version=` left in Sync `.csproj` files. **Status: done** (branch `suite/f04-sync-central-packages`). Where Sync's version differs from the root's, the project keeps today's through `VersionOverride`: `Npgsql` 9.0.3 (F05), `Microsoft.Data.SqlClient` 7.0.2 and `DuckDB.NET.Data.Full` 1.5.5 (F06), `Oracle.ManagedDataAccess.Core` 23.9.1 (A09). Taken from the root instead: `Hosting.Systemd`/`WindowsServices` 10.0.13, `Microsoft.NET.Test.Sdk` 18.11.0, `xunit.runner.visualstudio` 4.0.1, `coverlet.collector` 6.0.4 (root raised from 6.0.0); the root's `AspNetCore.TestHost` and `System.Diagnostics.EventLog` rose to 10.0.11 and 10.0.13 to satisfy Sync's graph. Sync's `DbDataKitReference` packages get their version from `DbDataSync/Directory.Packages.props` |
| F05 | Sync: `Npgsql` 9.0.3 to 10.0.3 | F04 | S | Sync Postgres driver, state and integration tests green. **Status: done** (branch `suite/f05-sync-npgsql`): the seven `VersionOverride`s are gone; Postgres, Generic and State suites green against Sync's compose Postgres |
| F06 | Sync: `Microsoft.Data.SqlClient` 7.0.2 to 7.1.1, `DuckDB.NET.Data.Full` 1.5.5 to 1.5.6 | F04 | S | Sync MsSql, DuckDb and integration tests green; one version of each package in the repository (a CI check fails on two). **Status: done** (branch `suite/f06-sync-sqlclient-duckdb`): `scripts/check_package_versions.py` and `.github/workflows/repo-checks.yml` are the CI check; the one exception it allows is Sync's Oracle override (A09 removes it). DuckDB.NET 1.5.6 writes the connection string key as `DataSource`, so the driver test's expected strings changed |
| F07 | Proxy: warnings as errors, fixing what surfaces | F01 | S | Proxy builds with warnings as errors; tests unchanged. **Status: done** (branch `suite/f07-proxy-warnings-as-errors`): nothing surfaced, a full rebuild of the solution has no warnings, so only the setting changed |
| F08 | Build: the `DbDataKitReference` mechanism (`Directory.Build.targets` as Proxy and Sync have it) | F03 | S | a test project in Build references one shared package through it and builds in CI. **Status: done** (branch `suite/f08-build-kit-reference`): `DbDataBuild.Tests.Unit` references `Dialect.Abstractions` through it, with `SharedKitReferenceTests`. The four product CI change filters now also react to the root `Directory.Build.props`, `Directory.Packages.props` and `global.json` |
| F09 | Root `DbDataKit.slnx` with every project; per-product `.slnf` filters | F01 | S | a CI job builds `DbDataKit.slnx`; each product's workflow builds its filter. **Status: done** (branch `suite/f09-root-solution`): `DbDataKit.slnx` lists all 160 projects (`scripts/check_solution.py` keeps it complete and the filters inside it); the `repo-checks` workflow builds it; Proxy, Sync and Build have `<Product>.slnf` and their CI builds through it. The per-product `.slnx`/`.sln` files stay for now because the Sync Dockerfile and the docs use them; X01 removes them |
| F10 | Reusable `_product-ci.yml` (changes job, build, test with MTP floors, artifact names); Proxy moves onto it | F09 | M | Proxy CI runs through it with the same jobs and test counts as before |
| F11 | Sync and Build move onto `_product-ci.yml` | F10 | M | their CI runs through it with unchanged test counts |
| F12 | Build's conformance suite uses the `testenv/` engines instead of `scripts/test-engines.sh` | F11 | M | the quick conformance group green in CI on `testenv/`; the old script prints where it went |
| F13 | Root `CLAUDE.md` (who owns what, build and test the repository); root `docs/review/` moved into products or deleted | | S | docs only; links checked by the docs link test where one exists. **Status: done** (branch `suite/f13-root-claude-md`): each product's review detail is now its `review.md` (Sync's in `architecture/`, because a test keeps `docs/` public); `SECURITY.md` spans all three and plans cite it, so it is the root `docs/security-review.md`; the summary and the overlap review (superseded by plan 13) are deleted and stay in git history |
| F14 | Sync starts its workers (TaskRunner, library validation) as their own executables instead of `dotnet exec`, so Sync can publish self-contained (D11) | | M | Sync's integration and service tests pass on a self-contained publish on Linux and Windows |

## K. DuckDB 2.0.0 now (W1)

The released 2.0.0 engine, pinned, with `DuckDB.NET.Data` / `DuckDB.NET.Bindings` (no bundled engine) in place of `DuckDB.NET.Data.Full`
(D6, D7). Proxy's DuckDB data source (a test backend) gets no work of its own: it moves with the shared packages in K04.

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| K01 | `duckdb.pin` (2.0.0: URL and sha256 per platform: linux-x64, linux-arm64, win-x64, osx-arm64, osx-x64) and `scripts/fetch-duckdb.sh`/`.ps1` | | S | the scripts download and verify every platform in CI; a changed byte fails the check (test). **Status: done** (branch `suite/k01-duckdb-pin`). The pin records, per platform, the archive's hash and the library's hash inside it (release asset names are `duckdb-shared-libs-<os>-<arch>.tar.gz`; one archive per macOS architecture; the Windows library is `duckdb.dll`). The libraries land in `.duckdb/<rid>/` (git-ignored) for K02 to place. The shell script is tested with fake archives (tampered archive, wrong library hash, tampered installed copy); the real downloads and the PowerShell script (Windows PowerShell 5.1 and 7) run in the `repo-checks` workflow, and the PowerShell one has not run yet: no `pwsh` on the machine this was written on |
| K02 | `DbDataKit/build/DuckDb.Native.targets`: the engine at `runtimes/<rid>/native/` in build, test and publish output; a clear build error naming the fetch command when it is missing | K01 | S | a test project loads the engine and `SELECT version()` returns `v2.0.0` on Linux and Windows runners. **Status: done** (branch `suite/k02-duckdb-native-targets`). **Plan change:** the engine goes next to the application, not under `runtimes/<rid>/native/`: DuckDB.NET.Bindings has no resolver of its own and the host probes that folder only for files listed in `deps.json`, so a copied file there is not found (tried, `DllNotFoundException`). The application folder is always probed, also in a single-file publish and in Docker. `DbDataKit.DuckDb.Native.Tests` is the test project (`DuckDB.NET.Data` 1.5.6, no bundled engine); `scripts/tests/test_duckdb_native_targets.py` builds throwaway projects for the missing-library error, an unpinned platform (musl Linux is not pinned) and the file names per platform. Kit's CI fetches the engine first |
| K03 | Storage format: a test that opens a 1.x database file read-only with 2.0.0 and checks its bytes are unchanged; an audit of every read-write DuckDB open in the products | K02 | S | the test passes; the audit is a table in `DbDataBuild/docs/research/duckdb-2.0/README.md`; any read-write open of a user file becomes read-only or is in the release notes. **Status: done** (branch `suite/k03-duckdb-storage-format`). Finding that corrects plan 13: 2.0 does **not** convert an existing 1.x file when it opens or writes it (it keeps storage version 64 and 1.5.6 still reads it); only a database 2.0 *creates* is unreadable by 1.x (version 69). So the audit's consequence is two release notes (Proxy and Sync: files they create from now on need a 2.0 reader), not a read-only switch. The tests also cover a read-write open that only reads |
| K04 | Shared packages on 2.0.0: `Connections.DuckDb`, `Metadata`, `Transfer.Conformance` to `DuckDB.NET.Data`, and Proxy with them (no Proxy-specific work); sandbox setting names fixed if 2.0 changed any | K02, K03 | M | the shared suite and Proxy's existing suite green on 2.0.0, including every sandbox test (no external access, locked configuration, allowed directories, read-only); Proxy's zip and MSI contain the engine (dist test). **Status: done** (branch `suite/k04-shared-and-proxy-on-duckdb2`). No sandbox setting name changed in 2.0: all 28 `Connections.DuckDb` sandbox tests pass untouched. One 2.0 difference needed code, and it is Proxy's after all (the plan said none): 2.0 types a column that is only an untyped `NULL` as `SQLNULL`, which DuckDB.NET cannot map, so `ArrowColumnWriter` sends it as a string column of nulls (test added). `publish.ps1` fetches the engine for the target runtime, `package.ps1` fails if `duckdb.dll` is not in the folder it zips (the MSI is built from that zip; this runs in Proxy CI on Windows), and the Dockerfile fetches it (image built here, `libduckdb.so` is in `/app`). The five Kit test projects that run DuckDB import the targets; Proxy CI fetches the engine first |
| K05 | Sync on 2.0.0: the `duckdb` library installs `DuckDB.NET.Data` from NuGet and the engine for the running platform from the pin, hash-checked (an offline source can be configured); compile references to `DuckDB.NET.Data` | K02, K03 | M | Sync's DuckDb driver, verification, scripting and segmenting tests green on 2.0.0; a fresh `server run` installs the library and opens DuckDB; the Docker image contains the engine. **Status: done** (branch `suite/k05-sync-on-duckdb2`). `DbDataSync.Libraries.DuckDbEngine` reads the pin (embedded in the assembly), downloads the archive, checks both hashes, and `RestorePackagesAsync` calls it after any restore that includes `DuckDB.NET.Data`, so install, `library sync` and the Docker catalog cache all get the engine. `DBDATASYNC_DUCKDB_ENGINE_SOURCE` is the offline source. `LibraryRegistry` now also finds a native library that is not in `deps.json`. `LibraryLoadTests` installs the real library and runs `SELECT version()` through it (`v2.0.0`); the runtime image's catalog cache was built and holds `libduckdb.so`. Two 2.0 differences surfaced in Sync: the query preview's column metadata no longer fails on an untyped-NULL column (`SQLNULL`), and the test projects that run DuckDB import the engine targets. The `duckdb` catalog entry is now `DuckDB.NET.Data` 1.5.6. Not covered: a Windows or macOS install of the library (CI's Windows job runs the unit tests only) |
| K06 | Sync: a DuckDB extension source (httpfs) on 2.0.0 | K05 | S | an integration test installs the extension for 2.0.0 and reads a file through it (gated on network access in CI). **Status: done** (branch `suite/k06-sync-duckdb-extension`). `Drivers.DuckDb.Tests/ExtensionTests` installs `httpfs` into a fresh extension directory for the 2.0.0 engine and reads a CSV through it (served from a listener on the machine, so only the extension download needs the internet); `[NetworkFact]` runs it only when `DBDATASYNC_TEST_NETWORK=true`, which the integration job sets. Sync has no extension code of its own (DuckDB installs known extensions itself), so nothing needed changing |
| K07 | Build on 2.0.0: `DbDataBuild.Execution`, `Targets.DuckDb` and the spike to `DuckDB.NET.Data`; publish bundles the engine | K02, K03 | M | Build's unit suite on 2.0.0 with the known 2.0 failures marked by the slices below (no other failure); the publish smoke test loads the engine. **Status: done** (branch `suite/k07-build-on-duckdb2`). 10 of 2767 unit tests failed on 2.0.0. Three were a real gap, fixed here: 2.0 folds `TIMESTAMP '...' + INTERVAL ...` into a `TIMESTAMP` constant and the lowerer had no such literal (`PlanLowerer.Literal`). Seven are marked skipped, each with its reason and slice: the decimal quantile row (the one already in the research notes) and the three covariance rows (2.0.0 returns rounding noise, -78.9, for the 1e10-outlier probe where the exact sums give 0) go to **K10**; the three VALUES rows go to **K11** because 2.0.0 folds a call of constants (`upper('a')`, a date difference) before the plan, so the call is not there to lower (K11 decides: accept the folded value in the expected text, or lower it). These were not in the plan's K08-K11 lists. `scripts/publish.sh` fetches the engine; the single-file publish (254 MB) was run here, creates and compiles the `retail` template and seeds it, and the seeded file's header says `v2.0.0`; the release workflow's smoke test now does the same, and all Build workflows fetch the engine first |
| K08 | Build: ANY/ALL in 2.0's CASE form refused with a named diagnostic (D8) | K07 | S | the six ANY/ALL unit cases accept the refusal; the diagnostic has a catalog fixture. **Status: done** (branch `suite/k08-build-any-all`). The refusal was already in the lowerer and the six unit cases already accept it on 2.0.0 (K07's run). Added: three project-level fixtures in `LoweringIntegrationTests` (two filters and a value use, each DDB-324 naming "ANY or ALL", nothing written), DDB-324's explanation now names the form, and a note in the DuckDB 2.0 research notes |
| K09 | Build: `%` and `//` by zero follow 2.0 and the targets (they raise): the `NULLIF` is no longer written (D8) | K07 | S | unit text, differential and real-engine tests: all raise on division by zero. **Status: done** (branch `suite/k09-build-division-by-zero`). **Plan change for `//`:** dropping the `NULLIF` was enough for `%`, but not for `//`: the lowered form divided as DOUBLE, and the transpiler rewrites every `/` into a `NULLIF`-guarded float division for SQL Server and PostgreSQL, so the engines gave NULL where DuckDB raises (the `int_floor_div` probe failed on both). `//` is now written `CAST((a - (a % b)) / b AS T)`, where the `%` raises first on every engine; an operand with `random()`, `uuid()` and the like is refused because it is written twice. Real-engine probes (SQL Server 2022, PostgreSQL 17) and the unit differential agree. Found on the way: `column - DATE '...'` was not recognised as a date difference (a constant has no `return_type` in the plan) and reached SQL Server as `date - date`, which it rejects; fixed from the function's argument types. K11 also needs to re-check matrix row `type.decimal_product_wide`: 2.0.0 now gives SQL Server's answer on the `dec_product_wide` probe, so its Known entry is removed |
| K10 | Build: decimal `median`/`quantile_cont` matched to 2.0, or refused over DECIMAL; `current_schemas`, `current_setting`, `getvariable`, `getenv` refused with a named diagnostic (D8) | K07 | S | the quantile test passes on 2.0; each function has a diagnostic fixture; also the three skipped `Covariance_on_PostgreSQL_is_the_exact_sums...` rows (K07 found them). **Status: done** (branch `suite/k10-build-decimal-quantiles`). Decimal `median`/`quantile_cont` are **refused** (DDB-324, with the fix: cast to DOUBLE and back): no formula tried reproduces 2.0.0 on every group and fraction (the research notes list them), and DOUBLE quantiles also differ in the last bit now and then, so they are checked to six decimals. `quantile_disc` is unchanged. `getvariable` was not refused at all (2.0 folds it, so the lowered query silently carried NULL): the four session readers are now refused by name before planning, with a fixture each. The covariance rows K07 skipped pass again with the one cross-join query judged against the scale of its data (2.0.0 returns noise of 1e-18 relative). The `adventureworks` sample takes its median and p90 over DOUBLE (compile, unit and the real-engine templates group pass) |
| K11 | Build: regenerate the lowered artifacts and template goldens on 2.0.0; Build conformance suite on 2.0.0; decide the three skipped VALUES-list tests (K07 found that 2.0.0 folds constant calls before the plan) | K08-K10 | M | the `UPDATE_GOLDEN=1` diff reviewed and committed; conformance (quick, apply, templates, release groups) green |
| K12 | 2.0 only: delete code that exists only for 1.x plans; retire `scripts/test-duckdb-preview.sh`; one DuckDB engine in the repository (a CI check) | K04, K05, K11 | S | all suites green on Linux and Windows; the check fails if any project references `DuckDB.NET.Data.Full` |

## V. The shared secret store (W13)

Encrypted values in `<data>/secrets/secrets.yaml`, the key protected by DPAPI on Windows and by a permission-restricted file elsewhere;
no ClrKernel (D10).

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| V01 | Key ring and key protection: `IKeyProtector` with DPAPI (Windows, ACL to the service account, SYSTEM, Administrators) and a restricted file (Unix 0600/0700, owner check); a key from `secrets.keyFile` or `DBDATA_SECRETS_KEY_FILE` | | M | tests on Linux, macOS and Windows runners: a key round-trips; a key file readable by others is refused on Unix; a wrong DPAPI entropy fails |
| V02 | `EncryptedFileSecretStore`: AES-256-GCM, a nonce per write, the name as associated data, atomic and restricted writes; `ISecretStore` and `ISecretProvider` for `secret:` | V01 | M | tests: round trip; a tampered byte fails; a value moved to another name fails; concurrent writers do not corrupt the file; a sentinel value never reaches a log |
| V03 | Rotation: `rotate-key` re-encrypts every value and removes the old key; an interrupted rotation resumes | V02 | S | tests, including a rotation killed half way and resumed |
| V04 | Export and import under a passphrase (PBKDF2-SHA256, 600,000 iterations) | V02 | S | tests: export on one store, import on another with a different key; a wrong passphrase fails |
| V05 | The shared `secret` verbs in `DbDataKit.Secrets.Cli`: `set` (prompt or stdin only), `list`, `delete`, `check`, `rotate-key`, `export`, `import`, `protect`; no verb prints a value | V02-V04 | M | CLI tests per verb; a test fails if any verb writes a value to stdout or stderr |
| V06 | Proxy resolves `secret:` through the store (and inline `dpapi:` from `secret protect`); `secret set` writes to it; `LegacyEnvProvider` deleted | V05 | S | Proxy's secret tests; a data source password set with `secret set` connects |
| V07 | Delete the `Secrets.Keyring` stub; the Secrets READMEs describe the store, the key, backup and moving a server | V02 | S | the package is gone from the solution; docs reviewed |

## S. Libraries, drivers and scripts on dotnet-script (W2)

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| S01 | Spike: `Dotnet.Script.Core` 2.x inside the ASP.NET host on .NET 10 (throwaway branch) | | M | a written report: restore online and from a local feed, native assets (DuckDB, SQLite, Oracle), unload, cold-compile time against today's Roslyn path, Docker image, Windows service profile; D9 decided |
| S02 | `DbDataKit.Scripting`: the resolver (package references to a closure on disk, cached by hash), with `Connections.Libraries.Validation` moved in | S01 | M | unit tests against a local feed fixture; the H1 hostile-input corpus passes |
| S03 | The loader: a collectible load context per library, driver or script, with native assets | S02 | S | tests load Npgsql and DuckDB from a resolved closure, run a query, and unload |
| S04 | The compiler: `.csx` to a cached assembly, the allow-list and `ScriptSyntaxGuard` ported | S02 | M | Sync's `ScriptCompiler` tests, ported, pass against it |
| S05 | Sync libraries through the resolver and loader (`library.csx`) | S03 | M | Sync's library tests and the Libraries page Playwright test pass unchanged |
| S06 | Sync compiled drivers as `driver.csx` | S03, S04 | S | the plugin-driver tests pass with a `.csx` driver |
| S07 | Sync scripts as `.csx` | S04 | S | Sync's scripting tests pass on `.csx` scripts |
| S08 | Old-format files refused: a `.cs` script, `library.json` or `driver.json` in a config repository stops the load with a message naming it (clean break) | S05-S07 | S | a test per file kind |
| S09 | `dbdatasync script test <name>` and the editor stub for `.csx` | S07, C06 | S | CLI test runs a fixture script; the stub resolves in a C# language-server test |
| S10 | Delete the old paths (`DbDataSync.Libraries`, plugin loading in `Drivers.Descriptor`, `ScriptCompiler`'s compilation, `Connections.Libraries`' installer) | S08 | S | tests green; a source scan finds no reference to the removed types |

## A. Sync adopts the shared libraries (W3)

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| A01 | Sync connection files read through `Config.Yaml`'s strict reader | F04 | S | Sync's config tests pass; unknown keys are errors with a path (new tests); fuzz test over the connection files |
| A02 | The remaining Sync entity files (replications, mappings, scripts' manifests, drivers) through the strict reader | A01 | M | same as A01 for each type |
| A03 | A required `schemaVersion` in every Sync file; a file without it, or older, refused with a message (clean break) | A02 | S | a test per file kind |
| A04 | Sync secret references through `DbDataKit.Secrets` and the encrypted store, under `dbdata:sync:...` names (the old `dbdatasync:...` names are not read) | V05, A01 | M | Sync's secret tests pass; a sentinel-password test finds no secret in any log line |
| A05 | Remove `ClrKernel.Core.Secrets`; nothing imported (clean break) | A04 | S | no ClrKernel reference left; tests green; the release note says secrets are set again |
| A06 | Sync quoting and lexer from `Dialect` | F04 | M | Sync's dialect tests pass; the shared hostile-SQL corpus runs against Sync's dialects |
| A07 | Sync type maps from `Dialect` (`CanonicalType` from the shared package); delete `Core/Sql`'s copies | A06 | M | type round-trip tests per engine; `Core/Sql` keeps only what is Sync's |
| A08 | Sync SQL Server and Postgres connection strings through the `Connections` composers | A07 | M | connection tests per driver; the hostile-credential corpus; TLS verify-by-default named in release notes |
| A09 | `Connections.MySql` and `Connections.Oracle` (new shared packages) and Sync on them | A08 | M | composer tests; Sync's MySQL and Oracle integration tests |
| A10 | Sync failures through the shared classifier | A08 | S | each driver's failure tests map to the shared kinds; no raw driver text reaches the UI (test) |
| A11 | Sync catalog reads for SQL Server and Postgres through `Metadata` | A08 | M | metadata tests pass unchanged; the mapping metadata cache still works (Playwright) |
| A12 | MySQL and Oracle readers in `Metadata`; Sync on them | A09, A11 | M | reader tests per engine, live in CI |
| A13 | Transfer benchmark baseline for Sync's staging paths | | S | numbers committed; the benchmark runs in a manual job |
| A14 | Sync SQL Server bulk staging through `Transfer.SqlServer` | A13, A08 | M | bulk-load tests; benchmark within 5% of A13 |
| A15 | Sync Postgres COPY staging through `Transfer.Postgres` | A13, A08 | M | same as A14 |
| A16 | Sync merge and delete-insert strategies from the shared strategy builders | A14, A15 | M | replication integration tests unchanged |
| A17 | `DbDataKit.Connections.FlightSql` from Sync's Flight SQL connection code; Sync's driver keeps only its readers | A08 | M | Sync's Flight SQL tests pass against the shared client |
| A18 | CI: the Flight SQL client against a real `dbdataproxy server` | A17 | S | an end-to-end job runs queries through Proxy to SQL Server and DuckDB |
| A19 | Build's `FlightSqlRowSource` and `FlightSqlLogin` on `Connections.FlightSql` | A17, B02 | S | Build's Flight conformance group (`up flight`) green |
| A20 | Unused shared packages: adopt or delete `Connections.Descriptor`, `Dialect.Arrow` (Proxy benchmark, plan 10 C7), `Secrets.Cli` | | S each | each either has a consumer with tests or is removed from the solution |

## B. Build adopts the shared libraries (W3)

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| B01 | Build quoting and lexer from `Dialect`, behind `DialectProfile` | F08 | M | Build unit suite unchanged; Build's quoting fixtures added to the shared conformance cases |
| B02 | Build logins (`Logins`, `OdbcLogin`, `FlightSqlLogin`) through `Connections`; `DBDATA_BUILD_<CONN>_<READ|WRITE>` only, the old `DBDATABUILD_*` names dropped here and in the shared resolver | F08 | M | login tests; an old name is not read (test); no read-to-write fallback (test) |
| B03 | Build failures through the shared classifier; `DriverFailure` removed | B02 | S | the real-engine scrub test passes; "driver messages are never echoed" is a shared-classifier test |
| B04 | Build catalog reads (`CatalogReader`, `SourceCatalogReader`, `OdbcCatalogClient`) through `Metadata` | B02 | M | Build's import and status tests unchanged; conformance quick group green |
| B05 | `ServerFacts`: Build uses the shared record | B04 | S | facts golden tests unchanged |
| B06 | `MutationGate` replaced by the shared `WriteGate`; `GateInvariantTests` moved to `DbDataKit/` and run against both | B02 | M | the gate invariant test passes in the shared suite and for Build |
| B07 | Build's statement log on the shared write gate's log | B06 | S | statement log tests and retention tests unchanged |
| B08 | Build's `Transfer.cs` (copies) on `Transfer` | B06, A16 | M | the `fan_in_fan_out` template and copy conformance tests green |

## C. One command surface and the renames (W4)

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| C01 | `DbDataKit.Cli`: the command spec model (effect class, JSON document schema, help) taken from Build's `CommandSpecs`; Build uses it | F08 | M | Build's command tests, `docs/commands.md` generation and the output-schema test unchanged |
| C02 | The shared nouns in `DbDataKit.Cli`/`Hosting.Cli`: `server run`, `service`, `cert`, `config`, `secret`, `health check` | C01 | M | unit tests per verb against fakes; help snapshot tests |
| C03 | Proxy: lower-case executables (`dbdataproxy`, `dbdataproxy-worker`), service registration, `dist.json`, `install.ps1`, MSI | F10 | S | dist tests, the MSI end-to-end test and the service tests pass with the new names |
| C04 | Proxy grammar: `server run` (no default command), `secret hash`, `secret protect`, `query run`; `install`/`uninstall` removed | C02, C03 | S | CLI snapshot tests of help and exit codes; release note |
| C05 | Proxy commands on the spec model with JSON documents | C04 | M | every Proxy command has a schema; a test fails for a command without one |
| C06 | Sync grammar: `server run`, `server setup`, `cert`, `secret`, `library`, `driver`, `health check` | C02 | M | `CertCommandSnapshotTests` and the CLI tests updated to the new names; the service's command line and `service-e2e.ps1` updated; release note |
| C07 | Sync commands on the spec model with JSON documents | C06 | M | as C05 |
| C08 | `ui mcp` from the shared surface for Sync and Proxy | C05, C07 | S | an MCP tool-list snapshot per product |

## T. Console and server trust (W12)

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| T01 | Token format, signing and validation in `DbDataKit.Web` (ed25519, issuer, audience, expiry, replay, ceiling) | | M | unit tests, including hostile tokens: wrong key, wrong audience, expired, replayed, above the ceiling, malformed |
| T02 | The local channel in `DbDataKit.Hosting`: named pipe (Windows) and Unix socket, OS-restricted | | M | tests on Linux and on the Windows job: allowed identity connects, another is refused |
| T03 | Server side of pairing: `server pair` (single-use code), paired consoles in `server.yaml`, `server consoles`, `server unpair` | T01, T02, C02 | M | tests: code expires, code is single use, unpair takes effect on the next call |
| T04 | Console side: `dbdatakit instance add`, certificate pinning, the console's signing key in a protected store | T03, U07 | M | an end-to-end test pairs a console with a fake server over HTTPS with a self-signed certificate |
| T05 | Authorization on management APIs: the role from the effect class, the ceiling, the lower of the two | T01, C01 | S | a test per role and effect class; the server refuses what the console would hide |
| T06 | Audit: user and console on every change, and as the author of config commits | T05, U06 | S | tests read the audit log and the git author after a change |
| T07 | Signing key rotation (the new key signed with the old) | T04 | S | after rotation old tokens fail, new ones pass, no re-pairing |
| T08 | Write version checks (stale writes refused with the current version) | T05 | S | two concurrent writes: the second is refused with the current version |
| T09 | Management API version reported at pairing and on every call; the console's N and N-1 rule (D17) | T04 | S | a console refuses a server two versions ahead with the upgrade message (test) |

## U. Shared web platform and the console host (W5)

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| U01 | npm workspace at `DbDataKit/web/`; `DbDataSync.Web` moved in as `@dbdatakit/sync`, building the same output | | S | Sync's vitest and Playwright suites pass; the served files are the same |
| U02 | `@dbdatakit/shell` extracted (shell, sign-in, common components, API client) | U01 | M | suites unchanged; `shell` imports nothing from `sync` (lint rule) |
| U03 | `@dbdatakit/admin` extracted (users, certificates, updates, config history, libraries, drivers, files, docs) | U02 | M | suites unchanged; `admin` imports nothing from `sync` |
| U04 | `DbDataKit.State`: users, sessions, invites, notifications and audit stores out of `DbDataSync.State` | F04 | M | the moved store tests pass on SQLite, SQL Server and PostgreSQL |
| U05 | `DbDataKit.Web`: session handler, passkeys, Windows sign-in, policies out of `DbDataSync.Api` | U04 | M | Sync's API auth tests pass unchanged |
| U06 | `DbDataKit.Config.Git` from Sync's `GitCommitService` | F04 | S | the git tests moved and green |
| U07 | Console host skeleton: `dbdatakit server run` serving the workspace build, sign-in, users, invites (`dbdatakit user ...`) | U03, U05, C02 | M | Playwright: first-run invite, passkey sign-in, user list against the console |
| U08 | Console relay (D16): the console back-end forwards a module's calls to a paired server with a token | U07, T04, T05 | M | Sync's Playwright suite passes through the console against a paired `dbdatasync server` |
| U09 | Live updates through the relay (Sync's SignalR run hub) | U08 | S | the run-progress Playwright tests pass through the console |
| U10 | Console first run: users start fresh (first-run invite); nothing is read from Sync's state database (clean break) | U07 | S | Playwright: a new console's first-run invite, then sign-in |
| U11 | Sync's server stops serving the SPA | U08-U10 | S | Sync's API tests green; `/` on the Sync server returns the console's address; release note |
| U12 | Console: instances page (pair, list, health, version, unpair) | T04, T09 | S | Playwright: pair, see health, unpair |

## E. Shared definitions (W8)

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| E01 | Sync connections as `connection/1`; the old form refused | A03, A08 | M | connection tests on the new format; a test for the refusal; the Connections page works |
| E02 | Proxy data sources as `connection/1` with `extensions.proxy` | P01 | S | Proxy's config tests on the new format |
| E03 | Build connection files as `connection/1` (a project schema change; the templates rewritten, an old file refused with a diagnostic) | B02 | M | every template compiles on the new format; schema and fuzz tests; a diagnostic fixture for an old files |
| E04 | Sync host settings in `server.yaml`; an `appsettings.json` refused with a message | A03 | S | `server run` reads only `server.yaml`; a test for the refusal; release note |
| E05 | Console: the Connections view across servers, with copy to another server | E01, E02, U08 | M | Playwright: list connections of a Sync and a Proxy server; copy one; the target validates it |

## P. DbDataProxy management (W6)

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| P01 | Proxy config to YAML (`server.yaml`, `proxy/`, `connections/`) in a git-tracked directory; an `appsettings.json` refused with a message; Proxy's JSON config migrations deleted | C04, U06 | M | config tests on the new format, including the shipped example; a test for the refusal; strict validation unchanged |
| P02 | Proxy state store (SQLite through `DbDataKit.State`, D18) | U04 | S | store tests; created on first start |
| P03 | Management listener: health, version, effective config (redacted, with origins), validation | T05, P01 | M | API tests through a console token and through the local channel; no secret in any response (sentinel test) |
| P04 | Data sources: create, change, delete, test connection; hot reload | P03, E02 | M | API tests; a change applies without a restart (test runs a query before and after) |
| P05 | Client identities: Flight users, Windows accounts, JWT mappings, pass-through | P03 | M | API tests; the password hash is never returned |
| P06 | Sessions and running queries (with cancel); limits and login throttle state | P03 | M | a cancel test against a long query; limits reported |
| P07 | Certificate status and renewal; recent classified failures | P03 | S | API tests |
| P08 | Console module: overview and data sources | U08, P04 | M | Playwright against a paired Proxy |
| P09 | Console module: client identities, TLS, activity, config history | P05-P07, P08 | M | Playwright against a paired Proxy |
| P10 | Proxy feeds failed logins, refused statements and limits reached into `Monitoring` | M04, P03 | S | events appear in the console's monitoring view (test) |

## R. DbDataBuild server and management (W7)

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| R01 | `dbdatabuild server run` with one project: the management API exposes the read-only commands through child processes | C01, T05 | M | API tests: `connection status`, `project show graph` return the same documents as the CLI |
| R02 | Several projects: `server project add|remove|list`, `build/projects.yaml`, local path or git remote (D19) | R01 | M | tests: two projects served; a remote project is cloned and pulled |
| R03 | Write and deploy commands through the API with roles; approvals recorded in plan events | R01, T05 | M | test: Viewer refused, Approver applies; the plan's events name the approver and console |
| R04 | Secret references into each command's environment (through `DbDataKit.Secrets` and the store) | R01, V05 | S | test: a command logs in with a resolved secret; no fallback from read to write |
| R05 | `dbdatabuild service install|...` (Windows and systemd) | C02, R01 | S | unit-file snapshot; Windows service test in the Windows job |
| R06 | Build state store (SQLite) for last runs, workspaces, approvals | U04, R01 | S | store tests |
| R07 | Console module: projects, compile and diagnostics, graph | U08, R02 | M | Playwright against a paired Build server with a template project |
| R08 | Console module: status and drift, plans (review, approve, apply), compare, sample | R03, R07 | M | Playwright: deploy a template project to the test PostgreSQL, approved by a second user |
| R09 | Retire `ui web` | R08 | S | the command is gone (CLI snapshot); release note |

## M. Shared scheduling and monitoring (W10)

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| M01 | `DbDataKit.Scheduling`: the schedule model and evaluator (cron with time zone, interval, misfire) from Sync's `SchedulingEvaluator`/`TaskScheduling` | F04 | M | Sync's scheduling tests moved and green; a fake-clock test for daylight saving and missed runs |
| M02 | Work queue with leases and the run lock into `Scheduling`, on the `State` dialects | M01, U04 | M | `WorkQueueStore` and `RunLockStore` tests moved and green on the three dialects |
| M03 | Sync's scheduler on `Scheduling` | M02 | S | Sync's scheduler and run tests unchanged |
| M04 | `DbDataKit.Monitoring`: run model and health rules (overdue, slow, failed, lag) | | M | rule tests with a fake clock |
| M05 | Notifications in `Monitoring`, with per-console read state | M04, U04 | S | store tests; two consoles read independently |
| M06 | Sync's run monitor, lag and notifications on `Monitoring` | M05, M03 | M | Sync's monitoring tests and the Playwright monitoring tests unchanged |
| M07 | Build: `schedule:` and `time_zone:` on refresh plans (parse, validate, store in the plan file, `project show refresh`; D13) | | S | schema and fixture tests; fuzz tests; a diagnostic for a bad cron |
| M08 | The Build server runs schedules through `Scheduling` | M02, M07, R02 | M | a fake-clock test runs a plan at its time; a manual run at the same time is skipped or waits per `on_busy` |
| M09 | Build run history from the tracking tables into `Monitoring` | M04, R01 | S | overdue and slow appear for a plan that missed its `expected_every` (test) |
| M10 | Retention of plan files and statement logs from scheduled runs | M08 | S | test: files beyond the policy are removed, the newest kept |
| M11 | Console: one monitoring view across servers; pause and run-now for schedules | M06, M09, U08 | M | Playwright: a failed Build run and a lagging Sync replication appear together; pause stops the next run |

## W. Build authoring in the console (W11)

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| W01 | `project file list|read|write|move|delete`, confined to the project tree, with content hashes | C01 | M | tests: path escapes refused, stale hash refused; mutation tests over every file it parses |
| W02 | Workspaces: a git worktree per user on the Build server; list, create, abandon | R02, W01 | M | tests: a draft does not change what a scheduled run reads |
| W03 | A long-lived child process per workspace (warm DuckDB and parser) | W02 | M | compile of a changed model under a set time on the template projects (measured in CI) |
| W04 | Console: file tree and the SQL editor with diagnostics as markers | W02, R07 | M | Playwright: edit a model, see a diagnostic, fix it |
| W05 | Console: schema-driven forms for model, folder, connection, refresh plan, test and seed files, with a YAML tab | W04 | M | Playwright per file kind; round trip keeps comments |
| W06 | Console: live lowered and rendered text per target, sample rows, the graph around the model | W03, W04 | M | Playwright: change a model, the rendered SQL and sample rows change |
| W07 | Console: new project from a template, new model, the definition questions as a form, import from a connection | W05 | M | Playwright: create the `retail` template, add a model, answer its questions |
| W08 | Git in the console: diff, commit, history; push and pull request, or merge inside the server (D15) | W02 | M | tests for both paths; conflicts refused and shown |
| W09 | The Author role | T05, W08 | S | Author can edit and commit, cannot approve or apply (test) |
| W10 | End-to-end: template to applied plan, all in the console | W07-W09, R08 | S | one Playwright run: create, edit, commit, merge, plan, approve (second user), apply on the test PostgreSQL |

## L. Distribution and releases (W9)

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| L01 | `dbdatakit` console zip and Docker image | U07 | S | image builds in CI; health check passes in a container |
| L02 | Build server Docker image | R05 | S | image builds; `server run` answers its health check |
| L03 | Compose files pairing each product image with the console image (pairing code from an environment variable) | L01, T03 | S | a CI job starts each compose file and the console shows the paired server |
| L04 | Each product MSI includes the console as a feature, on by default, and pairs it at install | L01, T03 | M | the MSI end-to-end test (`dist/tests/msi-e2e.ps1`) installs, pairs and signs in |
| L05 | The `dbdatakit` suite MSI (one feature per product plus the console) | L04 | S | MSI end-to-end test with all features |
| L06 | One release workflow producing every image, MSI and zip from one commit; product release workflows call it | L01-L05 | M | a dry-run dispatch produces every artifact and checksum; a beta release |

## X. Clean-up

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| X01 | Remove code that plan 13 section 6 lists as replaced and that is still present | the slices that replace it | S each | a source scan per item finds no use |
| X02 | Shared packages still without a consumer (except `Update.*`, shelved) removed | A20 | S | solution and package lists |
| X03 | Product docs audited for the renamed commands and the console | C04, C06, R09, U11 | S | a docs test that fails on an old command name in any product doc |
| X04 | Root `docs/plans/STATUS.md` brought up to date | | S | docs only |

## What can start now

Nothing waits on a decision. The first pull requests, in parallel:

1. **K01** (the DuckDB pin and fetch scripts), then **K02** and **K03**, then **K04** (shared packages, Proxy along), **K05** (Sync)
   and **K07** (Build) side by side.
2. **V01**, then **V02**: the secret store, which Sync's secrets (A04), Proxy (V06) and the Build server (R04) wait on.
3. **F01** (root build setup) and **F14** (Sync without `dotnet exec`).
4. **S01**, the dotnet-script spike (a report, not merged).
5. **T01** (console tokens) and **U01** (Sync's web app into the shared workspace).
