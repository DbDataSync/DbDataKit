# Plan 25: surrogate keys and agent testing

Status: **direction decided by the owner (2026-10-27)**, one question at a time. Sliced in section 4 (surrogate keys; the agent-testing sections are not sliced).

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

## 1a. How a model specifies its keys (decided, owner, 2026-10-27)

Declaring keys over a model's output columns does not work: a fact table often lacks some of a dimension's business-key columns, or
reaches them only through a join. So a key is **defined once, on the model that owns it**, and **computed in any query by a function
named after the key**. The function can take its arguments from any column the query can see.

1. **The definition lives in the owning model's YAML.** The key's name is also the name of its function.

   ```yaml
   # dim_customer.yml
   keys:
     customer_key: {from: [source_system, customer_id]}
   ```

2. **Queries call the function,** in the owning model and in every model that refers to it:

   ```sql
   -- dim_customer.sql
   SELECT customer_key(c.source_system, c.customer_id) AS customer_key, ...

   -- fct_sales_lines.sql: an argument can come from a join
   SELECT customer_key(st.source_system, l.customer_id) AS customer_key, ...
   FROM staging.sales_lines l
   JOIN staging.stores st ON st.store_id = l.store_id
   ```

3. **Build generates a DuckDB macro per key**, so bodies stay plain DuckDB that runs as is (DESIGN.md: no templating). Lowering to
   another engine replaces the call with that engine's hash expression, or with a lookup for a key-map key.
4. **Checks.** Build's query analysis checks each call's argument count and types against the definition. For example, a fact that
   passes one argument is refused: "`customer_key` takes 2 arguments (`source_system`, `customer_id`)". A key name that clashes with a
   DuckDB built-in function is refused.
5. **Relationships are inferred, not declared.**
   - A call records a relationship from the calling model's output column to the owning model, which feeds lineage, the generated
     relationship checks and the semantic layer.
   - Roles come from the output column's name: `order_date_key` and `return_date_key` are two relationships to `dim_date`.
6. **History keys** name their version key explicitly:

   ```yaml
   keys:
     product_key: {from: [product_id], history: valid_from, version_key: product_version_key}
   ```

   `product_version_key(product_id, valid_from)` keys the version, and `product_key(product_id)` is the durable key.
7. **Key maps:** `method: key_map` keeps the same SQL and lowers the call to a lookup in Build's map table. New business keys get new
   integers when the owning model loads.
8. **Missing values.** A call with a NULL argument returns NULL by default. A definition can say `unknown_member: true` instead: the
   function then returns a reserved "unknown" key, and Build adds that row to the owning model.
9. **Later: keys defined in pure SQL.**
   - The owning model's SQL can define the key itself, through a generic call in its select list, for example
     `surrogate_key(c.source_system, c.customer_id) AS customer_key`.
   - Build infers the definition from that call (the name from the output column, the parts and types from the arguments) and writes
     it into the YAML, which stays the stored form, the same way `project import` writes metadata.

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

## 4. Slices (surrogate keys; section 1 and 1a)

One slice, one branch `suite/<id>-<short-name>`, one pull request, as in plan 13a. "Done when" is the test.

| ID | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| K01 | The key hash, defined once: normalization, the C# reference and the SQL for DuckDB, SQL Server, Fabric and PostgreSQL (`SurrogateKey` in `DbDataKit.Dialect`) | | S | the reference matches SHA-256's first 8 bytes; DuckDB computes the reference value for text, integer and date parts, both signs, NULL; the other engines are checked live in K05. **Status: done** (branch `suite/k01-key-hash`). Chosen here, not by the owner: no case folding (upper-casing differs between engines outside ASCII, so a key that ignores case is made from a folded column), U+001F between parts, trimming of spaces only, a NULL part gives a NULL key. The SQL Server and PostgreSQL text was written from the engines' documentation and has **not run**: no engine was available in the session |
| K02 | `keys:` in a model's YAML: name, `from`, `method` (`hash` default), `unknown_member`, `history`/`version_key`; closed schema, diagnostics with file and line | K01 | M | loader tests: a good file, an unknown setting, a key whose `from` names no column of the model, a key named like a DuckDB built-in |
| K03 | The function per key: Build generates a DuckDB macro for each key; query analysis checks argument count and types against the definition (the "takes 2 arguments" error) | K02 | M | a fact that passes one argument is refused with the plan's message; the key computes in DuckDB |
| K04 | Lowering: a call of a key function becomes the engine's hash expression (K01's SQL) in the rendered model | K03 | M | rendered golden files per target; a differential run against DuckDB |
| K05 | Real engines: the hash of a fixed set of business keys on SQL Server 2022, PostgreSQL 17 and DuckDB agree with the reference | K04 | S | conformance test, run in CI on the test engines |
| K06 | Generated uniqueness test for every hashed key (plan 23 check, severity block) and the collision message | K04, plan 23 | S | a table with two business keys that collide (forced with a fake hash in a test) fails the build |
| K07 | Relationships inferred from calls: output column to owning model, roles from the column name (`order_date_key`, `return_date_key` to `dim_date`); into lineage | K03 | M | a model calling `date_key` twice records two relationships to `dim_date` |
| K08 | `unknown_member: true`: the function returns the reserved key and Build adds the row to the owning model | K04 | M | a NULL argument gives the reserved key; the owning model holds that row after a load |
| K09 | History keys: `version_key` and the durable key beside it, over `ColumnHistory` | K04 | M | a changed attribute gets a new version key and the same durable key |
| K10 | `method: key_map`: the map table kept in the target, the lookup lowering, new keys on load | K04 | L | a business key keeps its integer across reloads and is never reused |
| K11 | DbDataStore entity ids as UUID v7, made by the serving instance | | S | ids are time-ordered, unique across two instances in a test, and a draft keeps its id when submitted |
| K12 | Later: keys defined in pure SQL (`surrogate_key(...)` in the select list), written back into the YAML | K03 | M | the definition is inferred and stored |
