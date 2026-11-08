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

| `delete_insert_by_scope` | delete a scope (`ScopeFilter`: all rows, a list, or a half-open range of one column), then insert the stage rows without delete-marked ones | Keyless. `ScopedOptions.Marker` (`__Operation`, `D` deletes), `Ordinal` (refill one slice of the stage's `__Ordinal`). |
| `merge_staged_changes` | one `MERGE` by key: delete-marked stage rows delete, others upsert; optional ordinal slice; optional delete-unmatched within a scope | Delete-unmatched is a `MERGE` into a CTE (`WHEN NOT MATCHED BY SOURCE`): SQL Server only. PostgreSQL 15+ without it. |

### Scope-shaped statements and callbacks (plan 13, A16a)

`ScopedStatements` (`ScopeDelete`, `Refill`, `Merge`, `RenderScope`) is the dialect-free core under those two strategies: it takes a `ScopedApplySpec` and a `StatementSyntax` and returns a `BuiltStatement`: the text plus an ordered list of `ParameterSlot`s (position, logical name, marker text, role, the scope column, the value to bind). Values never reach the text; the **caller binds each slot to its own typed parameter** (Sync: `varchar` against a `varchar` column, `date` against `date`, so the index is still seeked). Three choices are fixed:

1. **A structured scope**, not an opaque predicate: the builder owns the shape (`1 = 1`, `IN (...)`, `>= min AND < max`) and the placeholder names (`seg0..`, `segMin`, `segMax`, `afterOrdinal`, `upToOrdinal`).
2. **The generated-column override is a style the request carries** (`GeneratedColumnOverride`: `None`, `Clause` = the caller renders the INSERT head via `StatementSyntax.InsertInto`, `Bracketed` = the statement comes back with `NeedsOverrideBracket` and the caller wraps it, e.g. SQL Server `SET IDENTITY_INSERT`). `ISqlDialect` has no new member; its `IdentityInsert` capability stays the "engine supports it" signal.
3. **Markers and quoting are callbacks** (`StatementSyntax.QuoteIdentifier`, `ParameterReference(position, name)`); `StatementSyntax.From(dialect)` is the shared-dialect way in, and `ApplyRequest.Scoped.Syntax` lets a strategy caller supply its own. `TransactSql` selects the T-SQL spelling of MERGE (`;`, `BY TARGET`, qualified update targets). `AdoWriteExecutor` binds by the marker (`@x`/`:x` named, `$1`/`?` unnamed), so a caller's style binds as it reads.

Sync kind names resolve through `StrategyMatrix.Resolve` (`MsSqlMerge` to `merge_by_key`, ...). `Snapshot`, `Scd2` and `KeyReconcileScd2Close` stay Sync-only.

## Capability matrix

`Matrix/strategy-matrix.json` (embedded, replaceable via `StrategyMatrix.Parse`) says per strategy and engine: `reference` (verified by this repo's conformance), `product-verified` (Build/Sync verified it, not this repo), `unverified`, or `unsupported`, plus `min_version`. `Check`/`Require` reject unknown engines, unsupported rows, and too-old versions; `requireVerified` rejects `unverified`. Fabric stays unverified everywhere.

## Staging

`StagingNamer.Name` gives `stg_<schema>__<table>`, hash-shortened (`_` + 8 hex of SHA-256 of `schema.table`) to the identifier limit in UTF-8 bytes; `StagingPolicy.BuildCompatible` reproduces Build's names exactly (60 bytes, tracking schema). `RunScoped` appends `_r<8 hex>`. Temp variants: `#name` (SQL Server), `CREATE TEMP TABLE` (Postgres, DuckDB), `CREATE TEMPORARY TABLE` (MySQL), `CREATE GLOBAL TEMPORARY TABLE ... ON COMMIT PRESERVE ROWS` (Oracle). Lifecycle: `DropOnSuccess` (default; retained after failure so a deterministic name can resume), `DropAlways`, `Retain`. `GenericStagingProvider` creates, loads (any `IBulkWriter`, default `BatchInsertBulkWriter` with per-engine parameter limits) and drops through the gate. Permissions: persistent staging needs CREATE/DROP TABLE on the staging schema; temp tables need none beyond TEMP/connect.

## WriteGate

`WriteGate` is the single choke point (Build's `MutationGate`, generalised): every statement carries a `WriteIntent`; `AccessMode.ReadOnly` refuses all writes; `Permitted` limits effect classes; the log entry is written **before** execution and an unwritable log aborts the statement; dry-run runs every check and log line and executes nothing; `RunAsync` wraps a `StatementSet` in a transaction and evaluates the delete guard; `AcquireLockAsync` is a session lock (`sp_getapplock`, `pg_try_advisory_lock`; other engines throw `NotSupportedException` from the ADO executor). `Sibling(executor, permitted?)` is another gate of the same run for a session of its own (loads that run side by side): one command, log, value salt and ordinal. A refusal is a `GateRefusedException` whose `Reason` (`NotPermitted`, `ReadOnly`, `LogUnavailable`) lets a product word it itself. `SqlStatement.Payload` carries what the product's own executor needs beyond the portable parameters (never read or logged by the gate); `ParametersAreRows` makes the log count the values of a bulk insert instead of listing them. Log detail: the default keeps hashes and types only; `WithText` adds the statement text; `WithValues` adds the parameter values (after the redactor). `WriteGateOptions.LogLockResource` puts the lock's resource name in the logged text (off by default). A statement's hash reads line endings as LF. DbDataBuild runs its writes through this gate (its `MutationGate` is the front, at `WithValues`).

Statement log (`StatementLogDetail`): default `HashAndTypes` = statement hash, parameter names, canonical types and per-run salted value hashes (no text, no values); `WithText` adds text; `WithValues` adds values. Text, values and outcomes pass through the `IRedactor` (`DbDataKit.Secrets.Redactor`). Failures log the exception type and driver error code only. `FileStatementLog` writes flushed JSON lines, owner-only on Unix; on Windows it inherits the directory ACL (put it in a restricted directory; an explicit ACL is not set yet).

`SourceInvariants` is the reusable form of Build's `GateInvariantTests`: `FindOffenders(sourceRoot, SourceInvariants.DriverCalls, allowedPaths)` lists source files outside the allow list that create or run driver commands or bulk routes (comments and string literals are ignored). `InvariantTests` and `GateInvariantTests` apply it to the Transfer packages and to DbDataBuild's `src` (the gate invariant of plan 13, B06: one rule, run from the shared suite and from Build's own); Sync calls it over its own `src`.

## Not done

Arrow-first/Parquet staging, MySQL/Oracle bulk writers, `ITransferVerifier` implementation, segmenter/worker lanes, SCD2/Snapshot, Windows ACL on the statement log, Sync/Build adoption.

## Strategy semantics in Sync and in Build

Written from the code of both products. What each strategy means in each product, and the decision this package took for it. Sync's `DeleteGuard` is the shared ratio guard; Build's `MutationGate` is generalised as `WriteGate`.

| Aspect | Sync | Build | Shared decision |
|---|---|---|---|
| Unit of input | A *change set* (rows with an operation column I/U/D and ordering columns) staged on the target, then applied by a writer. | A *query result* (full or windowed) staged once into a temp or `stg_` table. No operation column. | Strategy input is a staged table plus a `WriteIntent`; the operation column is a Sync extension handled Sync-side. |
| Delete handling | Explicit: rows flagged `D` (CDC/CT/triggers); `Merge` deletes on `D`; `DeleteInsert` empties the *scope* (segment or whole table) so absent rows vanish; `KeyReconcileDelete` anti-joins target keys against a staged key set. | Implicit: `full_replace` deletes everything; `delete_insert_by_key` deletes only keys present in the stage (absent rows are never removed); `delete_insert_by_range` deletes the `[start,end)` window; `merge_by_key` never deletes; `watermark_append` replaces the window from the watermark (with lookback) or never deletes. | Keep distinct strategies. Shared SQL builders for the six listed; `D`-flag handling stays in Sync writers. |
| Delete safety | `DeleteGuard`: `Ratio(MaxRatio=0.5)` by default, or `None`. Count scope, delete, compare, roll back on breach. An empty scope passes. | None (bounded by definition: range or key). | The ratio guard is shared (`DeleteGuard.Evaluate`). |
| Transactionality | `DeleteInsert` and `KeyReconcileDelete`: one transaction (delete plus chunked inserts; never commit per chunk, so readers never see a half-filled scope). `Merge` commits per chunk (idempotent). `Snapshot` is one insert. Staging is outside the transaction. | One script per load: BEGIN (XACT_ABORT ON on SQL Server), stage, delete or merge, insert, COMMIT. Stage is `CREATE TEMP TABLE ... ON COMMIT DROP` on Postgres and `#temp` on SQL Server. Cross-engine copies use a persistent `stg_` table in the tracking schema, outside the load transaction. | Builders emit an ordered `StatementSet` with an explicit `Transactional` flag; the WriteGate owns BEGIN/COMMIT. |
| Idempotency | `Merge` and `DeleteInsert` are idempotent per staged set; the watermark advances only after apply, so a re-run re-reads and re-applies. `Snapshot` and `Scd2` append and are not idempotent for one run. | All five strategies are conformance-tested for rerun idempotency against a DuckDB reference. `watermark_append` without lookback is append-only and idempotent only because the watermark moves. | The conformance suite asserts idempotent re-run for every shared strategy; non-idempotent strategies are labelled. |
| Ordering | Change rows carry ordering columns (LSN or sequence); writers apply in order and resolve delete+insert of one key in a change set. For replace within a segment, order is irrelevant. | Set semantics. Stage order is not preserved; duplicate keys in a stage are inserted as given (key uniqueness is the model's contract). | Shared strategies are set-based; ordering resolution stays in readers and Sync writers. |
| NULL keys | Writer-defined (`=` joins). | Documented: rows with a NULL key never match in `delete_insert_by_key`. | Contract: NULL keys never match (no NULL-safe compare). |
| Failure | Transaction rolls back; staging table cleaned up (`CleanupAsync`) on success or failure; position not advanced; the run process re-queues the work item. | Transaction rolls back; temp stage disappears; the step is marked failed in tracking tables; the `stg_` copy table is retained so an interrupted copy reruns on the same deterministic table; the app lock is released with the session. | Staging policy lifecycle: `DropOnSuccess`, `RetainOnFailure`, `AlwaysDrop`; deterministic names with optional run suffix. |
| Resume | State DB: per-mapping watermark, work queue, segment progress; resume at segment granularity. | Tracking tables: last applied hash and resolver values; apply refuses when base hash or resolver differ (`base.stale`, `resolver.stale`); resume means re-run the step. | `ITransferStateStore` interface only; schemas stay product-specific. |
| Concurrency | Worker lanes; parallel segments; per-mapping ordering. | One app lock per target (`sp_getapplock` / `pg_try_advisory_lock`), shared for refresh and exclusive for deploy; sibling gates for parallel loads. | App-lock abstraction in WriteGate (shared and exclusive, optional wait). |
| Write choke point | None; writers call ADO directly (preview and hooks exist). | `MutationGate` is the only place a command object exists (source-scanning invariant test). The statement log is written before execution; an unwritable log aborts the statement; dry-run runs checks and log but executes nothing; permitted effect classes per command. | Generalise as `WriteGate`. Default log: text hash plus parameter types and hash; values opt-in; redaction via `Redactor`. |
| Type conversion | `*ValueBinding` per engine and `GenericValueBinder`. | `TransferValues.Convert` by declared logical type; errors name the column only. | One `ValueConverter` with a plan per column, fidelity from the Dialect, error policy per column. |
| Engine coverage | SQL Server, Postgres, MySQL, Oracle, DuckDB, ODBC. | SQL Server and Postgres verified; DuckDB native reference; Fabric unverified; MERGE needs PG 15 or later. | Capability table as data; Fabric stays unverified. |
| Bulk write | `SqlBulkCopy` from a reader, PG binary COPY, batch INSERT providers. | `SqlBulkCopy` over a `DataTable` buffered at 20,000 rows; Npgsql binary import. | `SqlBulkCopy` from a reader with no DataTable buffering; typed COPY writers. |
| Staging name | `DS_STG_<guid>` in the target schema, per call. | `stg_<schema>__<table>`, hash-shortened (over 60 UTF-8 bytes: first 51 characters, `_`, 8 hex of SHA-256 of the model name). | Policy supports both: deterministic, with an optional run suffix. |

### Differences that must not be unified

1. Operation-column change sets (Sync) versus set-based replacement (Build).
2. Commit-per-chunk upsert (Sync Merge) versus one script transaction (Build).
3. Staging naming (random per call versus deterministic for resume).
4. SCD2 and Snapshot (Sync-only) and key-reconcile (guarded) are outside Build's matrix.
