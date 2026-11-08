# Plan 07: Data Transfer, Staging and Loading (`DbDataKit.Transfer`)

Status: in progress. What is built is in `DbDataKit/src/DbDataKit.Transfer*/README.md` (value conversion, staging policy, strategy SQL builders and the capability matrix, the write gate, and the Sync and Build strategy semantics). This plan keeps only what is not built.

## What is left

1. **Arrow-first and Parquet staging.** Today the interchange is `DbDataReader`-shaped streaming with `AsArrow()` as an adapter. Arrow-first (columnar, typed, zero-copy to Parquet and Flight) stays the long-term direction, but building columns from rows has a cost: benchmark it against the current bulk paths first (Sync has `tools/DbDataSync.Benchmarks`), and implement Parquet and Arrow files as another `IStagingProvider`, not a special case.
2. **A shared transfer verifier** (`ITransferVerifier`): row counts, key checks and column hashes by canonical type, generalised from Sync's `Verification` (source against target, result files). Build's `compare` and tests reuse it; Sync keeps the result-file storage.
3. **Segmenting.** Sync's `ISegmenter` (range, list, auto, expansion) and the worker-lane scheduling stay Sync-side behind the shared interfaces; move them into a shared package only when Build needs parallel copies.
4. **MySQL and Oracle writers in the shared packages.** The writers exist in Sync's drivers (MySQL bulk copy, Oracle array binding, SCD2 included). The shared extraction is partial: `DbDataKit.Transfer.SqlServer` and `.Postgres` exist; `.MySql` and `.Oracle`, and SCD2 and Snapshot as shared strategies, do not. Move them when a second product needs them.

Non-goals: a scheduler, a change-data-capture framework for every engine (stays in Sync), query transpilation (Build). Orchestration, and the state (`ITransferStateStore` stays an interface; Sync's state database and Build's tracking tables are product-specific), stay in the products.

## Write-gate rules

One choke point for database writes, taking a `WriteIntent` (kind, target, risk). Both products must keep these:

- Writes go only through the gate; a source-scanning test (Build's `GateInvariantTests` pattern) proves no command object is created elsewhere. Sync's writers adopt the gate for the same audit trail.
- The gate enforces `access: readonly`, the per-target app lock (`sp_getapplock`, `pg_try_advisory_lock`; shared for refresh, exclusive for deploy), transaction control and retry classification (`ConnectionFailure`).
- The statement log is written before execution; an unwritable log aborts the statement; dry-run runs the checks and the log and executes nothing.
- The log holds the statement's text hash and the parameter types by default, values only by opt-in, redacted through `IRedactor`, in a file with a restricted access list.
- Every strategy has a conformance test against a DuckDB reference, including rerun idempotency, on every engine fixture; a strategy that is not idempotent is labelled. Fabric stays unverified; do not mark a strategy verified for an engine this repository did not run.
- Performance: every extraction of a hot path is gated by a benchmark, with no regression against the current bulk paths.
