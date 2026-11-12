# Plan 25: surrogate keys and agent testing, what is left

Status: **in progress** (audited 2026-11-09 against the code, re-audited 2026-11-11: no row changed; nothing here was built in between, except that the Store's draft layer (plan 21 CH01) now exists, so K11's wait is over and only its own work is left). The built part (the hash, `keys:`, the per-key function and lowering, argument checks, inferred relationships, history keys, the unknown member, the generated uniqueness check) is described in [DbDataBuild/docs/keys.md](../../DbDataBuild/docs/keys.md); slices K01 to K06, K08 and K09 are built. This plan holds only unbuilt work and open questions, and keeps the two designs of sections 2 and 3 (the agent plans 16, 27 and 27a and plan 32 cite them by section, and the agent runtime they test is not built).

Audit, against the code: **K07** is open (`KeyRelationships.Infer` in `DbDataBuild.Models` is read by nothing outside its own tests; lineage, the checks and the semantic layer do not consume it). **K10** is open (`ModelDefinitionLoader` refuses `method: key_map`, naming K10). **K11** is open as far as it goes: `EntityIds` in `DbDataStore.Server` exists with its tests (`EntityIdTests`) and no code calls it, drafts do not exist (plan 21), and the SQL Server dialect maps `guid` to `uniqueidentifier`, so the owner's `BINARY(16)` column is not built. **K12** is open (no `surrogate_key(...)` inference in `DbDataBuild`). **K06b**: the uniqueness check is `KeyChecks` in `DbDataBuild.Semantic`; plan 23's check model exists (`DbDataKit.Quality`, `CheckEvaluator` in `DbDataKit.Quality.Evaluation`, and Build's `ChecksAfter` in `RefreshCommand` runs a model's checks after a refresh), so K06b no longer waits on a framework: it turns `KeyChecks` into a `check/1` entry with severity `block`; its Needs is now FQ02 and FQ09 of plan 23a, whichever is not built. Sections 2 and 3 are unbuilt designs: no `DbDataKit.Agents` and no `Microsoft.Extensions.AI` in the repository (`docs/agent-tools.md`). Slices K01 to K06, K08 and K09 are built and described in `DbDataBuild/docs/keys.md`; nothing in this plan is obsolete.

Editors: K10 and K12 are authored in the model file, which the existing row "Build model, project settings, model tests, folder settings, answers" in [docs/ui-editors.md](../ui-editors.md) already covers (form and YAML from the schema); no new screen. K11 builds nothing a person authors. Section 3's review step is the Store's change-request screen (row "Store change requests", UE14 of plan 33) and the agent screens are plan 27a's rows.

## 1. Surrogate keys, what is left

| ID | Slice | Needs | Size | Format | Done when |
| --- | --- | --- | --- | --- | --- |
| K07 | Consumers of the inferred relationships: lineage column edges, the generated relationship checks (plan 23), the semantic layer. `KeyRelationships.Infer` computes model, column, owner, key and role; nothing reads it | K03 (done) | M | none | a model calling `date_key` twice records two relationships to `dim_date` in the consumer. **Decided: lineage column edges (`derives`-level detail with the role) and the semantic layer read them; no `relationships` command** (default taken, owner delegated, 2026-11-09) |
| K10 | `method: key_map`: the map table kept in the target, the lookup lowering, new keys on load; the definition is refused today with a message naming K10 | K04 (done) | L | `build-model` (open, schemaVersion 2): `method: key_map` is already a reserved value, no new version; the map table is a table in the target, not a file | a business key keeps its integer across reloads and is never reused. **Decided by the owner:** **one table per key**, and **each channel its own map** |
| K11 | **Status: not built (re-audited 2026-11-11):** the drafts it waited for exist (plan 21 CH01, `DraftStorage`), `EntityIds` is still called by no code, the id column is not `BINARY(16)`. DbDataStore entity ids as UUID v7 made by the serving instance, and a draft keeps its id when submitted. `EntityIds.New` and `MadeAt` exist in the store server (time-ordered, 20000 ids in one millisecond distinct) and nothing outside tests calls them; the drafts of plan 21 do not exist | plan 21 CH01 (done) | S | none (a column type in the Store's storage, created by `storage create`, not a file format) | ids are time-ordered, unique across two instances in a test, and a draft keeps its id when submitted. **Decided by the owner:** the id column is **`BINARY(16)` on SQL Server** (`uniqueidentifier` orders by its own byte order, so a clustered key on it is not time-ordered) and **native `uuid` on PostgreSQL** |
| K12 | Keys defined in pure SQL: the owning model's SQL calls a generic `surrogate_key(c.source_system, c.customer_id) AS customer_key`; Build infers the definition (the name from the output column, the parts and types from the arguments) and writes it into the YAML, which stays the stored form, as `project import` writes metadata | K03 (done) | M | `build-model` (open): the inferred key definition is written into the model YAML in the form `keys:` already has; no new version | the definition is inferred and stored |
| K06b | The uniqueness check becomes one of plan 23's checks with severity `block` | plan 23a FQ02, FQ09 (both done) | S | none | the colliding-key test fails the build through the framework **Status: done** (2026-11-12): `ModelQuality.KeyChecksOf` adds a `unique` check of severity `block`, id `key.<column>.unique` (the version key for a key with history), to every model that owns a hashed key, evaluated after a refresh with the model's own checks (so a repeat fails the refresh's checks and blocks dependents); the semantic check of `connection check` stays as it was. Tested in `KeyChecksTests`; not run on an engine |

Also unverified or limited, to settle with the slice that touches it:

- **Fabric**: `HASHBYTES` and the UTF-8 collation were never run on an engine; Fabric's text is the SQL Server text.
- **Types** of a call's arguments are not checked, only their count (DuckDB casts them); a stricter check needs the bound plan.
- A key map's behaviour when several channels build (answered by "each channel its own map") and who may write it.

## 2. Testing agent operations without an outside API (design, not sliced)

Depends on the agent of plan 16.

1. **Unit tests use a scripted fake chat client.** It implements `Microsoft.Extensions.AI`'s `IChatClient` and returns scripted turns: text, tool calls, malformed tool calls, refusals. No network and no model is involved, so the tests are deterministic.
2. **Provider adapters** (OpenAI, Microsoft Foundry, Anthropic, Ollama; plan 16) are tested against recorded responses checked into the repository. A recording is refreshed by hand, never in CI.
3. **A live local model** (Ollama with a small model) is an optional CI job for end-to-end checks. It is not required for a pull request.
4. **What the agent tests must prove:**
   - **Approval gates:** every write a scripted agent attempts waits for the user's approval, and a denied step changes nothing.
   - **Acting as the user:** tool calls run with the user's own permissions and access rules (plan 22 section 2), and a forbidden call is refused.
   - **Tool schemas:** every tool's arguments and results validate against their schemas, and a malformed tool call from the model is refused cleanly, with an error the model can act on.
   - **Transcripts:** a scripted conversation produces the expected tool calls and audit records, checked as golden files.

## 3. Agent-built mappings for semantic queries (design, not sliced)

When a semantic query joins sources whose dimension values do not match (`NE` against `Northeast`), the agent can build the mapping as part of answering (the owner's addition, 2026-10-27).

1. The agent proposes the mapping as a **DbDataStore draft** (plan 21), and the query uses that draft.
2. **The user reviews and approves each step,** as with every agent action.
3. The mapping can later be **promoted to a managed mapping entity**, the same path as a query's temporary mapping tables (plan 15, M8).
4. **Tested with the scripted fake:** a scripted conversation hits the mismatch; the agent proposes a draft map, the user approves, and the query reruns with matching values; a denied map leaves the query unanswered and nothing stored.
