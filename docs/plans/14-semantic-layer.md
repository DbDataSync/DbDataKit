# Plan 14: the semantic layer, what is left

Status: **in progress** (audited 2026-11-09). What is built (semantic models in Build projects, publishing to a Gateway and hosting there, the packages, the engine registry, semantic SQL, parameters, saved queries of a model, SSAS) is described in [docs/semantic/architecture.md](../semantic/architecture.md), [DbDataBuild/docs/semantic-models.md](../../DbDataBuild/docs/semantic-models.md) and the Gateway's README ("Semantic queries"). This plan holds the unbuilt parts, **checked against the code on 2026-11-09 in [plan 14a](14a-semantic-slices.md)**, which has every item's evidence, the slices and the questions. **MDX and XMLA for Excel PivotTables and the extra protocols (Postgres wire, Quack) are low priority and deferred (owner, 2026-11-09): sliced as SL40 to SL55 in 14a, not scheduled, and nothing else depends on them.** The old slice list (plan 19's J rows, deleted 2026-11-09 with the finished lanes) is mapped, row by row, in section 2 of 14a: what was built is in the documents named above, and every J row with work left is an SL slice there.

## Left to build

| Item | What is left | Status (2026-11-09) | Where it is sliced |
| --- | --- | --- | --- |
| R4 console | the semantic module of the console: model browser (cubes, dimensions, measures with lineage back to Build columns), query explorer with explain, matrix and chart views from the presentation specs, review of ad hoc maps proposed for promotion | **built 2026-11-11 except review:** the model browser (SL13), the query explorer with run, explain, describe, matrix and chart drawn by the Gateway's `ViewRenderer`, saved queries and recent runs (SL14 to SL18, SL23, SL24); open: promotion of a saved query (SL19), the Build editor of `queries:` (SL20), the review of ad hoc maps (SL25, SL26). Was: the Gateway's Models screen listed published models and versions only. The explorer can run requests through the views module's preview, so it need not wait for J28 (question Q1 of 14a) | 14a SL12 to SL16 (J14 split), SL25 and SL26 for the review |
| R4 agents in Build | mounting the `semantic` noun in Build (its own connection to a Gateway); a token instead of a password | partly built 2026-11-11: the Gateway's noun takes a token (SL21, built); Build does not mount the noun yet (SL22) | 14a SL21, SL22 |
| R1 gaps | `references:` on built models is `foreign_keys:` (J29, **built**); the metadata documents do not carry the semantic models or the physical name per connection; a check that every mapped table and column exists when a package is served (**tables are checked, columns are not**); pruning old versions | partly | 14a SL10 (metadata, key origin), SL06 (columns), SL07 (pruning) |
| R2 state | semantic state in the Gateway's store (ad hoc registrations, run records, clarify sessions); the child process for the combine if the in-process limits prove too soft | partly built 2026-11-11: run records (`semantic_runs`, SL23) are built; ad hoc registrations (SL25, SL26) and the combine in a child process (SL27) are not (a person's saved queries and the versions of a model are in the state; clarify needs none) | 14a SL23 to SL27 (SL25 to SL27 wait for the owner, Q5; SL27 is conditional) |
| R2a leftovers | a command-line form to publish a model, rollback from Build, a model on engines other than SQL Server | not started (low priority); a model on PostgreSQL is built | 14a SL30 (was J12b, low priority); other engines: 14a Q13 |
| R5 | the products read the engine registry (Sync's `DriverType`, Build's `engine` keywords, the Gateway's engine names) and their README support tables are generated from it | partly: the registry and `docs/engine-support.md` are built; no product reads it, and three role statuses are older than the code | 14a SL01 to SL05 |
| R7 | MDX and XMLA for Excel PivotTables (below) | **deferred, low priority (owner, 2026-11-09)**; only the recording forwarder is built, never run against Excel | 14a SL40 to SL47; plan 20 O16 |
| R8 | more protocols (below) | **deferred, low priority (owner, 2026-11-09)** | 14a SL50 to SL55; `docs/bi-tools-on-semantic-models.md` R4 |

Also found by the audit (not in this table before): the descriptors' statuses (SL01), types in `describe` and decimals in the answer (SL08, SL09), a form for the `semantic:` settings (SL11), saved queries of a person by API and screen (SL17 to SL20) and the SSAS executor's gaps (SL28, SL29).

## R7. MDX, enough for Excel PivotTables

**Deferred: low priority, owner 2026-11-09.** The decisions below stand; nothing is scheduled (slices SL40 to SL47 of [14a](14a-semantic-slices.md)).

An Excel PivotTable on an OLAP source connects with the Analysis Services provider over **XMLA** (SOAP over HTTP or HTTPS): `Discover` for schema rowsets, `Execute` for MDX, sessions, cell sets. Excel generates a narrow, recognizable set of MDX shapes (`NON EMPTY Hierarchize(AddCalculatedMembers(DrilldownLevel({...})))` on axes with `DIMENSION PROPERTIES`, filters as a sub-select or a `WHERE` slicer, drill-down, member searches). No .NET XMLA server exists to reuse.

Decided: **translate that MDX into semantic requests; do not build a general MDX engine.**

- The semantic model already has most of what MDX needs (cubes, dimensions, hierarchies with levels, sort keys, measures with rollup rules). To add: an `All` member and member unique names per hierarchy, measure groups, display folders and format strings on measures.
- An MDX parser for the subset produces the axes, the slicer and the sub-select filters, then one semantic request per cell set, then the result reshaped into the cell set Excel expects. Anything outside the subset is an XMLA error with a clear message, logged with the request's shape (values removed), never answered approximately. The parser matches the parsed tree, not text.
- **Excel's own traffic is the specification, catalogued once on a Windows VM with Excel** (the owner can set one up). The recording forwarder (`Shared/tools/DbDataKit.Tools.XmlaForwarder`, J20) is built and not yet run against SSAS. Steps: record Excel's requests against the QIS Tabular model while a script of PivotTable actions runs by hand (fields on rows, columns, values and filters; expand and collapse; sort; filter by label and value; search; refresh; with and without totals; each available Excel); the recordings become golden fixtures; in CI each recorded request goes to the XMLA endpoint and to SSAS and the two cell sets must agree.
- In order: (1) `Discover` rowsets so Excel lists the fields (`DISCOVER_PROPERTIES`, `DBSCHEMA_CATALOGS`, `MDSCHEMA_CUBES`, `_DIMENSIONS`, `_HIERARCHIES`, `_LEVELS`, `_MEASURES`, `_MEMBERS`, `_PROPERTIES`, `_SETS`); (2) rows, columns and values with subtotals and grand totals; (3) filters, drill-down, sorting; (4) member search, named sets, measures Excel calculates itself. Write-back, drill-through and user-defined calculated members are out of scope.
- Authentication is what Excel can do over HTTP: Windows (Negotiate) or Basic over TLS, resolved to the serving layer's identities, so every PivotTable query carries a user (plan 20 O16).
- Risk: MDX scope creep. The subset is defined by recorded Excel traffic; other clients are out of scope until asked for.

## R8. More protocols

**Deferred: low priority, owner 2026-11-09.** The decisions below stand; nothing is scheduled (slices SL50 to SL55 of [14a](14a-semantic-slices.md)).

All protocols are front ends to one query core: an adapter turns wire messages into a SQL pass-through query, a semantic request or an MDX request, maps its authentication onto the serving layer's identities, and returns rows. It never opens a connection, applies a limit or formats an error itself. The adapters are shared code (`DbDataKit.Serving.*`) written against the host interface; the layer is in plan 20.

| Protocol | Decision |
| --- | --- |
| Postgres wire (v3) | **Next after Flight SQL.** Semantic SQL only, not pass-through (a Postgres client writing Postgres SQL against a SQL Server source is a dialect mismatch). The protocol is small (simple and extended query, SCRAM-SHA-256, TLS); the long tail is `pg_catalog` and `information_schema` emulation, answered tool by tool with recorded fixtures. Every member also has a flat column name (`customer_region`, separator configurable, `flat_name` to resolve collisions). No .NET Postgres server library exists |
| XMLA | with the MDX work (R7) |
| Quack (DuckDB 2.0 client-server) | **deferred** until DuckDB publishes a server specification and a stability promise. The cheaper route then may be a DuckDB child process serving Quack over views of semantic results, behind the serving layer's identities |

## Open questions

- Whether the combine needs its child process (decided 2026-10-29: limits in process first; revisit if they prove too soft; SL27, conditional).
- Which BI tools' Postgres introspection is catalogued first (`docs/bi-tools-on-semantic-models.md` R4 says Power BI Report Server is not a Postgres-wire client). Deferred with R8; 14a D32 has the recommended default.
- The questions of the audit are all decided (14a section 3, D18 to D33): the owner answered the decimals question (D33), the others took the default (owner delegated, 2026-11-09).
