# Implementation status

## Dialect (Plan 05)

Branch `worktree-agent-a12e81128462776ae`. Packages under `shared/src`: `DbData.Dialect.Abstractions`, `DbData.Dialect`, `DbData.Dialect.Arrow`, `DbData.Dialect.Conformance`; tests under `shared/tests`.

- Done: engine ids/caps, parameterised `CanonicalType`, `TypeMapping`/`Fidelity`, `ISqlDialect`; shared `SqlLexer` (+ fuzz/property tests), parameter rewriter, statement classifier, `ReadOnlyPolicy` with per-dialect deny lists and `ISqlParser` seam; strict `dialect.yaml` loader; built-in ansi/sqlserver/postgres/mysql/oracle/duckdb/fabric + generic ODBC from facts; type tables with `types.golden.yml`; Canonical<->Arrow table; offline conformance suite green for all dialects.
- Stubbed: live conformance (skipped unless `DBDATA_CONFORMANCE_<ENGINE>` is set; no driver in `shared/`), live type round trip, DDL fragments (`CreateTable` etc. not in `ISqlDialect` yet), array/list native types, product adoption (migration phases 1 and 6).
- Run tests from `shared/` (a `shared/global.json` opts into the Microsoft.Testing.Platform runner required by .NET 10 `dotnet test`).
