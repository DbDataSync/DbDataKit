# DbDataSync - Review Detail

Reviewed: `C:\Projects\DbDataKit\DbDataSync` (git submodule, read-only; HEAD `78adace`). Paths below are relative to that
repo unless stated. Static review only: nothing was built, run or restored, so `dotnet list package --vulnerable` and
`npm audit` were NOT run; dependency notes in 9.14 come from reading versions only.

## 1. Purpose and architecture

DbDataSync is a cross-database replication tool. An operator defines a replication (source table, target table, column
mappings, schedule, change-processing strategy) in a web UI; DbDataSync does an initial bulk load, then applies
incremental changes on a schedule. Source and target may be different engines. Distributed as a .NET global tool (NuGet id
`DbDataSync`), a Windows service, a systemd service, or a container image on GHCR (`README.md`, `docs/install.md`, `Dockerfile`).

Tech stack: .NET 10 (`net10.0` in every csproj), ASP.NET Core controllers + SignalR, React 19 / TypeScript 6 / Vite 8 SPA
(`src/DbDataSync.Web/package.json`: Monaco, TanStack Query, react-router 7, react-markdown), YamlDotNet 18.1.0, LibGit2Sharp
0.32.0, Roslyn 4.14, Fido2.AspNet 4.0.1 (passkeys), Terminal.Gui 2.5.0 (setup TUI), Parquet.Net 6.1.0 + DuckDB.NET 1.5.5
(verification, DuckDB query source), IKVM 8.16.1 (JDBC bridge). Size: ~64k LOC C# in `src/` (21 projects), ~64k LOC in 394 C#
test files, 115 SPA source files, 200+ numbered "phase" design docs in `architecture/implementation/{done,todo}`.

| Project (`src/`) | Role |
|---|---|
| `DbDataSync.Core` (7.0k LOC) | Config model + YAML, `ConfigRepository` (git-backed), validation, `Sql/*Dialect`, secret-ref naming, git commit service |
| `DbDataSync.Drivers.Abstractions` (2.0k) | `IDriver`, `IChangeReader`, `IStagingProvider`, `IChangeWriter`, `DriverRegistry`, capabilities, metadata records |
| `DbDataSync.Drivers.Generic` (6.4k) | `GenericDriver` over any `DbProviderFactory`: Watermark/BatchReload/TriggerAudit readers, StagingTable, DeleteInsert/Snapshot/SCD2/KeyReconcile writers, information_schema catalog |
| `Drivers.{MsSql,Postgres,MySql,Oracle,DuckDb}` | Built-in engines; MsSql (CT/CDC, SqlBulkCopy, MERGE) and Postgres (logical decoding, binary COPY) have native extras |
| `DbDataSync.Drivers.Descriptor` (0.9k) | `driver.yaml` reader, `DescriptorDialect`, `DriverLoader` (descriptor + compiled plugin), `KnownDrivers` catalog |
| `DbDataSync.Drivers.Jdbc` (1.7k) | Hand-written ADO.NET facade over `java.sql.*` via IKVM, `JdbcGenericDriver` |
| `DbDataSync.Libraries` (1.1k) | NuGet "library" install (via `dotnet publish`), `KnownLibraries` catalog, Cecil/reflection compatibility check |
| `DbDataSync.Scripting(.Abstractions)` (2.2k) | Roslyn-compiled C# extension scripts (query builders, transforms, hooks, custom segmenting) |
| `DbDataSync.State` (6.9k) | Run history, work queue, watermarks, users/sessions/invites; SQLite default, MsSql/Postgres optional; remote runner-state protocol |
| `DbDataSync.TaskRunner` (2.1k) | One OS process per run: read, stage, apply |
| `DbDataSync.Api` (14.3k) | ASP.NET host: REST, SignalR `RunHub`, scheduler, process supervisor, auth, loopback-only state server |
| `DbDataSync.Cli` (7.2k) | `dbdatasync` tool: `serve`, `setup` (TUI), `service`, `cert`, `config`, `driver`, `library`, `invite`, `update`, `health` |
| `DbDataSync.Certificates` (1.4k) | Windows cert store, ADCS enrollment, managed self-signed cert |
| `DbDataSync.Updates` (1.8k) | Self-update (NuGet + GitHub snapshot prereleases, SHA-512 sidecar) |
| `DbDataSync.Verification` (0.6k) | Source-vs-target verification, Parquet/DuckDB result files |

Runtime topology (`architecture/detailed-design.md` section 2; `src/DbDataSync.Api/DbDataSyncHost.cs`):
- One long-lived API process serves SPA + REST + SignalR and hosts the scheduler (`SchedulerService`), run monitor and pruning, plus
  a **second loopback-only Kestrel server** (`Api/State/StateHost.cs`) that spawned runners use to write run state and config
  (`RunnerStateGuard` + per-process random `RunnerToken` handed to children through the environment, not argv).
- Per run, `ProcessSupervisor` (`Api/Services/ProcessSupervisor.cs:110`) spawns `dotnet exec DbDataSync.TaskRunner.dll ...` using
  `ArgumentList`, no shell. Data movement never happens in the API process.
- Config lives in a git repo (`<RepoRoot>/config/...`) auto-committed on each API save via LibGit2Sharp
  (`Core/Git/GitCommitService.cs`). Run state lives in a DB (SQLite default). Secrets live in the OS secret store.
- App settings: `dbdatasync.config.yaml` inserted as a configuration source ahead of env vars (`DbDataSyncHost.InsertConfigFile`, line 453), keys `DbDataSync:*`.

## 2. Conceptual model

Definition format: **YAML files in a git repo** (diffable; git history is the audit trail and drives the "Version Control" tab).
Layout (`Core/Config/ConfigPaths.cs`):
```
<RepoRoot>/config/connections/<name>.yaml
<RepoRoot>/config/replications/<name>/task.yaml
<RepoRoot>/config/replications/<name>/table-mappings/<mapping>.yaml
<RepoRoot>/config/scripts/<name>.yaml + <name>.cs|.sql
<RepoRoot>/drivers/<id>/driver.yaml | driver.json (+lib/)   # engine extensibility
<RepoRoot>/libraries/<id>/library.json (+lib/)              # NuGet closures
<RepoRoot>/files/*.jar                                      # user-supplied JDBC jars
<RepoRoot>/dbdatasync.config.yaml                           # app settings
```
Operational state (not config) lives in the state DB: runs, logs, watermarks, work queue, users, sessions, invites, notifications,
verification results (`State/*Store.cs`, `State/Migrations.cs`).

Key entities (`Core/Config/`):
- **ConnectionConfig** (`ConnectionConfig.cs`): `Name`, `DriverType` (string id, not enum), host/port XOR engine-native `ConnectionString`
  (`ConfigValidation.ValidateAddressing`), `Database`, `AuthMode {SqlAuth, IntegratedAuth, None}`, `UserId`, `CredentialSecretRef`,
  connect/command timeouts, free-form `Properties`, script/hook bindings, `TestQuery`. `ConnectionInput` is the write-side type that may
  carry a plaintext `Password` and is never serialized.
- **ReplicationTaskConfig**: scheduling (continuous or cron), `ChangeProcessingConfig` (reader/staging/writer Kinds + options +
  parallelism), `BulkLoadConfig`, source/target endpoints (connection + database), `ReconcileConfig` (delete detection),
  `SegmentingStrategies`, hooks, scripts, provisioning, read intent, notes.
- **TableMappingConfig**: `SourceTableSpec` (schema/table, optional raw `Query`, `Filter`, `AllowSubquery`), target `TableSpec`, column mappings
  with SQL/scripted transforms, relationships/foreign columns, cached column metadata, per-mapping pipeline overrides, verification checks.
- **Strategies are string "Kinds" per driver**: readers (`Watermark`, `BatchReload`, `TriggerAudit`, `KeyReconcile`; native `ChangeTracking`,
  `Cdc`, `PgLogicalSlot`, `OracleFlashback`, `ScriptedQuery`, `DuckDbQuery`), staging (`StagingTable`; native SqlBulkCopy-based and `PgCopy`), writers
  (`DeleteInsert`, MsSql `Merge`, `Snapshot`, `Scd2`, `KeyReconcileDelete`, `KeyReconcileScd2Close`). A driver advertises supported Kinds; config validation
  rejects invalid combinations (`ConfigValidation.cs` ~240-330).
- **Jobs**: each scheduled/manual/bulk-load invocation is a row in `WorkQueueStore` and a `TaskRun`; bulk loads split into **segments** (range/list/auto,
  `BatchReloadSegment*`, `SegmentingStrategyConfig`) run in parallel lanes (`TaskRunner/WorkerLanes.cs`). Per-mapping watermarks in `ChangeWatermarkStore`.
- **Scripts**: a global registry bound at connection/replication/mapping level with mapping winning (`ScriptResolution`); slots: metadata provider, query builder,
  column transforms, custom segment expansion, lifecycle hooks (C# or SQL).
- **Driver descriptor / Library**: `driver.yaml` (declarative engine over a `DbProviderFactory`) and `library.json` (NuGet package + factory type), see section 3.

Validation is at save time (`ConfigValidation`, `ParameterValidation`, `HookValidation`, compile-before-save for scripts); invalid config is rejected 400.

## 3. Driver / provider / connection management

Abstraction (`Drivers.Abstractions/IDriver.cs`): `IDriver` exposes `Readers`, `StagingProviders`, `Writers`, `CreateConnection(ConnectionConfig, string? credential)`
(returns an unopened `DbConnection`) and catalog methods (`ListDatabases/Tables/Columns`). Optional capability interfaces: `IConnectionTester`,
`IConnectionPreviewer`, `IDialectProvider`, `IProvisioner`, `IReadIntentDeclaring`, `ITableRowEstimator`. `ContractVersion` gates compiled plugins.
`DriverRegistry` keys drivers by string id and describes capabilities to the UI.

Four ways a driver exists:
1. **Hand-written built-ins**: `MsSqlDriver`, `PostgresDriver`, `MySqlDriver`, `OracleDriver`, `DuckDbDriver` (registered in `DbDataSyncHost.cs:185-220` and again in
   the TaskRunner's own composition root). Each has its own connection-string builder (`MsSqlDriver.cs:91`) and catalog (`*Catalog.cs`).
2. **`GenericDriver`** (`Drivers.Generic/GenericDriver.cs`, `GenericDriverBase.cs`): any ADO.NET `DbProviderFactory` + a `GenericDriverSpec`. The connection string is assembled with a
   generic `DbConnectionStringBuilder` (`GenericDriver.cs:34-92`): fixed properties -> operator `ConnectionString` or host -> database -> port -> timeout -> auth -> `connection.Properties` (last, can override anything).
3. **YAML descriptor** (`Drivers.Descriptor/DriverDescriptorYaml.cs`, `DriverLoader.cs`): `drivers/<id>/driver.yaml` declares `library`, `dialect` (`quoteIdentifier`, `parameterPrefix`,
   `rowLimit` limitOffset/offsetFetch/topN, tie-safe flag, connection-string key spellings, `fixedProperties`), `typeMap` (native -> canonical with `(p,s)` placeholders), `capabilities`,
   optional `metadataQueries` (operator catalog SQL) and `base` (reflection hook that selects the JDBC driver class). Curated entries: `Resources/mysql.generic.driver.yaml`, `mssql.odbc.driver.yaml` via `KnownDrivers.cs`.
4. **Compiled plugin** (`driver.json` + DLL in `drivers/<id>/lib`, per-plugin `DriverPluginLoadContext`) and **JDBC** (`Drivers.Jdbc`): an ADO.NET shim (`Jdbc/Ado/*`) over IKVM `java.sql`, with `UrlTemplate`/`ConnectionStringKeys`
   and jars from `<repo>/files/`. JDBC is read-oriented; write support is a todo (phase 172V).

Libraries (`Libraries/`): client packages are not compiled in. `KnownLibraries.cs` pins packages (incl. `system-data-odbc`, `mysql-connector`, `ikvm`); `LibraryInstaller` writes a throwaway
csproj and runs `dotnet publish` into `<repo>/libraries/<id>/lib/` (`LibraryInstaller.cs:196-246`). The runtime-only container image falls back to a pre-built `/app/library-cache`.
Built-in drivers auto-install their library on first connect (`State/BuiltInDriverLibraries.EnsureInstalledAsync`). `DriverLibraryCompatibility` statically checks (Cecil/MetadataLoadContext) that the installed library has the members the driver calls.

Connection lifecycle: no DbDataSync pool. `DriverConnectionFactory.OpenAsync` (`Api/Services/DriverConnectionFactory.cs`) loads config, resolves the credential from `SecretStore`, calls `driver.CreateConnection`,
opens; callers dispose. Pooling is whatever the provider does. Command timeouts are stamped on the connection (`WithCommandTimeout`) and read by `CreateTimedCommand()`. MsSql forces MARS on (`MsSqlDriver.cs:130-150`).

Dialects: `Core/Sql/SqlDialect.cs` (abstract) with `MsSqlDialect`, `PostgresDialect`, `MySqlDialect`, `OracleDialect`, `DuckDbDialect`, `JdbcDialect` in Core; `Descriptor/DescriptorDialect.cs` is the data-driven one.
Responsibilities: `QuoteIdentifier` (escapes closing delimiter), parameter reference/name, `QualifyTable`, quote-aware `SplitQualifiedName`, `UseDatabaseAsync`, row limits incl. tie-safe, literal rendering, DDL fragments, native<->canonical type mapping.
The doc comment states dialect is only for "small mechanical differences"; structural differences go in engine-specific Kinds.

Type mapping: `Core/Sql/CanonicalType.cs` defines a canonical type set (Boolean, Int8..Int64, Decimal, Float, Double, String(+unicode/max/fixed), Binary, Date, Time, Timestamp, TimestampTz, Guid, Json, Xml, Unmappable).
Each dialect maps native -> canonical -> native returning `RenderedColumnType` with a fidelity note; `CreateTargetTablePlanner`/`AlterTargetTablePlanner` refuse `Unmappable`.

Supported engines: SQL Server (CT, CDC, trigger audit), PostgreSQL (wal2json logical decoding, trigger audit), MySQL/MariaDB (watermark/trigger audit), Oracle (Flashback Version Query, trigger audit),
DuckDB (query-only source), plus anything reachable through descriptor + ADO.NET, ODBC or JDBC jars (mostly as sources).

### The existing ODBC commit (`1543502`, "Add ODBC support via the existing generic descriptor mechanism, not a new base class")
- Design: ODBC reuses `GenericDriver` over `System.Data.Odbc.OdbcFactory` (library id `system-data-odbc`, already in `KnownLibraries.cs:59`). One new mechanism,
  `GenericDriverSpec.FixedConnectionStringProperties` / `DescriptorDialectYaml.FixedProperties` (`Generic/GenericDriver.cs:39-45`): descriptor-owned connection-string entries (`Driver={...}`, `DSN=...`)
  applied first and overridable by operator values.
- Ships catalog entry `mssql.odbc` (`KnownDrivers.cs:34-39`, `Resources/mssql.odbc.driver.yaml`): bracket quoting, `rowLimit: topN`, Watermark + BatchReload readers, StagingTable, DeleteInsert, `TrustServerCertificate: "yes"`,
  `Login Timeout`, no port key (port must be composed into `Server` by the operator via `Properties`).
- Tests: only connection-string assembly unit tests (`tests/DbDataSync.Drivers.Generic.Tests/GenericDriverFixedConnectionStringPropertiesTests.cs`) and catalog round-trips. **No end-to-end ODBC execution anywhere**; the commit message says no vendor ODBC driver was available.
- Risks (read from code, not run):
  1. **Named parameters.** Generic readers/writers render `@name` markers and bind by name (`DbCommandExtensions.AddParameter`). `System.Data.Odbc` binds strictly by position and does not rewrite `@name` to `?`.
     The JDBC shim implements its own name->position translation (`Jdbc/Ado/JdbcCommand.cs:15-24`, whose comment wrongly says OdbcCommand does this too). No such translation exists for ODBC, so parameterised statements
     (watermark compare, segment bounds) will likely fail, leaving `mssql.odbc` usable only for trivial queries.
  2. **Connection-string quoting** uses the ADO.NET generic builder (double quotes) instead of `OdbcConnectionStringBuilder` (braces); passwords with `;`, `}` or `=` can be mis-parsed (see 9.8).
  3. The ODBC typeMap is a hand copy of `MsSqlDialect` (drift risk).
- Positive: tiny change surface, no new driver class, reuses descriptor validation and the catalog.

## 4. Secrets handling

- **Store**: external package `ClrKernel.Core.Secrets` 0.11.0 (`new SecretStore("DbDataSync", true)`, `DbDataSyncHost.cs:97`). OS keyring (Windows Credential Manager, macOS Keychain, Linux Secret Service) with env-var fallback
  `DBDATASYNC_SECRET_<REF_UPPERCASED>` (`docs/configuration.md` "Secrets" ~line 511). Internals (fallback encryption/memory handling) are outside this repo and were not reviewed. This is clearly the author's shared library and the obvious shared-secrets anchor across the three siblings.
- **Refs** (`Core/Secrets/SecretRefs.cs`): `dbdatasync:connection:<name>`; `dbdatasync:config:<key>` for app settings (only `stateConnectionString`).
- **Write path**: `ConfigRepository.SaveConnection` (`Core/Config/ConfigRepository.cs:31-95`) stores `ConnectionInput.Password` in the store and serializes only `CredentialSecretRef`. Embedded-credential keys
  (`password|pwd|passwd|secret|accountkey|apikey`) in `ConnectionString` are rejected before any side effect (`ConfigValidation.cs:55-120`); the same check guards `State:ConnectionString`. Delete removes the secret (line 110).
- **Resolve path**: at connect time only (`DriverConnectionFactory.OpenAsync`, TaskRunner's equivalent), passed as a plain `string` to `IDriver.CreateConnection`, spliced into the connection string last. The TaskRunner is a separate process
  and resolves secrets itself from the OS store (service account needs store access). It gets the loopback URL and token via environment (`ProcessSupervisor.cs:132-136`).
- **To the UI**: `GET /api/connections` returns config without password (ref only). `/credential-source` returns ref name and env var name. Passwords are write-only. Test-connection output passes through `ConnectionDiagnostics.Redact`
  (`Api/Services/ConnectionDiagnostics.cs`, exact-substring replace of the resolved credential in errors, preview connection string and JDBC URL); previews use a masked credential (`GenericDriver.cs:108`).
- **Logging**: no credential/connection-string logging found by grep. The bootstrap invite code IS logged and written to `FIRST-RUN.txt` (9.9).
- **State DB password**: stored via the same store (`Api/Services/AdminConfigService.cs:230-240`); admin config screen has an `IsSecret` flag.
- Gaps: `ConnectionConfig.Properties` is not scanned for credentials (9.4); session ids stored in cleartext (9.7); `Redact` handles only the exact literal.

## 5. Query / staging / load strategies

Per run (`TaskRunner/RunExecutor.cs`): read -> stage -> write -> advance watermark.
- **Readers** (`IChangeReader.ReadChangesAsync` returns a streaming `ReadResult` of `ChangeRow` plus a position string): generic `WatermarkReader`, `BatchReloadReader`, `TriggerAuditReader` (engine triggers write an audit table;
  per-engine `*TriggerAudit.cs`), `KeyReconcileReader` (delete detection by key comparison); native `MsSqlChangeTrackingReader`, `MsSqlCdcReader`, `PgLogicalSlotReader`, `OracleFlashbackReader`; `DuckDbQueryReader`/`ScriptedQueryReader` for query sources.
  Bounded reads use tie-safe row limits (`RenderTieSafeRowLimit`). Relationship joins add foreign columns (`RelationshipJoins.cs`, `SourceProjection.cs`). `ReadIntent` controls read consistency.
- **Staging** (`IStagingProvider`): `BatchInsertStagingProvider` (generic parameterised batched INSERT into a real staging table), `MsSqlStagingTableProvider` (`SqlBulkCopy` via `ChangeRowDataReader`),
  `PgCopyStagingProvider` (binary `COPY ... FROM STDIN`, explicit per-type coercion in `PostgresValues.cs`). Always a target-side staging table; columnar/Parquet staging is a deferred todo (phase 150).
- **Writers**: generic `DeleteInsertWriter` (delete by key then insert from staging; no portable upsert), `MsSqlMergeWriter`/`MsSqlMergeReconcileWriter` (MERGE), `MsSqlDeleteInsertWriter`, temporal `SnapshotWriter` and `Scd2Writer`
  (`HistorizedStatement`), `KeyReconcileDeleteWriter`, `KeyReconcileScd2CloseWriter`. Delete guards (`DeleteGuard` ratio) and after-change strategies limit runaway deletes.
- **Cross-engine value conversion**: per-engine `*ValueBinding.cs` convert source CLR values to provider parameters/bulk values; `GenericValueBinder` for the generic path; target table creation goes through canonical types.
- **Segmenting** for parallel bulk loads: range/list/auto, `SegmentExpansion`, custom C# strategy scripts, relationship-column segmenting/watermarking (phase 195S).
- **Hooks**: SQL or C# lifecycle hooks around read/stage/write (`HookRenderer`, `ScriptHost`).

## 6. Install / update / deploy

- `dotnet tool install -g DbDataSync` then `dbdatasync setup` (Terminal.Gui TUI, `Cli/Tui/*`). Also machine-wide tool install, Windows service (`Cli/ServiceCommand.cs`, `Microsoft.Extensions.Hosting.WindowsServices`), systemd (`Cli/SystemdService.cs`),
  container (`Dockerfile`; default SDK-based image so web-triggered library install can run `dotnet publish`; `--target runtime` is an SDK-less image with a library cache).
- Release pipeline (`.github/workflows/`): `release.yml` (manual dispatch; NuGet Trusted Publishing via OIDC, no stored NuGet key; tag + GitHub Release; image via `publish-image.yml` to GHCR), `publish-snapshot.yml`
  (prerelease nupkg per promoted commit), `promote-test.yml` (dev -> test fast-forward after green CI through a GitHub App token), `ci.yml` (dotnet, frontend, Playwright, package jobs). dev/test/main model in `architecture/branching-and-releases.md`.
- Self-update (`DbDataSync.Updates`): opt-in (`Updates:Mode` default `Disabled`, `Api/Configuration/ApiOptions.cs:41-44`); versions from nuget.org flat container and GitHub releases (`ReleaseSources.cs`); snapshot download only from the repo's
  `releases/download/` prefix (`SnapshotStager.cs:154`) with SHA-512 sidecar check and `.partial` rename; work drain (`UpdateDrainMiddleware`), `dotnet tool update/install` (`UpdateApplier`), `UpdateConfirmationService` after restart.
- Data directory: `DbDataSync__App__RepoRoot` (container `/var/lib/dbdatasync`).
- TLS: Kestrel certificate config; BYO cert, Windows store + ADCS enrollment, or managed self-signed (`Certificates/*`, `SelfSignedCertificateService`). Default bind is plain `http://localhost:5080`; container binds `http://0.0.0.0:8080`.

## 7. Configuration and definition schemas, validation

- **No JSON Schema/YAML schema files**: the schema is the C# POCOs (`Core/Config/*.cs`), serialized by YamlDotNet CamelCase with `OmitDefaults` plus hand-written converters for polymorphic types
  (`BatchReloadSegmentYamlConverter`, `DeleteGuardYamlConverter`, `AfterChangeStrategyYamlConverter`, `Descriptor/TypeMapEntryYamlConverter`).
- Core config deserializes with `IgnoreUnmatchedProperties()` (`YamlConfigSerializer.cs:31`), so typos or removed keys silently vanish on next save; the descriptor reader is strict (`DriverDescriptorReader.cs:12-15`).
- Validation: `ConfigValidation.ValidateName` (letters/digits/`-_.`, no `..`, no leading/trailing dot; becomes a file name), `ValidateAddressing`, kind-combination rules, segment/watermark rules, `ParameterValidation`
  driven by each driver's declarative `ParameterDescriptor`s (UI forms generated from the same descriptors), `HookValidation`, `DriversController.Validate` (in-memory build + resolved-shape echo), compile-on-save for scripts.
- App settings: `dbdatasync.config.yaml` flattened to `DbDataSync:*` (`DbDataSyncConfigFile.cs`), mode enums instead of booleans (phase 164), edited through `/api/admin/config` with `RestartRequiredState`.
- Library manifest `library.json` (`LibraryManifest.cs`) and compiled-driver `driver.json` (`CompiledDriverManifest.cs`) are JSON.

## 8. Reusable components, shared-library candidates

| Component | Paths | Coupling | Notes |
|---|---|---|---|
| Secret store wrapper + ref naming | external `ClrKernel.Core.Secrets`; `Core/Secrets/SecretRefs.cs` | Low | Already external; only the ref convention is local. Best anchor for a shared secrets contract. |
| Embedded-credential detector | `Core/Config/ConfigValidation.cs:55-120` | Low (string only) | Trivial to lift. |
| Redaction helper | `Api/Services/ConnectionDiagnostics.cs` | Low (`Describe` depends on `JdbcSqlException`) | Extract `Redact` only. |
| `SqlDialect` family + canonical types | `Core/Sql/*` (`SqlDialect`, `CanonicalType`, `*Dialect`, `SelectListFormatting`) | Low-medium (Core also holds config types) | Strongest reusable cross-engine knowledge: quoting, literals, row limits, type mapping. Engine dialects live in Core and would need splitting. |
| YAML driver descriptor mechanism | `Drivers.Descriptor/*`, `Drivers.Generic/GenericDriver*.cs` | Medium | "Describe an ADO.NET engine in YAML" (dialect, typeMap, key spellings, fixedProperties). Share if siblings model engine differences. |
| Library/NuGet closure installer + compat checker | `Libraries/*` | Medium (reads `IDriver`) | Self-contained "install provider package on demand"; security-sensitive (9.2). |
| Known ADO.NET provider catalog | `Libraries/KnownLibraries.cs` | Low | Package id -> factory type table. |
| Connection abstraction | `ConnectionConfig`, `AuthMode`, `IDriver.CreateConnection`, `DriverConnectionFactory`, `IConnectionTester/Previewer`, `ConnectionTimeouts` | Medium | Direct overlap if siblings manage DB connections; `ConnectionConfig` embeds script/hook binding and needs slimming. |
| Git-backed YAML store with auto-commit | `Core/Git/GitCommitService.cs`, `Core/Config/ConfigRepository.cs`, `YamlConfigSerializer.cs` | Medium | `GitCommitService` extractable; `ConfigRepository` is a god object over all entity types. |
| Auth stack (session cookie, invites, passkeys, Windows groups, closed-by-default fallback) | `Api/Auth/*`, `State/{User,Session,Invite}Store.cs` | Medium | Reusable if siblings have a web surface. |
| Loopback state endpoint with token guard | `Api/State/StateHost.cs`, `RunnerStateGuard.cs`, `State/Remote/*` | Medium | Pattern for supervised workers. |
| Certificate management | `DbDataSync.Certificates/*` | Low-medium (Windows) | Standalone project; reusable for any Kestrel host. |
| Self-update | `DbDataSync.Updates/*` | Low (hardcoded `ReleaseSources` repo/package id) | Parameterize and reuse. |
| Roslyn script host + allow-list + assembly cache | `DbDataSync.Scripting/*` | Medium | `ScriptCompiler`, `ScriptSyntaxGuard`, `ScriptCacheDirectory` generic; slots are replication-specific. |
| Declarative parameter descriptors (forms + validation) | `Core/Config/ParameterDescriptor.cs`, `ParameterValidation.cs`, `Abstractions/DriverParameters.cs` | Low | Schema-driven form pattern. |
| State store over SQLite/MsSql/Postgres | `State/StateDatabase*.cs`, `*StateDialect.cs`, `Migrations.cs` | Medium | Hand-rolled mini-ORM with a third dialect hierarchy. |

Not reusable as is: the replication pipeline (readers/writers/segments), TaskRunner, Verification, SPA (component-level only).

## 9. Security review

"Admin" means the Admin role, who can by design already run arbitrary C# and install NuGet packages (9.1).

| # | Finding | Rating |
|---|---|---|
| 9.1 | Admin can execute arbitrary code (scripts, NuGet, compiled drivers, jars, descriptor `base`): accepted by design | Medium |
| 9.2 | Library install: unvalidated `PackageId`/`Version`/id interpolated into generated csproj and a path; delete-by-id traversal | High |
| 9.3 | Path traversal via unvalidated driver ids and file names | Medium |
| 9.4 | `ConnectionConfig.Properties` exempt from credential rule and Viewer-readable | Medium |
| 9.5 | SQL Server `TrustServerCertificate=true` default (also ODBC catalog entry) | Medium |
| 9.6 | Network-trust auth unsafe behind same-host proxy; plain-HTTP default; `AllowedHosts: *` | Medium |
| 9.7 | Session ids stored unhashed | Low |
| 9.8 | ODBC connection-string quoting; `Properties` can override `Password` | Low |
| 9.9 | Bootstrap invite in log and `FIRST-RUN.txt` | Low |
| 9.10 | CI: no `permissions:` in `ci.yml`, tag-pinned actions, container runs as root, no scanning | Low-Medium |
| 9.11 | ODBC named-parameter gap (correctness) | Info |
| 9.12 | Self-update checksum is same-release (integrity not authenticity) | Low |
| 9.13 | Hardcoded test credentials | Low |
| 9.14 | Dependencies not scanned | Info |
| 9.15 | SQL injection review: good hygiene, Admin raw SQL by design | Low |

### 9.1 Arbitrary code execution by Admin (by design) - Medium
Evidence: `Scripting/ScriptCompiler.cs:19-45` compiles operator C# with a reference allow-list but states "a guardrail, not a security boundary". `LibrariesController.Create`
(`Api/Controllers/LibrariesController.cs:69-96`) installs any package from any `Source` feed into the API process. `DriverLoader.LoadCompiledDrivers` loads arbitrary DLLs.
`DriverDescriptorReader.BuildDriver` (`:44`) does `Type.GetType(descriptor.Base)` and reflectively invokes a public static `FromDescriptor`. `FilesController` accepts `.jar` (200 MB) later loaded by IKVM.
`POST /api/connections/{n}/query-preview` runs operator SQL. All Admin-gated (fallback policy `DbDataSyncHost.cs:337-347`, or explicit attributes). Impact: Admin == code execution as the service account and access to every stored credential.
Recorded as accepted in `architecture/planning/done/csharp-scripting-host.md`.
Remediation: document prominently; run service least-privileged; allow-list NuGet sources/package ids unless an explicit "allow arbitrary" mode is set; keep `Updates:Mode` and library install off in hardened deployments; consider a role between "edit mappings" and "install code".

### 9.2 Library install injection and traversal - High
Evidence: `LibrariesController.Create` only checks non-empty `PackageId`/`Version` (`:69-76`). `PackageId` becomes the library id, and `LibraryPaths.LibraryDir = Path.Combine(<repo>/libraries, id)` (`Libraries/LibraryRegistry.cs:225`) has no name validation.
`LibraryInstaller.PublishAsync` builds XML by interpolation, `<PackageReference Include="{p.Id}" Version="{p.Version}" />` (`LibraryInstaller.cs:199`), then runs `dotnet publish`: a crafted `Version` can close the element and add an MSBuild `Exec` target.
`DELETE /api/libraries/{id}` passes `id` to `Directory.Delete(libraryDir, recursive: true)` (`LibrariesController.cs` ~100-120); a traversing id escapes `libraries/`. `Source` is unvalidated too.
Remediation: NuGet-id regex for `PackageId`, NuGet version grammar for `Version`, `ConfigValidation.ValidateName` for ids; build the csproj with `XElement`; `GetFullPath` + prefix check inside all `*Paths` helpers; restrict `Source` to configured feeds.

### 9.3 Path traversal via ids and file names - Medium
Evidence: `DriversController.Create` writes to `Path.Combine(RepoRoot, "drivers", descriptor.Id)` (`:199`) where `Id` is operator YAML with no validation (`DriverDescriptorYaml.Id` is only `required string`). `GetYaml`, `Status`, `UpdateYaml` use the route `{id}` the same way
(`:81,103,219`); `Status` is `[Authorize(Policies.Viewer)]`, so a Viewer can probe `<x>/driver.yaml` existence and get parse-error text. `FilesController.Delete` (`FilesController.cs:74-90`) uses `Path.Combine(files, name)` without the `Path.GetFileName` that upload applies.
Config entity names are well protected by `ConfigValidation.ValidateName` (good pattern to reuse). Whether encoded separators survive route decoding is host/OS dependent; verify.
Remediation: apply `ValidateName` or `GetFileName` equality to every id/name centrally in the `*Paths` helpers.

### 9.4 Credentials in `Properties`; Viewers read all connection config - Medium
Evidence: `RejectEmbeddedCredential` runs only on `ConnectionString` (`ConfigValidation.cs:55-60`). `ConnectionConfig.Properties` is serialized verbatim to git-tracked YAML and applied last (`GenericDriver.cs:88-89`, `MsSqlDriver.cs:121-123`); it can hold `Password`, wallet
passwords, tokens. `GET /api/connections` and `/{name}` are Viewer (`ConnectionsController.cs:31-55`), as is config history. The ODBC YAML itself suggests `connection.Properties.Server`.
Remediation: run the key detector over `Properties`; offer secret-valued properties resolved through `SecretStore`; redact for non-Admin.

### 9.5 TLS validation off by default for SQL Server - Medium
Evidence: `Drivers.MsSql/MsSqlDriver.cs:130-150` sets `TrustServerCertificate = true` unless the operator set it (comment: "Dev/test default ... Revisit before any hardened-production deployment guidance ships"). `Resources/mssql.odbc.driver.yaml:19` bakes `TrustServerCertificate: "yes"`;
`tools/DbDataSync.DevHarness/Scenario.cs:176` too. Impact: MITM of replication traffic and credential capture. Other engines use provider defaults (not explicitly configured).
Remediation: default to encrypt + verify; expose an explicit per-connection trust toggle; remove from catalog YAML.

### 9.6 AuthN/AuthZ - strong baseline, three caveats - Medium
Strengths: fallback policy requires authenticated Admin (`DbDataSyncHost.cs:344-347`), explicit Viewer on read endpoints, `[AllowAnonymous]` only on `HealthController`, `AuthController` status/sign-out, `InvitesController` redeem/lookup, and the SPA fallback; mutating/executing endpoints
(connection test, query-preview, upsert/delete, scripts, libraries, files, admin/*) are Admin; `RunHub` requires Viewer (`Hubs/RunHub.cs:13`). Session cookie is `HttpOnly`, `SameSite=Strict`, `Secure` when HTTPS, 32 random bytes, revocable, re-read each request
(`SessionAuthenticationHandler`, `AuthController.IssueSession`). No CORS configured anywhere (cross-origin denied by default). No antiforgery tokens, acceptable only while cookies stay Strict and CORS stays off. Invites are single-use, SHA-256 hashed. The runner state server binds loopback only and requires a fixed-time-compared token.
Caveats:
- `Auth:Network:Admin=Loopback` / `Viewer=Loopback|Remote` (`Auth/AuthOptions.cs`, `SessionAuthenticationHandler.NetworkFallback`) trust the socket peer. Behind a same-host reverse proxy every request looks like loopback, so `Admin=Loopback` makes everyone Admin; `Viewer=Remote` exposes config and logs to anyone who reaches the port.
  No forwarded-header handling. Defaults are `Disabled`; the risk is misconfiguration. Remediation: refuse or warn when enabled with a non-loopback bind or forwarded headers; document as dev-only.
- Default bind is plain HTTP (`ApiOptions.DefaultUrl`, container `http://0.0.0.0:8080`), so cookies are not `Secure`; `AllowedHosts` is `*` (`appsettings.json:8`). Remediation: warn on non-loopback HTTP; set `AllowedHosts` from `App:Url`.
- Viewers can read all connection config (hosts, users, `Properties`), script source, run logs and credential ref names. Intentional probably; state it.
- No rate limiting on invite or passkey endpoints (256-bit codes make brute force impractical).

### 9.7 Session ids stored in cleartext - Low
`State/SessionStore.cs:25,33,55` stores and looks up the raw id; invites store only a hash (`InviteStore.cs:155`). A state-DB read yields live sessions. Remediation: store `SHA256(id)`; add idle expiry and per-user cap.

### 9.8 Connection-string handling - Low
Values go through `DbConnectionStringBuilder` indexers (`GenericDriver.cs:34-92`), not concatenation, so ADO.NET providers get correct quoting; the password is set via the builder. Gaps: for ODBC the ADO.NET double-quote escaping differs from ODBC `{...}` escaping, so a password containing `;`/`}` can truncate
or inject attributes (use `OdbcConnectionStringBuilder` or a descriptor flag); `Properties` applied after the credential can override `Password`/`Host`, undermining "secret store is the only password source".

### 9.9 Bootstrap invite in log and file - Low
`Auth/BootstrapInvite.cs:47-70` logs the invite URL at Warning and writes `<RepoRoot>/FIRST-RUN.txt`. Generated only while no users exist, not when loopback-admin trust is on, removed after the first user. Anyone able to read logs/file on a fresh network-reachable install can claim Admin. Remediation: owner-only file ACL; console-only in containers.

### 9.10 CI/CD and container - Low to Medium
- `ci.yml` has no `permissions:` block (inherits repo default); add `contents: read`. `release.yml` grants `contents: write`, `actions: read`, `id-token: write` at workflow level (`:29-39`); scope per job. NuGet Trusted Publishing (no long-lived key) is a strong pattern.
- Workflow inputs go through `env:` rather than shell interpolation in the places seen (`release.yml:145`, `publish-image.yml:61,88,161`); `inputs.version` is used in `with:` fields only. Good.
- Actions pinned to mutable tags (`actions/checkout@v7`, `docker/*`, `NuGet/login@v1`, `actions/create-github-app-token@v3`), not SHAs. GitHub App key `PIPELINE_APP_PRIVATE_KEY` only in privileged workflows. No `dependabot.yml`, CodeQL or vulnerability scan (only `.github/workflows/`).
- `Dockerfile` has no `USER`: runs as root, and the default image is the full SDK. Base images are unpinned tags. `docker-compose.yml` publishes SQL Server on 14330/14331 with the default `sa` password; bind to 127.0.0.1.

### 9.11 ODBC named-parameter gap - Info
See section 3; likely breaks parameterised statements on `mssql.odbc`. Correctness, not security, but shipped as a catalog driver.

### 9.12 Self-update trust - Low
`SnapshotStager.cs` documents that the SHA-512 sidecar comes from the same release (integrity, not authenticity). Stable path uses NuGet. Updates default `Disabled`. Optionally verify NuGet package signature for snapshots.

### 9.13 Hardcoded credentials - Low
No production secrets found. `DbDataSync_Test_Pw1` (SA password) appears in ~35 test files (`tests/DbDataSync.Api.Tests/*IntegrationTests.cs`, `tests/DbDataSync.State.Tests/StateEngineFixture.cs:18`, `tools/DbDataSync.Benchmarks/Program.cs:41`) and as the default of
`${DBDATASYNC_MSSQL_SA_PASSWORD:-DbDataSync_Test_Pw1}` in `docker-compose.yml`; `sup3r-s3cr3t`, `hunter2` are unit-test fixtures. Acceptable for throwaway containers. GitHub App id in `release.yml` is not secret.

### 9.14 Dependencies - Info (scanner NOT run)
Reading csproj/package.json: nothing obviously pinned to a famous-critical-CVE version, but no scan was possible without a restore into the read-only repo. Add `dotnet list package --vulnerable` and `npm audit` to CI. Watch: `Npgsql 9.0.3`, `Microsoft.Data.SqlClient 7.0.2`,
`Oracle.ManagedDataAccess.Core 23.9.1`, `MySqlConnector 2.4.0`, `LibGit2Sharp 0.32.0` (tracks libgit2 advisories), `Mono.Cecil 0.11.6` (parses untrusted DLLs from installed libraries), `IKVM 8.16.1` plus operator jars, `System.DirectoryServices 9.0.0` on net10, `Parquet.Net 6.1.0`, `DuckDB.NET 1.5.5`.
SPA: react-markdown without `rehype-raw`, so notes Markdown does not render raw HTML. No Central Package Management (`Directory.Packages.props` absent; the sibling DbDataProxy has one): YamlDotNet and DuckDB.NET versions are repeated across csprojs. The team does track upstream advisories (CVE-2026-6471 note in `docker-compose.yml`).

### 9.15 SQL injection and dynamic SQL - Low
Identifiers go through `QuoteIdentifier`, which doubles the closing delimiter (`MsSqlDialect.cs:13`, `PostgresDialect.cs:20`, `MySqlDialect.cs:32`, `OracleDialect.cs:36`); values are bound as parameters; DDL literals use `Literal(...)` (`MsSqlDialect.cs:77`, `MsSqlProvisioner.cs:171`); Postgres slot names are validated
(`PgLogicalSlotStatement.ValidateSlotName`). State DB uses named parameters (`SessionStore`, `InviteStore`; `WorkQueueStore.cs:439` interpolates only constant kinds). No concatenation of user values into SQL found in a targeted grep of drivers, core, state, verification, API.
By design, Admin-authored raw SQL reaches source/target: `SourceTableSpec.Query/Filter`, `sqlColumnExpression`, SQL hooks, `TestQuery`, descriptor `metadataQueries` (`{{schema}}/{{table}}` become string literals, so check `'` escaping), and `query-preview`. Recommend read-only DB principals for sources and read-only intent on the preview path.

### Checked and acceptable
- YAML deserialization: typed `DeserializerBuilder` into concrete POCOs, no tag mapping; polymorphism via explicit-discriminator converters; `DbDataSyncConfigFile.Read` uses `Dictionary<object,object>`. No arbitrary-type instantiation.
- Command execution: `ArgumentList` and `UseShellExecute=false` in `ProcessSupervisor`, `LibraryValidationLauncher`, `LibraryInstaller.RunDotnetAsync`, `UpdateApplier`.
- SSRF: outbound HTTP only to fixed hosts (nuget.org, api.github.com, github.com release prefix) with a 5 s search timeout; user-controlled targets are DB connections (inherent) and the NuGet `Source` (9.2).
- Unsafe binary deserialization: none found. Verification result reads use a `GetFullPath` root check (`VerificationController.cs:149-150`), a good pattern to copy to 9.3.

## 10. Code quality observations relevant to consolidation

- **Tests**: 21 test projects, ~64k LOC (parity with product code): unit, SQL Server/Postgres/MySQL/Oracle integration (docker compose; `dbdatasync.runsettings` serializes assemblies), Playwright UI suite with golden-path screenshots. Gaps: no ODBC end-to-end, no automated vulnerability scan, no reflection test asserting every endpoint carries an auth attribute (fallback policy mitigates).
- **Docs**: excellent: `docs/*.md`, 200+ phase docs, `architecture/detailed-design.md`, branching/release methodology. Drift: `detailed-design.md` still says "v1 MSSQL only" and "no real auth yet"; stale comment at `DbDataSyncHost.cs:79-80`. Heavy phase-number references and very long XML doc comments hurt readability.
- **Internal duplication / parallel hierarchies**: (a) three dialect systems: `Core/Sql/*Dialect`, `Descriptor/DescriptorDialect`, `State/*StateDialect`; (b) hand-written engine drivers vs `GenericDriver` with overlapping connection/catalog logic; (c) two composition roots (`DbDataSyncHost.cs`, `TaskRunner/Program.cs`) that must register identical drivers; (d) type maps duplicated between C# dialects and descriptor YAML;
  (e) `Api/Services/AnsiScriptDialect.cs`; (f) `State/Remote` re-exposes `LocalRunnerState` over HTTP; (g) `ConfigRepository` is a large entity-agnostic class; (h) repeated load-connection/404 try-catch in `ConnectionsController`; (i) per-csproj package versions.
- **Layering**: Core holds engine dialects and `JdbcDialect`; `Api` references every driver project and `Drivers.Jdbc` (`ConnectionDiagnostics`); `Cli` references `Api` (the tool hosts the API). Large files: `DbDataSyncHost.cs` (479 lines of DI wiring with prose), `ConnectionsController.Test` (~100 lines).
- **Extensibility stance**: heavy investment in "any ADO.NET provider via YAML" plus JDBC-via-IKVM; this is the main conceptual overlap with any sibling that abstracts databases, drivers or connections.
- **Provenance**: commits are co-authored by Claude; quality is high, but claims such as "verified" in commit/phase docs need independent confirmation (the ODBC commit's own E2E caveat is an example).

## Appendix: prioritized recommendations
1. Validate/escape `PackageId`, `Version`, ids and file names at the API boundary and in `*Paths` helpers; build the library csproj via an XML API (9.2, 9.3).
2. Default SQL Server to verified TLS; expose an explicit trust toggle (9.5).
3. Extend credential-in-config detection and Viewer redaction to `Properties` (9.4).
4. Guard network-trust auth modes against proxy/non-loopback binds (9.6).
5. CI: least-privilege `permissions:`, SHA-pinned actions, Dependabot, CodeQL, `dotnet list package --vulnerable`, `npm audit`; non-root container (9.10, 9.14).
6. Before sharing the descriptor/ODBC mechanism: fix ODBC named-parameter binding and brace quoting, add an ODBC integration test (3, 9.8, 9.11).
7. For consolidation, extract first: SecretRefs convention + embedded-credential detector + redactor, `SqlDialect`/`CanonicalType`, `GitCommitService`, `Updates`, `Certificates`.
