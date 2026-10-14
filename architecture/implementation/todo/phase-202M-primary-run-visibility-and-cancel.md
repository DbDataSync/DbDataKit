# Phase 202M — A Primary run says what it is doing, shows the SQL it runs, and can be cancelled

**Status**: Parts 1 and 2 built (run log only). Part 3 planned, not started.
**Plan reference**: none — reported from a real run. A mapping with a custom segmenting strategy backfilled
correctly; a scheduled/"Run now" Primary pass of the same mapping ran longer, with no log of whether it
segmented, no SQL, no way to cancel, and an idle-looking runner process.

## What was found

- **Segmenting does apply to a Primary pass.** `RunExecutor.ResolveSegmentsAsync` runs for every work item and
  uses the mapping's `DefaultSegmenting` (expanding `Custom` strategies to their *selected* candidates, and
  `Auto`). So the pass was probably doing the right thing — but nothing said so. The only segment log was
  `Processing N configured segment(s)`, written only when N > 1. A strategy that selected one candidate, or
  none, logged nothing at all, and a strategy whose own query is slow ran silently before any log line.
- **No SQL anywhere in the run log.** Readers, staging providers and writers already describe their
  statements through `IStatementPreview` (what the Preview screen uses), but the executor never asked.
- **Cancel is narrower than it looks.** The only Cancel button is on the Live run panel, for the run you
  triggered in this browser session (`activeRunId`); history rows have none. `ProcessSupervisor.CancelRun`
  cancels a *pending* item directly, but for a running one it kills the whole worker process, and only if
  *this API instance spawned it* (`_workers`): a worker it didn't start (previous API instance, service,
  scheduler) gives "No cancellable run with that id on this API instance". Killing the worker also fails every
  other run in it.

## Part 1 — built: say what the segmenting did

`ResolveSegmentsAsync` now logs: that a request carried its own segment (default not used); that no default
segmenting is set (one whole-table query); the default list; and the resolved list (first 20, with a count),
every pass. A resolution to zero segments is a **warning** ("reads and writes nothing").

## Part 2 — built: log the SQL

Before each segment's read, `LogPlannedStatementsAsync` asks the reader, staging provider and writer for
their `IStatementPreview` statements — built from the same inputs the run uses, from the mapping's cached
source/target columns — and writes each as an Info log line (`'<mapping>' (<segment>) source read — <title>:`
then the SQL). Best effort: a component that can't describe itself is logged as such; a failure is a warning
and never stops the pass. Because the run log is what History shows, the SQL is in history too.

Not covered: statements a *segmenting strategy* itself runs (the Source SQL / Target SQL / DuckDB text), and
the metadata queries around a pass. Add to the strategy runner's log.

## Part 3 — planned: cancel a run that is running

1. **Cooperative cancel.** A `CancelRequested` marker on the work-queue row (state-database migration, and the
   remote-state protocol, so a worker on another host sees it). `CancelRun` sets it for a Claimed/Running item;
   the worker's item loop polls it (cheap, ~1s) and cancels that item's linked `CancellationToken`, which the
   executor already threads through readers, staging and writers. Only that item stops; other runs in the
   worker continue. The kill-the-worker path stays as a last resort when the worker is silent.
2. **No dependence on who spawned the worker**, which fixes "not cancellable on this API instance".
3. **UI**: a Cancel action on each Queued/Pending/Running row in Runs history and on the run details dialog,
   not only the Live run panel.
4. A cancelled run ends `Cancelled`, releases its run lock, and logs who/when.

## Tests

Part 1/2 have no automated test yet (the executor's integration tests need SQL Server in this sandbox). Add: a
unit test for the segment-description text, and an executor test using the DuckDB source asserting the log
contains the resolved segment list and the reader SQL. Part 3: store-level cancel marker, worker honouring it
mid-read, API on a worker it didn't spawn.
