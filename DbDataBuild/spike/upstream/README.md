# Upstream write-ups (polyglot-sql)

**Status: drafts only. Nothing here has been submitted.** The project owner reviews and decides.

| Draft | State |
|---|---|
| `01-fabric-safe-division.md` | Ready for review. Confirmed on SQL Server; **not confirmed on a Fabric engine** (stated in the draft). |

## Withdrawn
**`QUALIFY` for the `fabric` dialect.** An earlier note said polyglot should rewrite it for Fabric. That was wrong: Microsoft documents `QUALIFY` (and `GROUP BY ALL`) as supported in Fabric Data Warehouse (T-SQL surface area page, checked 2026-09-30), so leaving it in place is correct. The failures came from running Fabric-transpiled text on SQL Server 2022, which does not have them.

## Other differences seen in the spike (not drafted)
None of these is specific to Fabric. They are listed so you can decide whether any is worth reporting. Checked against open and closed issues on 2026-09-30 where noted; all measured on `0a7a1a7`.

| Observation | Where | Existing issue? |
|---|---|---|
| `CAST(x AS VARCHAR(20))` becomes `VARCHAR(MAX)` for `tsql` and `fabric`; the length is dropped | `spike/RESULTS.md` | Not found (#255 is about `varchar(max)` types, a different case) |
| `TRY_CAST(s AS INTEGER)` becomes plain `CAST` for PostgreSQL, changing error behavior | | Not found |
| `GROUP BY 1` and `GROUP BY ALL` are emitted unchanged for `tsql`, even with `unsupportedLevel: raise`. `GROUP BY ALL` is fine on Fabric (documented) but a syntax error on SQL Server 2022. | | Not found |
| List literal `[1, 2]` and struct literal `{'a': 1}` are emitted unchanged for `tsql`, even with `raise` | | Not found |
| `USING SAMPLE` is emitted unchanged for `tsql`, even with `raise` | | Not found |
| String literals with non-ASCII characters get no `N` prefix for `tsql` | | Not found |
| `AVG(int_col)` is emitted unchanged for `tsql`; T-SQL returns an integer, DuckDB a double | | Not found |
| With default options, `JOIN ... USING` and `NATURAL JOIN` pass through to `tsql`; with `raise` they are rejected | | #328 (closed) covers `strict()` |
| DuckDB `//` is not parsed; `ORDER BY ALL` | | #482, #483 (open) |
| The PostgreSQL parser rejects `CREATE TEMP TABLE t ON COMMIT DROP AS SELECT ...` (valid on PostgreSQL 17; `polyglot_validate` reports "Expected LParen, got On") | `src/DbDataBuild.Targets/Loaders/PostgresLoader.cs` works around it | Not searched |
