# Plan 35: Isolation by trust

Status: **in progress** (the owner accepted the design and all its recommendations on 2026-11-16; I1, I2 and I3 built 2026-11-16, I4 to I9 open). Slice ids `I1`... (`I` followed by a number is free in every plan).
I1 and I2 replace [plan 33](33-fewer-services.md) SC23b (the console's embedded runner) inside SC23; I3 to I9 come after SC23. Same rules as the other slice plans
(`CLAUDE.md`, "Making changes"): one slice, one commit whose message starts with its id, "done" is a test, the status goes in the slice's row in the same commit.

## The rule

**Work is placed by what it can touch, not by product or by how it is started.** There are three places, and each holds only the logins its work needs.

| Place | Servers | What runs there | Logins it holds | Faces |
| --- | --- | --- | --- | --- |
| **Serving** | the Gateway group | request/response only: our serving code, views, the apps host, agents' tools, front-end custom code | read logins, and the Store's person-attributed row-write login | end users, BI tools, agents |
| **Work** | the Runner group | everything scheduled or queued, and all trusted custom code (integrations, actions, the engines: Build, Sync, Store deploy), each task in a process of its own | the bulk-write and schema-change logins, bound to slots; the integration secrets | nothing outside |
| **Control** | the console and the Workspace | authoring, approvals, publishing, binding, starting manual tasks, Monitoring | no customer data login; the console makes every group key | people who administer and author |

Every server holds a login to **its own state database only**.

### How the three axes map

| Axis | Where |
| --- | --- |
| Reading | Serving, as the person. A Work task may read in bulk through its slot. |
| Person-scale data changes | Serving (Store rows, a view's change actions). |
| Bulk or system data changes | Work. |
| Structural changes | Work only, as a manual task started from Control under a person's name against an approved plan hash. |
| Request/response with safe code | Serving. |
| Request/response with trusted code | A high-priority Runner run the caller waits on for 10 seconds, then a link to the run. |
| Scheduled and queued | Work, including subscriptions, catalog reads and evaluation. |
| Front-end code | No logins: it calls the governed APIs as the viewer, and acts by asking for a run. |
| Integration and action code | Work. |

### Housekeeping

A server's work on **its own state** (retention, its own snapshot, key rotation) stays in its own process, on one shared housekeeping library (I1). Work on
**customer data** goes to the Runner. So the console needs no runner to keep its own state, and a Runner is never handed a login to another server's state.

### The three paths

1. **Serving to Work:** "run this task", through the link the console made between a Gateway group and a Runner group, with a durable outbox (I4, I5).
2. **Work to Serving:** "data changed", governed reads as a person (subscriptions, the contract guard's question of which columns are served), and publishing
   definitions (I6).
3. **Control to either:** publish, bind, approve, start a manual task (exists today through pairing; I9 makes it the only way to run a structural task).

**A missing partner warns and never refuses.** A Gateway group with no linked Runner group, or the reverse, shows a warning on Monitoring and on the feature that
needs it; neither server refuses to start, and requests wait in the outbox until the partner is there.

### Configuration and backup

- **Fixed shape:** four services, with one Gateway group and one Runner group linked by the console by default (I4).
- **Keys by place:** a Serving key per Gateway group, a Work key per Runner group, and the console's own key. Secrets are sealed to the place that uses them
  (the group keys of plan 28c E20d and E20e are the first of these).
- **One database server per install**, with a database or schema and a login per service and group (I7); SQLite remains the single-machine default.
- **One backup set** (I8): each server snapshots its state through the housekeeping library into one location the console sets; Monitoring shows the snapshot's
  age per service; the console backs up the keys file once, under one passphrase. Restore is one drill.

## What exists (read in the code, 2026-11-16)

- **Retention** is the shared `RetentionJob` (`DbDataKit.State`, over each module's `RetentionRule`s), and a **snapshot** is `StateBackupJob`
  (`DbDataKit.State.Engines.Snapshots`, verified and pruned). Only the console runs them on a schedule, as the system tasks `state-retention` and `state-backup` of
  its embedded runner (`ConsoleSystemTasks`, package `console-system` on the channel `system`, settings in `runner:` of `settings.yaml`). The Gateway reads a
  `retention:` setting in its `state:` section but nothing runs it; the Workspace and the Runner run no retention of their own state; Sync's server prunes runs itself
  and goes with SC23d.
- **The embedded runner** (`ConsoleRunner`, `ConsoleRunnerApi`, `ConsoleRunnerSettings`, the `taskhost/` the console's publish carries) is opt-in and, when on,
  adds the runner's modules to the console's state (`ConsoleState.ModulesFor`). The bindings, releases and promotions screens offer it as the server
  `console-runner`. Plan 28a R20 (owner, 2026-11-14, S10) dropped it, sending the system tasks to an installed Runner; decision 1 below reverses the second half.
- **Evaluation** was a placeholder task of the embedded runner that failed saying it was not built (deleted in I2; I3 evaluates on the Runner).
- **Group keys:** the console makes a Runner group's key and hands it to each joined instance (28c E20d); the Gateway's is E20e.
- **The contract guard** (plan 21 CH06) asks nobody outside a test since SC23c (that slice's default).

## Decisions (owner, 2026-11-16)

| # | Decision | Why |
| --- | --- | --- |
| 1 | **Housekeeping is in-process**, on one shared library, in every server. This reverses part of 28a R20 (S10): the console's retention and backup do not move to the Runner. | A server's own state is its own; handing a Runner a login to it widens what Work can touch. A console alone keeps its state healthy. |
| 2 | **The Store's row-write login stays on Serving.** | Row writes are person-scale and attributed to the person; they need no queue. |
| 3 | **A manual task marked structural accepts a run only from Control** (the Workspace route, which asks the linked Gateways first); the Runner's screen shows it read-only. | A schema change is approved against a plan hash and must not be started from a place that skipped the question. |
| 4 | **The console is the root of trust** (recorded in `docs/architecture.md`). | It pairs every server and makes every group key; nothing else issues trust. |
| 5 | **Trusted request/response waits 10 seconds**, then answers with a link to the run. | Long enough for a small action, short enough that a request does not hold a connection. |
| 6 | **Subscriptions and catalog reads run on Work** and read through path 2 as the subscriber, after I3. | They are scheduled; the read stays governed because it is made as the person. |
| 7 | **A missing partner warns and does not refuse.** | A partial install must still start; the outbox keeps the requests. |

## Slices

| Slice | What | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| I1 | **Status: built (2026-11-16).** **A housekeeping library**: the interval loop, the run record and the settings (retention every, backup every, backup keep), composed into the console, the Workspace, the Gateway and the Runner, each over its own state. **As built:** `StateHousekeeping` in `DbDataKit.State.Engines` (namespace `DbDataKit.State.Engines.Housekeeping`, a hosted service; `Shared/src/DbDataKit.State.Engines/README.md`, "Housekeeping") over the existing `RetentionJob` and `StateBackupJob`; the settings are `state.housekeeping` (`retention_every_minutes`, `backup_directory`, `backup_every_hours`, `backup_keep`) in the shared state section, read, written and carried by the shared reader, writer, resolver, `state move` and `state init` (the Gateway takes them as a map under its `state:` options); each run is a row of the `runs` module under the server's service name (`state-retention`, `state-backup`) with a log line; next due is the last run's start plus the interval. The Workspace's state gained the `runs` module. The console composes it only while its embedded runner is off (with it on, the system tasks still do it until I2). Tests: `StateHousekeepingTests` (State.Engines, 7), `ConsoleHousekeepingTests`, `WorkspaceHousekeepingTests`, `RunnerHousekeepingTests`, `GatewayHousekeepingTests` (each on SQLite, PostgreSQL and SQL Server). **Defaults taken (owner not asked):** retention runs hourly in every server by default (before, only the console's embedded runner ran it), a job that never ran is first due five minutes after the start, no snapshot unless a folder is set, the library is part of `DbDataKit.State.Engines` rather than `DbDataKit.Scheduling` (it needs `StateBackupJob`, which is there, and nothing of the work queue), and every instance of a group runs it under one service name, so they share one schedule | none | M | each server's tests run retention and a snapshot on a short interval and record the run |
| I2 | **Status: built (2026-11-16).** **The console's embedded runner is deleted** (was plan 33 SC23b), with the console's housekeeping running through I1: `ConsoleRunner`, its API, its settings' runner part, `ConsoleSystemTasks`, the `taskhost/` in the console's package, the `console-runner` entry of the bindings, releases and promotions. **As built:** deleted `ConsoleRunner`, `ConsoleRunnerApi` (`/api/embedded-runner`, `/relay/console-runner`), `ConsoleRunnerSettings`, `ConsoleSystemTasks` (with the `evaluation` placeholder), the `runner accept-production|revoke-production` verbs, the task host's build and publish targets in the console's project, the posture setting `accept_production_on_console_runner`, the embedded branches of the bindings, compare, posture list, releases, promotions, relay and Servers list, and `EmbeddedRunnerTests`; the console's state is always its own modules in the install scope, and its housekeeping always runs. Clean break: a `settings.yaml` with a `runner:` section stops the console naming the file and where its settings went (`RemovedEmbeddedRunner`), and `dbdatakit runner ...` answers the same (exit 2, `RemovedCommands`). The Bindings page reads the slots of every paired Runner that answers, Compare starts on the first paired Runner or Gateway. The console tests that used the embedded runner (`ConsoleRunnerBindingsTests`, the runner half of `ConsoleBindingCompareTests`) pair a runner in the test's process (`PairedRunner`: the real engine and routes behind the token check); the Playwright stories of the bindings name a paired runner `runner-1`. Docs: `DbDataKit/README.md`, `DbDataRunner/README.md`, `docs/channel-bindings.md`, `docs/publishing.md`, `docs/ui-editors.md`. **Defaults taken (owner not asked):** the evaluation placeholder goes now, not in I3 (it only failed); the `runner:` section is refused, not ignored | I1 | M | a console with no Runner paired runs retention and backup, and no runner module is in its state |
| I3 | **Status: built (2026-11-16).** **Evaluation as a Runner task**, its results written through the owning server's API. **As built:** the checks of a dataset are evaluated **in the process of the Runner task that loads it**, after its loads, and the Runner, which owns the results, records them: a Build `refresh` task (`RefreshJob`) evaluates the checks of the models whose loads ended `done` with the shared `QualityRunService` over the run's read login (as `--check-results` does, plan 23a FQ09) and writes each baseline read and result through four additive calls of the runner protocol (`QualityBaseline`, `QualityStatus`, `QualityRecord`, `QualityBlocks`; the kit's `EngineQuality`, an `IQualityResults`, which `QualityStore` now implements too), so the task holds no connection to the Runner's state; the Runner writes them with `QualityStore.Record` (the store's block rules) into its group's `quality` module (`QualityModule.GroupModule`, added to `RunnerNames.EngineModules`), **under the run's own id whatever the task says**, and refuses a result the module does not know (status, severity, `on_error`, bounded text) with nothing written. A model a check blocks is held back by what the Runner keeps (`QualityBlocks`, plan 23a FQ14). The Runner answers `GET /api/v1/quality` and `/quality/history` (viewer) and `POST /api/v1/quality/release` (administrator, audited as `runner.quality.release`) in the shared shape (`QualityDocuments`; `RunnerQualityApi` in `DbDataKit.Runner.Api`), the console's relay table has the three rows, and `ConsoleQuality.PathOf` names the Runner, so the console's Quality page (and the lineage panel and the alert watcher, which read the same overview) shows the Runner's results with no screen change. The in-host `QualitySystemTasks` and `QualityTaskRunner` (the system package `quality-system`, which nothing hosted since SC23 and I2) are deleted with their test. Tests: `QualityProtocolTests` (kit, SQLite, PostgreSQL and SQL Server: a task records through the protocol under its run, the refusals, the in-process kit), `ApplyConformanceTests.A_refresh_on_the_runner_evaluates_the_checks_of_the_models_it_loaded_and_the_runner_keeps_the_results` (Build conformance, PostgreSQL and SQL Server: the refresh task fails a not-null check, the Runner keeps the block, fixing the data clears it), `RunnerQualityApiTests` and the rows of `RelayRolesTests` (DbDataRunner), `ConsoleQualityTests` (a console paired with a runner shows its results with the runner named and reads a check's history through the relay) and the rows of `ConsoleRunnerTests`. **Defaults taken (owner not asked):** (1) no separate `quality-system` package: a task of another package would need the files and the read logins bound to the package that loads the dataset, which widens what one task can touch (the rule of this plan), so the evaluation stays in the task that already holds that package's slots, and the name `quality-system` is retired; on-request and scheduled re-evaluation of a dataset (the old task's other triggers) are not built: the overview says `can_run: false` and that the task that loads the dataset evaluates it; (2) the Runner keeps the results per group (`GroupModule`, like its tracking copy), not per install; (3) the release of a block is offered on the Runner (`can_release: true`); (4) the Runner does not check that a result names a dataset the run wrote (a task's identities are its own computation); the run id it stores says which run made each. **Not covered by this row (no row has taken them yet):** the `checks:` of a Sync replication's `task.yaml` (FQ10 was evaluated by the deleted Sync server; a drain does not evaluate them), the `checks:` of a Store model (`model/2`, nothing evaluates them), the `checks` of a Gateway's data sources (`connection/2`, `ConnectionFilesQualitySource`) and of a semantic catalog: each needs its own source on the task that holds the login, or on Serving. **Found, not fixed:** the console's alert watcher (`ConsoleQualityAlerts`) reads the overview through the relay as the signed-in person, and a background pass has none, so it asks no server and sends no alert (the same for the sample keys it reads); it needs an identity of the console's own toward a Runner, which is the trust design (plan 35 I4) and is left there. | SC23 | M | a Runner evaluates a dataset's checks after a refresh, and the console's quality screen shows them |
| I4 | **The console-made link** between a Gateway group and a Runner group, made by default | SC23 | M | a new install's Gateway and Runner are linked with no setting, and Monitoring shows an unlinked group |
| I5 | **Path 1** with an outbox and the 10-second wait | I4 | M | a view action starts a Runner task and returns its result within the wait or a link to the run, and with the Runner down it waits and then runs |
| I6 | **Path 2** for the contract guard's lineage read and `data.changed`; closes SC23c's default that the guard asks nobody | I4 | M | Build `apply_plan` and the Store contract refuse to drop a column a linked Gateway serves |
| I7 | **State logins per service and group** on the one database server | SC23 | M | a Gateway's state login is refused on a Runner's tables, on SQL Server and PostgreSQL |
| I8 | **One backup set**: every server snapshots into the location the console sets, Monitoring shows each snapshot's age, the console backs up the keys file under one passphrase | I1, SC23 | M | the restore drill brings back all four services from one set in the servers suite |
| I9 | **Structural manual tasks accept runs only from Control** (decision 3) | SC23 | S | the Runner's run route refuses a structural task without a Control-signed request, and the Workspace route runs it |

Order: I1, then I2 (inside SC23, in place of SC23b); after SC23, I3, I4, I7 and I9 can go in any order, I5 and I6 after I4, I8 after I1.
