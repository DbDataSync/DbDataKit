# DbDataKit.Runner.Kit.Outbound

Sending suite data **out** (plan 27a IX22): a table or view, a DbDataStore entity or any query, to an API or a file, so that

* **a push that fails halfway resumes without repeating the sent part,**
* **a second run with no change sends nothing,** and
* **every request carries an idempotency key** the other end can recognise a repeat by.

```csharp
public sealed class SentProgress : Progress
{
    [Checkpoint] public RowSnapshot? Sent { get; set; }                 // what went out: key -> hash of the content (DbDataKit.Runner.Kit.Changes)
}

// in RunAsync:
Progress.Sent ??= new RowSnapshot();
var push = new OutboundPush(new SnapshotLedger(Progress.Sent), new PushOptions { BatchSize = 100, Commit = SaveProgressAsync, KeyPrefix = "orders-" });
var report = await push.PushAsync(
    OutboundSources.FromReaderAsync(reader, ["order_id"], ct),          // a table, a view or a query of the target database; FromStoreAsync reads a DbDataStore entity
    (batch, ct) => http.SendJsonAsync(HttpMethod.Post, "orders/batch", batch.Records.Select(r => r.Values), batch.IdempotencyKey, ct),
    delete: (d, ct) => http.SendJsonAsync(HttpMethod.Post, "orders/delete", d.Keys, d.IdempotencyKey, ct),   // optional: only when asked for
    ct);
```

## How it works

1. **What to send.** Each record is an `OutboundRecord` (a key that identifies it at the other end, its values, and the hash of its content, `RowHash` of the values by name). The ledger (`ISentLedger`) holds the hash that was sent for each key. A record whose key and hash are in it is skipped; a new key or another hash is sent. A source that is unchanged sends nothing, whatever the order of its rows.
2. **Batches, each committed.** Records go out in batches of `BatchSize`. After every batch is sent the ledger is written and `PushOptions.Commit` is called: pass the job's `SaveProgressAsync`, so the ledger (a checkpoint member of the progress) is saved at that point, and a run that fails later keeps what it sent. The next run starts at the first batch that was not sent. Without `Commit` the ledger is saved with the run's outcome, and a run that fails halfway sends the sent part again.
3. **Idempotency keys.** A batch is sent under `IdempotencyKeys.ForBatch(prefix, records)`: a hash of the records' keys and content hashes, so **the same batch has the same key whenever it is made**. A crash between a send and its save repeats that one batch under the key it already had, which the other end recognises (`OutsideHttp.SendJsonAsync(..., idempotencyKey:)` sends it as `Idempotency-Key` and repeats a POST only with one). Keys are derived from the content, never random: a changed record is a new key. `KeyPrefix` keeps two targets of one task apart.
4. **Deletions** are sent only when a `delete` callback is given, only after the whole source was read, and never for a source that gave no records while the ledger held some (`PushException`, `AllowEmptySource` accepts it): a key in the ledger the source no longer gives is deleted at the other end and forgotten.
5. **Files.** `PushFileAsync(name, write, send)` renders a file into a temporary file, hashes it, and calls `send(stream, idempotencyKey)` only when the ledger does not hold this name with this content; a file that did not change is not sent again, one that did goes under a new key. Writing the bytes (CSV, Parquet, ...) is `DbDataKit.Runner.Kit.Formats`.

## Ledgers

* `SnapshotLedger(RowSnapshot)`: the job's own checkpoint member. It works the same in a runner (through the protocol), in the simulator, and survives restarts; it is rewound with the progress; it can list keys, so it can send deletions. Size: every key and 22 characters a record (see `DbDataKit.Runner.Kit.Changes`).
* `ProgressStoreLedger(ProgressStore)`: the runner's idempotency keys (`TryBeginIdempotent`, per task, expiring after 30 days). For code that holds the state database itself. **A job in a task process cannot use it:** the kit's protocol does not carry those keys, and adding them is a protocol change this row does not make. It cannot list keys, so it cannot find deletions.

## Sources

`OutboundSources.FromReaderAsync(DbDataReader, keyColumns)` makes records from the rows of any query through a connection: a table, a view, or a table or view a Build model owns (it is a table of the target database). `FromStoreAsync(ModelWriter, model, entity)` reads a DbDataStore entity page by page, keyed by its key attributes (the version and period columns are not part of the content). A query of a semantic model goes through the Gateway (Flight SQL) and has no source here.

Tests: `DbDataKit.Runner.Kit.Outbound.Tests` (the push ones under `RunSimulator`: a failure halfway and the resume, a rerun and a change, the crash between send and save, deletions and the empty source, a file once per content, the runner's idempotency keys as a ledger, rows of a query, and a DbDataStore entity on PostgreSQL and SQL Server).
