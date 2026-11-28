# RUNNER: the Runner and the engines it runs

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
| RUNNER-1 | Concurrency limit per connection: a second resource per work item claimed together, set as a Runner setting per connection copy | none | M | a task over a connection with limit 1 holds a second task on that connection while another package's task runs |
| RUNNER-2 | The cgroup v2 limits path run somewhere: a CI job on a host that delegates a subtree, or a documented manual run | none | S | the cgroup test runs and passes in that job (or the manual run's result is in the Runner README) |
| RUNNER-3 | A per-package account: on Linux a configured user through a `setuid` helper, on Windows a configured account with its password as a secret reference | none | L | a task runs as the configured account and cannot read the Runner's state or key files; without the choice the package is refused naming it |
| RUNNER-8 | Lineage retention on the Runner (`task_lineage` pruned with the run retention), Store packages' design lineage, the apps host's own graph | none | M | runs older than the retention are gone after a prune; a published Store model's package shows its entities in the console's graph |
| RUNNER-12 | In a group of Runners, a run whose answer the console waits for (**Preview rows**, **Try**) is pinned to the instance that takes the call: that instance queues it for itself only, and the console's call that waits for the answer reaches the same instance, so the rows never leave that instance's memory and the first ask is answered. `docs/authoring-metadata.md` ("What the Runner does with an answer") follows; until it is built, a call that reaches another instance answers `gone` | none | M | a test with a group of two Runner instances behind one address: a `preview` taken by one instance runs there, and the console's answer call returns its rows on the first ask, never `gone` |

## Needs from other subjects

- INSTALLER-22: a Runner under the real Windows service in CI.

## Proposed decisions

None.

## Shared areas touched

- `DbDataKit.Runner*`, `DbDataKit.Scheduling` (work queue), `DbDataKit.Tracking`, `DbDataKit.Lineage`, `DbDataKit.Hosting.Children`.
- `.github/workflows/dbdatasync-ci.yml`, `dbdatarunner-ci.yml`, `docs/formats.json` (`lineage-entries/1`), `docs/ui-editors.md`.
