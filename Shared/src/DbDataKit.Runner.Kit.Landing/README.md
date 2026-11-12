# DbDataKit.Runner.Kit.Landing

Rows an integration brings in land in two places (plan 27a IX11): a **staging table** of a suite connection, for Build's models to read, and the **tables of a DbDataStore model**, through the Store's own write path. Both are idempotent: landing the same rows again changes nothing.

## Staging tables: `StagingLander`

Rows into a table of a suite connection through `DbDataKit.Transfer`: every statement passes the **write gate** (its checks, dry run, statement log and read-only mode apply), a batch is **one transaction**, and a batch has an **idempotency key**.

```csharp
var lander = new StagingLander(gate, dialect, new TableName("dbo", "orders_raw"), schema, new LandOptions { OnBadRow = BadRowDecision.Collect, Bad = bad });
await lander.EnsureTableAsync(ct);                                          // the columns of the schema, plus __batch and __row
var landed = await lander.LandAsync($"orders-{fileHash}", DelimitedReader.ReadAsync(stream), ct);   // landed.AlreadyLanded when the key was in the table
```

* The key is written into every row (`__batch`, and the record's number in `__row`); a key that is in the table is not written again. It names a batch's **content**, not a try: use the hash of a file, a day, a page's cursor. The same key with other rows is a mistake the lander does not detect. `LandOptions.Replace` deletes the batch and writes it anew, in the same transaction.
* Rows of the readers of `DbDataKit.Runner.Kit.Formats` are coerced to the column types by `ValueCoercion` (text and file values to the column's type, never rounded or guessed); a value that does not fit is a bad record by the reader's decision (refuse: nothing of the batch is written; skip; collect: the position and reason are kept, never the value).
* The table is a staging table: its columns are nullable, the model downstream enforces the rules.

Tested on **DuckDB, PostgreSQL and SQL Server** (`LandingTests`; PostgreSQL and SQL Server by the suite's test environment variables).

## DbDataStore: `StoreLander`

Records into an entity of a Store model through `ModelWriter`, the library the Store's server itself writes with, **in process**: the integration holds the model and a connection to the database the model lives in, and no server stands between. Every rule, version check, history row and audit row of a write by a person applies; the actor is the integration's (`WriteActor`).

```csharp
var lander = new StoreLander(writer, model, "territory", new WriteActor("i-crm-sync", "CRM sync"), new StoreLandOptions { BatchRecords = 200 });
var landing = await lander.LandAsync(records, ct);          // or the rows of a Formats reader
if (landing.Refused is { } refused) { /* refused.Violations: rule, attribute and the Store's sentence; refused.BadRecord: the first bad record's number */ }
```

* Records are matched by the entity's key: an unknown key is inserted, a known key with other values is updated (only the attributes the record names, at the version just read), a known key with the same values is left alone (no new version, no audit row). Landing the same records again changes nothing.
* A batch is one transaction. A batch the Store refuses stops the landing at the **last good point**: `Through` records are in the Store, the refusal names the rule (`pattern`, `allowed`, `unique`, `reference`, ...) and the first bad record (found by a dry run of each record, `ModelWriter.ValidateAsync`), nothing after the batch was tried. A rerun with the same records, fixed or not, finds the good point by looking at the Store, so nothing is remembered between runs.
* Not offered: a Store reached through its HTTP API (no client library of that API exists; this route reaches the same write path without it and needs the database connection), files to DbDataStore documents (plan 15's documents do not exist yet; the row waits for `webdav`).

Tested on **PostgreSQL and SQL Server** (`StoreLandingTests`).
