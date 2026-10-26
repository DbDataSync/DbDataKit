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
  Attached to design edges as their latest runs, with a short history; this is where freshness and failures come from.

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
- **Trace this number**: from a semantic query's result (the console's query explorer, a dashboard cell), open its provenance: the
  measures, their columns, the Build models and loads, the Sync replications and the source tables.
- **Text too**: every view has a list form (and the commands' JSON), so lineage is usable without the drawing and in an agent's answer.

## 7. Slices

Same rules as plan 13a (one pull request each, a "Done when" test, status in the row). Lane notes are in plan 19.

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| G01 | `DbDataKit.Lineage`: nodes, edges (kind, granularity, confidence), design and run layers, the per-server store on `DbDataKit.State` | | M | unit tests: build a graph, store and reload it on SQLite, SQL Server and PostgreSQL |
| G02 | Identity: dataset and column identity from a `connection/1` definition and the engine registry; normalization of server names and ports; OpenLineage-shaped namespace and name | G01 | M | tests: two definitions of one server (an alias, an explicit default port) give one identity; case rules per dialect |
| G03 | Graph queries: impact, provenance, path; bounded depth; gaps reported | G01 | S | tests over a fixture graph, including cycles refused and unknown upstreams reported |
| G04 | The lineage commands on the shared surface (`lineage graph`, `lineage impact`, `lineage provenance`) with JSON schemas, served by any host | G03, plan 13 C01 | S | command tests: the documents validate against their schemas |
| G05 | Build: design lineage from the model graph and the metadata documents' column lineage; table level for native models and copies | G02, G04 | M | a template project's lineage matches its `project show graph --columns` (test) |
| G06 | Build: run lineage from the tracking tables (loads, rows, times) | G05 | S | after a refresh in the conformance suite, each load edge carries its last run |
| G07 | Sync: design lineage from replications and column mappings; run lineage from run history | G02, G04 | M | a replication's mapping appears as column edges with the last run's rows (test) |
| G08 | Proxy: design lineage for data sources and the tables they serve; run lineage aggregated per table (counts and last use, not every query) | G02, G04 | S | after queries, the served tables show their last use (test) |
| G09 | Console: pull and join the servers' graphs by identity; cache and refresh | G04, plan 13 U08 | M | a Sync target and a Build source on one table are one node in the console (test with two paired servers) |
| G10 | Console: the graph view (systems to datasets to columns), impact and provenance highlighting, run overlay | G09, plan 19 Y04, plan 13 M11 | M | Playwright: open a table, see its upstream to the source, its downstream to a Build model; a failed run is marked |
| G11 | Semantic layer: measure and dimension bindings as `binds` edges; a semantic query's sources as run lineage; "trace this number" | G05, plan 19 J14 | M | Playwright: from a result cell to the source table through the measure, the Build model and the Sync replication |
| G12 | DbDataStore and DbDataApps emitters (entities, documents, extractions, promotions; apps and dashboards as consumers), when those products exist | G04, plan 19 Z03 | S each | per product, its objects appear in the console's graph |

Later, not in the first version: manual lineage entries for external systems (a lineage file or console form, as `declared` edges with an
owner), and export to OpenLineage (events from run lineage, datasets and jobs from design lineage) and to external catalogs.

## 8. Questions for the owner

1. Run lineage history: how long to keep it per edge (the last run only, a fixed number of runs, or a time window)?
2. Proxy's run lineage: aggregated per table (recommended, G08) or per query for audit?
3. The graph view's layout library: decided in slice Y04 by a small comparison, unless there is a preference.
