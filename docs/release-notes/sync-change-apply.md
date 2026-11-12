# Sync: one change writer for every engine, and the new default behind an incremental reader

Release note for plan 28c, CW01. Sync supports SQL Server, PostgreSQL, MySQL, MariaDB and Oracle, but an incremental pass (Change Tracking, CDC, watermark, trigger audit, logical slot, Flashback) could only write through the SQL Server `MERGE` writers. This release adds **`ChangeApply`**, one writer for all of them: the staged changes are collapsed to the last change per key, then a `DELETE`, an `UPDATE` and an `INSERT` run against the target in one transaction. No `MERGE`, `ON CONFLICT` or `ON DUPLICATE KEY` is needed. It is documented in Sync's [Replication concepts](../../DbDataSync/docs/replication-concepts.md#changeapply-the-change-writer-of-every-engine).

## What changes by itself (the default)

| Was | Is |
| --- | --- |
| `changeProcessing.writer` left out of `task.yaml`: the file could not be read | the writer is named when the file is read: **`ChangeApply` behind an incremental reader** (`MsSqlChangeTracking`, `MsSqlCdc`, `Watermark`, `TriggerAudit`, `PgLogicalSlot`, `OracleFlashback`), `DeleteInsert` behind any other reader |
| a table mapping that overrides the reader to an incremental one kept the replication's reconciling writer and was refused at save | while the replication's writer is unnamed it gets the default for its own reader |
| the pipeline screen kept the previous writer when the reader was changed to an incremental one | it offers `ChangeApply` when the previous writer was a reconciling one (you can pick another) |
| a Bulk Load of an unnamed writer | `DeleteInsert` (a reload reconciles), not the change writer |

## What does not change

A writer that is **named** is never changed: every existing `MsSqlMerge`, `MsSqlMergeReconcile`, `DeleteInsert`, `Scd2` or other configuration keeps running as written. The rule that a reconciling writer is refused behind an incremental reader stays, for the writers that really reconcile (`DeleteInsert`, `MsSqlDeleteInsert`, `MsSqlMergeReconcile`, `MsSqlBulkInsert`, the key-reconcile writers); `ChangeApply` does not reconcile and is accepted behind all of them.

## Behaviour to know

- The key is the target's primary key and the mapping must write it; a keyless target is refused with a message that says so (read it with `BatchReload` and write with `DeleteInsert`).
- A primary key change at the source arrives as a delete of the old key and an insert of the new one from Change Tracking, CDC and, since this release, the logical-slot reader. `TriggerAudit` says it too once its trigger is the current text (plan 28c CW02a: PostgreSQL, MySQL, MariaDB and Oracle triggers installed before it must be replaced, and a pass fails naming the table until they are; SQL Server's always did); `Watermark` cannot say it, so the old key stays in the target until a reload or the delete-reconcile sweep.
- Oracle staging: a NULL cell in a multi-row staging insert next to a value failed with ORA-01790 and ORA-00918 (a delete carries NULL in every non-key column); fixed, every Oracle target that stages a delete benefits.
- Formats: no key and no format version changes (`sync-task-config` stays at 2): `writer` may be left out and `ChangeApply` is a new value of an existing key. A Sync that is older than this release cannot read a file without a `writer` and fails a run that names `ChangeApply` with its unknown-writer message.
