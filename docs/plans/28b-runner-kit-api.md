# Plan 28b: the runner kit API (what an ETL job writes)

Status: **accepted by the owner with its defaults (2026-11-07); being built as slices R03s, R03a, R03b and R03c of [28a](28a-runner-slices.md).** It follows the owner's two points of 2026-11-07: a job needs a **simple, scoped computation
for the pieces of state it must not redo** (a file that disappears when it is downloaded; work that is not bytes or rows), and it must be able
to **stream large results into the state database** without holding them in memory. This page defines the API a .NET job sees. The wire
protocol ([plan 28](28-runner.md) section 4.1, slice R03 of [28a](28a-runner-slices.md)) is derived from it, not the other way round.

Method: four sample jobs first (section 2), then the smallest surface that makes them work (section 3), then what each call does and when it
fails (sections 4 to 9). **Nothing is in the surface that a sample does not use.**

## 1. Principles

1. **One base class, `RunnerJob<TInput, TProgress, TSlots>`.** A job is a class that overrides one method. Everything the runner provides (progress,
   steps, units, files, inbox, counters, connections) is a member of it. There is nothing to construct, register or inject.
   **Everything the job names is typed:** its input, progress, connection slots, steps, units and counters, so a wrong name or result type does not compile.
2. **No replay.** On a retry the job's code runs again from the top. Work that must not be repeated is wrapped in a **step**, which the runner
   remembers by name and key and answers from memory. (This is deliberately not the replay model of workflow engines: code outside a step is
   ordinary code and may do anything, including things that differ between runs.)
3. **A step is the unit of "do this at most once, and keep what it produced".** Its result is stored before the step returns. A result is a small
   value (JSON) or a **stream** (a payload written in chunks, never held in memory).
4. **The author states what a crash in the middle means.** Every step says how it recovers (`Recovery`). It is a required argument, so it cannot
   be forgotten (plan 28 section 5: make the choice explicit).
5. **Progress is a typed class** (plan 29 section 4.3's declaration, generated from it for a .NET job). A job reads and writes its members and saves at commit points.
6. **Reporting is two verbs.** `Status` (what I am doing now) and `Count` (how much). Units report for themselves.
7. **Cancellation is the `CancellationToken`** every method takes; nothing else is needed.
8. **It is testable without a runner.** `RunSimulator` runs a job in memory with failure injection (section 10).

## 2. The shape of a job (typed)

A .NET job is a class. Its **input, progress and connection slots are types**; its **steps, units and counters are named, typed values**, declared
once next to the job. Nothing is addressed by a string at a call site, so a misspelled step, a wrong result type or a progress value that does
not exist is a compile error, and the console, the staging check and the simulator can list every step and counter without running anything.

```csharp
public abstract class RunnerJob<TInput, TProgress, TSlots> where TProgress : Progress, new() where TSlots : Slots, new()
{
    protected IRun Run { get; }          // run id, attempt, channel, Log, Status, Lineage: the few things not typed by the job
    protected TInput Input { get; }      // a record: what the manifest's `inputs` declares
    protected TProgress Progress { get; }// typed checkpoints and state, read at start, saved with the outcome
    protected TSlots Slots { get; }      // typed connections: Slots.Warehouse is already the right kind of handle
    protected abstract Task RunAsync(CancellationToken ct);
}
```

**The types are the source of truth for a .NET job, and the manifest's task entry is generated from them at publish** (decision K8). A job in another
language writes the manifest by hand (plan 28b section 9). The runner checks a package's manifest against its own declaration at staging either way.

### 2.1 Incremental API pull (a typed checkpoint)

```csharp
public sealed record OrdersInput;                                              // no inputs
public sealed class OrdersProgress : Progress { [Checkpoint] public DateTimeOffset OrdersModified { get; set; } }
public sealed class OrdersSlots : Slots { public Slot<IWarehouse> Warehouse { get; } = new("warehouse"); }
public static class OrdersCounters { public static readonly Counter Rows = new("rows"); }

[RunnerTask("orders")]
public sealed class Orders : RunnerJob<OrdersInput, OrdersProgress, OrdersSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        await using var warehouse = await Slots.Warehouse.OpenAsync(ct);       // IWarehouse, typed by the slot
        var newest = Progress.OrdersModified;                                  // a DateTimeOffset, not a string lookup
        await foreach (var page in Crm.OrdersModifiedSince(newest, ct))
        {
            await warehouse.UpsertAsync("orders", page.Rows, ct);
            newest = page.Newest;
            Count(OrdersCounters.Rows, page.Rows.Count);
        }
        Progress.OrdersModified = newest;                                      // saved with the run's outcome
    }
}
```

### 2.2 A file that disappears when it is downloaded (typed steps, streaming)

Steps are declared as static values: a **name**, the **input** the key is made from, and the **result type**. The body and the recovery are supplied where
the step is used, because they need the job's services.

```csharp
public static class DailyDropSteps
{
    // a streaming step: its result is a PayloadRef; the key is made from the day
    public static readonly StreamStep<DateOnly> Download = Step.Stream<DateOnly>("download", day => $"{day:yyyy-MM-dd}");
    // a value step: its result is a small typed record, kept as JSON
    public static readonly ValueStep<DateOnly, ArchiveReceipt> Archive = Step.Value<DateOnly, ArchiveReceipt>("archive", day => $"{day:yyyy-MM-dd}");
}
public sealed record ArchiveReceipt(string Url, long Bytes);

public sealed record DailyDropInput(DateOnly Day);

[RunnerTask("daily-drop")]
public sealed class DailyDrop : RunnerJob<DailyDropInput, NoProgress, DailyDropSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        PayloadRef file = await Step(DailyDropSteps.Download, Input.Day)       // StreamStep: only .StreamAsync is offered
            .Repeatable()                                                      // recovery: required. A crash mid-copy leaves the file on the server (it is deleted after the copy); a crash after
                                                                               // the copy leaves a complete payload, which the kit adopts without asking (K14)
            .StreamAsync(async (sink, ct) =>
            {
                await using var source = await Sftp.OpenAsync(Remote(Input.Day), ct);
                await source.CopyToAsync(sink, ct);
                await Sftp.DeleteAsync(Remote(Input.Day), ct);                 // only after the copy finished
            }, ct);

        await using var warehouse = await Slots.Warehouse.OpenAsync(ct);
        await using var reader = file.OpenRead();                              // streams back out, never loaded whole
        await foreach (var batch in Csv.Batches(reader, 10_000, ct))
        {
            await warehouse.InsertAsync("orders_raw", batch, ct);
            Count(DailyDropCounters.Rows, batch.Count);
        }

        ArchiveReceipt receipt = await Step(DailyDropSteps.Archive, Input.Day) // ValueStep<DateOnly, ArchiveReceipt>: RunAsync returns ArchiveReceipt
            .Repeatable()                                                      // copying to the archive is harmless to repeat
            .RunAsync(async ct => await Archive.PutAsync(file, ct), ct);       // a lambda returning anything else does not compile
    }
}
```

**Type-state recovery.** `Step(def, input)` returns a call that has only `.Repeatable()`, `.Ask()` and `.Check(...)`. Each returns the runner that has
`RunAsync` (value steps) or `StreamAsync` (streaming steps). A step with no recovery chosen has nothing to run, so "forgot to say what a crash means"
does not compile.

### 2.3 Fan-out over customers (typed units, typed per-key progress)

```csharp
public sealed class CustomersProgress : Progress
{
    public Keyed<CustomerProgress> Customer { get; } = new();                  // one CustomerProgress per customer id
}
public sealed class CustomerProgress { [Checkpoint] public DateTimeOffset Modified { get; set; } }

public static class CustomerUnits { public static readonly Unit<Customer> Sync = Unit.Of<Customer>("customer", c => c.Id); }

[RunnerTask("customers")]
public sealed class Customers : RunnerJob<NoInput, CustomersProgress, OrdersSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        var customers = await Crm.CustomersAsync(ct);
        await ForEachAsync(CustomerUnits.Sync, customers, parallelism: 4, async (c, ct) =>
        {
            var mine = Progress.Customer[c.Id];                                // typed, per key, no dictionary of strings
            var rows = await Crm.ChangesAsync(c.Id, mine.Modified, ct);
            await warehouse.UpsertAsync("customer_changes", rows, ct);
            mine.Modified = rows.Newest;
            Count(CustomerCounters.Rows, rows.Count);                          // counted against this unit and the run
        }, ct);
    }
}
```

A retry skips the units that finished in this attempt chain and runs the rest. The console shows the table of units (done, running, waiting, failed, with
rows and duration each).

### 2.4 A webhook consumer (typed inbox, dead letters) and work that is not bytes or rows

```csharp
public static class InvoiceSteps
{
    public static readonly ValueStep<Invoice, LetterReceipt> SendLetter = Step.Value<Invoice, LetterReceipt>("send-letter", i => i.Id);
}
public sealed record LetterReceipt(string TrackingNumber, DateTimeOffset SentAt);

[RunnerTask("invoices")]
public sealed class Invoices : RunnerJob<NoInput, NoProgress, NoSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        while (await Inbox<Invoice>().TakeAsync(ct) is { } item)               // InboxItem<Invoice>: the body is parsed as an Invoice
        {
            if (!item.Value.IsValid(out var why)) { await item.RejectAsync(why, ct); continue; }   // a dead letter, replayable later

            LetterReceipt receipt = await Step(InvoiceSteps.SendLetter, item.Value)
                .Check(ct => Mail.FindAsync(item.Value.Id, ct))                // Task<LetterReceipt?>: checked against the step's result type
                .RunAsync(ct => Mail.SendAsync(item.Value, ct), ct);
            Count(InvoiceCounters.Letters, 1);
            await item.CompleteAsync(ct);
        }
    }
}
```

## 3. The surface

```csharp
// --- declared once, as static values: names, keys and result types -------------------------------------------
public static class Step
{
    public static ValueStep<TIn, TOut>  Value<TIn, TOut>(string name, Func<TIn, string> key);   // TOut: a record that round-trips through System.Text.Json
    public static StreamStep<TIn>       Stream<TIn>(string name, Func<TIn, string> key);        // the result is a PayloadRef
}
public static class Unit { public static Unit<T> Of<T>(string name, Func<T, string> key); }
public sealed record Counter(string Name);
public abstract class Progress { }                                              // members marked [Checkpoint] or [State]; Keyed<T> for per-key sets
public sealed class Keyed<T> where T : new() { public T this[string key] { get; } }
public sealed class Slot<TConnection> { public Slot(string name); public Task<TConnection> OpenAsync(CancellationToken ct); }

// --- used in a job (members of RunnerJob) ---------------------------------------------------------------------
StepCall<TIn, TOut>        Step<TIn, TOut>(ValueStep<TIn, TOut> step, TIn input);
StreamStepCall<TIn>        Step<TIn>(StreamStep<TIn> step, TIn input);
//   StepCall:        .Repeatable() | .Ask() | .Check(Func<CancellationToken, Task<TOut?>>)   each returns ValueRunner<TOut>   { Task<TOut> RunAsync(Func<CancellationToken, Task<TOut>> body, CancellationToken ct) }
//   StreamStepCall:  .Repeatable() | .Ask() | .Check(Func<CancellationToken, Task<PayloadRef?>>)   each returns StreamRunner   { Task<PayloadRef> StreamAsync(Func<Stream, CancellationToken, Task> body, CancellationToken ct) }
Task ForEachAsync<T>(Unit<T> unit, IEnumerable<T> items, int parallelism, Func<T, CancellationToken, Task> body, CancellationToken ct, UnitOptions? options = null);
Inbox<T>  Inbox<T>();                           // TakeAsync -> InboxItem<T>? { T Value; Task CompleteAsync; Task RejectAsync(string reason) }
Files     Files { get; }                        // Claim(connection, path, size, modified, hash) -> FileClaim? (null: this content was already processed)
Task      RejectAsync<T>(T record, string reason, CancellationToken ct);
void      Count(Counter counter, long n);       // typed: Counters are declared, so the console knows them
void      Status(string text);                  // what I am doing now (one line, replaces the last)
Task      SaveProgressAsync(CancellationToken ct);   // an optional commit point; throws ProgressConflictException if another run got there first
IRun      Run { get; }                          // Id, Attempt, Channel, Package, Task, Log (ILogger, redacted), Lineage(reads, writes)
```

**What is typed, and from what.**

| Thing | Typed by | Checked |
| --- | --- | --- |
| input | a record (`TInput`); its properties are the manifest's `inputs` | compile time; the manifest is generated |
| progress | a `Progress` class; `[Checkpoint]`/`[State]` members, `Keyed<T>` for per-key | compile time; the declaration is generated, and a change between versions is planned by the progress migration |
| connections | `Slot<TConnection>` members of a `Slots` class | compile time; the manifest's slots are generated, and binding a slot to a connection of another kind fails at staging |
| steps | `ValueStep<TIn,TOut>` / `StreamStep<TIn>` static values: name, key, result type | compile time (result type, which method is offered); names are unique per job (checked at first use and by the analyzer) |
| units, counters | `Unit<T>`, `Counter` static values | compile time; listed in the manifest so the console shows them before a run |
| recovery | the call's type state (`.Repeatable()`, `.Ask()`, `.Check(...)` before `RunAsync`) | compile time: no recovery, no run |
| inbox items | `Inbox<T>()` | the body is parsed as `T` at take; a body that does not parse is a dead letter, not a crash |

**A small analyzer** (shipped with the kit) adds what the type system cannot: two steps with the same name in one job, a step used inside another step,
a `Check` that does not return the step's result type (the compiler already catches it), `Progress` members set to a type the declaration cannot store.

## 4. Steps in detail

**Identity.** A step is identified by `(task, name, key)` (the name is the declared step's, the key is made by its key function) within one **attempt chain** (the work item and its retries) by default. The key is a
string the job builds, usually from an input or from an earlier step's result (`file.Hash`). A different key is a different step; a step that
ran with a key is never run again with it in the same chain.

**States.** `started` (the runner recorded that it is about to run the body), `completed` (the result is stored), `failed` (the body threw).
`started` without `completed` on a retry is the **in-doubt** case, and it is the only one `Recovery` decides:

| Recovery | In doubt means |
| --- | --- |
| `Repeatable` | run the body again |
| `Check(f)` | call `f`: a non-null result is stored and used (status `recovered`); null runs the body |
| `Ask` | the run stops with a failure naming the step and key; an operator marks it `happened` (with the result, or a payload) or `did not happen` in the console, and the run is retried |

**A stream step in doubt adopts a complete payload first (K14).** The payload of a streaming step is owned by the step's row and is complete only when the body returned normally. So when a
streaming step is in doubt on retry and the payload its dead attempt left is **complete**, the body provably finished writing and only the step's completion was lost: the kit **adopts** that payload as the step's
result (status `recovered`), **without running the body and without calling `Check`**, whatever the step's recovery says (also for `Ask`). The step row records that the result was adopted (its note names the payload and the attempt
that wrote it). The recovery in the table above applies to a stream step only when **no complete payload** exists (a write that was interrupted leaves an incomplete one). This is what lets a download whose source file is gone
after the copy be recovered: `Check` can only return a `PayloadRef` that exists outside the step, the adopted payload is inside it.

`failed` (an exception) is not in doubt: the body ran and said it failed. A retry runs it again, unless the exception was classified `Permanent`
(`TaskFailureException`, or a `ConnectionFailure` the connection classifies as permanent), which fails the run.

**Results.** A `ValueStep<TIn,TOut>` stores `TOut` as JSON (System.Text.Json; `T` must serialise and round-trip, checked on first use) under a cap
(`step.max_result_bytes`, 64 KiB default). A larger value is refused with a message that says to declare a `StreamStep` instead. A result is available to any
later step or run of the chain, and is kept under retention (30 days default).

**Streams.** A `StreamStep` hands the body a write-only `Stream` that is a `PayloadWriter` (plan 29 section 3.6): 1 MiB chunks, SHA-256 as it goes,
memory use one chunk. The payload is **complete only when the body returns normally**; an exception or a crash leaves an incomplete payload that
retention removes, so a retry can tell "nothing stored" from "stored". `PayloadRef` is `(Id, Size, Sha256, MediaType)` with `OpenRead()`
(a stream), `ReadAllBytesAsync()` (refused over a cap), and it can be passed to a later step, written to a connection, or named in the run's outputs.
Per-run and per-step size caps are settings; a stream over the cap fails with a message naming the cap.

**Known limit.** The bytes land in the state database. That is right for files up to a few gigabytes; for larger, the step must be able to name
another store later (a runner-local directory, an object store) without changing its shape: `Step.Stream<TIn>(name, key, new(Store: "scratch"))`. Not in v1.

**Rules.**
- A step may not contain another step (a step is the smallest piece; compose steps in the job). Refused at run time with a message naming both.
- A step may run inside a unit; its identity then includes the unit's key.
- Step names are fixed strings (so the console can group them), keys are the variable part.
- The runner reports each step's start, end, duration, status (`ran`, `reused`, `recovered`, `failed`), and the `Count`s made inside it.

## 5. Progress in detail

- **Reading.** The runner hands the job its task's progress at start and fills the `Progress` object; a member read is memory (no call). The class
  is the declaration: a member the job does not have cannot be written, and the manifest's declaration is generated from the class.
- **Writing.** Setting a member marks it changed. The runner saves all changed values **with the run's outcome, in one transaction** (the `Advance`
  callback of plan 29a Q20): a failed run leaves them. `SaveProgressAsync` is an optional commit point inside a long run (for example, after each
  day of a backfill), so a crash does not lose a day. It returns after the save and throws `ProgressConflictException` if the version moved
  (another run got there first, or an administrator rewound it): the job should stop, and the runner marks the run `Stopped`.
- **Per key.** A `Keyed<T>` member (`Progress.Customer[c.Id].Modified`) is the typed form of the declaration's `per: [customer]`; the key parts come from the
  member's name and the key given.
- **State values** (not a position in a source: a cursor token, a last-seen id) are members marked `[State]` instead of `[Checkpoint]`. The difference is only in the declaration (history and rewind apply to checkpoints).

## 6. Units in detail

- `ForEachAsync` runs the body for each item with the given parallelism (clamped by the task's `concurrency` limit and the connection's).
- **Skipping.** A unit that completed in this attempt chain is skipped on a retry (its key is the identity). Whether it should run again on the
  *next scheduled run* is not a unit question: that is what per-key progress (`Keyed<T>`) is for.
- **Failures.** By default a unit that throws is recorded `failed`, the others continue, and the run ends `CompletedWithFailures` (plan 28a QR4)
  with the first failed key. `new UnitOptions(OnFailure.Stop)` stops at the first.
- **Reporting.** Each unit reports its status, counts and duration without being asked; `Count` inside a unit body is attributed to that unit. The run view
  is the table in 2.3. Items are enumerated once; a lazy `IAsyncEnumerable<T>` overload is part of the same call.

## 7. Files, inbox, dead letters, connections

- **Files.** `Files.Claim(connection, path, size, modified, hash)` returns a handle, or `null` if a file with that content hash was already
  processed (the ledger of plan 29 section 4.2). `handle.CompleteAsync(outcome)` marks it processed. A file that arrives twice runs once.
- **Inbox.** `TakeAsync` returns the next received item (a webhook body the serving layer verified and stored as a payload) exactly once across
  runners, or null when none is waiting. `item.CompleteAsync()` finishes it; `item.RejectAsync(reason)` makes a dead letter; an item not
  completed or rejected when the run ends is released and is taken again.
- **Dead letters.** `run.RejectAsync(record, reason)` stores the record as a payload and a dead letter. They are listed, replayed and discarded
  in the console; a replay becomes an inbox item of the same task.
- **Connections.** `Slots.Warehouse.OpenAsync(ct)` returns the connection handle the slot's type names (the suite's `DbDataKit.Connections` handles, or
  an integration kind's), bound to what the channel binds that slot to. The job never sees a secret and never opens the runner's state.

## 8. Failure and outcome

A job ends in exactly one of: **returned** (succeeded, or `CompletedWithFailures` if any unit failed or a record was rejected with
`OnFailure.Continue`), **threw** (failed; classified by the exception: `TaskFailureException(kind)`, a `ConnectionFailure`, or `Permanent` /
`Temporary` by type, otherwise `Temporary` for I/O and timeouts and `Permanent` for everything else), **was cancelled** (the token; the job
should let `OperationCanceledException` propagate), or **vanished** (the process ended: classified by the runner, plan 28a R02). The
mapping to run outcomes and retries is R02's.

## 9. From the same API in other languages

The protocol messages are the calls above, with **names and keys as strings on the wire**: the typed definitions are the .NET kit's convenience, and
a non-.NET job speaks the same messages through its own thin client (and writes the manifest by hand, since there is no class to generate it from).

| .NET call | Protocol message (R03) |
| --- | --- |
| start | `Start` (inputs, progress snapshot, run id, attempt) |
| setting a `Progress` member, `SaveProgressAsync`, outcome | `Advance` with the changed values and the expected version |
| `Step(def, input).….RunAsync` | `BeginStep(name, key)` returns `reuse(result)`, `run`, or `in_doubt`; then `CompleteStep(result)` |
| `Step(def, input).….StreamAsync` | `BeginStep` then a client stream of `StepChunk` then `CompleteStep(payload)` |
| `ForEachAsync(unit, …)` | `BeginUnit(unit name, key)` returns `run` or `skip`; `UnitProgress`; `EndUnit(outcome)` |
| `Files.Claim`, `Inbox.TakeAsync`, `RejectAsync` | `ClaimFile`, `TakeInbox`, `Reject` |
| `Status`, `Count`, `Log`, `Lineage` | `Status`, `Count`, `Log`, `Lineage` (one-way, batched) |
| cancellation | the runner's `Cancel` with the grace period |

## 10. Testing a job

`DbDataKit.Runner.Kit.Testing` ships `RunSimulator<TJob>`: runs a job in memory over the real stores on SQLite (so the semantics are the real ones).

```csharp
var sim = new RunSimulator<DailyDrop>(input: new DailyDropInput(new DateOnly(2026, 11, 7)));
sim.FailAfterBody(DailyDropSteps.Download, times: 1);                       // the in-doubt case: typed, so a renamed step breaks the test at compile time
await sim.RunAsync();                                                        // first attempt fails
await sim.RunAsync();                                                        // retry: the recovery check runs
Assert.Equal(1, sim.Executions(DailyDropSteps.Download));                    // the body ran once
Assert.Equal(StepStatus.Recovered, sim.StatusOf(DailyDropSteps.Download));
Assert.Equal(RunOutcome.Succeeded, sim.Outcome);
Assert.Equal(1_240, sim.Counted(DailyDropCounters.Rows));
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
| K8 | **For a .NET job the C# types are the source of truth** (input record, `Progress` class, `Slots` class, step/unit/counter values); the manifest's task entry is **generated from them at publish**. Other languages write the manifest by hand | is generating the manifest from code acceptable? It reverses plan 28's "manifest declares" for .NET only, and the staging check still compares the two |
| K9 | The base class `RunnerJob<TInput, TProgress, TSlots>` (with `NoInput`, `NoProgress`, `NoSlots`); a job needing none writes `RunnerJob<NoInput, NoProgress, NoSlots>` | is a base class right, or would you prefer an interface plus a source-generated partial? |
| K10 | **Steps are static typed values** (`ValueStep<TIn,TOut>`, `StreamStep<TIn>`) declared beside the job; body and recovery are supplied at the call | alternative: a step is a class with the body inside it (testable alone, but needs service injection) |
| K11 | **Recovery by type state:** `.Repeatable()`, `.Ask()` or `.Check(...)` must be called before `RunAsync`/`StreamAsync` exists | confirm: the cost is a slightly longer call chain; the gain is that "forgot" cannot compile |
| K12 | `Count` takes a declared `Counter`, not a string; counters, units and steps are listed in the generated manifest so the console shows them before a run | confirm |
| K13 | A small **analyzer** ships with the kit (duplicate step names in a job, a step inside a step) | none |
| K14 | **A streaming step in doubt adopts its complete payload** (found by the step row's ownership), status `recovered`, with no body and no `Check`; `Repeatable`/`Ask`/`Check` apply only when no complete payload exists. Found by R03a: a `Check` can only name an outside `PayloadRef`, so a download whose source is gone could not be recovered | none: a complete payload is proof the body returned |

## 12. What this changes in plan 28a

- **R03 splits:** **R03a** the kit's typed surface (`RunnerJob`, the step/unit/counter definitions, the type-state calls, the analyzer and the manifest generator for .NET) over an **in-process implementation** on the real stores (`RunSimulator` is this with injection
  hooks), so the API is proven on all three engines before any transport exists; **R03b** the `.proto` and the gRPC server and client, derived
  from section 9, with the same job tests run through the protocol.
- **R04 and the progress store** gain what steps and units need: a **step table** (`step_result`: key, state `started|completed|failed`, result
  JSON or payload id, timing, counts) and a **unit table** (`unit_result`), both under the `runner_progress` module with retention; `PayloadStore`
  gains a size cap on a writer.
- **The manifest** (R01) gains `steps`, `units` and `counters` lists (names, kinds, result types as a type name) so the console and the staging check can show them; for a .NET job they are generated (K8).
- **R06 (executor)** reconciles in-doubt steps at run start: it marks any `started` step of a dead run as in doubt before the retry begins.

Slices to add after the review: **R03a** (surface and in-process run, steps, units), **R03c** (`RunSimulator<TJob>` and the testing package). Not
added to 28a yet, so that the review can change them.
