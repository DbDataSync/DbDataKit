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
- The deleted plans 14a, 22a, 23a, 24a, 27, 27a, 28c and 32 held decision tables that were only partly carried into product docs (last on `main` at the
  commit before the one that created `planning/`).

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| CODECLEANUP-1 | Delete the old state path (adapter first: every product opens its state through modules) and fix the `SessionAuthRegistration.cs` comment | none | M | nothing references `StateSchema`, `SchemaVersion` or the older stores; the module tests pass on SQLite, SQL Server and PostgreSQL |
| CODECLEANUP-2 | Delete `LineageKeeper`, `SyncLineageRuns`, `LocalRunnerState` and `IInitialLoadEnqueuer` with their tests, and the comments that name them | none | S | the solution builds; the Lineage and Sync State suites pass; `git grep` finds none of the four names |
| CODECLEANUP-3 | Comments and docs that describe a deleted server as current: rewrite or remove them (outside `DbDataSync/architecture/`) | none | S | `git grep -n "DbDataSync.Api\|Build server\|Store server"` outside history files lists only clean-break messages and the release notes |
| CODECLEANUP-4 | `DbDataSync/architecture/` as CODECLEANUP-P1 says | CODECLEANUP-P1 | S | the folder is gone or holds what CODECLEANUP-P1 kept, and `check_docs_commands.py` passes |
| CODECLEANUP-5 | Carry what is still true of the decision tables of the deleted plans 14a, 22a, 23a, 24a, 27, 27a, 28c and 32 into the product docs that describe those parts (`docs/semantic/`, `docs/serving-layer.md`, `Shared/src/DbDataKit.Quality/README.md`, `docs/views.md`, `docs/knowledge.md`, `docs/agent-tools.md`, `docs/integrations.md`, `DbDataRunner/README.md`), as design prose; drop what later work overturned | none | M | checked by hand (it is prose): each table read against the docs, the commit lists each entry carried or dropped |

## Needs from other subjects

None.

## Proposed decisions

### CODECLEANUP-P1: `DbDataSync/architecture/` (awaiting the owner)

Option A: delete the folder; git keeps it. Option B: keep only `staging-value-types.md` and `runner-coverage-matrix.md` (cited by the Transfer README and
Sync's docs) and delete the rest. Option C: keep it as it is. Scenario: a new agent searches for how Sync verifies a load; under C it finds phase notes about
a server that no longer exists; under A and B it finds `DbDataSync/README.md` and the code. Recommendation: B.

### CODECLEANUP-P2: Build's command line on the shared router (awaiting the owner)

Build's `CliApp` parses its own commands; the other programs use `NounRouter`. Option A: move it (a few days, no behaviour change). Option B: leave it; new
code calls Build as a library, so the command line is only a tool for people. Scenario: a new noun is added to every product; under A Build gets it with
the others, under B it is wired by hand. Recommendation: B until a change needs A.

## Shared areas touched

- `DbDataKit.State`, `DbDataKit.State.Engines`, `DbDataKit.Scheduling`, `DbDataKit.Web` (`SessionAuthRegistration.cs`), `DbDataKit.Lineage`.
- `DbDataKit.slnx` (removed test files only), `scripts/product_name_baseline.txt` and `scripts/check_docs_commands.py` if `DbDataSync/architecture/` goes.
