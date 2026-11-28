# TRUSTZONES: isolation by trust, the paths between places

## Goal

Work is placed by what it can touch (`docs/architecture.md`, "What runs where"). The console links each Gateway group with a Runner group. Serving
asks Work to run a task through a durable outbox and waits 10 seconds for the answer (path 1). Work tells Serving that data changed, publishes to it and
reads through it as a person (path 2). Structural tasks run only when Control starts them. Every service and group has a state login of its own.

## What exists today

- Built: the housekeeping library in every server (`StateHousekeeping`), the console's embedded runner deleted, evaluation of a Build model's checks
  in the refresh task and the `check` task (`QualityCheckTask`).
- The console makes every Gateway and Runner group's key and hands it in the join's answer (`ConsoleJoinGroups`); the answer has a place for the group's link (`JoinAnswer.Link`), empty for now.
- No link between a Gateway group and a Runner group. The Runner tells Gateways that data changed through its own code pairing
  (`dbdatarunner gateway add --code`, `gateways.json`, `GatewayNoun` and `GatewayPairings` in `DbDataKit.Serving.Notify`, `RunnerDataChanges.cs`).
- A view's `task` action (`DbDataKit.Views`) answers "not connected": the Gateway cannot start a Runner task.
- The Runner's webhook end exists (`POST /api/v1/webhooks/...`, the inbox, replay refusal); the Gateway does not receive webhooks.
- The contract guard (`ContractGuard`, `DbDataKit.Lineage`): the Workspace asks the paired Gateways for their lineage before it runs a Store model's
  contract (`WorkspaceManagement.cs`, the `.../contract` route). Build's `apply_plan` on the Runner asks nobody.
- The console has no identity of its own toward a Runner: its quality alert watcher reads as the signed-in person and a background pass asks nobody.
- Each service and group can have a state login of its own on the shared database server, with its own schemas and registry (`docs/state-logins.md`); an install that shares one login still works.
- The report-catalog snapshot sink over plan 30's route is built as a library only (`DefinitionsClientSnapshotSink`).

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| TRUSTZONES-1 | The console-made link between a Gateway group and a Runner group, made by default: each side gets the other's addresses and a link credential that the console signs per pair of groups, scoped to the three paths and rotated with the group keys, so revoking a group's key also revokes its link; a missing partner warns on Monitoring; a new instance of a linked group gets the link at its join. The console gets an identity of its own toward a Runner (read only: runs, quality) | none | M | servers suite: a new install's Gateway and Runner are linked with no setting; Monitoring shows an unlinked group; the console reads a Runner's runs with no person signed in |
| TRUSTZONES-3 | Mark the tasks that change table structure with the capability `ddl` (Build's `apply_plan`, the Store model's `deploy` and `contract`) and give them an approval screen in the console: the person reads the exact statements and approves, and the console starts the run through `POST api/runner-runs/...`. The Runner's refusal of an unsigned DDL run, the console's signature and the Runner screens without a run button exist (`DbDataRunner/README.md`, "Tasks that change table structure"). The Workspace's run and contract routes are already gone (the console's `/api/deploys/{id}/runs` and `.../contract`); the approval screen makes the plan's text an input of the run, so the task checks that the statements it runs have the digest the person approved | none | S | Build conformance and `StoreDeployOnRunnerTests`: `apply_plan`, `deploy` and `contract` are refused with no signature and run through the console's approved run |
| TRUSTZONES-4 | Path 1: the Gateway's outbox of run requests to its linked Runner group, the high-priority run, the 10-second wait, then an answer with a link to the run; requests wait in the outbox while the Runner is down | TRUSTZONES-1 | M | a test: a request returns the run's result within the wait; with the Runner down it is kept, then runs once when the Runner is back; a request past the wait answers with the run's link |
| TRUSTZONES-5 | A view's `task` action on path 1: the app manifest lists the tasks it may name (`runner:`), the app's access rules show the link, and the linked Runner group starts a listed task for its linked Gateway group; the action names the viewer and an idempotency key, and the person is recorded, never given a Runner role. Per-task grants and their screens come with the security model | TRUSTZONES-4 | M | vitest and a Playwright story: a click on a view's action starts a listed task as the viewer and shows the result or the link; a task the manifest does not list is refused before the Runner is asked; a person whose access rules hide the link does not see it |
| TRUSTZONES-6 | Webhooks on Serving (deferred: needed only when the Runner has no inbound address outside; the Runner receives signed webhooks itself today): the Gateway receives a signed webhook at an address per package and source, checks the signature, refuses replays and oversize bodies, and hands the body through path 1 to the Runner's inbox | TRUSTZONES-4 | M | a signed webhook to the Gateway becomes an inbox item a task takes exactly once; a replay is refused with nothing queued; the Runner has no inbound address outside |
| TRUSTZONES-7 | Path 2 for `data.changed`: the Runner tells its linked Gateways over the link. Deletes the Runner's code pairing with Gateways (`gateway add --code`, `gateways.json`, `GatewayNoun`, `GatewayPairings`, `GatewayPairingApi`); voids the `docs/ui-editors.md` row "Serving-group pairings of a product server" (updated in the same change) | TRUSTZONES-1 | M | a refresh on the Runner clears the linked Gateway's cache for that dataset with no pairing made by hand; `dbdatarunner gateway add` answers the sentence |
| TRUSTZONES-8 | Path 2 for Build's `apply_plan`: the control service asks the linked Gateways before it starts `apply_plan` (the Runner runs only a control-signed structural task, TRUSTZONES-3), so it is refused, before the task is queued, when it drops a column a linked Gateway serves; the person sees the consumer on the build's page. A Store model's contract already asks the Gateways the console manages (`POST /api/deploys/{id}/contract`) | TRUSTZONES-1 | M | Build conformance: `apply_plan` is refused naming the consumer and the Gateway; with `allow-consumer` it runs |
| TRUSTZONES-9 | Path 2 for publishing from Work: a Runner task publishes a definition to its linked Gateway group as the link's publisher; the report-catalog snapshot (`DefinitionsClientSnapshotSink`) is the first user | TRUSTZONES-1 | S | a Runner task publishes a `report-catalog` definition into the linked group's channel; the same bytes publish nothing; a failing group keeps the last snapshot and says why |
| TRUSTZONES-10 | Path 2 governed reads: a Work task reads through the linked Gateway as a named person (for subscriptions and catalog reads), with that person's rules | TRUSTZONES-1 | M | two subscribers with different row rules read different rows through the link; a person with no access gets none |
| TRUSTZONES-11 | A Gateway group shown as one node with its instances on the console, and a group's data sources as the group lists them; the console keeps each instance's address and calls any live instance of the group, so a moved instance is followed without being removed and joined again (today it calls the instance that joined first, `docs/install.md`, "Which instance of a group the console calls") | TRUSTZONES-1 | S | Playwright: a group of two instances is one node listing both; Servers suite: with the first instance of a group stopped, the console's calls to the group reach the second |
| TRUSTZONES-12 | Every secret value and login has an optional expiration date: stored with the secret (the shared stores, the group stores, the console's store, connection logins), shown and edited where the secret is, warned of before it expires; a run that uses an expired login fails naming it (owner, 2026-11-15; queued) | none | M | tests on each store: an expiry round-trips; a warning is raised N days before; a task using an expired login fails naming the secret |
| TRUSTZONES-14 | Outbound lists in the services: the console, the Gateway and the Runner read the `outbound:` setting and send their own outbound connections through `OutboundGate` (`DbDataKit.Outbound`), each service's refusals go to its audit through an `IOutboundAudit`, and a settings screen of each edits the list (the row "Outbound list" of `docs/ui-editors.md`). The Workspace's list is WORKSPACEISOLATION-9 | none | M | a test per service: a listed destination connects; an unlisted one is refused, and the audit names it and the operation; a story edits the list on a service's settings screen |
| TRUSTZONES-16 | The `group` topology writes per-login schema names too: `state init --topology group` writes `dbdatakit_<service>_<group>_{module}` and the registry schema `dbdatakit_<service>_<group>_registry`, as `--topology install` does, and the login is granted those schemas (`state grants`). Nothing is migrated. A group setup that still names the old built-in schemas (`dbdatakit_<module>`) fails at start with a message naming the settings file and the `state:` setting (a clean break, never silent); `state init` on it points to the new names. Creating the login and database on request is parked (`planning/PARKED.md`). Owner's answer: per-login names everywhere, no migration, consistency over keeping old names | none | M | tests on SQL Server and on PostgreSQL: `state init --topology group` writes the per-login names, the grants cover exactly them, and a setup on the old names fails naming the file |

## Needs from other subjects

- The join's answer has a place for the link (`JoinAnswer.Link`, filled by the console through `JoinGroupHandover.Link` in `ConsoleJoinGroups`); TRUSTZONES-1 fills it.

## Open designs

Not scheduled. Each becomes tasks only when the owner schedules it.

### Admin lock-down settings

**What.** Settings an administrator may turn on later to restrict risky actions that the suite allows by default with a clear warning
(`docs/architecture.md`, "Risky actions"). The first is production reads for authoring: discovery and describe in
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
