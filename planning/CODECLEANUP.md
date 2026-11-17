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
| CODECLEANUP-6 | A repository check against references to the deleted plans and their row ids in code, comments, docs, workflows and messages (`plan 33`, `28c E17`, `SC23`, `GP16` and the like), with a baseline of today's occurrences per file that only shrinks; a new reference fails, naming the file and line | none | S | the check and its unit tests run in CI; the baseline lists every file that has references today (about 5,700 in about 2,700 files) |
| CODECLEANUP-7 | `Shared/` (libraries, tests and web packages): remove the old plan and row references, and rewrite or delete every comment and code doc that is out of date (a deleted server, the embedded runner, pairing by code, a route or type that no longer exists, a "not built yet" that is built); a comment keeps what it explains about the code, not where the work came from | CODECLEANUP-5, CODECLEANUP-6 | L | the check's baseline has no `Shared/` entry; the Shared suites pass |
| CODECLEANUP-8 | The console (`DbDataKit/`, `Shared/web/console` if not done in CODECLEANUP-7), the Workspace (`DbDataWorkspace/`) and the Runner host (`DbDataRunner/`): as CODECLEANUP-7 | CODECLEANUP-5, CODECLEANUP-6 | M | no baseline entry under those folders; their suites pass |
| CODECLEANUP-9 | The Gateway (`DbDataGateway/`): as CODECLEANUP-7 | CODECLEANUP-5, CODECLEANUP-6 | M | no baseline entry under `DbDataGateway/`; its suite passes |
| CODECLEANUP-10 | Sync, Build and Store (`DbDataSync/` outside `architecture/`, `DbDataBuild/`, `DbDataStore/`): as CODECLEANUP-7 | CODECLEANUP-5, CODECLEANUP-6 | M | no baseline entry under those folders; their suites pass |
| CODECLEANUP-11 | Everything else (`docs/`, `dist/`, `scripts/`, `testenv/`, `.github/`, root files): as CODECLEANUP-7; then the baseline is empty and is deleted, and the check refuses any reference | CODECLEANUP-7 to CODECLEANUP-10 | S | the baseline file is gone and the check passes with no exceptions |

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
