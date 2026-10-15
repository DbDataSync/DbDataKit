# DbData.Odbc.Metadata

Catalog metadata for ODBC data sources, read through the driver manager's own catalog functions instead of
`INFORMATION_SCHEMA` or `System.Data.Odbc.GetSchema` (which has no primary or foreign keys). Shared by DbDataProxy,
DbDataSync and DbDataBuild. No dependencies beyond .NET (net10.0).

Origin: copied from `DbDataProxy/src/DbData.Odbc.Metadata` at commit `c08355f` (a history-preserving subtree split was not
possible from a worktree of the hub), then hardened per Plan 04 section 2.2/2.3.

```csharp
await using var session = await OdbcCatalogSession.OpenAsync("Driver={ODBC Driver 18 for SQL Server};Server=...;Database=...");
var crawl = await session.CrawlAsync(schema: "dbo");        // tables, columns, keys, indexes on ONE connection
var types = await session.Catalog.TypeInfoAsync();          // SQLGetTypeInfo, raw SQL type codes (OdbcSqlType)
var procs = await session.Catalog.ProceduresAsync(schema: "dbo");
await foreach (var c in session.Catalog.StreamColumnsAsync()) { }   // huge catalogs, not capped
```

## Hardening

* **Limits** (`OdbcCatalogOptions`): login timeout, `StatementTimeout` (`SQL_ATTR_QUERY_TIMEOUT`, default 60 s), `MaxRows`
  (default 500,000) with `RowLimitBehavior` Throw (`OdbcResultTruncatedException`) or Truncate (`result.GetTruncation()`
  returns an `OdbcResultTruncated`). Cancellation calls `SQLCancel` and surfaces as `OperationCanceledException`.
* **Connection strings**: `OdbcConnectionString.Build/Quote` (brace quoting; control characters refused), strict parser
  `TryParseStrict`, and `OdbcConnectionPolicy` (`AllowedDrivers`, `AllowedDsns`, `ForbidFileDsn`, `ForbidSaveFile`,
  `ForbidKeys`) enforced in `OdbcCatalog.Connect` and again before `SQLDriverConnect`. The default policy forbids
  `FILEDSN`/`SAVEFILE`; use `Unrestricted` only for administrator-written strings. Hostile-string tests cover brace
  breakouts, `FILEDSN=`, `SAVEFILE=`, DSN/driver smuggling and duplicates.
* `SQLGetInfo` strings grow and retry (no silent 1 KB cut); a value too long for SQLSMALLINT is an error.
* `SQLWCHAR` width is probed after connecting; a 4-byte (UTF-32) unixODBC build fails with `OdbcConfigurationException`.
* P/Invoke uses `LibraryImport` behind an internal `IOdbcApi` seam.

## Machine facts

`OdbcDriverInventory.Read()` lists drivers and DSNs with bitness (Windows registry, both views; `odbcinst.ini`/`odbc.ini`
on Unix, bitness from the library path when it says). Registry and file access go through `IOdbcRegistryReader` /
`IOdbcFileReader`. `OdbcDiagnostics.Explain(...)` turns IM002/IM014 and other common SQLSTATEs into plain advice.

## Verification status

* Fake-driver tests (`IOdbcCatalogFunctions` fake and a scripted `IOdbcApi`) cover everything, including timeouts, cap,
  cancellation, buffer growth, session reuse and policy.
* Verified against real odbc32.dll on Windows, and against SQL Server (`ODBC Driver 17/18`) including SQLGetTypeInfo,
  SQLProcedures, SQLProcedureColumns, the cap and sessions. Not verified on Linux/macOS (unixODBC naming, UTF-32 detection) or
  with drivers that reject `SQL_ATTR_QUERY_TIMEOUT`; those paths are covered by fakes only.
* Integration tests skip when no server is reachable: `DBDATA_TEST_SQLSERVER` (default `.\SQLEXPRESS`), `_USER`, `_PASSWORD`,
  `DBDATA_REQUIRE_SQLSERVER=true` to fail instead of skip (the `DBPROXY_` names also work).

Public API is tracked with `Microsoft.CodeAnalysis.PublicApiAnalyzers` (`PublicAPI.Shipped.txt`).
