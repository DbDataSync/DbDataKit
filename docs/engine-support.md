# Engine support

Generated from the engine registry (`DbDataKit/src/DbDataKit.Connections.Descriptor/engines/*.engine.yaml`, plan 14 R5) by `EngineSupportTableTests`; do not edit it by hand. Change the descriptor and run the test with `UPDATE_GOLDEN=1`.

**yes** built and run against a real engine, **unverified** built and never run against one, **planned** not built (the note names the slice or plan), **no** not supported.

## By role

| Engine | Sync source | Sync target | Build target | Build origin | Proxy source | Semantic emitter | Master data store |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Arrow Flight SQL | yes | no | no | yes | no | no | no |
| DuckDB | yes | no | no | yes | yes | yes | no |
| Microsoft Fabric warehouse | no | no | unverified | unverified | no | no | no |
| Microsoft SQL Server | yes | yes | yes | yes | yes | yes | planned |
| MySQL and MariaDB | yes | yes | no | no | no | no | no |
| ODBC | no | no | no | yes | yes | no | no |
| Oracle Database | yes | yes | no | no | no | no | no |
| PostgreSQL | yes | yes | yes | yes | no | planned | planned |

## What each says

### Arrow Flight SQL

`flightsql`; dialect `ansi`; catalog reader `information_schema`

- Sync source: yes. A source only: a reload of a table or query from any server that speaks the protocol (usually a proxy)
- Build origin: yes. Flight SQL metadata calls, information_schema as the fallback

### DuckDB

`duckdb` (also `duck`); dialect `duckdb`; provider `duckdb`; catalog reader `duckdb`

- Sync source: yes. Query only: Parquet, CSV, an S3 glob or an attached database
- Build origin: yes. Project import reads a DuckDB database's catalog
- Proxy source: yes. Opened READ_ONLY in the sandbox (no external access, locked configuration)
- Semantic emitter: yes. The DuckDB twin of each query, on a read-only file (plan 19 J03)

### Microsoft Fabric warehouse

`fabric`; dialect `fabric`; port 1433

- Build target: unverified. Built, never run against a real engine
- Build origin: unverified. The same catalog reads as SQL Server; never run against a real engine

### Microsoft SQL Server

`sqlserver` (also `mssql`, `sql-server`); dialect `sqlserver`; provider `sqlserver`; catalog reader `sqlserver`; port 1433

- Sync source: yes. Change Tracking or CDC, or a watermark column; TriggerAudit as the portable option
- Sync target: yes. Native bulk staging (SqlBulkCopy)
- Build target: yes. SQL Server 2022 in Build's real-engine suite
- Build origin: yes. Project import reads keys, foreign keys and indexes from the live catalog
- Proxy source: yes. Address, connection string or secret reference; pass-through identity
- Semantic emitter: yes. T-SQL; every conformance fixture passes on SQL Server 2022 (plan 19 J02, J03)
- Master data store: planned. Plan 15

### MySQL and MariaDB

`mysql`; dialect `mysql`; provider `mysql`; catalog reader `mysql`; port 3306

- Sync source: yes. Watermark column; TriggerAudit. MariaDB is tested beside MySQL
- Sync target: yes. Batched insert staging

### ODBC

`odbc`; dialect `ansi`; provider `odbc`; catalog reader `odbc`

- Build origin: yes. The driver's catalog functions, or information_schema when the dialect says so
- Proxy source: yes. Through the 32-bit worker where the driver is 32-bit

### Oracle Database

`oracle`; dialect `oracle`; provider `oracle`; catalog reader `oracle`; port 1521

- Sync source: yes. Flashback Version Query, or a watermark column; TriggerAudit
- Sync target: yes. Batched insert staging

### PostgreSQL

`postgres` (also `postgresql`, `pgsql`, `pg`); dialect `postgres`; provider `postgres`; catalog reader `postgres`; port 5432

- Sync source: yes. Native logical decoding, or a watermark column; TriggerAudit
- Sync target: yes. Native binary COPY staging
- Build target: yes. PostgreSQL 17 in Build's real-engine suite
- Build origin: yes. Project import reads the live catalog
- Proxy source: no. Only through ODBC; the proxy has no PostgreSQL provider of its own
- Semantic emitter: planned. Plan 19 J05; until then the DuckDB-dialect twin runs on it and 9 fixtures need the emitter
- Master data store: planned. Plan 15
