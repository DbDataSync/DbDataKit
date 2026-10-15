# Plan 07a: Sync vs Build strategy semantics (Phase 0)

Status: written from the code of both products (read-only). Sources: Build `matrix/strategies.yml`, `matrix/decision-table.yml`,
`Targets/Loaders/LoaderBase.cs`, `Execution/MutationGate.cs`, `Execution/Transfer.cs`; Sync `Drivers.Generic/{DeleteInsert,Snapshot,KeyReconcileDelete,Scd2}Writer.cs`,
`Drivers.MsSql/MsSqlMergeWriter.cs`, `Core/Config/DeleteGuard.cs`, `Drivers.Abstractions/{IChangeWriter,IStagingProvider}.cs`.

## 1. Semantics table

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

## 2. Differences that must not be unified

1. Operation-column change sets (Sync) versus set-based replacement (Build).
2. Commit-per-chunk upsert (Sync Merge) versus one script transaction (Build).
3. Staging naming (random per call versus deterministic for resume).
4. SCD2 and Snapshot (Sync-only) and key-reconcile (guarded) are outside Build's matrix.

## 3. Interchange decision

Start with `DbDataReader`-shaped streaming (both products already use reader-style bulk paths) and `AsArrow()` as an adapter. Arrow-first and
Parquet staging are deferred pending benchmarks. The benchmark baseline of the current bulk paths was not measured in this phase (no live engines
available); a benchmark project stub ships with the Transfer packages.

## 4. Consequences for the shared code

- Shared strategies: `full_replace`, `delete_insert_by_key`, `merge_by_key`, `delete_insert_by_range`, `watermark_append`, `key_reconcile_delete`.
- Name aliases map Sync kinds (`DeleteInsert`, `MsSqlMerge`, `KeyReconcileDelete`) to the shared names; the mapping is config-level (Plan 01 migrations).
- Every statement is parameterised; identifiers are quoted via `ISqlDialect`; the only literals in SQL text are structural.
