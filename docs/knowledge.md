# Knowledge and search: what exists today

**DbDataKnowledge is partly built: the library, not the product.** `DbDataKit.Knowledge` reads the item format; there is no index, no embedding connection, no `knowledge` command and no knowledge app. [Plan 27](plans/27-knowledge-agents-integrations-work.md) section 2 is the decided design; [plan 27a](plans/27a-knowledge-slices.md) slices it. This page lists the pieces that are built and that the design stands on, so a reader does not mistake the design for a feature.

## What a person or an agent can use now

| Need | What exists | Where |
| --- | --- | --- |
| Find a node of the graph by name or id, and see what feeds it and what it feeds | the Lineage page of the console (search by name or id; marks upstream and downstream) and the `lineage` commands (`graph`, `impact`, `provenance`) | [lineage.md](lineage.md) |
| Read what a semantic model offers: dimensions, attributes, measures with their synonyms and descriptions, saved queries | `semantic describe` (also an agent tool, [agent-tools.md](agent-tools.md)); the model, dimensions, attributes and measures carry `description` and `label`, attributes and measures `synonyms`, measures `format`, and the model an `ai_context` text, in the catalog schema | [semantic/catalog.md](semantic/catalog.md) |
| One standard form for a question asked of a model | the JSON **request** (measures, attributes, filters with relative periods such as `last_complete`, order, limit); semantic SQL is translated into it (`DbDataKit.Semantic/Sql`) | [semantic/query.md](semantic/query.md), [semantic/semantic-sql.md](semantic/semantic-sql.md) |
| Saved queries that keep their versions and can be shared | a model's own saved queries in its catalog, and a person's saved queries in the serving layer's state (`SavedQueryStore`: versions, sharing with a user, a group or everyone, lookup as the caller) | [semantic/catalog.md](semantic/catalog.md); the surface that saves them belongs to apps |
| An engine that can hold vectors | the state engines declare a **feature** (`EngineFeature.Vector`: SQL Server 2025's vector type, PostgreSQL's `pgvector`); a module that needs it refuses to be created where it is missing and says how to fix it; the test environment runs SQL Server 2025 and PostgreSQL with `pgvector` | [DbDataKit.State.Engines README](../Shared/src/DbDataKit.State.Engines/README.md), `testenv/docker-compose.yml` |
| Descriptions of what Build builds | Build's metadata documents (one per model of any kind: title, description, tags, properties, columns, what it reads and what reads it), shown by `dbdatabuild project show metadata` and stored in the target's tracking tables on `connection publish` | [DbDataBuild/docs/concepts.md](../DbDataBuild/docs/concepts.md) |

## What the library does (`DbDataKit.Knowledge`, plan 27a)

| Piece | What it does | Where |
| --- | --- | --- |
| The item, `knowledge/1` (KN01) | a Markdown file with a front matter (role: reference, instruction or guardrail; `about` subjects; `for` audience, agent and channel; `owner`; `chunking`), read strictly with file and line; a file in a project's `knowledge/` folder is about that project; `prompting`, `limits` and `data-rules` are refused with the place they belong (the install policy) | `KnowledgeItemLoader`, `schemas/knowledge.schema.json` |
| Folder defaults (KN01) | `_knowledge.yml` gives the folders beneath it defaults, nearest wins | `KnowledgeProject.Load` |
| Subjects and orphans (KN02) | a subject is checked against the catalog when an item is saved (never heard of: refused at its line); an item whose subjects are all gone is orphaned, reaches nobody and is listed | `SubjectResolver`, `ISubjectCatalog` |

[Shared/src/DbDataKit.Knowledge/README.md](../Shared/src/DbDataKit.Knowledge/README.md) describes each piece.

## What does not exist

| Designed in plan 27 section 2 | State today |
| --- | --- |
| The `knowledge/1` item format (role, scope, owner), `_knowledge.yml` defaults, knowledge projects in git | **built as a library** (above); not published, not served, no editor screen yet |
| Generated knowledge from published definitions; the `build-metadata` kind (Build publishing its metadata to the serving group) | not started; the published kinds it would read are `semantic-model`, `entity-model`, `data-source`, `app` |
| Chunking, embedding models (built in, Ollama, OpenAI-compatible), the vector index per channel, `sqlite-vec` | not started; only the engine feature check exists |
| `knowledge search`, `show`, `about`, `check`, `context`, `suggest`, `index` commands | not started |
| The serving layer's `knowledge` module, suggestions and approvals at the edge, the knowledge app, `@dbdatakit/knowledge-ui` | not started |
| Verified queries (`verified-query` items, "Verify this answer", re-verification when a model changes) | not started; saved queries and the JSON request are the pieces they pin |
| Access-filtered search | not started; the identity home maps directory groups to attributes (plan 22a GP04, built), but the row and column rules a search would apply are not (GP01 and GP11 to GP15 wait for the rule language, QG2) |
| `catalog search` and `catalog show` (were plan 22a GP25) | dropped (owner, 2026-11-09): the `knowledge` commands of [27a](plans/27a-knowledge-slices.md) KN16 are the search over generated knowledge |
| Workspace search (exact text in a project's files) | not started; needs the Workspace server (plan 22a GP31) |
