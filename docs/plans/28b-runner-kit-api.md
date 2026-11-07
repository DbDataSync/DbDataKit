# Plan 28b: the runner kit API (what an ETL job writes)

Status: **draft for the owner's review** (2026-11-07). It follows the owner's two points of 2026-11-07: a job needs a **simple, scoped computation
for the pieces of state it must not redo** (a file that disappears when it is downloaded; work that is not bytes or rows), and it must be able
to **stream large results into the state database** without holding them in memory. This page defines the API a .NET job sees. The wire
protocol ([plan 28](28-runner.md) section 4.1, slice R03 of [28a](28a-runner-slices.md)) is derived from it, not the other way round.

Method: four sample jobs first (section 2), then the smallest surface that makes them work (section 3), then what each call does and when it
fails (sections 4 to 9). **Nothing is in the surface that a sample does not use.**

## 1. Principles

1. **One object, `IRun`.** A job is a method that receives it. Everything the runner provides (progress, steps, units, files, inbox, reports,
   connections) hangs off it. There is no other thing to construct, register or inject.
2. **No replay.** On a retry the job's code runs again from the top. Work that must not be repeated is wrapped in a **step**, which the runner
   remembers by name and key and answers from memory. (This is deliberately not the replay model of workflow engines: code outside a step is
   ordinary code and may do anything, including things that differ between runs.)
3. **A step is the unit of "do this at most once, and keep what it produced".** Its result is stored before the step returns. A result is a small
   value (JSON) or a **stream** (a payload written in chunks, never held in memory).
4. **The author states what a crash in the middle means.** Every step says how it recovers (`Recovery`). It is a required argument, so it cannot
   be forgotten (plan 28 section 5: make the choice explicit).
5. **Progress is typed and declared** (plan 29 section 4.3). A job reads and writes it through typed members, and saves at commit points.
6. **Reporting is two verbs.** `Status` (what I am doing now) and `Count` (how much). Units report for themselves.
7. **Cancellation is the `CancellationToken`** every method takes; nothing else is needed.
8. **It is testable without a runner.** `RunSimulator` runs a job in memory with failure injection (section 10).

## 2. Four sample jobs

### 2.1 Incremental API pull (a checkpoint)

```csharp
[RunnerTask("orders")]
public static async Task Orders(IRun run, CancellationToken ct)
{
    var since = run.Progress.Get<DateTimeOffset>("orders_modified");          // typed, from the declaration
    await using var warehouse = await run.Connections.OpenAsync("warehouse", ct);

    DateTimeOffset newest = since;
    await foreach (var page in Crm.OrdersModifiedSince(since, ct))
    {
        await warehouse.UpsertAsync("orders", page.Rows, ct);
        newest = page.Newest;
        run.Count("rows", page.Rows.Count);
    }
    run.Progress.Set("orders_modified", newest);                               // saved with the run's outcome
}
```

If the job fails, the checkpoint stays where it was (plan 29a Q20's test). Nothing else is needed: no commit call, because the outcome commits it.

### 2.2 A file that disappears when it is downloaded (steps, streaming)

```csharp
[RunnerTask("daily-drop")]
public static async Task DailyDrop(IRun run, CancellationToken ct)
{
    var day = run.Input<DateOnly>("day");

    // runs once for this key; the file is streamed into the state database; a retry gets the stored file back
    PayloadRef file = await run.StepStream($"download", key: $"{day:yyyy-MM-dd}", Recovery.Check(
            check: async ct => await Sftp.ExistsAsync(Remote(day), ct) ? null : await Archive.FindAsync(day, ct)),
        async (sink, ct) =>
        {
            await using var source = await Sftp.OpenAsync(Remote(day), ct);
            await source.CopyToAsync(sink, ct);
            await Sftp.DeleteAsync(Remote(day), ct);                           // only after the copy finished
        }, ct);

    var rows = 0L;
    await using var reader = file.OpenRead();                                  // streams back out, never loaded whole
    await foreach (var batch in Csv.Batches(reader, 10_000, ct))
    {
        await warehouse.InsertAsync("orders_raw", batch, ct);
        rows += batch.Count;
        run.Count("rows", batch.Count);
    }
    run.Status($"loaded {rows} rows from {day}");
}
```

The download step is the only thing that must not run twice. If the process dies after the delete and before the stored result is committed, the
runner calls `check`: it finds the file in the archive (or reports that nothing happened, and the step runs again).

### 2.3 Fan-out over customers (units, per-key progress)

```csharp
[RunnerTask("customers")]
public static async Task Customers(IRun run, CancellationToken ct)
{
    var customers = await Crm.CustomersAsync(ct);
    await run.ForEachAsync(customers, key: c => c.Id, parallelism: 4, async (unit, c, ct) =>
    {
        var since = unit.Progress.Get<DateTimeOffset>("customer_modified");    // per key, because the declaration says per: [customer]
        var rows  = await Crm.ChangesAsync(c.Id, since, ct);
        await warehouse.UpsertAsync("customer_changes", rows, ct);
        unit.Progress.Set("customer_modified", rows.Newest);
        unit.Count("rows", rows.Count);
    }, ct);
}
```

A retry skips the units that finished in this attempt chain and runs the rest. The console shows 400 units: 312 done, 4 running, 80 waiting, 4
failed, with the rows and the duration of each.

### 2.4 A webhook consumer (inbox, dead letters) and work that is not bytes or rows

```csharp
[RunnerTask("invoices")]
public static async Task Invoices(IRun run, CancellationToken ct)
{
    while (await run.Inbox.TakeAsync(ct) is { } item)                          // a webhook the serving layer received
    {
        var invoice = await item.ReadJsonAsync<Invoice>(ct);
        if (!invoice.IsValid(out var why)) { await item.RejectAsync(why, ct); continue; }   // a dead letter, replayable later

        // the work is neither bytes nor rows: send one letter. Once per invoice, remembered.
        var sent = await run.Step("send-letter", key: invoice.Id, Recovery.Check(async ct => await Mail.FindAsync(invoice.Id, ct)),
            async ct => await Mail.SendAsync(invoice, ct), ct);                // returns the small result: a tracking number
        run.Count("letters", 1);
        await item.CompleteAsync(ct);
    }
}
```

## 3. The surface

```csharp
public interface IRun
{
    // who and where
    Guid Id { get; }  int Attempt { get; }  string Channel { get; }  string Package { get; }  string Task { get; }
    T Input<T>(string name);                                                   // declared in the manifest, typed

    // progress (declared, typed): read at start, saved with the outcome, or at a commit point
    IProgress Progress { get; }
    Task SaveProgressAsync(CancellationToken ct);                              // an optional commit point; throws ProgressConflictException if another run got there first

    // steps: at most once per name and key; the result is kept
    Task<T> Step<T>(string name, string key, Recovery<T> recovery, Func<CancellationToken, Task<T>> body, CancellationToken ct);
    Task<PayloadRef> StepStream(string name, string key, Recovery<PayloadRef> recovery, Func<Stream, CancellationToken, Task> body, CancellationToken ct);

    // units: many pieces of one kind, each reported, retried and skipped on its own
    Task ForEachAsync<T>(IEnumerable<T> items, Func<T, string> key, int parallelism, Func<IUnit, T, CancellationToken, Task> body, CancellationToken ct, UnitOptions? options = null);

    // files and records that arrive
    IFiles Files { get; }                                                      // Claim(connection, path, size, modified, hash): null if this content was already processed
    IInbox Inbox { get; }                                                      // TakeAsync, item.ReadJsonAsync / OpenRead, CompleteAsync, RejectAsync
    Task RejectAsync(object record, string reason, CancellationToken ct);      // a dead letter from inside the job

    // connections (the suite's own handles; the runner resolves the secrets)
    IConnections Connections { get; }                                          // OpenAsync(slot, ct)

    // reporting
    void Status(string text);                                                  // what I am doing now (one line, replaces the last)
    void Count(string name, long n);                                           // how much; added up per run and per unit
    ILogger Log { get; }                                                       // Microsoft.Extensions.Logging, redacted by the runner
    void Lineage(LineageRef reads, LineageRef writes);                         // optional: outside systems as nodes (plan 18)
}

public interface IUnit { string Key { get; } IProgress Progress { get; } void Status(string text); void Count(string name, long n); }
public interface IProgress { T Get<T>(string name); void Set<T>(string name, T value); }   // names and types are those of the manifest's declaration
```

**Typed members instead of strings (a source generator, optional).** The manifest's declaration generates `OrdersProgress` with
`DateTimeOffset OrdersModified { get; set; }`, and `run.Progress<OrdersProgress>()` returns it. The string form above is what the generated one
calls, and it is checked at staging against the declaration (a name or type that the declaration does not have fails staging, not a run).

**Recovery:**

```csharp
public abstract record Recovery<T>;
public static class Recovery
{
    public static Recovery<T> Repeatable<T>();                                          // a pure or harmless step: run it again
    public static Recovery<T> Ask<T>();                                                 // in doubt, stop the run and ask an operator
    public static Recovery<T> Check<T>(Func<CancellationToken, Task<T?>> check);        // look at the outside world: the result if it happened, null if it did not
}
```

## 4. Steps in detail

**Identity.** A step is identified by `(task, name, key)` within one **attempt chain** (the work item and its retries) by default. The key is a
string the job builds, usually from an input or from an earlier step's result (`file.Hash`). A different key is a different step; a step that
ran with a key is never run again with it in the same chain.

**States.** `started` (the runner recorded that it is about to run the body), `completed` (the result is stored), `failed` (the body threw).
`started` without `completed` on a retry is the **in-doubt** case, and it is the only one `Recovery` decides:

| Recovery | In doubt means |
| --- | --- |
| `Repeatable` | run the body again |
| `Check(f)` | call `f`: a non-null result is stored and used (status `recovered`); null runs the body |
| `Ask` | the run stops with a failure naming the step and key; an operator marks it `happened` (with the result, or a payload) or `did not happen` in the console, and the run is retried |

`failed` (an exception) is not in doubt: the body ran and said it failed. A retry runs it again, unless the exception was classified `Permanent`
(`TaskFailureException`, or a `ConnectionFailure` the connection classifies as permanent), which fails the run.

**Results.** `Step<T>` stores `T` as JSON (System.Text.Json; `T` must serialise and round-trip, checked on first use) under a cap
(`step.max_result_bytes`, 64 KiB default). A larger value is refused with a message that says to use `StepStream`. A result is available to any
later step or run of the chain, and is kept under retention (30 days default).

**Streams.** `StepStream` hands the body a write-only `Stream` that is a `PayloadWriter` (plan 29 section 3.6): 1 MiB chunks, SHA-256 as it goes,
memory use one chunk. The payload is **complete only when the body returns normally**; an exception or a crash leaves an incomplete payload that
retention removes, so a retry can tell "nothing stored" from "stored". `PayloadRef` is `(Id, Size, Sha256, MediaType)` with `OpenRead()`
(a stream), `ReadAllBytesAsync()` (refused over a cap), and it can be passed to a later step, written to a connection, or named in the run's outputs.
Per-run and per-step size caps are settings; a stream over the cap fails with a message naming the cap.

**Known limit.** The bytes land in the state database. That is right for files up to a few gigabytes; for larger, the step must be able to name
another store later (a runner-local directory, an object store) without changing its shape: `StepStream(..., options: new(Store: "scratch"))`. Not in v1.

**Rules.**
- A step may not contain another step (a step is the smallest piece; compose steps in the job). Refused at run time with a message naming both.
- A step may run inside a unit; its identity then includes the unit's key.
- Step names are fixed strings (so the console can group them), keys are the variable part.
- The runner reports each step's start, end, duration, status (`ran`, `reused`, `recovered`, `failed`), and the `Count`s made inside it.

## 5. Progress in detail

- **Reading.** The runner hands the job its task's progress at start; `Get` reads from memory (no call). A name or type not in the declaration
  throws at the first `Get`/`Set` with the list of declared names (and at staging, when the generator or a static check can see it).
- **Writing.** `Set` marks the value changed. The runner saves all changed values **with the run's outcome, in one transaction** (the `Advance`
  callback of plan 29a Q20): a failed run leaves them. `SaveProgressAsync` is an optional commit point inside a long run (for example, after each
  day of a backfill), so a crash does not lose a day. It returns after the save and throws `ProgressConflictException` if the version moved
  (another run got there first, or an administrator rewound it): the job should stop, and the runner marks the run `Stopped`.
- **Per key.** `unit.Progress` is the same interface bound to the unit's key; a declaration `per: [customer]` is what makes it legal.
- **State values** (not a position in a source: a cursor token, a last-seen id) use the same `Get`/`Set`, declared as `state` instead of
  `checkpoint`. The difference is only in the declaration (history and rewind apply to checkpoints).

## 6. Units in detail

- `ForEachAsync` runs the body for each item with the given parallelism (clamped by the task's `concurrency` limit and the connection's).
- **Skipping.** A unit that completed in this attempt chain is skipped on a retry (its key is the identity). Whether it should run again on the
  *next scheduled run* is not a unit question: that is what per-key progress (`unit.Progress`) is for.
- **Failures.** By default a unit that throws is recorded `failed`, the others continue, and the run ends `CompletedWithFailures` (plan 28a QR4)
  with the first failed key. `new UnitOptions(OnFailure.Stop)` stops at the first.
- **Reporting.** Each unit reports its status, counts and duration without being asked; `unit.Status` and `unit.Count` add detail. The run view
  is the table in 2.3. Items are enumerated once; a lazy `IAsyncEnumerable<T>` overload is part of the same call.

## 7. Files, inbox, dead letters, connections

- **Files.** `Files.Claim(connection, path, size, modified, hash)` returns a handle, or `null` if a file with that content hash was already
  processed (the ledger of plan 29 section 4.2). `handle.CompleteAsync(outcome)` marks it processed. A file that arrives twice runs once.
- **Inbox.** `TakeAsync` returns the next received item (a webhook body the serving layer verified and stored as a payload) exactly once across
  runners, or null when none is waiting. `item.CompleteAsync()` finishes it; `item.RejectAsync(reason)` makes a dead letter; an item not
  completed or rejected when the run ends is released and is taken again.
- **Dead letters.** `run.RejectAsync(record, reason)` stores the record as a payload and a dead letter. They are listed, replayed and discarded
  in the console; a replay becomes an inbox item of the same task.
- **Connections.** `Connections.OpenAsync("slot")` returns the suite's connection handle (`DbDataKit.Connections`) bound to what the channel binds
  that slot to. The job never sees a secret and never opens the runner's state.

## 8. Failure and outcome

A job ends in exactly one of: **returned** (succeeded, or `CompletedWithFailures` if any unit failed or a record was rejected with
`OnFailure.Continue`), **threw** (failed; classified by the exception: `TaskFailureException(kind)`, a `ConnectionFailure`, or `Permanent` /
`Temporary` by type, otherwise `Temporary` for I/O and timeouts and `Permanent` for everything else), **was cancelled** (the token; the job
should let `OperationCanceledException` propagate), or **vanished** (the process ended: classified by the runner, plan 28a R02). The
mapping to run outcomes and retries is R02's.

## 9. From the same API in other languages

The protocol messages are the calls above; a non-.NET job speaks them through its own thin client.

| .NET call | Protocol message (R03) |
| --- | --- |
| start | `Start` (inputs, progress snapshot, run id, attempt) |
| `Progress.Set`, `SaveProgressAsync`, outcome | `Advance` with the changed values and the expected version |
| `Step` | `BeginStep(name, key)` returns `reuse(result)`, `run`, or `in_doubt`; then `CompleteStep(result)` |
| `StepStream` | `BeginStep` then a client stream of `StepChunk` then `CompleteStep(payload)` |
| `ForEachAsync` | `BeginUnit(key)` returns `run` or `skip`; `UnitProgress`; `EndUnit(outcome)` |
| `Files.Claim`, `Inbox.TakeAsync`, `RejectAsync` | `ClaimFile`, `TakeInbox`, `Reject` |
| `Status`, `Count`, `Log`, `Lineage` | `Status`, `Count`, `Log`, `Lineage` (one-way, batched) |
| cancellation | the runner's `Cancel` with the grace period |

## 10. Testing a job

`DbDataKit.Runner.Kit.Testing` ships `RunSimulator`: an in-memory `IRun` over the real stores on SQLite (so the semantics are the real ones).

```csharp
var sim = new RunSimulator(inputs: new { day = new DateOnly(2026, 11, 7) });
sim.FailAfter(step: "download", times: 1, when: FailWhen.AfterBodyBeforeCommit);   // the in-doubt case
await sim.RunAsync(Jobs.DailyDrop);                                                 // first attempt fails
await sim.RunAsync(Jobs.DailyDrop);                                                 // retry: the recovery check runs
Assert.Equal(1, sim.Executions("download"));                                        // the body ran once
Assert.Equal(CompletedOk, sim.Outcome);
```

It can inject: a failure before or after a step body, a crash between the body and the commit, a cancellation at a point, a conflict on
`SaveProgressAsync`, and a unit failure; it reports step statuses, counts and the stored progress after each attempt.

## 11. Decisions and open questions

| # | Decision (default) | Question |
| --- | --- | --- |
| K1 | **No replay**: a retry re-runs the code, steps answer from memory | confirm; the alternative (deterministic replay) is much harder for authors and for non-.NET jobs |
| K2 | `Recovery` is a required argument | confirm: or should `Ask` be the silent default? Required is safer and costs one word |
| K3 | A step's scope is the attempt chain; a longer-lived reuse (yesterday's download used by tomorrow's run) is **not** in v1 | do you want a `Scope.Task` with a lifetime? It makes a step a cache, with invalidation questions |
| K4 | `Step<T>` results are JSON under 64 KiB; `StepStream` for anything else | is 64 KiB right? |
| K5 | Streaming results land in the state database, with per-run and per-step size caps | the first large-file case decides whether a "scratch store" is needed in v1 |
| K6 | No nested steps | confirm |
| K7 | Units skip within an attempt chain only; across runs use per-key progress | confirm |
| K8 | Typed progress members come from a **source generator** over the manifest declaration, with the string form as the base | is a generator acceptable, or should it be only strings plus a staging check? |
| K9 | `IRun` is an interface so jobs can be tested without the simulator too | none |

## 12. What this changes in plan 28a

- **R03 splits:** **R03a** the kit's `IRun` surface over an **in-process implementation** on the real stores (`RunSimulator` is this with injection
  hooks), so the API is proven on all three engines before any transport exists; **R03b** the `.proto` and the gRPC server and client, derived
  from section 9, with the same job tests run through the protocol.
- **R04 and the progress store** gain what steps and units need: a **step table** (`step_result`: key, state `started|completed|failed`, result
  JSON or payload id, timing, counts) and a **unit table** (`unit_result`), both under the `runner_progress` module with retention; `PayloadStore`
  gains a size cap on a writer.
- **The manifest** (R01) gains an optional `steps` and `units` declaration only if a staging check of step names is wanted; otherwise nothing.
- **R06 (executor)** reconciles in-doubt steps at run start: it marks any `started` step of a dead run as in doubt before the retry begins.

Slices to add after the review: **R03a** (surface and in-process run, steps, units), **R03c** (`RunSimulator` and the testing package). Not
added to 28a yet, so that the review can change them.
