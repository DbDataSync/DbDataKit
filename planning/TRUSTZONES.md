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
| TRUSTZONES-3 | Mark the tasks that change table structure with the capability `ddl` (Build's `apply_plan`, the Store model's `deploy` and `contract`) and give them an approval screen in the console: the person reads the exact statements and approves, and the console starts the run through `POST api/runner-runs/...`. The Runner's refusal of an unsigned DDL run, the console's signature and the Runner screens without a run button exist (`DbDataRunner/README.md`, "Tasks that change table structure"). The Workspace's own run buttons for these tasks (`WorkspaceManagement.cs`, the `.../run` and `.../contract` routes) and the conformance tests that start them (`BuildOperationsOnRunnerTests`, `StoreDeployOnRunnerTests`) move to the console's call in the same change; the approval screen makes the plan's text an input of the run, so the task checks that the statements it runs have the digest the person approved | none | S | Build conformance and `StoreDeployOnRunnerTests`: `apply_plan`, `deploy` and `contract` are refused with no signature and run through the console's approved run |
| TRUSTZONES-4 | Path 1: the Gateway's outbox of run requests to its linked Runner group, the high-priority run, the 10-second wait, then an answer with a link to the run; requests wait in the outbox while the Runner is down | TRUSTZONES-1 | M | a test: a request returns the run's result within the wait; with the Runner down it is kept, then runs once when the Runner is back; a request past the wait answers with the run's link |
| TRUSTZONES-5 | A view's `task` action on path 1: the Runner keeps start grants (package, task, channel, client, who granted), the action names the viewer and an idempotency key, the console's runner task panel and the Gateway's Links panel edit grants and links. Who may start a task from a view is three layers, each in its own place: the app's access rules show the link, the app manifest lists the tasks it may name (`runner:`), and the Runner's grant says which client may start which task in which channel; the person is recorded, never given a Runner role | TRUSTZONES-4 | M | vitest and a Playwright story: an administrator grants a task to the linked Gateway, a click on a view's action starts it as the viewer and shows the result or the link; revoking the grant makes the next click refuse; a task the manifest does not list is refused before the Runner is asked; a person whose access rules hide the link does not see it |
| TRUSTZONES-6 | Webhooks on Serving: the Gateway receives a signed webhook at an address per package and source, checks the signature, refuses replays and oversize bodies, and hands the body through path 1 to the Runner's inbox | TRUSTZONES-4 | M | a signed webhook to the Gateway becomes an inbox item a task takes exactly once; a replay is refused with nothing queued; the Runner has no inbound address outside |
| TRUSTZONES-7 | Path 2 for `data.changed`: the Runner tells its linked Gateways over the link. Deletes the Runner's code pairing with Gateways (`gateway add --code`, `gateways.json`, `GatewayNoun`, `GatewayPairings`, `GatewayPairingApi`); voids the `docs/ui-editors.md` row "Serving-group pairings of a product server" (updated in the same change) | TRUSTZONES-1 | M | a refresh on the Runner clears the linked Gateway's cache for that dataset with no pairing made by hand; `dbdatarunner gateway add` answers the sentence |
| TRUSTZONES-8 | Path 2 for the contract guard: the control service asks the linked Gateways before it starts `apply_plan` or a contract (the Runner runs only a control-signed structural task, TRUSTZONES-3), so Build's `apply_plan` and a Store model's contract are refused, before the task is queued, when they drop a column a linked Gateway serves; the person sees the consumer on the build's page | TRUSTZONES-1 | M | Build conformance and the Workspace's tests: `apply_plan` and the Store contract are refused naming the consumer and the Gateway; with `allow-consumer` they run |
| TRUSTZONES-9 | Path 2 for publishing from Work: a Runner task publishes a definition to its linked Gateway group as the link's publisher; the report-catalog snapshot (`DefinitionsClientSnapshotSink`) is the first user | TRUSTZONES-1 | S | a Runner task publishes a `report-catalog` definition into the linked group's channel; the same bytes publish nothing; a failing group keeps the last snapshot and says why |
| TRUSTZONES-10 | Path 2 governed reads: a Work task reads through the linked Gateway as a named person (for subscriptions and catalog reads), with that person's rules | TRUSTZONES-1 | M | two subscribers with different row rules read different rows through the link; a person with no access gets none |
| TRUSTZONES-11 | A Gateway group shown as one node with its instances on the console, and a group's data sources as the group lists them; the console keeps each instance's address and calls any live instance of the group, so a moved instance is followed without being removed and joined again (today it calls the instance that joined first, `docs/install.md`, "Which instance of a group the console calls") | TRUSTZONES-1 | S | Playwright: a group of two instances is one node listing both; Servers suite: with the first instance of a group stopped, the console's calls to the group reach the second |
| TRUSTZONES-12 | Every secret value and login has an optional expiration date: stored with the secret (the shared stores, the group stores, the console's store, connection logins), shown and edited where the secret is, warned of before it expires; a run that uses an expired login fails naming it (owner, 2026-11-15; queued) | none | M | tests on each store: an expiry round-trips; a warning is raised N days before; a task using an expired login fails naming the secret |
| TRUSTZONES-14 | Outbound lists in the services: the console, the Gateway and the Runner read the `outbound:` setting and send their own outbound connections through `OutboundGate` (`DbDataKit.Outbound`), each service's refusals go to its audit through an `IOutboundAudit`, the shared host code sends the join to a console, the announcement (`DBDATAKIT_ANNOUNCE_URL`) and the telemetry export through the gate too, each service adding the console it joined and its telemetry endpoint to its own list itself, and a settings screen of each edits the list (the console's also edits the Workspace's, `outbound:` in its `server.yaml`, edited by hand today) (the row "Outbound list" of `docs/ui-editors.md`). The Workspace reads its list already (`WorkspaceOutbound`; `DbDataWorkspace/README.md`, "Where the Workspace connects") | none | M | a test per service: a listed destination connects; an unlisted one is refused, and the audit names it and the operation; a story edits the list on a service's settings screen |
| TRUSTZONES-15 | Create the login and the database in one go on request: by default the suite creates nothing (the administrator does, `state grants`), but `state init --apply` of a service's command line and a screen of the console (a button on the service's state settings) create the login and its database when the person asks, through a temporary administrator connection string they give once; the string is used for that run only and never stored, and the generated password is stored as the service's secret and used by the service from then on. The screen is not a stored format, so it needs no row of `docs/ui-editors.md`. INSTALLER-21 writes the `state:` settings and keeps passing the password by secret reference | none | M | a test on SQL Server and on PostgreSQL: the login and the database are created; the temporary connection string is found nowhere on disk and in no log or audit line; the generated password is stored as the secret and the service starts with it; a second run changes nothing; a story runs the screen |
| TRUSTZONES-16 | The `group` topology writes per-login schema names too: `state init --topology group` writes `dbdatakit_<service>_<group>_{module}` and the registry schema `dbdatakit_<service>_<group>_registry`, as `--topology install` does, and the login is granted those schemas (`state grants`). Nothing is migrated. A group setup that still names the old built-in schemas (`dbdatakit_<module>`) fails at start with a message naming the settings file and the `state:` setting (a clean break, never silent); `state init` on it points to the new names. TRUSTZONES-15 creates the login and database on request in both topologies. Owner's answer: per-login names everywhere, no migration, consistency over keeping old names | none | M | tests on SQL Server and on PostgreSQL: `state init --topology group` writes the per-login names, the grants cover exactly them, and a setup on the old names fails naming the file |

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

### Outbound list: what the first form leaves out

**What.** The outbound list (`DbDataKit.Outbound`) ships with the simplest form: an entry is `scheme://host[:port]`, and a service with no `outbound:` line
connects anywhere. This design keeps what that form defers, so it is reviewed later.

**Why.** The first form fits the lists people can write today (package sources, git remotes, a database host). Two choices were taken for now and may not
hold once installs use the lists in earnest.

**Open questions.**

- Paths: whether an entry may name a path prefix (`https://github.com/org/`), so a list can allow one organisation of a host rather than all of it.
- Address ranges: whether an entry may be a range such as `10.0.0.0/8`, for internal servers that have no name worth listing, and how names and ranges
  meet in the operating-system rules of the design above.
- Closed by default: whether a service with no `outbound:` line should connect nowhere, so a new install is closed until an administrator lists what it
  needs (the installer could write the list), and what an upgrade of an install without the line does then.

### The console reading other services' runs from their databases

**What.** An option for installs where the console can reach a service's database: the console's login gets a read-only grant on the runs, audit and
notifications schemas of that service, never payloads or secrets, and the console's registry lists them so one query reads all. It is never assumed. The
default is each service's API (`docs/state-logins.md`, "Limits"), which needs no network path to the service's database.

**Why.** An API is not always the best path for a large list of runs, and a person may prefer one query. The owner has no rule between APIs and database
queries and cares about UX, performance and security, chosen case by case; direct access is at most an option, because some installs give the console no
network access to a service's database (SQL Server Express, a private container database).

**Open questions.** Where the option is switched on (the console's state settings, per service); how the grant is made (`state grants` for the console's
login, limited to those three modules); how the console falls back to the API when the database is unreachable; and a test on SQL Server and PostgreSQL that the grant is
read-only and limited to those modules, which is the "Done when" of a task made when the owner schedules this.

### The outbound list behind an HTTP proxy

**What.** How the outbound list (`DbDataKit.Outbound`) behaves when a service sends its traffic through an HTTP proxy. Nothing about proxies is built: the
gate's HTTP handler checks the address each connection is opened to and does not read the proxy settings, so behind a proxy it sees only the proxy.

**Why.** A service that reaches `https://api.nuget.org` through `proxy.corp:3128` can list the proxy (`tcp://proxy.corp:3128`), but then the list no longer
limits which sites the proxy fetches. The alternative is for the handler to read the proxy settings and check the request's own address first and the
proxy second, so the list holds `https://api.nuget.org` and the proxy.

**Open questions.** Whether the handler should read the proxy settings at all, and from where (the service's settings, the environment, the system);
whether a proxy changes what an entry means; and how this meets the operating-system rules of the design above, one of which is a forward proxy that is the
only way out.

## Shared areas touched

- `DbDataKit.Web` (link credential), `DbDataKit.Serving.Notify` (deleted pairing), `DbDataKit.Runner.Api` (start grants, run route), `DbDataKit.Views`
  (the `task` action), `DbDataKit.Lineage` (`ContractGuard` callers), `DbDataKit.ReportCatalogs`, `DbDataKit.State` (logins, grants),
  `DbDataKit.Secrets.*` (expiry).
- `docs/architecture.md`, `docs/ui-editors.md`, a state migration of the Runner's and the console's modules (`scripts/check_state_migrations.py`).
