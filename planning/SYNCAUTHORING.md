# SYNCAUTHORING: the Sync authoring screens, restored as the Designer of the Workspace's sync projects

## Goal

A person who authors a replication set in a Workspace sync project works, inside a replication, in the screens the Sync server had for authoring
it: a replication's page with its tabs and sub-tabs, the table mappings with the source and target side by side and the column mapping editor, its
quality checks. They are the **Designer** of a sync project; **Files** (today's stock editor: each file as its generated form or YAML) stays beside it
as the fallback. Files and Designer are two areas of a project, each with its own address and its own navigation entries, the same for every kind of
project; the person moves between them by clicking. Sync is the first kind with a Designer (the design is in `DbDataWorkspace/README.md`, "A project's
two areas: Files and Designer").

This pass is authoring only, and only what lives inside a replication. The screens are taken from the code as it was before the Sync server was deleted (`96601f27^`), with everything that
watched or operated a running replication taken out; those screens come back in a second pass reading the Runner (the open design "Monitoring and
operations, second pass" below). The old Version Control tab is not restored: history and diffs are the Files area's History, Changes and earlier
texts. The old screens outside a replication (the Replications list, the Connections and Scripts pages) are not restored either: a replication is
reached from the Designer's navigation, and connections and scripts are edited in Files until the open design "Outside a replication, later" becomes
tasks.

Owner, 2026-11-18: "We need to restore the overall look and feel of the old Sync authoring UI. The new workspace file-format-driven UI needs to remain as
a fallback (every file should be editable as YAML or generated UI)." Owner's direction, 2026-11-18: one project-level choice between Files and Designer
for every project kind; take a copy of the old authoring code and port its services into the Workspace; remove everything about monitoring; skip the old
Version Control UI; monitoring is a second pass, later. Owner, 2026-11-18: "No need for a user preference: both areas should be separate URLs and
separate navigation. The user chooses by clicking." and "We also don't need to cover any of the old UI outside of a replication for now."

## What exists today

The Sync server's web screens (`Shared/web/sync`, the npm workspace `@dbdatakit/sync`, about 13,500 lines of pages and components) and its API
(`DbDataSync/src/DbDataSync.Api`) were deleted with the server; git keeps them at `96601f27^`. The reference look is in the tree:
`DbDataSync/screenshots/golden-path/` and `replication-wide-provisioning/`. The CSS those screens used is the shell's (`Shared/web/shell/src/styles.css`:
`.detail-body`, `.detail-main`, `.detail-rail`, `.card`, `.card-head`, `.toggle`, `.btn-chrome`, `.subtabbar`, `.grid-row`), unchanged since, so the
look needs no new stylesheet.

Today a sync draft is the generic workspace page (`Shared/web/workspace/src/pages/WorkspacePage.tsx`): the file tree, the Replications and Connections
cards of `SyncSetPanel.tsx` (whose `.grid-row` rows overlap), each file in `StructuredPanel.tsx` (the form generated from `syncSchemas.ts`, or YAML),
`MappingColumns.tsx` for a mapping's columns, and the tabs Changes, Check, Scripts (`SyncScriptsPanel.tsx`), Provisioning (`SyncProvisioningPanel.tsx`),
Catalog, Runs, Builds, History. That page becomes the **Files** area unchanged.

The Workspace server already answers, for a sync draft (`SyncDraftTools.cs`, running `dbdatasync` on a copy of the draft): the check, the scripts list
(`GET .../scripts`), a script's test against generated input (`POST .../script-test`), the provisioning plan from the catalog cache
(`POST .../provisioning-plan`), the catalog refresh through a paired Gateway (`POST .../catalog/refresh`), and the build, publish and run routes.

### What is ported (authoring)

Under `96601f27^:Shared/web/sync/src/`, with the old Sync API call each used and what answers it in the Workspace.

| Old screen or component | Old call | In the Workspace |
| --- | --- | --- |
| The way into a replication: the Designer's navigation lists the draft's replications (no list page) and ends with **New replication**, the old page's form (name, schedule: `pages/ReplicationsPage.tsx`, its form only) as a dialog | `GET /api/replications`, `PUT /api/replications/{n}` | the draft's `config/replications/*/task.yaml`, read and written through the draft model (no route) |
| Replication page chrome (`pages/ReplicationDetailPage.tsx`, `replication-detail/tabs.tsx`): breadcrumbs, the tab bar, **Enabled**, **Save settings**, **Delete** | `PUT .../enabled`, `PUT /api/replications/{n}`, `DELETE` | `enabled` and the replication's folder in the draft |
| Rail: **Unpublished changes** (`components/UnpublishedChanges.tsx`) | `GET /api/runner/package/unpublished` | the draft's commits after the commit of the build last published, and its unsaved edits (the Workspace's builds and history) |
| Overview (`OverviewPanel.tsx`, `EndpointsCard.tsx`, `components/EndpointSidePair.tsx`, `ScheduleCard.tsx`, `TrackingCard.tsx` for the `tracking:` settings) | the replication's config | `task.yaml`; connection and database pickers from the draft's connections and the catalog cache |
| Overview sub-tabs Notes (`components/NotesPanel.tsx`), Pipeline (`MappingPipelineCard.tsx`, and `ReconcileConfigCard.tsx` for the `reconcile:` settings), Provisioning (`ReplicationProvisioningPanel.tsx`), Custom Transforms (`components/ScriptBindings.tsx`), Bulk Load (`SegmentingStrategiesCard.tsx`) | config; `GET .../provisioning`; a connection's capabilities; `/api/scripts`, `/api/scripts/slots` | `task.yaml`; the readers, staging providers and writers as a ported service; the draft's scripts from the `scripts` route the Workspace has and their slots as a ported service; the plan from `POST .../provisioning-plan`; its apply stays the Runner's `provision` task as `SyncProvisioningPanel.tsx` starts it today; a strategy's **Try** reads a database (AUTHORING-3) |
| Table Mappings sidebar and Overview (`TableMappingsPanel.tsx`, `MappingsOverview.tsx`): the source's tables, mapping counts, ticks, **Create mappings** | `GET .../table-mappings`, metadata, `POST .../table-mappings/bulk` | the mapping files; tables from the catalog cache; bulk create as a ported service (below) |
| Mapping editor (`TableMappingForm.tsx`, `MappingSide.tsx`, `components/InheritableToggle.tsx`, `SourceFilterCard.tsx`, `QuerySourcePanel.tsx` without its rows, `SavedMappingHeading.tsx`) | config; metadata | the mapping file; the catalog cache |
| Column Mapping (`ColumnMappingEditor.tsx`, `suggestMappings.ts`, `PrimaryKeyCard.tsx`, `naturalKey.ts`, `RelationshipsCard.tsx`, `CachedMetadataCard.tsx`, `components/ColumnMultiPicker.tsx`) | metadata; `.../provisioning/inferred-column-types`, `inferred-natural-key`, `.../refresh-metadata` | the mapping file and the catalog cache; inferred types and key as a ported service; refresh is the Catalog's refresh through a Gateway (AUTHORING-6); `MappingColumns.tsx` folds into it |
| Mapping sub-tabs Notes, Custom Transforms, Bulk Load (`DefaultSegmentingCard.tsx`), Provisioning (`ProvisioningCard.tsx`, the plan only), Pipeline (the overrides), Diagnostics (`traceTiming`), Verify (`ChecksCard.tsx`, the `verification:` list only) | config; `.../provisioning` | the mapping file; the plan |
| Quality Checks (`ReplicationChecksPanel.tsx`, `checkForms.ts`) | `GET\|PUT .../checks` | `checks:` of `task.yaml` (`check/1`) |
| Helpers with tests: `api/readerNotes.ts`, the writer defaults of `api/types.ts` (`writerDefaults.test.ts`), `api/resolveEndpoint.ts`, `api/canonicalJson.ts`, `api/tableExists.ts`, `components/icons.tsx`, `StatusBadge.tsx`, `mappingTabs.ts`; the shared pieces the replication's cards use: `components/CodeEditor.tsx` (a filter, a query), `ParameterForm.tsx` and `parameterValues.ts` (a script's parameters in `ScriptBindings.tsx`) | | restored with their tests |

### What is left out of this pass

- **Monitoring and operations** (the open design below): `StatusCard.tsx`, `MetricsCard.tsx`, `RefreshCountdown.tsx`, `MonitoringPanel.tsx`, `lag.ts`,
  `holdState.ts`, `readIntent.ts`, `components/ReadIntentSetting.tsx`, `MappingReadStateDialog.tsx`, `MappingPositionHistory.tsx`, `RunsPanel.tsx`,
  `RunDetailsDialog.tsx`, `RunFigures.tsx`, `runTimes.ts`, `PauseHistoryPanel.tsx`, `PauseDialog.tsx`, `BulkLoadHistoryPanel.tsx`,
  `BulkLoadProgressCard.tsx`, `BulkLoadForm.tsx`, `ReconcileDeletesForm.tsx`, `JoinedRunNotice.tsx`,
  `RunnerHistoryNotice.tsx`, `RunnerStatusRow.tsx`, `RunningCounts.tsx`, `ReplicationRunnerPanel.tsx`, `pages/RunnerPage.tsx`, `RunnerLinkCard.tsx`,
  `runnerPublish.ts`, `VerificationPanel.tsx`'s results and `VerificationResultPage.tsx`, `api/useRunHub.ts` (SignalR), the tracking card's live state
  and catch-up, and the connection list's reachable column and **Test all**.
- **Outside a replication** (the open design "Outside a replication, later"): the Replications list's table (`pages/ReplicationsPage.tsx` apart from
  its form), the Connections list and page (`ConnectionsPage.tsx`, `ConnectionEditPage.tsx`, `connection-edit/RunLoginCard.tsx`,
  `connection-edit/ConnectionTestCard.tsx`), the Scripts list and page (`ScriptsPage.tsx`, `ScriptEditPage.tsx`, `script-edit/ScriptTestPanel.tsx`).
  Connections and scripts are edited in Files (the generated forms, the Scripts tab) meanwhile.
- **Row previews**: `PreviewGrid.tsx`, a query source's rows (`query-preview`).
- **Version Control** (`HistoryPanel.tsx`, `components/DiffViewer.tsx` as used there): the Files area's History, Changes and a file's earlier texts.
- **Preview SQL** (`MappingPreview.tsx`): the statements of a pass need the drivers with a connection and the read position; an open design below.
- The product frame and admin pages (`App.tsx`, `SyncModule.tsx`, `adminHost.tsx`, `components/AppShell.tsx`, Drivers, Libraries, Files, Docs, Admin):
  the Workspace's frame replaces them.

### The old services the ported screens need

From `96601f27^:DbDataSync/src/DbDataSync.Api/` (and the libraries it composed), only those the screens inside a replication call. Reading and writing a
replication, a mapping or `checks:`, and reading the draft's connections and scripts, is a file edit or read of the draft through the Workspace's files API; no route is ported for it. What needs code on the server:

| Old service or controller | What it answered | Composed from |
| --- | --- | --- |
| `ScriptsController` slots | the script slots a binding can name (Custom Transforms, `ScriptBindings.tsx`) | `DbDataSync.Scripting` (`ScriptHost`) |
| `ConnectionsController` capabilities; `Services/SyncEngines.cs` | what a connection's driver can do: its readers, staging providers and writers (the Overview's and a mapping's Pipeline, `useReplicationCapabilities`) | `DbDataSync.Drivers.Abstractions` (`DriverRegistry`), `DbDataSync.Drivers.Descriptor`, `DbDataKit.Connections.Descriptor` (the engine registry) |
| `MetadataController` (databases, tables, columns) | a connection's metadata | the draft's catalog cache (`DbDataKit.Catalog.Cache`); how the cache is filled and refreshed, and what is checked live, is AUTHORING-P2 and SYNCAUTHORING-P9 |
| `ProvisioningController` inferred column types and natural key; `Services/MappingMetadataService.cs`, `MappingMetadataCapture.cs`; `TableMappingsController` bulk create | the target types and key of a mapping from its source columns; a mapping file per ticked table with its columns and key | `DbDataSync.Provisioning` (`MappingColumnReader`) over the catalog cache |
| `ProvisioningController` and `ReplicationProvisioningController` plan | a mapping's and a replication's provisioning plan | the Workspace's `provisioning-plan` route (exists) |

Left on the old server: everything in `Runner/`, `Hubs/`, `RunsController`, `ReplicationLagController`, `MetricsController`, `VerificationController`,
`ReplicationTrackingController`, the read-state and position routes of `TableMappingsController`, `PreviewController`, `QualityController`,
`LineageController`, the admin, driver, library and files controllers. Not ported in this pass because only the screens outside a replication used them
(the open design "Outside a replication, later"): a script's compile (`ScriptsController` compile) and where each script is bound
(`Services/ScriptUsageScanner.cs`), a driver's capabilities by type (`/api/drivers/{type}/capabilities`), a driver's connection parameters
(`connection-parameters`, `Services/ParameterCheck.cs`) and a connection's credential source (`credential-source`). How the ported services run in the
Workspace is SYNCAUTHORING-P8.

## Tasks

Every Designer task keeps the Files area working on the same draft, and its browser story clicks into Files and back without losing an unsaved edit.
The stories are `sync-designer.spec.ts` in `DbDataKit/tests/DbDataKit.Server.Web.Tests` against `workspace-fake.ts`.

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| SYNCAUTHORING-1 | The two areas Files and Designer, generic in the Workspace's project frame (`Shared/web/workspace`), as `DbDataWorkspace/README.md` describes them: a kind registers a Designer (a component, its routes and its navigation entries) with `@dbdatakit/workspace`; each area has its own addresses (`/:project/:id/files/*`, `/:project/:id/designer/*`) and its own entries in the project's navigation (Files: the file tree and the draft's tabs, today's page unchanged; Designer: the entries the kind registers); the person moves between them by clicking an entry or a link, and nothing is remembered per person (no stored choice, no switch control); a draft of a kind with a Designer opens on it, a kind without one shows only Files; the draft's unsaved edits live in the frame, so both areas see them; the chrome (**not saved** with **Save**, the check's count opening Check, **Commit…**) is the frame's; a file a Designer cannot show opens in Files from a link naming it. Sync registers a Designer whose navigation lists the draft's replications and whose page is a placeholder until SYNCAUTHORING-4 | none | M | vitest of the frame with a test kind (its registered entries shown under Designer, a kind without one shows no Designer entries, an unsaved edit survives a click from one area to the other); Playwright: an app draft shows only Files; a sync draft opens on Designer, a click on a file under Files keeps an unsaved edit made in the Designer, a reload on `.../files/...` stays in Files and one on `.../designer/...` in the Designer |
| SYNCAUTHORING-2 | The old authoring components of a replication restored into a package (SYNCAUTHORING-P6) from `96601f27^:Shared/web/sync/src/`: the files listed under "What is ported" (the replication's page, its tabs and sub-tabs, the table mappings, the mapping editor and Column Mapping, Quality Checks, the Unpublished changes card, the New replication form, the helpers and their tests), with every part listed under "What is left out" removed (no run, lag, position, pause, bulk-load, verification-result or Runner code, no SignalR, no Replications list table, no Connections or Scripts page); the old API hooks replaced by a draft model over the sync draft's files (`useSyncDraft`: `task.yaml`, the mappings, the connections and the scripts as typed views, the connections and scripts read only; a change written as a path edit of the file's text with the shell's `yamlText`, so comments, key order and unknown keys are kept; a file that does not parse or of a newer `schemaVersion` is reported for Files) and by a client of the ported routes (SYNCAUTHORING-3); generic components (`InheritableToggle`, `StatusBadge`, `NotesPanel`, `CodeEditor`, the missing `icons`) move to `@dbdatakit/shell` | none | L | vitest: the restored tests pass (`suggestMappings`, `checkForms`, `readerNotes`, `writerDefaults`, `PrimaryKeyCard`, `ReplicationChecksPanel`, `TrackingCard` on its settings); a `task.yaml` with comments and an unknown key keeps both after its schedule and an endpoint change through the model; a mapping of a newer `schemaVersion` reports itself for Files; a test fails if a module of the package imports `@microsoft/signalr` or calls a Runner route |
| SYNCAUTHORING-3 | The authoring services the replication's screens call, ported into the Workspace server for a sync draft, as SYNCAUTHORING-P8 says, under `/api/v1/projects/{project}/workspaces/{id}/sync/`: the script slots a binding can name; a connection's capabilities (readers, staging providers, writers); the databases, tables and columns of a connection from the catalog cache; a mapping's inferred column types and natural key; bulk create of mappings (the files' texts for the ticked tables, written into the draft as unsaved edits by the client); each route in the console's relay table (`ServerProducts.WorkspaceRoutes`) with its role, and in `workspace-fake.ts`. Not ported (only the screens outside a replication used them): a script's compile and where it is bound, a driver's connection parameters and capabilities by type, a connection's credential source. Reusable code the Workspace needs from a product folder moves under `Shared/src` first, as CLAUDE.md says | SYNCAUTHORING-P8 | M | xunit in `DbDataWorkspace.Tests`: each route on a sync draft fixture (two connections, a replication, a catalog cache): the slots list, the readers and writers of a SQL Server connection, the columns of a cached table, the inferred key of a table with a primary key, bulk create for two tables; a viewer is refused bulk create; the console's relay test lists the new routes with their roles |
| SYNCAUTHORING-4 | Designer, a replication: the Designer's navigation (the draft's replications, each with the check's error count, and **New replication** as the old form in a dialog: name and schedule, the endpoints then set on the Overview; with no connection in the draft it says so and links to `config/connections/` in Files); breadcrumbs as the heading; a replication's page with the tabs Overview, Table Mappings, Quality Checks, the chrome **Enabled** (edits `enabled` of the draft's `task.yaml`; its title says it takes effect when published), **Save settings**, **Delete** (the replication's folder, asked twice), the rail's **Unpublished changes** with **Build and publish…**; the Overview (endpoint cards side by side with the connection and database pickers from the draft's connections and the catalog cache, "inherited by N mappings unless overridden", the schedule strip, the tracking settings) and its sub-tabs Notes, Pipeline, Provisioning (the plan in the old layout; apply as today), Custom Transforms, Bulk Load (strategies; **Try** says it waits on AUTHORING-3); Quality Checks as the old `ReplicationChecksPanel` | SYNCAUTHORING-1, SYNCAUTHORING-2, SYNCAUTHORING-3 | L | Playwright: three replications in the navigation, one with an error count; New replication makes the task and opens its Overview; pick the two connections there; change the schedule to Periodic with a cron and see in Files that only those keys changed; Enabled off marks the draft not saved and the rail unpublished; bind a script on Custom Transforms from the slots offered; the Provisioning sub-tab shows a missing target table and its statement; add a freshness check and see it in Files |
| SYNCAUTHORING-5 | Designer, table mappings: the sidebar of mappings with column counts and **+**; the Overview of the source's tables from the catalog cache with mapping counts, ticks and **Create mappings**; the mapping editor (name, Cancel, Delete, Save mapping), the two sides with **Override for this table**, the table picker from the cache, **Custom query, not a table** (the query text, no rows), the source filter; Column Mapping as the old `ColumnMappingEditor` (**Auto-map by name**, source type, target type, transform, the primary key ticks, relationships, the cached metadata); the sub-tabs Notes, Custom Transforms, Bulk Load (`DefaultSegmentingCard`), Provisioning (the mapping's plan, its pending steps as the tab's badge), Pipeline (the overrides), Diagnostics (`traceTiming`), Verify (the `verification:` checks as `ChecksCard`) | SYNCAUTHORING-4 | L | Playwright: tick two tables and Create mappings makes two mapping files; on one, Auto-map by name maps three columns, untick a key column, override the target side's schema, add a row-count verification and a default segmenting by month; the Provisioning badge counts the missing table's step; Files shows the same changes |
| SYNCAUTHORING-6 | Documentation and coverage: `docs/ui-coverage.md` names every Designer page and its stories; the `docs/ui-editors.md` rows of `sync-task-config`, `sync-mapping-config`, the checks and the verification checks name the Designer screens and Files (the rows of the connections and `sync-script-config` keep Files and say the Designer does not cover them yet); `docs/authoring-parity.md`'s Sync rows; the Sync part of `DbDataWorkspace/README.md` says what the Designer covers (inside a replication), that connections and scripts are edited in Files, and that monitoring is the Runner's screens; the shots under `DbDataSync/screenshots/golden-path/` that show a replication's authoring are retaken from the stories | SYNCAUTHORING-4, SYNCAUTHORING-5 | S | `scripts/check_ui_coverage.py` and `scripts/check_ui_editors.py` pass with the new rows; the stories run in CI |

## Needs from other subjects

- AUTHORING: the other project kinds (build, store, integration, app) adopt the two areas of SYNCAUTHORING-1 as their Designers are built: each
  kind's designed screens register as its Designer (its routes and navigation entries) rather than as tabs of the Files area, and a task that builds one
  says so.
- AUTHORING-14: the engine list of the Pipeline sub-tabs comes from the engine registry (SYNCAUTHORING-4);
  until it lands the Designer uses the list `SyncSetPanel.tsx` has today. Ownership is SYNCAUTHORING-P7.
- AUTHORING-3: a segmenting strategy's **Try** (SYNCAUTHORING-4); until then it says it waits on a Runner task.
- AUTHORING-4: the source side of the Provisioning sub-tabs (SYNCAUTHORING-4, SYNCAUTHORING-5); until then the source is "not known".
- AUTHORING-6: the catalog cache of a Sync connection, which the table pickers, Create mappings, Column Mapping and the metadata routes read
  (SYNCAUTHORING-3, SYNCAUTHORING-5); who fills it, a connection's test before publish and the live checks wait on AUTHORING-P2 and SYNCAUTHORING-P9.
- AUTHORING-9: its stories of the Sync forms stay the stories of the Files area; this subject's stories cover the Designer (no story is written twice).
- AUTHORING-12: reconcile deletes and resync stay paused; they come back with the monitoring pass, not in the Designer's authoring screens.

## Proposed decisions

### SYNCAUTHORING-P6: where the restored screens live (awaiting the owner)

Scenario: a worker restores `ColumnMappingEditor.tsx` and `ReplicationChecksPanel.tsx` from git. Option A: the Sync Designer and its cards are a shared
package `@dbdatakit/sync-ui` (`Shared/web/sync-ui`, an npm workspace like `store-ui`), registered as the sync kind's Designer by `@dbdatakit/workspace`;
components any product uses go to `@dbdatakit/shell`. Option B: everything goes into `Shared/web/workspace/src/sync/`. Under A the package is built and
tested on its own and the monitoring pass can add its screens beside them for the console's Runner pages; under B the Workspace package grows by several
thousand lines of one kind's screens. Recommendation: A.

### SYNCAUTHORING-P7: who owns the engine list of the Sync Designer (awaiting the owner)

Scenario: AUTHORING-14 replaces the list written in `SyncSetPanel.tsx`, and SYNCAUTHORING-4 needs an engine list for the Pipeline sub-tabs (the
Connections page's **New connection**, which needed one too, is outside a replication and not in this pass). Option A: AUTHORING-14 stays in AUTHORING
and lands first as a hook (`useSyncEngines`) the Files area and the Designer both
use. Option B: AUTHORING-14 moves into this subject. Option C: both build it. Recommendation: A, so AUTHORING-14 is not blocked by this subject's larger
tasks.

### SYNCAUTHORING-P8: how the ported services run in the Workspace server (awaiting the owner)

Scenario: a mapping's Custom Transforms sub-tab asks for the slots a script can bind to, its Pipeline asks for the readers and writers the source's and
the target's drivers offer, and the Column Mapping tab asks for the inferred key of `dbo.customers`. The code that answered them lives in
`DbDataSync.Scripting`, `DbDataSync.Drivers.*` and `DbDataSync.Provisioning`, product projects the Workspace (under `Shared/`) may not reference.
Option A: the libraries the services need (the script host's slot list, the driver descriptors and registry, the mapping column reader, and the
configuration model they read) move to `Shared/src/DbDataKit.Sync.*` with adapters left in `DbDataSync`, and the Workspace composes them in its own
process; testing a person's script still runs in a child process, as `script-test` does today, since it runs code the person wrote. Option B: every
service is a `dbdatasync` verb with `--json` run on a copy of the draft in a child process (`script slots`, `driver describe`, `mapping infer`,
`mapping create --from-cache`), as `SyncDraftTools.cs` runs the scripts list and the provisioning plan today; nothing moves. Under A each answer is a
call in the process (fast enough for the column picker as you type) and the libraries become the suite's; under B the Workspace stays free of Sync's
assemblies and the verbs are usable from a terminal, at the cost of a process per call. Recommendation: A for the slot list, the descriptors and the
column reader; anything that compiles or runs a person's script stays a child process (only the existing `script-test` in this pass; a script's compile
comes with the Scripts page, outside a replication, later).

### SYNCAUTHORING-P9: discovering and checking a replication's source and target in the Designer (awaiting the owner)

Who reads a database, with which login, and where the answer is kept is AUTHORING-P2 (recommended: Runner tasks through a channel's binding; the
Workspace holds no login). This proposal is what a sync project discovers and checks under that rule, case by case. Until both are answered, this
subject's Designer reads only the catalog cache, as the Files area does today.

**Discovery.**

- *A new replication.* Ana presses **New replication** in the Designer's navigation and, on the new replication's Overview, picks `crm` and `dw`. The database pickers list the cache's databases of each connection
  (`catalog-cache/2`, AUTHORING-P2); a connection with no cache shows **Refresh catalog** with the channel picker, which runs the built-in `discover` task on
  the Runner (AUTHORING-P2, scenario 1) and fills the pickers when it ends.
- *Table Mappings.* The Overview lists the source's cached tables with their mapping counts; **Create mappings** writes a mapping file per ticked table from
  the cache (columns, types, key; the target's types inferred by the ported `MappingColumnReader` over the cache). **Refresh** on the Overview reads the
  connection again; **Refresh** on a mapping's cached-metadata card reads one table; both show the difference before the cache changes, and the Check tab
  then lists the mappings it touches (a mapped column gone, a type changed).
- *A query source.* **Custom query, not a table**, then **Describe columns**: the built-in `describe` task in the chosen channel (read-only policy first,
  zero rows) gives the columns, written into the mapping's column cache; the query's rows stay AUTHORING-P1's question.
- *A connection whose driver is Sync's own* (a YAML driver, or a connection with a `metadataProvider` script): the built-in package does not run Sync's
  drivers or scripts, so discovery and describe run as the tasks `discover` and `describe` of the project's own package (Sync's `MetadataService` behind
  them), which needs a build staged in that channel; the Designer says so and offers **Build and stage for dev**.

**Checking the source** (each item says whether it is offline, from the cache and the files, or live, as a check of the package's `preflight` task with
the run login the replication will use):

| Check | Offline | Live (`preflight`, run login, read-only) |
| --- | --- | --- |
| the table and every mapped column exist | yes, against the cache | yes, the catalog read again |
| the filter's columns exist and the filter compiles | the column names the filter names, against the cache; a filter the dialect's tokenizer cannot read is "not checked offline" | a zero-row read `WHERE (<filter>) AND 1 = 0` |
| the login can read the table | no | the engine's privilege function (`HAS_PERMS_BY_NAME`, `has_table_privilege`), then the same zero-row read |
| change tracking, CDC or a logical slot is on where the reader's strategy needs it | no (today's offline plan says `unknown`) | the database's and the table's setting, `wal_level`, the slot and the publication; a missing one is a provisioning step of the source side (absorbs AUTHORING-4) |
| every source type is one the driver reads | yes, the driver's type map over the cached type text | the engine's own type of each column |
| a key for a reader or writer that needs one | yes, from the cache's key flags | yes |

**Checking the target.**

| Check | Offline | Live (`preflight`, run login, nothing written) |
| --- | --- | --- |
| the table exists, or provisioning can create it | the provisioning plan from the cache (built), the create or alter statements shown | the plan again from the live catalog, as the `provision` task plans before it applies |
| the login can insert, update and delete; for provisioning, create in the schema and alter the table | no | the engine's privilege functions on the table and the schema; never a trial write |
| types are compatible with the mapping (each source column's inferred type against the target column's) | yes, against the cache | yes |
| the keys agree (the mapping's key against the target's primary or unique key; no key column nullable) | yes | yes |
| where the replication's state is kept (the `tracking:` schema on the target, TASKENGINE-6) | the configured place is named | the schema and tables exist or the login may create them |

**The connection test.** Each endpoint card's **Test** (and the Connections page's, when that page comes back: the open design "Outside a replication,
later") runs the binding test in the chosen channel (AUTHORING-P2, scenario 5):
binding, document, the login's secrets, then reach, TLS and login, with the server's version; the old page's **Test all** and its reachable column are the
monitoring pass's. The old test's sample rows are not restored.

**Where the Designer shows it.** The Check tab's count includes the offline findings; the Provisioning sub-tabs show the offline plan, and the newest
`preflight` run of the chosen channel (time, run link, findings per mapping); the publish panel runs `preflight` on the staged version before activation
(AUTHORING-P2, scenario 6). Nothing live is committed into the draft; the cache and a query's column cache are.

**Options.** A: as above: offline checks in the Check tab and the plan, live checks as one `preflight` task of the replication set's package, run on the
staged version (before publish to a channel) or the served one (Re-check). B: no live check in authoring; the first pass fails naming what is missing,
as it does today. C: the live checks as tasks of the built-in package (AUTHORING-P2) instead of the set's own, so they run before any build exists, at the
cost of the built-in package learning each reader's strategy and Sync's drivers. Scenario: Ana maps `crm.dbo.orders` with the change-tracking reader and
publishes to `prod`, where change tracking is off on that table. Under A the staging's `preflight` reports "change tracking is off on dbo.orders" with the
statement that enables it, before the version is served; under B the first scheduled pass fails at 02:00; under C the same as A, from a package that has to
follow every release of Sync's readers. Recommendation: A.

**If accepted, the tasks:** the Sync package's manual read-only task `preflight` (every check above, per mapping, findings as step outputs; AUTHORING-4
folds into it); the offline checks in the sync draft's check (columns, the filter's columns, types, keys, compatibility) over the cache; `describe` into a
mapping's column cache; the set's own `discover` and `describe` for Sync's own drivers; the Designer's Refresh, Describe, Test and newest-check card on
the shared pieces of AUTHORING-P2.

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

### Outside a replication, later

The old Sync screens outside a replication's page, left out of this pass by the owner. Meanwhile a replication is reached from the Designer's navigation
(the draft's replications and **New replication**), and connections and scripts are edited in Files (the generated forms of `config/connections/` and
`config/scripts/`, and Files' Scripts tab, which lists the scripts and tests one against generated input). Where these screens live (Designer pages of
a sync project with their own navigation entries, or Designer pages shared by every kind that has connections) is designed before they become tasks.

- **The Replications list** (`pages/ReplicationsPage.tsx`): a table of every replication with its reader, its schedule as a sentence and its mapping
  count, a row opening the replication, and the form that makes a new one. Needs: nothing beyond the draft model (the files of
  `config/replications/`) and the check's findings per replication; the form itself is restored in this pass as the navigation's **New replication**.
- **The Connections list** (`ConnectionsPage.tsx`): driver, host, database and authentication per connection, **New connection**, and (monitoring) the
  reachable column and **Test all**. Needs: the draft's connection files; the engine list of **New connection** from the engine registry (who owns it is SYNCAUTHORING-P7);
  the reachable column and Test all wait on the connection test of SYNCAUTHORING-P9 and the monitoring pass.
- **A connection's page** (`ConnectionEditPage.tsx`, `connection-edit/RunLoginCard.tsx`, `connection-edit/ConnectionTestCard.tsx`): engine, address,
  database, timeouts, the driver's own parameters with their check, the login by secret reference and the run login, custom transforms and hooks, the
  test. Needs: the driver's connection parameters and the parameter check (`connection-parameters`, `Services/ParameterCheck.cs`), a driver's
  capabilities by type (`/api/drivers/{type}/capabilities`), a connection's credential source (`credential-source`), ported as the services above are
  (SYNCAUTHORING-P8); the secret findings of the draft's check; a link to the project's secrets overview (the AUTHORING subject's); the test as the binding test
  in a channel (AUTHORING-P2, SYNCAUTHORING-P9), without the old test's sample rows.
- **The Scripts list** (`ScriptsPage.tsx`): kind, language, where each script is bound, enabled, **New script**. Needs: the draft's `config/scripts/`
  (the `scripts` route exists) and where each script is bound (`Services/ScriptUsageScanner.cs`).
- **A script's page** (`ScriptEditPage.tsx`, `script-edit/ScriptTestPanel.tsx`): the manifest's fields, the code in the code editor, compile
  diagnostics at their lines, the test against generated input, where it is bound. Needs: a script's compile (`ScriptsController` compile, which runs
  the person's code and so stays a child process), where it is bound, and the `script-test` route the Workspace has.

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
