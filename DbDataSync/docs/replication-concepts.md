# Replication concepts

[DbDataSync](../README.md) · [Install](install.md) · [Configuration](configuration.md) · [Getting started](getting-started.md) · **Replication concepts** · [Drivers and libraries](drivers-and-libraries.md) · [State database](state-database.md) · [Building from source](development.md)

[Getting started](getting-started.md) walks through setting up a replication end to end. This page is
the reference for *how* it actually behaves once it's running: how DbDataSync detects changes at a
source, how an on-demand or first-ever reload works, how it notices a row was deleted when its ordinary
sync mechanism can't see deletes, and how it checks a target actually matches its source.

## The four kinds of run

Everything DbDataSync's worker does is one of four `RunKind`s, and they split across two independent
lanes so a long reload can never starve the latency-sensitive incremental sync of a worker slot:

| Kind | Lane | Does |
| --- | --- | --- |
| **Primary** | Change Processing | The ongoing incremental sync — one per table mapping, on its own schedule. The only kind that ever advances a mapping's watermark. |
| **BulkLoad** | Bulk Load | An on-demand or first-time full/segmented reload of one mapping — "Backfill" in the UI. Never advances the watermark. |
| **ReconcileDeletes** | Bulk Load | A cheap, key-only sweep that removes target rows whose key vanished from the source. Never inserts, updates, or advances the watermark. |
| **Verification** | Bulk Load | A source-vs-target comparison. Writes a report, never a row. |

The rest of this page covers each in turn.

## Change processing

A mapping's **Change Processing** pipeline is a reader, a staging provider, and a writer — configured at
the replication level, overridable per mapping. The reader is what actually detects changes at the
source; which reader you pick, and what it requires, is the single biggest decision behind how a
replication behaves.

### Reader kinds

| Kind | Mechanism | Detects deletes? | Setup | When to use |
| --- | --- | --- | --- | --- |
| `MsSqlChangeTracking` | SQL Server **Change Tracking** — the *net* change since a version, one row per key | Yes | Enable Change Tracking on the database and table (an operator's own DBA task — DbDataSync only ever queries it, never enables it) | The recommended default for SQL Server: simpler to enable than CDC, and its net-change semantics match what mirroring wants |
| `MsSqlCdc` | SQL Server **Change Data Capture** — every intermediate change harvested from the transaction log | Yes | Enable CDC capture on the table | Reach for this over Change Tracking specifically for **read consistency under concurrent writes** — Change Tracking's own reader joins live table data with its change list, and the two can move against each other under load; CDC's reader needs no such join. Not an "upgrade" to Change Tracking — a different trade |
| `PgLogicalSlot` | PostgreSQL **logical decoding** — every change, read out of the write-ahead log through a replication slot with the `wal2json` output plugin | Yes | `wal_level = logical` (a **server restart**), the `wal2json` plugin installed *and* listed in `output_plugin_libraries` (a reload, not a restart), and a replication slot DbDataSync previews and creates | PostgreSQL's equivalent of CDC, and the only Postgres mechanism that reports deletes without costing every write to the table. The obstacles are real and worth knowing up front: the restart, the plugin, and a slot that pins WAL on the source until something advances it |
| `TriggerAudit` | A trigger-maintained shadow audit table, engine-neutral | Yes | DbDataSync generates the trigger/table DDL; an operator previews and applies it | Any engine with no native change-tracking metadata — the one mechanism that reaches SQL Server, PostgreSQL, MySQL/MariaDB, Oracle, and anything ODBC/JDBC-reachable identically. Costs every write to the table, forever — that trade is worth naming to an operator before they turn it on |
| `Watermark` | A monotonic column (an `updated_at`, a version) — reads rows where it exceeds the stored position | **No** | A monotonic watermark column on the table | Any engine, when the table has such a column and either never deletes rows or re-keys them, or deletes and key changes are handled separately by [reconciliation](#reconciliation-delete-detection) below (it also **cannot see a primary key update**: see [A key change](#changeapply-the-change-writer-of-every-engine)) |
| `BatchReload` | A full (or segmented) table read — not incremental | N/A (a reload always converges) | None beyond read access | A standalone reload replication, or as the mechanism a [Bulk Load](#bulk-loading-backfill) actually runs under |
| `OracleFlashback` | Oracle **Flashback Version Query** — `VERSIONS BETWEEN SCN` reads a table's own committed row history directly. No shadow table, no trigger | Yes | None beyond ordinary `SELECT`, `FLASHBACK` privilege (only if the connection doesn't own the table), and `EXECUTE` on `DBMS_FLASHBACK` | Oracle sources, when the per-write trigger cost isn't wanted. Bounded by the source's undo retention — a replication paused longer than that needs a reload to catch back up, the same way any log-based mechanism's history can expire |
| `ScriptedQuery` | An operator-supplied SQL script | Depends on the script | Write the script | A change-tracking mechanism DbDataSync has no built-in driver for — the MySQL binlog, Oracle LogMiner — as an escape hatch |
| `DuckDbQuery` | An operator-written query against DuckDB's own scanners | No — not incremental | Write the query | A query-first source: Parquet, CSV, an S3 glob, an attached database — the query *is* the configuration |

PostgreSQL has `PgLogicalSlot` as its CDC-equivalent; without it, its options are `Watermark`,
`TriggerAudit` and `BatchReload`, the same as any generic engine reached through a
[descriptor driver](drivers-and-libraries.md#descriptor-drivers). MySQL/MariaDB's options are those
same three — the binlog-based native alternative is not supported yet. Oracle adds `OracleFlashback` to
them as its own native option; LogMiner is not supported yet.

**MySQL/MariaDB's `Watermark` reader has one real, unresolved gap worth knowing before combining it with
a per-pass row cap**: the bounded read that caps a `Primary` pass at N rows relies on the engine
supporting a tie-safe row limit (`WITH TIES` or equivalent), which MySQL and MariaDB have no working
form of. A capped pass that ends exactly on a tie can skip a sibling row sharing the boundary value until
a later pass happens not to land on that same tie — real, not theoretical, and not specific to any one
schema. Every other built-in driver's `Watermark` reader is tie-safe; MySQL/MariaDB's is the one
exception.

### Read intent

A mapping's next `Primary` pass resolves one of four **read intents** — what it's being asked to do,
distinct from what it's currently doing:

| Intent | Means |
| --- | --- |
| `InitialLoad` | Start from scratch. The default for a brand-new mapping — see [Bulk loading](#bulk-loading-backfill) for what this actually triggers. |
| `Changes` | Ordinary incremental read from the stored position. What every mapping settles into after its first successful pass. |
| `ChangesFromEarliest` | Replay everything the feed still retains, without a full table load. Not every reader can honor this — the `Watermark` reader can't, since for it the feed *is* the table and this would just be a full load under another name. |
| `ChangesFromLatest` | Skip to now — adopt the current position without reading any backlog. |

Set on a replication (with a per-mapping override) as **Default read intent**, in the same
inherit/override shape every other pipeline setting uses. Setting a mapping to either `Changes...` value
is how an operator asserts it must never full-load on its own — useful for a brand-new mapping that
should never see rows older than its feed.

A mapping's next pass can also be **held** from running at all, for a reason distinct from its intent:

| Hold | Means |
| --- | --- |
| `None` | Nothing is holding it back. |
| `PositionExpired` | The source discarded the change history a pass needed — reload to recover. |
| `Paused` | An operator stopped this one table specifically. |
| `Loading` | An initial load is running and nothing durable backs a position yet — see below. |

Holds show up on the Monitoring tab — a mapping stuck at `Loading` reads as "loading," not as silently
idle.

### Why some readers' first pass becomes a Bulk Load

An initial load is only correct if the change feed's position is captured **before** the table is read —
capture position, run the load, persist the position, switch to `Changes`. Get that ordering backwards
and every change made during a multi-hour load is lost silently: the load succeeds, the row counts look
right, and the rows in between are simply never seen again.

Readers that can report their current position **without reading a row** (`MsSqlChangeTracking`,
`MsSqlCdc`, `Watermark`, `TriggerAudit`, `PgLogicalSlot`, `OracleFlashback`) do exactly that on a
mapping's first-ever pass: capture the position, stash it, set `ReadHold.Loading`, and hand the actual table read to the
[Bulk Load](#bulk-loading-backfill) pipeline as a normal `RunKind.BulkLoad`. Only once every segment of
that load finishes does the captured position become the mapping's live watermark and its intent flip
to `Changes`. A reader with no honest answer to "what's my position" (`BatchReload`, `DuckDbQuery`)
has nothing to capture — its first pass just is the reload, with no separate position step.

### Scheduling

A replication runs **Continuous** (a worker loops: one `Primary` pass per due mapping, then waits
`FrequencySeconds` before checking again) or **Periodic** (a cron expression instead of a fixed
interval). A continuous worker that finds nothing to do for `IdleTimeoutSeconds` (default 60) exits — an
idle replication costs nothing between passes, and the timeout exists so that emptying the queue for a
fraction of a second after every pass doesn't respawn the process several times a minute.

### Staging and writers

Staging moves a batch of changes into a form the writer can apply efficiently — `MsSqlStagingTable`
(a real SQL Server bulk copy) or the portable `StagingTable` (batched parameterized inserts, works
anywhere including ODBC/JDBC).

| Writer | Reconciles? | Does |
| --- | --- | --- |
| `ChangeApply` | No | **The change writer of every engine, and the default behind an incremental reader** (SQL Server, PostgreSQL, MySQL, MariaDB, Oracle, and descriptor drivers that offer it). Loads nothing itself: the staged rows (with the operation marker I/U/D and their arrival order) are collapsed to the last change per key, then one transaction runs a `DELETE`, an `UPDATE` and an `INSERT` against the target. No `MERGE`, `ON CONFLICT` or `ON DUPLICATE KEY`; the `UPDATE` uses each engine's own join form. See [ChangeApply](#changeapply-the-change-writer-of-every-engine). |
| `MsSqlMerge` | No | A single `MERGE` — inserts, updates, applies explicit deletes the change set names. A target row the change set simply doesn't mention is left alone: exactly right for an incremental feed. SQL Server only; a replication that names it keeps it. |
| `Snapshot` | No — can't | Appends every staged row, timestamped. No comparison, no delete — for a target whose whole job is keeping what used to be there. |
| `Scd2` | No — deliberately not | The historized writer: keeps every version of every key rather than just the current one, via `DS_ValidFrom`/`DS_ValidTo`/`DS_IsCurrent`. Needs a reader that reports deletes to ever close a version for a deleted key — paired with one that doesn't, a deleted source row just stays current forever, which the writer picker says plainly. |
| `MsSqlMergeReconcile` / `MergeReconcile` | **Yes** | Inserts, updates, *and* deletes target rows in scope but absent from the change set — what lets a `BatchReload` converge. |
| `MsSqlBulkInsert` | **Yes** | Loads straight into the target with `SqlBulkCopy` — nothing staged. Paired with the `PassThrough` cache; see [Loading straight into the target](#loading-straight-into-the-target). |
| `MsSqlDeleteInsert` / `DeleteInsert` | **Yes** | Delete everything in scope, insert the staged set. No primary key needed — nothing is joined row by row. The portable option: what makes `Watermark` mode useful even on an engine with no delete-aware change source, when paired with a segmented reload. |

#### ChangeApply, the change writer of every engine

An incremental pass stages only what changed, so the writer must add, change and remove rows by key and must never remove a row the pass does not mention. Every engine can do that
with three plain statements; only the `MERGE`/upsert spellings differ, and `ChangeApply` does not use one. In one transaction on the target:

1. **Collapse.** The staging table keeps the last row per key (the highest arrival ordinal) and drops the rest. Several changes to one key in one pass are normal for a logical slot
   and for CDC's all-changes mode: *insert then update* is an insert of the final values, *insert then delete* is a delete of a key that is not there (nothing), *update then delete*
   is a delete, *delete then insert* is an update to the final values. Change Tracking, net-changes CDC, `TriggerAudit` and `Watermark` already hand over one row per key.
2. **Delete** the target rows whose key has a delete-marked staged row.
3. **Update** the non-key columns of the target rows whose key the stage holds, in the engine's own form: SQL Server `UPDATE t SET .. FROM t JOIN s`, PostgreSQL `UPDATE t SET .. FROM s WHERE ..`,
   MySQL and MariaDB `UPDATE t JOIN s ON .. SET ..`, Oracle a correlated `UPDATE t SET (..) = (SELECT ..) WHERE EXISTS (..)`.
4. **Insert** the staged rows (not delete-marked) whose key the target does not hold, `INSERT .. SELECT` with an anti-join.

The delete runs first, so a unique value that moves from a deleted row to an inserted one never collides.

- **Replay is safe.** The statements converge on the final state and count nothing: a delete of a key that is gone deletes nothing, the update sets final values, the insert only adds keys the
  target lacks. A pass applied twice (a retry, or a crash after the target committed and before the position was saved) leaves the same target. A pass is one transaction, so it is applied
  whole or not at all (`applyBatchSize` still splits each statement into slices of staged rows, inside that one transaction).
- **The key is the target's primary key**, and the mapping must write it. A target without one is refused with a message that says so; read the whole table with `BatchReload` and write with
  `DeleteInsert` for a keyless table. A key column is NOT NULL, as a primary key is.
- **A key change** (`UPDATE orders SET id = 2 WHERE id = 1`) reaches the writer as a delete of the old key and an insert of the new row, so the target ends with one row, not two. What
  says it depends on the reader: Change Tracking, CDC, the logical-slot reader and Flashback say it themselves. `TriggerAudit` says it too: the trigger records the old key as a delete
  and the new one as an update (SQL Server's statement-level trigger always did, because a row whose key moved matches nothing in its `deleted` table; the PostgreSQL, MySQL/MariaDB and Oracle
  triggers write the old key when the update changes a key column, and the reader refuses a pass over a trigger that still has the old text, naming the table and the way out: see [TriggerAudit and key updates](#triggeraudit-and-key-updates)). `Watermark` **cannot**: a scan sees only rows that
  exist, so the row with the old key is simply not there to read, and the new key is read only if the update also moved the watermark column. After a primary key update at the source the old key
  stays in the target until a reload or the delete-reconcile sweep removes it (the same with `MsSqlMerge`): the example is `orders` and its replica both holding `(1, 'Alice')`, the application
  running `UPDATE orders SET id = 2 WHERE id = 1`, and the target then holding `(1, 'Alice')` and `(2, 'Alice')`, with no pass failing. That is correct for a table whose keys never change. For one that can
  re-key, turn on [reconciliation](#reconciliation-delete-detection) and set `afterEveryPasses`, which runs the sweep every N passes so the old key is gone within a bounded time without a hand-run
  reload. The reader picker marks such a reader "cannot see a key change" (the capability `detectsKeyChanges`: false for `Watermark` and `ScriptedQuery`) and the Pipeline screen says so beside the choice.
- **Generated (identity) columns** that are not part of the key are written on insert and never updated.
- It does not reconcile: a target row the stage does not mention is left alone, which is why it is accepted behind every incremental reader, where a reconciling writer is refused.
- It needs a staging cache (`StagingTable`, `MsSqlStagingTable`, `PgCopyStaging`); it is refused behind `PassThrough`, which stages nothing.

**The default.** When a replication's `changeProcessing.writer` is left out of `task.yaml`, Sync names one when it reads the file: `ChangeApply` behind an incremental reader (`MsSqlChangeTracking`,
`MsSqlCdc`, `Watermark`, `TriggerAudit`, `PgLogicalSlot`, `OracleFlashback`), `DeleteInsert` behind any other reader, as the Add replication screen always started. A table mapping that overrides the reader
gets the default for its own reader while the replication's writer is unnamed, and a Bulk Load of an unnamed writer reloads with `DeleteInsert`. **A writer that is named is never changed**: every
existing `MsSqlMerge` (or any other) configuration keeps running as written. Choosing an incremental reader on the Pipeline screen offers `ChangeApply` for the writer when the writer was a reconciling one.
Saving a replication writes the writer into the file. This default is in the release notes of the repository (`docs/release-notes/sync-change-apply.md`; a page of the console's docs cannot link outside its folder).

#### Loading straight into the target

For a full load or reload of a large table, staging is a second copy of the data: written to a temporary
table, then written again into the target. On SQL Server, pair the **`PassThrough`** cache with the
**`MsSqlBulkInsert`** writer and the rows go from the reader into the target once, with `SqlBulkCopy`:

```yaml
bulkLoad:
  cache:
    kind: PassThrough
  writer:
    kind: MsSqlBulkInsert
    options:
      clear: Truncate        # Delete (default) | Truncate
      transaction: "true"    # "true" (default) | "false"
```

- **`clear`** — what happens to the target before the load. `Delete` removes the rows in this pass's scope:
  the whole table when unsegmented, only the segment when segmented. `Truncate` runs `TRUNCATE TABLE`,
  which is far cheaper but has no scope, so it is refused for a segmented load (each segment would wipe the
  previous one's rows) and for a table a foreign key references (SQL Server does not allow it) — both
  failures say to use `Delete`.
- **`transaction`** — `true`: the clear and the load commit together, so a failed load leaves the previous
  rows and readers never see an empty table, at the cost of holding locks for the whole load. `false`: a
  failure leaves the table cleared or partly loaded until the next run.
- The two go together: `PassThrough` only works with a writer that loads directly, and `MsSqlBulkInsert`
  only works with `PassThrough`; any other pairing fails the pass before anything is read.
- It reconciles, so like every reconciling writer it belongs on the **Bulk Load** pipeline (or behind a
  reload reader), not behind an incremental reader. It inserts only: a row that is not an insert fails the
  pass.
- Nothing is staged, so a hook's `{{staging}}` has no table to name.

**Engine support.** Only SQL Server has a direct-load writer so far. PostgreSQL, MySQL / MariaDB and Oracle
still stage first (PostgreSQL's staging is already a binary `COPY`, MySQL's and Oracle's batched inserts);
each needs its own writer that implements `IPassThroughWriter`, and the `PassThrough` cache is engine-neutral
and ready for them. Choosing `PassThrough` on one of those engines fails with "Target driver does not support
staging kind 'PassThrough'". The per-engine table is in the [README](../README.md).

`SupportsReconciliation` is what the reader/writer pickers use to show which writers reconcile —
covered in [Getting started](getting-started.md).

A reconciling writer takes the staged rows to be *everything* in its scope, so it only belongs behind a
reader that reads the whole scope (`BatchReload`, or a Bulk Load). Behind an incremental reader
(`Watermark`, `TriggerAudit`, Change Tracking, CDC, a logical slot, Flashback) a pass stages only what
changed, and the writer would delete every other target row. Saving a mapping with that pairing for change
processing is refused, and a mapping that reaches it anyway (saved before the rule, or its replication's
writer changed since) fails its incremental pass before anything is read. Use an upsert writer for change processing — `ChangeApply` on every engine
(the default when no writer is named), or `MsSqlMerge` on SQL Server — and set the reconciling writer on the Bulk Load pipeline if full
reloads should use it.

### TriggerAudit and key updates

A `TriggerAudit` shadow table holds one row per write to the source table: `DS_Seq` (the position), `DS_Op` (`I`, `U` or `D`) and the key columns. The generated trigger writes an `INSERT` as
`I`, a `DELETE` as `D` with the old key, and an `UPDATE` as `U` with the new key. **An `UPDATE` that changes a key column also writes a `D` row with the old key** (PostgreSQL and Oracle test every key column,
MySQL and MariaDB compare the old and new key null-safely), so `UPDATE orders SET id = 2 WHERE id = 1` leaves `D 1` and `U 2` in the shadow table, and the target loses row 1 as it gains row 2. SQL Server's
trigger is one statement-level trigger that joins the `inserted` and `deleted` tables on the key; a row whose key moved matches nothing in the other table, so it writes the `D` and the `I` without a
change. The two rows have different keys, so their order does not matter, and the read still collapses to the last row per key; a delete of a key that no row holds any more (a key that existed for a moment
in a swap through a free key) deletes nothing in the target.

The read takes the source row as the truth: a `D` as the last row of a key while the row exists (two rows swapping keys in one statement, or a key deleted and taken again by a write after the pass's end) is
staged as the update of that row.

**The trigger in your database is the text that was generated when the table was set up.** A trigger created before this change records only the new key of an update. The reader looks at the installed
text before every pass (PostgreSQL: the marker comment `-- ds-audit-v2` in the capture function's source; Oracle: the same marker in the trigger's source; MySQL and MariaDB: the update trigger's body
names `OLD`), and a pass over the old text **fails**, naming the table and the way out, because after the first key update at the source the target would be wrong with nothing to say so. Replace it
from the replication's **Provisioning** card: *Enable source change capture* lists a step "Replace the change function" (PostgreSQL), "Replace the change trigger" (Oracle) or "Replace the change triggers"
(MySQL, MariaDB); applying it replaces the text and leaves the shadow table, its rows and the replication's stored position alone, so nothing is read twice or missed. A trigger that is not there at all (set up by
hand, or not yet) is not a version question and is left to the reader's existing message. A descriptor (ODBC/JDBC) driver's trigger is the operator's own and is not checked.

## Bulk loading (backfill)

**"Backfill" in the UI is `RunKind.BulkLoad` internally** — the same operation, one name change partway
through the project's history that never touched the docs. [Getting started](getting-started.md#backfilling-a-table)
already covers the operator-facing side — segments, custom segmenting strategies, the Monitoring card.
This section covers the mechanics behind it.

### What triggers one

Two paths, both funneling through the same enqueue logic:

1. **An operator's own backfill** (`Runs → Backfill…`) — an explicit segment list, submitted once.
2. **A mapping's own first-ever pass**, for a [position-capturing reader](#why-some-readers-first-pass-becomes-a-bulk-load) —
   segmented exactly like a scheduled reload of that mapping (its own *Default reload segmenting*, empty
   meaning the whole table), with no operator in the loop.

Two identical operator-triggered requests for the same segment collapse into one run. The auto-triggered
path is different on purpose: it never collapses into someone else's unrelated request for the same
segment — it fails cleanly and retries on the mapping's own next scheduled pass instead, so two genuinely
different reasons for wanting the same data never get silently merged.

A Bulk Load **never advances the incremental watermark**, regardless of which of the two paths started
it — it lives entirely on its own lane, so it can run for hours without disturbing an ongoing sync.

### Its own pipeline

Bulk Load has its own reader/staging/writer configuration, separate from Change Processing, with the
same replication-level-plus-per-mapping-override shape as every other pipeline setting:

- **Reader** defaults to `BatchReload` — every driver is expected to offer a whole-table-read Kind.
- **Staging/Writer** default to `null`, meaning *inherit whatever Change Processing uses* — a reload
  into the same target table naturally uses the same write mechanism unless told otherwise (typically an
  upsert-only writer is picked deliberately for a genuinely first load, since there's nothing yet to
  reconcile away).

A [descriptor driver](drivers-and-libraries.md#descriptor-drivers) can be a Bulk Load source too, subject
to the same provisioning caveat that page documents.

### Segmenting

| Segment | Means |
| --- | --- |
| `Full` | The whole table, unsegmented. |
| `List` | Rows matching one of a list of values in one column. |
| `Range` | Rows where a column falls in `[min, max)` — half-open, so consecutive ranges tile without gaps or overlaps. |
| `Auto` | Split a column's actual value range into N even buckets — resolved against the live source at reload time, never persisted as itself. |
| `Custom` | Run a named, authored segmenting strategy and use whichever candidates it flags as selected — re-evaluated against live data every time, never frozen as a stored list. |

A table mapping's own **Default reload segmenting** (empty meaning `Full`) is what a scheduled
`BatchReload` replication re-reads on its normal cadence, what an auto-triggered initial load segments
by, and what the Backfill form opens pre-filled with — an operator can still edit it per-request. Custom
segmenting strategies — DuckDB SQL, Source SQL, Target SQL, or a bound C# strategy — are fully documented
in [Getting started](getting-started.md#custom-segmenting-strategies).

When a scheduled run of a mapping like that resolves to **more than one** segment, it doesn't read them
itself. The run works out the segments (a custom strategy runs once, and its log says what it selected),
queues one run per segment as a batch — exactly what a Backfill does — and finishes, reading nothing. The
segment runs carry the rows, and the batch below shows every segment queued, running or done, each of
which can be cancelled on its own. If the mapping's previous batch is still running when the next
scheduled run arrives, that run queues nothing and says so in its log. A mapping that resolves to a
single segment, or to none (the whole table), still reads inline in the one run.

### Progress and history

A batch is `Running` while any segment is still queued or in flight, `Completed` once every segment
succeeded, `CompletedWithFailures` once every segment finished and at least one didn't. The Monitoring
tab's live card shows rows copied against a one-time catalog-statistics estimate of the whole table (not
a live count — reads that estimate once, at enqueue) and a segment count; **Monitoring → Bulk Load
History** lists every past batch, filterable by mapping.

### Reading a run's log, and cancelling it

Open any run from **Runs** (click its status) to read its log — live while it runs, as recorded once it has
finished. This works for scheduled runs as well as ones you started from the page. A pass logs how its
segmenting resolved (the default list, which segments a custom strategy selected, or that it reads the whole
table in one query), the SQL its reader, staging and writer run, and the SQL a source/target segmenting
strategy runs. A run that is queued or running has a **Cancel** button in the history list and in this dialog.
Cancelling a running pass stops that one run at its next opportunity and leaves the others in the same worker
alone; the worker is only stopped if the run ignores the request for a minute.

## Reconciliation (delete detection)

A mapping on `Watermark`, or any reader that can't see deletes, notices inserts and updates through its
ordinary `Primary` pass but **never notices a row was removed at the source** — it's simply never
selected again. The only fix without reconciliation is a full reload through a reconciling writer, which
converges the target but moves every column of every row just to catch a handful of absences.

**Reconciliation is a separate, narrower sweep for exactly that gap** — read only the source's primary
keys (cheap, a covering-index scan), and delete the target rows whose key is absent from that set, within
the segment's scope. It never inserts or updates, and never touches content drift; a mapping that also
needs to repair rows that changed behind the replication's back still wants an ordinary reconciling Bulk
Load. The two are complementary, not substitutes for each other.

### The mechanism

Always the same reader/staging pair — `KeyReconcile` reads only mapped key columns, staged the ordinary
way — paired with one of two writers depending on the mapping's own writer:

- **`KeyReconcileDelete`** — deletes target rows in scope whose key the source scan didn't produce.
- **`KeyReconcileScd2Close`** — the SCD2-safe version: instead of deleting, **closes** the open version
  (`DS_ValidTo`/`DS_IsCurrent`) of every key absent from the source, matched by the *natural* key rather
  than the surrogate one. Used automatically when the mapping's own writer is `Scd2`.

Before either commits, a **delete guard** checks how much of the segment it's about to remove:

| Guard | Behavior |
| --- | --- |
| `none` | No check — every computed delete applies. |
| `ratio` (default, max `0.5`) | Refuses (rolls back) if the fraction of the segment being deleted exceeds the limit. |

The guard exists because a key-diff sweep's failure mode is silent and total — a source pointed at the
wrong database, a filter that stopped matching, and a genuinely deleted table all look identical to "the
whole segment is gone" from the writer's side, and a committed delete has no second chance to notice. An
empty scope (nothing matched) always passes — that's not a runaway delete, it's nothing to sweep. An
on-demand sweep can override the configured guard for one run.

### Configuration and scheduling

Reconciliation is **opt-in**, per replication with a per-mapping override — a **Delete reconciliation**
toggle, off by default. Once enabled, it runs on its own schedule (the same Continuous/Periodic shape as
Change Processing, evaluated independently), an **after-change** trigger, or both:

| After-change strategy | Means |
| --- | --- |
| `none` (default) | Cadence only. |
| `any` | Any row read by a successful `Primary` pass since the last sweep is enough to trigger another one — still floored by the configured cadence. |

**`afterEveryPasses: N`** (a number beside `every` and `afterChange`, off when left out) runs a sweep after every N successful change passes of the mapping since its last sweep was queued. It needs no
cadence, counts passes that read nothing (a quiet table still gets its sweep), and is the way to bound how long an old key stays in the target after a primary key update when the reader cannot see one
(`Watermark`): with `afterEveryPasses: 3` the old key is gone after the third pass. A sweep still in flight is not queued behind; a mapping never swept counts its whole history, so it is due at once if it
has made N passes. Under `reconcile:` in `task.yaml` (or the mapping's `reconcileOverride`) it is written as `afterEveryPasses: 3`; the Reconcile card calls it *Sweep after every N passes*. A value below 1 is
refused at save.

An operator can also trigger a sweep on demand (`Runs → Reconcile deletes…`), with the same
pre-filled-from-default segment picker the Backfill form uses — no reader/staging/writer choice to make,
since the pair is always fixed.

## Verification

A **Verification** run compares a mapping's source and target using one or more checks — `RowCount`,
`Sum`, a hand-written `Sql` check, or a script-generated pair of statements — grouped by declared
columns, with a difference threshold (a fraction of the larger side) so ordinary replication lag doesn't
paint everything red. For a historized target (`Scd2`/`Snapshot`), a check can compare against only the
target's *current* rows rather than its full history. It's operator-triggered only — nothing runs it on
a schedule today — and its result is written as a parquet file rather than as rows anywhere, indexed so
it can be found and paged back through the API.

## Telling a serving group that a table changed

A Gateway can keep the answers of queries in a result cache. It empties the entries that read a table when it is told the table changed, and otherwise when their time-to-live ends. Sync tells it. When a run of a replication ends having written rows (a change-processing run, a bulk load or a reconcile of deletes; a run that wrote nothing, a verification and a cancelled run do not count, and a run that failed after rows went in does, because the old rows are stale either way), the server sends one `data.changed` message for each target table of the mapping to every serving group it is paired with. The message names the table the way the lineage graph does (`engine://server/database.schema.table`, the same node `GET /api/lineage` sends) and carries the run's id and time. It never carries rows or statement text.

The server signs the message as itself, with the key of its own pairing, and the role Publisher; nobody's sign-in is involved. A group whose graph does not know the table ignores the message. A failed delivery is tried once more after 30 seconds and then given up on, with a line in the log and a counter, and never changes the run. A server with no pairing sends nothing.

Pair a server with a group from the console (**Servers**, **Link**), or on the server: `dbdatasync gateway add <name> --url https://gateway:port --code <code>` with a code made on the Gateway by `dbdatagateway server pair --max-role publisher`. `dbdatasync gateway list` and `gateway remove` show and forget the pairings (`gateways.json` in the repository root). A replication owned by a Runner is not told from here: the Runner reports what its tasks wrote.

## Where the tracking state lives

A replication's positions and the record of what each entry moved are its **tracking state**. The optional `tracking:` section of `task.yaml` says where it is kept: `mode: state` (the runner's
state only), `mode: target` (a state table on the target, written in the entry's transaction) or `mode: both` with `primary: target|state`. **A file without the section keeps the runner's state exactly as
before and creates nothing on its target**; the target is used only when the file asks for it.

**The position log** (the name since 2026-11-14; it was the "unit" table, `dbdatasync.unit`, `DBDATAKIT_SYNC_UNIT` on Oracle): a target that still holds the table under its old name is refused when a pass would create the new one, with a message naming the old table (drop it, or rename it and its columns `unit_seq`, `unit_id` to `entry_seq`, `entry_id` to keep its history); `tracking.retention.units` is now `entries`, and the copies' identities start `syncposition:` with the stream `position`. **MySQL, MariaDB and Oracle** keep a replication's state on the Runner only (`mode: state`, owner 2026-11-14): no writer keeps the position log on them, `target` and `both` are refused at save naming the engine, and a restored target of those engines is not flagged as diverged.

**On the target** (SQL Server and PostgreSQL): the table `dbdatasync.position_log`, created on first use (the target login needs the right to create the schema `dbdatasync` and the table; without it the pass fails
saying so). Every pass that read a new position appends one row (the entry: replication, mapping, source key, the position before and after, the rows staged, the run) **in the same transaction as
the entry's data**: the writer calls the state write just before its commit, so after a crash between the write and the commit the target holds the data and its row, or neither. The newest row of a mapping is the
position the next pass resumes from when the target is the primary (`target`, or `both` with `primary: target`). The writers that carry the row are ChangeApply, DeleteInsert, Scd2 and, on SQL Server, MsSqlMerge (which then
runs all its chunks in one transaction instead of one per chunk) and MsSqlDeleteInsert; a file that asks for the target with another writer is refused when the pass starts, naming the writer. MySQL/MariaDB and Oracle are
refused at save for now (plan 28c E17), a Flight SQL or DuckDB target never holds the table. The runner's state keeps its own copy of every position for status and lag in every mode.

**When the two sides differ** the primary decides and the run's log says so as a warning:

* *A target restored from a backup* holds an older position than the runner's state: with the target primary the replication resumes from the target's position (rows between are read again; writers are idempotent).
* *A target that holds no state for the mapping* (the first pass in this mode): the pass continues from the runner's position and the target records it from there.
  **Blind spot:** a target restored from a backup older than the table's creation has no table, so this cannot be told apart from the first pass; the runner's position is used and the data between the backup and that
  position is not read again. Reload the mapping after restoring such a target.
* *A runner state that was lost or reset* while the target holds positions: the pass resumes from the target's position, except that a reader that captures its own position starts an initial load (its position is captured before the target is opened).

The Overview tab's **Tracking** card and `GET /api/replications/{name}/tracking` say which mode applies, which was asked for and why they differ.

**The copy in the state, and divergence.** In `target` and `both` the Sync server follows the table: a minute apart (and on **Follow now** of the card, an administrator's call) it copies the entries a replication
recorded since its last checkpoint into the `tracking` module of the state (`tracking_row`, identity `syncposition:<engine>://<server>/<database>/<replication>@<connection>`), with the follower Build's tracking uses, so
the rows and the checkpoint move in one transaction. A run never waits for it, and a target that is down is the card's "the last follow failed", not an error of any run. Two kinds of difference are **persisted**
as a divergence (they stay until an administrator resolves them, and while one is open nothing is copied):

* *the table parts from its copy*: the target's newest entry is behind the checkpoint (a restore) or the copy holds entries the table does not;
* *the state's position is not the target's*: for a mapping, the state's position is an older entry of the target (the target is **ahead** of the state), is no entry the target recorded (the sides **part**: a restore, a
  replaced target), or the state has none (reset or lost). Only a difference the target has held for more than two minutes counts, because the pass that wrote an entry moves the state's position a moment later.

Resolving is the same action as Build's (`POST /api/v1/tracking/divergences/{id}/resolve`, administrator, audited in the management audit log): **archive** keeps the copy's rows after the point where the sides last agree
apart (out of the live stream), **reset** deletes them; the checkpoint goes back to that point and the next follow copies the target's entries from there. The resolve changes the *copy*; what heals a position difference is
the pass itself, which already resumes from the primary. The replication's Tracking card shows the open divergence with its reason and both buttons (disabled for anyone but an administrator), and the console's
**Tracking** page lists every replication's copy beside Build's and the runners', with its own mode and primary (the primary of a replication is the `tracking.primary` of its file, edited on the card). Every target
that holds the replication's entries is followed: the replication's own and the one each mapping names.

**A replication the runner owns** is followed by its own drain, not by the Sync server (whose state does not hold that replication's positions): after its passes (at most every 30 seconds) and once more when it ends, the
drain copies the entries into the **runner's** state through the runner protocol (rows and checkpoint in one transaction there, under the same `syncposition:...` identity, which must end with a connection the run holds a handle
for) and compares the target's newest entry of each mapping with the position its own checkpoint holds, with the same two-minute grace. A divergence is recorded in the runner's state; the runner's own
`GET /api/v1/tracking` lists it beside Build's copies (mode `both`, the target the primary: the setting itself travels in the published definition) and `POST /api/v1/tracking/divergences/{id}/resolve` (administrator,
audited as `runner.tracking.resolve`) archives or resets it, the console's Tracking page reaching both through the runner relay. The follower never fails the run: a target that is down, a refused grant or a copy that
failed are lines in the run's log.

**Retention of `dbdatasync.position_log`.** After each pass the mapping's old entries are removed in a transaction of their own: an entry goes only when it is older than 7 days **and** not among the newest 1000 entries of its
mapping, and the newest entry of every source is never removed (it is where the next pass resumes). The numbers are the optional `tracking.retention` of the file (`entries`, `days`; refused with `mode: state`, which
keeps no table, and for a number below 1 or a negative one, naming the key). A prune that fails is a warning in the run's log and does not fail the pass. The copy in the state keeps its rows past the table's
pruning (the module's retention has no age: how long the history is kept is the owner's decision).

## Next: Drivers and libraries

Which readers, writers, and staging providers are actually offered depends on the mapping's driver — see
[Drivers and libraries](drivers-and-libraries.md) for the built-in three and how to add another engine.
