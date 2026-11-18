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
- `dbdatasync provisioning plan --from-cache` plans the target side offline; the source side (change tracking, logical slots) is `unknown`.
- A model's `queries:` are edited as YAML only (the Build server's editor went with the server).
- The offline verb `dbdatabuild project publish --to gateway --out` and the per-publish adopt confirmation of a hand-made data source still exist beside
  the slot bindings that replace them.
- The console's build-helper agent installs itself on a Build server project (`ConsoleAgents.InstallBuildHelperAsync`), which no longer exists.
- Many Workspace screens are tested by vitest only, never opened in a browser (`docs/ui-coverage.md`); many browser stories of the console's and
  the Gateway's screens are written and have never run in CI.
- The Sync forms offer a fixed list of engines (`SyncSetPanel.tsx`), not the engine registry's `sync_source` and `sync_target` roles.
- Resync and reconcile-deletes buttons of a sync project's Runs tab are paused (owner, 2026-11-16).

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| AUTHORING-1 | The Workspace builds the operations package beside the refresh package and publishes it to the same channel; the Runs tab offers `init`, `seed`, `apply_plan` and `ack` with their inputs | none | M | `WorkspaceBuildPublishTests`: a build stages both packages; vitest of `ProjectRunsPanel`: the four tasks are offered; a conformance run of `seed` from the Workspace route |
| AUTHORING-2 | Sync publish reads the target Runner's platform from its `/version` and rebuilds for it; `WorkspaceSettings.SyncPlatform` goes | none | S | a test publishes to a fake Runner reporting `linux-x64` and the package is built for it; the setting is refused naming the file |
| AUTHORING-3 | The mapping preview and the strategy try-run as Runner tasks of the published set, the rows kept where AUTHORING-P1 says | AUTHORING-P1 | M | a Sync conformance test: the preview of a mapping returns its first rows through the Runner; the try-run's SQL runs against the bound login |
| AUTHORING-4 | The source side of the provisioning plan as a read-only Runner task (change tracking, CDC, `wal_level`, slots) | none | M | Sync conformance on SQL Server and PostgreSQL: a source without change tracking is `missing`, with it `satisfied` |
| AUTHORING-5 | A form editor for a model's `queries:` in a build project (name, description, `sql` or `request`, parameters), checked as you type | none | M | vitest on the form and its YAML; Playwright: add a query with a list parameter, see a translation error at its line, fix it, publish |
| AUTHORING-6 | Authoring-time read logins where AUTHORING-P2 says; the console's posture page lists each Workspace-held login. The catalog cache's producer follows the same answer: today a paired Gateway answers the Workspace's read-only schema call (`POST /api/v1/data-sources/{name}/schema`, never a row or a login) and the answer is committed into the draft as `catalog-cache.yaml`; a Sync project's connection is read through a Gateway data source of the same name, since no server of Sync answers it | AUTHORING-P2 | M | written for the answer to AUTHORING-P2 (its "if accepted" list replaces this row when it is answered); until then: the catalog refresh runs once by hand against a real database and its file passes the strict reader |
| AUTHORING-7 | Retire the offline verb `project publish --to gateway --out` and the per-publish adopt confirmation (adapter first: the slot bindings' tests have run) | PUBLISHING-2 | S | the verb and `adopt` answer their sentence; `gateway-publish/1` leaves `docs/formats.json` and `docs/ui-editors.md`; the Workspace's publish tests pass unchanged |
| AUTHORING-8 | The build-helper agent works on a Workspace build project: `InstallBuildHelperAsync` targets a Workspace project through its relay; the relay to the deleted Build server goes | none | S | `ConsoleAgentsTests`: the helper is installed on a Workspace build project and reads its files; no code path names a Build server |
| AUTHORING-9 | Browser stories for the Workspace screens never opened in a browser: column lineage, model files from cached tables, the provisioning plan, scripts, the slot bindings screen, the Sync forms, the catalog refresh form; and the stories written and never run in CI: `workspace-authoring.spec.ts` (the kind picker on a new project with what each kind publishes to, check, fix, and the publish panel by product for a build, a sync and a store project), `workspace-publish.spec.ts` (create, edit, build, preview, commit, merge, publish an app) and the preview with live data; each listed in `docs/ui-coverage.md` | none | M | the Playwright specs run in CI; `scripts/check_ui_coverage.py` passes with the stories named |
| AUTHORING-10 | The secrets overview of a Workspace project's connections (decided 2026-11-12, not built): every secret reference of the project, where it is bound, whether the target holds it | none | S | vitest and a Playwright story: a project with three references shows each with its binding and state |
| AUTHORING-11 | Agents author through MCP with every step approved (the remaining half of 22a GP36): the Workspace's project commands offered to an outside agent, each change approved by the person | none | M | an MCP test drives create, edit, build and publish of a build project; each change waits for an approval; a denied step changes nothing |
| AUTHORING-12 | Resync and reconcile-deletes buttons on a sync project's Runs tab (paused, owner 2026-11-16; queued until the drain works out a mapping's source key) | none | M | Sync conformance: resync of one mapping reloads it; reconcile removes target rows the source lost |
| AUTHORING-13 | The form editor of the `access:` section (rules tables, row rules, column rules) in the Workspace's build and store model panels | none | M | vitest: the form round-trips a model's `access:` section; Playwright: add a column rule, see the check's finding for a missing rules table, publish |
| AUTHORING-14 | A sync project's engine lists come from the engine registry: the Sync forms offer as a source the engines whose `sync_source` role is supported and as a target those whose `sync_target` is, with the role's status and note, instead of the list written in `SyncSetPanel.tsx` (the Sync server read the registry, `SyncEngines`, and went with it); a user-written driver is shown as custom | none | S | vitest: the source and target lists equal the registry's roles; an engine whose `sync_target` is `planned` is shown as such and not offered as a target; a YAML driver is listed as custom |
| AUTHORING-15 | Browser stories of the console's own screens that are written and never run in CI, run there like AUTHORING-9: the agents' evaluation panel (`screens-agents-evaluation.spec.ts`), the Runner's Link flow, the store approvals (`store-approvals.spec.ts`), the monitoring story against real servers (`monitoring.spec.ts`), and the others `docs/ui-coverage.md` lists as not run | none | M | the Playwright specs run in a CI job of the console; `docs/ui-coverage.md` no longer lists them as not run |
| AUTHORING-16 | Browser stories of the Gateway's screens (shown by the console) that are written and never run in CI, and the missing ones: the semantic model browser and query explorer (`semantic-models-explorer.spec.ts`), the result cache screen (a dataset's hit rate rises after repeated queries, Clear empties it and the next query is a miss), click-to-sort of tables and matrices (`views-sort-print.spec.ts`), the stories that need a real Gateway (`views-apps-addresses`, `views-apps-embed`), and the `semantic-models.spec.ts` and `refresh-run.spec.ts` stories that need the test PostgreSQL | none | M | the Playwright specs run in a CI job against a real Gateway; the cache story passes; `docs/ui-coverage.md` no longer lists them as not run |

## Needs from other subjects

- PUBLISHING-2: the per-kind routes removed and every caller on the shared route, before AUTHORING-7.

## Proposed decisions

### AUTHORING-P1: where a mapping preview's rows may be kept (awaiting the owner)

Option A: in the Runner's run record as the step result (kept under retention). Option B: only in the answer of the call, never stored. Option C: no preview.
Scenario: an engineer previews `customers` (20 rows, one with an email address). Under A the rows are readable later by anyone who can read that run; under
B they are shown once and gone; under C the engineer runs a trial run instead. Recommendation: B.

### AUTHORING-P2: who reads a database's metadata while a project is authored, and where the answer is kept (awaiting the owner)

Owner, 2026-11-18: "For sync authoring, we need to address how discovering and checking the metadata / config of a source and target are handled; those
will require some further design to make sure it makes sense." This proposal is the rule for every project kind (build, sync, store); what only a sync
project checks is SYNCAUTHORING-P9, which applies this rule.

**What holds today.** Control (the console and the Workspace) holds no customer-data login (`docs/architecture.md`, "Isolation by trust"); Work (the
Runner) holds the logins bound to a package's slots, per channel; Serving (the Gateway) holds the read logins of its data sources. Three ways to read a
database exist: the Workspace's catalog refresh asks a paired Gateway (`POST /api/v1/data-sources/{name}/schema`, with the Gateway's own login: tables,
columns, nullability and primary key, no row) and commits the answer as `catalog-cache.yaml` (`catalog-cache/1`); the Runner's slot binding test
(`POST .../bindings/{channel}/integration/{name}/test`, relayed by the Workspace's `.../builds/{build}/binding-test`; owner, 2026-11-14: "through the
Runner, after publish only") opens the login a channel binds; a staged version's manual tasks run before it is served (`.../builds/{build}/run`: Sync's
`provision`, Build's `status` and `check`, the Store's `deploy` dry run and `contract` plan). Sync's documentation still says its read login lives where
browsing is made, the Workspace (`DbDataSync/docs/configuration.md`, "Two logins"), which the place rule no longer allows. The old Sync server browsed
with a read login it held, and its connection test showed up to 20 sample rows.

**Who connects: the options.**

| Option | Who opens the connection, with which login | Fits the place rule | Cost and limits |
| --- | --- | --- | --- |
| A. Runner tasks | the Runner, in a process per task, with the login the channel binds to the project's slot (its `read` role where one is bound, else `default`); the run is recorded as the person who asked | yes: the login stays on Work | a run per question (queue and process start: seconds, not keystrokes); needs a binding in some channel first; before a version of the project exists, a task of a built-in package (below) instead of the project's own |
| B. The Workspace with its own read logins | the Workspace, in its process, with a read login per connection (or, as the earlier form of this proposal had it, per development connection only) kept in its secret store | no: Control would hold customer-data logins, in the server that also runs authors' build children and script compiles; the architecture would be amended | fastest (a picker can ask as you type); a second copy of each login to rotate and to list on the posture page; it reads with another login than the run uses |
| C. A paired Gateway's schema call (built) | the Gateway, with its data source's read login | yes, for a source the Gateway already serves | only a connection that is a Gateway data source; a Sync target, or an operational source the Gateway does not serve, would need a data source on Serving made only for authoring (a login on the public tier that serves nobody); the Gateway holds no write login, so it cannot say what the run login may write or create; it reads through `DbDataKit.Metadata`, not Sync's drivers or `metadataProvider` scripts |
| D. The person's own login, typed in the Designer, used once | whoever runs the read (the Workspace, or a Runner task the login is handed to), with what the person typed; kept nowhere | only when the Runner connects (D through A); the Workspace connecting with it is a customer-data login in Control, if only for one call | discovery with no administrator's setup at all; but what the person may see is not what the bound login may (Ana's own login sees `hr.salaries`, the run's does not: the cache would offer a table the run cannot read); integrated sign-in (Windows, Entra) cannot be typed or delegated from a browser to a server; a password typed into a web page at every refresh |

**Scenarios, one per case.** Each follows the recommendation below; where another option differs, it is said.

1. *Discovery of a new source before anything is published.* Ana makes the sync project `crm-to-dw` and adds the connection `crm` (engine, address,
   database, a secret reference: files only). On **Refresh catalog** the Designer asks which channel to read from. The project's Runner has no binding of
   `crm` in `dev`, so it offers **Set up crm for development**: Ana types the development login once; the Workspace hands the connection copy and that
   login to the Runner and binds the slot `crm` in `dev` (CHANNELS-1's client; the login is sealed to the Runner group and kept in its store, never in the
   Workspace's state, audit or log; a non-production channel only, production's bindings stay the administrator's). The Runner then runs the task
   `discover` of its built-in package `dbdatakit-metadata` in `dev` with `crm`'s login, for Ana: databases, schemas, tables and views, columns with their
   type text, nullability, keys and foreign keys, within the schemas or name pattern she gave. The Workspace waits up to 10 seconds, else shows the run's
   link and takes the answer when the run ends; it writes the answer into the draft's `catalog-cache.yaml` (an unsaved edit, committed with the draft).
   Under B the Workspace reads with its own copy of a login; under C Ana first needs a Gateway data source `crm`; under D she types her login at every
   refresh.
2. *Discovery of a new target.* The same, for the connection `dw` and its slot; the cache then says whether `sales.customers` exists, which the offline
   provisioning plan reads. A target is never made a Gateway data source for this (C does not apply).
3. *A query's columns.* A Build model's query or a Sync query source: **Describe** runs `describe` of the built-in package in the chosen channel: the
   read-only policy (`QueryProbe.RequireReadOnly`) first, then the engine's describe (`sp_describe_first_result_set`, PostgreSQL's schema-only command, a
   wrapped zero-row probe elsewhere). The columns go where the kind keeps them (a Sync mapping's column cache, a Build model's descriptor); no row is read.
4. *Refreshing after the database changed.* A DBA adds `crm.dbo.accounts.segment`. Ana presses **Refresh** on the table or on the connection; the task
   reads again in the channel the cache says it was read from, and the Designer shows the difference (columns added, removed, retyped) before the cache is
   replaced; the draft's check then names every mapping or model the change touches. Nothing refreshes by itself.
5. *A connection test.* **Test** on a connection runs the Runner's slot binding test in a channel: the binding, the connection document, the login's
   secrets, then a connect (reach, TLS, login, the server's version), each step said; no row and no sample (a configured test query is run only as the
   probe; its rows are neither shown nor kept). Before publish it needs a binding in a channel, not a staged version: this asks the owner to widen
   2026-11-14's "after publish only" to "once the slot is bound in a channel", since the binding test reads the binding, not a version.
6. *Checking before publish to production.* The publish panel stages the version in `prod` (staged, not served), runs the package's own read-only manual
   task `preflight` there with production's bound logins (each kind's checks; Sync's are SYNCAUTHORING-P9), shows its findings, and activates only when the
   person goes on: a warning shows with its run link, an error refuses activation unless the person confirms, which is recorded. After publish the same task
   is **Re-check** on the Runs tab.
7. *Development and production.* The Designer reads by channel: the picker defaults to the first non-production channel that binds the slot, and the cache
   records, per connection, the channel and Runner group it was read from, so a mapping made against `dev`'s shape is checked against `prod`'s by
   `preflight` at staging. Discovery in `prod` (scenario 1 against production's binding) is allowed to the project's approvers, as the binding test is
   today; in other channels to its authors.
8. *A person's own credentials against the bound login.* Reads use only the bound login, so the cache and the checks say what the run will be able to do.
   A login a person types only becomes a non-production channel's bound login on the Runner (scenario 1); it is never used for a read of its own.

**Offline and live.** From the committed cache, with no connection, as the person types: the pickers (databases, schemas, tables, columns), a mapping's or
a model's columns and types, the draft's check (a named table or column missing from the cache, a type the engine's type map does not know, key columns),
the provisioning plan's target side, Build's `project import --from-cache`. Live, as a Runner run in a channel: discovery, a query's columns, the
connection test, and everything that depends on the login or on the server's settings (privileges, change tracking and capture, `wal_level`, slots, the
state's schema), through `preflight` or SYNCAUTHORING-P9's checks.

**What is kept where.** The shape: `catalog-cache.yaml`, committed in the draft, so builds, reviews and agents read the same thing. A query's columns: in
the kind's own file. Check findings and test steps: the Runner's run record (step outputs, under its retention); a Designer card shows the newest run per
channel with its time and link; nothing of it is committed, since it describes a login at a moment, not the project. Never kept or shown: a row or a value
of a table, a sample, a statistic of values (minimum, maximum, distinct values, segment bounds: AUTHORING-P1's question), a login, a connection string or
an address (the cache's strict reader already refuses them by key and by look).

**Every kind uses the same pieces.** Build: discovery of source tables into the cache, then `project import --from-cache` (built) writes mapped models; its
target's state is the refresh package's `status` task (built), which becomes Build's `preflight`. Store: a model's storage connection is tested (scenario 5)
and checked by the model package's `deploy` dry run and `contract` plan (built), which are its `preflight`; it browses nothing. Sync: SYNCAUTHORING-P9. The
built-in package, the cache, the channel picker, the test and the check card are one implementation (`DbDataKit.Authoring`, `Shared/web/workspace`) that
each kind's Designer and Files view use.

**Recommendation.** A for every read, through a binding in a channel: discovery, a query's columns and the test with the built-in package's tasks; the
checks with each package's `preflight` on its staged version. C stays as a second producer of the cache for a connection that is already a Gateway data
source (built, and the natural source of a Build model over a served warehouse); nothing new is built on it. D only as the way a person enters a
development login into the Runner (scenario 1), never as a read. B not at all: the Workspace keeps holding no login, so the posture page has none to list,
and Sync's "Two logins" section is corrected (the read login is the `read` role of a Runner binding, or the run login when none is bound).

**If accepted, the tasks** (rows once answered):

- The Runner's built-in package `dbdatakit-metadata` (tasks `discover`, `describe`, `facts`), shipped with the Runner, on `DbDataKit.Metadata`'s readers:
  started for a project's package, channel and slot, resolved through that slot's binding as the binding test resolves it (the `read` role, else
  `default`), refused for a slot the caller's package does not declare; it answers shape only; run as the person; an approver for `prod`.
- The binding test without a staged version (by package, channel and slot), relayed by a Workspace route of the draft rather than of a build.
- Setting up a slot in a non-production channel from the Designer: the connection copy, a login typed once and sent to the Runner, the binding
  (CHANNELS-1's client; the Workspace checks the person's role and refuses `prod`).
- The Workspace's catalog refresh from a Runner channel beside the Gateway one (`POST .../catalog/refresh` with a Runner, a channel and a connection), the
  difference shown before the cache is replaced, for one table or one connection.
- `catalog-cache/2`: the databases and schemas of a connection (empty ones too), foreign keys, and per connection where and when it was read
  (`{server, product, channel}`); `catalog-cache/1` refused naming the file (`docs/formats.json`, the C# reader and `catalogCache.ts` together).
- `preflight` as each package's read-only manual task (Build: `status` under that name; Store: the `deploy` dry run and the `contract` plan; Sync:
  SYNCAUTHORING-P9), started by the publish panel on the staged version before activation and by **Re-check**; findings as step outputs.
- The Designer's shared pieces: the channel picker, **Refresh**, **Describe**, **Test**, the newest-check card. AUTHORING-6 is rewritten to these; its
  posture-page line becomes "lists the non-production bindings made from the Workspace".
- Documentation: `DbDataSync/docs/configuration.md` ("Two logins"), `docs/authoring-parity.md` (what is read where), `docs/ui-editors.md` (the cache's row).

## Shared areas touched

- `DbDataKit.Authoring`, `DbDataKit.Authoring.Server`, `Shared/web/workspace`, `Shared/web/console` (posture, secrets overview), `DbDataKit.Agents`.
- `docs/formats.json` and `docs/ui-editors.md` (`gateway-publish/1` removed), `docs/ui-coverage.md`.
