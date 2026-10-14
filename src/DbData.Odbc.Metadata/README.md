# DbData.Odbc.Metadata

Catalog metadata for ODBC data sources, read through the driver manager's own catalog functions instead of
`INFORMATION_SCHEMA` or `System.Data.Odbc.GetSchema` (which has no primary or foreign keys). It is meant to be shared by
DbDataProxy, DbDataSync and DbDataBuild, and has no dependencies beyond .NET.

```csharp
await using var catalog = OdbcCatalog.Connect("Driver={ODBC Driver 18 for SQL Server};Server=...;Database=...");

var tables  = await catalog.TablesAsync(schema: "dbo");                 // tables and views
var columns = await catalog.ColumnsAsync(schema: "dbo", table: "orders");
var key     = await catalog.PrimaryKeyAsync(null, "dbo", "orders");     // null when there is none
var fks     = await catalog.ForeignKeysAsync(null, "dbo", "orders");
var indexes = await catalog.IndexesAsync(null, "dbo", "orders");
var info    = await catalog.GetInfoAsync();                             // product, version, identifier quote and case
```

## What it does for you

* **One connection, serialized calls.** `Connect` takes a connection string or a bare DSN name and connects on first use
  with `SQL_DRIVER_NOPROMPT`. The package makes its own connection because `System.Data.Odbc` keeps its handle private.
  Calls run one at a time on a thread-pool thread.
* **Catalog arguments are patterns; names are not.** `SQLTables` and `SQLColumns` treat `_` and `%` as wildcards. Names
  are escaped with the driver's `SQL_SEARCH_PATTERN_ESCAPE`, repeated unescaped if a driver finds nothing, and the rows
  are matched exactly (then case-insensitively) so `my_table` never returns `myXtable`.
* **Failures say what failed, not what you sent.** `OdbcCatalogException` carries the function name and SQLSTATE only
  (driver messages can quote names or paths). `NotSupported` is true for the SQLSTATEs drivers use for "I do not
  implement this" (IM001, HYC00, HY106, ...).
* **Bitness follows the process.** A 32-bit process loads 32-bit drivers; DbDataProxy hosts this package inside its
  `win-x86` worker for them.

## Layers

`OdbcCatalog` (typed records) sits over `IOdbcCatalogFunctions` (raw result sets as `string?[]` rows). Fake the interface
in tests; the real implementation is internal and P/Invokes `odbc32.dll` / `libodbc.so.2` / `libodbc.2.dylib`.

## Tests

`tests/DbData.Odbc.Metadata.Tests`: unit tests against a fake driver, and integration tests against SQL Server through
`ODBC Driver 18/17 for SQL Server` using `DBPROXY_TEST_SQLSERVER`, `_USER` and `_PASSWORD` (default `.\SQLEXPRESS` with
Windows authentication); they skip when no server is reachable unless `DBPROXY_REQUIRE_SQLSERVER=true`.
