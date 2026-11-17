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
- Many Workspace screens are tested by vitest only, never opened in a browser (`docs/ui-coverage.md`).
- Resync and reconcile-deletes buttons of a sync project's Runs tab are paused (owner, 2026-11-16).

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| AUTHORING-1 | The Workspace builds the operations package beside the refresh package and publishes it to the same channel; the Runs tab offers `init`, `seed`, `apply_plan` and `ack` with their inputs | none | M | `WorkspaceBuildPublishTests`: a build stages both packages; vitest of `ProjectRunsPanel`: the four tasks are offered; a conformance run of `seed` from the Workspace route |
| AUTHORING-2 | Sync publish reads the target Runner's platform from its `/version` and rebuilds for it; `WorkspaceSettings.SyncPlatform` goes | none | S | a test publishes to a fake Runner reporting `linux-x64` and the package is built for it; the setting is refused naming the file |
| AUTHORING-3 | The mapping preview and the strategy try-run as Runner tasks of the published set, the rows kept where AUTHORING-P1 says | AUTHORING-P1 | M | a Sync conformance test: the preview of a mapping returns its first rows through the Runner; the try-run's SQL runs against the bound login |
| AUTHORING-4 | The source side of the provisioning plan as a read-only Runner task (change tracking, CDC, `wal_level`, slots) | none | M | Sync conformance on SQL Server and PostgreSQL: a source without change tracking is `missing`, with it `satisfied` |
| AUTHORING-5 | A form editor for a model's `queries:` in a build project (name, description, `sql` or `request`, parameters), checked as you type | none | M | vitest on the form and its YAML; Playwright: add a query with a list parameter, see a translation error at its line, fix it, publish |
| AUTHORING-6 | Authoring-time read logins where AUTHORING-P2 says; the console's posture page lists each Workspace-held login | AUTHORING-P2 | M | a test: a development connection's browse runs in the Workspace with its login; a production connection's browse runs as a Runner task; the posture page lists the Workspace's logins |
| AUTHORING-7 | Retire the offline verb `project publish --to gateway --out` and the per-publish adopt confirmation (adapter first: the slot bindings' tests have run) | PUBLISHING-2 | S | the verb and `adopt` answer their sentence; `gateway-publish/1` leaves `docs/formats.json` and `docs/ui-editors.md`; the Workspace's publish tests pass unchanged |
| AUTHORING-8 | The build-helper agent works on a Workspace build project: `InstallBuildHelperAsync` targets a Workspace project through its relay; the relay to the deleted Build server goes | none | S | `ConsoleAgentsTests`: the helper is installed on a Workspace build project and reads its files; no code path names a Build server |
| AUTHORING-9 | Browser stories for the Workspace screens never opened in a browser: column lineage, model files from cached tables, the provisioning plan, scripts, the slot bindings screen, the Sync forms; each listed in `docs/ui-coverage.md` | none | M | the Playwright specs run in CI; `scripts/check_ui_coverage.py` passes with the stories named |
| AUTHORING-10 | The secrets overview of a Workspace project's connections (decided 2026-11-12, not built): every secret reference of the project, where it is bound, whether the target holds it | none | S | vitest and a Playwright story: a project with three references shows each with its binding and state |
| AUTHORING-11 | Agents author through MCP with every step approved (the remaining half of 22a GP36): the Workspace's project commands offered to an outside agent, each change approved by the person | none | M | an MCP test drives create, edit, build and publish of a build project; each change waits for an approval; a denied step changes nothing |
| AUTHORING-12 | Resync and reconcile-deletes buttons on a sync project's Runs tab (paused, owner 2026-11-16; queued until the drain works out a mapping's source key) | none | M | Sync conformance: resync of one mapping reloads it; reconcile removes target rows the source lost |
| AUTHORING-13 | The form editor of the `access:` section (rules tables, row rules, column rules) in the Workspace's build and store model panels | none | M | vitest: the form round-trips a model's `access:` section; Playwright: add a column rule, see the check's finding for a missing rules table, publish |

## Needs from other subjects

- PUBLISHING-2: the per-kind routes removed and every caller on the shared route, before AUTHORING-7.

## Proposed decisions

### AUTHORING-P1: where a mapping preview's rows may be kept (awaiting the owner)

Option A: in the Runner's run record as the step result (kept under retention). Option B: only in the answer of the call, never stored. Option C: no preview.
Scenario: an engineer previews `customers` (20 rows, one with an email address). Under A the rows are readable later by anyone who can read that run; under
B they are shown once and gone; under C the engineer runs a trial run instead. Recommendation: B.

### AUTHORING-P2: where authoring-time read logins live (awaiting the owner)

Option A: browsing and previews of production connections are Runner tasks; the Workspace holds a read login only for development connections, listed on
the posture page. Option B: the Workspace holds a read login per connection. Scenario: an author browses the columns of `dw.orders` in production. Under A
a Runner task reads them with the bound read login and the run names the author; under B the Workspace connects itself. Recommendation: A.

## Shared areas touched

- `DbDataKit.Authoring`, `DbDataKit.Authoring.Server`, `Shared/web/workspace`, `Shared/web/console` (posture, secrets overview), `DbDataKit.Agents`.
- `docs/formats.json` and `docs/ui-editors.md` (`gateway-publish/1` removed), `docs/ui-coverage.md`.
