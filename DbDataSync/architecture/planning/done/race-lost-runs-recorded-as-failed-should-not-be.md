# A run that loses a race against other in-flight work is recorded as "Failed" — it shouldn't be

**Status: done (2026-11-09, owner decision).** A pass that loses the initial-load race ends `Succeeded`; see "Decision" at the end.

## The problem

Two existing cases in `RunExecutor` (`src/DbDataSync.TaskRunner/RunExecutor.cs`) catch a run that did
nothing wrong — it simply lost a race against other work already in flight for the same mapping — and
still record it as `RunStatus.Failed`:

- `WorkQueueCollisionException` → `RunFailureKinds.ConcurrentLoadInProgress` — an auto-triggered initial
  load lost its race against other in-flight work for the same mapping+segment (phase 143).
- `MappingLoadingException` → `RunFailureKinds.MappingStillLoading` — a manual "Run Now" landed on a
  mapping still `ReadHold.Loading`.

Both catch blocks say outright, in their own comments, that there is nothing for an operator to do and
the mapping resolves itself on its next pass. That's not a failure — it's an expected, self-healing
non-event — but the Runs tab currently shows it exactly like a real failure needing attention, and
whatever ties failure counts/health rollups together (see `architecture/planning/done/run-metrics.md`)
would count it as one too.

## What the user suggested

Some other outcome instead of `Failed` — a warning, an info-level result, or a distinct "skipped" result.
`RunStatus` today is `Queued | Pending | Running | Succeeded | Failed | Cancelled` — `Cancelled` already
exists but is reserved for an explicit user-initiated cancel (`ProcessSupervisor`/`BulkLoadService`'s
"Cancelled by user request."), a different thing from a race the system itself decided to back off from,
so reusing it as-is would blur two meanings that are worth keeping distinct.

## Open questions

- New `RunStatus` value (e.g. `Skipped`), or keep `Failed`/`Cancelled` as the only two terminal-but-not-
  `Succeeded` states and instead lean entirely on `RunFailureKinds`-style metadata to say "this one's
  fine, ignore it"? A new status is the more honest fix but touches every place that already
  pattern-matches `RunStatus` (`JournalRecovery`, health rollups, the Runs tab, filters, exit codes).
- Does this apply to *only* these two existing exception cases, or does it become the general answer for
  "the run system itself decided not to run this rather than something failing" — which is exactly the
  shape the sibling doc
  (`architecture/planning/todo/mapping-added-to-running-replication-without-source-provisioning-never-syncs.md`)
  also wants for a mapping skipped due to unmet source provisioning?
- Whatever aggregates "failures" for a health view needs to actually exclude the new outcome, not just
  the Runs tab's own status label — an easy thing to fix in one place and miss in another.
- Naming: "Skipped" reads right for the provisioning case above (a tick chose not to run it) but slightly
  off for these two (a run *did* start and then bailed once it discovered the collision) — "warning" per
  the user's own suggestion might fit the existing two better than "skipped" does, or these could end up
  as two different outcomes rather than one.

## Decision (2026-11-09)

The owner decided this is a product fix: a Sync run that loses the initial-load race must not end `Failed`. It lit up Monitoring and the run history for nothing, and it made
`ConcurrentRunsIntegrationTests.TriggeringManyReplicationsAtOnce_AllSucceed_WithNoStateStoreContentionFailures` fail intermittently on `main` (8 replications with no stored position triggered at
once; the API trigger and the scheduler tick can both queue a Primary pass for one mapping, and the second one finds the load of the first in flight).

**Chosen: the losing pass ends `Succeeded`, moved nothing (0 read, 0 written), with `Not run: ...` and the reason as its summary and no failure kind.** Both exceptions (`WorkQueueCollisionException`
from `RequestInitialLoad`, `MappingLoadingException` from the `Loading` hold check) are handled by the one catch in `RunExecutor.ProcessWorkItemAsync`, so the supervisor path and the runner path
(`DrainJob`, which only ever sees the outcome) agree without a second copy of the rule.

Why this and not the alternatives, against the open questions above:

- **A new `Skipped` status** would be the most explicit, but every reader of `RunStatus` has to learn it, and the shared run model has no such outcome (`RunOutcome` is shared with Build and the runner;
  `TaskRunStore.OutcomeOf` and `SyncRunHistory.ToRecord` map an unknown status to `Running`, a run that never ends). In the shared model it could only be `Succeeded` anyway, so the new value would add a
  stored value (the status text, the SPA's `RunStatus`, filters, `StatusBadge`, the failed-run counts) for no change in what Monitoring sees.
- **`Cancelled`** maps to `RunOutcome.Stopped`, which `HealthEvaluator` reports as a Warning finding ("was stopped before it finished"): Monitoring would still flag the lost pass. It also blurs the
  operator's cancel with a decision of the system.
- **Not creating the pass** is not possible where it matters: a trigger returns run ids the caller polls, the queue already collapses a pass queued while another of the same kind is in flight, and the
  loser is by definition queued after the winner's pass left the queue (the hold is only set by the winner's own `RequestInitialLoad`).
- **`Succeeded` with the reason** needs no new status and no state migration, and it is the shape the winning pass already has: the pass that requests the initial load also reads and writes nothing and
  ends `Succeeded`. The reason is the summary text (`error_summary`), which the run API returns for any run; the Runs tab's details dialog now shows it for a succeeded run. The failure kinds
  `ConcurrentLoadInProgress` and `MappingStillLoading` stay defined, because history written before this change carries them, but nothing records them any more.

Unchanged: the lost request still leaves no pending position behind (phase 143 orders the enqueue before `SetPendingLoad`), so the mapping is never held for a load that will not run, and its next pass
runs normally once the load completes. A real failure of the initial load itself still fails the load's own run. What this means for the other question (the sibling doc about a mapping skipped for
unmet provisioning): that case is not decided here; if it is built it can use the same `Not run:` summary convention.
