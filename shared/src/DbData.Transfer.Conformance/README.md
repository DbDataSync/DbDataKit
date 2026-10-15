# DbData.Transfer.Conformance

Runs the shared strategy SQL against an engine and compares the target table with an expected set of rows (Build's differential approach). `StrategyCases.All` holds the scenarios; `ConformanceRunner.RunAsync(engine, case)` creates tables, seeds them through the gate, applies the strategy, compares as a multiset, applies **again** (idempotent re-run) and compares again, then drops its tables. Guard breaches must fail with `DeleteGuardExceededException` and leave the target unchanged.

`DuckDbEngine.Create()` is the in-process reference (DuckDB.NET, in-memory, no setup). Other engines supply a `ConformanceEngine(name, dialect, version, openConnection)`; `Transfer.SqlServer.Tests` and `Transfer.Postgres.Tests` do this when `DBDATA_TEST_SQLSERVER` / `DBDATA_TEST_POSTGRES` hold connection strings to a scratch database.

## What is verified where

| Engine | Status | How |
|---|---|---|
| DuckDB | verified here (all 6 strategies, 11 cases, with re-run) | in-process; `merge_by_key` runs as `ON CONFLICT` with a primary key |
| SQL Server | cases and live runner exist; **not run** (no server in the build environment) | set `DBDATA_TEST_SQLSERVER`; Build's own conformance verified the same SQL shapes for four strategies |
| PostgreSQL | cases and live runner exist; **not run** | set `DBDATA_TEST_POSTGRES` |
| MySQL, Oracle | builder output only (text tests), never executed | no runner |
| Fabric | unverified, as in Build | no runner |

`ConformanceRunner.CasesFor` filters by the capability matrix. The suite has self-tests proving it fails on a wrong expectation and on a non-idempotent apply.
