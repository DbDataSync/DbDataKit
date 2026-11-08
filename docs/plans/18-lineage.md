# Plan 18: lineage, what is left

Status: **in progress**. What is built (the graph model, identity, queries, the commands, Build, Sync and Runner graphs, the console's join and page) is in [docs/lineage.md](../lineage.md). This plan holds the slices still open. Slice ids are as in plan 19's lanes.

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| G08 | Proxy: design lineage for data sources and the tables they serve (a `GET /api/v1/lineage` and the `lineage` noun); run lineage as a summary per table (read count, last read, the clients that read it; never per query: a per-query record belongs to the Proxy's activity log, plan 13a P09, under its retention, and the table node links to it) | `DbDataKit.Lineage`, identity | S | after queries, the served tables show their last use (test) |
| G11b | Semantic layer: `serves` edges (the Proxy serving a model), and semantic members of a catalog published to a Proxy without Build's project. The `binds` half is built | G08 | S | the console's graph shows a measure's path from the source table through the Sync replication and the Build model to the Proxy that serves it (test) |
| G10b | Console: the run overlay (a failed run marked on its edge, late and fresh colours from `DbDataKit.Monitoring`). The wire form carries design only, so `EdgeRun` (which Build and Sync both compute) has to be sent too | G09 | S | Playwright: a failed run is marked on its edge |
| G07b | Sync: the `lineage` noun on Sync's command line (the endpoint `GET /api/lineage` is built, and the library `SyncLineage`); keeping the graph in a `LineageStore` between runs; run lineage on the wire; the target table's own upstream when Sync is not its only writer | | S | `dbdatasync lineage graph` answers a document that validates against the noun's schema |
| G06b | Build: runs on column edges; a job node per load; copies at column level; the conformance test `After_a_refresh_each_load_edge_carries_its_last_run_from_the_tracking_tables` was written without a container engine and has not been run (run it on SQL Server and PostgreSQL); run lineage in the server's graph (needs the tracking database) | | S | the test passes in CI; column edges carry runs |
| G12 | DbDataStore and apps emitters (entities, documents, extractions, promotions; apps and dashboards as consumers) | Store, apps | S each | per product, its objects appear in the console's graph |
| Retention | one server calling `LineageStore.PruneRuns` with its run-detail retention; today no server keeps a store | G07b | S | runs older than the retention are gone after a prune (test) |
| Engine defaults | `LineageIdentity`'s default-port table replaced by the registry's `default_port` | plan 14 R5 | S | one source of default ports |

## Later, not in the first version

- **Tracing a value.** From a query result or a visual's value (the query explorer, a dashboard cell) open where it came from: the measures, their columns, the Build models and loads, the Sync replications and the source tables (the definition path), and later the rows behind the value (a drill to detail, which needs the semantic engine's help and is its own design).
- **Manual entries** for external systems (a lineage file or console form, as `declared` edges with an owner).
- **Export** to OpenLineage (events from run lineage, datasets and jobs from design lineage) and to external catalogs.
