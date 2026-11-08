# Lineage across the suite

Every dependency and data flow in the suite can be traced both ways: **downstream, for impact** (what reads this column, table, measure or entity, and what breaks if it changes) and **upstream, for provenance** (where a number or a row came from). The library is `DbDataKit.Lineage` (README beside the code); the graph view's choice of library is in [lineage-graph-view.md](lineage-graph-view.md).

The rules of the first version, decided by the owner:

- **Best effort.** Column level wherever a product knows it, table level where column level is too complicated or unavailable. **A gap is shown as a gap, never filled by a guess.**
- Manual lineage entries, export to OpenLineage and tracing a value back from a result are later features, not built. The model maps onto OpenLineage so export is an addition, not a redesign.

## What each product contributes

| Product | What goes in the graph | Built |
| --- | --- | --- |
| DbDataBuild | the model graph; column lineage from query analysis (`QueryAnalyzer`, the same facts as the metadata documents' `columns[].lineage`); native models and copies at table level; semantic members bound to columns; last load of each model from the tracking tables | `BuildLineage` (design, semantic `binds`, runs) |
| DbDataSync | each replication's table mapping as `replicates` edges and column mappings as column edges, with the last run's rows | `SyncLineage`, `SyncLineageRuns` |
| DbDataRunner | the deployment graph of its group: package, version, channel, group, instance, runtime, and the runs that name a version | `RunnerDeploymentLineage` |
| DbDataProxy, DbDataStore, apps | nothing yet | |

## The model

**Nodes** (`NodeKind`): `dataset` (a table or view, a file, a Store entity, an extracted table), `column`, `job` (a replication, a load, a copy, an extraction), `semantic_member`, `serving` (a data source, a feed, a semantic package), `consumer` (an app, a dashboard, a report, an agent action) and `deployment` (its `deployment` property says package, version, channel, group, instance, runtime or run). A node says when its upstream is unknown (`UnknownUpstream`), so "nothing flows here" and "we do not know" are different.

**Edges** (`EdgeKind`, running upstream to downstream, each at `column` or `table` granularity): `replicates` (source to target through a Sync replication), `derives` (upstream columns to a Build model's column, with the transform as detail; table level for a native model), `copies` (a Build copy's origin to its table), `binds` (a column to a semantic measure or dimension), `serves` (a dataset or semantic model to a data source or feed), `extracts` (a document to its extracted tables), `reads` (a consumer to what it queries), `promotes` (an ad hoc table to the Store entity made from it), `deploys` (package to versions, a version to the channel that serves it, a channel to the instance that holds it, a group or runtime to its instances) and `runs` (a deployed version to a run that named it).

**Confidence**: each edge is `declared` (written in a definition), `derived` (computed, as Build's analysis does) or `observed` (seen in a run).

**Two layers.** Design lineage comes from definitions and is replaced whole when a definition changes. Run lineage (`EdgeRun`: when, outcome, rows) is attached to design edges, only along an existing design edge; its history is meant to follow the server's retention of other run detail (`PruneRuns` takes that retention; which server calls it was not verified). The graph refuses a missing end, a self edge and a cycle, naming them.

## Identity

The same table must be one node everywhere. `LineageIdentity.Dataset(connection, schema, table)` gives the OpenLineage-shaped `namespace` (`engine://server`) and `name` (`database.schema.table`) from a `connection/1` definition: host lower-cased, the machine's own names one, the engine's default port left out, SQL Server's `tcp:` and `host,port` read, a named instance part of the server, names folded by the dialect's case rule, a DuckDB file by its path. Columns add the column name; suite objects (a replication, a model, a measure, an entity) are `suite://<instance>/<kind>/<name>`. A connection with no login in the environment gets a placeholder host of its own name so nothing joins wrongly. Engine default ports come from a small table in the library, not yet from the engine registry's `default_port`.

## Where it lives

- **Each server owns the lineage of its own objects**, builds it from its definitions and runs, and serves it: `GET /api/v1/lineage` on a Build server (all hosted projects as one graph, with what was left out in `problems`) and on a Runner, `GET /api/lineage` on a Sync server, all viewer role, in one strict wire form (`LineageJson`; a document of another shape or with an unknown kind is refused saying where). The wire carries design lineage only.
- **Commands** on the shared surface: `DbDataKit.Lineage.Cli` gives the `lineage` noun (`graph [--kind]`, `impact <node> [--depth]`, `provenance <node> [--depth]`, each with a declared schema, so also `--format json` and `ui mcp`). It is mounted on Build's server command line and on the Runner's. It is not mounted on Sync's command line.
- **Queries** (`LineageQueries`): `Impact`, `Provenance` (breadth-first, by distance, bounded; `Truncated` says it stopped with more to follow), `Path` (the shortest chain, null when data does not flow that way), and the `Gaps` reached, so agents ask "what would break if I drop this column" with the same answers the console gives.
- **The console joins** (`ConsoleLineage`): `GET /api/lineage` asks every reachable Build, Sync and Runner server through the relay as the signed-in person, keeps an answer 30 seconds (`?refresh=true` asks again), joins the graphs by identity and returns it with what each server said (its size, its problems, or why it gave none). A failing server is named and left out; graphs that together would make data flow in a circle are said, not hidden. `?node=&direction=downstream|upstream&depth=` is impact or provenance across all servers.
- **A store** (`LineageStore`, the `lineage` state module: nodes and edges keyed by hashes of their identities, a version per owner, runs; SQLite, SQL Server, PostgreSQL) exists in the library and is tested; no server keeps its graph in it yet, each builds the graph on request.

## Visualizations

The **Lineage** page of the console (`/lineage`) draws the joined graph with `LineageGraphView` in `@dbdatakit/visuals` (React Flow laid out by dagre). It starts at table level and a dataset opens to its columns; a search by name or id selects a node and marks what feeds it on one side and what it feeds on the other, with the rest dimmed; servers are listed under it with what they left out. Unknown upstream and table-level edges are drawn apart from known and column-level ones, and every view has a list form and the commands' JSON.

## Decisions

- Run lineage history follows the servers' retention of run detail, not a setting of its own.
- Tracing a value back from a result or a visual is later.
- The Proxy's run lineage is a summary per served table, never per query; a per-query record belongs in the Proxy's activity log.
- The graph library: React Flow with dagre; ELK is kept as an option if needed ([lineage-graph-view.md](lineage-graph-view.md)).
