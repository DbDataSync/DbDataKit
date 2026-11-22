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
- `dbdatasync provisioning plan --from-cache` plans the target side offline; the source side (change tracking, logical slots) is `unknown` (SYNCAUTHORING-9's `preflight` checks it live).
- A model's `queries:` are edited as YAML only (the Build server's editor went with the server).
- The offline verb `dbdatabuild project publish --to gateway --out` and the per-publish adopt confirmation of a hand-made data source still exist beside
  the slot bindings that replace them.
- The console's build-helper agent installs itself on a Build server project (`ConsoleAgents.InstallBuildHelperAsync`), which no longer exists.
- Many Workspace screens are tested by vitest only, never opened in a browser (`docs/ui-coverage.md`); many browser stories of the console's and
  the Gateway's screens are written and have never run in CI.
- Resync and reconcile-deletes buttons of a sync project's Runs tab are paused (owner, 2026-11-16).

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| AUTHORING-1 | The Workspace builds the operations package beside the refresh package and publishes it to the same channel; the Runs tab offers `init`, `seed`, `apply_plan` and `ack` with their inputs | none | M | `WorkspaceBuildPublishTests`: a build stages both packages; vitest of `ProjectRunsPanel`: the four tasks are offered; a conformance run of `seed` from the Workspace route |
| AUTHORING-2 | Sync publish reads the target Runner's platform from its `/version` and rebuilds for it; `WorkspaceSettings.SyncPlatform` goes | none | S | a test publishes to a fake Runner reporting `linux-x64` and the package is built for it; the setting is refused naming the file |
| AUTHORING-3 | The mapping preview and the strategy try-run as Runner tasks of the published set, the rows kept where AUTHORING-P1 says | AUTHORING-P1 | M | a Sync conformance test: the preview of a mapping returns its first rows through the Runner; the try-run's SQL runs against the bound login |
| AUTHORING-5 | A form editor for a model's `queries:` in a build project (name, description, `sql` or `request`, parameters), checked as you type | none | M | vitest on the form and its YAML; Playwright: add a query with a list parameter, see a translation error at its line, fix it, publish |
| AUTHORING-17 | Adds to the Runner's built-in package `dbdatakit-metadata` (its tasks `discover` and `describe` exist: `DbDataKit.Runner.Metadata`, the route `POST /api/v1/metadata/{channel}/{task}`, `docs/authoring-metadata.md`). Its task `facts`: the server's version and the database settings a kind's checks name, on `DbDataKit.Metadata`'s readers, started, resolved, refused and recorded as `discover` and `describe` are; never a value of a table | none | S | Runner tests on SQL Server and PostgreSQL: `facts` in `dev` answers the server's version and, on SQL Server, whether change tracking is on for the database; an author's `facts` in `prod` is refused |
| AUTHORING-18 | The binding test without a staged version: by package, channel and slot on the Runner, relayed by a Workspace route of the draft (beside today's `.../builds/{build}/binding-test`), so **Test** works before publish in any channel that binds the slot | none | S | a Workspace test: a draft with no build tests its slot in `dev` against a Runner that binds it; the steps come back and no row; a channel that does not bind the slot says so |
| AUTHORING-19 | **Set up for development**: from a draft, put the connection's copy on the project's Runner, send a login the person types once, and bind the slot in a non-production channel (CHANNELS-1's client); the Workspace checks the person's role and refuses `prod`; the login is sealed to the Runner group and never written to the Workspace's state, audit or log | CHANNELS-1 | M | a Workspace test: set up `crm` for `dev`, the Runner's Bindings show it; the typed password appears in no Workspace table, audit line or log; `prod` is refused naming the rule |
| AUTHORING-20 | **Needed first by SYNCAUTHORING's first slice** (SYNCAUTHORING-15, SYNCAUTHORING-16). `catalog-cache/2`: the databases and schemas of a connection (empty ones too), foreign keys, and per connection where and when it was read (`{server, product, channel}`); `catalog-cache/1` refused naming the file; `docs/formats.json`, the C# reader (`DbDataKit.Catalog.Cache`) and `catalogCache.ts` together; the `catalog-cache` row of `docs/ui-editors.md` | none | S | the strict reader's tests on both readers: a `/2` file round-trips, a `/1` file is refused naming it, a row or an address is still refused; `scripts/check_formats.py` passes with the new entry |
| AUTHORING-21 | **Needed first by SYNCAUTHORING's first slice** (SYNCAUTHORING-15, SYNCAUTHORING-16). The catalog refresh from a Runner channel beside the Gateway one (`POST .../catalog/refresh` with a Runner, a channel and a connection, running the built-in package's `discover` through the Runner's `POST /api/v1/metadata/{channel}/discover`), for one connection or one table: the Workspace waits up to 10 seconds, else answers the run's link and takes the answer when it ends; the difference (added, removed, retyped) is answered before the cache is replaced, and the draft's check names what the change touches; `docs/authoring-parity.md` says what is read where | AUTHORING-20 | M | a Workspace test against a real Runner and PostgreSQL: refresh writes a `/2` cache recorded as read from `dev`; a column added to the table is reported as the difference of a second refresh |
| AUTHORING-24 | **Needed first by SYNCAUTHORING's first slice** (SYNCAUTHORING-15, SYNCAUTHORING-16). (the part of AUTHORING-6 the slice builds). The shared pieces of a refresh in `Shared/web/workspace`, used by every kind's Designer and Files area: the channel picker (the first non-production channel that binds the slot by default; a channel that binds no slot is named with how to bind one, as `docs/authoring-metadata.md` says until AUTHORING-19) and **Refresh** of a connection or one table through AUTHORING-21's route (waiting, then the run's link after 10 seconds; the difference, added, removed and retyped, shown before the cache is replaced, then accepted or dismissed). The Files area's Catalog tab offers the same Refresh beside its Gateway refresh. Left for AUTHORING-6: Describe, Test, Set up for development, the newest-check card, the posture page | AUTHORING-21 | S | vitest of the picker and of Refresh (its difference and accept); Playwright against `workspace-fake.ts` and a fake Runner relay: Refresh of a table on the Catalog tab shows "1 column added", and accepted, the cache is an unsaved edit recorded as read from `dev` |
| AUTHORING-6 | Follows AUTHORING-24 (the channel picker and Refresh exist). The rest of the Designer's shared pieces of authoring-time reads (`DbDataKit.Authoring`, `Shared/web/workspace`), used by every kind's Designer and Files area: **Describe** (a query's columns into the kind's file), **Test** (AUTHORING-18), **Set up for development** offered by the channel picker when the channel does not bind the slot (AUTHORING-19), and the newest-check card (the newest `preflight` or test run per channel: time, run link, findings); the console's posture page lists the non-production bindings made from the Workspace. The Gateway refresh stays as a second way to fill the cache for a connection that is already a Gateway data source; nothing new is built on it | AUTHORING-24, AUTHORING-18, AUTHORING-19 | M | vitest of each piece; Playwright: a build draft with no binding offers Set up for development, then Refresh fills the Catalog tab; Test shows its steps; the posture page lists the binding |
| AUTHORING-22 | `preflight` as each package's read-only manual task (Build: its `status` under that name; the Store: the `deploy` dry run and the `contract` plan; Sync's is SYNCAUTHORING-9), started by the publish panel on the version staged in `prod` before activation (a warning shown with its run link; an error refuses activation unless the person confirms, and the confirmation is recorded) and by **Re-check** on the Runs tab after publish; findings as step outputs | none | M | `WorkspaceBuildPublishTests`: publishing a build to `prod` runs `preflight` on the staged version before activating; an error finding stops activation until confirmed, and the confirmation is in the Runner's record; Playwright: Re-check shows the findings |
| AUTHORING-7 | Retire the offline verb `project publish --to gateway --out` and the per-publish adopt confirmation (adapter first: the slot bindings' tests have run) | PUBLISHING-2 | S | the verb and `adopt` answer their sentence; `gateway-publish/1` leaves `docs/formats.json` and `docs/ui-editors.md`; the Workspace's publish tests pass unchanged |
| AUTHORING-8 | The build-helper agent works on a Workspace build project: `InstallBuildHelperAsync` targets a Workspace project through its relay; the relay to the deleted Build server goes | none | S | `ConsoleAgentsTests`: the helper is installed on a Workspace build project and reads its files; no code path names a Build server |
| AUTHORING-9 | Browser stories for the Workspace screens never opened in a browser: column lineage, model files from cached tables, the provisioning plan, scripts, the slot bindings screen, the Sync forms, the catalog refresh form; and the stories written and never run in CI: `workspace-authoring.spec.ts` (the kind picker on a new project with what each kind publishes to, check, fix, and the publish panel by product for a build, a sync and a store project), `workspace-publish.spec.ts` (create, edit, build, preview, commit, merge, publish an app) and the preview with live data; each listed in `docs/ui-coverage.md` | none | M | the Playwright specs run in CI; `scripts/check_ui_coverage.py` passes with the stories named |
| AUTHORING-10 | The secrets overview of a Workspace project's connections (decided 2026-11-12, not built): every secret reference of the project, where it is bound, whether the target holds it | none | S | vitest and a Playwright story: a project with three references shows each with its binding and state |
| AUTHORING-11 | Agents author through MCP with every step approved (the remaining half of 22a GP36): the Workspace's project commands offered to an outside agent, each change approved by the person | none | M | an MCP test drives create, edit, build and publish of a build project; each change waits for an approval; a denied step changes nothing |
| AUTHORING-12 | Resync and reconcile-deletes buttons on a sync project's Runs tab (paused, owner 2026-11-16; queued until the drain works out a mapping's source key) | none | M | Sync conformance: resync of one mapping reloads it; reconcile removes target rows the source lost |
| AUTHORING-13 | The form editor of the `access:` section (rules tables, row rules, column rules) in the Workspace's build and store model panels | none | M | vitest: the form round-trips a model's `access:` section; Playwright: add a column rule, see the check's finding for a missing rules table, publish |
| AUTHORING-15 | Browser stories of the console's own screens that are written and never run in CI, run there like AUTHORING-9: the agents' evaluation panel (`screens-agents-evaluation.spec.ts`), the Runner's Link flow, the store approvals (`store-approvals.spec.ts`), the monitoring story against real servers (`monitoring.spec.ts`), and the others `docs/ui-coverage.md` lists as not run | none | M | the Playwright specs run in a CI job of the console; `docs/ui-coverage.md` no longer lists them as not run |
| AUTHORING-16 | Browser stories of the Gateway's screens (shown by the console) that are written and never run in CI, and the missing ones: the semantic model browser and query explorer (`semantic-models-explorer.spec.ts`), the result cache screen (a dataset's hit rate rises after repeated queries, Clear empties it and the next query is a miss), click-to-sort of tables and matrices (`views-sort-print.spec.ts`), the stories that need a real Gateway (`views-apps-addresses`, `views-apps-embed`), and the `semantic-models.spec.ts` and `refresh-run.spec.ts` stories that need the test PostgreSQL | none | M | the Playwright specs run in a CI job against a real Gateway; the cache story passes; `docs/ui-coverage.md` no longer lists them as not run |

## Needs from other subjects

- PUBLISHING-2: the per-kind routes removed and every caller on the shared route, before AUTHORING-7.
- CHANNELS-1: the client that puts a connection copy on a Runner, sends its login and binds it for a channel, before AUTHORING-19.

## Proposed decisions

### AUTHORING-P1: where a mapping preview's rows may be kept (awaiting the owner)

Option A: in the Runner's run record as the step result (kept under retention). Option B: only in the answer of the call, never stored. Option C: no preview.
Scenario: an engineer previews `customers` (20 rows, one with an email address). Under A the rows are readable later by anyone who can read that run; under
B they are shown once and gone; under C the engineer runs a trial run instead. Recommendation: B.

### AUTHORING-P2: how `catalog-cache/2` lays out databases, and whether it keeps unique keys (default, awaiting the owner)

Built as the default: a connection has a flat `tables:` list as in format 1, each table naming its `database`, beside a `databases:` list of
`{name, schemas: [...]}` that holds the empty schemas too; a table must name a listed database and schema. Unique keys are kept (`unique_keys`) because
`discover` answers them and its answer is what the cache records (`docs/authoring-metadata.md`, "The built-in package"). Scenario: Ana refreshes `crm` (SQL Server, databases `crm` and
`crm_archive`, the schema `staging` empty). Option A (built): one list of tables, each with `database: crm`; `staging` appears only under `databases`.
Option B: tables nested under `databases[].schemas[].tables`, so the empty schema is a schema with `tables: []` and nothing is repeated; every reader walks
three levels. Option C: as A without `unique_keys` (a unique key is dropped from the answer). Recommendation: A, since every reader (Build's import, Sync's
provisioning plan, the pickers) works on a table list, and a diff of the file stays one line per table.

### AUTHORING-P3: a draft whose `catalog-cache.yaml` is still format 1 (default, awaiting the owner)

Built as the default: the file is refused naming it (the draft's check, the Catalog tab, `provisioning plan --from-cache`, `import --from-cache`) and a
refresh of any connection is refused too until the file is deleted, so nothing is converted or dropped silently. Scenario: Ana opens a draft committed last
week with a format-1 cache of `crm` and `dw`, and presses Refresh on `crm`. Option A (built): the refresh says the file is format 1 and to delete it; she
deletes it and refreshes both. Option B: the refresh replaces the whole file with only `crm`, saying `dw` was dropped. Option C: the reader converts a
format-1 file (no databases, `read` from its `source`, no channel). Recommendation: A for this release (no released install holds a cache yet); C if one
does by the next release.

### AUTHORING-P6: the task host runs with the machine's culture data (default, awaiting the owner)

Built as the default: the task host (`dbdatakit-taskhost`) no longer runs in invariant globalization mode, because Microsoft.Data.SqlClient refuses to open
a connection in that mode and `discover` and `describe` open SQL Server inside it. Scenario: a package's job writes `1234.5.ToString()` into a file, and the
runner's service account has the culture `de-DE`. Option A (built): the file says `1234,5`, as the same code does in any .NET program on that machine; a job
that writes for a program names `CultureInfo.InvariantCulture`. Option B: the task host stays invariant (`1234.5`), and the built-in package starts as an
executable of its own with its own runtime settings, built per platform (`win-x64`, `linux-x64`). Option C: the package sets SqlClient's view of the mode
from inside its process (an AppContext switch) and the task host stays invariant. Recommendation: A, since every task runs in a process of its own and the
runner itself already needs the culture data (ICU on Linux); C hides the mode from a library.

### AUTHORING-P7: the built-in package where the group requires signed versions (default, awaiting the owner)

Built as the default: the runner puts `dbdatakit-metadata` in a channel through its own publish, so a group with `require_signatures` on refuses it, and
the metadata route answers 409 naming the missing signature. Scenario: an administrator turned `require_signatures` on, and Ana presses Refresh catalog in
`dev`. Option A (built): the read is refused until signatures are turned off or the package is signed. Option B: the package the runner ships beside its
program is exempt, because it is the runner's own code, as trusted as the program; the audit line of its install says so. Recommendation: B once
signatures are built (today no version can be verified, so with the setting on every publish is refused anyway).

### AUTHORING-P8: `discover` reads the one database the connection opens (default, awaiting the owner)

Built as the default: `discover` lists the schemas of the database the connection opens and reads its tables; `databases` in the answer holds that one
database. Scenario: the connection `crm` (SQL Server) opens the database `crm`, and the same server holds `crm_archive`, which the login can read. Option A
(built): the cache lists `crm` only; Ana adds a connection `crm_archive` to read the other. Option B: `discover` also lists every database the login can
open (`sys.databases`, `pg_database`), each with its schemas, and reads tables in the one asked for (an input `database`); the database pickers then
offer `crm_archive` under `crm`. Recommendation: B for SQL Server, where one connection commonly reaches several databases; A elsewhere (a PostgreSQL
connection cannot read another database).

### AUTHORING-P9: which Runner role a read of a database's shape needs (default, awaiting the owner)

Built as the default: `POST /api/v1/metadata/{channel}/{task}` needs the operator role, as starting any task does; the console's token carries the person's
role on that Runner. Scenario: Ana authors the project `crm-to-dw`, and on the project's Runner she holds the viewer role only. Option A (built): her
Refresh catalog is refused with 403 until an administrator makes her an operator there. Option B: a read of shape needs the viewer role, since it starts
a run that changes nothing and the console has already checked that she has access to the project. Recommendation: B, matching "anyone with access to
the project may discover and describe".

### AUTHORING-P10: the built-in package carries every platform's drivers (default, awaiting the owner)

Built as the default: `builtin/dbdatakit-metadata/` is the portable publish of the package (about 60 MB, most of it the native libraries of SQL Server's
and SQLite's drivers for every platform). The runner stores it once in the group's state per version it installs, and every instance can run it.
Scenario: a group of one Windows and one Linux runner shares a state; the Linux runner installs the package in `dev`, and the Windows runner claims the
next read. Option A (built): the Windows runner runs it, its drivers being in the package. Option B: each runner ships the package built for its own
platform (about 25 MB) and a read is placed on a runner of the same platform as the version served. Recommendation: A while groups may mix platforms.

## Shared areas touched

- `DbDataKit.Authoring`, `DbDataKit.Authoring.Server`, `Shared/web/workspace`, `Shared/web/console` (posture, secrets overview), `DbDataKit.Agents`.
- `docs/formats.json` and `docs/ui-editors.md` (`gateway-publish/1` removed, `catalog-cache/2`), `docs/ui-coverage.md`, `docs/authoring-metadata.md`.
- The Runner (`DbDataKit.Runner`: the built-in package `dbdatakit-metadata`, the binding test by slot), `DbDataKit.Runner.Client`.
