# DbDataProxy review

Repo: `C:\Projects\DbDataKit\DbDataProxy` (git submodule, remote `github.com/DbDataSync/DbDataProxy`, HEAD `c08355f`
"Add shared DbData.Odbc.Metadata package; report ODBC primary keys through SQLPrimaryKeys"). Review was read-only.
~8.7k lines of C#, 91 tracked files. Line numbers below are per file; `~` means approximate.

## Summary table

| Area | Verdict |
|---|---|
| Overall design | Clean, small, well-commented Flight SQL proxy; good separation Core / Server / Worker |
| DbData.Odbc.Metadata | Extractable now with modest packaging work (zero package deps); a few hardening gaps (section 8) |
| Biggest security risks | Client SQL runs unrestricted on the query path (DuckDB = server file read/write); pass-through auth is unthrottled and proxies credential guessing to the DB; plaintext backend secrets in appsettings.json; self-signed key written unencrypted with default ACLs; service defaults to LocalSystem |
| Tests | ~190 tests, good auth/TLS/e2e coverage; no tests for resource limits or hostile SQL |

---

## 1. Purpose and architecture

**Purpose.** An Apache Arrow Flight SQL server that fronts relational databases (SQL Server, DuckDB, any ODBC driver).
Clients speak standard Flight SQL (ADBC, pyarrow, Java/Go/C++); the proxy runs the SQL against a configured backend and
streams Arrow record batches back, optionally Arrow-IPC-compressed (zstd/lz4) or gRPC-gzipped. Stated main goal:
shrinking result sets on the wire (`README.md` top).

**Projects** (`DbDataProxy.slnx`):

| Project | Type | Role |
|---|---|---|
| `src/DbData.Odbc.Metadata` | library, no package deps | ODBC catalog functions via P/Invoke (the extraction candidate) |
| `src/DbDataProxy.Core` | library | `IQueryExecutor`/`IDbProvider`, ADO.NET-to-Arrow conversion (`ArrowColumnWriter`), parameter binding, `ProxyFlightClient`, worker wire protocol |
| `src/DbDataProxy.Server` (assembly `DbDataProxy`) | `Microsoft.NET.Sdk.Web` exe | Kestrel host, Flight SQL server (`ProxyFlightServer*.cs`), auth, TLS, CLI, Windows service installer |
| `src/DbDataProxy.Worker` | web exe, published `win-x86` | 32-bit loopback Flight server that runs ODBC for 32-bit-only drivers |
| `tests/DbDataProxy.Tests`, `tests/DbData.Odbc.Metadata.Tests` | xunit.v3 | unit + e2e |
| `tests/interop/*.py/.ps1` | python | ADBC + pyarrow interop against the published exe |

**Runtime / stack.** .NET 10 (`Directory.Build.props` `net10.0`, `LangVersion latest`, nullable on,
`TreatWarningsAsErrors=false`); `global.json` pins only the test runner (`Microsoft.Testing.Platform`), no SDK version,
so builds float. ASP.NET Core/Kestrel HTTP/2 only (`ProxyHost.cs:101-109`), gRPC. Central package management
(`Directory.Packages.props`): Apache.Arrow(+Flight, Flight.Sql, Flight.AspNetCore, Compression) 23.0.0,
System.Data.Odbc 10.0.13, Microsoft.Data.SqlClient 7.1.1, DuckDB.NET.Data.Full 1.5.6, Grpc.Net.Client 2.84.0,
Microsoft.IdentityModel.* 8.23.0, Hosting.WindowsServices/Systemd 10.0.13, xunit.v3 4.0.2.

**Topology** (README diagram): client -> gRPC/TLS -> x64 proxy -> backend; for 32-bit drivers proxy -> loopback Flight ->
`DbDataProxy.Worker.exe` (x86) -> driver. Hosted as a Windows service (primary), systemd, or Docker (`Dockerfile`,
untested per README "Not supported yet"). Single-file self-contained publish; IIS explicitly not supported.

**Protocol.** Arrow Flight SQL over gRPC. Implemented by subclassing `Apache.Arrow.Flight.Sql.FlightSqlServer`
(`ProxyFlightServer.cs:17`): handshake (Basic -> bearer), `GetFlightInfo(CommandStatementQuery)` (executes immediately so
the schema is known, parks the open reader in `PendingQueryStore`), `DoGet` (streams), `DoPut` update, prepared
statements incl. parameter binding (`ProxyFlightServer.Prepared.cs`), catalogs/schemas/tables/table types/primary keys/
`GetSqlInfo` (`ProxyFlightServer.Metadata.cs`). Unimplemented (`ProxyFlightServer.Unsupported.cs`): imported/exported
keys, cross reference, XDBC type info, bulk ingest, transactions, cancel.

## 2. Conceptual model

* **Data source** (`DataSourceOptions`, `ProxyOptions.cs:113-139`): a *named* backend. Fields: `Provider`
  (`odbc|sqlserver|duckdb`), `ConnectionString`, `Bitness` (`x64|x86`), `PassThrough`, `AllowWrites`, `Metadata`
  (`auto|odbc|informationschema`), `BatchSize` (32768), `CommandTimeoutSeconds?`, per-source `Compression` override.
  Clients never send connection strings; they name a source with the `x-datasource` header
  (`ProxyFlightServer.DataSourceHeader`), resolved in `DataSourceCatalog.Resolve` (`Data/DataSourceCatalog.cs:29`).
* **Backend** = `IDbProvider` (`Core/Data/IDbProvider.cs:7`) + `IQueryExecutor` (`Core/Data/IQueryExecutor.cs`). There is
  no persistent "backend" object: `DataSourceCatalog.Resolve` builds a fresh `DbQueryExecutor` (or `WorkerQueryExecutor`)
  per call (`DataSourceCatalog.cs:54-65`).
* **Principal** (`Auth/ProxyPrincipal.cs`): name, method (`password|passthrough|windows|jwt`), allowed data-source set
  (`*` wildcard), default data source, optional `DbCredentials` (pass-through).
* **Session** = opaque random bearer token -> in-memory principal (`Auth/SessionStore.cs`); idle 30 min, max 12 h.
  Lost on restart. JWT principals are stateless (re-validated every call).
* **Pending query / prepared statement**: server-side handles (`Flight/PendingQueryStore.cs`,
  `PreparedStatementStore` in `ProxyFlightServer.Prepared.cs:20`) owned by principal name.
* **Catalogs**: not a stored concept. Flight SQL catalog/schema/table listings are derived live from the backend
  (INFORMATION_SCHEMA or the ODBC catalog API, `UseCatalogApi`, `ProxyFlightServer.Metadata.cs:~334`).
* **Definition/config format.** Single JSON config `appsettings.json` beside the exe (section `Proxy`), plus env
  (`DBPROXY_Proxy__...`) and CLI overrides (`ProxyHost.cs:29`). `appsettings.example.json` documents every option. Users,
  data sources, compression and auth reload live via `IOptionsMonitor`; listener/cert need a restart. Users:
  `UserOptions` (`ProxyOptions.cs:141`) {Name, AuthType Password|Windows, PasswordHash, DataSources[], DefaultDataSource}.
  No separate "definition" files, no schema/versioning field, no include mechanism.

## 3. Driver / provider / connection management

**Abstraction.** `IDbProvider { Name; CreateConnection(cs); ApplyCredentials(cs,user,pw); ParameterStyle }`.
Implementations: `OdbcProvider` (Core, `IDbProvider.cs:21`), `SqlServerProvider` and `DuckDbProvider` (Server,
`Data/Providers.cs`). Registered as DI singletons in `ProxyHost.ConfigureServices` (lines 64-66); the README invites
adding more by implementing the interface. `DbQueryExecutor` (`Core/Data/DbQueryExecutor.cs:20`) is generic over any
`DbConnection` via `IDbConnectionFactory`/`DelegateConnectionFactory`.

**Connection creation / pooling.** There is no proxy-level pool. Every query and every metadata call opens a new
`DbConnection` (`DbQueryExecutor.cs:24-45`) and disposes it with the result. Reuse relies on provider pools
(SqlClient pool, ODBC driver-manager pooling if enabled). The ODBC catalog path (`Core/Data/OdbcMetadata.cs`) opens a *new
raw ODBC connection per call* via `OdbcCatalog.Connect(connectionString)` (no pooling at all; `ListTables`, `ListColumns`,
`ListPrimaryKeys`, `GetDialect` each connect/disconnect). With `include_schema`, `GetTables` can therefore open one
extra connection per table (`ProxyFlightServer.Metadata.cs` `SerializedTableSchemaAsync` + `declared`). `IsOdbc` in
`DbQueryExecutor.cs:59-66` opens a throwaway `DbConnection` object just to type-test it (no network, but wasteful and
a smell: it should be a provider property).

**Per-engine dialect handling.** Thin and mostly static:
* `ParameterStyle` (`Positional` vs `NamedAtSign`) per provider; `SqlMarkers.Scan` (`Core/Data/QueryParameter.cs:~218`)
  rewrites `?` to `@pN` for SQL Server, skipping literals/comments/brackets.
* `GetSqlInfo` facts are hard-coded per provider with live probing (`ProxyFlightServer.Metadata.cs:~250-290`).
* Dialect for ODBC comes from `SQLGetInfo` (`DbData.Odbc.Metadata.OdbcCatalog.GetInfoAsync`), mapped into Core's
  duplicate `DialectInfo`/`IdentifierQuote` records (`IQueryExecutor.cs:53-64`).
* No SQL generation/dialect layer: the proxy never builds DML/DDL; the only SQL it authors is the INFORMATION_SCHEMA
  queries (`Metadata.cs:23-31`, `~417`) and `SELECT * FROM <quoted name> WHERE 1 = 0` (`Metadata.cs:491`).

**Identifier quoting.** Two implementations: `OdbcIdentifierQuote.Apply` (Metadata pkg, `OdbcRecords.cs:39`, ordinal
replace) and Core `IdentifierQuote.Apply` (`IQueryExecutor.cs:~57`, culture-sensitive `string.Replace` overload,
functionally same for ASCII). Server-side `StandardQuote` (`Metadata.cs:505`) double-quotes everything not ODBC; this is
wrong for SQL Server only if QUOTED_IDENTIFIER is off, acceptable. Bracket-style drivers are handled by `Prefix != Suffix`.
When the driver reports no quote char the name is used raw (`Metadata.cs:517`, `id => id`).

**Type mapping to Arrow** (`Core/Data/ArrowColumnWriter.cs:44-176`): chosen from `DbDataReader.GetFieldType`:
bool, int8/uint8 (`tinyint -> UInt8`), int16..int64 and unsigned variants, float/double, `decimal -> Decimal128`
(precision/scale from `GetSchemaTable`, fallback 38/9, `:116-126`), `DateTime -> Date32` if provider type name is `date`
else `Timestamp(us)` with *no tz* (`:127-138`), `DateTimeOffset -> Timestamp(us, "UTC")`, `DateOnly -> Date32`,
`TimeSpan/TimeOnly -> Time64(us)`, `byte[] -> Binary`, everything else (strings, GUIDs, unknowns) -> `Utf8` via
`Convert.ToString(InvariantCulture)`. Notes: Decimal128 `Append(decimal)` for values exceeding declared precision is not
guarded; `Time64` truncates ticks/10; no Arrow `LargeUtf8`, list or struct types; ODBC columns that System.Data.Odbc
cannot read throw a message instructing the caller to CAST (`:53-59`). Parameter direction (Arrow -> CLR/DbType) is in
`QueryParameter.ToClr` (`QueryParameter.cs:~20-40`, `ArrowParameters.ReadRow`).
Every column is declared nullable (`Make`, `:190`); catalog nullability is overlaid only for table-schema requests.

**Supported engines.** SQL Server (SqlClient, native), DuckDB (DuckDB.NET.Data.Full), any ODBC driver (64-bit in-process,
32-bit via worker). Pass-through (`ApplyCredentials`) supported for SQL Server and ODBC; DuckDB throws
`NotSupportedException`.

**ODBC handling.**
* Queries: `System.Data.Odbc.OdbcConnection` with `CommandBehavior.SequentialAccess`.
* Catalog metadata: `DbData.Odbc.Metadata` (native `SQLTables/Columns/PrimaryKeys/ForeignKeys/Statistics/GetInfo`),
  because `System.Data.Odbc.GetSchema` lacks keys. Note: the proxy only consumes tables, columns (precision/scale/nullable),
  primary keys and dialect; `ForeignKeysAsync`/`IndexesAsync` are implemented and tested but unused by the proxy (it
  returns Unimplemented for imported/exported keys, `Unsupported.cs:17-36`).
* 32-bit: `WorkerProcessHost` (`Server/Data/WorkerProcessHost.cs`) lazily spawns `worker\DbDataProxy.Worker.exe`
  (`--port N --parent PID`, secret in env `DBPROXY_WORKER_SECRET`, lines 47-91), plain-HTTP/2 loopback gRPC, restarts when
  `HasExited`, kills the tree on stop. Single worker process shared by all 32-bit sources; the connection string
  (including pass-through credentials) is re-sent in every `WorkerRequest` (`Core/Worker/WorkerProtocol.cs:27`) as JSON
  in the Flight ticket/action body. Worker actions: `execute-nonquery`, `list-tables`, `dialect`, `list-columns`,
  `list-primary-keys`; one DTO per action, hand-rolled JSON dispatch in `WorkerFlightServer.DoAction`.
* `Dockerfile` installs only `unixodbc`; drivers are the operator's job.

## 4. Secrets handling

| Secret | Where | Protection |
|---|---|---|
| Backend credentials | inside `DataSources:<n>:ConnectionString` in `appsettings.json` (example: `User Id=proxy_reader;Password=...`, `appsettings.example.json:26`) or env vars | **None**: plaintext at rest, no DPAPI/secret-store/`Password` indirection, no references/placeholders. Relies on file ACLs of `%ProgramFiles%\DbDataProxy` |
| Local user passwords | `Users[].PasswordHash` | PBKDF2-HMAC-SHA256, 210,000 iterations, 16-byte salt, 32-byte hash, stored `PBKDF2-SHA256$iter$salt$hash` (`Auth/PasswordHasher.cs:14-48`); `FixedTimeEquals`; unknown-user timing equalised via `BurnVerifyTime` (`CredentialValidators.cs:~76`). Good |
| `Tls:CertificatePassword` | config plaintext (`ProxyOptions.cs:49`) | none; default is null password, so the self-signed PFX `dbdataproxy-selfsigned.pfx` is written with an **empty password** (`CertificateProvider.cs:59`) |
| JWT HMAC `SigningKey` | config plaintext base64 (`JwtOptions.cs`) | none |
| Pass-through client credentials | in memory in `ProxyPrincipal.Credentials` for session lifetime (up to 12 h); sent to the x86 worker in clear over loopback HTTP/2 inside each request | never put in the bearer token (design note in `SessionStore.cs:~6`); session dictionary keyed by SHA-256 of token (`:Key`) |
| Worker secret | 24 random bytes hex, env var `DBPROXY_WORKER_SECRET` of the child, `x-worker-secret` header, compared `FixedTimeEquals` (`WorkerFlightServer` ~`Authorize`, `WorkerSecret`) | adequate for same-host defence; a same-user local process can read the child env |
| Service account password | `DbDataProxy install --account A --password P` -> `sc.exe create ... password= P` (`ServiceInstaller.cs:24-28`) | visible in the process command line during install |
| Client smoke command | `DbDataProxy query --password P` (`QueryCommand.cs`) or env `DBPROXY_PASSWORD` | visible in process list/history (env fallback exists) |

**Logging.** `LogSql` defaults **true** and logs up to 200 chars of every statement with the user name
(`ProxyFlightServer.cs:108,215-218`); SQL literals can be sensitive. Passwords are never logged; handshake failures log
scheme, header length and header *names* (`:48`). Exceptions from the worker are logged whole (`log.LogWarning(ex,...)`
in `WorkerFlightServer`), and the database exception *message* is returned to the client (see finding S-8). The Metadata
package deliberately strips driver messages (`OdbcCatalogException` keeps only function + SQLSTATE).

**Client credentials on the wire.** Basic (base64) in the Flight handshake; TLS is the only protection (section 9).

## 5. Query execution, streaming, staging

Flow for `CommandStatementQuery`: `AuthInterceptor` -> `GetStatementQueryFlightInfo` -> `catalog.Resolve` ->
`ExecuteForFlightInfoAsync` (`ProxyFlightServer.cs:103-126`): `pending.EnsureCapacity` (32 open per owner) ->
`Executor.ExecuteQueryAsync` which opens the connection, creates the command, binds parameters, calls
`ExecuteReaderAsync(SequentialAccess)` and builds an Arrow `Schema` from the live reader (`DbQueryResult` ctor,
`DbQueryExecutor.cs:109-125`) -> result parked with a random 16-byte handle (2-minute TTL, `PendingQueryStore.cs:22`) ->
client `DoGet` -> `PendingQueryStore.Take` (owner check, one-shot) -> `CompressionPlan.Resolve` -> `StartStreamAsync`
-> loop: read `BatchSize` (32768) rows into column builders, build `RecordBatch`, `WriteAsync`. **No staging**: the
reader streams straight to the gRPC response (memory bounded by one batch per query), so this is not a temp-file/
staged design. The query is executed once (in `GetFlightInfo`), not twice. Consequence: the connection and DB cursor are
held between GetFlightInfo and DoGet (up to 2 min), and the command timeout applies to that initial execution.
Updates (`PutStatementUpdate` / `PutPreparedStatementUpdate`) run `ExecuteNonQueryAsync` per parameter row (max 10,000
rows, `Prepared.cs:105`) after an `AllowWrites` check (`ProxyFlightServer.cs:179`).
Compression: `CompressionPlan` (None / Grpc gzip / Arrow IPC zstd|lz4). Arrow IPC compression is enabled by reflection on
non-public members of `Apache.Arrow.Flight` 23.0.0 (`FlightIpcCompression.cs`, `EnsureSupported` fails fast at startup;
README documents this as fragile on upgrades).
Metadata calls read INFORMATION_SCHEMA fully into memory then filter in C# (`Metadata.cs:69-130`), with LIKE patterns
turned into escaped regexes (`Pattern`, `:315-322`, safe via `Regex.Escape`; no timeout but input is `Escape`d so ReDoS
is not possible, only `.*` expansions).

## 6. Install / update / deploy

* **Build:** `build/publish.ps1` -> `dotnet publish` of Server (self-contained single-file `win-x64`) plus Worker
  (`win-x86`, self-contained single-file, into `worker\`). `build/package.ps1 -Version X` stages, drops `*.pdb`/`web.config`,
  zips `DbDataProxy-X-win-x64.zip`, writes `SHA256SUMS` (same dir).
* **Release:** `.github/workflows/release.yml` on tag `v*` (windows-latest): `dotnet test`, package, `gh release create`
  uploading the zip, `SHA256SUMS`, and `scripts/install.ps1`.
* **Install/upgrade:** `scripts/install.ps1` (documented one-liner
  `& ([scriptblock]::Create((irm .../releases/latest/download/install.ps1))) -InstallService`). Steps: require admin,
  64-bit OS, query GitHub API for latest/tagged release (`install.ps1:34-39`), download zip + `SHA256SUMS`, compare hash
  (`:48-54`), `Expand-Archive` to temp, stop service if running, copy over `%ProgramFiles%\DbDataProxy` preserving an
  existing `appsettings.json` (`:66-74`), optionally `exe install`, restart service. Defaults `-Repo DbDataSync/DbDataProxy`.
  No backup/rollback of the previous version, no config-migration step, no `web.config`/ACL setup, no data-dir creation.
* **Service:** `ServiceInstaller` shells out to `sc.exe` using `ArgumentList` (no shell injection), auto-start,
  restart-on-failure. No `--account` -> **LocalSystem**. Firewall rule is left to the operator.
* **Other hosting:** `Dockerfile` (aspnet:10.0 + unixodbc, runs as root, `0.0.0.0`, env `DBPROXY_Proxy__...`), systemd
  via `AddSystemd()`; neither is exercised in CI.
* **CI:** `.github/workflows/ci.yml`: Windows job (build, tests with `--minimum-expected-tests 100` / `25`, package smoke
  test) and Ubuntu job with a SQL Server 2022 container.

## 7. Config schemas and validation

Schema = POCO classes in `Server/Configuration/*.cs`, bound with `Get<ProxyOptions>()` and
`Configure<ProxyOptions>`. **There is no startup validation**: no `IValidateOptions`, no `ValidateOnStart`, no JSON Schema
file. Consequences found by reading:
* Unknown provider names fail at request time (`DataSourceCatalog.cs:~34`, `StatusCode.Internal`), not at start.
* Invalid `Bitness` strings silently behave as x64; `Metadata` invalid values fall to the default branch
  (`Metadata.cs:~335`).
* `Listen.Address` -> `IPAddress.Parse` throws an unhandled exception at startup (`ProxyHost.cs:100`).
* `Compression.Codec` validity is checked per request in `CompressionPlan.Validated` (throws `InvalidOperationException`
  -> mapped as Internal) except Arrow mode which is checked at startup via `EnsureSupported`.
* `Users[].DataSources`/`DefaultDataSource`/`JwtMapping.DataSources` are not cross-checked against `DataSources` keys.
* Duplicate user names: first match wins (`FirstOrDefault`, `CredentialValidators.cs:25`).
* `AuthType: Password` with missing `PasswordHash` is rejected at login, not at load.
* Secrets cannot be expressed as references (see 4). Config validation is a clear gap and a candidate for a shared
  approach across the three tools.
Good: JWT refuses all tokens when `Audience` is missing and logs an error (`JwtBearerValidator.cs:~26-30`);
`CertificatePath` required in File mode (`CertificateProvider.cs:~29`).

## 8. The DbData.Odbc.Metadata package

**Location:** `src/DbData.Odbc.Metadata/` (7 files, ~700 lines incl. README) + `tests/DbData.Odbc.Metadata.Tests/`
(27 tests: 18 fake-driver unit tests, 9 SQL Server integration tests).

**Contents / API surface**
* `OdbcCatalog` (sealed, `OdbcCatalog.cs:14`): `Connect(string)`, `GetInfoAsync`, `CatalogsAsync`, `SchemasAsync`,
  `TablesAsync(catalog, schema, table, types="TABLE,VIEW")`, `ColumnsAsync`, `PrimaryKeyAsync`, `PrimaryKeysAsync(IEnumerable<OdbcTable>)`
  (returns `(Keys, Supported)`), `ForeignKeysAsync`, `IndexesAsync`, `DisposeAsync`. Primary constructor takes
  `IOdbcCatalogFunctions` for faking.
* `IOdbcCatalogFunctions` (public, `IOdbcCatalogFunctions.cs:9`): raw `Tables/Columns/PrimaryKeys/ForeignKeys/Statistics` as
  `IReadOnlyList<string?[]>` plus `GetInfoString/Number`.
* `OdbcNativeFunctions` (internal, `OdbcNativeFunctions.cs:12`): P/Invoke of `SQLAllocHandle`, `SQLDriverConnectW`,
  `SQLTablesW`, `SQLColumnsW`, `SQLPrimaryKeysW`, `SQLForeignKeysW`, `SQLStatisticsW`, `SQLGetInfoW`, `SQLFetch`, `SQLGetData`,
  `SQLGetDiagRecW`; a `NativeLibrary.SetDllImportResolver` maps the pseudo name `dbdata-odbc-driver-manager` to
  `odbc32.dll` / `libodbc.so.2|1|` / `libodbc.2.dylib` (`:20-35`). SQLWCHAR assumed UTF-16 (documented; true for Windows and
  default unixODBC).
* Records (`OdbcRecords.cs`): `OdbcTable`, `OdbcColumn`, `OdbcPrimaryKey`, `OdbcForeignKey`, `OdbcIndex(Column)`,
  `OdbcDriverInfo`, `OdbcIdentifierQuote` (with `Apply`), enum `OdbcIdentifierCase`.
* `OdbcConnectionString` (static): `Parse` (brace-aware, lower-cased keys), `Normalize` (bare DSN -> `DSN=x`), `Get(keys)`.
* `OdbcCatalogException(function, sqlState)` with `NotSupported` SQLSTATE classification (IM001, HYC00, HY106, HYC01, S1C00, IM017, IM002).

**Dependencies / coupling.** `.csproj` has no PackageReferences and no ProjectReferences; only BCL + the OS driver manager.
It inherits `Directory.Build.props` (net10.0, nullable, implicit usings) from the repo root - **those are not in the project
file**, so a separate repo must carry its own props. Coupling *from* DbDataProxy to it is small and clean:
`Core/Data/OdbcMetadata.cs` (55 lines) is the only adapter; `ProxyFlightServer.cs:~200-208` references
`OdbcCatalogException` for error mapping. `DbDataProxy.Core` also still references `System.Data.Odbc` directly for the
query path (`OdbcProvider`, `IsOdbc`, `IsLoginFailure`), which is fine and separate from the package.

**Readiness for extraction: mostly ready (est. a day of work).**
Ready: no packages, no proxy types leak in, README written for multi-consumer use, interface seam + fake-driver tests,
good wildcard-escape semantics (`ExactAsync`, `OdbcCatalog.cs:192-200`), SQLSTATE-only exceptions, sealed records.
To do before moving:
1. Own `Directory.Build.props`/`Directory.Packages.props`, `PackageId`, version, SourceLink, `IsPackable`, multi-targeting
   (consumers may be netstandard2.0/net8/net10); Metadata `.csproj` currently has only `RootNamespace` and `Description`.
2. Replace `[DllImport]` + `StringBuilder` marshalling with `LibraryImport` (AOT/trim friendliness) - optional; add
   `AllowUnsafeBlocks` accordingly. Today `[Out] StringBuilder` in `SQLGetDiagRecW` is fine for JIT.
3. Hardening gaps (see S-12): no row cap and no statement timeout on catalog calls (`Fetch`, `OdbcNativeFunctions.cs:108-131`;
   only `SQL_ATTR_LOGIN_TIMEOUT=30` at `:84`), cancellation only checked between rows, `GetInfoString` truncates silently at
   1 KB (`:169-172`), no safe builder for connection strings (consumers use `OdbcConnectionStringBuilder` instead - see
   `OdbcProvider.ApplyCredentials`), no allow-list/validation hook for `DRIVER=`/`DSN=`/`FILEDSN=`/`SAVEFILE=` when the string is
   user-supplied (DbDataSync/DbDataBuild may accept user-authored connection strings, unlike the proxy where it is admin config).
   Recommend `OdbcConnectionString.Quote/Build` + an `OdbcConnectionOptions { AllowedDrivers, ForbidFileDsn }` option.
4. API additions the other two projects will probably want: type-name mapping (`SqlType`/`TypeName` -> a neutral
   `DbType`), a `SQLGetTypeInfo` call, `SQLProcedures`, 64-bit-vs-32-bit hint/diagnostic helper, connection reuse across calls
   (the proxy opens one connection per call; the library already holds one per `OdbcCatalog` so callers can keep the
   catalog open for a whole schema crawl - proxy does not).
5. Duplicate types to delete from Core after extraction: `IdentifierQuote`, `DialectInfo`, `ColumnEntry`, `PrimaryKeyEntry`,
   `TableEntry` mirror `OdbcIdentifierQuote`/`OdbcDriverInfo`/`OdbcColumn`/`OdbcPrimaryKey`/`OdbcTable`
   (`IQueryExecutor.cs:35-64`); `OdbcMetadata.cs` is a pure projection layer that would shrink. The worker protocol serialises
   these Core DTOs as JSON; switching the DTOs to the package records changes the wire contract (keep proxy and worker versions in lockstep - they ship in one zip).
6. Pinning: tests rely on `DBPROXY_TEST_*` env vars and a local `.\SQLEXPRESS`; reuse the CI SQL Server container job.

**Other shared-library candidates in this repo**

| Candidate | Files | Coupling / notes |
|---|---|---|
| ADO.NET -> Arrow conversion (type map, batching) | `Core/Data/ArrowColumnWriter.cs`, `ColumnMeta`, `DbQueryExecutor.cs` `DbQueryResult` | Needs only `Apache.Arrow` + `System.Data.Common`. `internal` today; highest-value overlap with DbDataSync (copy/extract) and likely DbDataBuild. Make public, extract type-mapping table |
| Provider abstraction (`IDbProvider`, `IDbConnectionFactory`, `ParameterStyle`) | `Core/Data/IDbProvider.cs`, `DbQueryExecutor.cs:1-18`, `QueryParameter.cs` | Pure ADO.NET; depends on `System.Data.Odbc` only in `OdbcProvider`. `SqlServerProvider`/`DuckDbProvider` live in Server and pull SqlClient/DuckDB - split into provider packages |
| Parameter binding + SQL marker scanner | `Core/Data/QueryParameter.cs` (`QueryParameter`, `ArrowParameters`, `ParameterBinder`, `SqlMarkers`) | Arrow + System.Data only. `SqlMarkers.Scan` is a mini SQL lexer (strings, `[ ]`, comments); no dollar-quoting/backticks/`#`-comments, so reuse in other dialects needs extension |
| Identifier quoting/`DialectInfo` | `IQueryExecutor.cs:53-64`, `OdbcRecords.cs:36-40`, `Metadata.cs:505-522` | Should converge on one `IdentifierQuoter` in the shared lib |
| Password hashing | `Server/Auth/PasswordHasher.cs` | zero deps; reusable as-is |
| Login throttle, session store, `ProxyPrincipal` | `Auth/LoginThrottle.cs`, `SessionStore.cs`, `ProxyPrincipal.cs` | small, `IOptionsMonitor<ProxyOptions>`-coupled; need an options interface to be shared |
| JWT validator | `Auth/JwtBearerValidator.cs` | coupled to `ProxyOptions.Auth.Jwt` and `ProxyPrincipal`; decouple via options |
| TLS self-signed provider | `Tls/CertificateProvider.cs` | coupled to `ProxyOptions`/`TlsOptions`; otherwise generic and valuable if any sibling hosts TLS |
| Windows service installer + CLI | `Hosting/ServiceInstaller.cs`, `Cli.cs` | generic; `sc.exe` wrapper |
| Install/package/publish scripts | `scripts/install.ps1`, `build/package.ps1`, `build/publish.ps1` | parameterised by repo name; the pattern (zip + SHA256SUMS + installer) is a candidate for a shared template across all three repos |
| 32-bit worker host + protocol | `Server/Data/WorkerProcessHost.cs`, `Core/Worker/*`, `Worker/*` | Useful to any sibling that needs 32-bit ODBC (e.g. Sync reading legacy sources); coupling to proxy DTOs |
| Flight client | `Core/Client/ProxyFlightClient.cs` | Reads Arrow-IPC-compressed streams unsupported by stock .NET client; relevant if siblings consume proxy data |
| Options POCOs & validation | `Server/Configuration/*` | proxy specific, do not share |

## 9. Security review

Rating: Critical / High / Medium / Low. Findings S-1.. in rough priority.

### S-1 (High) Client SQL is passed through unfiltered; "read-only" is not enforced; DuckDB exposes the host filesystem
* Evidence: `CommandStatementQuery.Query` goes straight to `command.CommandText` (`GetStatementQueryFlightInfo` ->
  `ExecuteForFlightInfoAsync`, `ProxyFlightServer.cs:~96-126`; `ParameterBinder.Bind` sets `CommandText = sql`,
  `QueryParameter.cs:~165`). `AllowWrites` is checked only in `RunUpdateAsync` (`ProxyFlightServer.cs:179`). README
  concedes this ("A client that runs INSERT/DDL through the query path ... is limited only by the database account's
  permissions"), and `GetSqlInfo` reports `FLIGHT_SQL_SERVER_READ_ONLY=true` for sources without `AllowWrites`, which is
  false assurance (`Metadata.cs:~252`).
* Impact: for SQL Server/ODBC, anything the DB account can do (INSERT/DROP, `xp_cmdshell`, `OPENROWSET`, linked servers).
  For **DuckDB** sources (`DuckDbProvider`, no `access_mode=read_only`, no `enable_external_access=false`): any authenticated
  user can `read_csv('C:\...')`, `COPY ... TO`, `ATTACH`, `INSTALL/LOAD` extensions, i.e. arbitrary file read/write as the service account (LocalSystem by default, S-9).
  ODBC text/Excel/Access drivers likewise expose the file system.
* Remediation: enforce per-source `ReadOnly` for real: open SqlClient with `ApplicationIntent=ReadOnly` and a read-only login,
  DuckDB with `access_mode=READ_ONLY` plus `SET enable_external_access=false; SET lock_configuration=true`, ODBC wrap in a read-only
  transaction where supported; optionally a statement allow-list (parse and permit only SELECT/WITH). Make `READ_ONLY` SqlInfo reflect enforcement.
  Document that production sources must use least-privilege DB accounts, and change the example (`erp` uses a "proxy_reader" - good).

### S-2 (High) Pass-through auth accepts any credentials and is unthrottled: the proxy becomes a password-guessing relay and lockout amplifier
* Evidence: `PassThroughValidator.ValidateAsync` returns `CredentialResult.Accepted` for any user/password when
  `AllowPassThrough` (default **true**, `ProxyOptions.cs:100`) and any source has `PassThrough` (`CredentialValidators.cs:45-60`).
  `ChainedCredentialValidator` only records failures for *definitive rejections* (`:62-80`); a pass-through "success" clears
  the throttle (`RecordSuccess`). Failures surface later as a DB login error (`IsLoginFailure`, `ProxyFlightServer.cs:~205`)
  with no throttling. Every attempt creates a session in `SessionStore` (memory) and every query a DB connection attempt.
* Impact: unlimited online password guessing against DB/AD accounts through the proxy (bypassing the 5/1min throttle),
  AD account lockout DoS for arbitrary DB users, and session-table growth (12 h lifetime). README admits pass-through "has not been exercised against real accounts".
* Remediation: validate pass-through credentials eagerly at handshake (open+close a connection), throttle by (client,user)
  on DB login failure (map `IsLoginFailure` to `RecordFailure`), cap sessions per client, default `AllowPassThrough=false`.

### S-3 (High) Backend and TLS secrets stored in plaintext config; self-signed private key unprotected
* Evidence: connection strings with passwords in `appsettings.json` (`appsettings.example.json:26`); `CertificatePassword`
  plaintext (`ProxyOptions.cs:49`); self-signed PFX is exported with the configured password, which is null by default
  (`CertificateProvider.cs:59`) into `%ProgramData%\DbDataProxy` (`ProxyHost.ResolveDataDirectory`) with inherited ACLs
  (typically readable by all local Users) - no ACL hardening is applied anywhere (grep for `FileSecurity`/`SetAccessControl`: none). Install dir
  `appsettings.json` ACL inherits Program Files (readable by Users).
* Impact: any local user can read DB credentials, JWT signing key and the TLS private key (impersonate the proxy, MITM clients that trusted the `.cer`).
* Remediation: support secret indirection (`env:`, DPAPI-protected values, Windows Credential Manager, Key Vault, `*_FILE`), restrict
  ACLs on install and data dirs at install time (`install.ps1`), generate the PFX with a random password stored DPAPI-protected, or keep the key non-exportable in a machine/user cert store.

### S-4 (Medium) Windows service defaults to LocalSystem; Docker runs as root
* Evidence: `ServiceInstaller.cs:24-28` only sets `obj=` when `--account` is given; `Dockerfile` has no `USER`.
  Combined with S-1 (DuckDB/ODBC file access) and the worker child inheriting the same identity.
* Remediation: install as `NT SERVICE\DbDataProxy` (virtual account) or a gMSA by default; `USER app` in the Dockerfile (aspnet image provides `app`).

### S-5 (Medium) TLS posture: plaintext mode and non-validating client paths; no client auth; self-signed trust model
* `TlsMode.None` is supported (`CertificateProvider.cs:~20`), logs a warning only; `Listen.Address` defaults to `0.0.0.0`
  (`ProxyOptions.cs:27`) and Basic credentials flow on every handshake. Default mode `SelfSigned` is sensible. Protocols
  restricted to TLS 1.2/1.3 (`ProxyHost.cs:108`) - good. 2048-bit RSA, SHA-256, SAN includes machine name, FQDN, loopback.
* `DbDataProxy query --insecure` disables all certificate validation (`QueryCommand.cs:29-30`); interop python tests set `TLS_SKIP_VERIFY` (tests only).
  `ProxyFlightClient` itself uses default validation (good; caller supplies `GrpcChannelOptions`).
* Basic credentials are also accepted on every non-handshake call (`RequestAuthenticator.AuthenticateAsync`, `AuthInterceptor.cs:~26`), so each call may incur a PBKDF2 (see S-6).
* Example config ships `TrustServerCertificate=True/yes` for backend connections (`appsettings.example.json:26,40`), which turns off backend cert validation and invites copy/paste insecurity; the tests use `Encrypt=False`.
* Remediation: refuse `TlsMode.None` unless bound to loopback; remove `TrustServerCertificate` from examples; optional mTLS; HSTS n/a.

### S-6 (Medium) Authentication and resource-exhaustion (DoS) weaknesses
* PBKDF2 with 210k iterations on **every Basic-authenticated call** is a CPU amplification: the throttle keys on (client IP, user) so rotating usernames or distributed clients avoid lockout, and throttle state itself grows unbounded between 15 s sweeps (`LoginThrottle.cs`, key at `:20`). Unknown users cost one PBKDF2 as well (`BurnVerifyTime`) by design.
* No limits: no max rows/bytes per result, no max concurrent queries (global or per user), no per-source connection cap; only `PendingQueryStore.MaxPerOwner=32` per owner name (`:26`) with no global cap, each holding an open DB connection + reader for 2 min. A fresh login per request yields new "owners"... (owner is the user name, so one user is capped, but pass-through lets an attacker pick arbitrary user names, S-2).
* `CommandTimeoutSeconds` is optional and null by default, so a runaway query on ODBC may never time out; `Kestrel.Limits.MinResponseDataRate = null` (`ProxyHost.cs:97`) allows slow-read clients to pin a connection and DB cursor indefinitely; `MaxReceiveMessageSize` 64 MB (`:80`) for any request incl. DoPut.
* Metadata endpoints materialise whole INFORMATION_SCHEMA result sets in memory (`Metadata.cs:23-31`); `include_schema` runs one query per table.
* Remediation: global and per-user concurrency semaphores, row/byte quotas, mandatory default `CommandTimeoutSeconds`, `MinResponseDataRate` with generous grace, cap JWT/session counts, reduce Basic-on-every-call by rejecting Basic outside Handshake or caching verified credentials.

### S-7 (Medium) Authorization model gaps
* `ProxyPrincipal.CanUse` is data-source granularity only; no per-table, per-operation or per-schema permissions. `*` wildcard grants all current and future sources (`ProxyPrincipal.cs:~30`).
* Ownership of pending queries/prepared statements is keyed by *principal name* (`PendingQueryStore.cs:~39`, `PreparedStatementStore`): names are case-insensitive and not namespaced by auth method, so a pass-through user `Alice` could collide with local `alice`. Local users take priority at login (`LocalUserValidator` authoritative) which mitigates impersonation, but a JWT `preferred_username` claim equal to a local user name shares handle ownership. Namespace owner as `method:name`.
* JWT: signature, audience, lifetime, `RequireSignedTokens` validated; issuer is **not validated** with a symmetric `SigningKey` unless `Issuer` is set (`JwtBearerValidator.cs:87-93`); `ClockSkew` configurable; mapping by claim value with `OrdinalIgnoreCase`; Entra group-overage claim not handled (users with >200 groups would get no mapping, fails closed). `HttpDocumentRetriever.RequireHttps = true` (good). `alg` is bound to the configured key type.
* Windows auth: `LogonUser` type 3 (network) per attempt (`WindowsAccountValidator.cs:17-31`) goes through the same throttle; domain taken from the client-supplied user string, harmless since the configured user list is the allow-list. Note DOMAIN\user: `entry.Name` must equal the string the client sends, case-insensitive.

### S-8 (Low/Medium) Information disclosure through error messages and logs
* `ToRpcException` returns `ex.Message` for `DbException`/`NotSupportedException`/`ArgumentException` (`ProxyFlightServer.cs:201-203`) and "Query failed: " + message for others; the worker does the same (`WorkerFlightServer`). SqlClient/ODBC messages include server names, database names, driver paths, and sometimes file paths (text driver). `GetFlightInfo` startup exceptions for connection failures can expose backend host names.
* `LogSql=true` default logs SQL text (200 chars) (`ProxyOptions.cs:11`). User names logged at INFO. Remediation: map to generic messages with an error id and log details server-side; default `LogSql=false`.
* Self-description: `GetSqlInfo` returns backend product and version to any authenticated principal (low).

### S-9 (Low) SQL injection / identifier quoting inside the proxy's own SQL
* Client text is never concatenated into proxy-authored SQL: metadata filters are applied in C# (`Metadata.cs` header comment, `Pattern` uses `Regex.Escape`); parameters use `DbParameter` (`ParameterBinder.Bind`); `@name` markers take client column names as parameter names but through `ParameterName`, not text. 
* The one dynamic statement is `SELECT * FROM {name} WHERE 1 = 0` (`Metadata.cs:491`) with names from the *database catalog* (not clients), quoted by `StandardQuote` or the driver quote char. Residual: if a driver reports no quote char (`id => id`, `:517`) an object name containing SQL metacharacters is spliced raw into a read-path statement; and `StandardQuote` escapes `"` only. Risk is low (needs a hostile object name in the backend). Fix: reject names with whitespace/`;`/comment tokens when unquoted, or use `SQLColumns` only.
* `SqlMarkers.Scan` does not know dialect-specific lexical forms (e.g. DuckDB `$$ ... $$`, MySQL backticks, `#` comments); a `?` inside those is miscounted -> parameter-count error, not injection.

### S-10 (Low) ODBC connection-string injection / attribute smuggling
* Client never supplies a connection string; `ConnectionString` is administrator config (`DataSourceCatalog.cs:43`).
* The only runtime mutation is pass-through: `OdbcProvider.ApplyCredentials` (`IDbProvider.cs:27-36`) removes `Trusted_Connection`/`Integrated Security`
  and sets `UID`/`PWD` via `OdbcConnectionStringBuilder`, which brace-quotes values with `;`/`{`/`}`; `SqlServerProvider` uses
  `SqlConnectionStringBuilder` (`Providers.cs:16-25`). This blocks `;Driver=...`, `;DSN=`, `;FILEDSN=`, `;SAVEFILE=` smuggling via username/password in the
  tested paths (tests: `AuthTests`/pass-through; no dedicated hostile-string test found). Residual risks: other auth-affecting keywords already in the admin string are not stripped (`Authentication=ActiveDirectoryIntegrated`, `Trusted_Connection` spelt differently in some drivers, `AuthenticationType`), so pass-through may silently fall back to integrated credentials on a driver that honours another keyword (**credential confusion -> runs as the service account**); no length cap on user/password.
* The Metadata package passes the string straight to `SQLDriverConnectW` with `SQL_DRIVER_NOPROMPT` (`OdbcNativeFunctions.cs:85`); it does not validate keys. Fine for admin config, but when extracted for DbDataSync/DbDataBuild (potentially user-authored strings) an allow-list is needed (see section 8). `FILEDSN`/`DSN` from user text can load attacker-chosen driver DLLs (`DRIVER=` path) = code execution in-process.
* Remediation: after `ApplyCredentials`, assert via builder that no integrated-security style key remains; add a hostile-credentials test (`x};Driver={evil};UID={`).

### S-11 (Low) Path traversal / file handling
* No client-controlled file paths in the proxy. File inputs are all administrator config (`Tls:CertificatePath`, `Worker:Path`, `DataDirectory`, DuckDB `Data Source=`). `Worker:Path` is launched as an executable (`WorkerProcessHost.cs:47-75`): a writable config therefore equals code execution, which is already implied by config write access. The self-signed generator writes `CertificatePath` verbatim (may point anywhere writable). Impact: none beyond config control. See S-1 for DuckDB/text-driver paths via client SQL.

### S-12 (Low) Deserialization and untrusted parsing
* JSON: `System.Text.Json` to fixed records only (`WorkerProtocol.Deserialize`, `JsonSerializerDefaults.Web`); no polymorphic/`TypeNameHandling`. Protobuf via Arrow Flight; hand-parsed BasicAuth protobuf (`ProxyFlightServer.cs:~66-90`, wrapped in try/catch).
* Worker is only reachable on loopback with the secret; it executes arbitrary SQL for any caller holding it. Defence relies on loopback binding and `FixedTimeEquals`; the loopback port is allocated by bind-then-release (`FreeLoopbackPort`, `WorkerProcessHost.cs:~120`), a small race where another local process could take the port and receive the secret + pass-through credentials in the header/body of the next request (HTTP/2 cleartext). Mitigation: Unix domain socket/named pipe, or TLS to the worker.
* `Metadata` native layer: bounded buffers (4096 for data, 1024 info) with truncation handling; the `SQLGetData` loop (`OdbcNativeFunctions.cs:133-148`) trusts `indicator` and clamps it; no unbounded growth per column but unbounded rows (see below). `AllowUnsafeBlocks=true` is set in `DbDataProxy.Server.csproj:3` though no `unsafe` appears to be required (LibraryImport source gen in `NativeWindowsLogon` does not need it) - drop it.
* Metadata package DoS: `SQLColumns` with no table on a huge catalog loads all rows into `List<string?[]>` (`Fetch`, `:108-131`).

### S-13 (Low) Command execution in scripts and CI
* `ServiceInstaller` uses `ProcessStartInfo.ArgumentList` for `sc.exe` (no injection); `--password` is on the command line (see 4).
* `install.ps1`: TLS 1.2 forced; checksum is fetched from the *same release* as the zip, so it protects integrity against corruption, not against a compromised release/account (no signature, no pinned hash, no Authenticode, no SLSA/attestation). The documented `irm | iex`-style one-liner executes remote code as admin. `Expand-Archive` of a verified zip into temp, then `Copy-Item` into Program Files (zip-slip is mitigated by the checksum, not by path validation). `-Repo` is overridable (supply-chain pivot only by the operator). The checksum line is matched with `-match [regex]::Escape(name)` (ok). No rollback of a failed upgrade (service is not restarted on failure: `finally` only deletes temp dir, so a partial copy leaves the service stopped).
* `release.yml`: interpolates `${{ github.ref_name }}` directly into PowerShell single-quoted strings (`release.yml:22,28-29`) - a crafted tag name containing `'` would break out (requires tag-push rights, so low). Actions pinned by tag, not SHA (`actions/checkout@v4`, `actions/setup-dotnet@v4`). Release job has `contents: write` at workflow level (acceptable) and `GH_TOKEN` is the default token. Release does not run the Linux SQL Server job (`ci.yml`), only the Windows tests, and tests on tag push run all tests including SQL-Server tests that skip when the server is absent.
* `ci.yml`: SQL Server SA password `Dbproxy-Ci-Passw0rd` is committed in the workflow (`MSSQL_SA_PASSWORD`, `DBPROXY_TEST_SQLSERVER_PASSWORD`). Throwaway container, low risk, but should use a generated value (`openssl rand`) and `::add-mask::`. Installs `msodbcsql18` from Microsoft's apt repository over HTTPS with `dpkg -i` of a downloaded `.deb` (standard Microsoft instructions). No `permissions:` block in `ci.yml` (defaults to the repo default; set `contents: read`). PRs from forks run the SQL container job with secrets-free config: fine.
* Python/ps1 tests: `run-interop.ps1` sets secrets only as process env vars for a temp data dir, sleeps 5 s instead of polling readiness, and uses `Stop-Process -Force`; `interop.py`/`sql_server_check.py` use `TLS_SKIP_VERIFY` (test only). No `eval`/`subprocess` with untrusted input.

### S-14 (Low) Dependency versions (from `Directory.Packages.props`)
Versions are current as of the review date (Arrow 23.0.0, SqlClient 7.1.1, System.Data.Odbc/Hosting 10.0.13, Grpc.Net.Client 2.84.0, IdentityModel 8.23.0). A comment documents a transitive pin of `System.Security.Cryptography.Pkcs` 10.0.13 (SqlClient pulled 9.0.18 with known vulnerabilities), and `CentralPackageTransitivePinningEnabled` is on - good hygiene. No `dotnet list package --vulnerable` step, no Dependabot/NuGet audit configuration in `.github`; `NuGetAudit` is not explicitly configured (default on). `DuckDB.NET.Data.Full` bundles native DuckDB (all extensions, including httpfs auto-install capability, enlarging S-1). Floating SDK (no version in `global.json`). Apache.Arrow.Flight IPC compression via reflection is a supply-chain fragility more than a vulnerability.

### S-15 (Info) Hardcoded credentials grep
Searched all `*.cs/json/yml/ps1/py`. No production credentials. Test-only literals: `alice-pw` (`ProxyFixture.cs:21`), `pw`, `interop-pw`, the CI SA password above. Placeholder `Password=...` in `appsettings.example.json`. No private keys or tokens committed. `.gitignore` is the stock VS template (8.5 KB).

### Positive security controls observed
PBKDF2 210k with versioned storage; timing-equalised unknown-user path; opaque random 256-bit session tokens keyed by SHA-256, idle + absolute expiry; brute-force lockout (local users); owner-bound one-shot statement handles with 2-minute TTL and per-owner cap; parameters always bound, never spliced; `Regex.Escape` for LIKE; `AddServerHeader=false`; TLS 1.2+ only; worker secret with constant-time compare and parent-death shutdown; JWT audience mandatory and signed-token required; DB login failures mapped to `Unauthenticated` without echoing the string; the Metadata package withholds driver messages.

## 10. Code quality, consolidation, tests, docs

**Quality.** Idiomatic modern C# (primary constructors, records, `IAsyncEnumerable`), small files, thoughtful comments explaining *why*
(especially around Flight client quirks). Weak spots: (a) `WorkerFlightServer.DoAction` is a hand-rolled if/else dispatcher with
repeated try/catch/JSON boilerplate (5 copies) - candidate for a typed action registry; (b) `ProxyFlightServer` is a 3-file partial class with 2xx-line files and mixed responsibilities
(metadata, prepared, query); (c) duplication of DTOs between Core and the Metadata package (section 8.5); (d) `IsOdbc` type test
by instantiating a connection (`DbQueryExecutor.cs:59-66`); (e) `ArrowColumnWriter.Create` is a long if-chain of near-identical closures (Func/Action allocation per column; a
generic `ColumnWriter<TBuilder,T>` table would shrink it ~60% and make the type map data-driven and shareable); (f) reflection into `Apache.Arrow.Flight` internals (`FlightIpcCompression`);
(g) `README` claims "not supported yet" features (pass-through, Windows logon, Docker untested), acknowledging gaps; (h) no `IValidateOptions` (section 7);
(i) `TreatWarningsAsErrors=false`, no analyzers/`.editorconfig` in repo.

**Overlap likely with siblings (for the consolidation review):** ADO.NET provider abstraction and per-engine connection-string/quote handling,
Arrow type mapping, ODBC metadata (already extracted), identifier quoting, install/publish/release scripting (zip + SHA256SUMS + `install.ps1`), config-secret handling (needs one shared approach: S-3), CLI/service-install plumbing, and the 32-bit ODBC worker.

**Tests.** ~190 `[Fact]/[Theory]` (Auth 31, Flight e2e/TLS/JWT/metadata/params/compression/worker ~77, SqlServer ~16, type mapping 5, pending store 5, Metadata pkg 27).
Strong: handshake/Basic/JWT/TLS end-to-end with real Kestrel (`ProxyFixture`), worker tests against the 32-bit Microsoft Text driver, interop with ADBC/pyarrow, CI guards
(`--minimum-expected-tests`, `DBPROXY_REQUIRE_SQLSERVER`). Gaps: nothing on resource limits/DoS, hostile SQL/read-only bypass, ODBC pass-through string smuggling,
Linux/Docker/systemd, option validation, large-result/memory profiling, Windows-account and pass-through against real accounts (README admits). Several tests need a local SQL
Server/`SQLEXPRESS` or the Windows-only 32-bit Text driver, so they skip silently on dev machines. No coverage tooling.

**Docs.** `README.md` (222 lines) is thorough and honest about limitations (identifier quoting, AllowWrites caveat, Arrow compression reflection risk), has quick start,
config table, auth modes, GetSqlInfo table, client samples. `appsettings.example.json` documents all options. Package README for Metadata is good. Missing: architecture/threat-model doc,
CHANGELOG, upgrade notes, an options reference generated from code, security guidance (least-privilege DB accounts, ACLs, TLS expectations).

## Top recommendations (ordered)

1. Enforce read-only for real per provider (S-1), especially DuckDB external access and `ApplicationIntent=ReadOnly`; fix the misleading `READ_ONLY` SqlInfo.
2. Make pass-through validate and throttle (S-2) and default it off.
3. Secret indirection + ACL hardening + non-null PFX protection (S-3); run the service under a virtual account (S-4).
4. Add resource limits: concurrency, rows/bytes, default command timeout, sanitized error messages, `LogSql=false` default (S-6, S-8).
5. Finish the Metadata package extraction: own build props/packaging, add timeout/row-cap/connection-string validation, delete the duplicate Core DTOs (section 8).
6. Add `IValidateOptions` and validate at start (section 7); pin Actions by SHA and fix the `ref_name` interpolation (S-13).
