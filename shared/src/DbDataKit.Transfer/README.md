# DbDataKit.Transfer

Shared building blocks for moving data (Plan 07): value conversion, staging policy, strategy SQL builders with a capability matrix, and a write gate. Orchestration (scheduling, plans, resume) stays in the products.

Depends on `DbDataKit.Transfer.Abstractions`, `DbDataKit.Dialect`, `DbDataKit.Secrets.Abstractions` (for `IRedactor`). No product and no driver dependency.

## ValueConverter

`ValueConverter.Plan(source, target, dialect, options)` computes a `ConversionPlan` **once per column**; `plan.Convert(value)` is then a delegate call. Output follows `ClrContract`. The plan carries `Fidelity` (worse of `FidelityRules` and the dialect's `ColumnType` verdict) and refuses unsupported pairs at plan time. `PlanAll` and `ConvertBatch` do whole schemas and batches.

Error policy per column (`ErrorPolicy`): `Fail` (default), `Null`, `Truncate` (cut text/binary, drop fractional digits and sub-scale time, strip NUL, replace unpaired surrogates with U+FFFD), `Clamp` (Truncate plus saturate integer, decimal and time-of-day ranges; infinity to the largest finite value; NaN cannot be clamped). `Null` into a NOT NULL column fails. Errors name the column and a code (`OutOfRange`, `PrecisionLoss`, `Truncated`, `InvalidFormat`, `NotFinite`, `InvalidText`, `NullNotAllowed`, `UnsupportedType`), never the value.

Edge cases and the rule applied:

| Case | Rule |
|---|---|
| Decimal overflow | `abs(value) >= 10^(p-s)` is `OutOfRange` (Clamp saturates to the largest value of the type). Scale loss is `PrecisionLoss` (Truncate rounds toward zero). The CLR `decimal` holds 28-29 digits, so precision above 28 integer digits cannot be checked. |
| datetimeoffset to timestamp | Non-tz target keeps the UTC instant as an unspecified-kind `DateTime`. |
| Zone-less to tz timestamp | Read as UTC (Build's rule); `Local` kind is converted to UTC. |
| NaN / Infinity | Allowed only where the dialect allows (`postgres`, `duckdb`); otherwise `NotFinite`. Into `decimal`, always `NotFinite`. |
| NUL in text | Rejected for `postgres`; Truncate strips it. |
| Unpaired surrogates | Rejected except for `sqlserver`/`fabric` (UTF-16 stores them); Truncate/Clamp replace with U+FFFD. Valid pairs survive; a cut never splits a pair. |
| Time / timestamp precision | Ticks beyond `scale` digits are `PrecisionLoss` (Truncate cuts). Time of day outside 00:00..24:00 is `OutOfRange`. |
| Timestamp to date | A non-zero time of day is `PrecisionLoss`. |

Limits not modelled: text length counts UTF-16 units (engines count code points or bytes); non-Unicode `varchar` code pages; date ranges narrower than `DateTime` (MySQL, SQL Server `datetime`).

## Strategies

`StrategyCatalog.Build(name, request, dialect)` validates against the matrix, then builds. Every statement is parameterised, identifiers go through `ISqlDialect.Quote*`, and a test asserts that no value reaches statement text on any dialect.

| Strategy | Statements | Notes |
|---|---|---|
| `full_replace` | `DELETE` (or `TRUNCATE` on request) then `INSERT ... SELECT` stage | TRUNCATE on engines without transactional DDL marks the set non-transactional. |
| `delete_insert_by_key` | `DELETE ... WHERE EXISTS (stage key match)` then insert | NULL keys never match. Rows absent from the stage are kept. |
| `merge_by_key` | `MERGE` / `ON CONFLICT` / `ON DUPLICATE KEY` | Auto: MERGE where the dialect has it, PostgreSQL 15+, else ON CONFLICT (duckdb, postgres < 15), ON DUPLICATE KEY (MySQL). Needs unique stage keys, and a unique constraint for the upsert forms. |
| `delete_insert_by_range` | delete `[start, end)` then insert stage rows in the window | Bounds are parameters `range_start`, `range_end`. |
| `watermark_append` | optional delete of the window, insert rows `> watermark` (`>=` with lookback) | The host resolves the watermark with `ResolverStatement` and subtracts any lookback. |
| `key_reconcile_delete` | count scope, delete keys absent from the stage | `DeleteGuard` (ratio, default 0.5, empty scope passes) is evaluated by `WriteGate.RunAsync` after the delete; a breach rolls back. |

Sync kind names resolve through `StrategyMatrix.Resolve` (`MsSqlMerge` to `merge_by_key`, ...). `Snapshot`, `Scd2` and `KeyReconcileScd2Close` stay Sync-only.

## Capability matrix

`Matrix/strategy-matrix.json` (embedded, replaceable via `StrategyMatrix.Parse`) says per strategy and engine: `reference` (verified by this repo's conformance), `product-verified` (Build/Sync verified it, not this repo), `unverified`, or `unsupported`, plus `min_version`. `Check`/`Require` reject unknown engines, unsupported rows, and too-old versions; `requireVerified` rejects `unverified`. Fabric stays unverified everywhere.

## Staging

`StagingNamer.Name` gives `stg_<schema>__<table>`, hash-shortened (`_` + 8 hex of SHA-256 of `schema.table`) to the identifier limit in UTF-8 bytes; `StagingPolicy.BuildCompatible` reproduces Build's names exactly (60 bytes, tracking schema). `RunScoped` appends `_r<8 hex>`. Temp variants: `#name` (SQL Server), `CREATE TEMP TABLE` (Postgres, DuckDB), `CREATE TEMPORARY TABLE` (MySQL), `CREATE GLOBAL TEMPORARY TABLE ... ON COMMIT PRESERVE ROWS` (Oracle). Lifecycle: `DropOnSuccess` (default; retained after failure so a deterministic name can resume), `DropAlways`, `Retain`. `GenericStagingProvider` creates, loads (any `IBulkWriter`, default `BatchInsertBulkWriter` with per-engine parameter limits) and drops through the gate. Permissions: persistent staging needs CREATE/DROP TABLE on the staging schema; temp tables need none beyond TEMP/connect.

## WriteGate

`WriteGate` is the single choke point (Build's `MutationGate`, generalised): every statement carries a `WriteIntent`; `AccessMode.ReadOnly` refuses all writes; `Permitted` limits effect classes; the log entry is written **before** execution and an unwritable log aborts the statement; dry-run runs every check and log line and executes nothing; `RunAsync` wraps a `StatementSet` in a transaction and evaluates the delete guard; `AcquireLockAsync` is a session lock (`sp_getapplock`, `pg_try_advisory_lock`; other engines throw `NotSupportedException` from the ADO executor).

Statement log (`StatementLogDetail`): default `HashAndTypes` = statement hash, parameter names, canonical types and per-run salted value hashes (no text, no values); `WithText` adds text; `WithValues` adds values. Text, values and outcomes pass through the `IRedactor` (`DbDataKit.Secrets.Redactor`). Failures log the exception type and driver error code only. `FileStatementLog` writes flushed JSON lines, owner-only on Unix; on Windows it inherits the directory ACL (put it in a restricted directory; an explicit ACL is not set yet).

`SourceInvariants` is the reusable form of Build's `GateInvariantTests`: `FindOffenders(sourceRoot, SourceInvariants.DriverCalls, allowedPaths)` lists source files outside the allow list that create or run driver commands or bulk routes (comments and string literals are ignored). `InvariantTests` applies it to the Transfer packages; Build and Sync call it over their own `src`.

## Not done

Arrow-first/Parquet staging, MySQL/Oracle bulk writers, `ITransferVerifier` implementation, segmenter/worker lanes, SCD2/Snapshot, Windows ACL on the statement log, Sync/Build adoption.
