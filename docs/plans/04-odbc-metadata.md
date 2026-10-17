# Plan 04: ODBC and Database/Query Metadata (`DbDataKit.Odbc.Metadata` and `DbDataKit.Metadata`)

Status: proposed. The ODBC package is already in progress in DbDataProxy (`src/DbDataKit.Odbc.Metadata`, commit `c08355f`).
This plan covers (a) finishing and extracting it into the shared repo and (b) a broader neutral metadata layer built on top.
Depends on: Plan 05 (canonical types, lexer), Plan 06 (connection policy, parameter markers).

## 1. Current state

`DbDataKit.Odbc.Metadata` (7 files, about 700 lines with README, 27 tests): `OdbcCatalog` (connect, `GetInfoAsync`, catalogs,
schemas, tables, columns, primary keys, foreign keys, indexes), `IOdbcCatalogFunctions` seam, internal P/Invoke
`OdbcNativeFunctions`, records (`OdbcTable`, `OdbcColumn`, `OdbcPrimaryKey`, `OdbcForeignKey`, `OdbcIndex`,
`OdbcDriverInfo`, `OdbcIdentifierQuote`), `OdbcConnectionString` (parse/normalize), `OdbcCatalogException` (SQLSTATE only).
No package dependencies. Proxy uses it through one 55-line adapter (`Core/Data/OdbcMetadata.cs`).

Other metadata code to bring into view:
- Sync: information_schema catalog in `Drivers.Generic`, per-engine `*Catalog.cs`, `ITableRowEstimator`, descriptor
  `metadataQueries`, `ConnectionConfig`-level cached column metadata in mappings.
- Build: `OdbcCatalogClient`, `SourceCatalogReader`, `DialectProfile.Types` (catalog type name to logical type),
  `DialectDiscovery`, `dialects.lock.yml`.
- Proxy: INFORMATION_SCHEMA SQL in `Metadata.cs`, `GetSqlInfo` facts, `SELECT * ... WHERE 1 = 0` schema probe.

## 2. Part A: extract and finish `DbDataKit.Odbc.Metadata`

### 2.1 Extraction steps
1. `git subtree split` (or `filter-repo`) the `src/DbDataKit.Odbc.Metadata` and `tests/DbDataKit.Odbc.Metadata.Tests` paths to preserve history,
   then add under `DbDataKit/src` and `DbDataKit/tests` of the hub. Proxy then switches to the NuGet package.
2. Add package metadata: `PackageId`, SemVer starting 0.x, SourceLink, README, `IsPackable`, symbols, deterministic build,
   own `Directory.Build.props` and `Directory.Packages.props` (the project currently inherits `net10.0`, nullable, implicit usings from the Proxy root).
3. Multi-target `net10.0` (and `net8.0` if any consumer needs it; all three are `net10.0` today, so `net10.0` only unless a reason appears).
4. Public API baseline with `Microsoft.CodeAnalysis.PublicApiAnalyzers`; treat `IOdbcCatalogFunctions` as public API.
5. Convert `[DllImport]`/`StringBuilder` marshalling to `LibraryImport` (trim/AOT friendly; needs `AllowUnsafeBlocks` in this project only).
6. Delete Proxy's duplicate Core DTOs afterwards (`IdentifierQuote`, `DialectInfo`, `ColumnEntry`, `PrimaryKeyEntry`, `TableEntry`
   at `IQueryExecutor.cs:35-64`). Worker wire contract changes with this: Proxy and Worker ship in one zip, keep them lockstep and bump the worker protocol version.

### 2.2 Hardening required before wider use
| Gap (Proxy review) | Fix |
|---|---|
| No statement timeout; only `SQL_ATTR_LOGIN_TIMEOUT=30` | `OdbcCatalogOptions.StatementTimeout` via `SQL_ATTR_QUERY_TIMEOUT`; cancellation checked in fetch loop and via `SQLCancel` |
| No row cap (`SQLColumns` with no table loads everything) | `MaxRows` with a typed `OdbcResultTruncated` signal, plus streaming `IAsyncEnumerable` variants |
| `GetInfoString` silently truncates at 1 KB | retry with the reported length |
| No safe connection-string builder or policy | `OdbcConnectionString.Build/Quote` (brace quoting) and `OdbcConnectionPolicy { AllowedDrivers, AllowedDsns, ForbidFileDsn, ForbidSaveFile, ForbidKeys }` enforced before `SQLDriverConnect` (`DRIVER=`, `FILEDSN=`, `SAVEFILE=` can load arbitrary libraries, S-10) |
| One connection per call in consumers | document and provide a session type: `OdbcCatalogSession` holds one connection for a whole crawl; Proxy adopts it for `include_schema` |
| `SQLWCHAR` assumed UTF-16 | detect UTF-32 unixODBC builds at startup and fail with a clear diagnostic or support both |

### 2.3 API additions
- `SQLGetTypeInfo` returning `OdbcTypeInfo` and `SqlType` to canonical type mapping (via Plan 05; this package stays dependency-free
  by exposing the raw SQL_xxx codes plus an optional `DbDataKit.Odbc.Metadata.Canonical` mapping package).
- `SQLProcedures`/`SQLProcedureColumns` (Sync hook discovery, Build native SQL).
- `OdbcDriverInventory`: list installed drivers and DSNs (Windows registry both bitness hives, `odbcinst` on Unix) with bitness, so
  consumers can detect "this driver is 32-bit only" and route to the Proxy worker (replaces trial and error).
- Diagnostics helper: explain `IM002`/`IM014` (architecture mismatch) in plain text.
- Tests: add the existing fake-driver unit tests to a shared matrix: SQL Server (msodbcsql18 container), SQLite/DuckDB ODBC on Linux,
  Microsoft Text driver 32-bit on Windows (already used by Proxy worker tests).

## 3. Part B: neutral metadata model (`DbDataKit.Metadata`)

Goal: one set of records and one reader interface that products consume regardless of how the catalog is reached.

```csharp
record TableRef(string? Catalog, string? Schema, string Name, TableKind Kind);
record ColumnInfo(string Name, int Ordinal, CanonicalType Type, string NativeType, bool Nullable,
                  string? DefaultExpr, bool IsIdentity, bool IsComputed, string? Collation);
record KeyInfo(...); record ForeignKeyInfo(...); record IndexInfo(...);
interface ICatalogReader {
  IAsyncEnumerable<string> ListCatalogsAsync(...);  IAsyncEnumerable<TableRef> ListTablesAsync(TableFilter f, ...);
  Task<TableShape> GetShapeAsync(TableRef t, ...);  // columns, pk, fks, indexes, row estimate
  Task<QueryShape> DescribeQueryAsync(string sql, ...); // result column names/types without executing (see 3.2)
}
```
Implementations (each in the package of the engine it belongs to, behind Plan 06 providers):
`InformationSchemaCatalogReader` (Sync Generic, Proxy), `SqlServerCatalogReader` (sys views for identity/computed),
`PostgresCatalogReader`, `OdbcCatalogReader` (this package), `DuckDbCatalogReader`, `FlightSqlCatalogReader` (Build's row source,
Flight SQL `GetTables` with `include_schema`).

### 3.1 Capabilities and dialect facts
`CatalogCapabilities` (supports FKs, indexes, row estimates, procedures, schemas) and `ServerFacts` (product, version, identifier quote,
case rules, max identifier length). Proxy's `GetSqlInfo` probing and Build's `DialectDiscovery` produce the same record; Plan 05 consumes it
to create or refine a `DialectProfile`. Build's `dialects.lock.yml` becomes a serialisation of `ServerFacts` plus a `DialectProfile`.

### 3.2 Query metadata
`DescribeQueryAsync` returns result column names/types for a query without fetching rows. Strategies by engine: SQL Server
`sp_describe_first_result_set`, Postgres prepared-statement description, ODBC `SQLPrepare` + `SQLDescribeCol`, generic wrapper
`SELECT * FROM (<q>) t WHERE 1 = 0` guarded by the Plan 05 read-only classifier. Sync uses it for query-shaped sources and mapping
previews; Build for `native` models and origin sources; Proxy for `GetFlightInfo` schemas. Name validation (Proxy S-9) lives here:
object names from the catalog are always quoted via the dialect, and names containing control characters or unterminated quote
sequences are rejected when the driver reports no quote character.

### 3.3 Caching
Optional `ICatalogCache` keyed by connection fingerprint and metadata epoch (Sync caches column metadata inside mapping YAML; keep that
product-side, but share the invalidation helpers).

## 4. Phases

| Phase | Work | Exit |
|---|---|---|
| A1 | Subtree extraction, packaging, public API baseline; Proxy consumes the package | Proxy tests green on the package |
| A2 | Hardening table 2.2 (timeout, row cap, policy, session, truncation) with tests | New tests for each gap |
| A3 | Driver inventory and bitness diagnostics; GetTypeInfo; procedures | Used by Proxy worker routing |
| B1 | Neutral records + `ICatalogReader` + capabilities; adapter for ODBC and INFORMATION_SCHEMA | Proxy `Metadata.cs` rewritten on top |
| B2 | Sync adopts for connection catalog browsing; replace the ODBC `metadataQueries` hack for `mssql.odbc` with the package | ODBC catalog browsing works end to end |
| B3 | Build adopts for origin catalogs (`OdbcCatalogClient`, `SourceCatalogReader`) | Duplicated catalog code deleted |
| B4 | `DescribeQueryAsync` per engine | Used by Proxy `GetFlightInfo` and Sync previews |

## 5. Risks
- P/Invoke surface differs per platform (unixODBC naming, SQLWCHAR width, 32-bit vs 64-bit); CI needs Windows and Linux legs.
- Keep Part A dependency-free so it can be consumed by lightweight tools; put canonical-type mapping in a separate package.
- Metadata semantics differ per driver (many ODBC drivers lack `SQLPrimaryKeys` or return empty); `Supported` flags must propagate to consumers.
- Scope: resist adding query execution to the metadata package.

## 6. Acceptance
Proxy depends on the NuGet package and has no duplicate ODBC DTOs; hostile connection strings are rejected by policy tests;
a catalog crawl uses one connection; at least two other products read catalogs through `ICatalogReader`.
