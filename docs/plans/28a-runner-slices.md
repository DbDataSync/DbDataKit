# Plan 28a: the runner in slices

Status: **proposed** (2026-11-07). [Plan 28](28-runner.md) says *what* the runner is and what the owner decided; this plan says *in what
pieces* it is built, what exists to build on, and what test proves each piece. It follows the shape of [13a](13a-suite-slices.md) and
[29a](29a-state-slices.md): one slice, one branch `suite/r<nn>-<short-name>`, one pull request, "done" is the test in the last column,
and the status goes in the slice's row in the same change.

The point of this plan is to **unblock**: Q20's `runner` module, Q21 and Q22 (Sync and Build on modules), Q25 (deleting the old state
path), and plan 30's runner kinds all wait on the runner. The slices are ordered so that each unblocked piece appears as early as it can.

## 1. What exists to build on

| Piece | Where | What the runner takes from it |
| --- | --- | --- |
| Schedules, misfire, pause | `DbDataKit.Scheduling` (`Schedule`, `ScheduleEvaluator`, `RunGate`) | used unchanged to decide when a task is due |
| Work queue, leases, run lock, job state, pause | `SchedulingModule`, `ModuleWorkQueueStore`, `ModuleStores` (module `scheduling`) | the queue and leases are the runner's: a runner claims an item with a lease, any runner of the group may take it |
| Run history, logs | `RunStore`, `RunsModule` (module `runs`) | every task run is a run; logs are `Log` lines; the run records package, version and channel (R04 adds the columns) |
| Progress | `ProgressStore`, `ProgressMigration` (module `runner_progress`, Q20) | the `Advance`-with-outcome transaction, history, rewind, ledger, idempotency, dead letters, the planner; the runner wires them to the protocol and to staging |
| Notifications, audit, payloads, retention | `NotificationFeedStore`, `AuditLog`, `PayloadStore`, `RetentionJob` | alerts on failure, recorded choices, inbox bodies and artifacts |
| Child processes | `DbDataKit.Hosting.Children` (`ChildProcessHost`: per-launch token and endpoint in the environment, Windows job object with memory and CPU caps, kill on dispose, parent-death monitor, restart policy) | launching a task process; Linux limits are new (R09) |
| Sync's TaskRunner | `DbDataSync.TaskRunner` (`RunExecutor`, `WorkerEntry`, `WorkerLanes`) | the precedent for lanes and one process per run; Sync keeps it until plan 28 section 8 |
| Scripting | `DbDataKit.Scripting` (`CsxCompiler`, `PackageResolver`, lock files) | used at **publish** time, not on the runner (plan 30 section 6.1) |
| Definitions | `DefinitionStore` (module `definitions`) | versions, channels, events, follower; plan 30 widens it (stage, artifacts, bindings, instance status) |
| Connections and secrets | `DbDataKit.Connections`, the secret store | resolved by the runner and handed to the task by handle, never written to disk |
| Hosting | `DbDataKit.Hosting.*`, `Hosting.Cli` | service install, certificates, `service`/`cert` verbs for `dbdatarunner` |
| Pairing, roles, sign-in | `DbDataKit.Web` (trust, `GroupTrust`, sessions, policies) | the runner's management API is a server in plan 13's shape, like DbDataStore |
| Lineage | `DbDataKit.Lineage` | a run's declared reads and writes become edges |

What has to be new: the **package model and manifest**, the **protocol** and its two ends, the **executor** (process, limits, retries,
timeouts, cancellation), the **trigger engine**, the **runner group** (instances, labels, capabilities, posture choices), the **host and
its API**, and the **publishing** of the `integration` kind (plan 30).

## 2. Shape of the code

```
DbDataKit/src/DbDataKit.Runner.Abstractions   package manifest model, task model, protocol messages (the .proto), outcomes, classified failures
DbDataKit/src/DbDataKit.Runner                the engine: scheduler loop, trigger engine, executor, limits, retries, progress wiring, staging checks
DbDataKit/src/DbDataKit.Runner.Kit            the task side for .NET (typed progress accessors, connections, logs, cancellation); wraps the protocol
DbDataKit/src/DbDataKit.Runner.TaskHost       the process the runner starts for a .NET package: loads the assembly, runs the entry point through the kit
DbDataRunner/src/DbDataRunner.Server          the host: `dbdatarunner server`, management API, group membership, pairing
DbDataRunner/src/DbDataRunner.Cli             commands (`state grants`, `service`, `cert`, `package`, `run`, `label`)
DbDataRunner/tests/...
```

The engine is a library so the console embeds a copy of it (plan 28 section 6a, slice R20) and later Sync and Build host the same engine.
A new product folder `DbDataRunner/` is added to `CLAUDE.md`'s table in R10 with `DbDataKit.slnx`, `DbDataRunner.slnf` and its own props
(plan 13 shape).

## 3. Decisions made while planning

Marked **default** where the plan picks one so work can go on; the owner may change any in the slice that is wrong.

| # | Decision | Why |
| --- | --- | --- |
| DR1 | **The runner knows two ways to start a task: a .NET assembly (through `TaskHost`) and an executable.** A `.csx` script and a compiled project are both published as compiled assemblies (plan 30 section 6.1), so on the runner they are one kind. A notebook is a later third (plan 28 question 3). | the runner never compiles; fewer kinds to test; compiled kinds differ only at publish time |
| DR2 | **The manifest is a `package/1` document** read by `DbDataKit.Config` (the `<name>/<n>` format rule), with strict reading and typo suggestions as `state/1` has. A file in another format is refused naming the file. | one reading rule across the suite; clean break rule of plan 13 |
| DR3 | **One gRPC server per runner, not per task**, on a Unix domain socket (Linux) or a named pipe (Windows) in a 0700 directory owned by the runner; a task authenticates with a **per-run token** passed in its environment (not argv), the way `ChildEnvironment` already does it. The token names the run, so a task can touch only its own run's progress and logs. | plan 28 section 4.1 asks for a local socket; one Kestrel listener is cheaper than one per task and the token handshake exists |
| DR4 | **The protocol is `dbdatakit.runner.v1`**, a `.proto` in `Runner.Abstractions`, versioned in its package name; an incompatible change is a `v2` served beside it until no published package uses `v1`. The kit sends its protocol version at start and the runner refuses a task it cannot serve, naming the version. | packages outlive runner upgrades (plan 24 N and N-1) |
| DR5 | **A task's progress is read once at start and written at commit points** through the protocol (`Advance` with the outcome, plan 29 section 4.1 rule 1). The process never holds a database connection to the runner's state. | the task process runs under a low-privilege account; state credentials stay with the runner |
| DR6 | **Process limits:** Windows job objects (built); Linux **cgroup v2** when the runner's account has a delegated subtree, otherwise a **watchdog** that samples resident memory and kills the process over the limit. The runner reports which one it is using as a capability ("memory limit: enforced / watched"), and the console shows it. | cgroup delegation is not always there; plan 28 section 5 says report, do not refuse |
| DR7 | **Concurrency limits are claims, not counters:** a limit per package and per connection is a `Resource` on the work item (`WorkQueueStore` already refuses two items with the same resource), with a count when more than one is allowed (R07 extends `Resource` to a pool of N). | no second source of truth to drift from the leases |
| DR8 | **Retries are runs of the same work item**, with the attempt number recorded on the run; back-off for a failure a connection classifies as temporary (`ConnectionFailure`), none for a classified permanent one. A retry never runs on a different package version than the first attempt unless the first never started. | one run record per attempt keeps history honest |
| DR9 | **Triggers write work items**; none of them runs a task directly. After-run, webhook (inbox item), file arrival, DbDataStore change and a serving-layer action all enqueue, so the lease and the limits apply to every start. | one path to start a task |
| DR10 | **The `runner` module holds what is about runners**, not what is about packages: `instance`, `instance_label`, `capability`, `posture_choice`, `task_projection`, `trigger_state`. Package versions, artifacts, bindings and per-instance preparation status are plan 30's, in `definitions`. | plan 30 section 3 already puts versions there |
| DR11 | **A `task_projection` row per (package, channel, task)** is rebuilt from the active manifest at activation: schedule, triggers, limits, required labels, progress declaration hash. The scheduler loop reads only the projection and never parses manifests. | a bad manifest cannot stop the loop; staging validates once |
| DR12 | **The runner is its own product folder, `DbDataRunner/`**, executable `dbdatarunner`, server on plan 13's shape like DbDataStore. | plan 28 decision 2 |
| DR13 | **Package artifacts live in the payload store** (plan 29 section 3.6) and a runner **materializes a version to a local cache directory by hash** before the first run, verifying every hash; a run uses a read-only copy. | content-addressed files are cached trivially; what runs is what was approved |
| DR14 | **A task's account is the runner's low-privilege account** (default). A per-package account is an option an administrator turns on (a recorded choice, R16); on Linux that is a configured user the runner may `setuid` to through a helper, on Windows a configured account with its password a secret reference. Not built before R16. | plan 28 question 6 |

## 4. Questions for the owner

None blocks R01 to R15. Each has a default; answer when convenient.

| # | Question | Default |
| --- | --- | --- |
| QR1 | Does a task's process get **its own working directory** that the runner deletes after the run (scratch space), with a size limit? | yes, `scratch` under the runner's data directory, 1 GiB default, cleaned after the run; a package asks for more |
| QR2 | **Output a task produces** (files, large results): kept as run payloads under retention, or only named in the run's outputs? | payloads under retention (30 days default), the run's outputs name them |
| QR3 | **How long may a runner be silent before its leases are taken?** | a 30 second heartbeat and a 90 second lease, both settings |
| QR4 | Is `partly succeeded` the existing `CompletedWithFailures`, with counts of failed items, or a new outcome? | the existing one; the protocol's end message carries counts and a first failed item |
| QR5 | **Manual runs by someone with the Operator role**: do they need a confirmation for a production channel? | no confirmation, audited; the console may add one later |

## 5. Milestones

| Milestone | Slices | What it gives |
| --- | --- | --- |
| **R-A: the model** | R01 to R04 | the manifest, the task model, the protocol, the `runner` module: nothing runs yet, but Q20's remainder is unblocked |
| **R-B: running a task** | R05 to R10 | one runner runs a published task in a process with limits, retries and progress; `dbdatarunner` exists |
| **R-C: a group** | R11 to R14 | several runners share a database; leases, labels, capabilities, triggers; Q21 and Q22 can start on the library parts |
| **R-D: published to** | R15 to R19 | plan 30's publishing for the `integration` kind; staging with the progress migration dry run; the console shows runners |
| **R-E: the embedded runner and the engines** | R20 to R24 | the console's runner; Sync's and Build's engines as packages; Q22 and Q25 can finish |

## 6. The slices

Size: S under a day, M a few days, L about a week. A slice that needs plan 30 says so.

### R-A: the model

| Slice | What | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| R01 | **The package manifest**, `package/1`, in `Runner.Abstractions`: package name, tasks (entry point, schedule, triggers, inputs, connection slots, progress declaration, limits, required labels, capabilities), launch kind (DR1), the .NET contract versions it was built against. A reader with strict keys and suggestions; a validator that says what is wrong in words | none | M | a valid manifest round-trips; each invalid case (unknown key, bad cron, a slot used but not declared, a progress declaration with two values of one name, a limit of zero) fails naming file and key; a manifest of another format version is refused naming the file |
| R02 | **The task and run model:** `TaskRef` (package, channel, task), the start message, the classified failure set (reuses `ConnectionFailure`), `RunEnd` (outcome, counts, first failed item, outputs), and the mapping onto `RunOutcome` and `WorkItem` (QR4) | R01 | S | the mapping tests: every end state has exactly one `RunOutcome`; an outcome the store cannot represent fails the build, not a run |
| R03 | **The protocol `dbdatakit.runner.v1`**: the `.proto` for start, log, metric, progress read and `Advance`, state get/set, ledger, idempotency, dead letter, inbox take, lineage record, heartbeat, cancel, end; `ProtocolVersion` check; the **server side** (`RunnerProtocolService`) over the progress, run and payload stores, bound to a **run token** (DR3, DR5); the **client side** in `Runner.Kit` over a socket or pipe | R02, Q20 | L | an in-memory client and server run a whole task conversation on all three engines; a client with another run's token is refused on every call; **the checkpoint-moves-with-the-outcome test** through the protocol (a failed run leaves the checkpoint, a second overlapping run is refused); the `.proto` has a test that fails on a removed or renumbered field |
| R04 | **The `runner` module** (`runner`, per group, DR10): `instance`, `instance_label`, `capability`, `posture_choice`, `task_projection`, `trigger_state`; the `runs` module gains `package`, `package_version`, `channel`, `attempt` columns (a new migration, not an edit) | Q03 | M | the module's convention checks, retention rules and layout tests on all three engines; a migration test from the `runs` of Q12 keeps its rows |

### R-B: running a task

| Slice | What | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| R05 | **Materialize a version** (DR13): an artifact cache by hash, hash verification, read-only copy per run, eviction of versions no channel serves | R04, payloads | M | a tampered file is refused naming the path and hashes; two versions sharing a file store it once; a run's copy cannot be written by the task |
| R06 | **The executor:** start a task process through `ChildProcessHost` for a .NET assembly (`TaskHost`) or an executable; scratch directory (QR1); environment with token and endpoint; time limit and cancellation with a grace period; exit-code and end-message reconciliation (a process that exits without an end message is `Failed` with the exit code, one that ends and then crashes keeps its outcome); output lines become run log lines, redacted by the shared redactor | R03, R05 | L | a task that succeeds, fails, is cancelled, times out, exits silently, crashes after its end message, and prints a secret (redacted in the log): each has a test; **no secret in argv or on disk** is a test that reads `/proc/<pid>/cmdline` and the scratch directory |
| R07 | **Limits and concurrency:** memory and CPU through the job object (built) and, on Linux, cgroup v2 or the watchdog (DR6); concurrency limits by package and by connection as pooled resources (DR7); the capability report | R06 | M | an allocating task is killed over its limit on Linux (watchdog) and, where the CI runner allows, under cgroup; limit 2 on a package lets two run and holds the third; the capability says which enforcement is in use |
| R08 | **Retries and timeouts** (DR8): per-task policy, attempt recorded, back-off for temporary failures, none for permanent; a run in flight when its version is replaced finishes on the old one | R06 | M | a task that fails twice with a temporary failure then succeeds is three runs of one item; a permanent failure is one run; the third attempt after a version switch keeps the first attempt's version |
| R09 | **The scheduler loop:** reads `task_projection`, evaluates `Schedule` against `job_state`, honours `job_pause` and misfire, writes work items; claims with a lease and runs through the executor; the run lock (a task never overlaps itself unless its manifest says so) | R04, R06 | L | the matrix of 13a's M03 tests, run against the runner's loop (cron in two time zones across a daylight change, interval, misfire skip and run-once, pause, overlap refused); a loop whose projection has a broken row skips that row and reports it, and runs the others |
| R10 | **`dbdatarunner`, a single runner:** the product folder, props, `slnx` and `slnf` entries, the host on plan 13's shape (settings, state open through `StateOpener`, the `state grants` verb, `service` and `cert` verbs), the first management API (`GET /api/v1/status`, runs, `POST .../run-now`, pause and resume), `CLAUDE.md` table and the product's own `CLAUDE.md` | R09 | L | `dbdatarunner server` on SQLite runs a sample package on its schedule end to end, and the same on PostgreSQL and SQL Server; the status call reports capabilities and the state engine; `scripts/check_solution.py` passes |

### R-C: a group

| Slice | What | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| R11 | **Group membership:** an `instance` row with heartbeat (QR3), `lease` expiry takes an instance's items back, an instance that returns without its lease is told so, the group's name and the scope (`StateScope.Group`) | R10 | M | two runners on one database: killing one hands its running item to the other once the lease expires; a revived runner does not finish the item it lost (its `Advance` is refused by `row_version`) |
| R12 | **Labels and routing** (plan 28 question 7): administrator labels, reported capabilities as read-only labels (`network: outside`, `os: linux`), a task's `requires` list, a work item claimed only by a runner with every label; a version whose required label no runner has is **not activatable** and says which | R11 | M | a task requiring a label no runner has stays pending with a reason the status call shows; adding the label to a runner starts it; the activation refusal (once R17 exists) names the label |
| R13 | **Triggers:** after-run (outcome filter), webhook through the `inbox_item` (the serving layer's hand-over is Q-side: a test double stands for it), file arrival (poll a connection's folder, recognise a re-sent file with the ledger), by hand; every one writes a work item (DR9) | R09, Q20 | L | a chain A then B on one runner; a webhook body becomes an inbox item and a task takes it exactly once, also with two runners; a file seen twice runs once; a trigger loop (A triggers B triggers A) is refused at staging naming the cycle |
| R14 | **Posture choices** (plan 28 section 5): `posture_choice` rows for run-in-process, longer limits, a more privileged account, recompile on instance; every one needs a confirmation that names the effect, writes audit, and shows in the status; a default never needs one | R10 | S | each loosened default without a recorded choice is refused with the message that names what it would change; with a choice it works and the audit entry names who and when |

### R-D: published to

Plan 30 has no slices yet. R15 is the first of them and covers its library part; the rest of plan 30 (the serving side) should get its own slice document.

| Slice | What | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| R15 | **Plan 30 library part:** `definitions` migration to `version`, `version_artifact`, `serving` (active, previous, staged), `binding`, `instance_status`, `event`; the registry of kinds with validator, slots, target types and default mode; **publish, stage, activate, switch, rollback, retire** with `expected_version`; idempotent publish; the same for the serving kinds already in use (clean break with a message naming the database) | Q07, R04 | L | the life-cycle test for one serving kind and one runner kind on all three engines; the same content again changes nothing; a promotion with an unbound slot is refused naming it; `depends_on` not active in the channel is refused naming it |
| R16 | **The publish API on the runner:** `HEAD/PUT /api/v1/artifacts/{sha256}`, `PUT /api/v1/definitions/integration/{name}`, activate/switch/rollback/retire, bindings, status and events; pairing as a Publisher with `OwnerScope`; signatures (plan 30 section 6.2) verified at stage | R15, R10 | L | a publisher pairs, uploads a changed file only (a second publish sends one artifact), stages, and a second runner prepares it; a hash mismatch upload is refused; an unsigned version is refused where signatures are required, naming the missing signature |
| R17 | **Staging on the runner:** `DefinitionFollower` for kind `integration`: materialize (R05), check contract versions (a compiled package built against a contract the runner lacks fails staging with a message asking for a republish), validate the manifest, **dry-run the progress migration** (`ProgressMigration.Plan`) with the loss list, report `ready` or `failed` per instance; activation when every instance is ready, or by an administrator anyway (a recorded choice); projection rebuilt at activation | R16, R12, Q20 | L | a package whose declaration loses a value stages with the loss listed and activates only after confirmation; one instance failing keeps the group on the old version and the status names the instance and why; **rollback restores the progress** from `checkpoint_history` |
| R18 | **Activation waits for runs in flight** per task, migrates that task's progress in one transaction with the switch (plan 30 section 3.5, plan 29 section 4.3 step 6) | R17 | M | a run in flight finishes on the old version, then the migration applies once, then the next run is on the new version; two runners activating at once migrate once |
| R19 | **The console shows runners:** a monitoring view of the runner group (instances, capabilities, labels, runs, health), pause and run-now acting on the runner, posture choices and the activation confirmation; reuses M11 | R17, Q19 | M | the console pairs with a runner, lists its runs, pauses a task and runs it now, and shows an instance that is gone |

### R-E: the embedded runner and the engines

| Slice | What | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| R20 | **The console's embedded runner** (plan 28 section 6a): the engine hosted in the console, group `console`, scope on the console's state, non-production channels and its own task kinds only, a cap on concurrent runs, an administrator's recorded choice to accept production, the warning that schedules stop when the console does | R10, R14 | M | a production package is refused by default with the message; a development package runs; the cap holds; the console's housekeeping runs as tasks of it |
| R21 | **Sync's engine as a package:** `RunExecutor` and the lanes hosted as an `executable`/assembly package with Sync's replication definitions as a kind; the TaskRunner becomes a task | R10, R18 | L, split by a plan of its own | a replication runs on the runner with the same result as Sync's own TaskRunner on the integration suites |
| R22 | **Build's executor as a package** (the child-process commands of D12), refresh plans as a kind | R10, R18 | L | a Build refresh runs on the runner; `max_parallel` is the package concurrency limit |
| R23 | **Sync's and Build's state onto modules** (Q21 and Q22 then proceed): watermarks as declared checkpoints, tracking as `build_tracking` with the follower | R21, R22, Q21 | L | the 29a Q21 and Q22 "Done when" tests |
| R24 | **Retire the old paths** (Q25) once no product uses `StateSchema` | R23 | M | the 29a Q25 test |

## 7. What each slice unblocks

| Waiting work | Unblocked by |
| --- | --- |
| Q20's `runner` module and protocol, manifest reading, typed accessors, rollback restore | R01 to R04, R03 for the protocol; rollback restore in R17 |
| Q21 (`build_tracking` library parts) | nothing here for the library part; the follower and a running Build need R22 |
| Q22 (Sync on modules) | R21, R23 |
| Q25 (delete the old state path) | R24 |
| Plan 30's slices | R15 is the first of them |
| Plan 27's DbDataIntegration | R10 for a first run, R17 for publishing |
| Plan 27's evaluation runs | R20 |
| Webhooks (plan 28 section 7) | R13 for the runner's end; the serving layer's end is its own slice in plan 20 |

## 8. What can start now

R01, R02 and R04 need nothing that is not built. R03 needs Q20's progress store, which is. R05 needs only the payload store. So **R-A and
R05 can start at once**, and **R01 then R03 then R06** is the shortest path to a task that runs in a process and moves its checkpoint
with its outcome, the first thing Q20's remainder and Q22's design need to see working.

## 9. Risks

- **Linux limits** (DR6): cgroup v2 may be unavailable to the CI container; the watchdog is the tested path, cgroup is tested where it can be.
- **gRPC over a named pipe** on Windows needs Kestrel's named-pipe transport; R03 proves it on Linux first, and the Windows run is in CI only.
- **Manifest drift:** the manifest, the progress declaration and the kit's generated accessors must agree; R01 gives the declaration one model, shared by R03 and the migration planner.
- **Scope of R21 and R22:** each is "L, split by a plan of its own"; they must not start before R10 runs real packages, because they are where the engines' assumptions meet the runner's.
