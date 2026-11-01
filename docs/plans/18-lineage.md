# Plan 18: lineage across the suite

Status: **proposed** (2026-10-26) from the owner's direction. The slices (section 7) belong to the next wave ([plan 19](19-next-wave.md));
none are in plan 13a.

## 1. Goal

Every dependency and data flow in the suite can be traced, both ways:

- **downstream, for impact**: what reads this column, table, measure or entity, and what breaks if it changes;
- **upstream, for provenance**: where a number on a dashboard, a row in a mart or a value in a report came from.

The owner's rules for the first version:

- **Best effort**: column level wherever a product knows it, **table level where column level is too complicated or unavailable**. A gap is
  shown as a gap, never filled by a guess.
- **Manual lineage entries** (documenting external systems by hand) may be needed later; not in the first version.
- **Export** (to OpenLineage and external catalogs) will be needed eventually; not in the first version. The model is designed so it maps
  onto OpenLineage, so export is a later addition, not a redesign.
- **Tracing a value back** (from a query result or a visual's value to where it came from, and further to the rows behind it) is a later
  feature, not in the first version (owner, 2026-10-26).

## 2. What the suite already knows

| Product | What it knows today | Granularity |
| --- | --- | --- |
| DbDataBuild | the model graph (`project show graph`, Mermaid and Graphviz output); **column lineage** from query analysis (`DbDataBuild.Sql/Analysis/QueryAnalyzer.cs`), carried in the metadata documents (`columns[].lineage`: transform, upstream columns); mapped and native models as the sources; loads and their rows in the tracking tables | column (built models), table (native models, copies) |
| DbDataSync | each replication's source and target tables and its column mappings; run history with rows and times | column (mappings) |
| DbDataProxy | its data sources and the tables it reads for each query; with plan 14, the semantic model's measures and dimensions bound to columns | table (pass-through SQL), column (semantic models) |
| Semantic layer (plan 14) | result provenance (`x_provenance`: source, request, overrides), run records | column |
| DbDataStore (plan 15), DbDataApps (plan 16) | not built yet | |
| Monitoring and audit (plan 13 W10, W12) | runs, outcomes, freshness; who changed what | |

## 3. The model

**One graph for the suite**, assembled from what each server knows about its own objects.

**Nodes** (each with a stable identity, section 4):

| Kind | Examples |
| --- | --- |
| dataset | a table or view in a database, a file, a DbDataStore entity, a document's extracted table |
| column | a column of a dataset |
| job | a Sync replication, a Build model's load, a Build copy, an extraction |
| semantic member | a semantic model, its measures and dimensions |
| serving | a Proxy data source, a published feed (plan 17 R2), a semantic package |
| consumer | a DbDataApps app, dashboard or report; an external report server (later); an agent action |

**Edges**, each with a kind, a granularity (`column` or `table`), and where the knowledge came from:

| Edge | From, to |
| --- | --- |
| `replicates` | source dataset (or column) to target, through a Sync replication |
| `derives` | upstream columns to a Build model's column (with the transform kind: identity, expression, aggregate); table level for a native model |
| `copies` | a Build copy's origin to its table |
| `binds` | a column to a semantic measure or dimension |
| `serves` | a dataset or semantic model to a Proxy data source or feed |
| `extracts` | a document to the tables extracted from it |
| `reads` | a consumer (an app, a report, a dashboard view) to what it queries |
| `promotes` | a query's ad hoc table to the DbDataStore entity made from it (plan 15) |

**Two layers**:

- **Design lineage**, from definitions: what *can* flow where (models, mappings, bindings, published queries). Rebuilt when a definition
  changes; cheap and complete for what the definitions say.
- **Run lineage**, from executions: what *did* flow, when and how much (a replication run, a load, a refresh, a semantic query's sources).
  Attached to design edges as their latest runs; this is where freshness and failures come from. **Its history is kept and pruned by the
  same retention policy as the server's other run detail** (plan 13 W10, `DbDataKit.Monitoring`), not by a lineage setting of its own
  (owner, 2026-10-26).

**Confidence**: every edge says how it is known: `declared` (written in a definition), `derived` (computed, as Build's query analysis does),
`observed` (seen in a run). A dataset whose upstream is unknown shows an explicit "unknown upstream" marker, so a reader can tell "nothing
flows here" from "we do not know".

## 4. Identity: the same table must be the same node everywhere

The graph is only useful if Sync's target table, Build's source and Proxy's served table are one node. Identity is built from the
connection, not from each product's names:

- **Dataset identity**: the engine, the server (host and port, normalized), the database, the schema and the table, from the
  `connection/1` definition (plan 13 W8) and the engine registry (plan 14 R5). Two products that name the same server differently (an
  alias, a port spelled out) resolve to one identity through the connection definition, not by string matching.
- **Column identity**: dataset identity plus the column name, as the engine stores it (case rules from the dialect).
- **Suite objects** (a replication, a model, a measure, an entity): the owning server's instance id plus the object's own name.
- The shape follows OpenLineage's `namespace` and `name` (namespace from the engine and server, name from database, schema and table), so
  a later export maps one to one.

## 5. Where it lives

The same pattern as monitoring (plan 13 W10) and the console's other cross-server views:

- **Each server owns the lineage of its own objects**, builds it from its definitions and runs, and serves it through its management API
  as commands on the shared surface (plan 13 W4a): `lineage graph`, `lineage impact <node>`, `lineage provenance <node>`, read-only, Viewer
  role.
- **A shared library, `DbDataKit.Lineage`**: the node and edge model, identities, the per-server store (on `DbDataKit.State`), and the
  graph queries (impact, provenance, path between two nodes), so every server and the console compute them the same way.
- **The console joins the servers' graphs** by identity into one view, pulled like monitoring (the console calls the servers; plan 13 W12
  rule 1), cached in the console's state and refreshed when a server reports a newer version of its graph.
- **Agents** read lineage through the same commands (plan 16): "what would break if I drop this column" is a tool call.

## 6. Visualizations

In the shared visuals package (plan 16 section 2a; plan 19 lane Y), a **graph view** beside the Vega-Lite and matrix views. Vega-Lite has no
graph layout, so the graph view uses a layout library chosen in slice Y04 (candidates: ELK.js, Cytoscape.js, React Flow).

- **Levels of detail**: systems (servers and their databases) to datasets to columns, expanding on demand; a big graph never draws at once.
- **Impact and provenance**: select a node, highlight everything downstream or upstream, with the path's edges labelled by kind.
- **Run overlay**: each job and dataset coloured by its last run and freshness (from `DbDataKit.Monitoring`): failed, late, fresh.
- **Gaps visible**: "unknown upstream" and table-level edges drawn differently from column-level ones.
- **Later, not in the first version: tracing a value.** From a query result or a visual's value (the console's query explorer, a
  dashboard cell), open where it came from: the measures, their columns, the Build models and loads, the Sync replications and the source
  tables (the definition path), and later the rows behind the value (a drill to detail, which needs the semantic engine's help and is
  its own design).
- **Text too**: every view has a list form (and the commands' JSON), so lineage is usable without the drawing and in an agent's answer.

## 7. Slices

Same rules as plan 13a (one pull request each, a "Done when" test, status in the row). Lane notes are in plan 19.

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| G01 | `DbDataKit.Lineage`: nodes, edges (kind, granularity, confidence), design and run layers, the per-server store on `DbDataKit.State` | | M | unit tests: build a graph, store and reload it on SQLite, SQL Server and PostgreSQL | **Status: done** (branch `suite/g01-lineage`, with G02 and G03). New package `DbDataKit.Lineage`: `LineageNode` (kind, name, `UnknownUpstream`, properties), `LineageEdge` (kind, granularity, confidence, detail; the edge runs upstream to downstream), `EdgeRun` (when, outcome, rows), `LineageGraph` (immutable, refuses a missing end, a self edge and a cycle, naming them; `Merge` joins graphs by identity; `Fingerprint`), and `LineageStore` on `DbDataKit.State` (`LineageSchema.Tables`: nodes and edges keyed by hashes of their identities, a version per owner, runs): `Replace` writes nothing when the graph is unchanged and drops the runs of an edge that is gone, `Load`, `Version`, `AddRun` (only along a design edge), `LatestRuns`, `PruneRuns` (for the server's retention of run detail). Tested on SQLite, SQL Server and PostgreSQL |
| G02 | Identity: dataset and column identity from a `connection/1` definition and the engine registry; normalization of server names and ports; OpenLineage-shaped namespace and name | G01 | M | tests: two definitions of one server (an alias, an explicit default port) give one identity; case rules per dialect | **Status: done** (same branch). `LineageIdentity.Dataset(connection, schema, table)` gives the OpenLineage-shaped `namespace/name` (`engine://server` and `database.schema.table`) from a `connection/1` definition (address or connection string): host lower-cased, the machine's own names one, the engine's default port left out, SQL Server's `tcp:` and `host,port` read, a named instance part of the server and its port not, names folded by the dialect's case rule (an engine with no dialect keeps what it is given), a DuckDB file by its path; `Column` and `SuiteObject` (`suite://<instance>/<kind>/<name>`). **Not done:** engine defaults come from a table in the library until the engine registry (plan 19 J06) can supply them |
| G03 | Graph queries: impact, provenance, path; bounded depth; gaps reported | G01 | S | tests over a fixture graph, including cycles refused and unknown upstreams reported | **Status: done** (same branch). `Impact`, `Provenance` (breadth-first, by distance, bounded by a depth that says `Truncated` when it stopped with more to follow) and `Path` (the shortest chain of edges, null when data does not flow that way); a slice reports its `Gaps`, the nodes reached whose upstream is unknown, so "nothing flows here" and "we do not know" stay different. A cycle is refused when the graph is built |
| G04 | The lineage commands on the shared surface (`lineage graph`, `lineage impact`, `lineage provenance`) with JSON schemas, served by any host | G03, plan 13 C01 | S | command tests: the documents validate against their schemas | **Status: done** (branch `suite/g04-lineage-commands`). `DbDataKit.Lineage.Cli`: `LineageNoun.Create(source)` gives the `lineage` noun for any `NounRouter` (so also `--format json` and `ui mcp`), over a function that returns the host's `LineageGraph`. `graph [--kind]`, `impact <node> [--depth]`, `provenance <node> [--depth]`; each declares its data schema, the documents are validated against the router's output schema in 7 tests; an unknown node is exit 1, bad use exit 2. No product uses it yet (G05, G07 and G08 do) |
| G05 | Build: design lineage from the model graph and the metadata documents' column lineage; table level for native models and copies | G02, G04 | M | a template project's lineage matches its `project show graph --columns` (test) | **Status: done** (branch `suite/g05-build-lineage`, with G06). `BuildLineage.Design` (Build CLI project) emits the model graph and the query analysis's column lineage (the same facts the metadata documents and `project show graph --columns` carry) as `DbDataKit.Lineage` nodes and edges: a dataset per model on each of its connections, table-level `derives` edges (derived from queries, declared from a native model's `reads:`), `copies` edges (declared), and column-level `derives` edges with the transform as detail for built models that are not copies. A mapped model, an unknown table, a native model without `reads:`, and the columns of a native model, a copy or a mapped model are marked unknown upstream. Identity is `LineageIdentity` from the address in the login's connection string (server, port, database only); a connection with no login in the environment gets a placeholder host of its own name so nothing joins wrongly. Test: every template's lineage equals its `project show graph --columns` (copies excepted: their column edges in the graph come from the tool's own staging table). The `lineage` noun (`graph`, `impact`, `provenance`) is on Build's **server** command line (`dbdatabuild lineage ... [--project <id>] [--repo <server directory>]`, `ServerLineage`): one graph merged over every hosted project (so the same table in two projects is one node), `--project` narrows it (an unknown id is refused naming the hosted ones), a server with no project has an empty graph, and the connections' addresses come from the logins the server stores. By decision the per-project command line stays unwired. **Not done:** run lineage is not in the server's graph (it needs the tracking database); a job node per load; copies at column level |
| G06 | Build: run lineage from the tracking tables (loads, rows, times) | G05 | S | after a refresh in the conformance suite, each load edge carries its last run | **Status: done** (same branch). `AuditLog.LastLoadsAsync` reads the last run of each model from `run_log` (its non-hook steps of the newest run id: end time, `ok` or failed, rows) and `BuildLineage.Runs` lays it as an `EdgeRun` along each table-level edge into the model. Conformance test `After_a_refresh_each_load_edge_carries_its_last_run_from_the_tracking_tables` (SQL Server and PostgreSQL) is written and compiles; **not run**, as no container engine was available. **Not done:** runs on column edges; a model that reads nothing has no load edge to carry its run
| G07 | Sync: design lineage from replications and column mappings; run lineage from run history | G02, G04 | M | a replication's mapping appears as column edges with the last run's rows (test) | **Status: done as a library; not yet reachable** (branch `suite/g07-sync-lineage`). `SyncLineage.Design` (`DbDataSync.Core`) lays each table mapping as `replicates` edges from its source tables to its target tables (declared; the detail is `replication/mapping`) and each column mapping as a column edge with its transform as the detail, in the identity `LineageIdentity` gives a table, so a table Sync writes and the one a Build model reads are one node. A source that is a hand-written query is a node of its own with an unknown upstream; a mapping whose connection is not configured is left out and said in the notes; without column mappings a mapping is table-level only. `SyncLineageRuns.Runs` (`DbDataSync.State`) lays each mapping's newest finished run (primary or bulk load) along its table edges: when it ended, whether it succeeded, the rows written. 3 tests (the edges and identities, the query source and the missing connection, the run layer with a failure and a queued run). **Not done:** the `lineage` noun in Sync's command line and an endpoint on its API (G09 pulls the graphs through the API), keeping the graph in a `LineageStore` between runs, and the target table's own upstream when Sync is not its only writer. |
| G08 | Proxy: design lineage for data sources and the tables they serve; run lineage aggregated per table (read count, last read, the clients that read it; never per query: a per-query record belongs to Proxy's activity log, plan 13a P09, under its retention, and the table node links to it) | G02, G04 | S | after queries, the served tables show their last use (test) |
| G09 | Console: pull and join the servers' graphs by identity; cache and refresh | G04, plan 13 U08 | M | a Sync target and a Build source on one table are one node in the console (test with two paired servers) |
| G10 | Console: the graph view (systems to datasets to columns), impact and provenance highlighting, run overlay | G09, plan 19 Y04, plan 13 M11 | M | Playwright: open a table, see its upstream to the source, its downstream to a Build model; a failed run is marked |
| G11 | Semantic layer: measure and dimension bindings as `binds` edges; the semantic models served by Proxy as `serves` edges | G05, plan 19 J12 | S | the console's graph shows a measure's path from the source table through the Sync replication and the Build model (test) |
| G12 | DbDataStore and DbDataApps emitters (entities, documents, extractions, promotions; apps and dashboards as consumers), when those products exist | G04, plan 19 Z03 | S each | per product, its objects appear in the console's graph |

Later, not in the first version: tracing a value from a result or a visual (section 6), then the rows behind it; manual lineage entries for external systems (a lineage file or console form, as `declared` edges with an
owner), and export to OpenLineage (events from run lineage, datasets and jobs from design lineage) and to external catalogs.

## 8. Decisions

All decided by the owner on 2026-10-26:

- Run lineage history follows the servers' retention policy for run detail (section 3).
- Tracing a value back from a result or a visual is a later feature (section 6).
- Proxy's run lineage is a summary per served table (G08). A per-query record, if wanted, belongs in Proxy's activity log, not in lineage.
- The graph view's layout library: no preference. It is chosen in slice Y04 by the comparison described there.
