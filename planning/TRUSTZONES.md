# TRUSTZONES: isolation by trust, the paths between places

## Goal

Work is placed by what it can touch (`docs/architecture.md`, "What runs where"). The console links each Gateway group with a Runner group. Serving
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
- Each service and group can have a state login of its own on the shared database server, with its own schemas and registry (`docs/state-logins.md`); an install that shares one login still works.
- The report-catalog snapshot sink over plan 30's route is built as a library only (`DefinitionsClientSnapshotSink`).

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| TRUSTZONES-1 | The console-made link between a Gateway group and a Runner group, made by default: each side gets the other's addresses and a link credential scoped to the three paths; a missing partner warns on Monitoring; a new instance of a linked group gets the link at its join. The console gets an identity of its own toward a Runner (read only: runs, quality) | INSTALLER-15 | M | servers suite: a new install's Gateway and Runner are linked with no setting; Monitoring shows an unlinked group; the console reads a Runner's runs with no person signed in |
| TRUSTZONES-3 | DDL tasks (creating, altering or dropping tables) accept a run only from the control service, as a request it signs after the person approved the exact statements; the Runner screens show these tasks without a run button | none | S | the Runner refuses a DDL task's run without the control service's signed request; a run started from the console after the person approves runs it |
| TRUSTZONES-4 | Path 1: the Gateway's outbox of run requests to its linked Runner group, the high-priority run, the 10-second wait, then an answer with a link to the run; requests wait in the outbox while the Runner is down | TRUSTZONES-1 | M | a test: a request returns the run's result within the wait; with the Runner down it is kept, then runs once when the Runner is back; a request past the wait answers with the run's link |
| TRUSTZONES-5 | A view's `task` action on path 1: the Runner keeps start grants (package, task, channel, client, who granted), the action names the viewer and an idempotency key, the console's runner task panel and the Gateway's Links panel edit grants and links. Who may start a task from a view is three layers, each in its own place: the app's access rules show the link, the app manifest lists the tasks it may name (`runner:`), and the Runner's grant says which client may start which task in which channel; the person is recorded, never given a Runner role | TRUSTZONES-4 | M | vitest and a Playwright story: an administrator grants a task to the linked Gateway, a click on a view's action starts it as the viewer and shows the result or the link; revoking the grant makes the next click refuse; a task the manifest does not list is refused before the Runner is asked; a person whose access rules hide the link does not see it |
| TRUSTZONES-6 | Webhooks on Serving: the Gateway receives a signed webhook at an address per package and source, checks the signature, refuses replays and oversize bodies, and hands the body through path 1 to the Runner's inbox | TRUSTZONES-4 | M | a signed webhook to the Gateway becomes an inbox item a task takes exactly once; a replay is refused with nothing queued; the Runner has no inbound address outside |
| TRUSTZONES-7 | Path 2 for `data.changed`: the Runner tells its linked Gateways over the link. Deletes the Runner's code pairing with Gateways (`gateway add --code`, `gateways.json`, `GatewayNoun`, `GatewayPairings`, `GatewayPairingApi`); voids the `docs/ui-editors.md` row "Serving-group pairings of a product server" (updated in the same change) | TRUSTZONES-1 | M | a refresh on the Runner clears the linked Gateway's cache for that dataset with no pairing made by hand; `dbdatarunner gateway add` answers the sentence |
| TRUSTZONES-8 | Path 2 for the contract guard: whoever asks (TRUSTZONES-P1), Build's `apply_plan` and a Store model's contract refuse to drop a column a linked Gateway serves | TRUSTZONES-1 | M | Build conformance and the Workspace's tests: `apply_plan` and the Store contract are refused naming the consumer and the Gateway; with `allow-consumer` they run |
| TRUSTZONES-9 | Path 2 for publishing from Work: a Runner task publishes a definition to its linked Gateway group as the link's publisher; the report-catalog snapshot (`DefinitionsClientSnapshotSink`) is the first user | TRUSTZONES-1 | S | a Runner task publishes a `report-catalog` definition into the linked group's channel; the same bytes publish nothing; a failing group keeps the last snapshot and says why |
| TRUSTZONES-10 | Path 2 governed reads: a Work task reads through the linked Gateway as a named person (for subscriptions and catalog reads), with that person's rules | TRUSTZONES-1 | M | two subscribers with different row rules read different rows through the link; a person with no access gets none |
| TRUSTZONES-11 | A Gateway group shown as one node with its instances on the console, and a group's data sources as the group lists them | TRUSTZONES-1 | S | Playwright: a group of two instances is one node listing both |
| TRUSTZONES-12 | Every secret value and login has an optional expiration date: stored with the secret (the shared stores, the group stores, the console's store, connection logins), shown and edited where the secret is, warned of before it expires; a run that uses an expired login fails naming it (owner, 2026-11-15; queued) | none | M | tests on each store: an expiry round-trips; a warning is raised N days before; a task using an expired login fails naming the secret |
| TRUSTZONES-14 | Outbound lists in the services: the console, the Gateway and the Runner read the `outbound:` setting and send their own outbound connections through `OutboundGate` (`DbDataKit.Outbound`), each service's refusals go to its audit through an `IOutboundAudit`, and a settings screen of each edits the list (the row "Outbound list" of `docs/ui-editors.md`). The Workspace's list is WORKSPACEISOLATION-9 | none | M | a test per service: a listed destination connects; an unlisted one is refused, and the audit names it and the operation; a story edits the list on a service's settings screen |

## Needs from other subjects

- INSTALLER-15: the console makes every group key and hands it at the join; TRUSTZONES-1 carries the link in that join.

## Proposed decisions

### TRUSTZONES-P1: who asks the Gateways before a column is dropped (awaiting the owner)

Today the Workspace service asks the paired Gateways before a Store contract (WORKSPACEISOLATION removes its calls to other services); Build's
`apply_plan` asks nobody. Option A, "Control asks": the control service asks the linked Gateways before it starts `apply_plan` or a contract, and the Runner runs only a Control-signed structural task (TRUSTZONES-3). Option B,
"Work asks": the Runner task asks its linked Gateways over path 2 before it runs the DDL. Scenario: an engineer approves a Build plan that drops
`orders.discount`, which a published dashboard reads. Under A the control service refuses before the task is queued and the person sees the consumer on the
build's page; under B the task is queued, asks, and fails, and the refusal is in the run's log. A run started from the Runner's own screen is refused under A
by TRUSTZONES-3 and checked under B. Recommendation: A, because a structural task is already a Control action against an approved plan hash.

### TRUSTZONES-P2: the link credential (default, awaiting the owner)

The console signs a link credential per pair of groups, scoped to the three paths, rotated with the group keys. Scenario: a Gateway instance is stolen;
revoking its group key also revokes its link. Recommendation: accept.

### TRUSTZONES-P3: the console's one query over every service's runs (default, awaiting the owner)

With a registry per login (`docs/state-logins.md`), the console's single query over the runs, audit and notifications of every service in a shared database finds only the console's own modules. Built as the default: the console reads another service's runs through that service's API, as it does for services on their own databases. Scenario: an install on one PostgreSQL database; the Runner's runs page in the console shows the Runner's runs through the Runner's API, and no login reads another's schemas. Option B: grant the console's login read rights on the other services' schemas and register them in the console's registry, so one query reads all. Recommendation: A, because the console would then reach every service's data with one login, which is what separate logins avoid.

### TRUSTZONES-P4: the topology `group` keeps the built-in schema names (default, awaiting the owner)

`state init --topology install` writes per-login schemas; `--topology group` (one database for one group) still writes the built-in names (`dbdatakit_<module>`), so a group database made earlier does not change names. Scenario: a Runner group `main` on its own database `dbdatakit_runner_main`: its login is granted `dbdatakit_runs` and the rest of that database only, and another group has another database. Option B: write per-login schemas in a group database too, so a second service pointed at the same database later still stays apart. Recommendation: A, because the database is already the boundary and the names stay as they are.

### TRUSTZONES-P5: who creates the login (default, awaiting the owner)

The suite prints the statements to grant a login and never creates it or its password; the administrator (or the installer, INSTALLER-21, with the password from a secret reference) creates it. Scenario: an administrator runs `CREATE ROLE dbdatakit_runner_main LOGIN PASSWORD '...'`, then `dbdatarunner state grants | psql`. Option B: `state init --apply` also creates the login with a generated password stored as a secret. Recommendation: A for now; INSTALLER-21 decides how the installer passes the password.

### TRUSTZONES-P6: a service with no `outbound:` setting connects anywhere (default, awaiting the owner)

`DbDataKit.Outbound` reads one list, `outbound:`, in the service's settings file. Scenario: an administrator upgrades an install whose `server.yaml` has no `outbound:` line; the service connects as before and nothing is refused or audited. Adding `outbound: [https://api.nuget.org]` refuses everything else, and `outbound: []` refuses everything; a line with no value fails at start. Option B: a service with no line connects nowhere, so a new install is closed until an administrator lists what it needs (the installer could write the list). Recommendation: A for now, because B breaks every upgrade and every install that has not listed its package sources and git remotes; the installer can write a list for new installs later.

### TRUSTZONES-P7: the form of an entry in the list (default, awaiting the owner)

An entry is `scheme://host[:port]` (schemes `http`, `https`, `ssh`, `git`, `tcp`; the scheme gives the default port; a host may be `*.example.com`), the form the app egress approvals already use (`https://*.example.com`), with no path. Scenario: `ssh://github.com` allows a git remote `git@github.com:org/repo.git`, `https://github.com` allows the same host over https, and `https://api.nuget.org` does not allow `https://api.nuget.org:8443`. Option B: also address ranges (`10.0.0.0/8`) for internal servers, or match on the scheme and a path prefix. Recommendation: A, because the list names hosts people recognise; ranges and paths can be added when someone needs them, and the operating-system rules of the open design below decide how names and addresses meet.

### TRUSTZONES-P8: a service behind an HTTP proxy lists the proxy (default, awaiting the owner)

The gate checks the address a connection is opened to. Scenario: a service reaches `https://api.nuget.org` through the proxy `proxy.corp:3128`; the list holds `tcp://proxy.corp:3128`, and the library cannot tell which site the proxy fetches, so the list no longer limits the site. Option B: the handler checks the request's own address first and the proxy second, so the list holds `https://api.nuget.org` and the proxy. Recommendation: B when a proxy is set; the default today is A because the handler does not read the proxy settings, and the change is small.

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

### Outbound rules applied by the operating system

**What.** The install applies each service's outbound list (`DbDataKit.Outbound`, the `outbound:` setting) as rules the service cannot get around: firewall rules for the service's
account on Windows and Linux, and egress rules for its container. The rules cover programs the service starts (git, `dotnet restore`, npm), which
the library cannot see. The install applies them automatically, and `service check` reports when they are missing or out of date.

**Why.** The outbound list stops our own code. Only the operating system or the container platform stops a program that ignores it. The rules are also a
partial mitigation of supply chain attacks: a compromised package or build tool running in a service cannot send data to, or fetch code from, an
address that is not on the list.

**Open questions.**

- Firewall rules match addresses, and the lists name hosts. A host such as a cloud provider's endpoint changes addresses often, so a list of addresses
  kept by hand is not workable. Options: rules that the platform keeps in step with DNS (Windows Firewall's FQDN-based dynamic keywords, an nftables
  set filled from DNS, a container network policy that matches DNS names), or one forward proxy that the firewall makes the only way out.
- Which platforms come first: Windows services, Linux systemd units, containers.
- How a list item that is a package source with mirrors and CDNs (NuGet, npm) is written.

## Shared areas touched

- `DbDataKit.Web` (link credential), `DbDataKit.Serving.Notify` (deleted pairing), `DbDataKit.Runner.Api` (start grants, run route), `DbDataKit.Views`
  (the `task` action), `DbDataKit.Lineage` (`ContractGuard` callers), `DbDataKit.ReportCatalogs`, `DbDataKit.State` (logins, grants),
  `DbDataKit.Secrets.*` (expiry).
- `docs/architecture.md`, `docs/ui-editors.md`, a state migration of the Runner's and the console's modules (`scripts/check_state_migrations.py`).
