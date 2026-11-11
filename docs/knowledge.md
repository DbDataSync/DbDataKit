# Knowledge and search: what exists today

**DbDataKnowledge is partly built: the library and a search by words, not the product.** `DbDataKit.Knowledge` reads the item format and `DbDataKit.Knowledge.Index` finds chunks by their words on a state database; there is no vector index, no embedding connection, no `knowledge` command and no knowledge app. [Plan 27](plans/27-knowledge-agents-integrations-work.md) section 2 is the decided design; [plan 27a](plans/27a-knowledge-slices.md) slices it. This page lists the pieces that are built and that the design stands on, so a reader does not mistake the design for a feature.

## What a person or an agent can use now

| Need | What exists | Where |
| --- | --- | --- |
| Find a node of the graph by name or id, and see what feeds it and what it feeds | the Lineage page of the console (search by name or id; marks upstream and downstream) and the `lineage` commands (`graph`, `impact`, `provenance`) | [lineage.md](lineage.md) |
| Read what a semantic model offers: dimensions, attributes, measures with their synonyms and descriptions, saved queries | `semantic describe` (also an agent tool, [agent-tools.md](agent-tools.md)); the model, dimensions, attributes and measures carry `description` and `label`, attributes and measures `synonyms`, measures `format`, and the model an `ai_context` text, in the catalog schema | [semantic/catalog.md](semantic/catalog.md) |
| One standard form for a question asked of a model | the JSON **request** (measures, attributes, filters with relative periods such as `last_complete`, order, limit); semantic SQL is translated into it (`DbDataKit.Semantic/Sql`) | [semantic/query.md](semantic/query.md), [semantic/semantic-sql.md](semantic/semantic-sql.md) |
| Saved queries that keep their versions and can be shared | a model's own saved queries in its catalog, and a person's saved queries in the serving layer's state (`SavedQueryStore`: versions, sharing with a user, a group or everyone, lookup as the caller) | [semantic/catalog.md](semantic/catalog.md); the surface that saves them belongs to apps |
| An engine that can hold vectors | the state engines declare a **feature** (`EngineFeature.Vector`: SQL Server 2025's vector type, PostgreSQL's `pgvector`); a module that needs it (the planned vector module `knowledge_vectors`; the module `knowledge` searches by terms and needs no vectors: SQLite FTS5, PostgreSQL `tsvector` `simple`, SQL Server term matching, owner 2026-11-11) refuses to be created where it is missing and says how to fix it; the test environment runs SQL Server 2025 and PostgreSQL with `pgvector` | [DbDataKit.State.Engines README](../Shared/src/DbDataKit.State.Engines/README.md), `testenv/docker-compose.yml` |
| Descriptions of what Build builds | Build's metadata documents (one per model of any kind: title, description, tags, properties, columns, what it reads and what reads it), shown by `dbdatabuild project show metadata` and stored in the target's tracking tables on `connection publish` | [DbDataBuild/docs/concepts.md](../DbDataBuild/docs/concepts.md) |

## What the library does (`DbDataKit.Knowledge`, plan 27a)

| Piece | What it does | Where |
| --- | --- | --- |
| The item, `knowledge/1` (KN01) | a Markdown file with a front matter (role: reference, instruction or guardrail; `about` subjects; `for` audience, agent and channel; `owner`; `chunking`), read strictly with file and line; a file in a project's `knowledge/` folder is about that project; `prompting`, `limits` and `data-rules` are refused with the place they belong (the install policy) | `KnowledgeItemLoader`, `schemas/knowledge.schema.json` |
| Folder defaults (KN01) | `_knowledge.yml` gives the folders beneath it defaults, nearest wins | `KnowledgeProject.Load` |
| Subjects and orphans (KN02) | a subject is checked against the catalog when an item is saved (never heard of: refused at its line); an item whose subjects are all gone is orphaned, reaches nobody and is listed; the catalog is read from the lineage graph (semantic models and their measures and dimensions, apps, integrations, Store models, connections) beside what an administrator says | `SubjectResolver`, `ISubjectCatalog`, `LineageSubjectCatalog`, `CompositeSubjectCatalog` |
| Scope matching (KN03) | which instructions and guardrails apply to what is in play, broad to narrow, guardrails apart, by audience, agent and channel | `ScopeMatcher` |
| The install knowledge project (KN35) | a template with `shared/`, `build/`, `sync-and-build/` and the administrators' `policy/` folder; `policy/` holds only guardrails owned by `administrators` | `InstallKnowledge` |
| Chunking (KN04) | long references split at headings, then paragraphs, then sentences; tables and code are never cut; the chunk's hash is of what is embedded, so only a changed chunk is embedded again | `Chunker` |
| The index on a state database (KN07, text-search half) | items, subjects and chunks per channel in the `knowledge` state module, found by their words with the engine's own text search (SQLite FTS5, PostgreSQL `tsvector`, a term match on SQL Server), under the same access filter, with the hash check that does not index an unchanged item again; no model and no vectors | `KnowledgeModule`, `KnowledgeStore`, `StateKnowledgeIndex` (`DbDataKit.Knowledge.Index`) |
| Generated knowledge (KN12, the renderer and the index half) | what a published semantic model says about itself, one small item per subject with a stable id and the subject's own access, kept in step with the definition (an unchanged item is not indexed again, a dropped one is retired); the list of what has no description | `GeneratedKnowledge`, `GeneratedSync` |
| Search and the access filter (KN09, with the fake of KN05) | a question is embedded and the nearest chunks are found among those the caller may see (a role on the subject; fail closed on a gone or unknown subject; an item with no subject follows its `for`), at most three chunks per item, adjacent ones joined | `KnowledgeSearch`, `KnowledgeAccess`, `InMemoryKnowledgeIndex`, `FakeEmbeddingGenerator` |

[Shared/src/DbDataKit.Knowledge/README.md](../Shared/src/DbDataKit.Knowledge/README.md) describes each piece.

## What does not exist

| Designed in plan 27 section 2 | State today |
| --- | --- |
| The `knowledge/1` item format (role, scope, owner), `_knowledge.yml` defaults, knowledge projects in git | **built as a library** (above); not published, not served, no editor screen yet |
| Generated knowledge from published definitions (the host that subscribes); the `build-metadata` kind (Build publishing its metadata to the serving group) | the renderer for a semantic model is built (above); nothing subscribes to published definitions yet and entity models, data sources and apps are not rendered; the published kinds are `semantic-model`, `entity-model`, `data-source`, `app` |
| Embedding models (built in, Ollama, OpenAI-compatible), the vector index per channel, `sqlite-vec` | not started; only the engine feature check exists and search by words (above) |
| `knowledge search`, `show`, `about`, `check`, `context`, `suggest`, `index` commands | not started |
| The serving layer's `knowledge` module, suggestions and approvals at the edge, the knowledge app, `@dbdatakit/knowledge-ui` | not started |
| Verified queries (`verified-query` items, "Verify this answer", re-verification when a model changes) | not started; saved queries and the JSON request are the pieces they pin |
| Access-filtered search | not started; the identity home maps directory groups to attributes (plan 22a GP04, built), but the row and column rules a search would apply are not (GP01 and GP11 to GP15 wait for the rule language, QG2) |
| `catalog search` and `catalog show` (were plan 22a GP25) | dropped (owner, 2026-11-09): the `knowledge` commands of [27a](plans/27a-knowledge-slices.md) KN16 are the search over generated knowledge |
| Workspace search (exact text in a project's files) | not started; needs the Workspace server (plan 22a GP31) |

## The console's knowledge screens (plan 27a KN28, KN36, KN37, KN38)

The console reads two knowledge projects in its directory, `install-knowledge/` (the template of KN35: the install's guardrails in `policy/`, owned by the administrators) and `knowledge/` (items about the install's objects), and `knowledge-subjects.json` (what exists to be about and what is gone: read from the joined lineage graph by the button "Read from the lineage graph" on the worklists tab, which marks an object gone only when every server answered, plus what an administrator says by hand, which the reading never changes). The page `Knowledge` shows:

- **Index and context**: items, chunks and failures per project, orphans, and what an agent would be given for what is in play, each part with its tokens and share of the budget. The page says that **no index is built**: there is no search here.
- **Worklists**: per owner, the items whose subject is gone, with the actions: point it at another object (only that line of the file changes) or retire it.
- **Items and preview**: an item edited as text with the strict reader's diagnostics, split chunk by chunk with the heading path and tokens, the chunks that changed against the saved text, the agents it reaches and whether it would push one over its budget, and what would be given for a set of subjects in play (in order, with what is left out and why).
- **Install knowledge**: make the project from the template, add a guardrail through a form that fixes its role and owner; a `prompting`, `limits` or `data-rules` key in any item is refused and names the install policy.
