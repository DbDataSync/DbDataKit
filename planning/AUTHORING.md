# AUTHORING: Workspace authoring of build, sync and store projects

## Goal

Everything a person did on the deleted Build, Sync and Store servers is done from a Workspace project, started where the person works and run where it
belongs: reads of a database as Runner tasks, results back on the same screen. Every authoring screen has a browser story.

## What exists today

- Build, sync, store and integration projects in the Workspace (`DbDataKit.Authoring.Server`), built with the products' tools, published to a Runner
  and a Gateway through plan 30's shared route (`WorkspaceGatewayPublish`, `DefinitionsClient`).
- Build's `tests`, `status` and `compare` are manual tasks of the refresh package (`ViewJobs.cs`) on the Runs tab. The operations package
  (`project publish --operations`: `init`, `seed`, `apply_plan`, `ack`) is not built by the Workspace, so those run from the Runner's screens only.
- Sync publish: the platform comes from `WorkspaceSettings.SyncPlatform`, not from the target Runner.
- Sync forms: all YAML-only parts have forms except the mapping preview and the segmenting strategy's try-run (both read a database).
- `dbdatasync provisioning plan --from-cache` plans the target side offline; the source side (change tracking, logical slots) is `unknown` (the Sync set's `preflight` checks it live).
- A model's `queries:` are edited as YAML only (the Build server's editor went with the server).
- The offline verb `dbdatabuild project publish --to gateway --out` and the per-publish adopt confirmation of a hand-made data source still exist beside
  the slot bindings that replace them.
- The console's build-helper agent installs itself on a Build server project (`ConsoleAgents.InstallBuildHelperAsync`), which no longer exists.
- Many Workspace screens are tested by vitest only, never opened in a browser (`docs/ui-coverage.md`); many browser stories of the console's and
  the Gateway's screens are written and have never run in CI.
- Resync and reconcile-deletes buttons of a sync project's Runs tab are paused (owner, 2026-11-16).
- A package's read-only manual task `preflight` runs before the console serves a version in `prod` and from **Re-check** on the Runs tab
  (`docs/publishing.md`, "Preflight before activation"). Build's refresh package, a Store model's package and a Sync set declare it.
- The Designer's shared reads (`@dbdatakit/workspace`): the channel picker offers **Set up for development** where a channel does not bind the slot
  (the login is stored on the Runner; the binding is still an administrator's), **Test** on the tab **Catalog**, **Describe columns** for a kind to
  place, and the newest `preflight` per channel on the Runs tab (`docs/authoring-metadata.md`, "Every kind uses the same pieces").

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| AUTHORING-1 | The Workspace builds the operations package beside the refresh package, and the control service deploys both to the same channel (WORKSPACEISOLATION-3); the Runs tab offers `init`, `seed`, `apply_plan` and `ack` with their inputs | WORKSPACEISOLATION-3 | M | `WorkspaceBuildPublishTests`: a build stages both packages; vitest of `ProjectRunsPanel`: the four tasks are offered; a conformance run of `seed` started from the console through the control service |
| AUTHORING-2 | A sync package is built for the target Runner's platform: when the control service deploys a sync build (WORKSPACEISOLATION-3), it reads the target Runner's platform from its `/version` and asks the Workspace service for the package built for that platform; the Workspace service never calls the Runner; `WorkspaceSettings.SyncPlatform` goes | WORKSPACEISOLATION-3 | S | a test deploys a sync build through the control service to a fake Runner reporting `linux-x64` and the package the control service pulls is built for it; the setting is refused naming the file |
| AUTHORING-3 | The mapping preview and the strategy try-run as Runner tasks of the published set, started by the control service as the person (the console's run calls, `/api/deploys/{id}/runs`), the preview's rows returned in the call's answer and never stored | none | M | a Sync conformance test: the preview of a mapping returns its first rows through the Runner; the try-run's SQL runs against the bound login |
| AUTHORING-5 | A form editor for a model's `queries:` in a build project (name, description, `sql` or `request`, parameters), checked as you type | none | M | vitest on the form and its YAML; Playwright: add a query with a list parameter, see a translation error at its line, fix it, publish |
| AUTHORING-17 | Adds to the Runner's built-in package `dbdatakit-metadata` (its tasks `discover` and `describe` exist: `DbDataKit.Runner.Metadata`, the route `POST /api/v1/metadata/{channel}/{task}`, `docs/authoring-metadata.md`). Its task `facts`: the server's version and the database settings a kind's checks name, on `DbDataKit.Metadata`'s readers, started, resolved, refused and recorded as `discover` and `describe` are; never a value of a table | none | S | Runner tests on SQL Server and PostgreSQL: `facts` in `dev` answers the server's version and, on SQL Server, whether change tracking is on for the database; an author's `facts` in `prod` runs and its run record names the author |
| AUTHORING-19 | **Set up for development**: from a draft on a Workspace screen, the person types a login once; the screen sends it to the console, which stores it in the project's Runner's secret store for the channel and the slot (built: `POST /api/workspace-dev-logins/...`, `ConsoleDevelopmentLogins`, `developmentLoginApi` of `@dbdatakit/workspace`; `docs/authoring-metadata.md`, "How a screen sends a development login"); the control service puts the connection's copy on the Runner, with the user as plain text and its password naming the stored secret, and binds the slot in the channel the person picked (CHANNELS-1's client); the Runner checks the person's role; the Workspace service only learns which binding exists | CHANNELS-1, WORKSPACEISOLATION-16 | M | a test: set up `crm` for `dev`, the Runner's Bindings show it with the user; the login reaches no Workspace service |
| AUTHORING-6 | The Designer's shared pieces of authoring-time reads are built (`docs/authoring-metadata.md`, "Every kind uses the same pieces"): the channel picker with **Set up for development**, **Refresh**, **Test**, **Describe columns** (`QueryDescribe`, the console's `POST .../describe`) and the newest-check card (`NewestChecks`, the console's `GET /api/deploys/{id}/runs?task=`). What is left: the console's posture page lists the non-production bindings made from the Workspace (AUTHORING-19 makes them); **Describe columns** writes a query's columns into a Build model's file (Sync's is SYNCAUTHORING-12) | AUTHORING-19 | S | vitest of the posture list and of Build's use of the columns; Playwright: after Set up for development binds the slot, the posture page lists the binding |
| AUTHORING-7 | Retire the offline verb `project publish --to gateway --out` and the per-publish adopt confirmation (adapter first: the slot bindings' tests have run) | PUBLISHING-2 | S | the verb and `adopt` answer their sentence; `gateway-publish/1` leaves `docs/formats.json` and `docs/ui-editors.md`; the Workspace's publish tests pass unchanged |
| AUTHORING-9 | Browser stories for the Workspace screens never opened in a browser: column lineage, model files from cached tables, the provisioning plan, scripts, the slot bindings screen, the Sync forms, the catalog refresh form; and the stories written and never run in CI: `workspace-authoring.spec.ts` (the kind picker on a new project with what each kind publishes to, check, fix, and the publish panel by product for a build, a sync and a store project), `workspace-publish.spec.ts` (create, edit, build, preview, commit, merge, publish an app) and the preview with live data; each listed in `docs/ui-coverage.md` | none | M | the Playwright specs run in CI; `scripts/check_ui_coverage.py` passes with the stories named |
| AUTHORING-10 | The secrets overview of a Workspace project's connections (decided 2026-11-12, not built): every secret reference of the project, where it is bound, whether the target holds it (read by the control service from the Runner, never by the Workspace service); the Sync parameter form picks a `Secret` parameter's reference from it (today the person types `secret:<name>`) | none | S | vitest and a Playwright story: a project with three references shows each with its binding and state |
| AUTHORING-12 | Resync and reconcile-deletes buttons on a sync project's Runs tab, started through the control service (the console's run calls, `/api/deploys/{id}/runs`) (paused, owner 2026-11-16; queued until the drain works out a mapping's source key) | none | M | Sync conformance: resync of one mapping reloads it; reconcile removes target rows the source lost |
| AUTHORING-13 | The form editor of the `access:` section (rules tables, row rules, column rules) in the Workspace's build and store model panels | none | M | vitest: the form round-trips a model's `access:` section; Playwright: add a column rule, see the check's finding for a missing rules table, publish |
| AUTHORING-15 | Browser stories of the console's own screens that are written and never run in CI, run there like AUTHORING-9: the agents' evaluation panel (`screens-agents-evaluation.spec.ts`), the Runner's Link flow, the store approvals (`store-approvals.spec.ts`), the monitoring story against real servers (`monitoring.spec.ts`), and the others `docs/ui-coverage.md` lists as not run | none | M | the Playwright specs run in a CI job of the console; `docs/ui-coverage.md` no longer lists them as not run |
| AUTHORING-16 | Browser stories of the Gateway's screens (shown by the console) that are written and never run in CI, and the missing ones: the semantic model browser and query explorer (`semantic-models-explorer.spec.ts`), the result cache screen (a dataset's hit rate rises after repeated queries, Clear empties it and the next query is a miss), click-to-sort of tables and matrices (`views-sort-print.spec.ts`), the stories that need a real Gateway (`views-apps-addresses`, `views-apps-embed`), and the `semantic-models.spec.ts` and `refresh-run.spec.ts` stories that need the test PostgreSQL | none | M | the Playwright specs run in a CI job against a real Gateway; the cache story passes; `docs/ui-coverage.md` no longer lists them as not run |
| AUTHORING-25 | On SQL Server, `discover` also lists every database the connection's login can open (`sys.databases`), each with its schemas, and reads the tables of the one asked for (an input `database`); the database pickers offer them. A PostgreSQL connection reads its one database, as today | none | M | a SQL Server conformance test: a login that reads two databases sees both in the cache, and the tables of the second after a read that names it |
| AUTHORING-26 | A read of a database's shape (`discover`, `describe`) needs the Runner's viewer role, not operator: it changes nothing, and the console has already checked that the person has access to the project | none | S | a person who is a viewer on the project's Runner refreshes the catalog; starting any other task still needs operator |
| AUTHORING-27 | The person who staged a version held for its preflight activates it: with no error found, the Runner lets that person activate it with the role that staged it (publisher); after an error, the same person confirms the errors, recorded with their name as today. `docs/publishing.md` ("Preflight before activation" and the route table) follows | none | S | a test: Bob, a publisher on the Runner, serves a package in a production channel; its preflight finds nothing and Bob activates it; a second version's preflight finds an error and Bob's activation with `"confirm": true` is recorded with his name |

## Needs from other subjects

- Agents authoring Workspace projects (through MCP or the console's embedded agent, each change approved) and the build-helper on a Workspace build
  project are AGENTACCESS-8.
- PUBLISHING-2: the per-kind routes removed and every caller on the shared route, before AUTHORING-7.
- CHANNELS-1: the client that puts a connection copy on a Runner, sends its login and binds it for a channel, before AUTHORING-19.
- WORKSPACEISOLATION-3: the control service's deploy, before AUTHORING-1 and AUTHORING-2 (the target Runner's platform).
- The Gateway catalog refresh is the console's `POST api/workspace-catalog/{workspace}/{project}/gateway-read` (`ConsoleCatalogRead.GatewayAsync`; `docs/authoring-metadata.md`, "A read from a Gateway").

## Proposed decisions

### AUTHORING-P50: reading a draft whose package the Runner does not hold yet (question, awaiting the owner)

Scenario: Ana makes the build project `sales-dw` and adds the connection `warehouse`. Nothing of it is on the Runner `plant` yet. On the tab **Catalog**
the channel picker lists no channel (the console lists only the channels of a package the Runner holds) and offers **Set up warehouse for development**
with `dev`. Ana sends the login; `plant` stores it. Even once an administrator (or, later, the console) binds the slot, **Refresh** in `dev` is refused:
the Runner's `discover` reads only a slot that a version served or staged in that channel declares, so Ana must first publish a build staged in `dev`.

- Option A (as built): keep it. The picker offers `dev` when the Runner lists no channel, the login is stored, and the line says what is left; the doc
  says to publish a build staged in `dev` first (`docs/authoring-metadata.md`, "Until then, an administrator binds the slot by hand").
- Option B: the console lists the channels the Runner serves (with none bound) when the Runner holds no version of the package, and the Runner lets
  a binding name a slot of a package it holds no version of yet, so `discover`, `describe` and the binding test read a draft that was never published.
  This changes the Runner's rule for its built-in package and its bindings.
- Option C: **Set up for development** also builds the draft and stages it in the channel through the console's deploy, before the binding.

Recommendation: B, so a draft is read before it is ever built, as the Designer's example on `docs/authoring-metadata.md` describes; until then A.

### AUTHORING-P51: the newest-check card shows the preflight, not the newest Test (default, awaiting the owner)

Scenario: Ana pressed **Test** on `crm` in `prod` yesterday and it failed at `login`. Today the Runs tab's newest-check card of `prod` shows the newest
`preflight` run, with its findings and link, and nothing about yesterday's test. The binding test is a call the Runner answers at once and records in
its audit, not a run, so the console finds no run of it to show.

- Option A (as built): the card shows the newest `preflight` run per channel; a test's steps are shown where **Test** is pressed.
- Option B: the Runner keeps the newest binding test per package, channel and slot (who, when, the step it stopped at) and the card shows it beside
  the preflight.

Recommendation: A; the preflight already reads every connection with the bound logins, and a failed login shows there as an error.

## Open designs

### The Runner the channel picker offers first

The picker offers the Runner the catalog cache says the connection was last read from, or else the first Runner the console lists. Once a project names the Runner it deploys to, the picker offers that one.

### The Runner's built-in package where a group requires signed versions

**What.** The Runner puts its built-in package `dbdatakit-metadata` in a channel through its own publish, so a group with `require_signatures` on
refuses it and a catalog read answers 409 naming the missing signature. The direction is to exempt that package, because it ships beside the Runner's
own program and is as trusted as the program, with the audit line of its install saying so. **Why it waits:** signatures are not built yet, so with
the setting on every publish is refused anyway, and the exemption is a supply chain exception to weigh again when they are. No code changes until then.

## Shared areas touched

- `DbDataKit.Authoring`, `DbDataKit.Authoring.Server`, `Shared/web/workspace`, `Shared/web/console` (posture, secrets overview), `DbDataKit.Agents`.
- `docs/formats.json` and `docs/ui-editors.md` (`gateway-publish/1` removed, `catalog-cache/2`), `docs/ui-coverage.md`, `docs/authoring-metadata.md`.
- The Runner (`DbDataKit.Runner`: the built-in package `dbdatakit-metadata`, the binding test by slot, the held staged version), `DbDataKit.Runner.Client`.
- The console's host (`DbDataKit.Server`: the catalog read from a Runner channel).
