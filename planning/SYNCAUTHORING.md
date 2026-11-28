# SYNCAUTHORING: the Sync authoring screens, restored as the Designer of the Workspace's sync projects

## Goal

A person who authors a replication set in a Workspace sync project works, inside a replication, in the screens the Sync server had for authoring
it: a replication's page with its tabs and sub-tabs, the table mappings with the source and target side by side and the column mapping editor, its
quality checks. They are the **Designer** of a sync project; **Files** (today's stock editor: each file as its generated form or YAML) stays beside it
as the fallback. Files and Designer are two areas of a project, each with its own address and its own navigation entries, the same for every kind of
project; the person moves between them by clicking. Sync is the first kind with a Designer (the design is in `DbDataWorkspace/README.md`, "A project's
two areas: Files and Designer").

This pass is authoring only, and only what lives inside a replication. The screens are taken from the code as it was before the Sync server was deleted (`96601f27^`), with everything that
watched or operated a running replication taken out; those screens come back in a second pass reading the Runner (SYNC's open design "Monitoring and
operations, second pass" below). The old Version Control tab is not restored: history and diffs are the Files area's History, Changes and earlier
texts. The old screens outside a replication (the Replications list, the Connections and Scripts pages) are not restored either: a replication is
reached from the Designer's navigation, and connections and scripts are edited in Files until SYNC's open design "Outside a replication" (and SANDBOX's connection pages) becomes
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

The Workspace server answers, for a sync draft (`SyncDraftTools.cs`, running `dbdatasync` on a copy of the draft): the check, the scripts list
(`GET .../scripts`), the provisioning plan from the catalog cache (`POST .../provisioning-plan`) and the build. A script's test, the catalog's reads,
deploys and runs go through the console to a Runner or a Gateway as the person (`docs/authoring-metadata.md`, `docs/publishing.md`).

### What is ported (authoring)

Under `96601f27^:Shared/web/sync/src/`, with the old Sync API call each used and what answers it in the Workspace.

| Old screen or component | Old call | In the Workspace |
| --- | --- | --- |
| The way into a replication: the Designer's navigation lists the draft's replications (no list page) and ends with **New replication**, the old page's form (name, schedule: `pages/ReplicationsPage.tsx`, its form only) as a dialog | `GET /api/replications`, `PUT /api/replications/{n}` | the draft's `config/replications/*/task.yaml`, read and written through the draft model (no route) |
| Replication page chrome (`pages/ReplicationDetailPage.tsx`, `replication-detail/tabs.tsx`): breadcrumbs, the tab bar, **Enabled**, **Save settings**, **Delete** | `PUT .../enabled`, `PUT /api/replications/{n}`, `DELETE` | `enabled` and the replication's folder in the draft |
| Rail: **Unpublished changes** (`components/UnpublishedChanges.tsx`) | `GET /api/runner/package/unpublished` | the draft's commits after the commit of the build last published, and its unsaved edits (the Workspace's builds and history) |
| Overview (`OverviewPanel.tsx`, `EndpointsCard.tsx`, `components/EndpointSidePair.tsx`, `ScheduleCard.tsx`, `TrackingCard.tsx` for the `tracking:` settings) | the replication's config | `task.yaml`; connection and database pickers from the draft's connections and the catalog cache |
| Overview sub-tabs Notes (`components/NotesPanel.tsx`), Pipeline (`MappingPipelineCard.tsx`, and `ReconcileConfigCard.tsx` for the `reconcile:` settings), Provisioning (`ReplicationProvisioningPanel.tsx`), Custom Transforms (`components/ScriptBindings.tsx`), Bulk Load (`SegmentingStrategiesCard.tsx`) | config; `GET .../provisioning`; a connection's capabilities; `/api/scripts`, `/api/scripts/slots` | `task.yaml`; the readers, staging providers and writers as a ported service; the draft's scripts from the `scripts` route the Workspace has and their slots as a ported service; the plan from `POST .../provisioning-plan`; its apply stays the Runner's `provision` task as `SyncProvisioningPanel.tsx` starts it today; a strategy's **Try** runs the set's task `try-strategy` through the console (built: `docs/authoring-metadata.md`, "Rows and trials are answered, never kept") |
| Table Mappings sidebar and Overview (`TableMappingsPanel.tsx`, `MappingsOverview.tsx`): the source's tables, mapping counts, ticks, **Create mappings** | `GET .../table-mappings`, metadata, `POST .../table-mappings/bulk` | the mapping files; tables from the catalog cache; bulk create as a ported service (below) |
| Mapping editor (`TableMappingForm.tsx`, `MappingSide.tsx`, `components/InheritableToggle.tsx`, `SourceFilterCard.tsx`, `QuerySourcePanel.tsx` without its rows, `SavedMappingHeading.tsx`) | config; metadata | the mapping file; the catalog cache |
| Column Mapping (`ColumnMappingEditor.tsx`, `suggestMappings.ts`, `PrimaryKeyCard.tsx`, `naturalKey.ts`, `RelationshipsCard.tsx`, `CachedMetadataCard.tsx`, `components/ColumnMultiPicker.tsx`) | metadata; `.../provisioning/inferred-column-types`, `inferred-natural-key`, `.../refresh-metadata` | the mapping file and the catalog cache; inferred types and key as a ported service; refresh is the shared **Refresh** from a Runner channel (AUTHORING-6); `MappingColumns.tsx` folds into it |
| Mapping sub-tabs Notes, Custom Transforms, Bulk Load (`DefaultSegmentingCard.tsx`), Provisioning (`ProvisioningCard.tsx`, the plan only), Pipeline (the overrides), Diagnostics (`traceTiming`), Verify (`ChecksCard.tsx`, the `verification:` list only) | config; `.../provisioning` | the mapping file; the plan |
| Quality Checks (`ReplicationChecksPanel.tsx`, `checkForms.ts`) | `GET\|PUT .../checks` | `checks:` of `task.yaml` (`check/1`) |
| Helpers with tests: `api/readerNotes.ts`, the writer defaults of `api/types.ts` (`writerDefaults.test.ts`), `api/resolveEndpoint.ts`, `api/canonicalJson.ts`, `api/tableExists.ts`, `mappingTabs.ts`; the shared pieces the replication's cards use: `components/CodeEditor.tsx` (a filter, a query), `ParameterForm.tsx` and `parameterValues.ts` (a script's parameters in `ScriptBindings.tsx`) | | restored with their tests |

### What is left out of this pass

- **Monitoring and operations** (SYNC's open design): `StatusCard.tsx`, `MetricsCard.tsx`, `RefreshCountdown.tsx`, `MonitoringPanel.tsx`, `lag.ts`,
  `holdState.ts`, `readIntent.ts`, `components/ReadIntentSetting.tsx`, `MappingReadStateDialog.tsx`, `MappingPositionHistory.tsx`, `RunsPanel.tsx`,
  `RunDetailsDialog.tsx`, `RunFigures.tsx`, `runTimes.ts`, `PauseHistoryPanel.tsx`, `PauseDialog.tsx`, `BulkLoadHistoryPanel.tsx`,
  `BulkLoadProgressCard.tsx`, `BulkLoadForm.tsx`, `ReconcileDeletesForm.tsx`, `JoinedRunNotice.tsx`,
  `RunnerHistoryNotice.tsx`, `RunnerStatusRow.tsx`, `RunningCounts.tsx`, `ReplicationRunnerPanel.tsx`, `pages/RunnerPage.tsx`, `RunnerLinkCard.tsx`,
  `runnerPublish.ts`, `VerificationPanel.tsx`'s results and `VerificationResultPage.tsx`, `api/useRunHub.ts` (SignalR), the tracking card's live state
  and catch-up, and the connection list's reachable column and **Test all**.
- **Outside a replication** (SYNC's open design "Outside a replication"; the connection pages are SANDBOX's): the Replications list's table (`pages/ReplicationsPage.tsx` apart from
  its form), the Connections list and page (`ConnectionsPage.tsx`, `ConnectionEditPage.tsx`, `connection-edit/RunLoginCard.tsx`,
  `connection-edit/ConnectionTestCard.tsx`), the Scripts list and page (`ScriptsPage.tsx`, `ScriptEditPage.tsx`, `script-edit/ScriptTestPanel.tsx`).
  Connections and scripts are edited in Files (the generated forms, the Scripts tab) meanwhile.
- **Row previews**: `PreviewGrid.tsx` is not restored; **Preview rows** on a mapping (`MappingRowsPreview.tsx`) shows a mapping's or a query source's first rows, read by a Runner task and never kept.
- **Version Control** (`HistoryPanel.tsx`, `components/DiffViewer.tsx` as used there): the Files area's History, Changes and a file's earlier texts.
- **Preview SQL** (`MappingPreview.tsx`): the statements of a pass need the drivers with a connection and the read position; SYNC's open design.
- The product frame and admin pages (`App.tsx`, `SyncModule.tsx`, `adminHost.tsx`, `components/AppShell.tsx`, Drivers, Libraries, Files, Docs, Admin):
  the Workspace's frame replaces them.

### The old services the ported screens need

From `96601f27^:DbDataSync/src/DbDataSync.Api/` (and the libraries it composed), only those the screens inside a replication call. Reading and writing a
replication, a mapping or `checks:`, and reading the draft's connections and scripts, is a file edit or read of the draft through the Workspace's files API; no route is ported for it. What needs code on the server:

| Old service or controller | What it answered | Composed from |
| --- | --- | --- |
| `ScriptsController` slots | the script slots a binding can name (Custom Transforms, `ScriptBindings.tsx`) | `DbDataSync.Scripting` (`ScriptHost`) |
| `ConnectionsController` capabilities; `Services/SyncEngines.cs` | what a connection's driver can do: its readers, staging providers and writers (the Overview's and a mapping's Pipeline, `useReplicationCapabilities`) | `DbDataSync.Drivers.Abstractions` (`DriverRegistry`), `DbDataSync.Drivers.Descriptor`, `DbDataKit.Connections.Descriptor` (the engine registry) |
| `MetadataController` (databases, tables, columns) | a connection's metadata | the draft's catalog cache (`DbDataKit.Catalog.Cache`); how the cache is filled and refreshed is `docs/authoring-metadata.md`, what is checked live its section "A sync project" |
| `ProvisioningController` inferred column types and natural key; `Services/MappingMetadataService.cs`, `MappingMetadataCapture.cs`; `TableMappingsController` bulk create | the target types and key of a mapping from its source columns; a mapping file per ticked table with its columns and key | `DbDataSync.Provisioning` (`MappingColumnReader`) over the catalog cache |
| `ProvisioningController` and `ReplicationProvisioningController` plan | a mapping's and a replication's provisioning plan | the Workspace's `provisioning-plan` route (exists) |

Left on the old server: everything in `Runner/`, `Hubs/`, `RunsController`, `ReplicationLagController`, `MetricsController`, `VerificationController`,
`ReplicationTrackingController`, the read-state and position routes of `TableMappingsController`, `PreviewController`, `QualityController`,
`LineageController`, the admin, driver, library and files controllers. Not ported in this pass because only the screens outside a replication used them
(SYNC's open design "Outside a replication"): a script's compile (`ScriptsController` compile) and where each script is bound
(`Services/ScriptUsageScanner.cs`), a driver's capabilities by type (`/api/drivers/{type}/capabilities`), a driver's connection parameters
(`connection-parameters`, `Services/ParameterCheck.cs`) and a connection's credential source (`credential-source`). The ported services run in the
Workspace's process on Sync's libraries in `Shared/src/DbDataKit.Sync.*` (`DbDataWorkspace/README.md`).

## First slice

Owner, 2026-11-18: "Your limited plan for sync is fine, but we do need to be able to pull table lists and refresh metadata. Let's make sure that the
differences of the smaller plan are clear, and don't cause issues implementing the larger plan." The first slice is a person making a replication in
the Designer: New replication, its source and target connections and databases, its schedule, the source's table list pulled from a Runner channel
and refreshed with the difference shown, mappings made from the ticked tables and edited side by side with Column Mapping, and publishing through the
Builds tab. Its tasks, in build order:

1. Built: the engine lists from the registry (`useSyncEngines` of `@dbdatakit/workspace`, `DbDataWorkspace/README.md`).
2. Built: the Files and Designer areas, Sync's replications in the Designer's navigation (`DbDataWorkspace/README.md`, "A project's two areas").
3. Built: `@dbdatakit/sync-ui`, the sync Designer the console registers: the draft model `useSyncDraft`, the metadata hooks over the draft's
   `catalog-cache.yaml`, and the slice's components (`DbDataWorkspace/README.md`, "A project's two areas").
4. `catalog-cache/2` (whole; built, `docs/authoring-metadata.md`).
5. Built: the Runner's built-in package `dbdatakit-metadata` with `discover` and `describe` (`docs/authoring-metadata.md`).
6. Built: a replication's page with its Overview, sub-tabs and Quality Checks, the chrome, New replication (`DbDataWorkspace/README.md`,
   "A project's two areas"; the story `sync-designer.spec.ts`).
7. Built: the Table Mappings sidebar and overview with Create mappings, the editor's two sides, Column Mapping, and the rest of a mapping (its
   source filter or query, its tabs)
   (`DbDataWorkspace/README.md`, "A project's two areas"; the story `sync-designer.spec.ts`).
8. Built: the catalog read from a Runner channel, a route of the console (`docs/authoring-metadata.md`, "How a screen asks for a read").
9. Built: the channel picker and **Refresh** (connection or table, the difference shown, then accepted), the shared pieces the slice's Refresh buttons
   are (`ChannelPicker`, `CatalogRefresh` of `@dbdatakit/workspace`; `docs/authoring-metadata.md`, "The catalog cache").
10. Built: the Designer's Refresh buttons on the endpoint cards, the Table Mappings overview and the cached-metadata card (`DbDataWorkspace/README.md`,
    "A project's two areas"; `RefreshPanel.tsx` of `@dbdatakit/sync-ui`).
11. Built: **Build and publish…** in a replication page's rail opens the draft's Builds tab, whose panel deploys through the console
    (`PublishCard.tsx` of `@dbdatakit/sync-ui`; `DbDataWorkspace/README.md`, "A project's two areas").

**The rule: the slice only leaves features out.** Every slice task builds the full design's routes, formats and components (the Designer's addresses,
`@dbdatakit/sync-ui` and its draft model, `catalog-cache/2`, the package `dbdatakit-metadata` and its answer, the console's catalog read, the shared channel
picker and Refresh) and leaves features out; it builds no stand-in API, format or screen that a larger task would replace. A larger task adds to what
the slice built and names the slice task it follows. Where a slice task uses something the larger plan changes later, it names that task:

- **The Workspace service calls no other service** (WORKSPACEISOLATION). The slice adds no call from it to a Runner or a Gateway: a read of a
  database goes from the screen to the console, which starts the Runner's task as the person (`api/workspace-catalog/...`). Publishing from the Designer
  opens the Builds tab, whose panel deploys through the console (`POST /api/deploys`).
- **No ported service.** The slice's screens read the draft's files and `catalog-cache.yaml` in the browser (`catalogCache.ts`, unsaved edits
  included) and fill the cache through the console's catalog read (`api/workspace-catalog/...`), which starts the built-in package's `discover` on the Runner; none of the Workspace's `sync/` routes
  (`DbDataWorkspace/README.md`) is called, so the slice uses neither Sync's shared libraries (`Shared/src/DbDataKit.Sync.*`) nor SYNCAUTHORING-8. The slice's Create mappings wrote each file through the
  client's mapping writer with the cache's columns and key and no `targetType`; the Designer now asks the Workspace's `sync/bulk-create` for the files
  and `sync/inferred-columns` for the types and key it shows (`DbDataWorkspace/README.md`, "A project's two areas"). The cache stays read in the browser
  with those routes too: the Workspace has no metadata route (`docs/authoring-metadata.md`, "A sync project").
- **No Set up for development** in the slice: a read needed the project's package on the Runner with the connection's slot bound in the channel,
  done by hand; the channel picker named a channel that binds no slot and said how. **Set up for development** is built since: the channel picker
  stores the login, copies the draft's connection to the Runner and binds the slot (`docs/authoring-metadata.md`, "Before anything is published: Set
  up for development").
- **No `facts`** (AUTHORING-17). A connection whose driver is Sync's own is read by the set's own `discover` and `describe`, with **Build and stage**
  where the channel holds no build (`docs/authoring-metadata.md`, "Sync's own drivers: the set's `discover` and `describe`"). The set's `preflight` is built: the
  console runs it before it serves a version in `prod`, and **Re-check** on the Runs tab runs it (`docs/publishing.md`, "Preflight before
  activation"; `docs/authoring-metadata.md`, "A sync project"); the Designer shows its newest run on the Provisioning sub-tabs (**Newest preflight**,
  `NewestPreflight.tsx` of `@dbdatakit/sync-ui`), and **Test** and **Describe columns** are placed on the endpoint cards and a query source's
  Column Mapping (`DbDataWorkspace/README.md`, "A project's two areas").
- **Publishing** is the Files area's Builds tab, linked from the Designer (**Build and publish…**, `PublishCard.tsx`), the card that also
  holds **Unpublished changes**.

## Tasks

In priority order (the first row is the first task). Every Designer task keeps the Files area working on the same draft, and its browser story clicks into Files and back without losing an unsaved edit.
The stories are `sync-designer.spec.ts` in `DbDataKit/tests/DbDataKit.Server.Web.Tests` against `workspace-fake.ts`.

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| SYNCAUTHORING-8 | Delete the forwarding projects left in `DbDataSync/src` when Sync's libraries moved to `Shared/src/DbDataKit.Sync.*` (`DbDataSync.Core`, `.Drivers.Abstractions`, `.Scripting.Abstractions`, `.Drivers.Generic` and `.Provisioning` hold only `TypeForwards.cs`; `.Drivers.Descriptor` forwards what moved and keeps what loads a library or compiles a `driver.csx`, with `DriverDescriptorReader` as a thin adapter over the shared `DriverDescriptors`): DbDataSync's projects reference `DbDataKit.Sync.*` directly (`<DbDataKitReference>`), the forwarding types and any project left empty are removed, the three engine tests left in `DbDataSync.Drivers.Generic.Tests` reference the shared library, and the tasks and docs that name the old projects are updated in the same pull request; the shared libraries' namespaces become `DbDataKit.Sync.*` (a transform script, a `driver.csx` and the script compiler's imports use the new names), and a set's check names each old `DbDataSync.*` `using` in its scripts with the name that replaces it | none | S | DbDataSync builds and its unit and integration suites pass with no forwarding type left (a test lists the `TypeForwardedTo` attributes of DbDataSync's assemblies and finds none); `scripts/check_solution.py` passes; a script with `using DbDataSync.Scripting.Abstractions;` fails its check naming `DbDataKit.Sync.Scripting.Abstractions` |
| SYNCAUTHORING-6 | Documentation and coverage: `docs/ui-coverage.md` names every Designer page and its stories; the `docs/ui-editors.md` rows of `sync-task-config`, `sync-mapping-config`, the checks and the verification checks name the Designer screens and Files (the rows of the connections and `sync-script-config` keep Files and say the Designer does not cover them yet); `docs/authoring-parity.md`'s Sync rows; the Sync part of `DbDataWorkspace/README.md` says what the Designer covers (inside a replication), that connections and scripts are edited in Files, and that monitoring is the Runner's screens; the shots under `DbDataSync/screenshots/golden-path/` that show a replication's authoring are retaken from the stories | none | S | `scripts/check_ui_coverage.py` and `scripts/check_ui_editors.py` pass with the new rows; the stories run in CI |
| SYNCAUTHORING-20 | The three rules the offline check copies from Sync's engine become public methods both call: the Flight SQL driver's reading of a type (`FlightSqlTypes` in `DbDataKit.Sync.Core`), `Scd2Writer.RequireNaturalKey` and `KeyReconcileDeleteWriter.RequireKey` | none | S | `SyncOfflineCheck` holds no copied rule, and its tests and the writers' tests pass |

## Needs from other subjects

- AUTHORING: the other project kinds (build, store, integration, app) adopt the two areas (`registerDesigner`, `DbDataWorkspace/README.md`) as their Designers are built: each
  kind's designed screens register as its Designer (its routes and navigation entries) rather than as tabs of the Files area, and a task that builds one
  says so.
- SANDBOX (step 2): a script's Test runs on a Runner today (the set's task `script-test`, staged through the console); SANDBOX moves it into the
  Workspace's sandbox.
- CLI (needs review): if it is accepted, CLI-3 moves Sync's engine of check and package from `dbdatasync` into `Shared/src`; Sync's
  configuration model, slot list, driver registry and column reader are there already (`Shared/src/DbDataKit.Sync.*`), and CLI-3 builds on
  those projects. CLI-6 removes the program `dbdatasync`, which changes what SYNCAUTHORING-8's adapters forward to, and
  CLI-4 changes the form of the runtime package that holds the set's `preflight`, `discover` and `describe` (`SetPreflightJob` and `SetCatalogJobs.cs` in `DbDataSync.TaskRunner`).
- AUTHORING-17, after the slice: `facts` (`docs/authoring-metadata.md`). **Set up for development** is built: it stores the login, copies the draft's
  connection to the Runner and binds the slot (`docs/authoring-metadata.md`, "Before anything is published: Set up for development"). The Designer
  places the shared **Set up for development**, **Test** and **Describe columns** already (`ChannelReads.tsx` of `@dbdatakit/sync-ui`); the connection
  test is the console's
  `POST /api/workspace-catalog/{workspace}/{project}/binding-test` (`docs/authoring-metadata.md`, "Testing a connection").
- TESTING-1: its stories of the Sync forms stay the stories of the Files area; this subject's stories cover the Designer (no story is written twice).
- SYNC-3: reconcile deletes and resync stay paused; they come back with the monitoring pass, not in the Designer's authoring screens.

## Shared areas touched

- `Shared/web/workspace` (the Files/Designer frame, routes), `Shared/web/sync-ui` (the new npm workspace `@dbdatakit/sync-ui`),
  `Shared/web/package.json` (its workspaces), `Shared/web/shell` (generic components moved in).
- `Shared/src/DbDataKit.Authoring.Server` (the sync routes), `Shared/src/DbDataKit.Sync.*` and `DbDataSync/src` (the moved libraries and their adapters),
  `DbDataKit/src/DbDataKit.Server/ServerProducts.cs` (the relay table), the console's catalog read (`ConsoleCatalogRead`).
- `DbDataKit/tests/DbDataKit.Server.Web.Tests` (`sync-designer.spec.ts`, `workspace-fake.ts`), `DbDataWorkspace/tests/DbDataWorkspace.Tests`,
  `DbDataSync/screenshots/`.
- `DbDataWorkspace/README.md`, `docs/ui-coverage.md`, `docs/ui-editors.md`, `docs/authoring-parity.md`.
