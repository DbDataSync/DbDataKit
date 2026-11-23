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
| Column Mapping (`ColumnMappingEditor.tsx`, `suggestMappings.ts`, `PrimaryKeyCard.tsx`, `naturalKey.ts`, `RelationshipsCard.tsx`, `CachedMetadataCard.tsx`, `components/ColumnMultiPicker.tsx`) | metadata; `.../provisioning/inferred-column-types`, `inferred-natural-key`, `.../refresh-metadata` | the mapping file and the catalog cache; inferred types and key as a ported service; refresh is the shared **Refresh** from a Runner channel (AUTHORING-6); `MappingColumns.tsx` folds into it |
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
| `MetadataController` (databases, tables, columns) | a connection's metadata | the draft's catalog cache (`DbDataKit.Catalog.Cache`); how the cache is filled and refreshed is `docs/authoring-metadata.md`, what is checked live its section "A sync project" |
| `ProvisioningController` inferred column types and natural key; `Services/MappingMetadataService.cs`, `MappingMetadataCapture.cs`; `TableMappingsController` bulk create | the target types and key of a mapping from its source columns; a mapping file per ticked table with its columns and key | `DbDataSync.Provisioning` (`MappingColumnReader`) over the catalog cache |
| `ProvisioningController` and `ReplicationProvisioningController` plan | a mapping's and a replication's provisioning plan | the Workspace's `provisioning-plan` route (exists) |

Left on the old server: everything in `Runner/`, `Hubs/`, `RunsController`, `ReplicationLagController`, `MetricsController`, `VerificationController`,
`ReplicationTrackingController`, the read-state and position routes of `TableMappingsController`, `PreviewController`, `QualityController`,
`LineageController`, the admin, driver, library and files controllers. Not ported in this pass because only the screens outside a replication used them
(the open design "Outside a replication, later"): a script's compile (`ScriptsController` compile) and where each script is bound
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
6. SYNCAUTHORING-15: the part of SYNCAUTHORING-4: a replication's page with the Overview's endpoints and schedule, the chrome, New replication.
7. SYNCAUTHORING-16: the part of SYNCAUTHORING-5: the Table Mappings sidebar and overview with Create mappings, the editor's two sides, Column Mapping.
8. AUTHORING-21: the catalog read from a Runner channel, a route of the console (whole).
9. AUTHORING-24: the part of AUTHORING-6: the channel picker and **Refresh** (connection or table, the difference shown, then accepted), the shared
   pieces the slice's Refresh buttons are.
10. SYNCAUTHORING-18: the Designer's Refresh buttons on the endpoint cards, the Table Mappings overview and the cached-metadata card.
11. SYNCAUTHORING-17: **Build and publish…** from the Designer to the Builds tab, once WORKSPACEISOLATION-4 has moved its panel to the control
    service's deploy; the slice is finished only when WORKSPACEISOLATION-3 and WORKSPACEISOLATION-4 are.

**The rule: the slice only leaves features out.** Every slice task builds the full design's routes, formats and components (the Designer's addresses,
`@dbdatakit/sync-ui` and its draft model, `catalog-cache/2`, the package `dbdatakit-metadata` and its answer, the console's catalog read, the shared channel
picker and Refresh) and leaves features out; it builds no stand-in API, format or screen that a larger task would replace. A larger task adds to what
the slice built and names the slice task it follows. Where a slice task uses something the larger plan changes later, it names that task:

- **The Workspace service calls no other service** (WORKSPACEISOLATION). The slice adds no call from it to a Runner or a Gateway: a read of a
  database goes from the screen to the console, which starts the Runner's task as the person (AUTHORING-21). Publishing from the Designer
  waits until WORKSPACEISOLATION-4 has moved the Builds tab's panel to the control service's deploy (SYNCAUTHORING-17).
- **No ported service.** The slice's screens read the draft's files and `catalog-cache.yaml` in the browser (`catalogCache.ts`, unsaved edits
  included) and fill the cache through the console's catalog read (AUTHORING-21), which starts the built-in package's `discover` on the Runner; none of SYNCAUTHORING-3's routes is
  called, so the slice uses neither Sync's shared libraries (`Shared/src/DbDataKit.Sync.*`) nor SYNCAUTHORING-8. What the slice does without them: Create mappings writes each file through the
  client's mapping writer (`mappingColumns.ts`) with the cache's columns and key and no `targetType`, which SYNCAUTHORING-5 changes to SYNCAUTHORING-3's
  bulk create with the inferred target types and natural key; a table with no key gets none until then. The cache stays read in the browser
  after SYNCAUTHORING-3 too: it ports no metadata route (`docs/authoring-metadata.md`, "A sync project").
- **No Set up for development** (AUTHORING-19, which needs CHANNELS-1): a read needs the project's package on the Runner with the connection's slot
  bound in the channel, done by hand as `docs/authoring-metadata.md` says; the channel picker names a channel that binds no slot and says how.
  AUTHORING-6 adds the button.
- **No `facts`** (AUTHORING-17), no preflight and no live checks (SYNCAUTHORING-9 to SYNCAUTHORING-12, AUTHORING-22).
- **Publishing** is the Files area's Builds tab, linked from the Designer (SYNCAUTHORING-17); SYNCAUTHORING-4's Unpublished changes card later holds
  the same link. SYNCAUTHORING-17 is built after WORKSPACEISOLATION-4, so the Designer never links to a panel that publishes through the Workspace
  service; until then a person publishes from the Files area's Builds tab as today.

## Tasks

In priority order (the first row is the first task). Every Designer task keeps the Files area working on the same draft, and its browser story clicks into Files and back without losing an unsaved edit.
The stories are `sync-designer.spec.ts` in `DbDataKit/tests/DbDataKit.Server.Web.Tests` against `workspace-fake.ts`.

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| SYNCAUTHORING-15 | **First slice** (the part of SYNCAUTHORING-4 the slice builds). Designer, a replication: **New replication** as the last entry of the Designer's navigation (the frame's), the old form in a dialog (name and schedule; with no connection in the draft it says so and links to `config/connections/` in Files), which makes the task and opens its Overview; breadcrumbs as the heading; the replication's page at its full design's address with the tabs Overview and Table Mappings and the chrome **Enabled** (edits `enabled` of the draft's `task.yaml`; its title says it takes effect when published), **Save settings**, **Delete** (the replication's folder, asked twice); the Overview's endpoint cards side by side with the connection and database pickers from the draft's connections and the catalog cache's databases (`catalog-cache/2`), "inherited by N mappings unless overridden", and the schedule strip; an endpoint card whose connection has no cache says so (its **Refresh catalog** is SYNCAUTHORING-18's). Left for SYNCAUTHORING-4: the error counts in the navigation, the tab Quality Checks, the rail's Unpublished changes card, the tracking settings and the Overview's sub-tabs | none | M | Playwright (`sync-designer.spec.ts` against `workspace-fake.ts`): New replication makes the task and opens its Overview; pick the two connections and a database there; change the schedule to Periodic with a cron and see in Files that only those keys changed; Enabled off marks the draft not saved; Delete asks twice and the folder is gone from Files; a connection with no cache says so on its card; a click into Files and back keeps an unsaved edit |
| SYNCAUTHORING-16 | **First slice** (the part of SYNCAUTHORING-5 the slice builds). Designer, table mappings: the sidebar of mappings with column counts and **+**; the Overview of the source's tables from the catalog cache with mapping counts, ticks and **Create mappings**; the mapping editor (name, Cancel, Delete, Save mapping) with its two sides, **Override for this table** and the table picker from the cache; Column Mapping as the old `ColumnMappingEditor` (**Auto-map by name**, source type, target type, transform, the primary key ticks, relationships, the cached-metadata card; its **Refresh** of one table and the overview's **Refresh** are SYNCAUTHORING-18's). Create mappings writes a mapping file per ticked table as unsaved edits with the columns and the key from the cache and no `targetType` (the target's type is inferred when the target is provisioned), through the client's mapping writer (`mappingColumns.ts`); the inferred target types and natural key, and their use by Create mappings, are SYNCAUTHORING-3's and SYNCAUTHORING-5's. Left for SYNCAUTHORING-5: the source filter, **Custom query, not a table**, the mapping's sub-tabs | SYNCAUTHORING-15 | L | Playwright (against `workspace-fake.ts`): tick two tables and Create mappings makes two mapping files; on one, Auto-map by name maps three columns, untick a key column, override the target side's schema; Files shows the same changes |
| SYNCAUTHORING-18 | **First slice.** Follows SYNCAUTHORING-15 and SYNCAUTHORING-16 (the endpoint cards, the Table Mappings overview and the cached-metadata card exist). The Designer's Refresh buttons, on AUTHORING-24's shared channel picker and Refresh (SYNCAUTHORING-12's placements, built here): **Refresh catalog** on an endpoint card whose connection has no cache, **Refresh** of the connection on the Table Mappings overview, and **Refresh** of one table on a mapping's cached-metadata card, each showing the difference before the cache is replaced; the read goes through the console (AUTHORING-21), never through the Workspace service | SYNCAUTHORING-15, SYNCAUTHORING-16, AUTHORING-24 | S | Playwright (`sync-designer.spec.ts` against `workspace-fake.ts` and a fake of the console's catalog read): on a connection with no cache, Refresh catalog in `dev` fills the database picker; Refresh on the overview reports a table added and, accepted, lists it; Refresh on the cached-metadata card reports "1 column added"; Files shows the new cache as an unsaved edit |
| SYNCAUTHORING-17 | **First slice**, built after WORKSPACEISOLATION-4. Publishing from the Designer through the Builds tab: **Build and publish…** on a replication's page (in the rail, where SYNCAUTHORING-4's Unpublished changes card will hold it) opens the Files area's Builds tab of the same draft by its address (the frame's), where the build and `PublishForm.tsx` work as today (target Runner, channel, staged or served, `bind`); with unsaved edits it says they are built only once saved and committed; no new publish screen. The panel then deploys through the control service as the person (WORKSPACEISOLATION-4) | SYNCAUTHORING-15, WORKSPACEISOLATION-4 | S | Playwright: from a replication's page Build and publish… opens Builds, a build is deployed through the control service staged to a Runner's `dev` with its slot bound, and the browser's Back returns to the replication |
| SYNCAUTHORING-2 | The package `@dbdatakit/sync-ui`, its draft model and the slice's components exist. The rest of the restore into `@dbdatakit/sync-ui` from `96601f27^:Shared/web/sync/src/`, every part listed under "What is left out" removed: the Overview's tracking settings (`TrackingCard.tsx`) and sub-tabs (`components/NotesPanel.tsx`, `MappingPipelineCard.tsx`, `ReconcileConfigCard.tsx`, `ReplicationProvisioningPanel.tsx`, `components/ScriptBindings.tsx`, `SegmentingStrategiesCard.tsx`), the tab Quality Checks (`ReplicationChecksPanel.tsx`, `checkForms.ts`), the rail's Unpublished changes card (`components/UnpublishedChanges.tsx`), the mapping editor's source filter and query source (`SourceFilterCard.tsx`, `QuerySourcePanel.tsx` without its rows) and its sub-tabs (`DefaultSegmentingCard.tsx`, `ProvisioningCard.tsx`, `ChecksCard.tsx`), the helpers `api/readerNotes.ts` and the writer defaults of `api/types.ts`, `components/CodeEditor.tsx`, `ParameterForm.tsx` and `parameterValues.ts`, with their tests; the draft model's scripts view (read only); the client of the ported routes (SYNCAUTHORING-3); `NotesPanel` and `CodeEditor` move to `@dbdatakit/shell` | none | L | vitest: the restored tests pass (`checkForms`, `readerNotes`, `writerDefaults`, `ReplicationChecksPanel`, `TrackingCard` on its settings); the package's test that refuses `@microsoft/signalr` and Runner routes covers the new modules |
| SYNCAUTHORING-8 | Delete the forwarding projects left in `DbDataSync/src` when Sync's libraries moved to `Shared/src/DbDataKit.Sync.*` (`DbDataSync.Core`, `.Drivers.Abstractions`, `.Scripting.Abstractions`, `.Drivers.Generic` and `.Provisioning` hold only `TypeForwards.cs`; `.Drivers.Descriptor` forwards what moved and keeps what loads a library or compiles a `driver.csx`, with `DriverDescriptorReader` as a thin adapter over the shared `DriverDescriptors`): DbDataSync's projects reference `DbDataKit.Sync.*` directly (`<DbDataKitReference>`), the forwarding types and any project left empty are removed, the three engine tests left in `DbDataSync.Drivers.Generic.Tests` reference the shared library, and the tasks and docs that name the old projects are updated in the same pull request | SYNCAUTHORING-3 | S | DbDataSync builds and its unit and integration suites pass with no forwarding type left (a test lists the `TypeForwardedTo` attributes of DbDataSync's assemblies and finds none); `scripts/check_solution.py` passes |
| SYNCAUTHORING-3 | First, a table of each built-in driver's capabilities (its reader, staging and writer kinds, which of them need a key, their options) in a shared project under `Shared/src/DbDataKit.Sync.*`: the built-in drivers (`DbDataSync.Drivers.MsSql`, `.Postgres`, `.MySql`, `.Oracle`, `.DuckDb`, `.ArrowFlightSql`) register from it, a DbDataSync test holds it equal to what each driver registers, and the Workspace answers a connection's capabilities from it without any database client. Then the authoring services the replication's screens call, ported into the Workspace server for a sync draft, in its process on Sync's libraries in `Shared/src/DbDataKit.Sync.*` (a draft's cache is read as Sync's column catalog by `SyncCatalogColumns`; the existing `script-test` is left as it is; WORKSPACEISOLATION-1 lists it), under `/api/v1/projects/{project}/workspaces/{id}/sync/`: the script slots a binding can name; a connection's capabilities (readers, staging providers, writers); a mapping's inferred column types and natural key; bulk create of mappings (the files' texts for the ticked tables, written into the draft as unsaved edits by the client); each route in the console's relay table (`ServerProducts.WorkspaceRoutes`) with its role, and in `workspace-fake.ts`. Not ported: the databases, tables and columns of a connection (the browser reads the draft's `catalog-cache.yaml` through the Workspace's file-reading route, unsaved edits included; `docs/authoring-metadata.md`, "A sync project"); and, since only the screens outside a replication used them, a script's compile and where it is bound, a driver's connection parameters and capabilities by type, a connection's credential source. No route references a `DbDataSync.*` assembly | none | M | xunit in `DbDataWorkspace.Tests`: each route on a sync draft fixture (two connections, a replication, a catalog cache): the slots list, the readers and writers of a SQL Server connection, the inferred key of a table with a primary key, bulk create for two tables; a viewer is refused bulk create; the console's relay test lists the new routes with their roles |
| SYNCAUTHORING-4 | Follows SYNCAUTHORING-15 (the page, its chrome, New replication, the endpoints and the schedule exist). Designer, the rest of a replication: each replication in the navigation with the check's error count; the rail's **Unpublished changes** card around SYNCAUTHORING-17's **Build and publish…**; the Overview's tracking settings and its sub-tabs Notes, Pipeline (the engines from the registry's hook `useSyncEngines`, the readers and writers from SYNCAUTHORING-3), Provisioning (the plan in the old layout; apply as today), Custom Transforms (the slots from SYNCAUTHORING-3), Bulk Load (strategies; **Try** says it waits on AUTHORING-3); the tab Quality Checks as the old `ReplicationChecksPanel` | SYNCAUTHORING-15, SYNCAUTHORING-2, SYNCAUTHORING-3 | L | Playwright: three replications in the navigation, one with an error count; Enabled off marks the rail unpublished; bind a script on Custom Transforms from the slots offered; the Provisioning sub-tab shows a missing target table and its statement; add a freshness check and see it in Files |
| SYNCAUTHORING-5 | Follows SYNCAUTHORING-16 (the sidebar, the overview with Create mappings and Refresh, the editor's two sides and Column Mapping exist). Designer, the rest of a mapping: Create mappings and Column Mapping take the inferred target types and natural key from SYNCAUTHORING-3 (bulk create by its route); the source filter; **Custom query, not a table** (the query text, no rows); the sub-tabs Notes, Custom Transforms, Bulk Load (`DefaultSegmentingCard`), Provisioning (the mapping's plan, its pending steps as the tab's badge), Pipeline (the overrides), Diagnostics (`traceTiming`), Verify (the `verification:` checks as `ChecksCard`) | SYNCAUTHORING-16, SYNCAUTHORING-4, SYNCAUTHORING-3 | L | Playwright: Create mappings on a table with no primary key writes the inferred natural key and target types; add a row-count verification and a default segmenting by month; the Provisioning badge counts the missing table's step; Files shows the same changes |
| SYNCAUTHORING-6 | Documentation and coverage: `docs/ui-coverage.md` names every Designer page and its stories; the `docs/ui-editors.md` rows of `sync-task-config`, `sync-mapping-config`, the checks and the verification checks name the Designer screens and Files (the rows of the connections and `sync-script-config` keep Files and say the Designer does not cover them yet); `docs/authoring-parity.md`'s Sync rows; the Sync part of `DbDataWorkspace/README.md` says what the Designer covers (inside a replication), that connections and scripts are edited in Files, and that monitoring is the Runner's screens; the shots under `DbDataSync/screenshots/golden-path/` that show a replication's authoring are retaken from the stories | SYNCAUTHORING-4, SYNCAUTHORING-5 | S | `scripts/check_ui_coverage.py` and `scripts/check_ui_editors.py` pass with the new rows; the stories run in CI |
| SYNCAUTHORING-9 | The replication set's manual read-only task `preflight` in its own package (`dbdatasync-runtime`), with the run login each replication uses, per mapping, findings as step outputs: on the source, the table and mapped columns exist, the filter's columns exist and it compiles (a zero-row read), the login can read the table (the engine's privilege function, then the zero-row read), change tracking, CDC or a logical slot and publication are on where the reader's strategy needs them (a missing one as a provisioning step of the source side, with its statement: the source side of the provisioning plan, which was a task of AUTHORING before), source types the driver reads, a key where a reader or writer needs one; on the target, the table exists or provisioning can create it (the plan from the live catalog), the login can insert, update and delete and, for provisioning, create and alter (privilege functions, never a trial write), types compatible, keys agreeing with no nullable key column, the `tracking:` schema and tables exist or may be created; nothing is written; started on the version staged in `prod` before activation and as **Re-check** after publish (AUTHORING-22) (`docs/authoring-metadata.md`, "A sync project") | AUTHORING-22 | L | Sync conformance on SQL Server and PostgreSQL: change tracking off on `dbo.orders` is a finding with the statement that enables it, and on it is satisfied; a login without insert on the target is a finding; a filter naming a missing column is a finding; the target's row count and every table are unchanged after the run |
| SYNCAUTHORING-10 | The rest of the offline checks in the sync draft's check (`SyncOfflineCheck` in `DbDataKit.Authoring.Server`): the key check of the readers and writers that are an engine's own (SQL Server's change tracking, CDC and `MsSqlMerge`, PostgreSQL's `PgLogicalSlot` reader), once SYNCAUTHORING-P19 says where their capabilities come from. Until then each is the note "whether it needs a key is not checked offline". The generic readers and writers, and the types and filters of YAML drivers and of Flight SQL, are built (`docs/authoring-metadata.md`, "A sync project") | none | S | xunit on a sync draft fixture with a cache: an `MsSqlChangeTracking` reader on a source table with no key in the cache is a finding on the mapping's file and line; a `PgLogicalSlot` reader on a keyed table is none |
| SYNCAUTHORING-11 | The set's own `discover` and `describe` tasks, for a connection whose driver is Sync's own (a YAML driver, or a `metadataProvider` script), answering the same shape as the built-in package `dbdatakit-metadata` (`docs/authoring-metadata.md`) through Sync's metadata service, started through the console's catalog read as the built-in package's are (AUTHORING-21); the Designer offers **Build and stage for dev** when no build is staged in the channel, which the control service deploys (WORKSPACEISOLATION-4) | AUTHORING-21, WORKSPACEISOLATION-4 | M | Sync conformance: `discover` of a YAML-driver SQLite connection returns its tables and columns; `describe` of a query gives its columns and no row |
| SYNCAUTHORING-12 | Follows SYNCAUTHORING-18 (Refresh catalog on an endpoint card, Refresh on the Table Mappings overview and on a mapping's cached-metadata card exist). The Designer's other live pieces of a replication, on AUTHORING-6's shared ones: **Set up for development** where the channel does not bind the slot; **Describe columns** of a query source written into the mapping's column cache, read through the console; **Test** on each endpoint card (the binding test in the chosen channel, sent by the control service, no sample rows); the newest `preflight` run of the chosen channel on the Provisioning sub-tabs (time, run link, findings per mapping), read through the control service (WORKSPACEISOLATION-5) | SYNCAUTHORING-18, SYNCAUTHORING-4, SYNCAUTHORING-5, AUTHORING-6, WORKSPACEISOLATION-5 | M | Playwright against `workspace-fake.ts`: Describe columns fills the column cache; Test shows its steps and no row; the Provisioning sub-tab shows the newest preflight with its link; a channel that does not bind the slot offers Set up for development |
| SYNCAUTHORING-19 | Sync uses the shared types: Sync's own type list (`CanonicalType` and `CanonicalTypeKind` in `DbDataKit.Sync.Core`) is replaced by the shared `DbDataKit.Dialect.CanonicalType`, **adapter first**, and every rule that exists only in Sync's conversion (`SharedTypes`) moves into the shared dialects so every product answers the same: an unsigned integer widened to the next signed type where the target has no unsigned type (today only DuckDB's reading does it), and a time or timestamp with no stated precision meaning microseconds; first the list of every case Sync needs that the shared types lack, each added to the shared types with a test, including whether SQL Server's `tinyint` (0 to 255) is read as a signed 8-bit integer; then Sync's dialects, drivers and `driver.yaml` type maps read and write the shared type, and `SharedTypes` is removed | none | M | the shared dialect tests cover each case on the list; DbDataSync's unit suite (`Category!=Integration`) and the moved `DbDataKit.Sync.*` tests pass with no test changed except where a case on the list changes an answer (each named in the commit); no `DbDataSync`/`DbDataKit.Sync` type named `CanonicalType` remains |

## Needs from other subjects

- AUTHORING: the other project kinds (build, store, integration, app) adopt the two areas (`registerDesigner`, `DbDataWorkspace/README.md`) as their Designers are built: each
  kind's designed screens register as its Designer (its routes and navigation entries) rather than as tabs of the Files area, and a task that builds one
  says so.
- AUTHORING-3: a segmenting strategy's **Try** (SYNCAUTHORING-4); until then it says it waits on a Runner task.
- AUTHORING-21 and AUTHORING-24, for the first slice (the built-in package `dbdatakit-metadata` with `discover` and `describe` is built): the console's catalog read from a
  Runner channel, and the shared channel picker and Refresh, before SYNCAUTHORING-18 (Refresh catalog and Refresh).
- WORKSPACEISOLATION-4: the Builds tab's publish panel calls the control service's deploy; SYNCAUTHORING-17's **Build and publish…** waits for it and then opens
  that panel, and SYNCAUTHORING-11's **Build and stage for dev** deploys through it.
- WORKSPACEISOLATION-5: runs, run results and binding tests through the control service, for SYNCAUTHORING-12 (Test, the newest `preflight`) and the
  Re-check of SYNCAUTHORING-9's `preflight` (AUTHORING-22).
- WORKSPACEISOLATION-1: the sync draft's `script-test` runs the author's code in a child process of the Workspace service; SYNCAUTHORING-3 leaves
  it as it is (the shared Sync libraries compile and run no script), and the list says where it goes.
- COMMANDLINES (needs review): if it is accepted, COMMANDLINES-3 moves Sync's engine of check and package from `dbdatasync` into `Shared/src`; Sync's
  configuration model, slot list, driver registry and column reader are there already (`Shared/src/DbDataKit.Sync.*`), and COMMANDLINES-3 builds on
  those projects. COMMANDLINES-6 removes the program `dbdatasync`, which changes what SYNCAUTHORING-8's adapters forward to, and
  COMMANDLINES-4 changes the form of the runtime package that holds SYNCAUTHORING-9's `preflight` and SYNCAUTHORING-11's tasks.
- AUTHORING-17, AUTHORING-18, AUTHORING-19 and AUTHORING-6, after the slice: `facts`, the connection test before publish and Set up for development
  (`docs/authoring-metadata.md`), which SYNCAUTHORING-12 places in the Designer.
- AUTHORING-9: its stories of the Sync forms stay the stories of the Files area; this subject's stories cover the Designer (no story is written twice).
- AUTHORING-12: reconcile deletes and resync stay paused; they come back with the monitoring pass, not in the Designer's authoring screens.

## Proposed decisions

### SYNCAUTHORING-P14: words inside a function call in a source filter (default, awaiting the owner)

The offline check compares each name a source filter uses as a column with the cached table. A lexer cannot tell a column from an argument a function
names, so a bare word inside a function's parentheses that is no column of the table is not reported; a quoted name there is. Scenario: the filter
`DATEADD(day, -7, GETDATE()) < created AND UPPER(stauts) = 'OPEN'` reports nothing about `day` (right) and nothing about the misspelt `stauts` (missed),
which `preflight`'s zero-row read finds. Option A, as built. Option B, report such a word as a note ("not a column of the table; checked when the set
runs") rather than a warning. Option C, a per-dialect list of the words functions take (date parts, `AS` types), and report any other word. Recommendation:
A now, C if authors ask for it.

### SYNCAUTHORING-P15: the shared Sync libraries keep the namespaces `DbDataSync.*` (default, awaiting the owner)

The assemblies are `DbDataKit.Sync.Core`, `DbDataKit.Sync.Drivers.Abstractions` and so on, and their types keep the names they had:
`DbDataSync.Core.Config.TableMappingConfig`, `DbDataSync.Drivers.Abstractions.IDriver`. A type forward needs the same full name, and DbDataSync's tests
compile unchanged because of it. The names are also what an author writes: a transform script in `config/scripts/` starts
`using DbDataSync.Scripting.Abstractions;`, a `driver.csx` implements `DbDataSync.Drivers.Abstractions.IDriver`, and the script compiler imports
`DbDataSync.Core.Config` for every script. Scenario: Ana's set has twelve scripts and one `driver.csx`. Under option A she changes nothing. Under
option B each file's `using` lines change when SYNCAUTHORING-8 lands, or a script fails to compile with "The type or namespace name 'DbDataSync' could
not be found". Option A, keep `DbDataSync.*` as the public names of Sync's script and driver contracts (built). Option B, rename them to
`DbDataKit.Sync.*` in SYNCAUTHORING-8, with a check that names the old `using` in every script of a set. Recommendation: A, since the names are part
of what an author's script compiles against.

### SYNCAUTHORING-P16: what moved beyond the four named libraries (default, awaiting the owner)

The column reader (`MappingColumnReader`) is in Sync's provisioning library with the provisioner that plans and applies DDL (`MappingProvisioner`), and
the driver descriptors build on the generic drivers (`DbDataSync.Drivers.Generic`: the readers, staging providers and writers every SQL driver shares,
and the rules for a target table and a natural key). Both moved whole, so the Workspace's process now has code that can open a database and run DDL.
Nothing in the Workspace calls it: `SyncCatalogColumns` answers the column reader from the draft's `catalog-cache.yaml`, and the Workspace has no
connection factory. Scenario: a later route calls `MappingProvisioner.GetPlansAsync` for a draft; it needs an `IConnectionFactory`, which the Workspace
does not have, so the call cannot be made without adding one, which WORKSPACEISOLATION would refuse in review. Option A, as built. Option B, split
the column reader, the natural-key rule and the target-table planner into their own shared project, and leave the provisioner and the readers and
writers in DbDataSync. Recommendation: A; B moves code the engine keeps calling as one unit, for a guarantee a review already gives.

### SYNCAUTHORING-P17: the generic drivers' engine tests stay in DbDataSync (default, awaiting the owner)

`DbDataSync.Drivers.Generic.Tests` keeps `GenericDriverTests`, `GenericDriverTestDatabase` and `QueryCatalogTests` (category Integration), unchanged,
running through the forwarding assembly; the other 26 files moved to `Shared/tests/DbDataKit.Sync.Drivers.Generic.Tests`. Those three connect to
Sync's PostgreSQL (`DBDATAKIT_TEST_POSTGRES_LOGICAL`, the login `dbdatasync`) and do not skip when it is missing, while the shared libraries' CI
runs without engines unless it is asked for them, and then sets only `DBDATAKIT_TEST_POSTGRES`. Scenario: moved to `Shared/tests`, the shared CI
run fails on a pull request with "Connection refused" on port 15432.
Option A, as built: they stay with Sync's integration job, and SYNCAUTHORING-8 points them at the shared project. Option B, move them and skip each
when its engine's variable is not set (`Assert.Skip`), and start the engines they need in the shared CI. Recommendation: A.

### SYNCAUTHORING-P18: where the tests of `DbDataKit.Authoring.Server` live (default, awaiting the owner)

The in-process test of this move (`SyncInProcessTests`: a mapping's natural key derived from the cached source key, through Sync's shared libraries,
and no `DbDataSync.*` assembly loaded) is in a new project, `Shared/tests/DbDataKit.Authoring.Server.Tests`, as the repository's rule puts a shared
library's tests. The Workspace's other tests of the same library (the sync draft's check, scripts, provisioning plan) are in
`DbDataWorkspace/tests/DbDataWorkspace.Tests` and start the Workspace's host. Scenario: SYNCAUTHORING-3 adds the route of the inferred key; its test
of the route goes beside the host's tests, and a test of the answer with no host can go to either. Option A, as built: tests that need no host in
`DbDataKit.Authoring.Server.Tests`, tests of routes in `DbDataWorkspace.Tests`. Option B, everything in `DbDataWorkspace.Tests` and no new project.
Recommendation: A, since a test with no host runs in a second rather than starting a server.

### SYNCAUTHORING-P19: the capabilities of the built-in drivers for SYNCAUTHORING-3 (default, awaiting the owner)

A connection's readers, staging providers and writers are what its driver registers. The generic kinds (`Watermark`, `BatchReload`, `ChangeApply`, ...)
are in the shared `DbDataKit.Sync.Drivers.Generic`, but the built-in drivers that add their own (SQL Server's change tracking and CDC readers and
`SqlBulkCopy` staging, PostgreSQL's logical reader) are `DbDataSync.Drivers.MsSql`, `.Postgres`, `.MySql`, `.Oracle`, `.DuckDb` and `.ArrowFlightSql`,
which did not move: each brings its database client (Microsoft.Data.SqlClient, Npgsql, MySqlConnector, Oracle's client, DuckDB). Scenario: the
Pipeline sub-tab of a replication from SQL Server asks the Workspace for the source's readers; with only the moved code it can list the generic kinds,
not `MsSqlChangeTracking`. The same gap is SYNCAUTHORING-10's key check of the engine-specific readers and writers (the generic ones are checked). Option A, move the built-in driver projects
too (the Workspace then carries every database client). Option B, a table of each built-in driver's capabilities (its kinds, which need a key, its
options) in a shared project, which the drivers register from and which a DbDataSync test holds equal to what each driver registers. Option C,
SYNCAUTHORING-3 answers only the generic kinds and descriptor drivers, and the screens offer the engine-specific kinds from a list of their own.
Recommendation: B, built at the start of SYNCAUTHORING-3.

### SYNCAUTHORING-P20: how the filter of a YAML driver's connection is read offline (default, awaiting the owner)

A `driver.yaml` names its identifier quoting (`quoteIdentifier: bracket`, `backtick` or `doubleQuote`) and nothing else of its engine's grammar. The
offline check reads such a connection's source filter with the ANSI lexer and reserved words, with the descriptor's quoting: brackets as well as
double quotes for `bracket`; backticks, and double quotes as strings, for `backtick`. Scenario: Ana's `acme-odbc` connection (a `driver.yaml` with
`quoteIdentifier: bracket`) filters `[region code] > 3 AND regoin_code > 3`; the check reports `regoin_code` and, if the cached table has no
`region code`, that name too, as errors on the filter's line. A filter in a grammar ANSI does not have (a `#` comment, a `$$` string) can be misread:
a word in it may be reported as a missing column. Option A, as built. Option B, read a YAML driver's filter as not checked offline, so no error comes
from a guess at its grammar. Option C, a `lexer:` key in `driver.yaml` naming the shared dialect whose lexer reads it (`sqlserver`, `mysql`, ...), ANSI
when absent. Recommendation: A now, C when a driver's filters are misread.

### SYNCAUTHORING-P21: the key check of a target that is not in the cache yet (default, awaiting the owner)

A writer that matches rows on the target's primary key (`ChangeApply`, `KeyReconcileDelete`) is checked against the cached target table. When the
target is not in the cache (provisioning creates it), the check builds the table provisioning would create, with Sync's `ProvisioningColumnBuilder`:
the mapped source columns under their target names, keyed by the source's key or the mapping's `primaryKey`. Scenario: Ana maps `crm.dbo.audit_log`,
which has no key, into `dw.public.audit_log`, which does not exist yet, under a `Watermark` reader (so the writer is `ChangeApply`). The check warns:
"The target dw.public.audit_log is not in catalog-cache.yaml, so it is checked as provisioning would create it, with the source's key. The writer
'ChangeApply' would refuse this mapping: ... has no primary key". Option A, as built. Option B, check only a target the cache holds, and leave a
missing one to the set's `preflight`. Recommendation: A, since the first pass after provisioning would fail on it.

### SYNCAUTHORING-P22: rules of Sync's engine the offline check copies (default, awaiting the owner)

Three rules have no public method in the shared Sync libraries, so `SyncOfflineCheck` repeats them: the Flight SQL driver's reading of a type (ANSI
first, then the dialect of the engine behind it: `FlightSqlDialect`, in `DbDataSync.Drivers.ArrowFlightSql`, which brings the Flight client),
`KeyReconcileDeleteWriter`'s refusal of a target with no cached key, and `Scd2Writer`'s refusal of a pass with no natural key (its check is
`internal`). The words of the two refusals are copied. Scenario: a later change makes `FlightSqlDialect` read a Flight SQL column's type in another
order; the engine changes and the offline check does not, until someone sees the two disagree. Option A, as built, with a comment at each copy naming
the class it copies. Option B, move each rule into a public method of the shared libraries (a `FlightSqlTypes` class in `DbDataKit.Sync.Core`,
`Scd2Writer.RequireNaturalKey`, `KeyReconcileDeleteWriter.RequireKey`) that both the engine and the check call. Recommendation: B, as a small task of
this subject or with SYNCAUTHORING-8.

### SYNCAUTHORING-P25: fixed-length binary (awaiting the owner)

Sync keeps SQL Server's and MySQL's `binary(n)`, and SQL Server's `rowversion`, as fixed-length: a SQL Server `binary(16)` key becomes `binary(16)` on a
SQL Server or MySQL target, and a `rowversion` becomes `binary(8)`. The shared type has no fixed-length flag: it reads `binary(16)` as variable-length
with the note "fixed-length binary becomes variable-length", and renders `varbinary(16)`. Scenario: Ana replicates `crm.dbo.device`, whose key is
`device_hash binary(16)`, into SQL Server. Today the target column is `binary(16)`; on the shared tables as they are, a new target gets `varbinary(16)`
(her existing target keeps `binary(16)`: the plan reads it back as the type it would write). Option A, the shared type gains a fixed-length flag (canonical
text `binary(fixed,16)`): SQL Server and MySQL read and write `binary(n)` exactly, `rowversion` reads as `binary(fixed,8)` with its note, and every other
engine writes its one binary type as now. Transfer then creates `binary(16)` where it now creates `varbinary(16)`, the catalog screens show the flag,
and the Gateway's Arrow map can read `FixedSizeBinary` exactly. Option B, no flag: Sync's new targets get `varbinary(n)` and a lossy note. Recommendation: A.

Answer from the owner, 2026-11-23: A add fixed length flag.

### SYNCAUTHORING-P26: a time column on Oracle (awaiting the owner)

Oracle has no time-of-day type. Sync writes a time as `timestamp(6)`, with the note that its date part means nothing; the shared `oracle` table writes
`interval day(0) to second(6)`, which reads back as an interval rather than a time. Scenario: Ana replicates `shop.dbo.opening_hours` (`opens time(0)`)
into Oracle. Today the target has `opens TIMESTAMP(0)`. On the shared table it would get `INTERVAL DAY(0) TO SECOND(0)`, and her existing target
would show a type change from `TIMESTAMP` to `INTERVAL` in its next provisioning plan, which Oracle cannot apply to a column with rows. Option A, the
shared `oracle` table writes a time as `timestamp(s)` with Sync's note; Transfer and the Store then write `timestamp` where they now write an interval.
Option B, Sync takes the interval for new targets; the shared table also reads `interval day(0) to second(s)` back as a time, and Sync's existing
targets keep `TIMESTAMP` (their plans would propose the change; Ana declines it). Recommendation: A, since Sync's targets already hold timestamps
and a timestamp is how Oracle tools show a time of day.

### SYNCAUTHORING-P27: ANSI type names, for Flight SQL columns and a generic Flight SQL target (awaiting the owner)

The Flight SQL driver names each Arrow column's type in ANSI words (`tinyint`, `varbinary`, `time`, `varchar`), and Sync reads those names, and writes
the tables of a generic "Arrow Flight SQL" target, with its own ANSI table. The shared `ansi` table reads `tinyint`, `varbinary`, `float`, `double`,
`bool`, `text` and `char` as unmappable and `time` as `time(0)`; it writes an unbounded string as `clob` (Sync: `VARCHAR(4000)` with a note), a
binary as `blob` (Sync: `VARBINARY(4000)`), and refuses an 8-bit integer, an unsigned one, a guid, JSON and XML (Sync: `SMALLINT`, `CHAR(36)`,
`VARCHAR(4000)`, each with a note). The shared `ansi` table is also the Gateway's Flight SQL dialect, Build's `ansi` connections, the base of every
ODBC dialect and Transfer's fallback. Scenario: Ana's source is a Flight SQL server with a `flags` column of Arrow type int8 and a `payload` binary
column. Today Sync reads them as `tinyint` and `varbinary` and replicates both; on the shared `ansi` table as it is, both are unmappable and the
mapping is refused. Option A, add Sync's reads and writes to the shared `ansi` table (the Gateway, Build, ODBC and Transfer then read those names
and write those types too). Option B, a new shared dialect `flightsql` that extends `ansi` with the names the Flight SQL driver gives Arrow types and
Sync's writes for a generic target, used by Sync's Flight SQL driver and the offline check; no other product changes. Option C, Sync takes the
shared `ansi` table as it is. Recommendation: B. The same answer decides how ANSI writes a time with no stated precision (`time(0)` in the
shared `ansi` table today, Sync's `TIME`), since every other engine writes one with microseconds.

## Open designs

### Monitoring and operations, second pass

What the old screens did to watch and operate a running replication, removed from this pass, and what they would read now that the Runner runs the set
and the console relays to it. The second pass designs where they live (the Designer reading the Runner through the console's relay as the signed-in
person, or the console's Runner screens with a link from the Designer) before it becomes tasks.

- **Run controls in the chrome**: Run Now, Pause and Resume (with a note asked in a dialog), Bulk Load…, Reconcile deletes…. Now: the Runner's task
  actions (`POST .../tasks/{...}/run|pause|resume`), the Workspace's run routes with its publisher key (`runServed`, `runStaged`), which
  WORKSPACEISOLATION-5 moves to the control service, as the person; reconcile and resync paused by the owner. Open: how **Enabled** in the draft and a
  pause on the Runner are told apart on one screen (the draft's setting takes effect when published, a pause at once).
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
  reachable column and **Test all**. Needs: the draft's connection files; the engine list of **New connection** from the engine registry (`useSyncEngines`);
  the reachable column and Test all wait on the connection test (SYNCAUTHORING-12) and the monitoring pass.
- **A connection's page** (`ConnectionEditPage.tsx`, `connection-edit/RunLoginCard.tsx`, `connection-edit/ConnectionTestCard.tsx`): engine, address,
  database, timeouts, the driver's own parameters with their check, the login by secret reference and the run login, custom transforms and hooks, the
  test. Needs: the driver's connection parameters and the parameter check (`connection-parameters`, `Services/ParameterCheck.cs`), a driver's
  capabilities by type (`/api/drivers/{type}/capabilities`), a connection's credential source (`credential-source`), ported as the services above are
  (on the shared libraries in `Shared/src/DbDataKit.Sync.*`); the secret findings of the draft's check; a link to the project's secrets overview (the AUTHORING subject's); the test as the binding test
  in a channel (`docs/authoring-metadata.md`, "Testing a connection"), without the old test's sample rows.
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

- `Shared/web/workspace` (the Files/Designer frame, routes), `Shared/web/sync-ui` (the new npm workspace `@dbdatakit/sync-ui`),
  `Shared/web/package.json` (its workspaces), `Shared/web/shell` (generic components moved in).
- `Shared/src/DbDataKit.Authoring.Server` (the sync routes), `Shared/src/DbDataKit.Sync.*` and `DbDataSync/src` (the moved libraries and their adapters),
  `DbDataKit/src/DbDataKit.Server/ServerProducts.cs` (the relay table), the console's catalog read (AUTHORING-21).
- `DbDataKit/tests/DbDataKit.Server.Web.Tests` (`sync-designer.spec.ts`, `workspace-fake.ts`), `DbDataWorkspace/tests/DbDataWorkspace.Tests`,
  `DbDataSync/screenshots/`.
- `DbDataWorkspace/README.md`, `docs/ui-coverage.md`, `docs/ui-editors.md`, `docs/authoring-parity.md`.
