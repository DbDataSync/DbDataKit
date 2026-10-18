<!--
DRAFT, NOT SUBMITTED. For review by the project owner before anything is posted to tobilg/polyglot.
Target repo: https://github.com/tobilg/polyglot   Suggested labels: bug, dialect: fabric
Searched existing issues on 2026-09-30 (division, NULLIF, safe divide, fabric division): no duplicate found.
Adjacent, different problems: #294 (PG->Fabric math functions), #482 (parse DuckDB `//`), #483 (DuckDB ORDER BY ALL).
-->

# DuckDB -> Fabric: `/` is not rewritten to float division (the `tsql` target gets `CAST(.. AS FLOAT) / NULLIF(.., 0)`, `fabric` does not)

Found with `polyglot-sql` **0.13.1** (main `0a7a1a7`), through the C FFI (`polyglot_transpile_with_options`), source `duckdb`. The output is identical with default options and with `unsupportedLevel: "raise"` (`strict()`). Scope: SELECT queries only.

### Input and output

| Input (DuckDB) | Actual, target `tsql` | Actual, target `fabric` |
|---|---|---|
| `SELECT 7 / 2 AS x` | `SELECT CAST(7 AS FLOAT) / NULLIF(2, 0) AS x` | `SELECT 7 / 2 AS x` |
| `SELECT a / b AS x FROM t` | `SELECT CAST(a AS FLOAT) / NULLIF(b, 0) AS x FROM t` | `SELECT a / b AS x FROM t` |

In DuckDB, `/` is always floating-point division: `7 / 2` is `3.5`. In T-SQL, `int / int` is integer division. I executed the `fabric` text on SQL Server 2022 (16.0.4265) with integer columns and got `3`, where DuckDB returns `3.5`. The query runs without error, so the difference is silent.

### Caveat

I did not have a Fabric warehouse to run this on, so the `3` above is from SQL Server. Fabric Data Warehouse is documented as T-SQL, and I expect `int / int` to behave the same way, but I have not confirmed it. If Fabric's `/` on integers already returns a fractional result, the current output would be correct and this issue can be closed; please say so.

### Root cause

Two places treat `TSQL` and omit `Fabric` (at `0a7a1a7`):

1. `crates/polyglot-sql/src/dialects/normalization/mod.rs`, lines 3045-3076: the "Safe-division source -> non-safe target" arm lists the targets that get `Action::Operators(MySQLSafeDivide)`. `DialectType::TSQL` is on the list (line 3064); `DialectType::Fabric` is not, so no action runs for `fabric`.
2. `crates/polyglot-sql/src/dialects/normalization/operators.rs`, lines 413-425: inside `Action::MySQLSafeDivide`, the `CAST(left AS FLOAT)` is added only for `DialectType::TSQL`. Every other target not listed in the match falls through to `_ => left`, which would give `NULLIF` but still integer division.

Many other places in the crate already pair the two (`DialectType::TSQL | DialectType::Fabric`, for example `dialects/mod.rs` lines 298, 3569, 4096), so the omission looks unintentional.

### Suggested fix

Add `DialectType::Fabric` to both: the target list in `normalization/mod.rs` and the `DialectType::TSQL =>` arm in `operators.rs` (`DialectType::TSQL | DialectType::Fabric =>`). Add a test next to the existing safe-division tests asserting `SELECT a / b FROM t` from DuckDB (and MySQL) to `fabric` equals the `tsql` output.

Happy to send a PR if that direction is right.

### Related observation (separate from this issue)

The safe-division comment lists DuckDB as a source whose division by zero returns NULL. In DuckDB 1.5.4, `SELECT 5 / 0` returns `inf` by default, and `NULL` only after `SET ieee_floating_point_ops = false`. So the `NULLIF` wrapping matches DuckDB only under that setting. This may well be a deliberate choice (NULL is the safer cross-database result); I mention it only so the `NULLIF` rewrite is documented as a decision, not an equivalence.
