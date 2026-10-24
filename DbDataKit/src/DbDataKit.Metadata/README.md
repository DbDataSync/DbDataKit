# DbDataKit.Metadata

Plan 04 Part B: one catalog reader interface (`ICatalogReader` from `DbDataKit.Metadata.Abstractions`) over every way a catalog can be reached.
Depends on `DbDataKit.Metadata.Abstractions`, `DbDataKit.Dialect` and `DbDataKit.Odbc.Metadata` (and YamlDotNet for the lock file). Works against
`System.Data.Common.DbConnection`; it does not depend on `DbDataKit.Connections` or on any ADO.NET provider package.

## Readers
| Reader | Source | Notes |
|---|---|---|
| `InformationSchemaCatalogReader` | standard INFORMATION_SCHEMA | any engine; tables filtered in memory; no indexes or row estimates (reported as unsupported); `DescribeQueryAsync` = wrapped zero-row probe |
| `SqlServerCatalogReader` | `sys.*` views | identity, computed, defaults, collations, keys, FKs, indexes, row estimates (`dm_db_partition_stats`); describe = `sp_describe_first_result_set`; catalog on a `TableRef` becomes a three-part name |
| `PostgresCatalogReader` | `pg_catalog` | identity/serial/generated, collations, keys with ordinality, index direction, `reltuples`; describe = schema-only command (parse/describe, no execute) |
| `DuckDbCatalogReader` | `duckdb_*()` functions | full type text, constraints, indexes, `estimated_size`; describe = `DESCRIBE`; runs in-process in the tests |
| `OdbcCatalogReader` | `DbDataKit.Odbc.Metadata` over one `OdbcCatalogSession` | `CrawlAsync` = one connection, one SQLColumns call; describe = SQLPrepare + SQLDescribeCol |

`CachingCatalogReader` + `MemoryCatalogCache` add the optional `ICatalogCache` (fingerprint + epoch; `Invalidate` bumps the epoch).

## Facts, dialect and the lock file
`ServerFactsProbe` produces `ServerFacts` from ODBC SQLGetInfo (`FromOdbcAsync`) or from a dialect plus a version (`FromDialect`).
`DialectProfiles.ToDialect(facts)` picks a built-in dialect for known products (SQL Server, PostgreSQL, MySQL/MariaDB, Oracle, DuckDB, Fabric)
and otherwise builds the generic ODBC dialect (`OdbcDialect.FromFacts`) from the facts; row-limit support the driver cannot report is inferred
from the product name. `DialectLockFile.Render/Parse` serialise facts + `DialectProfile` (quote, parameter style, case, row-limit style,
limits, hash) as a deterministic `dialects.lock.yml`-style file. Parsing is strict and rejects a hand-edited profile by its hash.

`ListSchemasAsync` reads `information_schema.schemata` (every schema, including empty ones and system catalogs; override `SchemasSql`), the ODBC reader derives schemas from its tables. `GetColumnsAsync` is the cheap read of one table's columns.

## Rules the readers follow
- **Unknown is not empty.** `ShapeSupport` says which parts the source could answer; a driver without SQLPrimaryKeys gives `PrimaryKeys = false`.
  A DbException while reading keys, foreign keys or indexes becomes a warning on the shape plus the flag (permission errors stay visible).
  ODBC row-cap truncation (`OdbcResultTruncated`) becomes a warning.
- **Values are parameters, names are quoted.** Schema/table names bind as parameters; catalog names used as database qualifiers (SQL Server) go
  through `CatalogNames.Quote`, which refuses control characters and, for a driver with no quote character, unterminated quotes.
- **Describe is read-only.** Every `DescribeQueryAsync` runs `ReadOnlyPolicy.Check` first (exactly one SELECT, no deny-listed words) and sends
  nothing when it refuses (`InvalidOperationException`).
- Readers borrow the `DbConnection`: it is opened if closed and left open; the caller disposes it. `OdbcCatalogReader` owns its session only when it opened it.
- No execution API: the package does catalog queries and the policy-checked describe only.

## Decisions and limits
- The generic wrapped probe (`SELECT * FROM (<q>) t WHERE 1 = 0`) fails for queries that cannot be a derived table (T-SQL trailing ORDER BY, WITH on some engines). Engines with a real describe use it.
- ODBC reports no identity/computed flags, collations or row estimates; `ShapeSupport` and `CatalogCapabilities` say so.
- SQL_TINYINT maps to unsigned Int8 on SQL Server/Fabric and signed elsewhere (open policy decision from Plan 05).
- DuckDB foreign keys assume the referenced table is in the same schema (`duckdb_constraints()` does not report its schema) and report no referential actions.
- PostgreSQL generated columns need server 12+.
- MySQL (`MySqlCatalogReader`) and Oracle (`OracleCatalogReader`, 12c+; column defaults and collations are not read) have their own readers.
- Not done: `FlightSqlCatalogReader`, procedures through `ICatalogReader` (use `OdbcCatalogReader.Session.Catalog.ProceduresAsync`).

## Tests
`dotnet test --project tests/DbDataKit.Metadata.Tests` from `shared/`. DuckDB tests run in-process (DuckDB.NET.Data.Full); SQL Server and PostgreSQL are tested offline
through a recording fake `DbConnection` (SQL text and row mapping); ODBC through a fake `IOdbcCatalogFunctions`. Live tests skip unless
`DBDATA_METADATA_ODBC` (connection string) or `DBDATA_METADATA_SQLSERVER` / `DBDATA_METADATA_POSTGRES` (plus a registered provider) are set.
