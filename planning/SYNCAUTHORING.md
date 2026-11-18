# SYNCAUTHORING: the Sync authoring screens, restored as the Designer of the Workspace's sync projects

## Goal

A person who authors a replication set in a Workspace sync project works in the screens the Sync server had for authoring: the replications list, a
replication's page with its tabs and sub-tabs, the table mappings with the source and target side by side and the column mapping editor, the
connections and scripts. They are the **Designer** of a sync project; **Files** (today's stock editor: each file as its generated form or YAML) stays
beside it as the fallback. The choice between the two is one navigation choice per project, the same for every kind of project, and Sync is the first
kind with a Designer (the design is in `DbDataWorkspace/README.md`, "A project's two views: Files and Designer").

This pass is authoring only. The screens are taken from the code as it was before the Sync server was deleted (`96601f27^`), with everything that
watched or operated a running replication taken out; those screens come back in a second pass reading the Runner (the open design "Monitoring and
operations, second pass" below). The old Version Control tab is not restored: history and diffs are the Files view's History, Changes and earlier
texts.

Owner, 2026-11-18: "We need to restore the overall look and feel of the old Sync authoring UI. The new workspace file-format-driven UI needs to remain as
a fallback (every file should be editable as YAML or generated UI)." Owner's direction, 2026-11-18: one project-level choice between Files and Designer
for every project kind; take a copy of the old authoring code and port its services into the Workspace; remove everything about monitoring; skip the old
Version Control UI; monitoring is a second pass, later.

## What exists today

The Sync server's web screens (`Shared/web/sync`, the npm workspace `@dbdatakit/sync`, about 13,500 lines of pages and components) and its API
(`DbDataSync/src/DbDataSync.Api`) were deleted with the server; git keeps them at `96601f27^`. The reference look is in the tree:
`DbDataSync/screenshots/golden-path/` and `replication-wide-provisioning/`. The CSS those screens used is the shell's (`Shared/web/shell/src/styles.css`:
`.detail-body`, `.detail-main`, `.detail-rail`, `.card`, `.card-head`, `.toggle`, `.btn-chrome`, `.subtabbar`, `.grid-row`), unchanged since, so the
look needs no new stylesheet.

Today a sync draft is the generic workspace page (`Shared/web/workspace/src/pages/WorkspacePage.tsx`): the file tree, the Replications and Connections
cards of `SyncSetPanel.tsx` (whose `.grid-row` rows overlap), each file in `StructuredPanel.tsx` (the form generated from `syncSchemas.ts`, or YAML),
`MappingColumns.tsx` for a mapping's columns, and the tabs Changes, Check, Scripts (`SyncScriptsPanel.tsx`), Provisioning (`SyncProvisioningPanel.tsx`),
Catalog, Runs, Builds, History. That page becomes the **Files** view unchanged.

The Workspace server already answers, for a sync draft (`SyncDraftTools.cs`, running `dbdatasync` on a copy of the draft): the check, the scripts list
(`GET .../scripts`), a script's test against generated input (`POST .../script-test`), the provisioning plan from the catalog cache
(`POST .../provisioning-plan`), the catalog refresh through a paired Gateway (`POST .../catalog/refresh`), and the build, publish and run routes.

### What is ported (authoring)

Under `96601f27^:Shared/web/sync/src/`, with the old Sync API call each used and what answers it in the Workspace.

| Old screen or component | Old call | In the Workspace |
| --- | --- | --- |
| Replications list (`pages/ReplicationsPage.tsx`): name, reader, schedule as a sentence, mappings, **New Replication** | `GET /api/replications`, `PUT /api/replications/{n}` | the draft's `config/replications/*/task.yaml`, read and written through the draft model (no route) |
| Replication page chrome (`pages/ReplicationDetailPage.tsx`, `replication-detail/tabs.tsx`): breadcrumbs, the tab bar, **Enabled**, **Save settings**, **Delete** | `PUT .../enabled`, `PUT /api/replications/{n}`, `DELETE` | `enabled` and the replication's folder in the draft |
| Rail: **Unpublished changes** (`components/UnpublishedChanges.tsx`) | `GET /api/runner/package/unpublished` | the draft's commits after the commit of the build last published, and its unsaved edits (the Workspace's builds and history) |
| Overview (`OverviewPanel.tsx`, `EndpointsCard.tsx`, `components/EndpointSidePair.tsx`, `ScheduleCard.tsx`, `TrackingCard.tsx` for the `tracking:` settings) | the replication's config | `task.yaml`; connection and database pickers from the draft's connections and the catalog cache |
| Overview sub-tabs Notes (`components/NotesPanel.tsx`), Pipeline (`MappingPipelineCard.tsx`, and `ReconcileConfigCard.tsx` for the `reconcile:` settings), Provisioning (`ReplicationProvisioningPanel.tsx`), Custom Transforms (`components/ScriptBindings.tsx`), Bulk Load (`SegmentingStrategiesCard.tsx`) | config; `GET .../provisioning` | `task.yaml`; the plan from `POST .../provisioning-plan`; its apply stays the Runner's `provision` task as `SyncProvisioningPanel.tsx` starts it today; a strategy's **Try** reads a database (AUTHORING-3) |
| Table Mappings sidebar and Overview (`TableMappingsPanel.tsx`, `MappingsOverview.tsx`): the source's tables, mapping counts, ticks, **Create mappings** | `GET .../table-mappings`, metadata, `POST .../table-mappings/bulk` | the mapping files; tables from the catalog cache; bulk create as a ported service (below) |
| Mapping editor (`TableMappingForm.tsx`, `MappingSide.tsx`, `components/InheritableToggle.tsx`, `SourceFilterCard.tsx`, `QuerySourcePanel.tsx` without its rows, `SavedMappingHeading.tsx`) | config; metadata | the mapping file; the catalog cache |
| Column Mapping (`ColumnMappingEditor.tsx`, `suggestMappings.ts`, `PrimaryKeyCard.tsx`, `naturalKey.ts`, `RelationshipsCard.tsx`, `CachedMetadataCard.tsx`, `components/ColumnMultiPicker.tsx`) | metadata; `.../provisioning/inferred-column-types`, `inferred-natural-key`, `.../refresh-metadata` | the mapping file and the catalog cache; inferred types and key as a ported service; refresh is the Catalog's refresh through a Gateway (AUTHORING-6); `MappingColumns.tsx` folds into it |
| Mapping sub-tabs Notes, Custom Transforms, Bulk Load (`DefaultSegmentingCard.tsx`), Provisioning (`ProvisioningCard.tsx`, the plan only), Pipeline (the overrides), Diagnostics (`traceTiming`), Verify (`ChecksCard.tsx`, the `verification:` list only) | config; `.../provisioning` | the mapping file; the plan |
| Quality Checks (`ReplicationChecksPanel.tsx`, `checkForms.ts`) | `GET\|PUT .../checks` | `checks:` of `task.yaml` (`check/1`) |
| Connections list and page (`ConnectionsPage.tsx`, `ConnectionEditPage.tsx`, `connection-edit/RunLoginCard.tsx`) | `/api/connections`, `.../capabilities`, `/api/drivers/{type}/capabilities`, `connection-parameters`, `.../credential-source` | the connection files (by secret reference); capabilities, parameters and the credential source as ported services |
| Scripts list and page (`ScriptsPage.tsx`, `ScriptEditPage.tsx`, `script-edit/ScriptTestPanel.tsx`, `components/CodeEditor.tsx`, `ParameterForm.tsx`, `parameterValues.ts`) | `/api/scripts`, `/api/scripts/slots`, `.../compile`, `.../test` | `config/scripts/`; the list and test routes the Workspace has; slots, compile and "used by" as ported services |
| Helpers with tests: `api/readerNotes.ts`, the writer defaults of `api/types.ts` (`writerDefaults.test.ts`), `api/resolveEndpoint.ts`, `api/canonicalJson.ts`, `api/tableExists.ts`, `components/icons.tsx`, `StatusBadge.tsx`, `mappingTabs.ts` | | restored with their tests |

### What is left out of this pass

- **Monitoring and operations** (the open design below): `StatusCard.tsx`, `MetricsCard.tsx`, `RefreshCountdown.tsx`, `MonitoringPanel.tsx`, `lag.ts`,
  `holdState.ts`, `readIntent.ts`, `components/ReadIntentSetting.tsx`, `MappingReadStateDialog.tsx`, `MappingPositionHistory.tsx`, `RunsPanel.tsx`,
  `RunDetailsDialog.tsx`, `RunFigures.tsx`, `runTimes.ts`, `PauseHistoryPanel.tsx`, `PauseDialog.tsx`, `BulkLoadHistoryPanel.tsx`,
  `BulkLoadProgressCard.tsx`, `BulkLoadForm.tsx`, `ReconcileDeletesForm.tsx`, `JoinedRunNotice.tsx`,
  `RunnerHistoryNotice.tsx`, `RunnerStatusRow.tsx`, `RunningCounts.tsx`, `ReplicationRunnerPanel.tsx`, `pages/RunnerPage.tsx`, `RunnerLinkCard.tsx`,
  `runnerPublish.ts`, `VerificationPanel.tsx`'s results and `VerificationResultPage.tsx`, `api/useRunHub.ts` (SignalR), the tracking card's live state
  and catch-up, and the connection list's reachable column and **Test all**.
- **Row previews**: `PreviewGrid.tsx`, a query source's rows (`query-preview`).
- **Version Control** (`HistoryPanel.tsx`, `components/DiffViewer.tsx` as used there): the Files view's History, Changes and a file's earlier texts.
- **Preview SQL** (`MappingPreview.tsx`): the statements of a pass need the drivers with a connection and the read position; an open design below.
- The product frame and admin pages (`App.tsx`, `SyncModule.tsx`, `adminHost.tsx`, `components/AppShell.tsx`, Drivers, Libraries, Files, Docs, Admin):
  the Workspace's frame replaces them.

### The old services the ported screens need

From `96601f27^:DbDataSync/src/DbDataSync.Api/` (and the libraries it composed). Reading and writing a replication, a mapping, a connection, a script or
`checks:` is a file edit of the draft through the Workspace's files API; no route is ported for it. What needs code on the server:

| Old service or controller | What it answered | Composed from |
| --- | --- | --- |
| `ScriptsController` slots and compile, `Services/ScriptUsageScanner.cs` | the script slots a binding can name; a script's compile diagnostics; where each script is bound | `DbDataSync.Scripting` (`ScriptHost`), `DbDataSync.Core` (`ConfigRepository`) |
| `ConnectionsController` capabilities, `connection-parameters`, `credential-source`; `Services/ParameterCheck.cs`, `Services/SyncEngines.cs` | what a driver can do, its connection parameters, which login a connection uses, the parameter check | `DbDataSync.Drivers.Abstractions` (`DriverRegistry`), `DbDataSync.Drivers.Descriptor`, `DbDataKit.Connections.Descriptor` (the engine registry) |
| `MetadataController` (databases, tables, columns) | a connection's metadata | the draft's catalog cache (`DbDataKit.Catalog.Cache`), never a live connection in this pass |
| `ProvisioningController` inferred column types and natural key; `Services/MappingMetadataService.cs`, `MappingMetadataCapture.cs`; `TableMappingsController` bulk create | the target types and key of a mapping from its source columns; a mapping file per ticked table with its columns and key | `DbDataSync.Provisioning` (`MappingColumnReader`) over the catalog cache |
| `ProvisioningController` and `ReplicationProvisioningController` plan | a mapping's and a replication's provisioning plan | the Workspace's `provisioning-plan` route (exists) |

Left on the old server: everything in `Runner/`, `Hubs/`, `RunsController`, `ReplicationLagController`, `MetricsController`, `VerificationController`,
`ReplicationTrackingController`, the read-state and position routes of `TableMappingsController`, `PreviewController`, `QualityController`,
`LineageController`, the admin, driver, library and files controllers. How the ported services run in the Workspace is SYNCAUTHORING-P8.

## Tasks

Every Designer task keeps the Files view working on the same draft, and its browser story switches to Files and back without losing an unsaved edit.
The stories are `sync-designer.spec.ts` in `DbDataKit/tests/DbDataKit.Server.Web.Tests` against `workspace-fake.ts`.

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| SYNCAUTHORING-1 | The Files/Designer switch, generic in the Workspace's project frame (`Shared/web/workspace`), as `DbDataWorkspace/README.md` describes it: a kind registers a Designer (a component and its routes) with `@dbdatakit/workspace`; the draft's page shows **Files** and **Designer** in its navigation when the kind has one and opens on Designer; Files is today's page unchanged; the view is in the address (`/:project/:id/files/*`, `/:project/:id/designer/*`) and the last choice per person and project is remembered in the browser (read and written in try/catch); the draft's unsaved edits live in the frame, so both views see them; the chrome (**not saved** with **Save**, the check's count opening Check, **Commit…**) is the frame's; a file a Designer cannot show opens in Files from a link naming it. Sync registers a Designer that is a placeholder page until SYNCAUTHORING-4 | none | M | vitest of the frame with a test kind (registered Designer shown, unregistered kind shows no switch, an unsaved edit survives the switch); Playwright: an app draft shows no switch; a sync draft opens on Designer, switches to Files with an unsaved edit kept, and a reload on `.../files/...` stays in Files |
| SYNCAUTHORING-2 | The old authoring components restored into a package (SYNCAUTHORING-P6) from `96601f27^:Shared/web/sync/src/`: the files listed under "What is ported", with every part listed under "What is left out" removed (no run, lag, position, pause, bulk-load, verification-result or Runner code, no SignalR); the old API hooks replaced by a draft model over the sync draft's files (`useSyncDraft`: `task.yaml`, the mappings, the connections and the scripts as typed views; a change written as a path edit of the file's text with the shell's `yamlText`, so comments, key order and unknown keys are kept; a file that does not parse or of a newer `schemaVersion` is reported for the Files view) and by a client of the ported routes (SYNCAUTHORING-3); generic components (`InheritableToggle`, `StatusBadge`, `NotesPanel`, the missing `icons`) move to `@dbdatakit/shell` | none | L | vitest: the restored tests pass (`suggestMappings`, `checkForms`, `readerNotes`, `writerDefaults`, `PrimaryKeyCard`, `ReplicationChecksPanel`, `TrackingCard` on its settings); a `task.yaml` with comments and an unknown key keeps both after its schedule and an endpoint change through the model; a mapping of a newer `schemaVersion` reports itself for Files; a test fails if a module of the package imports `@microsoft/signalr` or calls a Runner route |
| SYNCAUTHORING-3 | The authoring services ported into the Workspace server, for a sync draft, as SYNCAUTHORING-P8 says, under `/api/v1/projects/{project}/workspaces/{id}/sync/`: script slots, a script's compile, where each script is bound; a driver's capabilities and connection parameters, a connection's credential source and parameter check; the databases, tables and columns of a connection from the catalog cache; a mapping's inferred column types and natural key; bulk create of mappings (the files' texts for the ticked tables, written into the draft as unsaved edits by the client); each route in the console's relay table (`ServerProducts.WorkspaceRoutes`) with its role, and in `workspace-fake.ts`. Reusable code the Workspace needs from a product folder moves under `Shared/src` first, as CLAUDE.md says | SYNCAUTHORING-P8 | L | xunit in `DbDataWorkspace.Tests`: each route on a sync draft fixture (two connections, a replication, a catalog cache): the slots list, a compile error at its line, a script bound on a mapping named as used, the columns of a cached table, the inferred key of a table with a primary key, bulk create for two tables; a viewer is refused the writing routes; the console's relay test lists the new routes with their roles |
| SYNCAUTHORING-4 | Designer, replications: the Designer's frame (explorer of Replications, Connections, Scripts; breadcrumbs as the heading); the Replications list as the old table (name, reader, schedule as a sentence, mappings, the check's errors per replication) with **New Replication** as a dialog naming the two connections (replaces the card in `SyncSetPanel.tsx` for the Designer); a replication's page with the tabs Overview, Table Mappings, Quality Checks, the chrome **Enabled** (edits `enabled` of the draft's `task.yaml`; its title says it takes effect when published), **Save settings**, **Delete** (the replication's folder, asked twice), the rail's **Unpublished changes** with **Build and publish…**; the Overview (endpoint cards side by side, "inherited by N mappings unless overridden", the schedule strip, the tracking settings) and its sub-tabs Notes, Pipeline, Provisioning (the plan in the old layout; apply as today), Custom Transforms, Bulk Load (strategies; **Try** says it waits on AUTHORING-3); Quality Checks as the old `ReplicationChecksPanel` | SYNCAUTHORING-1, SYNCAUTHORING-2, SYNCAUTHORING-3 | L | Playwright: three replications listed with reader and schedule, one with an error count; New Replication makes the task and a mapping and opens its Overview; change the schedule to Periodic with a cron and see in Files that only those keys changed; Enabled off marks the draft not saved and the rail unpublished; the Provisioning sub-tab shows a missing target table and its statement; add a freshness check and see it in Files |
| SYNCAUTHORING-5 | Designer, table mappings: the sidebar of mappings with column counts and **+**; the Overview of the source's tables from the catalog cache with mapping counts, ticks and **Create mappings**; the mapping editor (name, Cancel, Delete, Save mapping), the two sides with **Override for this table**, the table picker from the cache, **Custom query, not a table** (the query text, no rows), the source filter; Column Mapping as the old `ColumnMappingEditor` (**Auto-map by name**, source type, target type, transform, the primary key ticks, relationships, the cached metadata); the sub-tabs Notes, Custom Transforms, Bulk Load (`DefaultSegmentingCard`), Provisioning (the mapping's plan, its pending steps as the tab's badge), Pipeline (the overrides), Diagnostics (`traceTiming`), Verify (the `verification:` checks as `ChecksCard`) | SYNCAUTHORING-4 | L | Playwright: tick two tables and Create mappings makes two mapping files; on one, Auto-map by name maps three columns, untick a key column, override the target side's schema, add a row-count verification and a default segmenting by month; the Provisioning badge counts the missing table's step; Files shows the same changes |
| SYNCAUTHORING-6 | Designer, connections and scripts: the Connections list as the old table (driver from `engine`, host, database, auth) with **New connection** (the engines as SYNCAUTHORING-P7 says); a connection's page as the old cards (engine, address, database, timeouts, the driver's parameters, the login by secret reference and the run login as `RunLoginCard`, custom transforms and hooks; the secret findings under it); the Scripts list as the old table (kind, language, used by, enabled) with **New script**; a script's page as `ScriptEditPage` (the manifest's fields, the code in the shell's `CodeEditor`, compile diagnostics, `ScriptTestPanel` over the `script-test` route, where it is bound) | SYNCAUTHORING-3, SYNCAUTHORING-4 | M | Playwright: a new connection by reference; typing a password into it in Files shows the secret finding in both views; a new value expression script with a compile error at its line, fixed, tested against generated input, bound on a mapping's Custom Transforms, and "used by" names the mapping |
| SYNCAUTHORING-7 | Documentation and coverage: `docs/ui-coverage.md` names every Designer page and its stories; the `docs/ui-editors.md` rows of `sync-task-config`, `sync-mapping-config`, `sync-script-config`, the connections, the checks and the verification checks name the Designer screens and the Files view; `docs/authoring-parity.md`'s Sync rows; the Sync part of `DbDataWorkspace/README.md` says what the Designer covers and that monitoring is the Runner's screens; the shots under `DbDataSync/screenshots/golden-path/` that show authoring are retaken from the stories | SYNCAUTHORING-4, SYNCAUTHORING-5, SYNCAUTHORING-6 | S | `scripts/check_ui_coverage.py` and `scripts/check_ui_editors.py` pass with the new rows; the stories run in CI |

## Needs from other subjects

- AUTHORING: the other project kinds (build, store, integration, app) adopt the Files/Designer switch of SYNCAUTHORING-1 as their Designers are built:
  each kind's designed screens register as its Designer rather than as tabs of the Files view, and a task that builds one says so.
- AUTHORING-14: the engine lists of the Pipeline sub-tabs and **New connection** come from the engine registry (SYNCAUTHORING-4, SYNCAUTHORING-6);
  until it lands the Designer uses the list `SyncSetPanel.tsx` has today. Ownership is SYNCAUTHORING-P7.
- AUTHORING-3: a segmenting strategy's **Try** (SYNCAUTHORING-4); until then it says it waits on a Runner task.
- AUTHORING-4: the source side of the Provisioning sub-tabs (SYNCAUTHORING-4, SYNCAUTHORING-5); until then the source is "not known".
- AUTHORING-6: the catalog cache of a Sync connection, which the table pickers, Create mappings, Column Mapping and the metadata routes read
  (SYNCAUTHORING-3, SYNCAUTHORING-5); a connection's test before publish waits on its read logins.
- AUTHORING-9: its stories of the Sync forms stay the stories of the Files view; this subject's stories cover the Designer (no story is written twice).
- AUTHORING-10: the secrets overview of the project, which the Connections page links to (SYNCAUTHORING-6).
- AUTHORING-12: reconcile deletes and resync stay paused; they come back with the monitoring pass, not in the Designer's authoring screens.

## Proposed decisions

### SYNCAUTHORING-P6: where the restored screens live (awaiting the owner)

Scenario: a worker restores `ColumnMappingEditor.tsx` and `ReplicationChecksPanel.tsx` from git. Option A: the Sync Designer and its cards are a shared
package `@dbdatakit/sync-ui` (`Shared/web/sync-ui`, an npm workspace like `store-ui`), registered as the sync kind's Designer by `@dbdatakit/workspace`;
components any product uses go to `@dbdatakit/shell`. Option B: everything goes into `Shared/web/workspace/src/sync/`. Under A the package is built and
tested on its own and the monitoring pass can add its screens beside them for the console's Runner pages; under B the Workspace package grows by several
thousand lines of one kind's screens. Recommendation: A.

### SYNCAUTHORING-P7: who owns the engine list of the Sync Designer (awaiting the owner)

Scenario: AUTHORING-14 replaces the list written in `SyncSetPanel.tsx`, and SYNCAUTHORING-4 and SYNCAUTHORING-6 need an engine list for the Pipeline
and **New connection**. Option A: AUTHORING-14 stays in AUTHORING and lands first as a hook (`useSyncEngines`) the Files view and the Designer both
use. Option B: AUTHORING-14 moves into this subject. Option C: both build it. Recommendation: A, so AUTHORING-14 is not blocked by this subject's larger
tasks.

### SYNCAUTHORING-P8: how the ported services run in the Workspace server (awaiting the owner)

Scenario: the Scripts page asks for the slots a script can bind to and a script's compile diagnostics; the Column Mapping tab asks for the inferred key of
`dbo.customers`. The code that answered both lives in `DbDataSync.Scripting`, `DbDataSync.Core` and `DbDataSync.Provisioning`, product projects the
Workspace (under `Shared/`) may not reference. Option A: the libraries the services need (the configuration model, the script host's slots and
compiler, the driver descriptors, the mapping column reader) move to `Shared/src/DbDataKit.Sync.*` with adapters left in `DbDataSync`, and the
Workspace composes them in its own process; compiling and testing a person's script still runs in a child process, as `script-test` does today, since
it runs code the person wrote. Option B: every service is a `dbdatasync` verb with `--json` run on a copy of the draft in a child process
(`script slots`, `script compile`, `script usage`, `driver describe`, `mapping infer`, `mapping create --from-cache`), as `SyncDraftTools.cs` runs the
scripts list and the provisioning plan today; nothing moves. Under A each answer is a call in the process (fast enough for the column picker as you
type) and the libraries become the suite's; under B the Workspace stays free of Sync's assemblies and the verbs are usable from a terminal, at the cost
of a process per call. Recommendation: A for the configuration model, descriptors and the column reader, B for anything that compiles or runs a
person's script.

## Open designs

### Monitoring and operations, second pass

What the old screens did to watch and operate a running replication, removed from this pass, and what they would read now that the Runner runs the set
and the console relays to it. The second pass designs where they live (the Designer reading the Runner through the console's relay as the signed-in
person, or the console's Runner screens with a link from the Designer) before it becomes tasks.

- **Run controls in the chrome**: Run Now, Pause and Resume (with a note asked in a dialog), Bulk Load…, Reconcile deletes…. Now: the Runner's task
  actions (`POST .../tasks/{...}/run|pause|resume`), the Workspace's run routes with its publisher key (`runServed`, `runStaged`), reconcile and resync
  paused by the owner. Open: whether a run started from a draft runs as the person (the relay) or as the Workspace's key, and how **Enabled** in the
  draft and a pause on the Runner are told apart on one screen (the draft's setting takes effect when published, a pause at once).
- **The rail's Replication status and Last 24h**: running or idle in words, next due, the last run; passes, failures, rows read and written,
  p50/p95/max, a bar per hour, 1h/24h/7d, a refresh countdown. Now: the Runner's task and its runs (`GET /api/v1/tasks/{package}/{channel}/{task}`,
  `GET /api/v1/runs` and their pass records).
- **Current Status**: per mapping the read position, its time and age, intent and hold, lag (exact or estimated), **Manage…** to rewind. Now: the drain's
  checkpoint `watermark` on the Runner (one item per mapping and source table), `GET .../checkpoints`, its history and rewind. Open: what lag means with
  no Sync server measuring it: the age of the position (no read of the source, but a quiet table looks behind), a read-only Runner task that measures it
  on demand, or the drain writing each mapping's lag into its pass record (exact for change tracking and logical slots, estimated otherwise, one query per
  mapping per pass, as the old server paid).
- **Run History, Pause History, Bulk Load History**: filters, paging, a run's dialog with its timing and log. Now: the Runner's runs, steps, logs and
  units, its decisions.
- **Verification results** of a mapping and **quality check results** of a replication: now the Runner's run steps and the console's Quality page.
- **The Runner tab and page**: the version served and staged per channel, slots and bindings, instances. Now: the Builds tab and `PublishForm.tsx`, and
  the Runner's own screens.
- **Tracking state**: the target's units, catch-up and divergences (the target's tracking state is TASKENGINE's).
- **Live updates**: the old screens followed runs over SignalR; the Runner's screens poll.
- **Row previews**: a query source's rows and a mapping's preview, wherever authoring-time reads of a database are settled (AUTHORING's proposals on
  where rows may be kept and where read logins live).

### Preview SQL of a mapping

The old **Preview SQL** tab listed every statement a pass of a mapping would run, in order (the reader's, the staging's and the writer's built-in
statements, the operator's SQL, the scripts' output, the hooks), built by the drivers from the mapping, the tables' metadata and the position the next
pass reads from. The likely authoring shape is a command run by the Workspace on a copy of the draft, as the provisioning plan is, with the catalog cache
as the metadata and "from the first position" as the position. Open: whether every driver builds its statements without a connection (the change
tracking reader asks the server for its version functions), and whether a statement that needs the live server is shown as "built on the Runner" or left
out.

### Drivers and libraries

The old rail had **Drivers** (driver YAML files) and **Libraries** (NuGet packages). A sync project's package carries the drivers its connections need
(`package publish` adds them) and the platform comes from the Runner. Open: whether a project may carry its own driver YAML (`config/drivers/`) and so
needs a Designer page, or drivers stay a command on the repository with a `none (reason)` row in `docs/ui-editors.md`.

## Shared areas touched

- `Shared/web/workspace` (the Files/Designer frame, routes), `Shared/web/sync-ui` (new npm workspace, if SYNCAUTHORING-P6 is answered A),
  `Shared/web/package.json` (its workspaces), `Shared/web/shell` (generic components moved in).
- `Shared/src/DbDataKit.Authoring.Server` (the sync routes), `Shared/src/DbDataKit.Sync.*` and `DbDataSync/src` (if SYNCAUTHORING-P8 is answered A),
  `DbDataKit/src/DbDataKit.Server/ServerProducts.cs` (the relay table).
- `DbDataKit/tests/DbDataKit.Server.Web.Tests` (`sync-designer.spec.ts`, `workspace-fake.ts`), `DbDataWorkspace/tests/DbDataWorkspace.Tests`,
  `DbDataSync/screenshots/`.
- `DbDataWorkspace/README.md`, `docs/ui-coverage.md`, `docs/ui-editors.md`, `docs/authoring-parity.md`.
