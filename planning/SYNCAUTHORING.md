# SYNCAUTHORING: the Sync authoring look and feel, restored in the Workspace's sync projects

## Goal

A person who authors a replication set in a Workspace sync project works in the screens the Sync server had: a replication's page with its tabs
(Overview, Table Mappings, Monitoring, Quality Checks, Runner, Version Control), the run controls in the page's chrome, the status and figures in a
rail beside every tab, the mapping editor with its source and target side by side, the connections and scripts lists. The screens read and write the
sync project's files and the Runner that runs the published set; nothing comes from a Sync server, which no longer exists.

The file-format-driven UI stays as the fallback: every file of a sync project opens in three views of the same text, **Designed** (the screen of this
subject), **Form** (the form generated from the file's schema, as today) and **YAML** (the code editor, as today). The check of the draft runs under all
three. Owner, 2026-11-18: "We need to restore the overall look and feel of the old Sync authoring UI. The new workspace file-format-driven UI needs to
remain as a fallback (every file should be editable as YAML or generated UI)."

## What exists today

The Sync server's web screens (`Shared/web/sync`, the npm workspace `@dbdatakit/sync`) were deleted with the server; git keeps them at
`96601f27^:Shared/web/sync/` (about 17,000 lines: 21 pages and panels, 38 cards and components, the client of the Sync API). The reference look is in
the tree: `DbDataSync/screenshots/` (`golden-path/` 01 to 52, `lag-monitoring/`, `monitoring-restructure/`, `replication-wide-provisioning/`, captured
against a real Sync server by its old Playwright suite). The CSS those screens used is the shell's (`Shared/web/shell/src/styles.css`, unchanged since:
`.detail-body`, `.detail-main`, `.detail-rail`, `.card`, `.card-head`, `.toggle`, `.btn-chrome`, `.subtabbar`, `.grid-row`), so the look needs no new
stylesheet. The old components import only `@dbdatakit/shell`, `@dbdatakit/admin` (its driver and library pages are gone) and the Sync API hooks.

**What made the look and feel.**

- A frame per product: the icon rail (Replications, Connections, Scripts, Runner, Drivers, Docs, Admin), breadcrumbs as the page's heading
  (`DbDataSync / Replications / erp-to-dw`), and a tab bar under them whose right end holds the page's actions.
- A replication's page is a layout route: its tabs are routes, so a reload or a link lands on the same tab and mapping; the draft of the replication
  lives in the layout, so switching tabs never loses an edit. The chrome holds **Enabled** (a switch, saved), **Pause/Resume** (a media button, with a
  note asked in a dialog), **Bulk Load…**, **Reconcile deletes…**, **Run Now**, **Save settings**, **Delete**; Run Now and Bulk Load move to Run History
  so the result is seen where it lands.
- A right rail beside every tab: **Unpublished changes** (the set changed after the version the Runner serves, with a link to publish), **Replication
  status** (running or idle, said in words: an idle worker is not a broken one), **Last 24h** (passes, failures, rows read and written, p50/p95/max,
  a bar per hour, a window switch 1h/24h/7d and a refresh countdown).
- Overview: the source and target endpoint cards side by side, coloured top borders and an arrow between them, "inherited by all 3 mappings unless
  overridden"; the schedule as one sentence of inputs ("Continuous every 60 s, idle timeout 300 s, up to 4 mappings and 2 bulk loads at once"); the
  tracking card; sub-tabs Notes (rendered Markdown, Edit), Pipeline (Reader → Staging → Writer as three boxes, settings per box), Provisioning (the
  plan of every mapping grouped by connection, apply), Custom Transforms (script and hook bindings), Bulk Load (segmenting strategies, Try).
- Table Mappings: a sidebar of the mappings with their column counts and **+**; an Overview listing every source table with how many mappings each
  has, ticks and **Create mappings**; the mapping editor with the two sides (each "inherited" or "Override for this table"), the source filter, then
  sub-tabs Notes, Column Mapping (source column picker per target column with the source type, target type, transform, **Auto-map by name**, the
  primary key ticks, relationships, the cached metadata), Custom Transforms, Bulk Load (default segmenting), Provisioning (a badge with the pending
  steps), Pipeline (the mapping's overrides), Diagnostics (trace timing), Preview SQL (every statement a pass would run, in order) and Verify (checks
  and their results, a result on its own page).
- Monitoring: Current Status (the reader lag across mappings, then a row per mapping: source, target, lag with exact or estimated, intent and hold,
  **Manage…** for the read position), Run History (filters, paging, a run's dialog with its timing), Pause History, Bulk Load History.
- Lists as tables with a heading, a count and the main action at the right (Replications: name, reader, schedule, max lag, status; Connections:
  driver, host, database, auth, reachable, **Test all**; Scripts: kind, language, used by, enabled).

**The old screens and what the Workspace has for each today.** "Generated" is the form made from the file's schema (`StructuredPanel.tsx`,
`syncSchemas.ts`, with Form and YAML modes); "Runner" is a Runner of the console reached by a link (`runnerScreens` in `ProjectRunsPanel.tsx`) or by the
Workspace server's run routes (`runServed`, `runStaged`, `bindingTest`, `stagedRun`, which the Workspace sends with its publisher key).

| Old screen (under `96601f27^:Shared/web/sync/src/`) | Old data (Sync API) | The Workspace today | Where the data is now |
| --- | --- | --- | --- |
| Replications list (`pages/ReplicationsPage.tsx`): explorer, table with reader, schedule, max lag, status; New Replication | `GET /api/replications`, `.../status`, `.../lag` | the card **Replications** of `SyncSetPanel.tsx` (names, mapping files, error counts, **New replication**); no reader, schedule, lag or status | the files `config/replications/*/task.yaml`; the status from the Runner's task (`GET /api/v1/tasks/{package}/{channel}/{task}`) |
| Replication chrome (`pages/ReplicationDetailPage.tsx`): Enabled, Pause, Bulk Load, Reconcile deletes, Run Now, Save settings, Delete | `PUT .../enabled`, `PUT .../paused`, `POST .../runs`, `POST .../mappings/{m}/bulk-load`, `.../reconcile-deletes`, `PUT /api/replications/{n}` | `enabled` is a field of the generated form; Run now and a bulk load on the Runs tab (`ProjectRunsPanel.tsx`); pause, resume and cancel by link to the Runner; reconcile paused (AUTHORING-12); the file's Save and Delete | `enabled` in `task.yaml`; pause and resume `POST .../tasks/{...}/pause|resume` of the Runner; runs through the Workspace's run routes |
| Rail: Unpublished changes, Replication status, Last 24h (`components/UnpublishedChanges.tsx`, `replication-detail/StatusCard.tsx`, `MetricsCard.tsx`) | `GET /api/runner/package/unpublished`, `.../status`, `.../metrics` | the Runs tab says which version is published; nothing else | the draft's commits against the commit of the build last published (`BuildDetail.publishes`); the Runner's task and its runs (`GET /api/v1/runs`) |
| Overview: endpoints, schedule, tracking (`OverviewPanel.tsx`, `EndpointsCard.tsx`, `ScheduleCard.tsx`, `TrackingCard.tsx`) | the replication's config, `GET .../tracking` | generated form of `task.yaml` (`endpoints`, `scheduling`, `tracking`) | `task.yaml`; the tracking state of the target is TASKENGINE-6 |
| Overview sub-tabs: Notes, Pipeline, Provisioning, Custom Transforms, Bulk Load (`OverviewPanel.tsx`, `MappingPipelineCard.tsx`, `ReplicationProvisioningPanel.tsx`, `SegmentingStrategiesCard.tsx`, `components/ScriptBindings.tsx`) | config; `GET|POST .../provisioning`, `.../segmenting/preview` | generated form (`notes`, `changeProcessing`, `scripts`, `hooks`, `segmentingStrategies`); the **Provisioning** tab (`SyncProvisioningPanel.tsx`: target side from the catalog cache, apply as the Runner's `provision` task) | `task.yaml`; the plan from `dbdatasync provisioning plan --from-cache` (source side: AUTHORING-4); a strategy's Try is AUTHORING-3 |
| Table Mappings sidebar and Overview (`TableMappingsPanel.tsx`, `MappingsOverview.tsx`): tables of the source, counts, Create mappings in bulk | `GET .../table-mappings`, metadata of the connection, `POST .../table-mappings/bulk` | the mapping files under the replication in `SyncSetPanel.tsx`; one mapping per **New replication** | the mapping files; the source's tables from `catalog-cache.yaml` (`CatalogPanel.tsx`, refreshed through a paired Gateway, AUTHORING-6) |
| Mapping editor sides and filter (`MappingSide.tsx`, `components/EndpointSidePair.tsx`, `components/InheritableToggle.tsx`, `SourceFilterCard.tsx`, `QuerySourcePanel.tsx`) | config; metadata; `POST /api/connections/{c}/query-preview` | generated form of the mapping (`sources`, `targets`, `filter`, `query`) | the mapping file; tables and columns from the catalog cache; a query's rows are AUTHORING-3 (AUTHORING-P1) |
| Column Mapping (`ColumnMappingEditor.tsx`, `suggestMappings.ts`, `PrimaryKeyCard.tsx`, `naturalKey.ts`, `RelationshipsCard.tsx`, `CachedMetadataCard.tsx`) | config; metadata; `.../refresh-metadata`, `.../provisioning/inferred-*` | `MappingColumns.tsx` (a row per target column from the catalog cache, same-name suggestion, key, **Write into the mapping**); relationships in the generated form | the mapping file (`columnMappings`, `primaryKey`, `relationships`, the column cache) and the catalog cache |
| Mapping sub-tabs Custom Transforms, Bulk Load, Provisioning, Pipeline, Diagnostics (`TableMappingForm.tsx`, `DefaultSegmentingCard.tsx`, `ProvisioningCard.tsx`) | config; `.../provisioning` | generated form (`scripts`, `hooks`, `defaultSegmenting`, `readerOverride`, `cacheOverride`, `writerOverride`, `traceTiming`); the Provisioning tab per replication | the mapping file; the offline plan |
| Preview SQL (`MappingPreview.tsx`): every statement a pass would run | `GET .../table-mappings/{m}/preview` (the drivers' statement builders) | nothing | not answered by anything today (an open design below) |
| Verify (`VerificationPanel.tsx`, `VerificationResultPage.tsx`, `ChecksCard.tsx`) | `.../verify`, `.../verification-results` | the `verification:` list in the generated form; a verification is a request of the drain, its result a step of the run on the Runner | the mapping file; the Runner's run steps |
| Monitoring: Current Status (`MonitoringPanel.tsx`, `lag.ts`, `holdState.ts`, `readIntent.ts`, `components/MappingReadStateDialog.tsx`, `MappingPositionHistory.tsx`) | `GET .../lag`, `.../read-state`, `.../position-history`, `.../position-rewind` | nothing in the Workspace; the Runner's task screen shows its checkpoints (`CheckpointsCard.tsx`) | the drain's checkpoint `watermark` on the Runner (one item per mapping and source table: position, time, intent, hold; `RunnerPositions.cs`), `GET .../checkpoints`, its history and rewind; lag is SYNCAUTHORING-P4 |
| Monitoring: Run History, Pause History, Bulk Load History (`RunsPanel.tsx`, `components/RunDetailsDialog.tsx`, `RunFigures.tsx`, `PauseHistoryPanel.tsx`, `BulkLoadHistoryPanel.tsx`, `BulkLoadProgressCard.tsx`) | `GET .../runs`, `/api/runs/{id}`, `.../logs`, `.../pause-history`, `.../bulk-loads` | the Runs tab shows the run it started with each pass; the history, logs and cancel are the Runner's `RunsPage.tsx`, linked | the Runner's runs of the task (`GET /api/v1/runs`, `.../steps`, `.../logs`, `.../units`), its decisions (`.../decisions`) |
| Quality Checks (`ReplicationChecksPanel.tsx`, `checkForms.ts`) | `GET|PUT .../checks` | `checks:` in the generated form (shared `check/1` schema) | `task.yaml`; results are DATAQUALITY-1 and the console's Quality page |
| Runner tab and Runner page (`ReplicationRunnerPanel.tsx`, `pages/RunnerPage.tsx`, `RunnerLinkCard.tsx`, `runnerPublish.ts`) | `GET .../runner`, `/api/runner/package`, publish, bindings | the **Builds** tab and `PublishForm.tsx` (build, stage or serve on a Runner's channel); slots and bindings are the Runner's screens; who runs it (`old`, `observe`, `runner`) is not planned (owner, 2026-11-14) | the Workspace's builds and publish routes; the Runner's definitions and bindings |
| Version Control (`HistoryPanel.tsx`): the replication's commits, diff, restore | `GET .../history`, `.../diff`, `.../restore` | the **History** and **Changes** tabs of the workspace, **Earlier texts** of a file (`FileHistory.tsx`) | the project's git, through the Workspace API |
| Connections list and edit (`ConnectionsPage.tsx`, `ConnectionEditPage.tsx`, `connection-edit/ConnectionTestCard.tsx`, `RunLoginCard.tsx`) | `/api/connections`, `.../test`, `.../capabilities`, `.../credential-source` | the card **Connections** of `SyncSetPanel.tsx`; the generated form of `config/connections/<name>.yaml`; **Test** on the Runs tab after publish (the Runner's binding test) | the connection files (by secret reference, never a password); the Runner's binding test |
| Scripts list and edit (`ScriptsPage.tsx`, `ScriptEditPage.tsx`, `script-edit/ScriptTestPanel.tsx`) | `/api/scripts`, `.../compile`, `.../test` | the **Scripts** tab (`SyncScriptsPanel.tsx`: list, **New script** by slot, **Test** against generated input); the files in the code editor | `config/scripts/`; `dbdatasync script list|test --json` on a copy (Workspace routes `scripts`, `script-test`) |
| Drivers, Libraries, Files, Docs, Admin (`@dbdatakit/admin` pages) | the Sync server's admin API | nothing (drivers and libraries are `dbdatasync driver` and `library`; the platform is AUTHORING-2) | not a Workspace concern; Docs are the console's |

Rendered side by side on 2026-11-18 (old screens from `96601f27^` with a mocked Sync API, today's Workspace with a mocked Workspace API, 1440x900):
the old screens look as the screenshots in `DbDataSync/screenshots/` do; today's sync workspace is the generic workspace page (a file tree and the
Replications and Connections cards on the left, the selected file's editor on the right, eight tabs under it), the generated forms list every key of
the file one under another, and the Replications card's rows overlap (each row is a `.grid-row`, a grid of fixed columns, given a replication, its
mapping files and an error count as free children).

## Tasks

The views of a file: every task that gives a file a designed screen keeps its **Form** and **YAML** views working on the same text (SYNCAUTHORING-1),
and its browser story switches to each and back without losing an edit.

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| SYNCAUTHORING-1 | The package `@dbdatakit/sync-ui` (`Shared/web/sync-ui`, an npm workspace beside `store-ui`) with a draft model over a sync project's files: `useSyncDraft` reads `task.yaml`, the mappings, the connections and the scripts of a draft, gives each as a typed view, and writes a change as a path edit of the file's text (the shell's `yamlText`: `setAt`, `appendAt`, `removeAt`), so comments, key order and keys the screen does not know are kept; a file that does not parse, or is of a format version the screens do not know, is reported as "open it as YAML" rather than shown. The generic old components move to `@dbdatakit/shell` (`InheritableToggle`, `StatusBadge`, `RefreshCountdown`, `NotesPanel` with its Markdown edit, the `icons` the shell lacks); the pure helpers come back from git with their tests (`suggestMappings.ts`, `naturalKey.ts`, `checkForms.ts`, `readerNotes.ts`, `holdState.ts`, `lag.ts`, `readIntent.ts`, `runTimes.ts`, `parameterValues.ts`) | none | M | vitest: a `task.yaml` with comments and an unknown key keeps both after the schedule and an endpoint are changed through the model; a mapping of a newer `schemaVersion` reports itself as YAML only; the restored helpers' tests pass |
| SYNCAUTHORING-2 | The Sync frame of a sync project (SYNCAUTHORING-P1): the project's page opens on an explorer of Replications, Connections, Scripts, Runner and Files with breadcrumbs as the heading, and every destination is a route under the workspace (`/:project/:id/replications/:name/:tab`, `.../mappings/:mapping/:tab`, `/connections/:name`, `/scripts/:name`, `/runner`, `/files/*`); the draft's chrome on every page: **not saved** with **Save**, the check's count (opens the Check view), **Commit…**, **Build and publish…**; the switch **Designed / Form / YAML** on every page that edits a file (SYNCAUTHORING-P2), and **Files** opens today's page (file tree, the generated editors, Changes, Check, Builds, History) unchanged | SYNCAUTHORING-1 | M | Playwright (`sync-authoring.spec.ts` against `workspace-fake.ts`): open a sync draft, land on Replications, reload on a mapping's Column Mapping tab and land there; switch a mapping from Designed to Form to YAML and back with an unsaved edit kept in each; open Files and see today's page |
| SYNCAUTHORING-3 | Runner reads for the designed screens (SYNCAUTHORING-P3): a client `runnerReads(instance)` in `@dbdatakit/runner` for the task's state, its runs, a run's steps and log, the task's checkpoints and their history, and its decisions, for the Runner a build of the draft published to (`publishedVersionOf`, the publish target's `instance`); every reader says "not published yet" or "the Runner does not answer" in words, never an empty table | none | S | vitest against a fake relay: the task's state, runs and checkpoints are read for the published package and channel; a draft never published answers "not published yet"; a refused call names the Runner and the role it needs |
| SYNCAUTHORING-4 | The Replications page as the old list: a table with name, reader, schedule (said as a sentence), max lag, status (enabled or disabled from the file, paused, running or idle and the last run's outcome from the Runner when published), the mappings count and the check's errors per replication; **New Replication** as a dialog naming the two connections (replaces the card in `SyncSetPanel.tsx`, whose `.grid-row` rows overlap) | SYNCAUTHORING-2, SYNCAUTHORING-3 | S | Playwright: three replications listed with their reader and schedule; one with an error count; one paused on the fake Runner shows paused; New Replication makes the task and a mapping and opens its Overview |
| SYNCAUTHORING-5 | A replication's page: the tab bar (Overview, Table Mappings, Monitoring, Quality Checks, Runner, Version Control) with the chrome at its right end, **Enabled** (writes `enabled` of the draft's `task.yaml`; its title says it takes effect when published), **Pause/Resume** (the Runner's task now, with the note in `PauseDialog`; disabled with the reason before a publish), **Bulk Load…** (the old `BulkLoadForm` over `bulkLoadRequests`), **Reconcile deletes…** (disabled, naming AUTHORING-12, until it lands), **Run Now**, **Save**, **Delete** (the replication's folder, asked twice); the rail beside every tab: **Unpublished changes** (the draft's commits after the commit of the build last published, and uncommitted edits, with **Build and publish…**), **Task status** in place of the worker's process (next due, running and queued, paused and by whom, the last run, from SYNCAUTHORING-3), **Last 24h** (`MetricsCard` computed from the task's runs and their pass records: passes, failures, rows read and written, p50/p95/max, a bar per hour, 1h/24h/7d, a refresh countdown); Run Now and Bulk Load move to Monitoring's Run History | SYNCAUTHORING-2, SYNCAUTHORING-3 | M | Playwright: Run Now on a published replication shows the run in Run History with its passes; Pause asks a note and the fake Runner's task is paused; Enabled off marks the file not saved and the rail says unpublished; before any publish the run buttons say why they are off |
| SYNCAUTHORING-6 | Overview: the endpoint cards side by side (connection and database pickers from the draft's connections and the catalog cache, "inherited by N mappings unless overridden"), the schedule strip, the tracking card (the file's `tracking:`; the target's state once TASKENGINE-6 lands), and the sub-tabs Notes, Pipeline (the old `MappingPipelineCard` for Change Processing and Bulk Load: Reader → Staging → Writer, each box's kind and options; the kinds from AUTHORING-14), Provisioning (the replication-wide plan grouped by connection, `SyncProvisioningPanel.tsx`'s data in the old `ReplicationProvisioningPanel` layout, apply as the `provision` task), Custom Transforms (`ScriptBindings` and hooks over `scripts:` and `hooks:`), Bulk Load (`SegmentingStrategiesCard`; **Try** once AUTHORING-3 lands) | SYNCAUTHORING-5 | M | Playwright: change the schedule to Periodic with a cron and see the file's YAML change only those keys; pick the writer kind on the Pipeline; the Provisioning sub-tab shows a missing target table and its statement; Notes renders Markdown and edits it |
| SYNCAUTHORING-7 | Table Mappings, first half: the sidebar of mappings with column counts and **+**; the Overview of the source's tables from the catalog cache with mapping counts, ticks and **Create mappings** (one file per table, columns and key from the cache); the mapping editor's header (name, Cancel, Delete, Save mapping), the two sides with **Override for this table**, the table picker from the cache, **Custom query, not a table**, the source filter; the Column Mapping tab as the old `ColumnMappingEditor` with **Auto-map by name**, the primary key ticks, relationships and the cached metadata, folding `MappingColumns.tsx` into it | SYNCAUTHORING-5 | M | Playwright: tick two tables and Create mappings makes two files; on one, Auto-map by name maps three columns, untick a key column, override the target side's schema; Form and YAML show the same change |
| SYNCAUTHORING-8 | Table Mappings, second half: the mapping's sub-tabs Notes, Custom Transforms, Bulk Load (`DefaultSegmentingCard`), Provisioning (the mapping's plan, with the pending steps as the tab's badge), Pipeline (the overrides), Diagnostics (`traceTiming`), Verify (the `verification:` checks as the old `ChecksCard`, and the latest verification results of the published task from its runs' steps) | SYNCAUTHORING-7 | M | Playwright: add a row-count verification and a default segmenting by month; the Provisioning tab's badge counts the missing table's step; a verification step of a run on the fake Runner is shown under Verify |
| SYNCAUTHORING-9 | Monitoring: Current Status (a row per mapping: source, target, the position, its time and age, intent and hold from the checkpoint `watermark`, lag as SYNCAUTHORING-P4 says; **Manage…** opens `MappingReadStateDialog` rewired to the checkpoint's history and rewind), Run History (the task's runs, the status filter, older and newer, `RunDetailsDialog` with the pass records and the log), Pause History (the task's decisions), Bulk Load History (runs that carried a bulk load request, by mapping); each links to the run on the Runner's screens | SYNCAUTHORING-3, SYNCAUTHORING-5 | M | Playwright against the fake Runner: three mappings with positions and one held; rewind one mapping to an earlier version after the confirmation; filter Run History to failures and open one with its log |
| SYNCAUTHORING-10 | Quality Checks: the old `ReplicationChecksPanel` over `checks:` of `task.yaml` (seven kinds, severity, on error), with the latest results of each check once DATAQUALITY-1 records them | SYNCAUTHORING-5 | S | Playwright: add a freshness check and see it in the YAML view; with a recorded result on the fake Runner the check shows failed with its measure |
| SYNCAUTHORING-11 | Runner tab and the Runner page: the old `RunnerPage` layout over the Workspace's builds and publish (`PublishForm.tsx`): the package, the version served and staged per channel, the slots of the set and their bindings, the instances' states, the publish history, **Check**, **Stage**, **Publish and serve**; the replication's tab shows its task on that Runner and links to the Runner's screens | SYNCAUTHORING-3, SYNCAUTHORING-5 | S | Playwright: publish a draft to the fake Runner and see version 13 served, its two slots bound, and the replication's task row |
| SYNCAUTHORING-12 | Version Control: the replication's folder's commits (newest first, author, when), a commit's diff (`DiffViewer`), **Restore to here** puts the earlier texts into the draft as unsaved edits | SYNCAUTHORING-2 | S | Playwright: restore a mapping's earlier text, see it not saved, Save, and see it under Changes |
| SYNCAUTHORING-13 | Connections: the list as the old table (driver from `engine`, host, database, auth, the binding test's answer after publish) with **Test all** and **New connection** (the engine list from AUTHORING-14); a connection's page as the old cards: the connection (engine, address, database, timeouts, properties), the login by secret reference and the run login (`RunLoginCard`), custom transforms and hooks, the test card (the Runner's binding test, `ConnectionTestCard` layout); the secret findings stay under it | SYNCAUTHORING-2 | S | Playwright: a new connection by reference; typing a password into the YAML shows the secret finding in all three views; Test all on a published draft shows one reachable and one failed with the step that failed |
| SYNCAUTHORING-14 | Scripts: the list as the old table (kind, language, used by from the task and mapping files, enabled) with **New script**; a script's page as `ScriptEditPage` (the manifest's fields, the code in the shell's `CodeEditor`, the test panel `ScriptTestPanel` over the `script-test` route, where it is bound) | SYNCAUTHORING-2 | S | Playwright: a new value expression script, test it against generated input, bind it on a mapping's Custom Transforms and see "used by" name the mapping |
| SYNCAUTHORING-15 | Documentation and coverage: `docs/ui-coverage.md` names every new page and panel and the stories; `docs/ui-editors.md` rows of `sync-task-config`, `sync-mapping-config`, `sync-script-config` and the connections say the designed screens and the fallback views; `docs/authoring-parity.md`'s Sync rows; the Sync section of the Workspace's docs says how a person switches views (SYNCAUTHORING-P2); the shots under `DbDataSync/screenshots/` are retaken from the stories against the fake servers | SYNCAUTHORING-4 to SYNCAUTHORING-14 | S | `scripts/check_ui_coverage.py` and `scripts/check_ui_editors.py` pass with the new rows; the stories run in CI |
| SYNCAUTHORING-16 | Lag of a mapping as SYNCAUTHORING-P4 says (recommended: the drain writes each mapping's lag into its pass record, exact when the reader can say it, estimated otherwise) | SYNCAUTHORING-P4 | M | Sync conformance on SQL Server change tracking and PostgreSQL logical replication: a pass behind the source by three versions records a lag; the Current Status row shows it with exact or estimated |

## Needs from other subjects

- AUTHORING-14: the engine lists of the Pipeline sub-tabs and **New connection** come from the engine registry (SYNCAUTHORING-6, SYNCAUTHORING-13);
  until it lands the designed screens use the list `SyncSetPanel.tsx` has today. Ownership is SYNCAUTHORING-P7.
- AUTHORING-3: a segmenting strategy's **Try** (SYNCAUTHORING-6), a query source's rows (SYNCAUTHORING-7); until then each says it waits on a Runner task.
- AUTHORING-4: the source side of the Provisioning sub-tabs (SYNCAUTHORING-6, SYNCAUTHORING-8); until then the source is "not known".
- AUTHORING-6: the catalog cache of a Sync connection, which the table pickers, Create mappings and Column Mapping read (SYNCAUTHORING-7).
- AUTHORING-12: **Reconcile deletes…** in the chrome and resync in Run History (SYNCAUTHORING-5, SYNCAUTHORING-9).
- AUTHORING-9: its stories of the Sync forms stay the stories of the Form view; this subject's stories cover the Designed view (no story is written twice).
- AUTHORING-10: the secrets overview of the project, which the Connections page can link to (SYNCAUTHORING-13).
- DATAQUALITY-1: recorded results of a replication's checks (SYNCAUTHORING-10).
- TASKENGINE-6: the tracking state of the target and its divergences (SYNCAUTHORING-6).
- CHANNELS-3: a **Trial run…** beside Run Now when it lands (the chrome keeps a place for it).
- TRUSTZONES: if the console's relay to a Runner changes, SYNCAUTHORING-3 follows it (it is the one client the screens read through).

## Proposed decisions

### SYNCAUTHORING-P1: where the designed screens live in the Workspace (awaiting the owner)

Scenario: an engineer opens the draft `add-order-lines` of the sync project `erp-sync`. Option A: the page opens on the Sync frame (explorer of
Replications, Connections, Scripts, Runner, Files; the replication's tabs and rail as the old screens had them); **Files** is today's workspace page,
whole. Option B: today's page stays the page; a tab **Designed** beside Changes, Check and Runs holds the old screens inside the right column (no rail
of their own, the file tree always beside them). Option C: the designed screens are a separate module of the console beside the Workspace, reading the
Workspace's draft. Under A the look is the old one and the generic page is one click away; under B the old layout is squeezed into a column of a page
with eight tabs and loses its rail; under C the person leaves the Workspace to author, which the owner's "one place" condition refuses.
Recommendation: A.

### SYNCAUTHORING-P2: how a person switches between the designed screen, the generated form and YAML (awaiting the owner)

Scenario: on the mapping `customers`, an engineer wants to set a key the designed screen does not offer (`relationshipColumns`). Option A: every page
that edits a file has the switch **Designed / Form / YAML** at the right of its header; Form and YAML show that file (a page made of several files, such
as a replication's Overview, lists them in a menu); the choice is kept per person and per kind of file in the browser and in the address
(`?view=yaml`), so a link opens the same view; a file the designed screen cannot show (it does not parse, or its format version is newer) opens in YAML
with a line saying why. Option B: the designed screen has an **Edit as YAML** link per card only, and the generated form is reached from Files.
Option C: one global setting per person. Under A every file is two clicks from its text and nothing is lost between views (they edit the same text and
the check runs under each); under B the generated form stops being a first-class view; under C a person who wants YAML for one file switches every page.
Recommendation: A.

### SYNCAUTHORING-P3: how the designed screens read the Runner (awaiting the owner)

Scenario: the Monitoring tab of `erp-to-dw` shows its runs and positions. Option A: the screens read through the console's relay to that Runner
(`/relay/<instance>/api/v1/...`) as the signed-in person, with that person's role on the Runner (a person without it sees "you need the Runner's
viewer role" and the link to its screens); starting runs stays on the Workspace's run routes as today. Option B: the Workspace server gets read routes
that call the Runner with its publisher key for anyone who can read the draft. Option C: no reads; the tabs link to the Runner's screens as today.
Under A nothing new is trusted and the Runner's audit names the person; under B the Workspace's key reads runs for people the Runner never granted;
under C the old Monitoring tab is not restored. Recommendation: A.

### SYNCAUTHORING-P4: what "lag" means now that no Sync server measures it (awaiting the owner)

Scenario: the Current Status row of `orders` should say how far behind the source it is. Option A: the age of the mapping's position (the time in
the checkpoint `watermark`), shown as "position from 4 minutes ago", no read of the source. Option B: a read-only Runner task that measures each
mapping's lag against the source on demand (**Measure lag**). Option C: the drain measures lag at the end of each pass and writes it into the pass record
(exact for change tracking and logical slots, estimated otherwise), and the row shows the last pass's figure with its time. Under A a quiet table looks
behind; under B a person waits for a run to see a number; under C the figure is as old as the last pass and costs one query per mapping per pass, which
the old server also paid. Recommendation: C, with A shown when no pass has recorded a lag yet (SYNCAUTHORING-16).

### SYNCAUTHORING-P5: the chrome's Enabled and Pause on a draft (awaiting the owner)

Scenario: an engineer turns **Enabled** off and clicks **Pause** on `erp-to-dw`. Option A: Enabled edits `enabled` of the draft's `task.yaml` (not
saved until Save, running until the set is published again; its title and the rail's Unpublished card say so) and Pause pauses the published task on
the Runner now (the note asked, recorded by the Runner); both stay in the chrome as the old screens had them. Option B: only Pause stays in the chrome;
Enabled moves into the Overview. Option C: Enabled also pauses the Runner's task at once. Under A each control says what it costs, as the old titles did;
under C the draft and the Runner disagree until the next publish. Recommendation: A.

### SYNCAUTHORING-P6: where the reused code lives (awaiting the owner)

Scenario: a worker restores `ColumnMappingEditor.tsx` and `RunDetailsDialog.tsx` from git. Option A: Sync's designed screens and their cards are a
shared package `@dbdatakit/sync-ui` (`Shared/web/sync-ui`, like `store-ui`), mounted by `@dbdatakit/workspace` for a sync project; components any
product uses go to `@dbdatakit/shell`; Runner readers and run dialogs go to `@dbdatakit/runner`. Option B: everything goes into
`Shared/web/workspace/src/sync/`. Under A the console's Runner screens can show a Sync run with the same dialog and the package is tested on its own;
under B the Workspace package grows by about ten thousand lines of one kind's screens. Recommendation: A.

### SYNCAUTHORING-P7: who owns the engine list of the Sync screens (awaiting the owner)

Scenario: AUTHORING-14 replaces the list written in `SyncSetPanel.tsx`, and SYNCAUTHORING-6 and SYNCAUTHORING-13 replace that panel. Option A:
AUTHORING-14 stays in AUTHORING and lands first as a hook (`useSyncEngines`) both use. Option B: AUTHORING-14 moves into this subject as its first
task. Option C: both build it. Recommendation: A, so AUTHORING-14 is not blocked by this subject's larger tasks.

## Open designs

### Preview SQL of a mapping

The old **Preview SQL** tab listed every statement a pass of a mapping would run, in order (the reader's, the staging's and the writer's built-in
statements, the operator's SQL, the scripts' output, the hooks), built by the drivers from the mapping and the tables' metadata. Nothing answers it now.
The likely shape is a command `dbdatasync mapping preview --from-cache --json` run by the Workspace on a copy of the draft, as the provisioning plan is,
with the column cache as the metadata. Open: whether every driver builds its statements without a connection (the change tracking reader asks the server
for its version functions), and whether a statement that needs the live server is shown as "built on the Runner" or left out.

### The Drivers, Libraries and Files sections

The old rail had **Drivers** (driver YAML files), **Libraries** (NuGet packages) and **Files**. A sync project's package carries the drivers its
connections need (`package publish` adds them) and the platform comes from the Runner (AUTHORING-2). Open: whether a project may carry its own driver
YAML (`config/drivers/`) and so needs an editor here, or drivers stay a command on the repository with a `none (reason)` row in `docs/ui-editors.md`.

## Shared areas touched

- `Shared/web/sync-ui` (new npm workspace), `Shared/web/package.json` (its workspaces), `Shared/web/workspace` (the Sync frame, routes, the view switch),
  `Shared/web/shell` (generic components moved in), `Shared/web/runner` (`runnerReads`, run dialogs), `Shared/web/console` (nothing beyond the relay it
  has, unless SYNCAUTHORING-P3 is answered B).
- `DbDataSync` drain (the lag of SYNCAUTHORING-16), `DbDataKit.Authoring.Server` only if SYNCAUTHORING-P3 is answered B.
- `DbDataKit/tests/DbDataKit.Server.Web.Tests` (`sync-authoring.spec.ts`, `workspace-fake.ts` and a fake Runner relay), `DbDataSync/screenshots/`.
- `docs/ui-coverage.md`, `docs/ui-editors.md`, `docs/authoring-parity.md`.
