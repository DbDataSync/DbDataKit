# Sync on the runner: what is proven, and what is not

Plan 28c, slices E03, E06d and E12. "Supervisor" is `RunExecutor` in the API's worker over Sync's own state; "runner" is the drain: the engine started by the runner's executor from the read-only view of a published definition, the position in the runner's checkpoint, the logins from the environment. A row is **proven** when a test that ran against the real engines ends with the target holding the source's rows. The tests are the source of truth; this page names them.

## Replication pairs

Reader `Watermark` (a version column), writer not named in the file (default `ChangeApply`), mapping with no position (so the first drain is also the initial load), then inserts, updates and NULLs, then a drain from a position the state lost. `ReplicationPairsTests.Ring` runs on both paths, `ReverseRing` on the runner only.

| Source | Target | Supervisor | Runner |
| --- | --- | --- | --- |
| postgres → mysql | Watermark, ChangeApply | proven | proven |
| mysql → mariadb | Watermark, ChangeApply | proven | proven |
| mariadb → oracle | Watermark, ChangeApply | proven | proven |
| oracle → sqlserver | Watermark, ChangeApply | proven | proven |
| sqlserver → postgres | Watermark, ChangeApply | proven | proven |
| postgres → sqlserver | Watermark, ChangeApply | not run | proven |
| sqlserver → oracle | Watermark, ChangeApply | not run | proven |
| oracle → mariadb | Watermark, ChangeApply | not run | proven |
| mariadb → mysql | Watermark, ChangeApply | not run | proven |
| mysql → postgres | Watermark, ChangeApply | not run | proven |

SQL Server Change Tracking into each target (`ChangeApplyReplicationTests`, the runner variant seeded with the start position):

| Source | Target | Supervisor | Runner |
| --- | --- | --- | --- |
| sqlserver CT | postgres, mysql, mariadb, oracle, sqlserver | proven | proven |
| sqlserver CT | sqlserver (MERGE writer, an existing configuration) | proven | not run |

## Every engine as a source and as a target on the runner

| Engine | As a source (runner) | As a target (runner) |
| --- | --- | --- |
| SQL Server | Change Tracking, Watermark | ChangeApply, StagingTable; 1,000,000 rows in `DrainOnTheRunnerTests` |
| PostgreSQL | Watermark | ChangeApply, CopyStaging |
| MySQL | Watermark | ChangeApply |
| MariaDB | Watermark | ChangeApply |
| Oracle | Watermark | ChangeApply |

## Other behaviours on both paths

| Behaviour | Supervisor | Runner | Test |
| --- | --- | --- | --- |
| Full load then incremental, SQL Server to SQL Server | proven | proven | `RunExecutorIntegrationTests.FullPipeline_...` |
| A failed write leaves the position | proven | proven | `RunExecutorIntegrationTests.AFailedWrite_...` |
| A table replicated by two mappings keeps a position for each | proven | proven | `ReplicationPairsTests.TwoMappingsOfOneSourceTable_...` |
| Verification result file | beside Sync's state | a payload of the run, scratch deleted | `ReplicationPairsTests.AVerification_...` |

## Not proven

- PostgreSQL logical slot (`PgLogicalSlot`): no wal2json in the test engines used for this work.
- SQL Server CDC, `TriggerAudit` (MySQL, MariaDB, Oracle, SQL Server) and Oracle Flashback as the reader on the runner: the readers have their own driver tests; no replication ends to end through the runner with them.
- A Watermark reader cannot see deletes, so no pair here deletes a row; Change Tracking deletes are proven in `ChangeApplyReplicationTests`.
- The API reading a verification result from a runner-owned replication (list and page of the payload): the engine side is done, the API still reads files beside its own state.
- Reverse-ring pairs on the supervisor; same-engine pairs (postgres to postgres, and so on).
- Other tests of `RunExecutorIntegrationTests` (provisioning, reconcile, timing, trace) stay supervisor-only: they assert on Sync's own stores.
