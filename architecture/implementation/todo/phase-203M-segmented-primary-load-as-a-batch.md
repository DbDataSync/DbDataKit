# Phase 203M — a segmented Primary load runs as a batch

**Status:** design, not started. Written after reading the Bulk Load enqueue path; open questions at the end.

## What the code does today

Segment *resolution* is shared. `RunExecutor.ResolveSegmentsAsync` serves every work item: an item's own
`SegmentJson`, else the mapping's `DefaultSegmenting` (a Custom strategy re-evaluated, then Auto expanded).

Segment *execution* is not. There are three paths:

| Path | Segments run | Visible as |
|---|---|---|
| Operator Bulk Load / Backfill (`BulkLoadService.EnqueueAsync`) | one `WorkQueue` item per segment, all under one `BulkLoadBatchId` | a batch: every segment pending / running / done, cancellable and retryable alone |
| Primary pass, intent `InitialLoad`, reader is `IPositionCapturing` (phase 134) | the pass captures its position and calls `RequestInitialLoad`, which enqueues a batch through `EnqueueForInitialLoadAsync` | a batch, the same |
| Primary pass, any other reader (the keyless / no change tracking case: BatchReload, query readers) | the segment loop inside **one** item, serially | one run; the segments appear only in its log |

The third row is the gap. The second row is why it looks as if the paths were unified — they were, for readers that
can capture a position.

## Design

When a Primary pass resolves to **more than one** segment and its reader has no position of its own, the pass becomes
a coordinator instead of running the loop:

1. Resolve the segments once, as today (so a Custom strategy runs once and is logged once).
2. Create a `BulkLoadBatches` row and enqueue one item per segment, reusing `CreateBatchAndEnqueueAsync`.
3. The Primary run completes, recording "dispatched N segments to batch X". The batch view is the progress view.

One segment (or none, meaning the whole table) keeps running inline: a batch of one adds nothing.

Both pieces already exist: segment items executed by a worker, and the batch view.

## Open questions

1. **Run kind.** Segment items would be `RunKind.BulkLoad`, so history tags a scheduled run `BULKLOAD`. Either accept
   that and show the trigger ("scheduled") beside it, or add a kind. Leaning: keep `BulkLoad`, show the origin.
2. **Overlap.** The next schedule fires while a batch is still running. Segments of the same mapping already collapse
   in `WorkQueue`; the coordinator should instead skip the tick and say so in its log, the way a `Loading` hold does.
3. **Writer semantics.** A segment item runs the BulkLoad pipeline: the writer scopes its delete/insert to the segment
   (`SegmentScope`). That is what a Primary segment loop does for BatchReload now; check it holds for each reader and
   writer a keyless table can use before relying on it.
4. **Failure.** Today one failed segment fails the pass. As a batch, the other segments finish and the batch ends
   `CompletedWithFailures`. Probably better; it changes what "the run failed" means.
5. **Where the coordinator runs.** In the worker (it already has the source connection and the strategy's scripting
   host) and enqueues through `IRunnerState`, as `RequestInitialLoad` does, so a remote runner needs no new path.

## Alternative considered

Report per-segment progress from inside the single run (a segment list on the run, updated as the loop goes). Much
smaller, but the segments still run serially in one process, can't be cancelled or retried one at a time, and would
be a second progress model beside the batch's. Rejected unless open question 4 argues for a single all-or-nothing run.
