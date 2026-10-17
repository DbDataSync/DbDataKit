# Phase 202M — A Primary run says what it is doing, shows the SQL it runs, and can be cancelled

**Status**: Built. See "What was built" and "Not done".
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

Strategy SQL: built afterwards — `CustomSegmentExpansion.ExpandAsync` takes a log callback and writes the
strategy's name/kind/SQL before it runs and how many candidates it proposed and selected after. The metadata
queries around a pass are still not logged.

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

## What was built

- **Run log visible for any run** (the user's question: where is a worker's live log?). The only place was the
  Runs tab's *Live run* card, which follows a run started from that page in the current session. The run
  details dialog (click any status) now has a **Log** section, live (SignalR plus a 750 ms poll) while the run
  is queued/running and loaded once for a finished one. Log lines keep their line breaks so SQL is readable.
- **Cooperative cancel**: `WorkQueue.CancelRequestedUtc` (migration), `WorkQueueStore.RequestCancel` /
  `IsCancelRequested`, `IRunnerState.IsCancelRequested` (local, remote HTTP, `/internal/state/is-cancel-requested`).
  `RunExecutor.ProcessWorkItemAsync` runs each item under its own linked token and a 1 s watcher; on a request it
  logs, cancels that token, and records the run `Cancelled` (the queue item is closed `Failed`: it didn't
  complete). `ProcessSupervisor.CancelRun` records the request for a claimed/running item and returns; it
  kills the worker only if the run is still `Running` 60 s later and the worker is one this API spawned.
- **UI**: Cancel on every queued/pending/running history row and in the details dialog.

## Not done

- Part 1/2 and the watcher have no end-to-end test: the executor tests need SQL Server/Postgres, which this
  sandbox lacks. Covered: the queue marker (`WorkQueueStoreTests`), TypeScript typecheck, web unit tests.
  The cancel catch is exercised only by reading; **run a real pass, cancel it, and watch the log** before
  relying on it.
- The details-dialog log was not driven in a browser.
- A segmented Primary pass still shows as one run with its segments in the log. Showing pending/completed
  segments the way a Backfill does is the next conversation.
- The work item is closed `Failed` rather than `Cancelled` (no state-interface method for it); a future
  `MarkCancelled` would be tidier.
