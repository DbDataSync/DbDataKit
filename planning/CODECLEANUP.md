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
  sections 4.4 and 4.5 in KNOWLEDGE's open designs), except those CODECLEANUP-P3 asks the owner about. What is left is CODECLEANUP-5.

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| CODECLEANUP-1 | Delete the old state path (adapter first: every product opens its state through modules) and fix the `SessionAuthRegistration.cs` comment | none | M | nothing references `StateSchema`, `SchemaVersion` or the older stores; the module tests pass on SQLite, SQL Server and PostgreSQL |
| CODECLEANUP-2 | Delete `LineageKeeper`, `SyncLineageRuns`, `LocalRunnerState` and `IInitialLoadEnqueuer` with their tests, and the comments that name them | none | S | the solution builds; the Lineage and Sync State suites pass; `git grep` finds none of the four names |
| CODECLEANUP-3 | Comments and docs that describe a deleted server as current: rewrite or remove them (outside `DbDataSync/architecture/`) | none | S | `git grep -n "DbDataSync.Api\|Build server\|Store server"` outside history files lists only clean-break messages and the release notes |
| CODECLEANUP-4 | `DbDataSync/architecture/` as CODECLEANUP-P1 says | CODECLEANUP-P1 | S | the folder is gone or holds what CODECLEANUP-P1 kept, and `check_docs_commands.py` passes |
| CODECLEANUP-5 | The decisions of the deleted plans that a product doc carries only in part: check each against the doc and write what is missing as design (what it does, why, an example), never as a list of decisions: plan 27 section 8's decisions 1 to 34 and 43 to 46 (agents, integrations, chunking) against `docs/agent-tools.md`, `docs/agents/`, `docs/integrations.md` and `docs/knowledge.md`; 27a SD8 to SD17 and SD23 against the same docs; 23a's decisions D1 to D7 and QF1 to QF10 against `Shared/src/DbDataKit.Quality/README.md`; plan 32's custom-view decisions D1 to D9 against `docs/views.md`; 14a D26 and 14's R2a against `docs/semantic/`. A decision that later work seems to have overturned is never dropped: it goes to the owner as a proposed decision, as CODECLEANUP-P3 does | none | M | checked by hand (it is prose): each decision read against its doc; the commit lists each one as carried (with the doc and section) or as a new proposed decision |
| CODECLEANUP-6 | (After the owner's sign-off of the migration audit, given 2026-11-18, so no plan reference is removed before its design has a home.) A repository check against references to the deleted plans and their row ids in code, comments, docs, workflows and messages (`plan 33`, `28c E17`, `SC23`, `GP16` and the like), with a baseline of today's occurrences per file that only shrinks; a new reference fails, naming the file and line | none | S | the check and its unit tests run in CI; the baseline lists every file that has references today (about 5,700 in about 2,700 files) |
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

### CODECLEANUP-P3: decisions of the deleted plans that later work seems to have overturned (awaiting the owner)

Each of these was decided (or taken as a default) in a deleted plan, and is in no product doc because later work seems to have replaced it. None is
dropped without the owner's word. Scenario: a worker carrying the old decisions into the docs reaches 28c ED16 ("Build keeps its write login for
person-driven flows") and finds that those flows are now Runner tasks of the operations package (AUTHORING-1); writing ED16 into the Build docs would
describe a product that no longer exists, and silently leaving it out would drop an owner decision.

- 28c ED6 and ED7 (move one replication or refresh plan at a time from the old scheduler to the Runner, with a recorded owner per object and shadow
  runs that only compare schedule decisions): the old schedulers were deleted with the servers, so there is nothing to move from.
- 28c ED9 (provisioned-column reports are run outputs that the Sync back end commits to the mapping's column cache): the Sync back end is gone; who
  reads the payload now (the Workspace) is not decided.
- 28c ED12 (Sync moves to the Runner in three steps, Build in two): the moves are done.
- 28c ED16 and QE6 (owner, 2026-11-07: Build keeps its write login for person-driven flows such as `deploy --apply`, `ack` and seeds, re-evaluated
  later, on the condition of the connection inventory): with the Build server gone these flows run as Runner tasks with the Runner's logins.
- 28c QE5 (a back end's paired client may pause and run now on the Runner as an Operator): the back ends are gone; the Workspace and the console act.
- 32 decisions 2 and 21 (one `dashboard/1` format for apps and published dashboards; two kinds `dashboard/1` and `report/1` on a shared core): replaced
  by `page/1` with a `kind` (`docs/pages.md`, PAGEFORMAT-1).
- 32 decision 8 ("PDF and SVG only for now, no PNG"): later decisions add an inline PNG to a subscription's email and a PNG export of a custom page
  (PAGEFORMAT-7, SUBSCRIPTIONS-3).
- 27a SD1, SD6 and SD7 (slice-id prefixes per lane, the status words of slice rows, which slices wait on the Workspace): conventions of a deleted plan's
  table, with no product behaviour.

Option A: drop all of them as overturned (git keeps them). Option B: write each into the doc of the part it describes as history ("before the servers
were deleted ..."). Option C: decide item by item. Recommendation: A, except ED16's condition (the connection inventory), which is built and
documented in `DbDataRunner/README.md`.

## Shared areas touched

- `DbDataKit.State`, `DbDataKit.State.Engines`, `DbDataKit.Scheduling`, `DbDataKit.Web` (`SessionAuthRegistration.cs`), `DbDataKit.Lineage`.
- `DbDataKit.slnx` (removed test files only), `scripts/product_name_baseline.txt` and `scripts/check_docs_commands.py` if `DbDataSync/architecture/` goes.
