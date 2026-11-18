# SEMANTICS: the semantic layer, the rest

## Goal

What a semantic model publishes is checked, described with types and decimals, carried in Build's metadata, promotable from a saved query and
configurable by form, and Analysis Services sources are bounded and cancellable. How the layer works is `docs/semantic/architecture.md`.

## What exists today

- Semantic models authored in Build projects, published from the Workspace, hosted by the Gateway; the engine registry; semantic SQL; parameters;
  saved queries; run records; the console's model browser, explorer and saved-queries screens; SSAS as a source.
- The Gateway reads the registry for most engines; a few of its old engine switches remain.
- Publish checks every mapped column (`MappedColumns.cs` in the Gateway: one `SELECT <columns> FROM <table> WHERE 1 = 0` per table); its tests ran on
  PostgreSQL, the SQL Server leg was never run.
- `describe` lists each attribute's and measure's type and a saved query's output columns; a measure over a decimal column is Arrow `decimal128` with
  the source's scale (`ArrowRows.cs`).
- Build's project document carries the semantic models (an optional `semantic` key); `project show semantic` says whether a relationship came from a key
  or the file (`origin`, `declared_at` in `SemanticShowCommand.cs`).
- The Gateway's saved-queries screen promotes a saved query to the model entry a person copies (`SavedQueriesPage.tsx`); its Playwright leg (promote,
  paste into a Workspace project, publish) is not written.
- The `semantic:` section of the Gateway's `server.yaml` has a form for scalars only (no hand-set models, no SSAS connections).
- SSAS: no row cap in the DAX text; the executor is synchronous with no timeout from the data source.
- Access rules (`docs/serving-layer.md`): row rules as a semi-join in the plan and column rules (`hide`, `null`, `mask`) apply to semantic requests,
  semantic SQL, saved queries, feeds and entity reads; `ModuleAccessSuite` proves no module bypasses them. Saved queries and REST feeds run the same path
  but have no tests of their own.
- Excel PivotTables over XMLA, the PostgreSQL wire protocol and Quack are not built; the XMLA recording forwarder (`Shared/tools/DbDataKit.Tools.XmlaForwarder`)
  is built and has never run against Excel. Their design is under "Open designs".

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| SEMANTICS-1 | The Gateway reads the registry for every engine: the old engine switches retired | none | S | `DbDataGateway.Tests`: every `supported` engine opens a data source, every other is refused naming the note |
| SEMANTICS-2 | The SQL Server leg of the mapped-column check: run `ManagementSemanticTests`' mapped-column cases (a catalog mapping a column the data source lacks) against a SQL Server data source, and fix what it finds | none | S | the cases pass on SQL Server in CI: the refusal names `table.column` and the serving version is unchanged |
| SEMANTICS-6 | The `semantic:` form: hand-set models, the data source of each source and the SSAS connections (secret references) | none | M | vitest: the fields round-trip the sample file; Playwright: save a change and the Gateway applies it without a restart |
| SEMANTICS-7 | The Playwright leg of promoting a saved query into its model (the promote itself is built: `docs/semantic/architecture.md`, "Saved queries"): promote on the Gateway's saved-queries screen, paste the entry into a Workspace build project, publish | AUTHORING-5 | S | Playwright: promote, paste into a Workspace build project, publish; the promoted query runs with the same rows |
| SEMANTICS-8 | A row cap in the DAX text (`TOPN (n + 1, ...)`) when no `limit` is asked | none | S | the `semantic-ssas` job: a query of a thousand-row table with `limit` 10 returns 10 and `truncated` |
| SEMANTICS-9 | The SSAS executor: asynchronous, one connection per host source, the command timeout from the data source, a role that can write refused at start | none | M | `SsasExecutorGuardTests` and the `semantic-ssas` job: a slow query is cancelled by the timeout; a writing role is refused naming it |
| SEMANTICS-10 | Access-rule tests of their own for saved queries and REST feeds (low priority: they run the same path as a semantic request, which is tested): a saved query and a feed each apply row rules and column rules as the caller | none | S | on SQL Server and PostgreSQL data sources: two callers with different row rules get different rows from the same saved query and the same feed; a hidden column is missing from both and a masked one is masked |

## Needs from other subjects

- AUTHORING-5: the `queries:` editor in a build project.

## Proposed decisions

### SEMANTICS-P2: where an agent's Gateway address comes from for Build's `semantic` noun (awaiting the owner)

`dbdatabuild semantic describe|query|explain|members` needs `--address`; `ui mcp` withholds `--address`, `--insecure`, `--ca-file` and `--server-dir`
from the tools, so an agent cannot send the person's token (`DBDATAKIT_GATEWAY_TOKEN`) to an address of its choosing. With the Build server's pairings
gone, an agent's semantic tools answer with the sentence that says to give `--address` (`DbDataBuild/DESIGN.md`). Scenario: an author asks an agent in
a Build project to check a measure's totals. Option A: the address comes from where the token comes from, never from the tool call: a variable
`DBDATAKIT_GATEWAY_ADDRESS` (and its CA file) set by the person who starts the agent, read by the verbs when `--address` is absent. Option B: Build's
agent tools leave semantics to the Gateway's `mcp` module (KNOWLEDGE-17), and `ui mcp` stops offering the four verbs. Option C: the Workspace project
names its Gateway, and the agent's tools use that. Recommendation: A, the smallest change that keeps the address out of the agent's hands.

## Open designs

Not scheduled. Each becomes tasks only when the owner schedules it. The protocols (the first four) are low priority (owner, 2026-11-09), come in the
order given below, and the Excel work needs the owner's Windows VM with Excel before any code that talks to Excel.

### MDX and XMLA for Excel PivotTables

**What.** An Excel PivotTable on an OLAP source connects with the Analysis Services provider over XMLA (SOAP over HTTP or HTTPS): `Discover` for schema
rowsets, `Execute` for MDX, sessions, cell sets. Excel generates a narrow, recognisable set of MDX shapes (`NON EMPTY
Hierarchize(AddCalculatedMembers(DrilldownLevel({...})))` on axes with `DIMENSION PROPERTIES`, filters as a sub-select or a `WHERE` slicer,
drill-down, member searches). No .NET XMLA server exists to reuse.

**How, as decided.** Translate only the MDX Excel sends into semantic requests; never build a general MDX engine. An MDX parser for the subset works on
the parsed tree, never on text, and produces the axes, the slicer and the sub-select filters, then one semantic request per cell set, then the result
reshaped into the cell set Excel expects. Anything outside the subset is an XMLA error with a clear message, logged with the request's shape (values
removed), never answered approximately. Other clients are out of scope until asked for.

- **Excel's own traffic is the specification and the CI fixture.** On a Windows VM with Excel, the forwarder (`dbdata-xmla-forwarder`) records Excel's
  requests against the QIS Tabular model while a script of PivotTable actions runs by hand (fields on rows, columns, values and filters; expand and
  collapse; sort; filter by label and value; search; refresh; totals on and off; each available Excel version). The recordings become golden fixtures
  (under the Shared tests' `xmla/` folder); in CI each recorded request goes to the XMLA endpoint and to SSAS, and the two cell sets must agree. A new
  Excel version is a new recording.
- **The order.** (1) `Discover` rowsets so Excel lists the fields (`DISCOVER_PROPERTIES`, `DBSCHEMA_CATALOGS`, `MDSCHEMA_CUBES`, `_DIMENSIONS`,
  `_HIERARCHIES`, `_LEVELS`, `_MEASURES`, `_MEMBERS`, `_PROPERTIES`, `_SETS`) and sessions, as an `xmla` serving module (a line of `modules:`); (2) rows,
  columns and values with subtotals and grand totals; (3) filters, drill-down and sorting; (4) member search, named sets and measures Excel calculates
  itself. Write-back, drill-through and user-defined calculated members are out of scope.
- **Model additions MDX needs.** An `All` member and member unique names per hierarchy, measure groups, display folders and format strings on measures:
  optional keys of the semantic package's format 2, refused with file and line when wrong, and refused by an instance that reads only format 1.
- **Sign-in.** XMLA is served over HTTPS only, with Windows (Negotiate) or the identity home's user name and a personal access token, resolved to the
  serving layer's identities so every PivotTable query carries a person. HTTP (msmdpump) is not used by Excel and native TCP is not built. A wrong
  token is an XMLA error, not a hang.
- **Access rules.** The translators produce semantic requests, so row and column rules apply through the planner; the module joins the access
  conformance suite when it exists.

**Open questions.** The VM and its recording session (the owner's). The risk is MDX scope creep: the subset is what the recordings show.

### The PostgreSQL wire protocol

**What.** A `pgwire` serving module: PostgreSQL wire v3 (simple and extended query, SCRAM-SHA-256, TLS, cancel). No .NET PostgreSQL server library
exists, so the protocol is the suite's own. Like every protocol it is a front end to the one query core: it turns wire messages into a semantic
request, maps its sign-in onto the serving layer's identities and returns rows; it never opens a connection, applies a limit or formats an error itself.

**How, as decided.**

- **Semantic SQL only, never pass-through:** a PostgreSQL client writing PostgreSQL SQL against a SQL Server source would be a dialect mismatch.
- **Flat names:** every member also has a flat column name (`customer_region`; the separator configurable; `flat_name` resolves a collision, and a
  collision without it is a load error naming both members): optional keys of the semantic package's format 2.
- **`pg_catalog` and `information_schema` are emulated tool by tool**, each against recorded fixtures: `psql` and a Python client first (they need the
  least of `pg_catalog`), then Power BI Desktop's PostgreSQL connector.
- **A compatibility layer** reads a tool's SQL as a strict semantic request (an aggregate over a measure column as `MEASURE()`, an aggregate that
  contradicts the measure refused with a message). It is separate from the semantic SQL specification, in its own document versioned on its own with
  per-tool fixtures, and every translated query is shown as the canonical semantic SQL it became.
- **Saved queries on the wire:** a `semantic` schema with a saved query as a view, and a function whose arguments are prepared-statement parameters;
  the same saved query returns the same rows over Flight SQL and this endpoint.
- The serving contract gains a multi-leg authentication exchange and cancel by session. ENDPOINTS-6 reserves the listener.

**Open questions.** Which tool after Power BI; the compatibility layer's document format (registered in `docs/formats.json` when it is built).

### Quack (DuckDB's client-server protocol)

Waits for DuckDB to publish a server specification and a stability promise. The cheaper route then may be a DuckDB child process serving Quack over
views of semantic results, behind the serving layer's identities.

### Entra tokens for Power Query

For an install with Entra configured, the feeds, saved queries and OData endpoints answer an unauthenticated call with a bearer challenge naming the
tenant and accept that tenant's access tokens for the configured resource, the person linked to their home account by UPN; nothing changes when Entra
is not configured. Proven with a stand-in tenant (the challenge names the tenant; a token for another audience or tenant is refused) and a real Power
Query refresh recorded on the Excel VM. Open question: whether it waits for the Excel work or comes first, since it serves Power Query rather than
PivotTables.

### Ad hoc registrations, and the combine in a child process

Not yet (decided 2026-11-09, the owner delegated): they wait until an app or an agent needs them. **Registrations:** a state module `semantic_adhoc`
in the Gateway holds a person's registered tables (with descriptors), bindings and value maps, at the scopes `session` and `personal` (per user), with a
row cap and expiry, served as the request kinds that need them; `shared` needs an owner's approval and `governed` is a commit to the Build project, so
neither is stored there. The documents used are those of `docs/semantic/combine.md` as they are. **A screen** lets a person upload and list their
registrations; a proposal to share goes to the model's owner, who approves or rejects with a reason, and an approved map is offered as a change to the
Build project (the Store's promotion of a value map is MASTERDATA's open design). **The combine in a child process** is taken only if the in-process
limits prove too soft (a recorded incident or a load test beating `memory_mb`): a combine beyond its memory limit stops its own process and the Gateway
answers `ResourceExhausted` while another query on the same instance finishes.

### Access rules: the rest

- **Lineage draws a rules table.** A rules table is matched to the model it filters by its identity in `GatewayAccess`, but the lineage graph does not
  draw it, so a person reading a model's lineage does not see which table decides its rows. Open: an edge kind of its own, and whether the console's
  graph shows the rules' columns.
- **A screen to rotate the mask key.** The `hash` mask is an HMAC under a per-install key from the secrets store; the route and the command to rotate
  it exist, no screen does. Open: where it sits (the Gateway's Secrets screen or the access rules screen), and what a rotation does to cached answers
  and to a person comparing masked values across the rotation.
- **A `null` rule on an Analysis Services column** is turned into a hide, like a mask, but the publish answer's warning names only masks that became
  hides. Whether the warning also names nulls is undecided (owner, 2026-11-14: Analysis Services has its own row-level security, so it needs more
  careful thought).

## Shared areas touched

- `DbDataKit.Serving.Semantic`, `DbDataKit.Semantic*`, the engine registry (`engine/1`), Build's metadata schema, `Shared/web/gateway`.
- `docs/semantic/`, `docs/formats.json` (metadata schema version if bumped), `docs/ui-editors.md`.
