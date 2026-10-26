# Plan 14: the semantic layer (Query Intent Specs) in the suite

Status: **proposed for review** (2026-10-24), **revised with the owner's long-term direction** the same day: semantic models are
developed **and deployed** by DbDataBuild and **hosted** by DbDataProxy; a connection is managed once, in Build, and Build keeps the
matching Proxy data source up to date (R1, R2; corrected after review: an earlier draft had Proxy deploying); connections, engines and metadata are normalized across the products (R5); a SQL query language over the JSON
requests (R6); enough MDX for Excel PivotTables (R7); more serving protocols in Proxy, XMLA certainly (R8). The master data and document
service is [plan 15](15-master-data-and-documents.md). **Not part of plan 13's work list** (`13a-suite-slices.md`): nothing here is to be
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

**What it is, in the owner's words: more an engine and a proof of concept than a spec** (2026-10-26). Its documents, schemas and fixtures
describe what the engine does today; parts of its design and implementation will be re-evaluated as it is incorporated, so nothing in
QueryIntentSpec is a contract the suite must keep. It is a semantic layer and federation engine for agents, about where Cube, MetricFlow or a Power BI semantic model sit,
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
- **Build authors and deploys; Proxy hosts** (owner direction). `connection deploy` applies the tables, then deploys the semantic model
  to the Proxy that serves that connection: a **semantic package** (the compiled model, its physical layer for that connection, the Build
  commit and the hash of the metadata it was compiled against). Proxy checks the package against the live catalog through
  `DbDataKit.Metadata` (every table and column there, types as declared), switches to it atomically, and keeps the earlier versions so a
  deploy can be rolled back from Build. A package compiled against tables that differ from what the data source shows is refused, with the
  table named, so the tables and the model they describe never disagree.
- **One connection, managed once, in Build** (section R2a below). The user defines a connection where it is built, in the Build project;
  the Proxy data source that serves it is created and kept in step by Build's deploy, never typed in a second time.
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

### R2a. One connection: a target for Build, a source for Proxy

A warehouse is a **target** for Build (it writes tables there) and a **source** for Proxy (it reads them for clients). The user must not
manage it in both places, so the connection has one owner and two views:

- **Defined once, in the Build project**, as today (`connection/1`, plan 13 E03): engine, server, database, TLS, and Build's two logins,
  read and write, kept apart as Build already keeps them. A `serve:` entry names the Proxy (an instance paired with Build, below) and the
  data source name to use there.
- **Build deploys the Proxy data source** together with the tables and the semantic package. What Proxy receives is the connection's
  definition with **the read login only**: the write login never leaves Build. The read secret is sent once over the paired, TLS channel
  and stored in Proxy's own secret store (plan 13 W13), as a secret typed into the console is. Proxy marks the data source as **managed
  by** that Build project and connection.
- **Ownership is split by concern.** The connection's fields (where it is, how to log in to read, TLS) belong to Build: in Proxy's console
  module they are shown read-only, with a link to the Build project. The **serving policy** (`extensions.proxy`: concurrency limits, the
  statement classifier, which client identities may use it, pass-through) belongs to Proxy and is edited there; Build's deploy never
  touches it.
- **An existing data source** that Proxy already has for the same server and database is not taken over silently: a deploy that finds one
  stops and says so, and an administrator adopts it (`--adopt`), after which Build manages its connection fields.
- **Server to server.** Plan 13 W12's rule is that a console calls a server and never the reverse. This adds one more kind of paired
  client: **a Build server pairs with a Proxy** the same way a console does (a code from `dbdataproxy server pair`, entered on the Build
  side), with a fixed ceiling role, **Publisher**, which can manage only the data sources and semantic models that this Build server
  created. A Build CLI on a developer's machine pairs the same way, its key in the user's secret store. The rules for this kind of client
  (owner-scoped ceilings, tokens naming a person, what may be sent) are in plan 13 W12, "Server-to-server clients", with slices T10 and
  T11 in plan 13a.
- **Deploying from the console** is the same path: the console asks the Build server to deploy, and the Build server calls Proxy.

### R2. DbDataProxy serves semantic queries

- **QIS sources are Proxy data sources.** Implement `ISourceExecutor` over Proxy's providers (`DbDataKit.Connections`): every source
  query a semantic request produces runs through the same composer, read-only session, statement classifier, concurrency limits,
  pass-through identity, secret references and failure model as a client's SQL. QIS's `SqlServerExecutor` (a raw connection string,
  driver text passed through) is replaced, not ported.
- **A new request kind on the Flight SQL server.** A client sends a semantic request and gets the combined result back as an Arrow
  stream, with the QIS descriptor and the `remaining`/`warnings` part of the result contract in the stream's metadata: a Flight SQL
  `CommandStatementQuery` on a semantic data source (`x-datasource: semantic:<model>`) whose text is either the JSON request or **semantic
  SQL** (R6), plus a `DoAction` for completions, registrations (tables, bindings, value maps) and clarify flows. Any Flight SQL client
  (ADBC, the JDBC driver, Python) can call it; nothing new is needed on the client. Other protocols (R8) are front ends to the same core.
- **Metadata calls describe the model.** `GetTables`/`GetSqlInfo` on a semantic data source list cubes, dimensions and measures (with
  titles, synonyms and descriptions), so a client can browse the model the way it browses tables today.
- **The combine engine runs in a child process** (`Hosting.Children`, plan 03), with DuckDB's `memory_limit` and `threads` set and the
  sandbox recipe of `Connections.DuckDb` (external access off, configuration locked). A federated query is the one place Proxy would hold
  many rows in memory; it must not be able to take the service down.
- **State.** Ad hoc registrations, run records, shadow reports and clarify sessions need durable, per-user storage: Proxy's state store
  (plan 13 D18), keyed by the console user or Proxy client identity.
- **SSAS is a supported source** (owner, 2026-10-26): QIS already supports it and will continue to. Proxy is already Windows-oriented
  (service, x86 worker) and hosts `SsasExecutor` on Windows; on Linux an SSAS source reports `dax_execution_unavailable` as the engine does
  today. SSAS runs in GitHub CI (QIS's Chocolatey setup), so the DAX path is tested against a real server. An SSAS data source for plain DAX
  pass-through falls out of it if wanted.

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

**An engine to incorporate, not a spec to conform to** (owner, 2026-10-26). QIS is a proof of concept: its documents, JSON Schemas and
fixtures come along as the **starting point** for the semantic layer's own documentation and tests, and are revised wherever incorporating
it shows a better design (names, the catalog format next to Build's metadata, the error shape, the request format beside semantic SQL). A
change to its behaviour is made deliberately, with its fixtures updated in the same change, not avoided to stay compatible with
QueryIntentSpec. The fixtures run in `dbdatakit-ci.yml` against real engines from `testenv/` (SQL Server, PostgreSQL) and SSAS in CI,
instead of a separate compose file.

**When to move it.** The other agent is building fast in its own repository, which is fine while the engine is moving. Agree the points in
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

### R5. One set of connections, engines and metadata

The owner's direction is to normalize as much as possible about connections, supported engines and metadata. With plan 13 most of the
code is shared already; what is missing is one place that says what the suite supports.

- **One engine registry.** `DbDataKit.Connections.Descriptor` (`engine/1` YAML descriptors, unused today) becomes the single list of
  engines: id, dialect (`DbDataKit.Dialect`), provider, catalog reader (`DbDataKit.Metadata`), and **a capability per role**: Sync source,
  Sync target, Build target, Build origin, Proxy source, semantic emitter, master data store (plan 15). Every product reads it instead of
  its own enum (Sync's `DriverType`, Build's `engine` keywords, Proxy's `Provider`, QIS's source `kind`), and the support tables in each
  product's README are generated from it, so a claim in a README is a fact in a file.
- **One connection definition.** `connection/1` everywhere (plan 13 W8). A connection Build writes to and Proxy serves is one
  definition, owned by Build, with the Proxy data source deployed from it (R2a); the console's cross-server Connections view (plan 13 E05)
  shows the pair as one connection.
- **One metadata model.** `DbDataKit.Metadata` records (tables, columns, keys, foreign keys, indexes, server facts) are what Build's
  metadata documents, Proxy's Flight SQL catalog answers, the semantic physical layer and the master data service's entities (plan 15)
  are written from. Types are `Dialect.CanonicalType` everywhere, with one mapping to Arrow and one to the QIS descriptor types.
- **Gaps this exposes.** SQLite, MariaDB (as distinct from MySQL) and Oracle are not equally covered: Sync has MySQL and Oracle drivers,
  `DbDataKit` has no MySQL, Oracle or SQLite provider yet (plan 13 A09), Build targets neither. The registry makes the gaps visible
  instead of fixing them; each is filled when a product needs it.

### R6. A SQL language over the semantic requests

**Prerequisite (owner, 2026-10-26): QIS is integrated as a shared library (R3, Q1 and Q2) before work on semantic SQL starts; the grammar
details are settled then, against the engine as it is.**

**Decided (owner, 2026-10-24): semantic SQL, a subset of DuckDB SQL with `MEASURE()`, translated to and from the JSON request.** No new
grammar.

Prior art, and what each would mean here:

| Prior art | Query form | Fit |
| --- | --- | --- |
| Databricks metric views | `SELECT category, MEASURE(net_sales) FROM sales_metrics GROUP BY category`; `MEASURE(m) FILTER (WHERE ...)`; the aggregation comes from the view's YAML definition | **closest match**: plain SQL syntax, measures defined outside the query as ours are |
| Cube SQL API | the same `MEASURE()` idea, served over the Postgres wire protocol so BI tools see a database | the protocol part is R8 |
| Julian Hyde, "Measures in SQL" (SIGMOD 2024; Calcite; the `yardstick` DuckDB extension) | `expr AS MEASURE m` in a view, `AGGREGATE(m)` in a query, `AT (...)` to change the evaluation context | accept `AGGREGATE(m)` as a synonym; `AT` maps onto overrides and `prior_period`, later |
| Snowflake semantic views | `FROM SEMANTIC_VIEW(v DIMENSIONS a, b METRICS m WHERE ...)` | maps one to one onto the JSON, but is its own grammar inside SQL; no |
| Rill metrics SQL, Malloy, LookML | their own languages | a new language to learn and parse; no |

Why a DuckDB subset:

- **The parser exists.** QIS already validates DuckDB SQL with DuckDB's own parser (`json_serialize_sql`), and Build reads DuckDB's
  serialized plans for a living. `MEASURE(x)` and `AGGREGATE(x)` parse as ordinary function calls, `FILTER (WHERE ...)` and named
  arguments (`spread := 'blank'`) are DuckDB syntax, and `GROUP BY ROLLUP (...)` / `CUBE (...)` already express QIS totals. No grammar of
  our own to design, document and fuzz.
- **The semantics are a closed mapping.** Every accepted query is a JSON request; anything outside the subset is refused with a named
  diagnostic (the QIS `unsupported` codes), never approximated.

**Naming: model-anchored structures** (decided with the owner). Users see SQL that looks like plain SQL over nested columns, with no
joins:

```sql
SELECT customer.region,
       order_date.fiscal_month,
       MEASURE(orders.net_revenue),
       MEASURE(budget.amount)
FROM sales                         -- the semantic model, not a table
GROUP BY ALL
LIMIT 100
```

- **`FROM` names the semantic model.** Its members are addressed as nested structures, with DuckDB's ordinary `a.b.c` syntax.
- **Conformed dimensions are top-level structures of the model** (`customer.region`, `order_date.fiscal_month`, `product.category.name`
  for a snowflake), usable with every fact that reaches them. This is what keeps a query over two facts (revenue against budget) free of
  joins and of any choice about "whose region".
- **Facts are namespaces** for their measures and for what only they have: role-playing dimensions (`orders.ship_date.month` beside the
  conformed `order_date`), degenerate dimensions and the fact's own columns (`orders.status`).
- **A path from a fact is the explicit form.** `orders.customer.region` means `customer.region` when that is unambiguous, and is required
  where it is not (two roles of one dimension). Every path is many-to-one by construction, which is the semantic layer's no-fan-out rule
  read as syntax: a structure field has one value per row.
- **Ad hoc and master data attributes attach where they belong**: a territory bridge on customers is `customer.territory`.
- **Struct operations have defined meanings or clear refusals**: grouping by a whole dimension (`GROUP BY customer`) groups by its key
  and label; `customer.*` expands to the dimension's attributes; `struct_extract` and the like are refused inside a semantic query, and
  work as usual in an outer query.
- **The canonical SQL** is the shortest unambiguous form; that is what end users see and what JSON to SQL renders.
- **Real SQL on the results.** An outer query (joins with other results, windows, `QUALIFY`, set operations) runs in DuckDB over the
  semantic query's result, where a dimension can be a real DuckDB struct.

**Flattened names** for tools that cannot use nested columns (Postgres-wire BI tools, Excel's field lists, CSV exports): every member also
has a flat column name, `customer_region` by default, from the path joined by a **separator** that the model configures (`_` by default;
`__` or another where single underscores are common in names). Where two paths flatten to the same name, or a flat name collides with a
member, **the model fails to compile with both paths named**, and the author fixes it by giving one member an explicit `flat_name`. The
flat form is a rendering of the same model, never a second model: a query written with flat names translates to the same JSON request.

The mapping:

| Semantic SQL | JSON request |
| --- | --- |
| `FROM <model>` (one semantic model; its cubes' members are its columns: `customers.region`) | the catalog |
| a dimension in the select list and in `GROUP BY` (or `GROUP BY ALL`) | `dimensions` |
| `MEASURE(orders.net_revenue)`, `AGGREGATE(...)` | `measures` |
| `MEASURE(m) FILTER (WHERE status = 'Completed') AS completed` | a filtered measure |
| `MEASURE(m, spread := 'blank')`, `MEASURE(m, rollup := {'dates.fiscal_month': 'avg'})` | overrides |
| `WHERE` on dimensions; `HAVING` on measures | `filters` (dimension), filters on measures |
| `GROUP BY ROLLUP (a, b)`, `GROUP BY CUBE (a, b)` | `x_totals` |
| a closed set of window patterns over measures (`MEASURE(m) / SUM(MEASURE(m)) OVER (PARTITION BY ...)`, `RANK() OVER (...)`, running sums) | transforms (`pct_of_total`, `rank`, `running_total`) |
| `QUALIFY rank <= 5` | `x_post_filters` |
| `ORDER BY`, `LIMIT` (required, as in JSON) | `order`, `limit` |
| an outer query over a semantic query used as a derived table | a **completion**: run in the sandboxed DuckDB over the result, as QIS completions are |
| joins, set operations, subqueries in `WHERE` | refused (`unsupported`: `set_filter`, `custom_expression`) |

And the reverse: every JSON request has **one canonical semantic SQL text**, shown in explain views, in run records and to agents. The
conformance fixtures gain a SQL form, and the tests check that SQL to JSON to SQL is stable. Agents may write either; people mostly read SQL.

The translator is part of `DbDataKit.Semantic` (R3), specified as a QIS document (`docs/semantic-sql.md` beside `query.md`).

### R7. MDX, enough for Excel PivotTables

**What Excel does.** A PivotTable on an OLAP source connects with the Analysis Services provider (MSOLAP) over **XMLA**, SOAP over
HTTP(S): `Discover` requests for the schema rowsets, `Execute` requests carrying MDX, sessions, and multidimensional results (cell sets).
Excel generates a **narrow, recognizable set of MDX shapes**: `NON EMPTY Hierarchize(AddCalculatedMembers(DrilldownLevel({...})))` on axes
with `DIMENSION PROPERTIES PARENT_UNIQUE_NAME, ...`, filters as a sub-select (`FROM (SELECT ... ON 0 FROM [cube])`) or a `WHERE` slicer,
drill-down and member searches. Prior art: Mondrian and its fork eMondrian (Java) serve Excel this way over any JDBC database, and
XMLA Connect is an open-source Excel client; there is no .NET XMLA server to reuse.

**Recommendation:** translate that MDX into semantic requests; do not build a general MDX engine.

- **The semantic model already has what MDX needs**, or nearly: cubes (facts), dimensions, hierarchies with levels, members lists, sort
  keys, measures with rollup rules (Excel's subtotals are QIS totals, `CUBE`-style for a matrix with both axes totalled). To add: an `All`
  member and member unique names per hierarchy, measure groups, display folders and **format strings** on measures.
- **An MDX parser for the subset**, producing the axes, the slicer and the sub-select filters, then one semantic request per cell set
  (dimensions from the axes' levels, `x_totals` from `DrilldownLevel`/`Hierarchize`, filters from slicers and sub-selects), then the
  QIS result reshaped into the cell set Excel expects. Anything outside the subset is an XMLA error with a clear message.
- **Excel's own traffic is the specification, catalogued once on a VM.** SSAS now runs in GitHub CI (the QueryIntentSpec work installs
  it with a Chocolatey SQL Server package); Excel will not. So the cataloguing is a one-time job on a Windows VM with Excel (the owner can
  set one up):
  1. Proxy's XMLA listener is built first as a **recording forwarder**: it passes Excel's requests to SSAS unchanged and saves each
     request and answer (with credentials removed).
  2. A script of PivotTable actions is run by hand against the QIS Tabular model: add a field to rows, columns, values and filters; expand
     and collapse; sort; filter by label and by value; search members; refresh; with one field and with several; with and without totals.
     Repeated on each Excel available (Microsoft 365 current channel, and an LTSC build if one matters).
  3. The recordings are golden fixtures. In CI, each recorded request goes to Proxy's XMLA endpoint *and* to SSAS (from Chocolatey), and
     the two cell sets must agree.
- **How often Excel changes its MDX is not known**, so nothing depends on it: the parser matches **MDX structure** (the parsed tree:
  functions, sets, member references), not text, so spacing, property lists and order do not matter; and an MDX shape the translator does
  not recognize is **refused with a clear XMLA error and logged** (the request's shape, with values removed), never answered approximately.
  A new Excel that changes its queries shows up as those log entries, which are what to catalogue next, on the VM again.
- **In order:** (1) `Discover` rowsets, so Excel lists the fields (`DISCOVER_PROPERTIES`, `DBSCHEMA_CATALOGS`, `MDSCHEMA_CUBES`,
  `_DIMENSIONS`, `_HIERARCHIES`, `_LEVELS`, `_MEASURES`, `_MEMBERS`, `_PROPERTIES`, `_SETS`); (2) rows, columns and values with
  subtotals and grand totals; (3) filters (slicer, sub-select), drill-down, sorting; (4) member search, named sets, measures Excel
  calculates itself. Write-back, drill-through and user-defined calculated members are out of scope.
- **Authentication** is what Excel can do over HTTP: Windows (Negotiate), which Proxy already validates, or Basic over TLS, resolved to
  Proxy's client identities, so every PivotTable query carries a user like any other Proxy query.

### R8. More serving protocols in Proxy

All protocols are **front ends to one query core**: a protocol adapter turns its wire messages into a SQL pass-through query, a
semantic request (JSON or semantic SQL) or an MDX request, maps its authentication onto Proxy's identities, and returns rows. It never
opens a connection, applies a limit or formats an error itself. Each adapter gets its own listener, off by default.

**The adapters are shared code, not Proxy code** (owner direction, 2026-10-24). They live in `DbDataKit.Serving.*` (`.FlightSql`, the
Flight SQL server Proxy has today, moved out of `DbDataProxy.Server`; `.PostgresWire`; `.Xmla`), written against a small interface the
host implements: authenticate a caller to an identity, list what can be queried (tables or semantic members, through `DbDataKit.Metadata`),
run a query under that identity's limits, and write if the host allows writes at all. Proxy is one host. **DbDataStore** (plan 15) is
another: it serves its own entities over the same protocols without going through Proxy, so neither product carries the other's job.

| Protocol | What it serves | Recommendation |
| --- | --- | --- |
| Flight SQL (today) | SQL pass-through, semantic JSON and semantic SQL | keep as the primary protocol |
| **XMLA** (with R7) | MDX for Excel PivotTables, the semantic model as cubes | **build**, with R7: it is the only way Excel's PivotTables reach a non-Microsoft model |
| **Postgres wire** (protocol v3) | semantic SQL to BI tools that speak Postgres (Tableau, Power BI's Postgres connector, Metabase, Superset, psql), as Cube's SQL API does | **build after semantic SQL works**. The protocol itself is small (simple and extended query, SCRAM-SHA-256, TLS); the long tail is **catalog emulation**: BI tools first query `pg_catalog` and `information_schema`, and each tool's introspection must be answered, tool by tool. Serve semantic SQL only, not pass-through: a Postgres client writing Postgres SQL against a SQL Server data source would be a dialect mismatch. There is no .NET Postgres server library; Npgsql is a client |
| **Quack** (DuckDB 2.0's client-server protocol: `ATTACH 'quack:host'`, a shared token, request and response) | DuckDB clients | **wait.** DuckDB describes it as an RPC protocol between DuckDB processes that "supports the full DuckDB feature set"; a server in .NET would have to behave like DuckDB, and no third-party server specification or stability promise was found. Revisit when one exists; the cheaper route then may be a DuckDB child process serving Quack over views of semantic results, behind Proxy's identities |

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
8. **SSAS: decided, a supported source** (owner, 2026-10-26). QIS supports it and will continue to; Proxy runs it on Windows, and the
   DAX path runs against a real SSAS in CI. (When this review was written, 19 of 364 fixtures failed on real DAX, per QIS's `ssas.md`.)
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
| Q6 | Build produces semantic packages and deploys them, with the managed data source, to the paired Proxy (R2a) | Q3, Q5, plan 13 W12 (pairing a Build server) |
| Q7 | Agent tools on the shared command surface; console semantic module (model browser, query explorer, views, promotion review) | Q5, plan 13 W4a, W5 |
| Q8 | PostgreSQL emitter | Q2 |
| Q9 | SSAS on Proxy (Windows): `SsasExecutor` hosted by Proxy, the DAX path in CI against SSAS | Q5 |
| Q10 | The engine registry (R5): `engine/1` descriptors read by every product; generated support tables | plan 13 A/B (Connections adopted) |
| Q11 | Semantic SQL (R6): the spec, the translator both ways, SQL forms of the fixtures. **Starts only after QIS is a shared library here** (Q1, Q2) | Q1, Q2 |
| Q12 | Hosting on Proxy (R1): validate a package against the live catalog, switch atomically, keep versions, rollback from Build; managed data sources read-only in Proxy's module | Q5, Q6 |
| Q13 | XMLA and MDX for Excel (R7): the recording forwarder; the one-time catalogue on the VM; then Discover, axes and totals, filters and drill, search, each checked against SSAS in CI | Q5, the VM with Excel, SSAS in CI |
| Q14 | Postgres wire for semantic SQL (R8), tool by tool | Q11 |

## 8. Risks

| Risk | Mitigation |
| --- | --- |
| QIS's design is re-evaluated while the suite starts depending on it | it is a proof of concept, not a contract: design changes are made deliberately, each with its fixtures updated in the same change; the suite's semantic documentation becomes the reference once it is incorporated |
| MDX scope creep: Excel is one client, but every MDX client is different | the subset is defined by recorded Excel traffic; other clients are out of scope until asked for |
| Postgres-wire BI tools each introspect differently | one tool at a time, each with its own recorded fixtures; semantic SQL only |
| A semantic package deployed against tables that changed | Proxy validates the package against the live catalog before serving it, and refuses with the table named (R1) |
| A Build server's pairing is used to change Proxy beyond its own data sources | the Publisher ceiling: only the data sources and models that Build server created (R2a) |
| The write login reaching Proxy | Build sends the read login only; a test asserts the deployed definition has no write login (R2a) |
| A federated query exhausts Proxy's memory | child process, DuckDB limits, row caps, concurrency gates (R2) |
| Wrong numbers from a semantic difference between the DuckDB stand-in and SQL Server | fixtures run on real SQL Server and PostgreSQL in CI; Build's string-semantics knowledge applied to the emitters |
| One user's cached result reaches another | identity in every cache and saved-result key (section 5 item 4) |
| Two governance systems (QIS scopes, the suite's roles and reviews) | `governed` means committed to the Build project (R4) |

## 9. Questions for the owner

1. ~~Home of semantic models~~: **decided**: authored and deployed by Build (`semantic/`, physical layer generated), hosted by Proxy (R1, R2a).
2. ~~Home of the engine~~: **decided** (owner, 2026-10-26): QIS is integrated here as shared libraries (R3) **before** any semantic SQL
   work starts. QIS is an engine and a proof of concept: its design is re-evaluated as it is incorporated (R3).
3. ~~Proxy's interface~~: **decided**: JSON requests and semantic SQL through Flight SQL (`CommandStatementQuery`, `DoAction`); other protocols are front ends to the same core (R2, R8).
4. ~~SSAS~~: **decided**, a supported source (section 5 item 8).
5. ~~Ad hoc data and extraction~~: **decided** (owner, 2026-10-26): probably not in the first release; governed semantic queries first.
6. ~~Names~~: **decided** (owner, 2026-10-26): "semantic model" in the suite, "catalog" kept for database catalogs. One thing to know:
   Microsoft now calls Power BI datasets "semantic models" too. That helps more than it hurts (the same idea, a familiar word), but in
   documentation about Power BI or SSAS integration (plans 14 R7, 17), say "Power BI semantic model" or "suite semantic model" where both appear.
7. ~~Semantic SQL~~: **decided**, the DuckDB subset with `MEASURE()` (R6).
8. ~~Excel~~: **decided**, PivotTables over XMLA, MDX translated to semantic requests, Excel's traffic catalogued once on a VM, SSAS in CI
   as the oracle (R7).
9. ~~Protocols~~: **decided** (owner, 2026-10-26): Postgres wire before Quack; XMLA with the MDX work; Quack deferred until DuckDB publishes a server specification (R8).
