# Plan 25: surrogate keys and agent testing

Status: **direction decided by the owner (2026-10-27)**, one question at a time. Not sliced yet.

## 1. Surrogate keys

1. **Build keys are a deterministic hash by default, with a key map as an option.**
   - **The default key** is a hash of the normalized business key. It comes out the same on every engine, in every channel and on every
     rebuild. That is required because a test build reads unchanged production tables (plan 21 section 2c), so keys must match across
     channels.
   - **The option** is a key-map table that assigns a stable integer to each business key once and never reuses it, for tools that want
     small integer keys. It is kept by Build in the target, and it is shared across channels the same way production tables are read.
2. **The hash is a 64-bit integer:** the first 8 bytes of SHA-256 over the normalized business key, stored as `BIGINT`.
   - **Normalization** is defined once, in the shared dialect code, and covers type, trimming, case rules and the separator between
     key parts, so every engine computes the same value.
   - **Collision guard:** every hashed key gets a generated uniqueness test (plan 23 data-quality checks, severity *block*).
3. **History versions** (slowly changing dimensions, Build's `ColumnHistory`) are keyed by `hash(business key, valid-from)`. A
   **durable key**, `hash(business key)`, is kept beside it so facts and reports can join across versions.
4. **DbDataStore entity ids are UUID v7.**
   - They are time-ordered, so indexes stay friendly.
   - They are made by the serving instance that takes the write, with no coordination, so they are safe across a serving group's
     instances and in drafts.
   - A draft row submitted as a change request (plan 21) keeps its id.

## 2. Testing agent operations without an outside API

1. **Unit tests use a scripted fake chat client.**
   - It implements `Microsoft.Extensions.AI`'s `IChatClient` and returns scripted turns: text, tool calls, malformed tool calls,
     refusals.
   - No network and no model is involved, so the tests are deterministic.
2. **Provider adapters** (OpenAI, Microsoft Foundry, Anthropic, Ollama; plan 16) are tested against recorded responses checked into the
   repository. A recording is refreshed by hand, never in CI.
3. **A live local model** (Ollama with a small model) is an optional CI job for end-to-end checks. It is not required for a pull request.
4. **What the agent tests must prove:**
   - **Approval gates:** every write a scripted agent attempts waits for the user's approval, and a denied step changes nothing.
   - **Acting as the user:** tool calls run with the user's own permissions and access rules (plan 22 section 2), and a forbidden call
     is refused.
   - **Tool schemas:** every tool's arguments and results validate against their schemas, and a malformed tool call from the model is
     refused cleanly, with an error the model can act on.
   - **Transcripts:** a scripted conversation produces the expected tool calls and audit records, checked as golden files.

## 3. Agent-built mappings for semantic queries

When a semantic query joins sources whose dimension values do not match (`NE` against `Northeast`), the agent can build the mapping as
part of answering. This is the owner's addition, 2026-10-27.

1. The agent proposes the mapping as a **DbDataStore draft** (plan 21), and the query uses that draft.
2. **The user reviews and approves each step,** as with every agent action.
3. The mapping can later be **promoted to a managed mapping entity**, the same path as a query's temporary mapping tables (plan 15
   section 7, M8).
4. **Tested with the scripted fake:**
   - a scripted conversation hits the mismatch;
   - the agent proposes a draft map, the user approves, and the query reruns with matching values;
   - a denied map leaves the query unanswered and nothing stored.
