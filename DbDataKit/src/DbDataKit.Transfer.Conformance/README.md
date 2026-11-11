# DbDataKit.Transfer.Conformance

Runs the shared strategy SQL against an engine and compares the target table with an expected set of rows (Build's differential approach). `StrategyCases.All` holds the scenarios; `ConformanceRunner.RunAsync(engine, case)` creates tables, seeds them through the gate, applies the strategy, compares as a multiset, applies **again** (idempotent re-run) and compares again, then drops its tables. Guard breaches must fail with `DeleteGuardExceededException` and leave the target unchanged.

`DuckDbEngine.Create()` is the in-process reference (DuckDB.NET, in-memory, no setup). Other engines supply a `ConformanceEngine(name, dialect, version, openConnection)`; `Transfer.SqlServer.Tests` and `Transfer.Postgres.Tests` do this when `DBDATAKIT_TEST_SQLSERVER` / `DBDATAKIT_TEST_POSTGRES` hold connection strings to a scratch database.

## What is verified where

| Engine | Status | How |
|---|---|---|
| DuckDB | verified here (8 of 9 strategies, 27 cases, with re-run; `merge_staged_changes` is unsupported on DuckDB) | in-process; `merge_by_key` runs as `ON CONFLICT` with a primary key |
| SQL Server | cases and live runner exist; `apply_staged_changes` (12 cases, with re-run, and with a caller's own quoting) ran on SQL Server 2025 (CW01a) | set `DBDATAKIT_TEST_SQLSERVER`; Build's own conformance verified the same SQL shapes for four strategies |
| PostgreSQL | cases and live runner exist; the A16a cases ran on PostgreSQL 16 (dialect syntax and a caller-supplied syntax); the `apply_staged_changes` cases ran on PostgreSQL 17 (CW01a) | set `DBDATAKIT_TEST_POSTGRES` |
| MySQL, MariaDB, Oracle | no runner here. `apply_staged_changes`' statements for them are covered by text tests (`ScopedStatementTests`, Sync's `ChangeApplyStatementTests`) and run live through Sync's own `ChangeApply` scenarios (MySQL 9, MariaDB 11, Oracle Free 23, CW01a); the other strategies are builder output only | no runner |
| Fabric | unverified, as in Build | no runner |

`ConformanceRunner.CasesFor` filters by the capability matrix. The suite has self-tests proving it fails on a wrong expectation and on a non-idempotent apply.
