# MASTERDATA: the Store module's change feed, ids and instances

## Goal

The Gateway's store module publishes a change feed that Runner triggers and integrations follow; entity ids are UUID v7 with native column types; Store
model instances share entities safely and the contract step covers every change; entities can be read over Flight SQL; inferred key relationships have
consumers. How the Store works is `DbDataStore/README.md`.

## What exists today

- `model/1` and `model/2`, validated writes, history, audit, change requests and the draft layer, served by the Gateway's store module.
- A Runner trigger kind `store_change` exists and reports "waiting for a change source"; there is no change feed on the store module.
- Entity ids: `EntityIds` was deleted with the Store server; the id column is not `BINARY(16)` or `uuid`.
- Instances of a model: the expand step and the contract step for one copy are built; shared entities in `column` mode, instance-scoped tokens and
  roles, retyping, mode moves, the consumer refusal and the contract screen are not.
- `KeyRelationships.Infer` (Build) computes inferred relationships; nothing reads them.
- Entities are not Flight SQL sources.
- Store drafts (the draft layer) are built; the console's entity editor opens one only by its id typed in the URL, and no browser story covers drafts.
- An entity's `role:` (`dimension`, `mapping`, `calendar`) is read and validated; nothing consumes it.
- The store module's `/schema` route lists every column, including one a column rule hides from the caller (the reads apply the rules).

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| MASTERDATA-1 | A change feed on the store module: each committed write and decided change request as an event with a position, read by a cursor over the management API as a linked Runner (path 2) | TRUSTZONES-1 | M | a Runner's `store_change` trigger starts a task once per change; a restarted reader resumes from its position |
| MASTERDATA-2 | Entity ids as UUID v7 stored as `BINARY(16)` on SQL Server and `uuid` on PostgreSQL, in `DbDataKit.DataModel`'s storage (`ModelStorage`) and the group store API; drafts keep the same ids | none | M | Store conformance on both engines: ids are time-ordered UUIDs of the native type; a draft row keeps its id when submitted |
| MASTERDATA-3 | Consumers of inferred relationships: lineage column edges with the role, and the semantic layer reads them | none | S | the console's graph shows a key edge between two models; a semantic model joins on an inferred key without a file relationship |
| MASTERDATA-4 | Instances, the rest: shared entities in `column` mode, instance-scoped tokens and roles, retype and mode moves, the consumer refusal, the contract screen beside the Storage tab; the store module serves a model's instance copies (`instances: schema \| connection \| column`), not only the default copy, reads a token's scope narrowing, and applies a batch of writes as one change set (`WriteAsync` on `IServingEntities`) | none | L | an instance reads a shared entity and cannot write it; a token scoped to A is refused on B; the store module reads and writes instance B's copy through its route; a change set of three writes with one invalid applies none; a model moves from `schema` to `column` with its rows (SQL Server and PostgreSQL); Playwright of the contract screen |
| MASTERDATA-5 | Entities as Flight SQL sources: `SELECT ... FROM` an entity, now or as of a date, as the caller, with the model's roles and rules | none | M | a Flight SQL query of an entity returns the rows the caller may read; `AS OF` returns history |
| MASTERDATA-6 | The semantic layer reads entity roles: an entity with `role: dimension`, `mapping` or `calendar` is used by a semantic model as a governed dimension, a value map (or bridge, weighted relation) or a calendar through the generated physical layer, beside MASTERDATA-3's inferred keys | MASTERDATA-3 | M | a semantic query joining `NE` to `Northeast` reads a `region_map` entity with `role: mapping`; a `calendar` entity serves a model's time dimension; an entity whose role does not fit its shape is refused at publish naming the entity |
| MASTERDATA-7 | Drafts in the console: a picker of a model's drafts (per channel and preview) on the Store entity editor instead of typing the draft id in the URL, and the drafts' browser story | none | S | vitest of the picker; Playwright: write a row into a draft, find the draft in the picker, see the row only there, submit it as a change request, discard another |
| MASTERDATA-8 | The store module's `/schema` route applies column rules (low priority, owner 2026-11-18): a column hidden from the caller is not listed, and a masked or nulled one is described as the reads describe it | none | S | a person with a `hide` rule on `salary` asks the schema route and finds no `salary`; another person sees it; a `mask` rule shows the column marked as masked |

## Needs from other subjects

- TRUSTZONES-1: the link the Runner reads the feed over.

## Proposed decisions

None.

## Shared areas touched

- `DbDataKit.Store`, `DbDataKit.DataModel`, `DbDataKit.Serving.*` (store module, Flight SQL), `DbDataKit.Lineage`, `DbDataKit.Runner` (trigger).
- `docs/formats.json` (`model` if the id type changes the schema), `docs/ui-editors.md`, `DbDataStore/README.md`.
