# DbDataKit.Runner.Kit.Changes

Change detection for a source with no change feed (plan 27a IX13): a file or an API that gives the whole picture every time and says nothing about what changed. The run hashes every row, compares the hashes with what the **last run** saw, and finds the rows that are new, changed or gone. It is the idea of Sync's `RowHash`, not a copy of its engine: no database, no comparison of two live sources, only "this run against the snapshot the last run kept".

```csharp
public sealed class ExportProgress : Progress
{
    [Checkpoint] public RowSnapshot? Seen { get; set; }          // saved with the run's outcome, rewound with the progress
}

// in RunAsync:
var detector = new ChangeDetector(Progress.Seen, new ChangeOptions { KeyColumns = ["id"], IgnoreColumns = ["exported_at"] });
await foreach (var change in detector.ChangesAsync(DelimitedReader.ReadAsync(stream), ct))
{
    // change.Kind is Added or Changed; change.Row is the row. Unchanged rows are not offered.
}
var changes = detector.Complete();                               // the whole source was read
foreach (var key in changes.DeletedKeys) { /* ... */ }
Progress.Seen = changes.Next;                                    // the last thing the run does
```

## Rules

* **The snapshot is a checkpoint.** `RowSnapshot` is a plain JSON class (algorithm name and `key -> hash`); a `[Checkpoint]` member of the progress class stores it as JSON. The runner saves progress with the run's outcome in one transaction, so a run that fails keeps the old snapshot and the next run compares with it again; rewinding the checkpoint (`checkpoints/{name}/rewind`) rewinds the comparison. Nothing is kept outside the progress.
* **`Complete()` only after the whole source was read.** A key not seen counts as deleted. A read that stops early (an exception, a missing page) must not reach `Complete`; because the run then fails, the snapshot member is never assigned.
* **Two refusals by default,** both a `ChangeException` (permanent): a source that gives **no rows** when the last snapshot held some (`AllowEmptySource` accepts it), and, if set, a comparison that would delete more than `MaxDeletedFraction` of the last snapshot. A key twice in one run, a row without its key and a snapshot of another hash algorithm are refused too; a message never holds a key or a value.
* **The hash** (`RowHash`, `sha256-128/1`): SHA-256 of a canonical text of the values, cut to 128 bits, 22 characters of base64. Missing and empty differ, a number and the same digits as text differ, `1.50` and `1.5` are one decimal value, times are UTC. `IgnoreColumns` leaves out a column the source changes on every export; the key columns are never in the hash. The text formats (CSV, TSV, fixed width, XML) give strings and a string is compared as written (`10.50` and `10.5` differ); Excel, Parquet and JSON give typed values, which are compared as values. A change of the algorithm is a new name, and a snapshot of the old name is refused rather than calling every row changed.
* **Size.** A snapshot holds the key and 22 characters per row, in the progress store's database; it fits a source of some millions of rows and is the wrong tool for much more. Keys are stored as they are, so a source whose keys must not be kept in the runner's state is not a source for this class.

Tests: `DbDataKit.Runner.Kit.Changes.Tests` (hashing, the comparison, and a job under `RunSimulator`: a changed, an added and a deleted row found between two runs, a failed run keeping the old snapshot).
