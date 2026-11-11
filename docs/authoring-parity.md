# Authoring parity: what the standalone screens do and what the Workspace does

The owner's decision (2026-11-11): **no standalone authoring screen or route is retired until the Workspace has feature parity with it and the owner has tested it in a snapshot release.** The three hand-over rows of [plan 33](plans/33-fewer-services.md) (SC20g Build, SC21d Store, SC22e Sync) are on hold for that reason: first parity, then the owner's snapshot test, then the owner's go. The standalone products keep working unchanged meanwhile (decision D3).

This page is the list that parity is measured against: every thing a person can do on a standalone authoring screen (`Shared/web/build`, `Shared/web/store`, `Shared/web/sync`), what the Workspace (`Shared/web/workspace`, with the console's Approvals page for the Store) does for it today, and the plan row that owns each gap. Read from the code as of 2026-11-11, not by clicking through every screen. Keep it current in the same commit that builds a row.

**States.** `built`: a person can do it in the Workspace. `partly`: some of it, the Workspace column says what. `missing`: not built, a plan row owns it. `not planned`: the Workspace will not do it, with the reason in the Workspace column; each is a decision the owner confirms before the hand-over, because the standalone screen is where the feature lives today and removing the screen removes the feature.

**What the Workspace does differently on purpose.** It authors files in a git draft and publishes artifacts; it holds no connection, login or row (D20), and it does not run anything against a database (a run is the Runner's task, plan 28c). So a standalone screen that reads or changes a database is `not planned` here unless the answer can come from a committed cache (D20, SC20i), which has the shape of tables and never their rows.

Summary (counts of the features below):

| Product | built | partly | missing | not planned | features |
| --- | --- | --- | --- | --- | --- |
| Build | 10 | 3 | 6 | 6 | 25 |
| Store | 6 | 1 | 0 | 3 | 10 |
| Sync | 9 | 4 | 5 | 3 | 21 |
| All | 25 | 8 | 11 | 12 | 56 |

### Build

| Feature | The standalone screen (files under `Shared/web/`) | The Workspace today (files under `Shared/web/`) | State | Owner |
| --- | --- | --- | --- | --- |
| Projects: list, make a project (name checked as you type) | `build/src/pages/ProjectsPage.tsx`, `NewProjectPanel.tsx` | `workspace/src/pages/WorkspacesPage.tsx` (kind picker on a new project) | built | SC20f |
| A draft of a project: make, abandon, commit, push, merge, diff, history | `build/src/pages/GitPanel.tsx`, `build/src/views/workspaceStore.ts` | `workspace/src/pages/WorkspacePage.tsx`, `WorkspacesPage.tsx` | built | plan 22a GP35a |
| Models by folder with the errors per model | `build/src/pages/ProjectPage.tsx` (`ModelsTab`) | `workspace/src/pages/BuildPlanPanel.tsx` (`ModelsView`) | built | SC20f |
| Edit a model's `.sql` and `.yml` and the other files of a project (tree, new, move, delete, diagnostics marked in the text) | `build/src/pages/EditTab.tsx`, `FileTools.tsx` | `workspace/src/pages/WorkspacePage.tsx` (the file editor: Save, Move, Delete) | built | SC20f |
| Schema forms for a model's `.yml` and a semantic file's `.yml` | `build/src/pages/EditTab.tsx` (form from `semantic.schema.json`) | none: YAML text only | missing | SC20m |
| New model by kind (`project model create`) | `build/src/pages/NewModelPanel.tsx` | none: a new file is made by path | missing | SC20m |
| New connection, grants, serving policy (`extensions.gateway`) | `build/src/pages/NewConnectionPanel.tsx`, `ConnectionGrants.tsx`, `ConnectionPolicy.tsx` | none: `connections/*.yaml` as text | missing | SC20m |
| Load graph of the models (layers; choose one to light up what it is built from and what is built from it) | `build/src/pages/ProjectPage.tsx` (`GraphTab`), `graphLayout.ts` | `workspace/src/pages/BuildGraphPanel.tsx`, `buildGraph.ts` (read from `rendered/<connection>/loads.yml`; draws with Build's `GraphView.tsx`) | built | SC20h |
| Check the project (compiler diagnostics, errors and warnings, file and line) | `build/src/pages/ProjectPage.tsx` (`CheckTab`), `Problems.tsx` | `workspace/src/pages/CheckPanel.tsx` (`dbdatabuild project show metadata`, offline) | built | SC20c |
| Compile: write the plan into `rendered/` and see what was written or removed | `build/src/pages/CompilePanel.tsx` | none: `rendered/` is read only | missing | SC20l |
| The lowered SQL of a model (rendered files per model) | `build/src/pages/PreviewPanel.tsx` | `workspace/src/pages/BuildPlanPanel.tsx` opens a rendered file read only | partly | SC20l |
| Offline sample: a model run on generated rows (Sample tab) | `build/src/pages/DeployTabs.tsx` (`SampleTab`), `PreviewPanel.tsx` | none | missing | SC20l |
| Preview of a table or model: its columns and types, with generated values (never the database's rows) | `build/src/pages/PreviewPanel.tsx` | `workspace/src/pages/CatalogPanel.tsx` (from the catalog cache, `catalogCache.ts`) | built | SC20k |
| Import tables from a database into the project (list what is not mapped yet, write the files) | `build/src/pages/ImportPanel.tsx`, `DefinitionPanel.tsx` | `workspace/src/pages/CatalogPanel.tsx` lists the cached tables; writing model files from them is not built | partly | SC20k |
| Project tests (metadata rules and model tests, violating rows) | `build/src/pages/TestsPanel.tsx` | none: the tests read the target database | not planned | SC20o |
| Status of a target (every declared object, what a deploy would do) | `build/src/pages/DeployTabs.tsx` (`StatusTab`) | none: reads a target database | not planned | SC20o |
| Refresh plans: the compiled plans and what each loads | `build/src/pages/DeployTabs.tsx` (`PlansTab`) | `workspace/src/pages/BuildPlanPanel.tsx` (`PlanView`: the plan files, how many loads) | built | SC20f |
| Refresh run, dry run, checks after a refresh | `build/src/pages/RefreshPanel.tsx` | none: a run is the Runner's task (plan 28c), shown by the Runner screens | not planned | SC20o |
| Compare two tables (counts and digests) | `build/src/pages/DeployTabs.tsx` (`CompareTab`) | none: reads databases | not planned | SC20o |
| Initialise a target (tracking tables, seeds) | `build/src/pages/DeployTabs.tsx` (`InitialiseView`) | none: an act on a database | not planned | SC20o |
| Publish the package to a Runner (channel, stage, serve, slots) | `build/src/pages/RunnerPublish.tsx`, `runnerPublish.ts` | `workspace/src/pages/PublishForm.tsx`, `BuildsPanel.tsx` | built | SC20d |
| Publish a semantic model to a Gateway | `build/src/pages/ModelPublish.tsx` | `workspace/src/pages/PublishForm.tsx`, `BuildsPanel.tsx` (a `gateway` target: the build's compiled semantic models, made offline by `dbdatabuild project publish --to gateway --out`, over the data sources the Gateway has under the connections' names) | built | SC20e |
| Publish the project's data sources (connection, read login as a secret, grants) to a Gateway; hand a data source over | `build/src/pages/PublishTab.tsx` | none: it puts a login on a Gateway, and the Workspace holds none (D20, the option not taken); the offline publish names data sources the Gateway already has, which an administrator or Build's server makes | not planned | SC20e |
| Dashboards and reports of a project: list, new, check, publish (Views tab) | `build/src/pages/ViewsTab.tsx` | none: `dashboards/*.yaml` and `reports/*.yaml` open as text | missing | SC20n |
| Lineage: the model graph; column lineage is the console's Lineage page, which reads Build servers | `build/src/pages/ProjectPage.tsx` (`GraphTab`); console Lineage page | `workspace/src/pages/BuildGraphPanel.tsx` (model level only) | partly | SC20h |

### Store

| Feature | The standalone screen (files under `Shared/web/`) | The Workspace today (files under `Shared/web/`) | State | Owner |
| --- | --- | --- | --- | --- |
| The models of a server and their entities | `store/src/pages/ModelsPage.tsx` | `workspace/src/pages/WorkspacePage.tsx` (the `models/` files of a `store` project) | built | SC21d |
| Model editor: a form from the model schema and YAML, the check as you type | `store/src/pages/ModelEditor.tsx`, `views/ModelEditorView.tsx` | `workspace/src/pages/StoreModelPanel.tsx`, `StructuredPanel.tsx` | built | SC21d |
| New model from a starting text, name checked | `store/src/pages/NewModelPage.tsx` | `workspace/src/pages/StoreModelPanel.tsx` (`NewStoreModel`) | built | SC21d |
| Change requests: read the proposed rows against the rows now, approve, reject with a reason | `store/src/pages/ChangesTab.tsx`, `views/ChangesView.tsx` | `console/src/pages/ApprovalsPage.tsx` decides in place (change requests are a Store server's data, not a draft's) | built | SC21c |
| History of a model's text: earlier texts with who and when, diff against now, restore | `store/src/pages/HistoryTab.tsx`, `views/HistoryView.tsx` | `workspace/src/pages/WorkspacePage.tsx` (git history and changes of the draft; no restore button) | partly | SC21e |
| Publish a model to the Gateway (the entity-model route) | `dbdatastore model publish` (the Store has no screen for it) | `workspace/src/pages/PublishForm.tsx` | built | SC21b |
| Publish with the approval path (`approval: steward|owner` opens a change request) | Store server | none: the Workspace's merge is the author's review (SC21b) | not planned | SC21b |
| Data source adoption for a publisher client (a data source made by hand is refused) | Gateway adopt route; the Store sends its own data source | `workspace/src/pages/PublishForm.tsx`: the Gateway's refusal of a data source made by hand opens a confirmation that names the data source and who made it; **Confirm and publish** sends `adopt`, which the Gateway records (who, when, which; in its audit and the data source's metadata) | built | SC21f |
| Storage: create tables, migrate by adding (dry run first), per copy and instance | `store/src/pages/StorageTab.tsx`, `views/StorageView.tsx` | none: an act on a database, the Store server's | not planned | SC21d |
| Rows of an entity: edit with the store-ui controls | `store/src/pages/EntityPage.tsx` | none: data, not authoring | not planned | SC21d |

### Sync

| Feature | The standalone screen (files under `Shared/web/`) | The Workspace today (files under `Shared/web/`) | State | Owner |
| --- | --- | --- | --- | --- |
| Connections: list, test a connection (reachable) | `sync/src/pages/ConnectionsPage.tsx`, `connection-edit/ConnectionTestCard.tsx` | `workspace/src/pages/SyncSetPanel.tsx` lists them; no test | partly | SC22h |
| Connection edit as a form (engine, address, auth by secret reference); no password in git | `sync/src/pages/ConnectionEditPage.tsx` | `workspace/src/pages/SyncSetPanel.tsx`, `StructuredPanel.tsx`, `syncSchemas.ts` (form from `sync-connection`, secret findings) | built | SC22d |
| The connection's run login (`extensions.sync.run_user`) | `sync/src/pages/connection-edit/RunLoginCard.tsx` | YAML text only | partly | SC22i |
| Replications: list with status, lag, enable and pause | `sync/src/pages/ReplicationsPage.tsx` | `workspace/src/pages/SyncSetPanel.tsx` (the list, `enabled` in the form); no run state | partly | SC22l |
| Overview: per replication, what is wrong with it | `sync/src/pages/replication-detail/OverviewPanel.tsx` | `workspace/src/pages/SyncSetPanel.tsx` fed by the check of the draft (`WorkspacePage.tsx`) | built | SC22f |
| Replication settings: notes, schedule, endpoints, pipeline (reader, cache, writer), reconcile | `replication-detail/OverviewPanel.tsx`, `EndpointsCard.tsx`, `ScheduleCard.tsx`, `ReconcileConfigCard.tsx` | `workspace/src/syncSchemas.ts` (the task form: scheduling, change processing, bulk load, endpoints, reconcile) | built | SC22d |
| New replication (a task and a mapping between two connections) | `sync/src/pages/ReplicationsPage.tsx` | `workspace/src/pages/SyncSetPanel.tsx` (**New replication**) | built | SC22d |
| Table mappings: new, delete, edit tables, filter, query source | `replication-detail/TableMappingsPanel.tsx`, `TableMappingForm.tsx`, `SourceFilterCard.tsx` | `workspace/src/syncSchemas.ts` (the mapping form: sources, targets, filter, query) | built | SC22d |
| Mapping editor with columns from the live database (the source column of each target column, same-name suggestions, types) | `replication-detail/ColumnMappingEditor.tsx`, `suggestMappings.ts`, `CachedMetadataCard.tsx` | `workspace/src/pages/MappingColumns.tsx` (from the catalog cache, `catalogCache.ts`) | built | SC22g |
| Primary key (natural key picked from the target columns) | `replication-detail/PrimaryKeyCard.tsx`, `naturalKey.ts` | `workspace/src/pages/MappingColumns.tsx` (picks from the cached columns), `syncSchemas.ts` | built | SC22g |
| Transforms per column, the mapping's whole pipeline (Custom Transforms tab, mapping preview) | `replication-detail/MappingPipelineCard.tsx`, `MappingPreview.tsx`, `OverviewPanel.tsx` | the transform text of a column mapping in the form; scripts and the pipeline in YAML | partly | SC22i |
| Segmenting strategies and the default segmenting of a reload | `replication-detail/SegmentingStrategiesCard.tsx`, `DefaultSegmentingCard.tsx`, `BulkLoadForm.tsx` | YAML text only | missing | SC22i |
| Provisioning: both sides' plan, preview, apply (DDL runs now) | `replication-detail/ProvisioningCard.tsx`, `ReplicationProvisioningPanel.tsx` | none | missing | SC22j |
| Quality checks of a replication (`checks:` seven kinds, severity, on_error) | `replication-detail/ReplicationChecksPanel.tsx`, `checkForms.ts`, `ChecksCard.tsx` | YAML text only | missing | SC22k |
| Scripts: the registry, write, compile, test against sample input, bind | `sync/src/pages/ScriptsPage.tsx`, `ScriptEditPage.tsx`, `script-edit/ScriptTestPanel.tsx` | none: the template has no scripts folder | missing | SC22k |
| Library management (drivers and the platform a package is made for) | `sync/src/pages/ScriptsPage.tsx`, the console's connection library | none: `SyncPlatform` has no setting (SC22c open) | missing | SC22k |
| Publish to a Runner (package check, channel, stage or serve, slots, bindings) | `sync/src/pages/RunnerPage.tsx`, `RunnerLinkCard.tsx`, `replication-detail/ReplicationRunnerPanel.tsx` | `workspace/src/pages/PublishForm.tsx` | built | SC22c |
| Who runs the replication (`old`, `observe`, `runner`) and the handover | `replication-detail/ReplicationRunnerPanel.tsx` | none: no Sync scheduler stands behind a Workspace publish (SC22c) | not planned | SC22c |
| Version control: the config's git log and diff | `replication-detail/HistoryPanel.tsx` | `workspace/src/pages/WorkspacePage.tsx` (history, changes, commit, merge) | built | plan 22a GP35a |
| Run actions: run now, cancel, resync, bulk load, reconcile deletes, pause | `replication-detail/RunsPanel.tsx`, `BulkLoadForm.tsx`, `ReconcileDeletesForm.tsx`, `PauseDialog.tsx` | none: runs belong to the Runner's screens and the Sync server | not planned | SC22l |
| Monitoring, run history, verification results, rows of a source query | `replication-detail/MonitoringPanel.tsx`, `VerificationPanel.tsx`, `QuerySourcePanel.tsx` | none: runtime state and rows; a cache holds no row (D20) | not planned | SC22l |


## What the hand-over rows must keep

Removing the standalone screens with these still `not planned` loses the feature, so the owner decides for each group before SC20g, SC21d and SC22e are released:

- **Runs and the database-facing Build views** (SC20o: tests, status, refresh run, compare, initialise). Candidates: they move to the Runner's task screens and a `dbdatabuild` command line, or the Build server keeps these routes and only its authoring routes are removed.
- **The Store's storage and rows** (`StorageTab.tsx`, `EntityPage.tsx`): acts on a database and data, not authoring. SC21d's hand-over removes only the authoring screens and keeps these.
- **Sync's run, monitoring and verification screens** (SC22l): the Sync server's runtime screens stay; SC22e removes the authoring routes only.
- **Publish of Build's models to a Gateway** (SC20e, owner decision 2026-11-11): Build has the offline verb `project publish --to gateway --out <dir>` (models and data-source documents, no login); the Workspace publishes its models with the publisher key over data sources the Gateway already has. Making or changing a data source (the login) stays with Build's server or a Gateway administrator. Built against a fake Gateway and a stand-in `dbdatabuild`; not run against a real Gateway.

## Gaps and their rows

Every `missing` and `partly` has a row in plan 33: SC20h (Build load graph), SC20k (cache-backed preview and import), SC20l (offline compile, lowered SQL and sample), SC20m (forms for model, connection and semantic files), SC20n (dashboards and reports of a project), SC20o (the decision on database-facing Build screens), SC21e (restore an earlier text), SC21f (data source adoption), SC22f (error counts), SC22g (mapping columns from the cache), SC22h (connection test), SC22i (transform, segmenting and pipeline forms, the run login), SC22j (provisioning plan), SC22k (checks, scripts, library), SC22l (the decision on Sync's run and monitoring screens). The cache itself is SC20i (format, readers, checks) and SC20j (the paired server's call that writes it).
