# CODECLEANUP: code and documents left behind by the deleted servers

## Goal

No code is kept that only the deleted Build, Sync and Store servers called; the old state path is gone; no comment or document describes a deleted
server as current; what the deleted plans decided and is still true is in the product docs.

## What exists today

- The old state path: `StateSchema`, `SchemaVersion`, `user_version` handling and the older stores (`UserStore`, `SessionStore`, `InviteStore`,
  `OneTimeValues`, the legacy `NotificationStore`) are still in `DbDataKit.State`, `DbDataKit.Scheduling` and `DbDataKit.State.Engines`
  (`StateSchema.cs`, `StateDatabase.cs`, `StateOpener.cs`, `JobStateStore.cs`, `WorkQueueSchema.cs`, the snapshot reader and writer).
  `SessionAuthRegistration.cs` says the older stores stay "for the identity home, Sync".
- Code with no caller outside tests: `LineageKeeper` (`DbDataKit.Lineage`), `SyncLineageRuns`, `LocalRunnerState` and `IInitialLoadEnqueuer`
  (`DbDataSync.State`).
- `DbDataSync/architecture/`: 384 files of phase notes and designs that describe the Sync server (`DbDataSync.Api`) as current.
- Source comments cite plan and slice ids of the deleted plans (`plan 33 SC23`, `28c E17`); they are history, not task ids.
- The deleted plans held decisions that were only partly carried into product docs (git keeps them at the commit before the one that created
  `planning/`). The migration audit (signed off by the owner on 2026-11-18) traced every item: the decisions it found in no doc are now written as
  design (plan 07 in the Transfer README; plan 14a's saved queries, run records and explorer in `docs/semantic/architecture.md`; plan 15's approval rule
  in `DbDataStore/README.md`; 22a QG11 and QG13 in `docs/observability.md` and the Delivery README; 27a SD2 to SD4 in `docs/agent-tools.md`; 28c ED1 to
  ED18 and QE1 to QE12 in `DbDataRunner/README.md`; plan 32's section 12 in `docs/views.md`; 33 section 6d in `docs/architecture.md`; plan 27 section 2 and
  sections 4.4 and 4.5 in KNOWLEDGE's open designs). The decisions later work overturned were dropped by the owner on 2026-11-18 (git keeps them: 28c
  ED6, ED7, ED9, ED12, ED16 with QE6, and QE5; plan 32's decisions 2, 8 and 21; 27a SD1, SD6 and SD7); ED16's condition, the connection and usage
  inventory, is built and documented in `DbDataRunner/README.md`. What is left is CODECLEANUP-5.

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| CODECLEANUP-1 | Delete the old state path (adapter first: every product opens its state through modules) and fix the `SessionAuthRegistration.cs` comment | none | M | nothing references `StateSchema`, `SchemaVersion` or the older stores; the module tests pass on SQLite, SQL Server and PostgreSQL |
| CODECLEANUP-2 | Delete `LineageKeeper`, `SyncLineageRuns`, `LocalRunnerState` and `IInitialLoadEnqueuer` with their tests, and the comments that name them | none | S | the solution builds; the Lineage and Sync State suites pass; `git grep` finds none of the four names |
| CODECLEANUP-3 | Comments and docs that describe a deleted server as current: rewrite or remove them (outside `DbDataSync/architecture/`) | none | S | `git grep -n "DbDataSync.Api\|Build server\|Store server"` outside history files lists only clean-break messages and the release notes |
| CODECLEANUP-4 | `DbDataSync/architecture/` as CODECLEANUP-P1 says | CODECLEANUP-P1 | S | the folder is gone or holds what CODECLEANUP-P1 kept, and `check_docs_commands.py` passes |
| CODECLEANUP-5 | The decisions of the deleted plans that a product doc carries only in part: check each against the doc and write what is missing as design (what it does, why, an example), never as a list of decisions: plan 27 section 8's decisions 1 to 34 and 43 to 46 (agents, integrations, chunking) against `docs/agent-tools.md`, `docs/agents/`, `docs/integrations.md` and `docs/knowledge.md`; 27a SD8 to SD17 and SD23 against the same docs; 23a's decisions D1 to D7 and QF1 to QF10 against `Shared/src/DbDataKit.Quality/README.md`; plan 32's custom-view decisions D1 to D9 against `docs/views.md`; 14a D26 and 14's R2a against `docs/semantic/`. A decision that later work seems to have overturned is never dropped: it goes to the owner as a proposed decision of this subject | none | M | checked by hand (it is prose): each decision read against its doc; the commit lists each one as carried (with the doc and section) or as a new proposed decision |
| CODECLEANUP-6 | (After the owner's sign-off of the migration audit, given 2026-11-18, so no plan reference is removed before its design has a home.) A repository check against references to the deleted plans and their row ids in code, comments, docs, workflows and messages (`plan 33`, `28c E17`, `SC23`, `GP16` and the like), with a baseline of today's occurrences per file that only shrinks; a new reference fails, naming the file and line | none | S | the check and its unit tests run in CI; the baseline lists every file that has references today (about 5,700 in about 2,700 files) |
| CODECLEANUP-7 | `Shared/` (libraries, tests and web packages): remove the old plan and row references, and rewrite or delete every comment and code doc that is out of date (a deleted server, the embedded runner, pairing by code, a route or type that no longer exists, a "not built yet" that is built); a comment keeps what it explains about the code, not where the work came from | CODECLEANUP-5, CODECLEANUP-6 | L | the check's baseline has no `Shared/` entry; the Shared suites pass |
| CODECLEANUP-8 | The console (`DbDataKit/`, `Shared/web/console` if not done in CODECLEANUP-7), the Workspace (`DbDataWorkspace/`) and the Runner host (`DbDataRunner/`): as CODECLEANUP-7 | CODECLEANUP-5, CODECLEANUP-6 | M | no baseline entry under those folders; their suites pass |
| CODECLEANUP-9 | The Gateway (`DbDataGateway/`): as CODECLEANUP-7 | CODECLEANUP-5, CODECLEANUP-6 | M | no baseline entry under `DbDataGateway/`; its suite passes |
| CODECLEANUP-10 | Sync, Build and Store (`DbDataSync/` outside `architecture/`, `DbDataBuild/`, `DbDataStore/`): as CODECLEANUP-7 | CODECLEANUP-5, CODECLEANUP-6 | M | no baseline entry under those folders; their suites pass |
| CODECLEANUP-11 | Everything else (`docs/`, `dist/`, `scripts/`, `testenv/`, `.github/`, root files): as CODECLEANUP-7; then the baseline is empty and is deleted, and the check refuses any reference | CODECLEANUP-7 to CODECLEANUP-10 | S | the baseline file is gone and the check passes with no exceptions |

| CODECLEANUP-12 | Docs describe the product, not decisions: the dated owner entries left in `docs/` (8 in `serving-layer.md`, a few in six other pages; the decision tables of `architecture.md` are gone, their facts written where each part is described) rewritten as how the product works, with the skill `product-docs`; an entry that is no longer true is listed for the owner instead of rewritten; an entry that is a direction not yet built (or built in part) goes to "Planned architecture changes" of `docs/architecture.md`, marked Planned or In progress, with the subject that carries it (`planning/ROLES.md`, "Architecture directions"); the open subjects that change the architecture and have no entry there yet get one; the code comments that cite a decision of the old architecture tables (`D<n>`, `13c D<n>`; the tables are gone) point at `Design: docs/<path>.md#<anchor>` | CODECLEANUP-5 | M | a repository check fails on a decision table, "Decided (owner", "(owner, <date>)" or "Default taken" under `docs/` and on `13c D<n>` in code, and passes on the repository |
| CODECLEANUP-13 | The old "unit" name of Sync's state table is gone: the position log is the only name. Delete the check for a table under the old name (`TargetPositionLog.OldQualified`, its exists query and the refusal that names it), its test in `TargetPositionLogTests.cs`, and the history in `DbDataSync/docs/replication-concepts.md` ("it was the 'unit' table"); no code, doc or message says "unit" for the state table (the `docs/ci-lessons.md` entry that names it is reviewed in CODECLEANUP-14) | none | S | `git grep -i "unit table\|dbdatasync.unit\|SYNC_UNIT\|OldQualified"` finds only that ci-lessons entry; Sync's driver tests pass |
| CODECLEANUP-14 | Names that the environment variable rename (prefixes `DBDATASYNC_`, `DBDATABUILD_` and the others to `DBDATAKIT_`) rewrote but that are not environment variables: database objects, index names, messages, example text. List each one for the owner with where it is and what it was; fix the ones the owner names. Found so far: Sync's Oracle tables `DBDATASYNC_POSITION_LOG` (now `DBDATAKIT_SYNC_POSITION_LOG`) and `DBDATASYNC_UNIT` (CODECLEANUP-13). Reviewed with the owner in the same list: the `docs/ci-lessons.md` entry on `test_check_env_names` (it names the old table and advised the prefix swap), and whether `scripts/check_env_names.py` and its tests are still needed once no old name is left | none | S | the list is in this file and the owner has answered each item; the fixes' tests pass |

## Needs from other subjects

None.

## Proposed decisions

### CODECLEANUP-P1: `DbDataSync/architecture/` (awaiting the owner)

Option A: delete the folder; git keeps it. Option B: keep only `staging-value-types.md` and `runner-coverage-matrix.md` (cited by the Transfer README and
Sync's docs) and delete the rest. Option C: keep it as it is. Scenario: a new agent searches for how Sync verifies a load; under C it finds phase notes about
a server that no longer exists; under A and B it finds `DbDataSync/README.md` and the code. Recommendation: B.

## Shared areas touched

- `DbDataKit.State`, `DbDataKit.State.Engines`, `DbDataKit.Scheduling`, `DbDataKit.Web` (`SessionAuthRegistration.cs`), `DbDataKit.Lineage`.
- `DbDataKit.slnx` (removed test files only), `scripts/product_name_baseline.txt` and `scripts/check_docs_commands.py` if `DbDataSync/architecture/` goes.
