# DbData.Connections.Odbc

ODBC provider over System.Data.Odbc 10.0.13.

- Composition is `OdbcComposer` (in `DbData.Connections`): brace quoting, driver/DSN allow-lists, no FILEDSN/SAVEFILE, credentials last (`x};Driver={evil};UID={` stays one value).
- `OdbcProvider.CreateCommand(connection, sql, sourceDialect, values, timeout)`: `@name` statements become `?` with the binding order recorded (the missing Sync ODBC path; replaces Proxy `SqlMarkers`).
- Read-only: driver keywords where the driver is known (SQL Server `ApplicationIntent=ReadOnly`, PostgreSQL `ReadOnly=1`, DuckDB `access_mode=READ_ONLY`), plus `OdbcAccessMode.TryApplyReadOnly`, a best-effort `SQL_ATTR_ACCESS_MODE=SQL_MODE_READ_ONLY` after open. System.Data.Odbc has no API for connection attributes, so it reads the driver manager handle through reflection and calls `SQLSetConnectAttr`; if that shape changes or the driver refuses, it reports "not applied" (`OdbcReadOnlySession.LastResult`) instead of failing. Treat both as hints: the read login is the control. This path has not run against a real driver here (no driver in `shared/`).
- `OdbcFailureMapper`: first diagnostic record's SQLSTATE and native error; text never used.

Live tests: set `DBDATA_TEST_ODBC` to a full ODBC connection string.
