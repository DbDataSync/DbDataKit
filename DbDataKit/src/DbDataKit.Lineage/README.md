# DbDataKit.Lineage

The lineage graph of the suite (plan 18, G01-G03), built by each server from what it knows about its own objects and joined by identity in the console.

- **Nodes** (`LineageNode`): a dataset, a column, a job, a semantic member, a serving object, a consumer. A node says when its upstream is unknown (`UnknownUpstream`), so "nothing flows here" and "we do not know" are different.
- **Edges** (`LineageEdge`): `replicates`, `derives`, `copies`, `binds`, `serves`, `extracts`, `reads`, `promotes`, each at `column` or `table` granularity and each `declared`, `derived` or `observed`. The graph refuses a cycle.
- **Two layers**: design lineage is what a definition says can flow (replaced whole when a definition changes); run lineage (`EdgeRun`) is what did flow, kept per edge and pruned by the server's retention.
- **Identity** (`LineageIdentity`): a dataset or column is named from its `connection/1` definition (engine, normalized server, port, database, schema, table; the dialect's case rule), in the shape of an OpenLineage namespace and name, so two products that spell one server differently still meet in one node.
- **Store** (`LineageStore`, `LineageSchema.Tables`): per server, on `DbDataKit.State` (SQLite, SQL Server, PostgreSQL), with a version that changes whenever the design graph changes.
- **Queries** (`LineageQueries`): `Impact` (downstream), `Provenance` (upstream) and `Path`, each bounded in depth, reporting the gaps they ran into.
