## Secrets (Plan 02)

Implemented under `shared/`: `DbData.Secrets.Abstractions`, `DbData.Secrets`, `DbData.Secrets.Windows`,
`DbData.Secrets.Keyring` (stub), `DbData.Secrets.Cli`, each with tests in `shared/tests/`.
Stubbed: no concrete OS-keyring backend (Phase 0 wrap-vs-fold decision on `ClrKernel.Core.Secrets` pending),
`import-env` CLI command, cloud providers. Unverified: exact Sync env-var sanitisation for
`DBDATASYNC_SECRET_<REF>`; Unix branch of `AclHelper` (tests skip on Windows).
Run tests: `cd shared/tests/<Pkg>.Tests && dotnet test` (needs `shared/global.json` MTP runner).

## Odbc (Plan 04 Part A)
Branch worktree-agent-afbf86949a65d1ed2. shared/src/DbData.Odbc.Metadata + tests: copied from DbDataProxy c08355f (no history split), hardened (options, row cap, timeouts, policy, Build/Quote, session, SQLWCHAR probe, LibraryImport), added TypeInfo/Procedures/driver inventory/diagnostics, PublicAPI baseline. 129 tests green. Not done: Proxy switch to the package, Linux/macOS live verification, Part B.
# Plan status

## Update (Plan 08)

Implemented in `shared/`: `DbData.Update.Abstractions`, `DbData.Update`, `DbData.Update.Service`, `DbData.Update.Detect`, `DbData.Update.Tools` (dotnet tool `dbdata-update`), `DbData.Update.Updater` (console `dbdata-updater`). Tests: `shared/tests/DbData.Update*.Tests` (all offline).

Done: release/1 manifest, semver + channels, UpdateChecker (min-safe, revocation, downgrade), sources (GitHub/NuGet/Folder/Url), downloader (trusted prefix, resume, size+sha256), ed25519 verification with key rotation, zip/tar-safe extraction, UpdateMode/window, side-by-side layout, update-plan journal with recovery, helper state machine with rollback, install-method detection and appliers, manifest create/sign/verify tool.

Stubbed / not done: real service control and Event Log (Plan 03; `dbdata-updater` has a throwing stub), Authenticode, helper trigger (scheduled task/systemd), cross-process helper mutex, `install.ps1` replacement (phase 6), Scoop/winget generation from manifest, porting Sync `Updates` onto the library (phase 2), reusable-release.yml (sketch only in Tools README), key custody decision.
# Plan status

## Config (Plan 01)

- Done (shared/src, tests green via `dotnet test --project shared/tests/<Pkg>.Tests`): DbData.Config.Yaml (reader, merge, editor, diff, fuzz), DbData.Config (attributes, binder, layering, env/CLI, migrations, JSON Schema), DbData.Config.Hosting (MEC provider, options validation, ShowOrigin).
- Not done: DbData.Config.Testing; products do not consume the packages yet (phases 1, 5, 6); JSON input format behind the node tree; git-commit backup mode; `schema: kind/int` form; CLI commands (`config show/migrate/schema`) are library APIs only.
## Hosting (Plan 03)

Branch: worktree-agent-ad4903181a885165d. Packages in `shared/src`: DbData.Hosting.Abstractions, DbData.Hosting, DbData.Hosting.WindowsService, DbData.Hosting.Systemd, DbData.Hosting.Certificates, DbData.Hosting.Children, DbData.Hosting.Cli (tests in `shared/tests`).

- Done: all six deliverables with unit tests (Windows-only tests are skipped elsewhere; the real-SCM mutating test needs `DBDATA_ADMIN_TESTS=1` and an elevated prompt and was not run here).
- Run tests from `shared/`: `dotnet test --project tests/<Package>.Tests` (`shared/global.json` selects the Microsoft.Testing.Platform runner required by xunit.v3 on the .NET 10 SDK).
- Stubbed: ADCS enrolment (interface only), UAC relaunch, logging/redactor wiring in `UseDbDataHost`, job assignment is post-start (tiny race documented).
- To reconcile at merge: `IAclPolicy`/`WindowsAclPolicy` here vs the Secrets agent's AclHelper (`DbData.Secrets.Windows`); `shared/global.json` may also be added by other agents.
# Implementation status

## Dialect (Plan 05)

Branch `worktree-agent-a12e81128462776ae`. Packages under `shared/src`: `DbData.Dialect.Abstractions`, `DbData.Dialect`, `DbData.Dialect.Arrow`, `DbData.Dialect.Conformance`; tests under `shared/tests`.

- Done: engine ids/caps, parameterised `CanonicalType`, `TypeMapping`/`Fidelity`, `ISqlDialect`; shared `SqlLexer` (+ fuzz/property tests), parameter rewriter, statement classifier, `ReadOnlyPolicy` with per-dialect deny lists and `ISqlParser` seam; strict `dialect.yaml` loader; built-in ansi/sqlserver/postgres/mysql/oracle/duckdb/fabric + generic ODBC from facts; type tables with `types.golden.yml`; Canonical<->Arrow table; offline conformance suite green for all dialects.
- Stubbed: live conformance (skipped unless `DBDATA_CONFORMANCE_<ENGINE>` is set; no driver in `shared/`), live type round trip, DDL fragments (`CreateTable` etc. not in `ISqlDialect` yet), array/list native types, product adoption (migration phases 1 and 6).
- Run tests from `shared/` (a `shared/global.json` opts into the Microsoft.Testing.Platform runner required by .NET 10 `dotnet test`).

## Wave 1 merge summary (main)
Merged: Secrets, Odbc.Metadata, Update, Config, Hosting, Dialect. `shared/DbData.Shared.slnx` builds everything;
run `cd shared && dotnet test --solution DbData.Shared.slnx` (962 pass, 17 skipped by design: live DB, admin, Unix).
Merge conventions: union-resolve `Directory.Packages.props`/`STATUS.md`; `shared/tests/Directory.Build.props` supplies xunit.v3
(do not add it per test project); `shared/**` is pinned to LF in `.gitattributes`.
Open decisions: Sync env-var sanitisation rule (Secrets), camelCase default keys (Config), migrations per product,
BouncyCastle for ed25519 (Update), signing-key custody (Update), Oracle/tinyint type policy (Dialect).
Reconcile pending: Hosting `IAclPolicy` vs Secrets `AclHelper`.
Wave 2 (in progress): Connections (06), Transfer (07), Metadata Part B (04), Config.Testing + ACL reconcile.

## Cleanup (ACL reconcile, Config.Testing)
- ACL: new `shared/src/DbData.Security.Acl` is the single implementation (`AclHelper`, `IAclService`/`FileSystemAclService`, `AclSpecification`). `DbData.Secrets.Windows` and `DbData.Hosting.WindowsService` reference it; Hosting's `IAclPolicy`, `WindowsAclPolicy` and `IFileSystemAclReader` are gone (doctor and `ServiceCommand` take one `IAclService`; `LayoutAcl.Apply` maps layout to spec). `AclHelper` namespace moved to `DbData.Security.Acl` (breaking for any product code that used `DbData.Secrets.Windows.AclHelper`).
- Config: `DbData.Config.Testing` added (golden migration pairs, fuzz helper, schema/binder agreement, catalog completeness) with tests, and used by `DbData.Config.Tests`. `schema: <kind>/<int>` header and JSON input (`*.json`, `JsonInput`) implemented with tests. Still not done: git-commit backup mode, CLI commands.
- `cd shared && dotnet test --solution DbData.Shared.slnx` green; new projects added to the slnx.
## Metadata (Plan 04 Part B)
Branch worktree-agent-a1cd09b6bde6b9eb0. `shared/src/DbData.Metadata.Abstractions` (TableRef, ColumnInfo/KeyInfo/ForeignKeyInfo/IndexInfo, TableShape + ShapeSupport, QueryShape, ServerFacts, CatalogCapabilities, ICatalogReader, TableFilter/LikePattern, CatalogNames, ICatalogCache/ConnectionFingerprint) and `shared/src/DbData.Metadata` (InformationSchema, SqlServer, Postgres, DuckDB, ODBC readers; ServerFactsProbe; DialectProfiles; dialects.lock-style YAML; CachingCatalogReader). Added `SQLPrepare`/`SQLDescribeCol` (`OdbcCatalog.DescribeQueryAsync`) and raw `GetInfoTextAsync/GetInfoNumberAsync` to `DbData.Odbc.Metadata` (PublicAPI updated, fake-backed tests).
- Done: tests green (`dotnet test --project tests/DbData.Metadata.Tests`; DuckDB in-process, SQL Server/Postgres via recording fake DbConnection, ODBC via fakes). New package version: DuckDB.NET.Data.Full 1.5.6.
- Stubbed/not done: live SQL Server/Postgres runs (need a provider package, skipped; env `DBDATA_METADATA_*`), `FlightSqlCatalogReader`, MySQL/Oracle-specific readers, procedures on `ICatalogReader`, product adoption (Proxy `Metadata.cs`, Sync, Build).
- Open: where the DuckDB/Npgsql/SqlClient live tests should get providers; SQL_TINYINT signedness policy; whether `ServerFacts` should replace Build's `ServerFacts` record directly.
## Transfer (Plan 07, phases 0-4 and part of 5)

Branch `worktree-agent-ae65f8928374b45cd`. Plan 07a (`docs/plans/07a-strategy-comparison.md`) written from the Sync and Build code. Packages in `shared/src`: `DbData.Transfer.Abstractions`, `DbData.Transfer`, `DbData.Transfer.Conformance`, `DbData.Transfer.SqlServer`, `DbData.Transfer.Postgres`; tests in `shared/tests/DbData.Transfer*.Tests`; benchmark stub in `shared/benchmarks`.

- Done: abstractions (schema, `DataBatch` with reader and Arrow views, source/writer/staging/strategy/state/verifier contracts); `ValueConverter` (plan per column, fidelity, Fail/Null/Truncate/Clamp, edge-case tests); staging policy (Build-compatible names, run suffix, temp variants, lifecycle, generic provider); six strategy SQL builders (full_replace, delete_insert_by_key, merge_by_key, delete_insert_by_range, watermark_append, key_reconcile_delete with DeleteGuard); capability matrix as data; `WriteGate` (intent, readonly, permitted kinds, locks, transactions, statement log with hashes/types by default, redaction, dry-run) and the reusable source-scan invariant helper; DuckDB conformance (all six strategies, idempotent re-run); SQL Server `SqlBulkCopy` and Postgres binary COPY writers (offline tests green; live tests gated by `DBDATA_TEST_SQLSERVER` / `DBDATA_TEST_POSTGRES`).
- Not run: any live SQL Server/Postgres test; MySQL/Oracle/Fabric SQL is text-tested only. Matrix marks SQL Server/Postgres `product-verified` (Build conformance), not verified here.
- Not done: Arrow-first and Parquet staging, MySQL/Oracle writers, `ITransferVerifier` implementation, segmenter, SCD2/Snapshot, Windows ACL on the statement log, Sync/Build adoption (phases 3-5 product work), benchmark gate in CI (baseline of current paths not measured; stub compares only in-memory feeding).
- Open: Windows ACL for the statement log; whether Sync Merge (commit per chunk) should map to a non-transactional `merge_by_key` option; `DateOnly`/`TimeOnly` bulk behaviour on SqlClient is adapted to `DateTime`/`TimeSpan` defensively and unverified live.
## Connections (Plan 06)

Packages in `shared/src`: `DbData.Connections.Abstractions`, `DbData.Connections`, `.SqlServer`, `.Postgres`, `.DuckDb`, `.Odbc`, `.Descriptor`, `.Libraries` (tests in `shared/tests`, all offline; live variants gated by `DBDATA_TEST_SQLSERVER`, `DBDATA_TEST_POSTGRES`, `DBDATA_TEST_ODBC`).

- Done: `connection/1` strict loader and JSON Schema; failure model and classifier with engine mappers; composers (SqlClient, Npgsql, generic ADO, ODBC with brace quoting and Plan 04 policy) with credentials last, properties deny-list, TLS verify-by-default and hostile-credential tests; pass-through injection that strips integrated-security keys and asserts none remain; login resolution (`DBDATA_<APP>_<CONN>_<READ|WRITE>` plus legacy `DBDATABUILD_*`, no read-to-write fallback); `ParameterStyleAdapter`; pool with limits, timeouts, idle reaping and metrics; DuckDB sandbox proven in-process; YAML engine descriptors (`engine/1`) with locked fixed properties; library-install validation and planning (H1 corpus).
- Stubbed or not done: JDBC/IKVM, Flight SQL, MySql/Oracle providers, plugins package, `IDbProvider.Catalog` (waits for Plan 04 part B), `SQL_ATTR_ACCESS_MODE` is reflection based and unrun against a real driver, `DotnetPublishExecutor` unrun, no product adoption, `KnownLibraries`/compat checkers not ported.
- Conventions: `Microsoft.Data.SqlClient` 7.1.1, `Npgsql` 10.0.3, `DuckDB.NET.Data.Full` 1.5.6 added to `Directory.Packages.props`; `System.Security.Cryptography.Pkcs` pinned to 10.0.13 for a transitive advisory.

## Overall status (all planned waves merged)
Packages on main (shared/): Config (Yaml, Config, Hosting, Testing), Secrets (Abstractions, core, Windows, Keyring stub, Cli), Security.Acl,
Dialect (Abstractions, core, Arrow, Conformance), Odbc.Metadata, Metadata (Abstractions, core), Connections (Abstractions, core, SqlServer,
Postgres, DuckDb, Odbc, Descriptor, Libraries), Transfer (Abstractions, core, Conformance, SqlServer, Postgres), Update (Abstractions, core,
Service, Detect, Tools), Hosting (Abstractions, core, WindowsService, Systemd, Certificates, Children, Cli).
`cd shared && dotnet test --solution DbData.Shared.slnx`: 1833 total, 0 failed, 50 skipped (live DB/admin/Unix gated).
Not built: Flight SQL and JDBC providers, MySQL/Oracle providers and writers, plugins package, Arrow-first/Parquet staging, verifier implementation,
keyring backend, config CLI commands, reusable-release workflow, product adoption (no product repo was changed).
Open decisions are listed per package section above.
