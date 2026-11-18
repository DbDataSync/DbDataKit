# TRUSTZONES: isolation by trust, the paths between places

## Goal

Work is placed by what it can touch (`docs/architecture.md`, "Isolation by trust"). The console links each Gateway group with a Runner group. Serving
asks Work to run a task through a durable outbox and waits 10 seconds for the answer (path 1). Work tells Serving that data changed, publishes to it and
reads through it as a person (path 2). Structural tasks run only when Control starts them. Every service and group has a state login of its own.

## What exists today

- Built: the housekeeping library in every server (`StateHousekeeping`), the console's embedded runner deleted, evaluation of a Build model's checks
  in the refresh task and the `check` task (`QualityCheckTask`).
- The console makes a Runner group's key and hands it at pairing; a Gateway group makes its own (INSTALLER-15 changes that).
- No link between a Gateway group and a Runner group. The Runner tells Gateways that data changed through its own code pairing
  (`dbdatarunner gateway add --code`, `gateways.json`, `GatewayNoun` and `GatewayPairings` in `DbDataKit.Serving.Notify`, `RunnerDataChanges.cs`).
- A view's `task` action (`DbDataKit.Views`) answers "not connected": the Gateway cannot start a Runner task.
- The Runner's webhook end exists (`POST /api/v1/webhooks/...`, the inbox, replay refusal); the Gateway does not receive webhooks.
- The contract guard (`ContractGuard`, `DbDataKit.Lineage`): the Workspace asks the paired Gateways for their lineage before it runs a Store model's
  contract (`WorkspaceManagement.cs`, the `.../contract` route). Build's `apply_plan` on the Runner asks nobody.
- The console has no identity of its own toward a Runner: its quality alert watcher reads as the signed-in person and a background pass asks nobody.
- All servers of an install can share one state database login.
- The report-catalog snapshot sink over plan 30's route is built as a library only (`DefinitionsClientSnapshotSink`).

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| TRUSTZONES-1 | The console-made link between a Gateway group and a Runner group, made by default: each side gets the other's addresses and a link credential scoped to the three paths; a missing partner warns on Monitoring; a new instance of a linked group gets the link at its join. The console gets an identity of its own toward a Runner (read only: runs, quality) | INSTALLER-15 | M | servers suite: a new install's Gateway and Runner are linked with no setting; Monitoring shows an unlinked group; the console reads a Runner's runs with no person signed in |
| TRUSTZONES-2 | State logins per service and group on the one database server: each server's state login can reach only its own schema or database | none | M | SQL Server and PostgreSQL: a Gateway's state login is refused on a Runner's tables and the reverse; `state grants` prints one login per service and group |
| TRUSTZONES-3 | Structural manual tasks accept a run only from Control (a Control-signed request); the Runner's screen shows them read only | none | S | the Runner's run route refuses a structural task without a Control-signed request; the Workspace route runs it |
| TRUSTZONES-4 | Path 1: the Gateway's outbox of run requests to its linked Runner group, the high-priority run, the 10-second wait, then an answer with a link to the run; requests wait in the outbox while the Runner is down | TRUSTZONES-1 | M | a test: a request returns the run's result within the wait; with the Runner down it is kept, then runs once when the Runner is back; a request past the wait answers with the run's link |
| TRUSTZONES-5 | A view's `task` action on path 1: the Runner keeps start grants (package, task, channel, client, who granted), the action names the viewer and an idempotency key, the console's runner task panel and the Gateway's Links panel edit grants and links. Who may start a task from a view is three layers, each in its own place: the app's access rules show the link, the app manifest lists the tasks it may name (`runner:`), and the Runner's grant says which client may start which task in which channel; the person is recorded, never given a Runner role | TRUSTZONES-4 | M | vitest and a Playwright story: an administrator grants a task to the linked Gateway, a click on a view's action starts it as the viewer and shows the result or the link; revoking the grant makes the next click refuse; a task the manifest does not list is refused before the Runner is asked; a person whose access rules hide the link does not see it |
| TRUSTZONES-6 | Webhooks on Serving: the Gateway receives a signed webhook at an address per package and source, checks the signature, refuses replays and oversize bodies, and hands the body through path 1 to the Runner's inbox | TRUSTZONES-4 | M | a signed webhook to the Gateway becomes an inbox item a task takes exactly once; a replay is refused with nothing queued; the Runner has no inbound address outside |
| TRUSTZONES-7 | Path 2 for `data.changed`: the Runner tells its linked Gateways over the link. Deletes the Runner's code pairing with Gateways (`gateway add --code`, `gateways.json`, `GatewayNoun`, `GatewayPairings`, `GatewayPairingApi`); voids the `docs/ui-editors.md` row "Serving-group pairings of a product server" (updated in the same change) | TRUSTZONES-1 | M | a refresh on the Runner clears the linked Gateway's cache for that dataset with no pairing made by hand; `dbdatarunner gateway add` answers the sentence |
| TRUSTZONES-8 | Path 2 for the contract guard: whoever asks (TRUSTZONES-P1), Build's `apply_plan` and a Store model's contract refuse to drop a column a linked Gateway serves | TRUSTZONES-1 | M | Build conformance and the Workspace's tests: `apply_plan` and the Store contract are refused naming the consumer and the Gateway; with `allow-consumer` they run |
| TRUSTZONES-9 | Path 2 for publishing from Work: a Runner task publishes a definition to its linked Gateway group as the link's publisher; the report-catalog snapshot (`DefinitionsClientSnapshotSink`) is the first user | TRUSTZONES-1 | S | a Runner task publishes a `report-catalog` definition into the linked group's channel; the same bytes publish nothing; a failing group keeps the last snapshot and says why |
| TRUSTZONES-10 | Path 2 governed reads: a Work task reads through the linked Gateway as a named person (for subscriptions and catalog reads), with that person's rules | TRUSTZONES-1 | M | two subscribers with different row rules read different rows through the link; a person with no access gets none |
| TRUSTZONES-11 | A Gateway group shown as one node with its instances on the console, and a group's data sources as the group lists them | TRUSTZONES-1 | S | Playwright: a group of two instances is one node listing both |
| TRUSTZONES-12 | Every secret value and login has an optional expiration date: stored with the secret (the shared stores, the group stores, the console's store, connection logins), shown and edited where the secret is, warned of before it expires; a run that uses an expired login fails naming it (owner, 2026-11-15; queued) | none | M | tests on each store: an expiry round-trips; a warning is raised N days before; a task using an expired login fails naming the secret |

## Needs from other subjects

- INSTALLER-15: the console makes every group key and hands it at the join; TRUSTZONES-1 carries the link in that join.

## Proposed decisions

### TRUSTZONES-P1: who asks the Gateways before a column is dropped (awaiting the owner)

Today the Workspace (Control) asks the paired Gateways before a Store contract; Build's `apply_plan` asks nobody. Option A, "Control asks": the Workspace
asks the linked Gateways before it starts `apply_plan` or a contract, and the Runner runs only a Control-signed structural task (TRUSTZONES-3). Option B,
"Work asks": the Runner task asks its linked Gateways over path 2 before it runs the DDL. Scenario: an engineer approves a Build plan that drops
`orders.discount`, which a published dashboard reads. Under A the Workspace refuses before the task is queued and the person sees the consumer on the
build's page; under B the task is queued, asks, and fails, and the refusal is in the run's log. A run started from the Runner's own screen is refused under A
by TRUSTZONES-3 and checked under B. Recommendation: A, because a structural task is already a Control action against an approved plan hash.

### TRUSTZONES-P2: the link credential (default, awaiting the owner)

The console signs a link credential per pair of groups, scoped to the three paths, rotated with the group keys. Scenario: a Gateway instance is stolen;
revoking its group key also revokes its link. Recommendation: accept.

## Open designs

Not scheduled. Each becomes tasks only when the owner schedules it.

### Admin lock-down settings

**What.** Settings an administrator may turn on later to restrict risky actions that the suite allows by default with a clear warning
(`docs/architecture.md`, "Risky actions: allowed, with a clear warning"). The first is production reads for authoring: discovery and describe in
`prod` (`docs/authoring-metadata.md`), which anyone with access to the project may run after a production warning. Others follow as
they are found (a run started in `prod` from a draft, a structural task, overwriting another person's file).

**Why.** Some organisations' rules require that only named roles touch production, even to read its shape. The default stays open, so the suite is not
crippled for everyone else; a setting is a hardening follow-up an administrator chooses.

**Open questions.** Where a setting lives (the console's installation settings, a Runner group, a channel, a project); whether it restricts to a role
(the project's approvers, an administrator) or turns the action off; which server enforces it (the Runner, which runs the read, so a caller cannot skip
it, and the Workspace, which hides or explains the button); how a refused person learns who may do it; and that the audit records a change of the setting.

## Shared areas touched

- `DbDataKit.Web` (link credential), `DbDataKit.Serving.Notify` (deleted pairing), `DbDataKit.Runner.Api` (start grants, run route), `DbDataKit.Views`
  (the `task` action), `DbDataKit.Lineage` (`ContractGuard` callers), `DbDataKit.ReportCatalogs`, `DbDataKit.State` (logins, grants),
  `DbDataKit.Secrets.*` (expiry).
- `docs/architecture.md`, `docs/ui-editors.md`, a state migration of the Runner's and the console's modules (`scripts/check_state_migrations.py`).
