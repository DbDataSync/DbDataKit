# The semantic layer in the suite

How semantic models are authored, deployed, served and governed across the products. The model and query formats are in [core.md](core.md), [catalog.md](catalog.md), [query.md](query.md) and [semantic-sql.md](semantic-sql.md); this page is about where each part lives. In the suite the thing is called a **semantic model** (files under `semantic/`); "catalog" stays the word for a database's catalog. In documentation about Power BI or SSAS, say "Power BI semantic model" or "suite semantic model" where both appear.

The engine came from QueryIntentSpec (QIS) and is now part of this repository. It is an engine, not a contract with the old repository: its documents, schemas and fixtures are the starting point of these docs and tests and change deliberately, with their fixtures, in the same commit.

```
sources -> DbDataSync -> warehouse -> DbDataBuild -> marts + metadata -> semantic layer -> agents, BI, console
         (replicate)               (model, deploy,                       on the serving layer   (Flight SQL, semantic SQL)
                                    refresh, test)                       (DbDataProxy today)
```

## Where models live: Build projects (R1)

- A Build project has a `semantic/` folder (`semantic/**/*.yml`, schema `DbDataBuild/schemas/semantic.schema.json`). A file is a catalog written **without its physical layer**: datasets name Build models, not physical tables.
- **The physical layer is generated, never written.** `DbDataBuild.Semantic` builds, per model and connection, a table (physical name = the model's name, columns and logical types from its definition, key = its `grain`) and instantiates the relationships per connection; the engine's `CatalogLoader` reads the result. `project show semantic` lists the sources, what every attribute and measure is bound to, the generated tables, relationships and hierarchies.
- **Compile checks it.** `project compile` validates the semantic files against the model graph (`DbDataBuild.Semantic/SemanticProject.cs`): a column renamed in a model breaks the compile at the semantic file's line.
- **Data checks are Build tests.** `SemanticChecks` generates one SELECT per hierarchy level pair (a member with two parents) and two per relationship (orphan keys; a one side that is not unique), so a broken hierarchy shows when the data changes. A calendar is a Build template, not a hand-kept table.
- **Measures stay in the semantic files.** Build validates them and does not own them. A source Build does not model (SSAS, a database nobody imported) may still be declared by hand in a semantic file.
- A built model can declare `foreign_keys:` as a mapped model does; they are checked by a generated test and never created in the database, and the semantic model reads its relationships from them (plan 19 J29, decided by the owner).

## Serving: the serving layer hosts, Build deploys (R2, R2a)

**One connection, two views.** A warehouse is a target for Build and a source for Proxy; it is defined once, in the Build project (`connection/1`: engine, server, database, TLS, and Build's read and write logins, kept apart).

- **Build publishes it.** `POST /api/v1/projects/{p}/proxies/{proxy}/publish-model` on a Build server publishes the connections a model reads, then the model. What reaches the Proxy is the connection with **the read login only**, sent once over the paired, pinned TLS channel into the Proxy's own secret store; the data source is written `access: readonly`. A test (`BuildServerProxyTests`) asserts the write login never appears in a request.
- **Ownership is split by concern.** Where the server is and how to log in to read belong to Build. The serving policy (`extensions.proxy`: limits, statement classifier, which identities, pass-through) belongs to Proxy and is never touched by a publish.
- **An existing data source is not taken over silently**: a publish that finds one stops and returns the call that adopts it (`POST /api/v1/data-sources/{name}/adopt`); after that Build manages its connection fields.
- **A Build server is a paired client of the Proxy**, as a console is (a code from `dbdataproxy server pair`, entered on the Build side), with the fixed ceiling **Publisher**: it may create data sources and models and change or remove only those its server made, and map models only to data sources it made. The token names the person who asked; the Proxy's audit records them.
- **The package.** A semantic package is the compiled catalog with its physical layer, source to data source. The Proxy (`PUT/GET/DELETE /api/v1/semantic-models/{name}`, `POST .../rollback`) loads it, checks it with a test query per data source, switches to it atomically, keeps the earlier versions under `semantic-models/` of its data directory and reloads them at start. A package that fails leaves the old one serving.
- **Semantic queries are Flight SQL queries.** `DbDataKit.Serving.Semantic` (`services.WithSemantic(models)`) wraps a serving host's core services. A request on the source `semantic:<model>` (`x-datasource`) is JSON when the text starts with `{`, otherwise semantic SQL. The answer is an Arrow table with `qis.*` schema metadata (status, request hash, level, warnings, remaining, resolved, descriptor). Every source scan goes to the host's own `IServingQueries` **as the caller**, so access rules, the read-only classifier, limits and the failure model are the host's. `{"kind":"describe"}`, `{"kind":"members"}` and `EXPLAIN ...` are statements on the model source; there is no custom Flight action. The Proxy reads a `semantic:` section of `server.yaml` (models, sources, combine limits) and applies a changed file without a restart; a catalog that does not load keeps the old model serving.
- **Source executors** are `DbDataKit.Semantic.Connections` (an `ISourceExecutor` over `DbDataKit.Connections`) and `DbDataKit.Semantic.Ssas` (Windows; other systems report `dax_execution_unavailable`). **SSAS is a supported source**; its DAX path runs against a real Analysis Services in the `semantic-ssas` CI job. Its connection is the server account's, so a data source's access rules do not reach it.
- **The combine** (union, spread, rollup, allocate, transforms, totals) runs in DuckDB, sandboxed like `Connections.DuckDb`. Limits are set by `semantic.combine` (`memory_mb` 1024, `timeout_seconds` 60, `max_input_rows` 5,000,000); a stop is answered as Flight `ResourceExhausted`.

## The packages (R3)

| Package | Holds | Used by |
| --- | --- | --- |
| `DbDataKit.Semantic.Abstractions` | requests, results, descriptors, errors, `ISourceExecutor`, Arrow rows | everyone |
| `DbDataKit.Semantic` | catalog, validator, planner, emitters (T-SQL, PostgreSQL, DuckDB, DAX through `Ssas`), combine, sandbox, semantic SQL (`Sql/`), authoring format | Proxy, Build |
| `DbDataKit.Semantic.Connections` | executor over the shared connections | serving hosts |
| `DbDataKit.Semantic.Ssas` | the DAX executor | Proxy on Windows |
| `DbDataKit.Semantic.Extraction` | spreadsheet extraction | uploads, agents |
| `DbDataKit.Semantic.Cli` | the `semantic describe\|query\|explain\|members` noun | the Proxy's command surface |
| `DbDataKit.Serving.Semantic` | the semantic module of a serving host | Proxy |

Emitters write through `ISqlDialect`; the fixtures run on real SQL Server and PostgreSQL from `testenv/`.

## Agents and the console (R4)

Agents use `semantic describe|query|explain|members`, mounted in the Proxy and offered by `ui mcp`. The password comes from `DBPROXY_PASSWORD`, never from a flag an agent would put in a tool call. The console's semantic module is not built (it waits on the console reaching a Proxy for queries). QIS scopes map onto the suite's: `session` and `personal` are per-user state, `shared` needs an owner's approval, and **`governed` means committed to the Build project**, so versioning and review are the project's.

## One engine registry (R5)

`DbDataKit.Connections.Descriptor` holds `engine/1` descriptors (`engines/*.engine.yaml`, nine built in: ansi, sqlserver, postgres, mysql, oracle, duckdb, fabric, odbc, flightsql): dialect, provider, catalog reader and a status per role (Sync source, Sync target, Build target, Build origin, Proxy source, semantic emitter, master data store: `supported`, `unverified`, `planned`, `no`; the last three need a note). `EngineRegistry` refuses a set that lacks a descriptor for a dialect, repeats an id or alias, or names an unknown role, with the file and line. [docs/engine-support.md](../engine-support.md) is generated from it by `EngineSupportTableTests`, which fails when the checked-in file differs. The products' own engine enums and README tables do not read it yet.

## Semantic SQL (R6)

A subset of DuckDB SQL with `MEASURE()`, translated to and from the JSON request, one canonical text per request; see [semantic-sql.md](semantic-sql.md). `FROM` names the semantic model, conformed dimensions are top-level structures, facts are namespaces, and anything outside the subset is refused with a named code.

## Decisions

- Semantic models are authored and deployed by Build and hosted by the serving layer; the Proxy never authors.
- The engine is a shared library, integrated before any semantic SQL work.
- Name: "semantic model".
- Ad hoc data (registrations, bindings, value maps) and extraction are not in the first release: governed semantic queries come first. The engine has them, the serving module does not serve them.
- SSAS is a supported source.
- Protocols: Postgres wire before Quack; XMLA with the MDX work; Quack waits for a published DuckDB server specification. See plan 14 for what is left.

## Rules that hold

- A package is validated against the live catalog before it serves; a mismatch is refused with the table named. (Today the check is a test query per data source; a check that every mapped table and column exists is not built.)
- Identity is part of every cache and saved-result key, or one user's numbers reach another. (No semantic result cache exists today; the rule binds the first one.)
- Build keeps three query compilers apart: Build writes table SQL, the semantic engine writes aggregate-query SQL over Build's tables, the serving layer writes none.
- The federated combine must not take the service down: limits today, a child process if they prove too soft.
