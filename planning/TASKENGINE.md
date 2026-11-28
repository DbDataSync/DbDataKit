# TASKENGINE: the Runner and the engines it runs

## Goal

The Runner limits work per connection, runs a package under its own account, runs Sync replications on Windows, follows Sync's state on the target,
shows and resolves divergence, keeps run lineage under retention, and its groups are tested for availability and outages.

## What exists today

- The Runner (`DbDataRunner/README.md`, `Shared/src/DbDataKit.Runner/README.md`): packages, limits per package, the memory watchdog over the process tree,
  retries, triggers, groups, staging and activation, posture choices, the operator screens.
- Not built: a concurrency limit per connection; the cgroup v2 path never ran in CI; a per-package account (`PerPackageAccount` exists, nothing switches).
- Sync on the Runner: the drain, bulk loads, verification and `provision` run as tasks; Sync's state on the target (`tracking:`) has writers for SQL
  Server and PostgreSQL; the follower of a Runner-owned Sync to the state database, the persisted divergence and its resolve are not built on the Runner.
- Build tracking onto the follower is built in part; the console shows divergence and the tracking mode; switch-primary is a refusal, not an action.
- The Sync integration CI job takes about 39 minutes with a 45-minute limit.
- Run lineage: the Runner keeps `task_lineage` with no retention; Store packages emit no design lineage; the apps host emits no graph of its own.
- `OutageTests` cover the console and a Gateway's store; no test of two Gateway instances behind a balancer; no outage rows for the Workspace and the Runner.

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| TASKENGINE-1 | Concurrency limit per connection: a second resource per work item claimed together, set as a Runner setting per connection copy | none | M | a task over a connection with limit 1 holds a second task on that connection while another package's task runs |
| TASKENGINE-2 | The cgroup v2 limits path run somewhere: a CI job on a host that delegates a subtree, or a documented manual run | none | S | the cgroup test runs and passes in that job (or the manual run's result is in the Runner README) |
| TASKENGINE-3 | A per-package account: on Linux a configured user through a `setuid` helper, on Windows a configured account with its password as a secret reference | none | L | a task runs as the configured account and cannot read the Runner's state or key files; without the choice the package is refused naming it |
| TASKENGINE-4 | A replication on a Windows Runner: the Windows job installs SQL Server and PostgreSQL and runs `SyncOnRunner` | INSTALLER-22 | M | the Windows job runs a drain on the Runner against both engines |
| TASKENGINE-5 | Split the Sync integration CI job: engine suites in one job, pairs and Runner suites in another, each starting only its containers (re-measure first) | none | S | each job finishes in under 25 minutes with a limit of 30; the union of the filters is the old filter |
| TASKENGINE-6 | Sync's state on the target for a Runner-owned Sync: the follower to the Runner's state, the persisted divergence and its resolve, retention, the console's Sync half | none | L | Sync conformance on SQL Server and PostgreSQL: a restored target shows diverged with the common point; resolve archives the copy's later rows |
| TASKENGINE-7 | Build tracking, the rest, and the effective tracking mode shown per connection and replication; switch-primary as an action | TASKENGINE-6 | M | Playwright: a restored target shows diverged; switch-primary moves the primary and is audited; it refuses while a side is down |
| TASKENGINE-8 | Lineage retention on the Runner (`task_lineage` pruned with the run retention), Store packages' design lineage, the apps host's own graph | none | M | runs older than the retention are gone after a prune; a published Store model's package shows its entities in the console's graph |
| TASKENGINE-9 | High availability in CI: two Gateway instances behind a balancer; one stopped during a query run | none | M | queries and sign-ins continue; the Flight SQL client reconnects to the other instance |
| TASKENGINE-10 | Outage tests for a serving instance, the Workspace and the Runner (the outage table in `docs/serving-layer.md`) | TASKENGINE-9 | M | `OutageTests` rows: with each stopped, what the table says keeps working keeps working |
| TASKENGINE-11 | Manual lineage entries (a file `lineage-entries/1`, then a console form), OpenLineage export, tracing a value from a result back to its sources (queued, low priority) | none | L | each: the entry is drawn apart in the graph; an export validates against OpenLineage's schema; a dashboard cell opens its path |
| TASKENGINE-12 | In a group of Runners, a run whose answer the console waits for (**Preview rows**, **Try**) is pinned to the instance that takes the call: that instance queues it for itself only, and the console's call that waits for the answer reaches the same instance, so the rows never leave that instance's memory and the first ask is answered. `docs/authoring-metadata.md` ("What the Runner does with an answer") follows; until it is built, a call that reaches another instance answers `gone` | none | M | a test with a group of two Runner instances behind one address: a `preview` taken by one instance runs there, and the console's answer call returns its rows on the first ask, never `gone` |

## Needs from other subjects

- INSTALLER-22: a Runner under the real Windows service in CI.

## Proposed decisions

None.

## Shared areas touched

- `DbDataKit.Runner*`, `DbDataKit.Scheduling` (work queue), `DbDataKit.Tracking`, `DbDataKit.Lineage`, `DbDataKit.Hosting.Children`.
- `.github/workflows/dbdatasync-ci.yml`, `dbdatarunner-ci.yml`, `docs/formats.json` (`lineage-entries/1`), `docs/ui-editors.md`.
