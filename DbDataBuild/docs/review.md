# DbDataBuild: detailed review

Repo: `C:\Projects\DbDataKit\DbDataBuild` (submodule, read-only). `Version` 0.4.0 in `Directory.Build.props:15`; the Scoop manifest is at 0.4.1. Paths below are relative to that repo unless stated. Method: read the top-level docs (`README.md`, `CLAUDE.md`, `docs/progress/OPEN-ITEMS.md`, `REVIEW.md`), the security-relevant source and scripts, and grepped everything (yml/sql/sh/json/py/mjs) for credentials. `DESIGN.md` (1,440 lines) and `docs/progress/state-and-apply.md` (1,210 lines) were skimmed, not read. No tests were run, no packages audited (read-only, offline).

---

## 1. Purpose and architecture

**What it is.** `dbdatabuild`: a .NET 10 CLI ("dbt-like", but offline-first and plan-then-apply) that builds analytics tables and views on **SQL Server, PostgreSQL and Microsoft Fabric** from models written once in **DuckDB-dialect SQL**. A model is a `.sql` SELECT body plus a `.yml` definition declaring its columns. DuckDB binds the query offline, the tool lowers the DuckDB logical plan to one explicit query, transpiles it per target (polyglot-sql, a Rust library via FFI), and renders committed load scripts (`rendered/<connection>/...`). Deploy = read the connection (read-only login) -> write a hashed **plan file** -> a person approves -> apply exactly what the plan recorded. `refresh` runs routine loads and never changes structure.

**Status claims (README/OPEN-ITEMS).** Pre-1.0, not used in production. SQL Server 2022/2025 and PostgreSQL 17 are verified by a real-engine suite; **Fabric has never been run** (every matrix row `unverified`). OPEN-ITEMS quotes 1,817 unit tests and 184 real-engine tests; the repo has 161 test `.cs` files and about 1,187 `[Fact]/[Theory]/[SkippableFact]` attributes.

**Projects (`src/`, all `net10.0`, `TreatWarningsAsErrors`, single-file analyzer on):**

| Project | Role |
|---|---|
| `DbDataBuild.Core` | Diagnostics catalog (`Diagnostics.cs`, DDB-nnn codes), `ReadGuard`, `RewritePolicy`, `StringBuilderLf`, question/answer model |
| `DbDataBuild.Models` | Strict YAML reader (`Yaml/StrictYamlReader.cs`), `ProjectConfig(Loader)`, `ModelDefinition(Loader)`, `ConnectionKinds`, `DialectProfiles`, hooks, macros, tests, dependency graph, validators |
| `DbDataBuild.Define` | `define`: infers/diffs column definitions, surgical YAML editor/writer |
| `DbDataBuild.Targets.DuckDb` | DuckDB binding offline: `QueryDescriber`, `DuckParseTree`, `MetadataDatabase` |
| `DbDataBuild.Lowering` | `PlanLowerer`/`PlanNormalizer`: DuckDB plan JSON -> explicit query |
| `DbDataBuild.Sql` | polyglot-sql binding (`Native/PolyglotNative.cs`, `Polyglot.cs`), AST, analysis, support-matrix linter |
| `DbDataBuild.Targets` | `ITarget` (sqlserver, fabric, postgres), `ILoader` (strategies), DDL generators, rendering, per-target rewrite rules |
| `DbDataBuild.Planning` | Planner, decision table, `PlanDocument` (hashed YAML), refresh plans |
| `DbDataBuild.State` | Tracking-table DDL/schema, hashing, object shapes, source import |
| `DbDataBuild.Execution` | **Runtime DB layer**: logins, `MutationGate` (writes), `ReadSession` (reads), row sources (ADO/ODBC/DuckDB/Flight SQL), `Transfer`, statement log, tracking store, diffs |
| `DbDataBuild.Apply` | `ApplyEngine`, `RefreshEngine`, `RefreshScheduler`, input runners |
| `DbDataBuild.Sample` | Offline sample-data runs and model tests on DuckDB |
| `DbDataBuild.Tui` | Terminal.Gui TUI (a client of the JSON surface) |
| `DbDataBuild.Cli` | About 57 files: `CliApp`, `CommandSpecs`, all commands, **web UI** (`Web/`, HttpListener), **MCP server** (`Mcp/`, stdio JSON-RPC), agent-kit, template store |

**Tech stack / versions** (grep of csproj): YamlDotNet 18.1.0, DuckDB.NET.Data.Full 1.5.6, Microsoft.Data.SqlClient 7.1.1, Npgsql 10.0.3, System.Data.Odbc 10.0.13, Oracle.ManagedDataAccess.Core 23.26.301 (probably test/probe only; verify), Apache.Arrow.Flight(.Sql/.AspNetCore) 23.0.0, Apache.Arrow.Adbc.Drivers.Apache 0.23.0 (probably probes), Microsoft.SqlServer.TransactSql.ScriptDom 180.117.0, JsonSchema.Net 7.0.4, System.CommandLine 2.0.12, Terminal.Gui 2.5.0, System.Security.Cryptography.Pkcs 10.0.13. `global.json`: SDK 10.0.100, `rollForward: latestFeature`. The SQL parser is a prebuilt Rust `polyglot_sql_ffi` shared library, **not committed** (`native/` is gitignored), fetched by `scripts/fetch-native.sh` for the pinned commit (`PolyglotNative.PinnedCommit`, `src/DbDataBuild.Sql/Native/PolyglotNative.cs:27`).

**Runtime shape.** Single self-contained executable (`scripts/publish.sh`; linux-x64 and win-x64). Four front ends share one in-process command set: CLI (`--format json` for every command, schemas in `schemas/output.schema.json`), TUI (`ui terminal`), local web page (`ui web`), MCP server (`ui mcp`, plus an MCP-App HTML UI). Command groups: `project` (files only, never a DB) and `connection` (touches a DB).

---

## 2. Conceptual model

**Entities**

- **Project**: `dbdatabuild.yml` at the root (`ProjectConfig`/`ProjectConfigLoader.cs`, 627 lines). Keys: `defaults`, `connections`, `tracking`, `string_semantics` (case, trailing space, collation profile), `policy` (diagnostic severity), `plans`, `refresh`, `retention`, `dialects`. A folder-level `_dbdatabuild.yml` layers onto it (`ModelDefinitionLoader` + `Yaml/YamlMerge.cs`: lists append, maps merge, scalars replace; `key=`, `key-`, `key+` suffixes change that).
- **Connection**: a named endpoint with an `engine` keyword string (`Models/ConnectionKinds.cs`): access (`readonly`), **driver** (`sqlserver|postgres|fabric|duckdb|odbc|flightsql`) and **dialect** (`ansi|duckdb|sqlserver|postgres|fabric` or a project `dialects:` entry). Only sqlserver/postgres/fabric are build targets; duckdb/odbc/flightsql are read-only *origins* (`ConnectionKeywords.NeedsReadOnly`). The engine `version:` is declared in YAML and drives the matrix linter and the ScriptDOM grammar (`Targets/Targets.cs:24-30`).
- **Model kinds**: `view`, `full`, `incremental`, `copy`, `mapped`, `native` (`docs/models.md`). Definition keys (`ModelDefinitionLoader.cs:9`): name, kind, grain, connections, columns, renames, loads, indexes, hooks, lint_ignore, rewrites, parameters, refresh, plus info keys (title, description, tags, properties). Columns carry *logical* DuckDB-flavoured types (`INTEGER`, `VARCHAR(40)`, `DECIMAL(13, 2)`), nullability, collation.
- **Mapped / native model** (`SourceDescriptor.cs`, `schemas/source.schema.json`): declare tables the tool does not build; `native` is a query the origin engine runs (`*.native.sql`).
- **Copy model** (`Models/CopyModels.cs`): moves rows origin -> destination via a staging table in the tracking schema, including "several systems of one application into one table".
- **Seeds** (`seeds/**.sql`): SELECTs generating sample rows in DuckDB (`SeedSet.cs`, `Sample/SeedRun.cs`).
- **Hooks** (`Models/HooksAndIndexes.cs`): project-relative `.sql` scripts attached to lifecycle events, with declared `effect` (ddl/data) and `risk` (safe/risky/destructive); groups via `use:`.
- **Plan** (`plans/<connection>/<id>.plan.yml`, `Planning/PlanDocument.cs`): every statement, risk, reasons, shape hashes of live and recorded objects, a content SHA-256 (`PlanDocument.cs:15-42`); `Parse` refuses a file whose hash does not match. A **refresh plan** is compiled into `rendered/<connection>/loads.yml`.
- **Tracking tables** (`State/TrackingSchema.cs`, `TrackingDdl.cs`): a schema on a connection (can be a different connection) recording events, shapes, acknowledgements, metadata; layout versioned (currently 5) with in-place upgrade scripts (`TrackingDdl.UpgradeScript`).
- **Matrix** (`matrix/*.yml`): product data. `constructs.yml` (status per construct per engine), `strategies.yml`, `decision-table.yml`, `covered.yml`. Its hash goes into every rendered header.
- **Dialect lock** (`dialects.lock.yml`, `Models/DialectLockFile.cs`): committed result of discovering a Flight SQL server's dialect.
- **Answer files** (`AnswerFileLoader.cs`, `schemas/answers.schema.json`): non-interactive answers to planner questions.
- "Environments" in the dbt sense do not exist. The equivalent is named connections plus `--against-schema dev` comparisons and `defaults.connections`.

**YAML format and validation**
- *Strict subset* (DESIGN 6.3): `StrictYamlReader` (`Models/Yaml/StrictYamlReader.cs:6-80`) uses YamlDotNet's low-level `Parser` event stream, **never the object deserializer**. Scalars stay strings, duplicate keys are errors, anchors/aliases/merge keys/tags are rejected, multi-document files are rejected, scanner exceptions become diagnostics.
- *Semantic validation* by hand-written loaders (`ProjectConfigLoader`, `ModelDefinitionLoader`, `ProjectValidator`); each problem is a coded `Diagnostic` in `DiagnosticCatalog.All` (every code needs a fixture and `supported`/`fix`/`explanation` text, enforced by tests).
- *JSON Schemas* in `schemas/` (config, model, source, plan, answers, metadata, output) are **editor aids** ("the tool applies the same rules with diagnostic codes"); `output.schema.json` is enforced by tests for each command's JSON `data`.
- *Fuzzing*: `tests/DbDataBuild.Tests.Unit/FuzzTests.cs` asserts that a damaged file yields a diagnostic, never an exception.
- *Versioning*: there is **no `version:`/`apiVersion:` key** in config or model YAML (only the engine `version`). Evolution is handled by tool version, closed key sets (unknown key = diagnostic), the tracking-table layout number, the plan hash, the matrix hash and `dialects.lock.yml`. The tool version is stamped by `scripts/publish.sh` and deliberately kept out of rendered files (determinism).

---

## 3. Driver / provider / connection management

**Abstractions**
- **Target (build engine)**: `ITarget` (`Targets/ITarget.cs`): `Name`, polyglot `Dialect`, `ILoader Loader`, `CreateDdl(ProjectConfig)`, `Validate(sql, file, version)`. Implementations in `Targets/Targets.cs`: `SqlServerTarget` and `FabricTarget` (via `TSqlTarget`, ScriptDOM `TSql140..170Parser` by version), `PostgresTarget` (polyglot validate). Closed `TargetRegistry` of three engines; adding an engine means a new ITarget, loader, DDL generator, matrix rows and lowering rules (no plug-in model).
- **Dialect profile (origin SQL)**: `DialectProfile` record (`Models/DialectProfiles.cs:23-35`), explicitly *data*: quote characters, parameter style (`@name` / `?` / `$1`), ping statement, catalog style (information_schema / ODBC catalog functions / none), identifier case, limit style (LIMIT/TOP/FETCH), catalog-type -> logical-type map, `Extends`. Projects add their own under `dialects:`. `DialectProfile.Quote` doubles the closing quote.
- **Row sources (reads)**: `IRowSource` (`Execution/RowSources.cs`): `AdoRowSource` (SqlClient/Npgsql), `DuckDbRowSource`, `OdbcRowSource` (+ `OdbcCatalogClient.cs`), `FlightSqlRowSource.cs` (528 lines, Arrow Flight SQL over gRPC).
- **Write executors**: internal `IWriteExecutor` (`Execution/MutationGate.cs`): `AdoWriteExecutor` for SQL Server/Postgres (bulk copy via `SqlBulkCopy` / Npgsql, session advisory locks via `sp_getapplock` / `pg_try_advisory_lock`).

**Connection creation.** `LoginSettings` (`Execution/Logins.cs`) reads the connection string from env var `DBDATA_BUILD_<CONNECTION>_<READ|WRITE>` (`VariableName`, line 41). Read and write are separate logins with **no fallback** (`FromEnvironment`); a `readonly` connection refuses a write login (DDB-455). `OpenAsync` (lines ~133-146) rebuilds `NpgsqlConnection`/`SqlConnection` through the *connection-string builders*, sets `ApplicationName = dbdatabuild-read|write`, and for Postgres read logins adds `default_transaction_read_only=on`. ODBC strings go to the driver manager as written (`OdbcLogin.Normalize`: a bare name becomes `DSN=name`). A DuckDB origin is a file path opened `access_mode=READ_ONLY`. A Flight SQL login uses a URI-plus-options grammar (`FlightSqlLogin.Parse`).

**Supported engines.** Build: SQL Server 2017+ (grammar 14..17), PostgreSQL (MERGE needs 15), Fabric (unverified). Origins: DuckDB files, any ODBC driver, Arrow Flight SQL. Oracle, Spark SQL and BigQuery emulator are only dialect-probed in tests, not supported products.

**Type mapping**, three layers: (a) *logical types* = DuckDB type names (`Define/LogicalTypes.cs`: DuckDB result type -> logical type, with certainty levels); (b) per-target native declarations in `Targets/Ddl/Ddl.cs` (`TSqlDdl`: `nvarchar(n)`, `varchar` on Fabric, `DECIMAL(p,s)`, collations from `string_semantics`; `PostgresDdl`); (c) copy-time value conversion by *declared logical type* in `Execution/Transfer.cs` (`TransferValues.Convert`), duplicated as CLR/Npgsql type switches in `MutationGate.cs` (`ClrType`, `PgType`). Origin catalog type names map via `DialectProfile.Types`.

**SQL generation.** Model SQL -> DuckDB plan -> `PlanLowerer` -> polyglot transpile -> `LoaderBase` (`Targets/Loaders/TSqlLoader.cs`, `PostgresLoader.cs`) assembles per-strategy scripts around a CTE named `ddb_body`. Per-target rewrites live in `Targets/Rules/TargetRules.cs` (about 30 rules; unfaithful constructs are refused as DDB-324, never approximated). `TsqlLiterals.Nationalize` adds the `N` prefix to non-ASCII literals. Runtime values go through bound parameters, never interpolated (`RenderedParameter` doc, `ITarget.cs:5`).

---

## 4. Secrets handling

- **Where secrets live**: only in the process environment. `DBDATA_BUILD_<NAME>_READ` / `_WRITE` each hold a whole connection string (SQL Server/Npgsql/ODBC) or a Flight SQL login value. `schemas/config.schema.json` states "credentials are never stored here". No secrets file, vault, keyring or encryption (nothing is stored, so nothing is encrypted).
- **Referenced in YAML**: not referenced. YAML names a connection; the env var name is derived from that name. There is no `${VAR}` interpolation in YAML, so a YAML file cannot pull env values into output.
- **Flight SQL extras** (`Execution/FlightSqlLogin.cs`): `token`, `token_env` (name of another env var), `token_command` (a program run to get a token), `user`/`password`, `header.<name>`; all live inside the env var value.
- **Redaction / logging**
  - `LoginSettings.Describe()` prints only the variable name and user (`Logins.cs:84`).
  - Driver messages are never echoed: failures show exception type + error number (`Execution/DriverFailure.cs`); a real-engine scrub test enforces this (CLAUDE.md). `FlightSqlLoginException` messages never contain fragments of the value.
  - The statement log (`Execution/StatementLog.cs`, `.dbdatabuild/statement-log/*.jsonl`, gitignored, pruned by `retention.statement_logs_days`) records full statement text **and parameter name/value pairs** (`StatementLog.cs:25-27`). No redaction; OPEN-ITEMS B lists redaction as "not built". See F-6.
  - Web UI token: 24 random bytes (`Cli/Web/WebServer.cs:77`), shown in the printed URL.
- **Test credentials**: `scripts/test-engines.sh:19-23` hard-codes throwaway container passwords; `env` prints connection strings (F-9).

---

## 5. Query / staging / load strategies

**Reading.** Source data is read only via `ReadSession` on a read login; `ReadSession.QueryAsync` runs `ReadGuard.Check` first (`ReadSession.cs:~63-69`). For a **copy**, rows stream from the origin through `IRowSource` into `TransferValues.Convert` (declared-type conversion) and into the destination's bulk route. A local copy (origin on the same connection) skips staging and is an ordinary load.

**Staging**
- Copy: bulk-write into `<tracking schema>.stg_<schema>__<table>` (deterministic name, hash-shortened to <= 63 bytes, `CopyModels.cs`), then a normal load strategy reads the staging table (a DuckDB-dialect query "every column of the origin from the staging table"). `AdoWriteExecutor.BulkCopyAsync`: `SqlBulkCopy` in 20,000-row batches via a `DataTable` buffer; Npgsql import for Postgres. Nothing is joined across connections; no linked servers.
- Load scripts: stage into a temp table (`#` temp / `CREATE TEMP TABLE ... ON COMMIT DROP`), then DML in **one transaction** the script itself opens (`XACT_ABORT ON` on SQL Server).

**Strategies** (`matrix/strategies.yml`, `Targets/Loaders/*`, `Models/LoadOperations.cs`): `full_replace`, `delete_insert_by_key`, `merge_by_key` (SQL Server MERGE; Postgres >= 15), `delete_insert_by_range` (bound start/end), `watermark_append` (a committed resolver query gives the watermark; lookback window replaced, never duplicated), and `view` (DDL only). Each strategy has a conformance test comparing against a DuckDB reference, including rerun idempotency. Fabric rows are `unverified`.

**Type conversion.** Driven by the declared logical type (section 3); errors name the column, never the value (`TransferException`).

**Concurrency / recovery.** Session-level application lock per target (`TryLockAsync`), resume after failure (DDB-440), refusal of stale or drifted plans (live shape hash vs recorded).

---

## 6. Install / update / deploy mechanism

- **Tool install**: GitHub Releases with `dbdatabuild-vX.Y.Z-{linux-x64.tar.gz,win-x64.zip}` and `SHA256SUMS`; Windows via a Scoop manifest in-repo (`bucket/dbdatabuild.json`, hash-pinned, `checkver`/`autoupdate`). An MCP bundle `.mcpb` (`scripts/pack-mcpb.sh`, `packaging/mcpb/`) installs the read-only MCP server. No code signing or installer (OPEN-ITEMS C).
- **Release pipeline** (`.github/workflows/release.yml`): tag `v*` -> `conformance-full.yml` -> build matrix (ubuntu/windows) -> unit tests -> `scripts/publish.sh` -> smoke test -> package -> `gh release create` -> `scoop` job commits the updated manifest straight to `main`.
- **Native lib**: `fetch-native.sh` downloads `native-<pin>` release assets, verified against that same release's `SHA256SUMS`.
- **Project update**: `project update` (dialect discovery -> `dialects.lock.yml`, import sources), `project agent-kit --write` (skill + JSON Schemas for AI agents; `Cli/AgentKitCommand.cs`, temp file then move), `project create <template>` (embedded templates via `Cli/TemplateStore.cs`: starter, retail, chinook, adventureworks, fan_in_fan_out, macro_examples).
- **Deploying models**: `connection deploy` (plan -> approve -> apply), `--write-plan` / `--apply-plan <file>` (hash verified), `connection refresh` (scheduled; `Apply/RefreshScheduler.cs`), `status`, `monitor`, `compare`, `ack`. Tracking tables are created/upgraded by `init` and the layout upgrade scripts. DB permissions (read login read-only, write login limited) are the operator's job (`docs/operations.md`).

---

## 7. Candidate components for a shared library

"Coupling" = what the component drags in. Overlap with DbDataSync/DbDataProxy should be confirmed against their own reviews.

| Component | Files | Coupling / notes |
|---|---|---|
| Env-var login + redacted description | `Execution/Logins.cs` (`LoginSettings`, `Login`, `VariableName`, `Describe`, `User`) | Needs `ConnectionConfig`, `DialectProfile`, `DiagnosticCatalog`, `ProductInfo.Cli` (prefix). Extractable if prefix is parameterised. The "no fallback read -> write" rule is worth sharing. |
| ODBC-style connection string parser | `Execution/OdbcLogin.cs` (`Parse` with brace quoting, `UserOf`, `Normalize`) | Pure static, no deps; reused by the Flight SQL parser. Prime candidate. |
| Driver failure scrubbing | `Execution/DriverFailure.cs` | Depends on Odbc and gRPC types only. Ideal "never echo driver text" helper. |
| Dialect profile as data | `Models/DialectProfiles.cs`, `ConnectionKinds.cs`, `DialectDiscovery.cs`, `DialectLockFile.cs` | Quote/limit/param style/type maps. Record + quoting is self-contained; the loader is tied to the YAML layer. Strongest overlap candidate for any provider abstraction. |
| Identifier quoting | `DialectProfile.Quote`, `TrackingDdl.Quote`, `DdlGenerator.QuoteIdentifier`, inline `[..]` in `MutationGate.cs` bulk copy | Duplicated 4+ ways inside this repo; consolidate first. |
| Value conversion by logical type | `Execution/Transfer.cs`, `ClrType`/`PgType` in `MutationGate.cs`, `Execution/ArrowConversions.cs`, `Define/LogicalTypes.cs` | Logical vocabulary is DuckDB's. Any cross-engine copy needs this; coupled only to `TransferColumn`. |
| Bulk write routes | `MutationGate.cs` `BulkCopySqlServerAsync` / `BulkCopyPostgresAsync` | Coupled to `IWriteExecutor`; extractable as an `IBulkWriter`. Per-row `DataTable` buffering is a perf concern. |
| Row-source abstraction | `Execution/RowSources.cs`, `FlightSqlRowSource.cs`, `FlightSqlLogin.cs`, `OdbcCatalogClient.cs`, `SourceCatalogReader.cs` | Flight SQL client + login grammar is most reusable for a proxy. Contains the `token_command` process exec. |
| Read-only guard | `Core/ReadGuard.cs` | Regex-based (see F-3); defence in depth only. |
| Strict YAML reader + merge | `Models/Yaml/StrictYamlReader.cs`, `YamlNodes.cs`, `YamlMerge.cs`, `YamlFieldReader.cs` | Position-tracking node tree over YamlDotNet's parser; safe by construction. Depends on `DiagnosticCatalog`. |
| Diagnostics catalog | `Core/Diagnostics.cs` | Coded diagnostics with fix/explanation; pattern reusable, content is project-specific. |
| Mutation gate + statement log + app lock | `Execution/MutationGate.cs`, `StatementLog.cs`, `Statements.cs`, `GateInvariantTests` | Single choke point for writes with an audit log; a pattern to copy. Coupled to tracking schema/plan model. |
| Tracking/state tables | `State/TrackingSchema.cs`, `TrackingDdl.cs`, `Execution/TrackingStore.cs` | Engine-specific DDL for a small metadata schema; coupled to this tool's event model. |
| Hashing, deterministic text | `State/Hashing.cs`, `Core/StringBuilderLf.cs` | Trivial; share. |
| Localhost web/MCP hardening | `Cli/Web/WebServer.cs`, `Cli/Mcp/McpServer.cs`, `ToolSurface.cs` | Token + Host + Origin pattern is a good template; bound to `ICommandHost`. |
| Not shareable | `Lowering/*`, `Sql/*` (polyglot FFI), `Targets/Rules/*`, `Planning/*` | DbDataBuild's differentiator (DuckDB-SQL lowering); keep local. |

---

## 8. Security review

**Trust model.** The project directory (YAML, SQL, hooks, seeds, macros) is treated as **trusted code written by the operator**: model SQL runs offline in a sandboxed DuckDB; hook scripts and loads run on the write login. What the tool defends against: damaged YAML (diagnostics, not exceptions), driver error text, web/MCP callers, accidental writes. There is no signing of projects: a hostile cloned project followed by an approved `connection deploy` runs its hook SQL on the write login, though only after a plan is shown to a person.

| ID | Severity | Finding |
|---|---|---|
| F-1 | Medium | `token_command` runs an arbitrary program taken from the login value |
| F-2 | Medium | Supply chain: native lib checksum from same release, mutable Action tags, bot push to `main` |
| F-3 | Medium | `ReadGuard` is regex-based and has scanner gaps |
| F-4 | Medium | Hooks/native SQL are executed code; risk label is author-declared |
| F-5 | Low | Plan hash is tamper-evidence, not authentication |
| F-6 | Low | Statement log keeps parameter values and text unredacted |
| F-7 | Low | Web UI / MCP: hardened; residual notes |
| F-8 | Low | Dependency hygiene (no central versions, no audit step, mixed test package versions) |
| F-9 | Low | Hard-coded throwaway credentials in `scripts/test-engines.sh` |
| F-10 | Low | TLS and connection-string trust options pass through |
| F-11 | Info | Things checked and found sound |

**F-1 Medium: command execution via `token_command`.** `Execution/FlightSqlLogin.cs:~124-140` (`RunTokenCommandAsync`, `SplitCommand`) does `new ProcessStartInfo(parts[0])` with `UseShellExecute=false` (no shell; own argument tokenizer; 60 s timeout; output must be a single word; non-zero exit refused; output never echoed). The command comes from the env var value, not YAML, so a hostile project cannot set it. Risk: anything that can set `DBDATA_BUILD_*_READ` (a CI variable controlled by a PR author, a shared `.env`) gets code execution as the tool's user. Remediation: document as trusted-environment only, optionally require an absolute path or an allow-list, never enable in MCP/web contexts, or drop in favour of `token_env`.

**F-2 Medium: supply chain.**
(a) `scripts/fetch-native.sh:32-47` verifies the native library against `SHA256SUMS` downloaded from the *same GitHub release*; a compromised release replaces both, and the library is loaded in-process via FFI. Remediation: embed the expected SHA-256 per rid in the repo (a unit test already ties the pin commit to `PolyglotNative.PinnedCommit`; extend it), or use sigstore/attestations.
(b) Workflows use mutable tags (`actions/checkout@v4`, `setup-dotnet@v4`, `upload/download-artifact@v4`), not commit SHAs.
(c) `release.yml` `scoop` job pushes straight to `main` (`contents: write`), building the manifest from the release's own `SHA256SUMS`. It runs an inline Python heredoc; the tag reaches it via env (`TAG`), which is the correct pattern. Tag filter `v[0-9]+.[0-9]+.[0-9]+*` permits pre-release suffixes.
(d) Good: `ci.yml` and `release.yml` set top-level `permissions: contents: read`, CI uses `pull_request` (not `pull_request_target`), and the only secret used is `github.token`. `native.yml` is `workflow_dispatch`-only with `contents: write`.
Remediation: pin Actions by SHA, add Dependabot/Renovate, add build provenance attestation, open a PR for the Scoop bump or protect `main`.

**F-3 Medium: `Core/ReadGuard.cs`.** Documented as a second layer ("the read login's permissions are the real enforcement"). It requires `select|with`, no `;`, no forbidden keywords (`insert|update|delete|merge|drop|alter|create|truncate|exec|execute|grant|revoke|into|call|copy|vacuum|set`) outside quotes/comments, and rejects `$tag$`. Gaps: it does not block side-effecting or file-reading functions in a SELECT (`OPENROWSET`/`OPENQUERY`/`OPENDATASOURCE`, `pg_read_file`, `lo_import`, `dblink`, `pg_terminate_backend`, `pg_sleep`, `nextval`); `Strip` does not understand Postgres `E'..\'..'` backslash escapes (a `\'` inside an E-string desyncs the scanner and could hide a `;` or keyword); backtick-quoted identifiers are not stripped. What reaches it is the project's own `native` queries and the tool's catalog queries, and Postgres read sessions are `default_transaction_read_only=on` (`Logins.cs:~140`), so impact is bounded by DB privileges. Remediation: state least-privilege read logins as the control, handle E-strings, or replace the regex with an AST check using the already-bundled polyglot parser.

**F-4 Medium: hooks and native SQL are executed code.** `HookReader.IsSafePath` (`Models/HooksAndIndexes.cs:67-70`) correctly rejects rooted paths, backslashes, `:`, `..` segments and non-`.sql` files, so YAML cannot traverse paths. But the scripts run on the write login as authored, and `risk` (`safe|risky|destructive`) is an **author declaration** (`HooksAndIndexes.cs:47`): a hostile hook can label itself `safe`. Mitigations in the design: the plan lists every statement and needs approval; MCP withholds apply/write flags unless `--allow-writes`. Also `IsSafePath` is string-based, so a symlinked `.sql` inside the project pointing outside would be read. Remediation: derive risk from the statement AST (any DROP/TRUNCATE/DELETE forces `destructive`), require re-approval for hooks changed since the last approved plan, resolve real paths.

**F-5 Low: plan integrity.** `PlanDocument` hash is plain SHA-256 over content (`PlanDocument.cs:15-42`); anyone who can edit the file can recompute it. Apply also checks project hash, commit, live shape hashes and staleness, so post-approval tampering is largely caught as drift, but there is no HMAC/signature. If plans pass between people or CI stages, sign them or record the approved plan id+hash in the tracking DB and compare at apply.

**F-6 Low: statement log.** `StatementLog.cs:25-27` writes `text`, `hash` and parameter name+value to `.dbdatabuild/statement-log/*.jsonl` (default ACLs, gitignored, retention-pruned). Contents may include business data (resolver results, range bounds, literals in hook text), not credentials (connection strings never enter statements). Remediation: implement the planned redaction option, restrict file permissions to the user, log value types/hashes by default.

**F-7 Low: web UI and MCP (hardened).** `Cli/Web/WebServer.cs`: `HttpListener` on `127.0.0.1` only, random 24-byte token in URL then header, `Host` header check (DNS rebinding), `Origin` check, optional one-time links, read-only unless `--allow-apply`. MCP (`Cli/Mcp/McpServer.cs`): stdio only; tool surface filtered by `Impact` (write commands withheld without `--allow-writes`); options that expose row values withheld from agents (`diff --show-values`, `sample --data`); results truncated at 120,000 chars; the `.mcpb` bundle ships read-only. Residual: token in a URL query (history, logs); with `--allow-apply` any local process holding the token can apply; an agent with `--allow-writes` is exposed to prompt injection through DB-derived text (names, comments) in tool results.

**F-8 Low: dependencies.** Versions are current for the 2026 timeframe (section 1). Observations: no central package management (`Directory.Packages.props` absent; SqlClient 7.1.1 and Npgsql 10.0.3 repeated in three csproj each); test projects mix `xunit 2.4.2`/`Microsoft.NET.Test.Sdk 17.6.0`/`xunit.runner.visualstudio 2.4.5` with `2.9.3`/`17.11.1`/`2.8.2` and `coverlet 6.0.0`; `Oracle.ManagedDataAccess.Core` and `Apache.Arrow.Adbc.Drivers.Apache` look test-only (confirm they do not ship in the single-file exe); `System.Security.Cryptography.Pkcs` is pinned explicitly in three projects (likely a transitive-vulnerability override; comment it). No `dotnet list package --vulnerable` step or Dependabot config. DuckDB and polyglot-sql are native binaries outside NuGet audit. `THIRD-PARTY-NOTICES.md` records a license audit (2026-10-02).

**F-9 Low: hard-coded credentials.** Grep of yml/sql/sh/json/py/mjs/txt/md found **no real credentials** in templates, schemas, workflows or source. Only `scripts/test-engines.sh:19-23` (`FLIGHT_PASSWORD='ddbflight'`, `SA_PASSWORD='Ddb!Conf_7xQ2'`, `PG_PASSWORD='ddbconf'`, `ORACLE_PASSWORD='ddbconf'`) and its `env` output (lines 90-101), for throwaway docker containers bound to `127.0.0.1`; the suite refuses non-loopback hosts. Acceptable; expect secret scanners to flag the SA pattern, and avoid echoing `env` output in CI logs.

**F-10 Low: TLS and connection-string trust.** Connection strings are rebuilt through `SqlConnectionStringBuilder`/`NpgsqlConnectionStringBuilder` (no tool-side concatenation), so no tool-side connection-string injection; the operator owns the whole string. ODBC strings reach the driver manager as written (a `DRIVER=` option is the operator's trust decision). Flight SQL `tls_skip_verify`/`ca_file` are rejected without TLS (`FlightSqlLogin.Parse`). Test strings use `TrustServerCertificate=true;Encrypt=false` (tests only); the tool does not override SqlClient's secure defaults.

**F-11 Info: checked and sound**
- *SQL injection / identifier quoting*: identifiers are quoted with the closer doubled everywhere (`DialectProfile.Quote`, `TrackingDdl.Quote`, `Ddl.cs`, bulk-copy target in `MutationGate.cs`). Literals go through `TrackingDdl.Literal` or bound parameters (`@res`, `GateParameter`). Collation names are regex-validated (`Ddl.cs:33,63`); model, hook and group names are validated by loaders (`NamePattern`).
- *Templated SQL*: no textual templating of user input into SQL; macros are DuckDB macros (`seeds/macros.sql`, `Models/MacroLibrary.cs`).
- *YAML deserialization*: no object deserializer, no tag resolution, no aliases (grep for `Deserializer`/`YamlStream` empty).
- *Offline DuckDB sandbox*: every DuckDB instance that binds or runs user SQL sets `enable_external_access=false` and disables extension autoinstall/autoload (`Targets.DuckDb/DuckParseTree.cs:20,58`, `Sample/SampleRun.cs:54-56`, `SeedRun.cs:66`, `SeededData.cs:25`, `ModelTestRunner.cs:47`, `InputExpressions.cs:51` plus `lock_configuration`, memory/thread caps). A model cannot `read_csv('/etc/passwd')` offline.
- *Process execution*: only two `Process.Start` sites: `git` (`Cli/PlanningSession.cs:48,76`; fixed program, args quoted by a custom `Quote` at line 67 that escapes `"` but not trailing backslashes; low risk) and `token_command` (F-1). No shell or Python from the product; Python/node appear only in dev scripts.
- *Path traversal*: `HookReader.IsSafePath`; plan path compared by full path (`Cli/ApplyCommand.cs:116`); agent-kit writes fixed embedded names under `Path.GetFullPath(Path.Combine(projectRoot, dir))` (`AgentKitCommand.cs:38-49`; `--dir` is operator input). Not exhaustively audited: `project import`/`render` output paths and the `--apply-plan` file argument (operator-supplied).
- *Driver message echo*: scrubbed and tested. *Fuzzing*: seeded mutation covers all human-edited inputs.

---

## 9. Code quality, consolidation, tests, docs

**Strengths.** Very disciplined. Invariant tests that scan source (`GateInvariantTests`: writes only via `MutationGate`, reads only via `ReadSession`); closed diagnostic catalog with fixtures; determinism rules (LF text, no timestamps in rendered files); golden files with `UPDATE_GOLDEN`; a JSON-schema-per-command test; Windows CI leg; real-engine conformance (SQL Server 2022/2025, PG 17, probes for Oracle/Spark/BigQuery); differential tests against DuckDB for lowering and rewrite rules. Comments explain why; names are long and plain.

**Consolidation-relevant observations**
- The Execution layer (`Logins`, `RowSources`, `FlightSql*`, `Odbc*`, `Transfer`, `MutationGate` bulk routes) is the natural shared "connections + provider" library but is entangled with `ProjectConfig`, `DiagnosticCatalog`, `ProductInfo.Cli` (env prefix) and the tracking schema. Extraction needs a small `ConnectionDescriptor` interface in place of `ConnectionConfig`, a configurable env prefix, and splitting `MutationGate` into gate+log versus executors.
- Engine knowledge is spread over **three parallel mechanisms**: `ITarget` (build side), `DialectProfile` (origin side, data-driven) and `TrackingDdl.For(engine)` (state side), plus ad-hoc `engine == "postgres"` branches (`Logins.cs`, `MutationGate.cs`, `DriverFailure.cs`). Adding an engine touches all of them; a single provider interface would reduce this and is probably the best overlap point with the sibling projects.
- Duplicated type switches: `TransferValues.Convert`, `MutationGate.ClrType/PgType`, `LogicalTypes`, `DialectProfile.Types`, `Ddl` native mapping. Quoting is duplicated 4+ ways.
- Large files: `DESIGN.md` 252 KB, `CliApp.cs` 559 lines, `ProjectConfigLoader.cs` 627, `FlightSqlRowSource.cs` 528; many `Cli/*Command.cs` mix parsing and orchestration.
- Pre-1.0 native and preview dependencies (polyglot-sql pinned to a commit; DuckDB plan JSON "DuckDB-internal"; DuckDB 2.0 alpha breaks 9 tests) make the lowering/transpile stack a poor fit for sharing; keep it isolated.

**Fixtures versus real content (yml/sql).** Everything under `templates/` (adventureworks, chinook, retail, starter, fan_in_fan_out, macro_examples) is a **sample project** embedded as a template; seeds are synthetic-data SELECTs. `matrix/*.yml` and `schemas/*.json` are **product data** used at runtime and in tests. `spike/` (`constructs.yml`, `seed.duckdb.sql`, `RESULTS.md`, generated results) is research material, not shipped. `docs/research/duckdb-plan-lowering/` is prototype code and notes. `tests/**/Golden` are golden files. No real customer data or schemas were observed.

**Test coverage.** Unit: about 118 files, no DB needed. Conformance: 46 files, real engines in docker, skipped without env vars. Known gaps (OPEN-ITEMS C): Fabric never run; concurrency under load, half-open TCP and server restart mid-step untested; interactive answer-flow fuzzing; real-engine tests on Windows; DuckDB 2.0 migration; code signing. Tests were not run for this review.

**Docs.** User docs are strong (`docs/concepts.md`, `getting-started.md`, `commands.md` generated and test-checked, `operations.md`, `agents.md`, `interfaces.md`, `models.md`). History-heavy: `docs/progress/state-and-apply.md` (numbered entries), `OPEN-ITEMS.md` (sections A-U), `REVIEW.md`, `docs/research/*`. Fabric sections are flagged unverified.

**Quick wins (security + consolidation)**
1. Pin GitHub Actions by SHA; add Dependabot and a vulnerable-package check; add `Directory.Packages.props`.
2. Embed native-library SHA-256 values in the repo and verify on fetch.
3. Replace or augment `ReadGuard` with a polyglot AST check; handle `E'..'`; document least-privilege read logins as the real control.
4. Derive hook risk from the statement AST; resolve symlinks in `IsSafePath`.
5. Add statement-log redaction and user-only file permissions.
6. For consolidation, lift `OdbcLogin`, `DriverFailure`, `DialectProfile`+`ConnectionKinds`, `TransferValues`, `FlightSqlLogin` and the gate/log pattern into a shared library behind one provider interface.
