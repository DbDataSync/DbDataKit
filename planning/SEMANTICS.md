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
| SEMANTICS-11 | An agent's Gateway address from the environment (`DbDataBuild/DESIGN.md`, "The `semantic` commands"): Build's `semantic describe\|query\|explain\|members` read `DBDATAKIT_GATEWAY_ADDRESS` (and the CA file's variable beside it) when `--address` is absent; `ui mcp` keeps withholding `--address`, `--insecure`, `--ca-file` and `--server-dir`; with neither, the sentence names the variable | none | S | `BuildSemanticNounTests`: with the variable set and no `--address` a verb runs against the stand-in session at that address; a tool call cannot set an address (the MCP tool's schema has no such argument); with neither, the sentence names `DBDATAKIT_GATEWAY_ADDRESS`; `check_env_names.py` passes |

## Needs from other subjects

- AUTHORING-5: the `queries:` editor in a build project.

## Proposed decisions

None open.

## Open designs

Not scheduled. Each becomes tasks only when the owner schedules it.

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
