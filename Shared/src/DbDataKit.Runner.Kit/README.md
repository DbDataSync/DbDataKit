# DbDataKit.Runner.Kit

What a .NET job for the runner writes (the runner is `DbDataRunner`; the engine is `DbDataKit.Runner`). The API came from four sample jobs and contains nothing a sample does not use. The wire protocol (`DbDataKit.Runner.Protocol`) is derived from it, not the other way round. Test a job without a runner with `DbDataKit.Runner.Kit.Testing`.

## Principles

1. **One base class, `RunnerJob<TInput, TProgress, TSlots>`** (`NoInput`, `NoProgress`, `NoSlots` for a job that needs none). A job overrides one method, `RunAsync(CancellationToken)`. Everything the runner provides (progress, steps, units, files, inbox, counters, connections) is a member of it; there is nothing to construct, register or inject.
2. **Everything the job names is typed:** its input record, its `Progress` class, its `Slots` class, and its steps, units and counters (declared once as static values). A wrong name or result type does not compile, and the console, the staging check and the simulator can list every step and counter without running anything.
3. **No replay.** On a retry the job's code runs again from the top. Work that must not be repeated is wrapped in a **step**, which the runner remembers by name and key and answers from memory. Code outside a step is ordinary code. (This is deliberately not the replay model of workflow engines: it is much harder for authors and for jobs in other languages.)
4. **The author states what a crash in the middle means.** Every step says how it recovers, and the call chain does not offer `RunAsync` until it has said so.
5. **Progress is a typed class**, saved with the run's outcome. **Reporting is two verbs**, `Status` and `Count`; units report for themselves. **Cancellation is the `CancellationToken`.**
6. **For a .NET job the C# types are the source of truth**; the manifest's task entry (inputs, connection slots, progress declaration, steps, units, counters) is generated from them by `ManifestGenerator.ForAssembly`, and the runner checks the package's manifest against its declaration at staging. A job in another language writes the manifest by hand and speaks the protocol through its own thin client.

## A job

```csharp
public sealed record DailyDropInput(DateOnly Day);
public sealed class DailyDropSlots : Slots { public Slot<IWarehouse> Warehouse { get; } = new("warehouse"); }
public static class DailyDropSteps
{
    public static readonly StreamStep<DateOnly> Download = Step.Stream<DateOnly>("download", day => $"{day:yyyy-MM-dd}");
    public static readonly ValueStep<DateOnly, ArchiveReceipt> Archive = Step.Value<DateOnly, ArchiveReceipt>("archive", day => $"{day:yyyy-MM-dd}");
}

[RunnerTask("daily-drop")]
public sealed class DailyDrop : RunnerJob<DailyDropInput, NoProgress, DailyDropSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        PayloadRef file = await Step(DailyDropSteps.Download, Input.Day)
            .Repeatable()                                   // recovery is required: no recovery, no RunAsync
            .StreamAsync(async (sink, ct) => { /* copy the remote file into sink, then delete it remotely */ }, ct);

        await using var warehouse = await Slots.Warehouse.OpenAsync(ct);
        await using var reader = file.OpenRead();           // streams back out, never loaded whole
        // ... insert in batches, Count(DailyDropCounters.Rows, batch.Count) ...

        ArchiveReceipt receipt = await Step(DailyDropSteps.Archive, Input.Day).Repeatable().RunAsync(ct => Archive.PutAsync(file, ct), ct);
    }
}
```

## The surface

* **Declared once, as static values:** `Step.Value<TIn,TOut>(name, key)` and `Step.Stream<TIn>(name, key)`, `Unit.Of<T>(name, key)`, `Counter`, `Slot<TConnection>`, a `Progress` class with `[Checkpoint]` and `[State]` members and `Keyed<T>` for per-key sets.
* **Members of the job:** `Step(...)`, `ForEachAsync(unit, items, parallelism, body, ct, options?)` (over `IEnumerable` and `IAsyncEnumerable`), `Inbox<T>()`, `Files`, `RejectAsync`, `Count`, `Status`, `SaveProgressAsync`, `Run` (`IRun`: id, attempt, channel, package, task, `Log` with secrets redacted, lineage).
* **Typed by:** input by a record whose properties are the manifest's `inputs`; progress by the `Progress` class (a change between versions is planned by the progress migration, `DbDataKit.State` README); connections by `Slot<T>` members (binding a slot to a connection of another kind fails at staging); recovery by the call's type state; inbox items by `Inbox<T>()` (a body that does not parse as `T` is a dead letter, not a crash).
* **An analyzer** (`DbDataKit.Runner.Kit.Analyzers`) adds what the type system cannot: `DBKR001` two steps with one name in a job, `DBKR002` a step used inside another step.

## Steps

A step is identified by `(task, name, key)` within one **attempt chain** (the work item and its retries). A different key is a different step; a step that ran with a key never runs again with it in the same chain. Steps are the smallest piece: a step may not contain another (refused at run time naming both); a step inside a unit has the unit's key in its identity; names are fixed strings, keys are the variable part.

States: `started` (the runner recorded that it is about to run the body), `completed` (the result is stored), `failed` (the body threw), and `in_doubt` (a `started` step of an earlier attempt, set for a dead run by the executor at the retry). **In doubt is the only state recovery decides:**

| Recovery | In doubt means |
| --- | --- |
| `Repeatable()` | run the body again |
| `Check(f)` | call `f`: a non-null result is stored and used (status `recovered`); null runs the body |
| `Ask()` | the run stops with a failure naming the step and key; an operator marks the step `happened` (with the result) or `did not happen` and the run is retried |

**A streaming step in doubt adopts a complete payload first.** A streaming step's payload belongs to the step's row and is complete only when the body returned normally. If the payload a dead attempt left is complete, the body provably finished and only the completion was lost, so the kit adopts it as the result (status `recovered`) without running the body and without calling `Check`, whatever the recovery says. The recovery applies to a streaming step only when no complete payload exists (an interrupted write leaves an incomplete one). This is what recovers a download whose source file is deleted after the copy.

`failed` is not in doubt: a retry runs the body again, unless the exception was classified permanent (`TaskFailureException`, or a `ConnectionFailure` that the connection classifies as permanent), which fails the run.

**Results.** A value step stores `TOut` as JSON (System.Text.Json; it must round-trip, checked on first use) under `step.max_result_bytes` (64 KiB); a larger one is refused with a message that says to declare a streaming step. A streaming step hands the body a write-only `Stream` that is a payload writer (1 MiB chunks, SHA-256 as it goes, memory use one chunk); `PayloadRef` is `(Id, Size, Sha256, MediaType)` with `OpenRead()`, and can be passed to a later step, written to a connection or named in the run's outputs. Per-run and per-step size caps are settings. Results are available to any later step of the chain and kept under retention (30 days). A step's scope is the attempt chain: yesterday's download is not reused by tomorrow's run (that would make a step a cache with invalidation rules; not offered). The bytes land in the state database, which is right up to a few gigabytes; a scratch store for larger files is not offered.

The runner reports each step's start, end, duration, status (`ran`, `reused`, `recovered`, `failed`) and the counts made inside it.

## Progress

The runner hands the job its task's progress at start; a member read is memory. Setting a member marks it changed; the runner saves all changed values **with the run's outcome in one transaction**, so a failed run leaves them. `SaveProgressAsync` is an optional commit point inside a long run (after each day of a backfill); it returns after the save and throws `ProgressConflictException` if the version moved (another run got there first, or an administrator rewound it): the job should stop and the runner ends the run `Stopped`. `Keyed<T>` is the typed form of the declaration's `per: [customer]`. State values (a cursor token, a last-seen id) are `[State]` members; history and rewind apply to checkpoints.

## Units

`ForEachAsync` runs the body per item with the given parallelism, clamped by the task's `concurrency` limit and the connection's. A unit that completed in this attempt chain is skipped on a retry; whether it runs again on the next scheduled run is what per-key progress is for. By default a unit that throws is recorded `failed`, the others continue, and the run ends `CompletedWithFailures` with the first failed key; `new UnitOptions(OnFailure.Stop)` stops at the first. A unit reports status, counts and duration without being asked, and `Count` inside its body is attributed to it.

## Files, inbox, dead letters, connections

* **Files.** `Files.Claim(connection, path, size, modified, hash)` returns a handle, or `null` when a file with that content hash was already processed (the ledger): a file that arrives twice runs once.
* **Inbox.** `TakeAsync` returns the next received item (a webhook body the serving layer verified and stored as a payload) exactly once across runners, or null; `CompleteAsync` finishes it, `RejectAsync(reason)` makes a dead letter, and an item neither completed nor rejected when the run ends is released and taken again.
* **Dead letters.** `RejectAsync(record, reason)` stores the record as a payload and a dead letter. **`RefusedRecords`** (plan 27a IX14) is the surface over them, over a state database and a task (`new RefusedRecords(database, new ProgressKey(package, channel, task))`, `RunSimulator.Refused()` in a test): `List()` the open ones with their reason, source and attempts, `ReadBody(id)` / `Read<T>(id)`, `Discard(id)`, and `ReplayAsync(send)` / `ReplayAsync<T>(send)`, which sends **exactly the open records, once each, oldest first**: one that goes through is marked replayed and never offered again, one that fails stays open with its attempts counted and the failure's public text, and by default (`ReplayFailure.Stop`) the replay ends at the first failure, so the next one starts there (`Continue` goes on to the next). The record's id is the idempotency key to send it under (a crash between the send and the mark would replay it). The runner's API, its command and the console screens that call this (list, replay, discard) are plan 28a R25's.
* **Connections.** `Slots.Warehouse.OpenAsync(ct)` returns the handle the slot's type names, bound to what the channel binds that slot to. The job never sees a secret and never opens the runner's state.

## How a job ends

Exactly one of: **returned** (succeeded, or `CompletedWithFailures` when a unit failed or a record was rejected), **threw** (failed, classified by the exception: `TaskFailureException(kind)`, a `ConnectionFailure`, otherwise temporary for I/O and timeouts and permanent for everything else), **was cancelled** (let `OperationCanceledException` propagate), or **vanished** (the process ended: the runner classifies it). The mapping onto run outcomes and retries is `TaskEndMapping` in `DbDataKit.Runner.Abstractions`.

## Two backends, one logic

The kit's logic (recovery, units, typed progress, counts) sits above an internal `IRunBackend`: `InProcessRun` over the real stores (used by the simulator and in-process tests) and `GrpcRunBackend` over the protocol (used by the task host in a task's process). The same job tests pass through both. `JobRunner.RunAsync` is the in-process entry; `RemoteJobHost.RunAsync` the remote one. The protocol messages are the calls above with names and keys as strings (see `DbDataKit.Runner.Protocol`).

## Not built

An operator's screens and API for what the kit leaves for a person: marking an `Ask` step `happened` or `did not happen`, listing, replaying and discarding dead letters (the kit's own surface for that, `RefusedRecords`, exists; what is missing is the runner's route and the screens), and the table of units of a run. The stores support all of it (`StepStore.Resolve`, the dead letter states); the runner's API and the console do not call them yet (plan 28a, R25).

## For integrations with outside systems: the companion libraries (plan 27a, lane IX)

The kit stays small. What an integration needs to talk to a system that is not a database is in libraries beside it, each with its own README, none required by a job that does not use it. They share nothing but the kit's rules (a failure is classified, a secret is a reference, nothing shows a secret).

| Library | What a job gets | Row |
| --- | --- | --- |
| `DbDataKit.Runner.Kit.Formats` | rows from and to CSV and TSV, fixed width, JSON, JSON Lines, XML, Excel and Parquet; gzip, zip and OpenPGP; a bad row reported with its position and a decision (refuse, skip, collect) | IX08, IX09 |
| `DbDataKit.Runner.Kit.Http` | an HTTP client on a connection's sign-in: paging (offset, cursor, link header), a per-connection rate limit, `Retry-After`, retries of temporary failures, the connection's failure classification | IX10 |
| `DbDataKit.Runner.Kit.Landing` | rows into a staging table (write gate, one transaction per batch, an idempotency key) and records into a DbDataStore entity through the Store's write path, both landing again changing nothing | IX11 |
