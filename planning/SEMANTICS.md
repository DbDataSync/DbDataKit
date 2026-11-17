# SEMANTICS: the semantic layer, the rest

## Goal

What a semantic model publishes is checked, described with types and decimals, carried in Build's metadata, promotable from a saved query and
configurable by form, and Analysis Services sources are bounded and cancellable. How the layer works is `docs/semantic/architecture.md`.

## What exists today

- Semantic models authored in Build projects, published from the Workspace, hosted by the Gateway; the engine registry; semantic SQL; parameters;
  saved queries; run records; the console's model browser, explorer and saved-queries screens; SSAS as a source.
- The Gateway reads the registry for most engines; a few of its old engine switches remain.
- Publish checks the tables of a model but not every mapped column.
- `describe` has no column types; answers carry decimals as `double`.
- Build's metadata documents do not carry the semantic models; `project show semantic` does not say where a relationship came from.
- The `semantic:` section of the Gateway's `server.yaml` has a form for scalars only (no hand-set models, no SSAS connections).
- A saved query cannot be promoted into its model's `queries:`.
- SSAS: no row cap in the DAX text; the executor is synchronous with no timeout from the data source.

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| SEMANTICS-1 | The Gateway reads the registry for every engine: the old engine switches retired | none | S | `DbDataGateway.Tests`: every `supported` engine opens a data source, every other is refused naming the note |
| SEMANTICS-2 | Publish checks every mapped column (`SELECT <columns> FROM <table> WHERE 1 = 0` per table) | none | S | a catalog mapping a column the data source lacks is refused naming `table.column`; the serving version is unchanged |
| SEMANTICS-3 | `describe` lists column types and a saved query's output columns | none | S | `SemanticNounTests`: `describe` has the types; the CLI document validates against its schema |
| SEMANTICS-4 | Decimals stay decimals: a measure over a decimal column is Arrow `decimal128` with the source's scale | none | M | a decimal measure round-trips 38 digits through Flight; the fixtures' rows equal the old ones read as decimals |
| SEMANTICS-5 | Build's metadata documents carry the semantic models; `project show semantic` says whether a relationship came from a key or the file | none | S | the retail template validates with the key; a file relationship and a key relationship are told apart |
| SEMANTICS-6 | The `semantic:` form: hand-set models, the data source of each source and the SSAS connections (secret references) | none | M | vitest: the fields round-trip the sample file; Playwright: save a change and the Gateway applies it without a restart |
| SEMANTICS-7 | Promote a saved query into its model: the entry the person copies into the project's file, compiled and published | AUTHORING-5 | S | the promoted entry loads and runs with the same rows; Playwright: promote, paste, publish |
| SEMANTICS-8 | A row cap in the DAX text (`TOPN (n + 1, ...)`) when no `limit` is asked | none | S | the `semantic-ssas` job: a query of a thousand-row table with `limit` 10 returns 10 and `truncated` |
| SEMANTICS-9 | The SSAS executor: asynchronous, one connection per host source, the command timeout from the data source, a role that can write refused at start | none | M | `SsasExecutorGuardTests` and the `semantic-ssas` job: a slow query is cancelled by the timeout; a writing role is refused naming it |

## Needs from other subjects

- AUTHORING-5: the `queries:` editor in a build project.

## Proposed decisions

None.

## Shared areas touched

- `DbDataKit.Serving.Semantic`, `DbDataKit.Semantic*`, the engine registry (`engine/1`), Build's metadata schema, `Shared/web/gateway`.
- `docs/semantic/`, `docs/formats.json` (metadata schema version if bumped), `docs/ui-editors.md`.
