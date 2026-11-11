# Plan 18: lineage, what is left

Status: **in progress** (audited 2026-11-09 against the code). What is built (the graph model, identity, queries, the commands, the Build, Sync and Runner graphs, the console's join and page) is in [docs/lineage.md](../lineage.md) and the READMEs of `DbDataKit.Lineage` and `DbDataKit.Lineage.Cli`. This plan holds the slices still open; the ids G06 to G12 are as in plan 19's lanes.

Audit, row by row, against the code: **G08** is open (`git grep -i lineage DbDataGateway/src` finds nothing: the Gateway has no graph and no `lineage` noun). **G11b**: the edge kinds `serves`, `extracts`, `promotes` and `reads` are defined in `EdgeKind` and no product emits one (only tests use `reads`); the `binds` half is built (`BuildLineage`). **G10b**: the console's page (`Shared/web/console`, `LineagePage`) draws design only and `LineageJson` carries no `EdgeRun`. **G07b**: Sync serves `GET /api/lineage` (`SyncLineage`, `SyncLineageRuns`), and `DbDataKit.Lineage.Cli` is mounted on Build's and the Runner's command lines but not Sync's; no server builds its graph in `LineageStore` (`git grep LineageStore` finds the library and its tests only). **G06b**: `LineageConformanceTests.After_a_refresh_each_load_edge_carries_its_last_run_from_the_tracking_tables` exists; whether it has run on the engines is not known from the repository. **G12**: neither DbDataStore nor the apps host emits anything. **Retention**: `LineageStore.PruneRuns` is called by nothing but its test. **Engine defaults** (G17): built, `LineageIdentity` reads the registry's `default_port`. Nothing in the old list was already built, and nothing is obsolete.

Formats: no slice changes a file in `docs/formats.json`. The lineage wire form (`LineageJson`) is not a stored file and has no registry entry; G10b adds a property to it (additive: the console reads it, a document of an unknown kind is still refused). The `lineage` state module (G07b) is state, versioned by `DbDataKit.State`'s own mechanism.

Editors: lineage is read by screens (the console's Lineage page), not authored, so G07b to G12 need no editor row. The one slice that lets a person author lineage, G13, has its row in [docs/ui-editors.md](../ui-editors.md).

## Slices

| Slice | What | Needs | Size | Format | Done when |
| --- | --- | --- | --- | --- | --- |
| G08 | **Gateway: design lineage for data sources and the tables they serve** (a `GET /api/v1/lineage` and the `lineage` noun, viewer role); run lineage as a summary per table (read count, last read, the clients that read it; never per query: a per-query record belongs to the Gateway's activity log, plan 13a P09, under its retention, and the table node links to it) | `DbDataKit.Lineage`, identity | S | none (wire form) | after queries, the served tables show their last use (test) |
| G11b | **Semantic layer: `serves` edges** (the Gateway serving a model) and semantic members of a catalog published to a Gateway without Build's project. The `binds` half is built | G08 | S | none | the console's graph shows a measure's path from the source table through the Sync replication and the Build model to the Gateway that serves it (test) |
| G10b | **Console: the run overlay** (a failed run marked on its edge, late and fresh colours from `DbDataKit.Monitoring`). The wire form carries design only, so `EdgeRun` (which Build and Sync both compute) has to be sent too | G09 (built) | S | wire form gains `runs` (additive) | Playwright: a failed run is marked on its edge |
| G07b | **Sync: the `lineage` noun on Sync's command line** (the endpoint and `SyncLineage` are built); the graph kept in a `LineageStore` between runs; run lineage on the wire; the target table's own upstream when Sync is not its only writer | none | S | `lineage` state module (existing) | `dbdatasync lineage graph` answers a document that validates against the noun's schema |
| G06b | **Build: runs on column edges;** a job node per load; copies at column level; the conformance test named above run on SQL Server and PostgreSQL (written without a container engine); run lineage in the server's graph (needs the tracking database) | none | S | none | the test passes in CI; column edges carry runs |
| G12 | **DbDataStore and apps emitters** (entities, documents, extractions, promotions; apps and dashboards as consumers with `reads` edges) | G08 for the shape, Store, apps | S each | none | per product, its objects appear in the console's graph |
| G16 | **Retention:** one server calls `LineageStore.PruneRuns` with its run-detail retention (decision 1 of the lineage doc: no setting of its own) | G07b | S | none | runs older than the retention are gone after a prune (test) |
| G17 | **Engine defaults:** `LineageIdentity`'s default-port table replaced by the registry's `default_port` | plan 14 R5 (built) | S | none | one source of default ports: a test fails when the table and the registry disagree, then the table is deleted **Status: done** (2026-11-11; no format touched). The table is gone: `LineageIdentity` asks `EngineRegistry.Builtin.Find(engine)?.DefaultPort`, and `LineageIdentityTests.Every_engine_with_a_default_port_in_the_registry_leaves_it_out_and_keeps_any_other` runs over every engine of the registry that has one. |

## Later, not scheduled

Not in the first version (owner), kept as slices so they are not forgotten. None is scheduled; give each a wave when a person asks for it.

| Slice | What | Needs | Size | Format | Done when |
| --- | --- | --- | --- | --- | --- |
| G13 | **Manual entries** for external systems: a lineage file (`lineage-entries/1`, edges with an owner, `declared`, a dataset named by `LineageIdentity`) or a console form, merged into the joined graph and drawn apart from computed edges | G08 | M | **new, open:** `lineage-entries/1`, registered in `docs/formats.json` with the slice | a declared edge from an outside system to a Build model appears in the console's graph with its owner; an entry naming a node that does not exist is refused naming it (test) |
| G14 | **Export to OpenLineage** (events from run lineage, datasets and jobs from design lineage) and to external catalogs; the model already maps onto OpenLineage | G07b, G10b | M | none (the export is OpenLineage's own format) | a run's events validate against OpenLineage's published schema (test) |
| G15 | **Tracing a value:** from a query result or a visual's value (the query explorer, a dashboard cell) open where it came from: the measures, their columns, the Build models and loads, the Sync replications and the source tables (the definition path); the rows behind the value (a drill to detail, which needs the semantic engine's help) is its own design after this | G11b, plan 14a SL14 to SL16 | L | none | a measure cell in the explorer opens its definition path across products (Playwright); a value with no known path says so |

## Decisions

1. **Manual entries are a file first** (G13), authored in git like other definitions, with the console form after. Default taken (owner delegated, 2026-11-09).
2. **The Gateway's lineage never holds per-query records** (unchanged: they belong to the activity log).
