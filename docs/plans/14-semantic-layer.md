# Plan 14: the semantic layer, what is left

Status: **in progress**. What is built (semantic models in Build projects, deploy and hosting through the Proxy, the packages, the engine registry, semantic SQL) is described in [docs/semantic/architecture.md](../semantic/architecture.md). This plan holds only the unbuilt parts. The slice list is plan 19's J rows ([19-next-wave.md](19-next-wave.md)); the mapping from the old Q steps is in the rows' "Q" column.

## Left to build

| Item | What is left | Where it is sliced |
| --- | --- | --- |
| R4 console | the semantic module of the console: model browser (cubes, dimensions, measures with lineage back to Build columns), query explorer with explain, matrix and chart views from the presentation specs, review of ad hoc maps proposed for promotion. The console reaches a Proxy only through its management API; queries run over Flight SQL, which its back end does not speak | plan 19 J14 (waits on J28) |
| R4 agents in Build | mounting the `semantic` noun in Build (its own connection to a Proxy); a token from an identity provider instead of a password | plan 19 J15 |
| R1 gaps | `references:` on built models is `foreign_keys:` (J29); the metadata documents do not carry the semantic models or the physical name per connection; a check that every mapped table and column exists when a package is served (today a test query per data source); pruning old versions | plan 19 J08, J13, J29 |
| R2 state | semantic state in the Proxy's store (ad hoc registrations, run records, clarify sessions); the child process for the combine if the in-process limits prove too soft | plan 19 J11 |
| R2a leftovers | a command-line form to publish a model, rollback from Build, a model on engines other than SQL Server | plan 19 J12b (low priority) |
| R5 | the products read the engine registry (Sync's `DriverType`, Build's `engine` keywords, the Proxy's `Provider`) and their README support tables are generated from it | plan 19 J06 |
| R7 | MDX and XMLA for Excel PivotTables (below) | plan 19 J20; plan 20 `xmla` module, O16 |
| R8 | more protocols (below) | plan 20 `pgwire` module; plan 17 R4 |

## R7. MDX, enough for Excel PivotTables

An Excel PivotTable on an OLAP source connects with the Analysis Services provider over **XMLA** (SOAP over HTTP or HTTPS): `Discover` for schema rowsets, `Execute` for MDX, sessions, cell sets. Excel generates a narrow, recognizable set of MDX shapes (`NON EMPTY Hierarchize(AddCalculatedMembers(DrilldownLevel({...})))` on axes with `DIMENSION PROPERTIES`, filters as a sub-select or a `WHERE` slicer, drill-down, member searches). No .NET XMLA server exists to reuse.

Decided: **translate that MDX into semantic requests; do not build a general MDX engine.**

- The semantic model already has most of what MDX needs (cubes, dimensions, hierarchies with levels, sort keys, measures with rollup rules). To add: an `All` member and member unique names per hierarchy, measure groups, display folders and format strings on measures.
- An MDX parser for the subset produces the axes, the slicer and the sub-select filters, then one semantic request per cell set, then the result reshaped into the cell set Excel expects. Anything outside the subset is an XMLA error with a clear message, logged with the request's shape (values removed), never answered approximately. The parser matches the parsed tree, not text.
- **Excel's own traffic is the specification, catalogued once on a Windows VM with Excel** (the owner can set one up). The recording forwarder (`DbDataKit/tools/DbDataKit.Tools.XmlaForwarder`, plan 19 J20) is built and not yet run against SSAS. Steps: record Excel's requests against the QIS Tabular model while a script of PivotTable actions runs by hand (fields on rows, columns, values and filters; expand and collapse; sort; filter by label and value; search; refresh; with and without totals; each available Excel); the recordings become golden fixtures; in CI each recorded request goes to the XMLA endpoint and to SSAS and the two cell sets must agree.
- In order: (1) `Discover` rowsets so Excel lists the fields (`DISCOVER_PROPERTIES`, `DBSCHEMA_CATALOGS`, `MDSCHEMA_CUBES`, `_DIMENSIONS`, `_HIERARCHIES`, `_LEVELS`, `_MEASURES`, `_MEMBERS`, `_PROPERTIES`, `_SETS`); (2) rows, columns and values with subtotals and grand totals; (3) filters, drill-down, sorting; (4) member search, named sets, measures Excel calculates itself. Write-back, drill-through and user-defined calculated members are out of scope.
- Authentication is what Excel can do over HTTP: Windows (Negotiate) or Basic over TLS, resolved to the serving layer's identities, so every PivotTable query carries a user (plan 20 O16).
- Risk: MDX scope creep. The subset is defined by recorded Excel traffic; other clients are out of scope until asked for.

## R8. More protocols

All protocols are front ends to one query core: an adapter turns wire messages into a SQL pass-through query, a semantic request or an MDX request, maps its authentication onto the serving layer's identities, and returns rows. It never opens a connection, applies a limit or formats an error itself. The adapters are shared code (`DbDataKit.Serving.*`) written against the host interface; the layer is in plan 20.

| Protocol | Decision |
| --- | --- |
| Postgres wire (v3) | **Next after Flight SQL.** Semantic SQL only, not pass-through (a Postgres client writing Postgres SQL against a SQL Server source is a dialect mismatch). The protocol is small (simple and extended query, SCRAM-SHA-256, TLS); the long tail is `pg_catalog` and `information_schema` emulation, answered tool by tool with recorded fixtures. Every member also has a flat column name (`customer_region`, separator configurable, `flat_name` to resolve collisions). No .NET Postgres server library exists |
| XMLA | with the MDX work (R7) |
| Quack (DuckDB 2.0 client-server) | **deferred** until DuckDB publishes a server specification and a stability promise. The cheaper route then may be a DuckDB child process serving Quack over views of semantic results, behind the serving layer's identities |

## Open questions

- Whether the combine needs its child process (decided 2026-10-29: limits in process first; revisit if they prove too soft).
- Which BI tools' Postgres introspection is catalogued first (plan 17 R4 says Power BI Report Server is not a Postgres-wire client).
