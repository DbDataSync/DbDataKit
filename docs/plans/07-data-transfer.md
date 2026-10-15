# Plan 07: Data Transfer, Staging and Loading (`DbData.Transfer`)

Status: proposed. Depends on: Plans 05 (dialect/types), 06 (connections). This is the largest design task; start with
Phase 0 (written comparison) before writing code.

## 1. Problem

Sync and Build both move rows between engines with different dialects and types; Proxy streams rows to Arrow.

| | Sync | Build | Proxy |
|---|---|---|---|
| Read | `IChangeReader`: Watermark, BatchReload, TriggerAudit, KeyReconcile; native CT/CDC, PG logical slot, Oracle Flashback; streaming `ReadResult` + position | `IRowSource` (ADO, DuckDB, ODBC, Flight SQL) via `ReadSession` + `ReadGuard` | `DbDataReader` to Arrow batches (32768 rows) |
| Stage | `IStagingProvider`: batch INSERT, `SqlBulkCopy`, PG binary `COPY`; target-side staging table; Parquet staging deferred | bulk write into `<tracking schema>.stg_<schema>__<table>` (name hash-shortened to 63 bytes); `SqlBulkCopy` (20,000 rows via `DataTable`), Npgsql import | none (stream straight to gRPC) |
| Load | writers: `DeleteInsert`, `Merge` (MsSql), `Snapshot`, `Scd2`, `KeyReconcileDelete`, `KeyReconcileScd2Close`; delete guard | strategies: `full_replace`, `delete_insert_by_key`, `merge_by_key` (PG >= 15), `delete_insert_by_range`, `watermark_append`, `view` | n/a (updates run per parameter row, max 10,000) |
| Types | canonical set, `*ValueBinding` per engine, `GenericValueBinder` | `TransferValues.Convert` by declared logical type, `ClrType/PgType`, `ArrowConversions` | `ArrowColumnWriter` |
| Parallelism | segments (range/list/auto) in worker lanes; per-mapping watermarks | per-target session app lock; resume after failure | per-owner pending query cap |
| Safety | delete guard, tie-safe limits, hooks | `MutationGate` (single write choke point), statement log, plan approval, drift refusal | none |
| State | State DB (runs, watermarks, queue) | tracking tables in the target (layout v5) | none |

Overlapping core: read rows, convert values by declared type, stage on the target, apply with a named strategy, record outcome.
Different orchestration: Sync is continuous/incremental with a scheduler and run process model; Build is plan/approve/apply with deterministic scripts.

## 2. Goals
1. Shared building blocks (sources, converters, bulk writers, staging, strategy SQL) behind interfaces; orchestration stays in products.
2. A single value-conversion matrix with explicit fidelity and error policy.
3. Strategy semantics defined once, conformance-tested against a reference, per engine.
4. A write choke point with audit logging and redaction usable by both products.

Non-goals: a scheduler, a change-data-capture framework for every engine (stays in Sync), query transpilation (Build).

## 3. Design

### 3.1 Core abstractions (`DbData.Transfer.Abstractions`)
```csharp
record TransferSchema(IReadOnlyList<TransferColumn> Columns);                 // canonical types (Plan 05), names, nullability, key flags
interface IDataSource { TransferSchema Schema {get;}  IAsyncEnumerable<DataBatch> ReadAsync(ReadRequest r, CancellationToken ct); }
abstract class DataBatch { int Rows; TransferSchema Schema; DbDataReader AsDataReader(); RecordBatch AsArrow(); }
interface IBulkWriter   { Task<long> WriteAsync(IWriteTarget t, IAsyncEnumerable<DataBatch> rows, BulkOptions o, ...); }
interface IStagingProvider { Task<StagingTable> CreateAsync(...); Task LoadAsync(...); Task DropAsync(...); }
interface IApplyStrategy { string Name; StrategyCaps Needs; Plan<StatementSet> Build(ApplyRequest r, ISqlDialect d); }
interface IChangePosition { string Serialize(); }                              // watermark/LSN/slot position, opaque to shared code
```
Interchange decision: start with `DbDataReader`-shaped streaming (both products already use IDataReader-style bulk paths:
Sync `ChangeRowDataReader`, Build `DataTable` buffering) and add `AsArrow()` as an adapter (Proxy's batching code), then measure. Arrow-first
(columnar, typed, zero-copy to Parquet/Flight) is the long-term direction but .NET row-to-column building has a cost that must be benchmarked.
Sync's native change readers (CT/CDC/logical slot/Flashback) remain in Sync as `IDataSource` implementations that also return an `IChangePosition`.

### 3.2 Value conversion (`ValueConverter`)
One matrix canonical x canonical (and CLR x target parameter type) replacing Sync `*ValueBinding`/`GenericValueBinder`,
Build `TransferValues`/`ClrType`/`PgType`/`ArrowConversions`, and Proxy `ArrowColumnWriter`:
- `Convert(source, target) -> ConversionPlan` computed per column once (not per value), with `Fidelity` from Plan 05.
- Error policy per column: `Fail` (default), `Null`, `Truncate`, `Clamp`; errors name the column and never the value (Build `TransferException`).
- Precision-specific handling (Decimal128 overflow guard, datetimeoffset to timestamp tz, SQL Server `time`/`datetime2` precision, Postgres `numeric`
  NaN/Infinity, text with NUL bytes, unpaired surrogates).
- Golden tests from the Plan 05 `types.golden.yml` plus value edge-case tables.

### 3.3 Bulk writers (`DbData.Transfer.SqlServer`, `.Postgres`, `.MySql`, `.Oracle`, generic)
Extract from Build `MutationGate.BulkCopySqlServerAsync/BulkCopyPostgresAsync` and Sync `MsSqlStagingTableProvider`/`PgCopyStagingProvider`/`BatchInsertStagingProvider`:
- SQL Server: `SqlBulkCopy` fed directly from `AsDataReader()` (avoid Build's `DataTable` buffering, the review's noted perf concern), batch size and timeout options.
- Postgres: binary `COPY ... FROM STDIN` with explicit type writers (Sync `PostgresValues.cs`).
- MySQL (`MySqlBulkCopy`), Oracle (`OracleBulkCopy`/array binding), generic batched parameterised INSERT with parameter-count limits per engine.
- Common options: batch size, transaction mode, identity handling, keep-nulls, timeouts, cancellation, progress callback.
A benchmark harness (Sync already has `tools/DbDataSync.Benchmarks`) tracks throughput regressions.

### 3.4 Staging
`StagingPolicy`: location (target schema, e.g. tracking schema in Build; Sync's staging table), deterministic naming with hash-shortening to the engine's
identifier limit (Build `stg_<schema>__<table>`), run-scoped suffixes for concurrent runs, lifecycle (create, truncate, drop on success, retain on
failure for resume), permissions requirements documented per engine, temp-table variants where DDL rights are missing (`#temp`, `CREATE TEMP TABLE ... ON COMMIT DROP`).
Alternative stagings (Parquet/Arrow files, Sync phase 150) implement the same interface later.

### 3.5 Apply strategies
Define semantic contracts once, with engine-specific SQL builders using Plan 05 dialects and capability negotiation:

| Strategy | Sync name | Build name | Notes |
|---|---|---|---|
| Replace all | Snapshot/BatchReload+DeleteInsert | `full_replace` | truncate vs delete-all by capability; transactional behaviour documented |
| Delete+insert by key | `DeleteInsert` | `delete_insert_by_key` | portable baseline |
| Merge/upsert | MsSql `Merge` | `merge_by_key` | SQL Server MERGE, Postgres MERGE (>= 15) or `ON CONFLICT`, MySQL `ON DUPLICATE KEY` |
| Delete+insert by range | segment replace | `delete_insert_by_range` | bound parameters |
| Append with watermark | Watermark reader + insert | `watermark_append` | lookback window replaced not duplicated |
| SCD2 / snapshot history | `Scd2`, `Snapshot` | n/a | Sync-only initially |
| Key-reconcile delete | `KeyReconcileDelete` + `DeleteGuard` | n/a | Sync-only; guard shared |

Strategy selection is data: a capability table (Build `matrix/strategies.yml` + `decision-table.yml`) says which strategy is valid per engine/version;
invalid combinations are rejected at validation time (Sync `ConfigValidation` kind rules). Each strategy ships a **conformance test** against a DuckDB reference
(Build's differential-testing approach, including rerun idempotency) run on every engine fixture.

### 3.6 Write gate and audit
Generalise Build `MutationGate`: one choke point for DB writes taking a `WriteIntent` (kind, target, risk), enforcing `access: readonly`, app lock
(`sp_getapplock` / `pg_try_advisory_lock`, Build), transaction control, retry classification (Plan 06 failures), and a statement log.
Statement log: text hash and parameter **types** by default, values opt-in, restrictive file ACL (Build F-6). Build's `GateInvariantTests` pattern
(source-scanning test that writes only go through the gate) ships as a reusable analyzer/test. Sync adopts the gate in its writers for the same audit trail.

### 3.7 Segmenting and concurrency
Move Sync's generic `ISegmenter` (range/list/auto, expansion) and worker-lane scheduling into a package only if Build needs parallel copies; until then
keep it Sync-side but behind the shared interfaces. Per-target lock and parallelism caps come from the gate and Plan 06 limits.

### 3.8 Verification
Sync's `Verification` (source vs target compare, Parquet/DuckDB result files) generalised as `ITransferVerifier` (row counts, key checks, column hashes
by canonical type). Build's `compare` and tests can reuse it; Sync keeps result-file storage.

### 3.9 State
`ITransferStateStore` interface only (get/set watermark by key, record run outcome, resume tokens). Sync's State DB and Build's tracking tables remain
product-specific implementations; do not share a schema.

## 4. Phases

| Phase | Work | Exit |
|---|---|---|
| 0 | Written comparison: strategy semantics (delete handling, transactionality, idempotency, ordering guarantees, failure/resume) for Sync vs Build; benchmark baseline of current bulk paths; decide interchange (reader vs Arrow) | `docs/plans/07a-strategy-comparison.md` reviewed |
| 1 | Abstractions + `ValueConverter` with golden/edge tests; port Build `TransferValues` first | Build copy tests unchanged |
| 2 | Bulk writers: SQL Server then Postgres; benchmarks show no regression vs current | Benchmark gate in CI |
| 3 | Staging providers + policy; Build copy models and Sync providers adopt | Both suites green |
| 4 | Strategy SQL builders + conformance suite; port `delete_insert_by_key`, `merge_by_key`, `full_replace`, `watermark_append`; Sync writers adopt the shared ones where semantics match | Conformance green on SQL Server and Postgres |
| 5 | Write gate + statement log redaction; Sync adoption | Gate invariant test in both repos |
| 6 | Arrow adapter/Parquet staging, MySQL/Oracle writers, shared verifier | Optional |

## 5. Risks
- **Semantic mismatch** (Sync continuous change application vs Build deterministic plans) is the main risk; shared code must not own orchestration.
- Delete semantics differ (Sync delete detection via reconcile; Build replaces ranges); keep them as distinct strategies.
- Sync's Kinds are strings configured by users in YAML; renaming breaks configs. Map names via aliases and Plan 01 migrations.
- Performance regressions in hot paths; every extraction is gated by benchmarks.
- Fabric is unverified in Build; do not mark strategies verified for it.

## 6. Acceptance
Value conversion and bulk writers exist once; both products run the same strategy SQL for the shared strategies and pass the same conformance suite;
writes in both products flow through the gate with redacted statement logs; benchmarks show parity or better.
