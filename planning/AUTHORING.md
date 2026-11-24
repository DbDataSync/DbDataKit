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

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| AUTHORING-1 | The Workspace builds the operations package beside the refresh package, and the control service deploys both to the same channel (WORKSPACEISOLATION-3); the Runs tab offers `init`, `seed`, `apply_plan` and `ack` with their inputs | WORKSPACEISOLATION-3 | M | `WorkspaceBuildPublishTests`: a build stages both packages; vitest of `ProjectRunsPanel`: the four tasks are offered; a conformance run of `seed` started from the console through the control service |
| AUTHORING-2 | A sync package is built for the target Runner's platform: when the control service deploys a sync build (WORKSPACEISOLATION-3), it reads the target Runner's platform from its `/version` and asks the Workspace service for the package built for that platform; the Workspace service never calls the Runner; `WorkspaceSettings.SyncPlatform` goes | WORKSPACEISOLATION-3 | S | a test deploys a sync build through the control service to a fake Runner reporting `linux-x64` and the package the control service pulls is built for it; the setting is refused naming the file |
| AUTHORING-3 | The mapping preview and the strategy try-run as Runner tasks of the published set, started by the control service as the person (the console's run calls, `/api/deploys/{id}/runs`), the preview's rows returned in the call's answer and never stored | none | M | a Sync conformance test: the preview of a mapping returns its first rows through the Runner; the try-run's SQL runs against the bound login |
| AUTHORING-5 | A form editor for a model's `queries:` in a build project (name, description, `sql` or `request`, parameters), checked as you type | none | M | vitest on the form and its YAML; Playwright: add a query with a list parameter, see a translation error at its line, fix it, publish |
| AUTHORING-17 | Adds to the Runner's built-in package `dbdatakit-metadata` (its tasks `discover` and `describe` exist: `DbDataKit.Runner.Metadata`, the route `POST /api/v1/metadata/{channel}/{task}`, `docs/authoring-metadata.md`). Its task `facts`: the server's version and the database settings a kind's checks name, on `DbDataKit.Metadata`'s readers, started, resolved, refused and recorded as `discover` and `describe` are; never a value of a table | none | S | Runner tests on SQL Server and PostgreSQL: `facts` in `dev` answers the server's version and, on SQL Server, whether change tracking is on for the database; an author's `facts` in `prod` runs and its run record names the author |
| AUTHORING-18 | The binding test without a staged version: by package, channel and slot on the Runner, sent by the control service for a draft (beside the console's binding test of a deploy, `POST /api/deploys/{id}/binding-test`), so **Test** works before publish in any channel that binds the slot | none | S | a test through the control service: a draft with no build tests its slot in `dev` against a Runner that binds it; the steps come back and no row; a channel that does not bind the slot says so |
| AUTHORING-19 | **Set up for development**: from a draft on a Workspace screen, the person types a login once; the screen sends it to the console, which stores it in the project's Runner's secret store for the channel and the slot (built: `POST /api/workspace-dev-logins/...`, `ConsoleDevelopmentLogins`, `developmentLoginApi` of `@dbdatakit/workspace`; `docs/authoring-metadata.md`, "How a screen sends a development login"); the control service puts the connection's copy on the Runner, its password naming the stored secret, and binds the slot in a non-production channel (CHANNELS-1's client); the control service checks the person's role and refuses `prod`; the Workspace service only learns which binding exists | CHANNELS-1 | M | a test: set up `crm` for `dev`, the Runner's Bindings show it; the login reaches no Workspace service; `prod` is refused naming the rule |
| AUTHORING-6 | The channel picker and Refresh exist (`ChannelPicker`, `CatalogRefresh` of `@dbdatakit/workspace`; the tab **Catalog**'s **Refresh from a Runner**). The rest of the Designer's shared pieces of authoring-time reads (`DbDataKit.Authoring`, `Shared/web/workspace`), used by every kind's Designer and Files area: **Describe** (a query's columns into the kind's file, read through the console as Refresh is), **Test** (AUTHORING-18), **Set up for development** offered by the channel picker when the channel does not bind the slot (AUTHORING-19), and the newest-check card (the newest `preflight` or test run per channel, read through the control service (the console's run calls, `/api/deploys/{id}/runs`): time, run link, findings); the console's posture page lists the non-production bindings made from the Workspace. The Gateway refresh stays as a second way to fill the cache for a connection that is already a Gateway data source; nothing new is built on it | AUTHORING-18, AUTHORING-19 | M | vitest of each piece; Playwright: a build draft with no binding offers Set up for development, then Refresh fills the Catalog tab; Test shows its steps; the posture page lists the binding |
| AUTHORING-7 | Retire the offline verb `project publish --to gateway --out` and the per-publish adopt confirmation (adapter first: the slot bindings' tests have run) | PUBLISHING-2 | S | the verb and `adopt` answer their sentence; `gateway-publish/1` leaves `docs/formats.json` and `docs/ui-editors.md`; the Workspace's publish tests pass unchanged |
| AUTHORING-9 | Browser stories for the Workspace screens never opened in a browser: column lineage, model files from cached tables, the provisioning plan, scripts, the slot bindings screen, the Sync forms, the catalog refresh form; and the stories written and never run in CI: `workspace-authoring.spec.ts` (the kind picker on a new project with what each kind publishes to, check, fix, and the publish panel by product for a build, a sync and a store project), `workspace-publish.spec.ts` (create, edit, build, preview, commit, merge, publish an app) and the preview with live data; each listed in `docs/ui-coverage.md` | none | M | the Playwright specs run in CI; `scripts/check_ui_coverage.py` passes with the stories named |
| AUTHORING-10 | The secrets overview of a Workspace project's connections (decided 2026-11-12, not built): every secret reference of the project, where it is bound, whether the target holds it (read by the control service from the Runner, never by the Workspace service) | none | S | vitest and a Playwright story: a project with three references shows each with its binding and state |
| AUTHORING-12 | Resync and reconcile-deletes buttons on a sync project's Runs tab, started through the control service (the console's run calls, `/api/deploys/{id}/runs`) (paused, owner 2026-11-16; queued until the drain works out a mapping's source key) | none | M | Sync conformance: resync of one mapping reloads it; reconcile removes target rows the source lost |
| AUTHORING-13 | The form editor of the `access:` section (rules tables, row rules, column rules) in the Workspace's build and store model panels | none | M | vitest: the form round-trips a model's `access:` section; Playwright: add a column rule, see the check's finding for a missing rules table, publish |
| AUTHORING-15 | Browser stories of the console's own screens that are written and never run in CI, run there like AUTHORING-9: the agents' evaluation panel (`screens-agents-evaluation.spec.ts`), the Runner's Link flow, the store approvals (`store-approvals.spec.ts`), the monitoring story against real servers (`monitoring.spec.ts`), and the others `docs/ui-coverage.md` lists as not run | none | M | the Playwright specs run in a CI job of the console; `docs/ui-coverage.md` no longer lists them as not run |
| AUTHORING-16 | Browser stories of the Gateway's screens (shown by the console) that are written and never run in CI, and the missing ones: the semantic model browser and query explorer (`semantic-models-explorer.spec.ts`), the result cache screen (a dataset's hit rate rises after repeated queries, Clear empties it and the next query is a miss), click-to-sort of tables and matrices (`views-sort-print.spec.ts`), the stories that need a real Gateway (`views-apps-addresses`, `views-apps-embed`), and the `semantic-models.spec.ts` and `refresh-run.spec.ts` stories that need the test PostgreSQL | none | M | the Playwright specs run in a CI job against a real Gateway; the cache story passes; `docs/ui-coverage.md` no longer lists them as not run |
| AUTHORING-25 | On SQL Server, `discover` also lists every database the connection's login can open (`sys.databases`), each with its schemas, and reads the tables of the one asked for (an input `database`); the database pickers offer them. A PostgreSQL connection reads its one database, as today | none | M | a SQL Server conformance test: a login that reads two databases sees both in the cache, and the tables of the second after a read that names it |
| AUTHORING-26 | A read of a database's shape (`discover`, `describe`) needs the Runner's viewer role, not operator: it changes nothing, and the console has already checked that the person has access to the project | none | S | a person who is a viewer on the project's Runner refreshes the catalog; starting any other task still needs operator |

## Needs from other subjects

- Agents authoring Workspace projects (through MCP or the console's embedded agent, each change approved) and the build-helper on a Workspace build
  project are AGENTACCESS-8.
- PUBLISHING-2: the per-kind routes removed and every caller on the shared route, before AUTHORING-7.
- CHANNELS-1: the client that puts a connection copy on a Runner, sends its login and binds it for a channel, before AUTHORING-19.
- WORKSPACEISOLATION-3: the control service's deploy, before AUTHORING-1 and AUTHORING-2 (the target Runner's platform).
- The Gateway catalog refresh is the console's `POST api/workspace-catalog/{workspace}/{project}/gateway-read` (`ConsoleCatalogRead.GatewayAsync`; `docs/authoring-metadata.md`, "A read from a Gateway").

## Open designs

### The Runner's built-in package where a group requires signed versions

**What.** The Runner puts its built-in package `dbdatakit-metadata` in a channel through its own publish, so a group with `require_signatures` on
refuses it and a catalog read answers 409 naming the missing signature. The direction is to exempt that package, because it ships beside the Runner's
own program and is as trusted as the program, with the audit line of its install saying so. **Why it waits:** signatures are not built yet, so with
the setting on every publish is refused anyway, and the exemption is a supply chain exception to weigh again when they are. No code changes until then.

### AUTHORING-P12: a read of one table that no longer finds it (default, awaiting the owner)

Built as the default: a Refresh of one table whose read no longer finds the table (it was dropped or renamed) shows "1 table removed", and Accept takes
the table out of the cache. Scenario: the DBA drops `dbo.accounts`; Ana refreshes that table on the tab **Catalog**. Option A (built): the difference
says the table is removed; she accepts, and the draft's check then names every mapping over it. Option B: the read of one table never removes it: the
tab says the table was not found and leaves the cache as it was, and only a read of the whole connection removes a table. Recommendation: A, since the
difference is shown before anything is replaced and the person decides.

### AUTHORING-P13: the Runner the channel picker offers first (default, awaiting the owner)

Built as the default: the picker offers the Runner the cache says the connection was last read from; with none (a first read, or a cache read through a
Gateway), it offers the first Runner the console lists. Scenario: the console is paired with `runners-eu` and `runners-us`; Ana refreshes `crm` for the
first time. Option A (built): `runners-eu` is offered (the first listed) and she can pick the other. Option B: the project names its Runner (a setting of
the project, as its publish target is) and the picker offers that one. Option C: no Runner is offered until she picks one. Recommendation: B once a
project names its Runner (WORKSPACEISOLATION's deploy will know it); A until then.

### AUTHORING-P14: a channel that does not bind the slot, in the picker (default, awaiting the owner)

Built as the default: such a channel is listed as "not bound", cannot be picked, and the picker says how an administrator binds it. Scenario: the package
`crm-to-dw` is on the Runner in `dev` and `test`, and only `dev` binds `crm`. Option A (built): `test` is listed and greyed out, with the sentence on how
to bind it. Option B: `test` can be picked, and Refresh answers the Runner's own refusal ("has no binding in channel 'test'"). Option C: such a channel
is not listed at all. Recommendation: A until Set up for development (AUTHORING-19) is built; then picking it offers Set up for development, as
AUTHORING-6 says.

### AUTHORING-P17: what in a Build project's status is an error (default, awaiting the owner)

Built as the default: in the refresh package's preflight, an object of the project that is not on the connection, a deployed structure behind the
project (pending deploy steps), a blocked deploy step and a status that could not be read are errors; an object changed outside dbdatabuild is a
warning; what the status noticed is information. A preflight run that fails counts as an error. Scenario: Ana adds a column to `marts.fct_orders`
and serves the new package in `prod` before `apply_plan` has run. Option A (built): the pending step is an error, the version is not activated until
she confirms, and the refresh would have been refused anyway. Option B: pending steps are a warning, the version is activated, and the next refresh
fails naming the structure. Recommendation: A.

## Shared areas touched

- `DbDataKit.Authoring`, `DbDataKit.Authoring.Server`, `Shared/web/workspace`, `Shared/web/console` (posture, secrets overview), `DbDataKit.Agents`.
- `docs/formats.json` and `docs/ui-editors.md` (`gateway-publish/1` removed, `catalog-cache/2`), `docs/ui-coverage.md`, `docs/authoring-metadata.md`.
- The Runner (`DbDataKit.Runner`: the built-in package `dbdatakit-metadata`, the binding test by slot, the held staged version), `DbDataKit.Runner.Client`.
- The console's host (`DbDataKit.Server`: the catalog read from a Runner channel).
