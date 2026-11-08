# Plan 25: surrogate keys and agent testing, what is left

Status: **in progress**. The built part (the hash, `keys:`, the per-key function and lowering, argument checks, inferred relationships, history keys, the unknown member, the generated uniqueness check) is described in [DbDataBuild/docs/keys.md](../../DbDataBuild/docs/keys.md); slices K01 to K06, K08 and K09 are built. This plan holds only unbuilt work and open questions, and keeps the two designs of sections 2 and 3.

## 1. Surrogate keys, what is left

| ID | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| K07 | Consumers of the inferred relationships: lineage column edges, the generated relationship checks (plan 23), the semantic layer. `KeyRelationships.Infer` computes model, column, owner, key and role; nothing reads it | K03 (done) | M | a model calling `date_key` twice records two relationships to `dim_date` in the consumer. **Open:** whether they appear in lineage as column edges, as a `relationships` command, or only in the semantic layer |
| K10 | `method: key_map`: the map table kept in the target, the lookup lowering, new keys on load; the definition is refused today with a message naming K10 | K04 (done) | L | a business key keeps its integer across reloads and is never reused. **Decided by the owner:** **one table per key**, and **each channel its own map** |
| K11 | DbDataStore entity ids as UUID v7 made by the serving instance, and a draft keeps its id when submitted. `EntityIds.New` and `MadeAt` exist in the store server (time-ordered, 20000 ids in one millisecond distinct) and nothing outside tests calls them; the drafts of plan 21 do not exist | plan 21 drafts | S | ids are time-ordered, unique across two instances in a test, and a draft keeps its id when submitted. **Decided by the owner:** the id column is **`BINARY(16)` on SQL Server** (`uniqueidentifier` orders by its own byte order, so a clustered key on it is not time-ordered) and **native `uuid` on PostgreSQL** |
| K12 | Keys defined in pure SQL: the owning model's SQL calls a generic `surrogate_key(c.source_system, c.customer_id) AS customer_key`; Build infers the definition (the name from the output column, the parts and types from the arguments) and writes it into the YAML, which stays the stored form, as `project import` writes metadata | K03 (done) | M | the definition is inferred and stored |
| K06b | When plan 23's check framework exists, the uniqueness check becomes one of its checks with severity `block` | plan 23 | S | the colliding-key test fails the build through the framework |

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
