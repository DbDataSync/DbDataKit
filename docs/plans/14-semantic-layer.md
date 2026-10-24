# Plan 14: the semantic layer (Query Intent Specs) in the suite

Status: **proposed for review** (2026-10-24). **Not part of plan 13's work list** (`13a-suite-slices.md`): nothing here is to be
implemented until the owner decides the questions in section 9. Reviewed: `DbDataSync/QueryIntentSpec` at `139bbf9` (specs in
`qis/docs/`, the C# engine in `qis/dotnet/`), against this repository at the head of `claude/monorepo-standardization-plan-2sq3l0`.

## 1. What QueryIntentSpec is today

- **A set of specs** (core, catalog, semantic query, combine, extraction, presentation) and **one C# engine** (.NET 10, about 7,400
  lines in `Qis.Contracts`, `Qis.Engine`, `Qis.Ssas`) with **209 conformance fixtures** across adoption levels 0 to 5.
- **The catalog** is a semantic model: an Apache Ossie-shaped physical model (datasets, fields, relationships) plus a QIS layer
  (sources, logical dimensions bound per source, structured measures with rollup/spread/grain semantics, derived measures,
  hierarchies, calendars, segments). Import from and export to Ossie are built.
- **A semantic request** is what an agent writes: measures, dimensions, filters, time ranges, overrides, transforms, totals. The engine
  validates it, plans it, emits **T-SQL** (SQL Server) and **DAX** (SSAS) per source, and combines the per-source results **in DuckDB**
  (union, spread, rollup through relations, allocate, transforms, totals). Whatever a level cannot execute comes back as `remaining`
  steps that the agent completes with sandboxed DuckDB SQL; completed runs are saved and replay without the agent.
- **Ad hoc data:** registered tables, bindings to catalog dimensions, value maps (catalog, fuzzy Jaro-Winkler, LLM-supplied pairs),
  bridges and weighted relations. **Extraction** turns spreadsheets (ClosedXML) into described tables. **Presentation** turns results
  into matrix grids and Vega-Lite specs.
- **Execution:** `ISourceExecutor` with a DuckDB stand-in (runs an equivalent DuckDB query over seed data), `SqlServerExecutor`
  (`Microsoft.Data.SqlClient`, a raw connection string), and `SsasExecutor` (ADOMD.NET, Windows only, never run against a server).
- **Pins:** `DuckDB.NET.Data.Full` 1.1.1, `Microsoft.Data.SqlClient` 6.1.2, `YamlDotNet` 16.1.3, `JsonSchema.Net` 7.2.3, `ClosedXML`.

It is a well-specified semantic layer and federation engine for agents, about where Cube, MetricFlow or a Power BI semantic model sit,
plus a mashup engine for ad hoc tables that those products do not have.

## 2. Where it fits

The suite already covers the stages around a semantic layer, and lacks only that layer:

```
  sources ──► DbDataSync ──► warehouse ──► DbDataBuild ──► marts (star schemas) ──► semantic layer (QIS) ──► agents, BI, console
            (replicate)                  (model, deploy,      + metadata documents      on DbDataProxy           (Flight SQL, MCP,
                                          refresh, test)                                (query service)           matrix and charts)
```

- **DbDataBuild produces exactly what a QIS catalog's physical layer describes**: tables with declared columns, logical types,
  nullability and grain, per connection, with column lineage, titles, descriptions, tags and properties, published as metadata
  documents (`project show metadata`, `connection publish`, `schemas/metadata.schema.json`). Mapped models describe tables Build does not
  build (an enterprise SQL Server star), with primary keys, foreign keys and indexes imported from the live catalog.
- **DbDataProxy is already the suite's query service**: it holds the connections, the identities, read-only enforcement, limits and the
  failure model, and speaks Flight SQL to clients. A semantic query is another kind of query it can answer.
- **The console** (plan 13, W5) is where a person would browse the model, run a semantic query, see a matrix or chart, and review
  promotions of ad hoc maps to governed ones.

## 3. Recommendations

### R1. Semantic models live in DbDataBuild projects, on top of Build's outputs

- **Authoring.** A Build project gets a `semantic/` folder: one or more QIS catalogs, written in the QIS authoring format **without the
  physical layer**. Datasets name Build models (`marts.fct_sales`, a mapped `erp.dim_customer`), not physical tables.
- **The physical layer is generated, never written.** `datasets`, `fields`, `primary_key` and `relationships` come from Build's metadata:
  columns and logical types from the model definitions, `primary_key` from `grain`, relationships from declared references, the physical
  table name from the model's target on each connection. A QIS `source` is a Build connection (and so a `connection/1` definition, plan 13 W8).
  This removes the largest hand-written, drift-prone part of the catalog.
- **Compile checks it.** `project compile` loads the semantic files with QIS's validator and Build's model graph: every dimension
  binding and measure `of` names a real column of a real model, types agree (`binding_type_mismatch`), relationships are many-to-one by
  declared grain, measures' native `grain` names dimensions their fact can reach. Errors are Build diagnostics (new `DDB-6xx` codes, with
  fixtures, per Build's rules).
- **Data checks become Build tests.** QIS checks hierarchy cardinality and relationship keys on the data (`CheckHierarchies`, "run by an
  operator or a scheduled job"). Build already runs tests after a refresh and has a scheduler and monitoring coming (plan 13 W10): generate
  a project test per hierarchy level pair and per relationship (orphan keys), so a broken hierarchy is caught when the data changes, not
  when a query is wrong.
- **Deploy publishes it.** `connection deploy` / `connection publish` store the compiled semantic model (physical layer filled in for that
  connection) with the project's other metadata, versioned with the deployed commit. The tables and their semantic model come from one
  commit, so they cannot drift apart.
- **Calendars are a Build model.** QIS calendars need a date table with level columns and prior-period counterpart columns. Ship a Build
  template or macro that builds that table (fiscal levels, 4-4-5 if asked, `*_prior_year` columns), so a calendar is a few lines in the
  semantic file instead of a hand-maintained table.
- **Sources Build does not model** (SSAS, a database nobody imported) stay possible: a semantic file may declare a source and datasets by
  hand, as QIS does today. Mapped models are the preferred way for any SQL source, because import keeps them in step with the database.

What Build needs for this (gaps found):

| Gap | Today | Needed |
| --- | --- | --- |
| Relationships on built models | `foreign_keys:` exist on mapped models only (imported, never created) | a declared `references:` (or `foreign_keys:`) on built models' columns, verified by a generated test, not created in the database |
| Measures | none (Build has no measures, by design) | stay in the semantic files; Build validates them, it does not own them |
| Semantic files in the project schema | none | `schemas/semantic.schema.json`, the strict YAML loader, fuzz tests, `project show semantic` |
| Metadata documents carry the physical name per connection | partly (`connections`, `files`) | the resolved table name per connection, for the generated `source:` |

### R2. DbDataProxy serves semantic queries

- **QIS sources are Proxy data sources.** Implement `ISourceExecutor` over Proxy's providers (`DbDataKit.Connections`): every source
  query a semantic request produces runs through the same composer, read-only session, statement classifier, concurrency limits,
  pass-through identity, secret references and failure model as a client's SQL. QIS's `SqlServerExecutor` (a raw connection string,
  driver text passed through) is replaced, not ported.
- **A new request kind on the Flight SQL server.** A client sends a semantic request (JSON) and gets the combined result back as an
  Arrow stream, with the QIS descriptor and the `remaining`/`warnings` part of the result contract in the stream's metadata. Two ways in,
  in this order:
  1. a Flight SQL `CommandStatementQuery` on a semantic data source (`x-datasource: semantic:<catalog>`) whose text is the JSON request,
     plus a `DoAction` for completions, registrations (tables, bindings, value maps) and clarify flows. Any Flight SQL client (ADBC, the
     JDBC driver, Python) can call it; nothing new is needed on the client.
  2. later, a small SQL surface over the semantic model (`SELECT region, MEASURE(net_revenue) FROM sales_catalog GROUP BY region`), so BI
     tools that only write SQL can use governed measures. This is a separate spec and a real cost; only if asked for.
- **Metadata calls describe the model.** `GetTables`/`GetSqlInfo` on a semantic data source list cubes, dimensions and measures (with
  titles, synonyms and descriptions), so a client can browse the model the way it browses tables today.
- **The combine engine runs in a child process** (`Hosting.Children`, plan 03), with DuckDB's `memory_limit` and `threads` set and the
  sandbox recipe of `Connections.DuckDb` (external access off, configuration locked). A federated query is the one place Proxy would hold
  many rows in memory; it must not be able to take the service down.
- **State.** Ad hoc registrations, run records, shadow reports and clarify sessions need durable, per-user storage: Proxy's state store
  (plan 13 D18), keyed by the console user or Proxy client identity.
- **SSAS.** Proxy is already Windows-oriented (service, x86 worker). It can host `SsasExecutor` on Windows; on Linux an SSAS source reports
  `dax_execution_unavailable` as the engine does today. An SSAS data source for plain DAX pass-through falls out of it if wanted.

### R3. The engine becomes shared libraries in this repository

Not a fourth server product: the engine has no lifecycle of its own (it needs a host's connections, identities and state). Proposed
packages, in `DbDataKit/`:

| Package | From | Used by |
| --- | --- | --- |
| `DbDataKit.Semantic.Abstractions` | `Qis.Contracts` (requests, results, descriptors, errors) | everyone |
| `DbDataKit.Semantic` | `Qis.Engine` without its executors: catalog, validator, planner, emitters, combine, sandbox, ad hoc, time | Proxy (queries), Build (compile checks), console back-end (explain, validation) |
| `DbDataKit.Semantic.Ssas` | `Qis.Ssas` | Proxy on Windows |
| `DbDataKit.Semantic.Extraction` | `Qis.Engine.Extraction` | console back-end (uploads), agents |
| front-end `@dbdatakit/semantic` | `Qis.Engine.Presentation` output | console: matrix and chart renderers over the view specs |

**The spec stays a spec.** The documents, JSON Schemas and fixtures are the contract; keep them versioned on their own
(`specs/qis/` in this repository, or the QueryIntentSpec repository as the spec's home with the engine moved here). The fixtures run in
`dbdatakit-ci.yml` against real engines from `testenv/` (SQL Server, PostgreSQL) instead of a separate compose file.

**When to move it.** The other agent is building fast in its own repository, which is fine while the spec is moving. Agree the points in
section 6 now (they are cheap now and expensive later), and import the engine here (a subtree merge, as the three products came in, plan
09) once its level 5 settles. Until then, keep its seams where the suite will plug in: `ISourceExecutor`, the catalog source registry,
storage for registrations and run records.

### R4. Console and agents

- **Agents.** QIS was designed for agents. Its request, clarify, unsupported, completion and registration documents become tools on the
  suite's command surface (plan 13 W4a, one MCP surface), next to Build's commands, so one agent can build a mart, add a measure, deploy
  and answer a question with it.
- **Console.** A semantic module in the Build area: browse the model (cubes, dimensions, measures with semantics and lineage back to Build
  columns), a query explorer that writes semantic requests and shows the native queries and the plan ("explain"), matrix and chart views
  from the presentation specs, and review of ad hoc maps and bindings proposed for promotion.
- **Governance uses the suite's, not a second one.** QIS's scopes (`session`, `personal`, `shared`, `governed`) map onto what plan 13 has:
  `session` and `personal` are Proxy state per user; `shared` needs an owner's approval (the console's roles, W12); **`governed` means
  committed to the Build project** (a value map becomes a seed or a mapped table, a measure goes into `semantic/`), reviewed through the
  project's pull request or the Build server's merge (plan 13 D15). That gives versioning, history and review without a QIS-specific store.

## 4. What QIS duplicates

| QIS piece | Already in the suite | Recommendation |
| --- | --- | --- |
| `SqlServerExecutor`: `SqlConnection` from a raw connection string, driver text in errors | `DbDataKit.Connections` (composers, TLS verify by default, failure classification with driver text redacted, pool, limits), Proxy's providers | replace with an executor over `Connections` (R2) |
| SQL emission: identifiers written unquoted, `@pN`/`?` chosen by dialect, its own `LIKE` escaping | `DbDataKit.Dialect` (`ISqlDialect.Quote`, `ValidateName`, `ParameterStyleAdapter`, canonical types, per-engine dialects), Build's target rules | emit through `ISqlDialect`; keep QIS's own query shapes. A PostgreSQL emitter is then mostly the dialect |
| DuckDB sandbox (`enable_external_access = false`, `lock_configuration = true`) | `Connections.DuckDb`'s sandbox recipe (also no extension autoload or install, allowed directories, read-only) | use the shared recipe; it is stricter |
| Completion SQL validation by walking `json_serialize_sql` | Build reads DuckDB's serialized *plans* and has met the 2.0 shape changes; `Dialect`'s statement classifier and read-only policy | keep DuckDB's parser (the right tool) but run QIS's sandbox tests in the suite's DuckDB 2.0 job (plan 13 W1): the serialized JSON changed in 2.0 and QIS is on DuckDB.NET 1.1.1 |
| YAML catalog loading (YamlDotNet, scalars typed so they can be checked as JSON) | `DbDataKit.Config.Yaml` strict reader (paths, unknown keys as errors, fuzz tests), Build's strict YAML subset | load through `Config.Yaml` |
| Errors `{spec, code, path, message, suggestions}` | Build diagnostics (stable codes, catalog with `fix` and `explanation`, `help code`), `Config` diagnostics with "did you mean" | one error shape for the suite's JSON documents (plan 13 W4a); QIS codes become a namespace in it |
| "Closest members" suggestions; Jaro-Winkler | `Config`'s suggestions | one suggestion helper; keep Jaro-Winkler for value maps (it is a pinned, specified score there) |
| Table descriptor types (`string`, `integer`, `number`, `boolean`, `date`, `datetime`) | `Dialect.CanonicalType`, `Dialect.Arrow`, Build's logical types | map descriptor types onto canonical types; over Flight SQL the descriptor travels with an Arrow schema |
| Plain-Ossie import that infers dimensions and measures from a physical model | Build's `project import` (tables, keys, foreign keys, indexes from the live catalog, as mapped models) and `DbDataKit.Metadata` readers | for SQL sources, Build import plus the generated physical layer (R1) replaces it; keep Ossie import for models written elsewhere |
| Physical datasets, fields, keys and relationships written by hand in `catalog.yaml` | Build's model definitions and metadata documents | generated (R1) |
| `CheckHierarchies` and relationship checks, run on request | Build project tests, after every refresh, monitored (plan 13 W10) | generated Build tests (R1) |
| Scopes, `name@vN` versions, promotion with review | console roles and approvals (W12), git-tracked config with history (`Config.Git`), Build plan review | map onto them (R4) |
| Run records, request hashes, replay | Build plan files with hashes and statement logs; Sync's run history; `DbDataKit.Monitoring` (W10) | store runs through `State`/`Monitoring`; keep replay, which is QIS's own |
| Shadow mode: comparing two results as sets | Build's differential tests (DuckDB against a target) and `connection compare` | small; share a row-set comparer only if it grows |
| SQL Server test container (`qis/docker`) | `testenv/` | use `testenv/` |
| Presentation output (grids, Vega-Lite) | the console's front-end (`PreviewGrid`, the shared shell) | the console renders QIS view specs; the engine keeps producing data, not markup |
| Spreadsheet extraction | Sync's user-provided files store (todo phase 173V), the console's Files page, Build seeds | the files store is shared; extracted tables register as ad hoc tables or, when governed, become Build seeds |
| Package versions | the suite's central versions (DuckDB 2.0.0 engine, SqlClient 7.1.1, YamlDotNet 18.1.0) | adopt them when the engine moves (and the DuckDB 2.0 pin now, see section 6) |

**Not duplicated, and QIS's own:** the semantic request and its validation, the measure semantics (rollup, spread, grain, additivity
derived from rules), the planner and the fixed combine pipeline, the result contract with `remaining` steps and agent completions, replay,
value maps, bridges, allocation, transforms, calendars, the DAX emitter, extraction specs and view specs. That is most of the engine, and
none of it exists in the suite today.

## 5. Concept-level thoughts and concerns

1. **Three query compilers.** The suite would have Build's lowerer (DuckDB SQL to SQL Server, PostgreSQL and Fabric, at build time, with
   the support matrix and string-semantics rules), QIS's emitters (semantic requests to T-SQL, DAX and DuckDB, at query time) and Proxy's
   pass-through. Keep the boundary sharp: Build owns *table* SQL, QIS owns *aggregate query* SQL over Build's tables, Proxy owns none. Where
   QIS meets a semantic difference Build already knows (case-insensitive comparison on SQL Server against case-sensitive DuckDB in the
   stand-in, trailing spaces, integer division), reuse Build's profile knowledge rather than finding it again; the DuckDB stand-in will
   otherwise pass fixtures that SQL Server answers differently.
2. **PostgreSQL and Fabric.** Build targets SQL Server, PostgreSQL and Fabric; QIS emits T-SQL and DAX. A Build user on PostgreSQL gets no
   semantic layer until a PostgreSQL emitter exists, and Fabric's SQL endpoint is T-SQL with differences Build's matrix lists. With the
   emitter on `ISqlDialect`, PostgreSQL is the first addition to make.
3. **Federation is a resource problem.** Combining in DuckDB inside the query service is the right design, and the costly part to operate:
   row caps per source (QIS has them), memory and thread limits, a child process (R2), and Proxy's concurrency gates per user.
4. **Identity and caching.** Proxy's pass-through means two users can see different rows for the same request. Request hashes are cache
   and comparison keys in QIS; any cache or saved result must include the identity (and later any row-level rule), or one user's numbers
   reach another.
5. **Row-level security** is not in QIS. Proxy is where it would belong (one place that sees the user and every query). Not needed now;
   the design should not make it hard (filters added per identity before planning).
6. **The Ossie dependency.** Ossie is a `0.2.0.dev0` draft. Generating the physical layer from Build (R1) reduces exposure: the authoring
   format matters less when people write only the logical layer, and Ossie becomes an export format.
7. **Names.** QIS's "catalog", "dataset" and "source" collide with the suite's: `DbDataKit.Metadata`'s catalog readers, Build's datasets
   in metadata documents, Sync's sources and Build's origins. In the suite, call it the **semantic model** (files under `semantic/`), and keep
   "catalog" for database catalogs.
8. **Is SSAS strategic?** The DAX emitter and the Windows-only executor are a large share of QIS's untested surface (19 of 364 failing on
   real DAX, per its `ssas.md`). If the governed models of the future are Build marts, the DuckDB stand-in for SSAS may be all that is
   needed; if SSAS models are a real source, Proxy on Windows is the place to run them. Worth a decision before more DAX work.
9. **The agent-completion design is a strength.** Partial results with explicit `remaining` steps, sandboxed completion SQL, replay and
   shadow comparison give a safe way to grow the engine. Keep it as is when the engine moves.
10. **Two specs about rows over the wire.** QIS's table descriptor (Frictionless plus `x_` fields) and Flight SQL's Arrow schema both
    describe a result. Over Flight, send the Arrow schema with the descriptor in its metadata; inside the engine keep the descriptor.

## 6. To agree with the QueryIntentSpec work now

Cheap to settle while the engine is young, expensive later:

1. **DuckDB 2.0.0** with the engine-free DuckDB.NET packages and the suite's pin (plan 13 W1), instead of `DuckDB.NET.Data.Full` 1.1.1;
   run the sandbox and `json_serialize_sql` tests on 2.0.0.
2. **Source execution behind `ISourceExecutor` only**; no new code that opens connections itself.
3. **Logical names that can carry Build model names**: datasets that reference a model name and a connection, so the physical layer can
   be generated later without changing the catalog's logical part.
4. **The error shape** of the suite's JSON documents for new codes.
5. **Package versions** close to the suite's (SqlClient 7.x, YamlDotNet 18).
6. **No new storage** for registrations and run records beyond an interface; the suite's state store will hold them.

## 7. Suggested order, once approved

After plan 13's M2 (the console with Sync, the command surface, the trust model), and alongside M3:

| Step | What | Depends on |
| --- | --- | --- |
| Q1 | Import the engine into `DbDataKit/` as the packages in R3; fixtures in `dbdatakit-ci.yml` on `testenv/` engines | plan 13 F01-F04 (build setup), K (DuckDB 2.0) |
| Q2 | Executor over `Connections`; emitters on `ISqlDialect`; YAML through `Config.Yaml` | Q1 |
| Q3 | Build: semantic files in projects, compile-time validation, generated physical layer, references on built models | Q1, plan 13 B (Build on the shared libraries) |
| Q4 | Build: generated hierarchy and relationship tests; the calendar template | Q3 |
| Q5 | Proxy: semantic data source over Flight SQL (`CommandStatementQuery` with JSON, `DoAction`), combine in a child process, state in Proxy's store | Q2, plan 13 P (Proxy management), D18 |
| Q6 | Build deploy publishes the semantic model; Proxy loads it per connection | Q3, Q5 |
| Q7 | Agent tools on the shared command surface; console semantic module (model browser, query explorer, views, promotion review) | Q5, plan 13 W4a, W5 |
| Q8 | PostgreSQL emitter | Q2 |
| Q9 | SSAS on Proxy (Windows), if the decision in section 5 item 8 says so | Q5 |

## 8. Risks

| Risk | Mitigation |
| --- | --- |
| The QIS spec keeps moving while the suite depends on it | the spec versions itself (`spec_version`); the engine moves here only after level 5 settles; fixtures are the contract |
| A federated query exhausts Proxy's memory | child process, DuckDB limits, row caps, concurrency gates (R2) |
| Wrong numbers from a semantic difference between the DuckDB stand-in and SQL Server | fixtures run on real SQL Server and PostgreSQL in CI; Build's string-semantics knowledge applied to the emitters |
| One user's cached result reaches another | identity in every cache and saved-result key (section 5 item 4) |
| Two governance systems (QIS scopes, the suite's roles and reviews) | `governed` means committed to the Build project (R4) |

## 9. Questions for the owner

1. **Home of semantic models:** in Build projects (`semantic/`, physical layer generated), as recommended, or standalone catalogs that
   reference Build's published metadata?
2. **Home of the engine:** shared libraries in this repository (recommended), with the spec documents and fixtures kept versioned on
   their own; and when to import it.
3. **Proxy's interface:** JSON requests through Flight SQL `CommandStatementQuery` and `DoAction` first (recommended); a SQL surface over
   the semantic model later, or never?
4. **SSAS:** a real source to support (on Proxy, Windows), or test stand-in only?
5. **Ad hoc data and extraction:** in the first version on Proxy, or after governed queries work?
6. **Names:** "semantic model" in the suite, "catalog" kept for database catalogs?
