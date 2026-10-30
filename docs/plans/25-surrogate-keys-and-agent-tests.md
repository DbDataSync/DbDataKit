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
| K02 | `keys:` in a model's YAML: name, `from`, `method` (`hash` default), `unknown_member`, `history`/`version_key`; closed schema, diagnostics with file and line | K01 | M | loader tests: a good file, an unknown setting, bad names, repeated arguments, `history` without `version_key`. **Status: done** (branch `suite/k02-key-definitions`). `KeyDefinition` on `ModelDefinition.Keys`, read by `ModelDefinitionLoader`, `keys` in `model.schema.json`. `from` names arguments, not columns of the model (a fact passes columns the owner lacks), so no column check; `method: key_map` is refused naming K10. Two checks moved to K03, where DuckDB's function list and the project's other keys are at hand: a name that clashes with a DuckDB built-in, and a name used by two models. Nothing reads a key yet, so a `keys:` block has no effect until K03 |
| K03 | The function per key: Build generates a DuckDB macro for each key; query analysis checks argument count and types against the definition (the "takes 2 arguments" error), a name that clashes with a DuckDB built-in, and a name used by two models | K02 | M | a fact that passes one argument is refused with the plan's message; the key computes in DuckDB. **Status: done** (branch `suite/k03-key-functions`, with K07 and K11). `MacroLibrary.WithKeys` adds one `CREATE OR REPLACE MACRO` per key (and per version key) over K01's DuckDB expression; a part is a whole number or a date when the owner declares a column of that name with such a type, else text (DuckDB writes all three the same way). `ModelLowering` refuses a call with the wrong number of arguments with "`customer_key` takes 2 arguments (`source_system`, `customer_id`), but this call passes 1"; the count is read from the scrubbed text at the call's own nesting depth. **Types are not checked:** DuckDB casts the arguments, and the owner's column types decide how a part is written; a stricter check needs the bound plan. A key named like a DuckDB built-in (from `duckdb_functions()`), a name two models define, and a name a user macro has are refused. The test runs a query that calls the key on DuckDB and compares with K01's reference. **A build for SQL Server or PostgreSQL still fails** with DDB-324 on the hash functions the macro expands to, until K04 |
| K04 | Lowering: a call of a key function becomes the engine's hash expression (K01's SQL) in the rendered model | K03 | M | rendered golden files per target; a differential run against DuckDB |
| K05 | Real engines: the hash of a fixed set of business keys on SQL Server 2022, PostgreSQL 17 and DuckDB agree with the reference | K04 | S | conformance test, run in CI on the test engines |
| K06 | Generated uniqueness test for every hashed key (plan 23 check, severity block) and the collision message | K04, plan 23 | S | a table with two business keys that collide (forced with a fake hash in a test) fails the build |
| K07 | Relationships inferred from calls: output column to owning model, roles from the column name (`order_date_key`, `return_date_key` to `dim_date`); into lineage | K03 | M | a model calling `date_key` twice records two relationships to `dim_date`. **Status: inference done, consumers not** (branch `suite/k03-key-functions`). `KeyRelationships.Infer` reads the calls that carry an alias (`customer_key(...) AS order_customer_key`) and gives model, column, owner, key and role (the column name unless it is the key's own name); a call in the owner is its own column, a call with no alias records nothing. Nothing reads the result yet: lineage, the generated relationship checks (plan 23) and the semantic layer are the next steps |
| K08 | `unknown_member: true`: the function returns the reserved key and Build adds the row to the owning model | K04 | M | a NULL argument gives the reserved key; the owning model holds that row after a load |
| K09 | History keys: `version_key` and the durable key beside it, over `ColumnHistory` | K04 | M | a changed attribute gets a new version key and the same durable key |
| K10 | `method: key_map`: the map table kept in the target, the lookup lowering, new keys on load | K04 | L | a business key keeps its integer across reloads and is never reused |
| K11 | DbDataStore entity ids as UUID v7, made by the serving instance | | S | ids are time-ordered, unique across two instances in a test, and a draft keeps its id when submitted. **Status: the id is done, drafts are not** (branch `suite/k03-key-functions`). `EntityIds.New` and `MadeAt` in the store server (the store has no entity or draft yet, so nothing calls it). Tested: version 7 with the time in it, text order follows creation order, 20000 ids in one millisecond are distinct. "A draft keeps its id" needs the drafts of plan 21 |
| K12 | Later: keys defined in pure SQL (`surrogate_key(...)` in the select list), written back into the YAML | K03 | M | the definition is inferred and stored |

## 5. Decisions made and requests that are open (2026-10-30, written when K01 to K03, K07 and K11 were done)

**Decisions taken without the owner** (each can be reversed; the first four are in K01's code):

1. Text is trimmed of spaces only and never case-folded; a key that ignores case is made from a column the source has folded. Reason: `UPPER` differs between engines outside ASCII.
2. Parts are joined with U+001F; a part that holds U+001F can collide, which the uniqueness check (K06) finds.
3. A NULL part makes the key NULL (the default of section 1a, 8).
4. A part is text, a whole number or a date. Decimals, timestamps, booleans and GUIDs are not defined: a key over one is refused until the owner says how it is written.
5. `from` names arguments, not columns of the model; a part's type comes from the owner's column of the same name, else text.
6. `method: key_map` is refused with a message naming K10 rather than accepted and ignored.
7. Only the argument count of a call is checked; types are cast by DuckDB.

**Requests for the owner:**

- **K04 (lowering) is the hard one, and it was not started.** The macro expands inside DuckDB's bind, so the plan the lowerer reads holds `sha256`, `substr`, `CAST('0x...' AS BIGINT)` and arithmetic, not a call of the key. Neither SQL Server nor PostgreSQL can be given that text: the matrix lists `sha256`/`from_hex` as unsupported and a hex string does not cast to a BIGINT there. Three ways, in the order I would pick them: (a) give the expansion a shape the lowerer recognizes (a marker the macro leaves, such as a call of a function DuckDB does not know, `__dbdatabuild_key(text)`, kept out of the bind by a DuckDB scalar macro that the lowerer sees before expansion) and replace it with K01's engine text; (b) add function rules for `sha256` (hex text on each engine) and for the hex-to-BIGINT step, with the three tests each rule needs; (c) stop expanding: lower the author's own SQL, which keeps the call, with the key's SQL substituted by the lowerer. (a) or (c) keeps the hash text in one place, in `SurrogateKey.Sql`. Whichever is chosen needs SQL Server 2022 and PostgreSQL 17 to prove the expression (K05): **the SQL Server and PostgreSQL text in K01 has never run.** Needs: a decision, and a session with the test engines.
- **K05, K06, K08, K09, K10, K12** all wait on K04 (K06 also on plan 23's checks). K08 needs the value of the reserved unknown key: I would use `-1` for a hash key (a hash is 2^-64 likely to be that) and say so in a diagnostic if a real key ever equals it; the owner should confirm.
- **K10 (key map)** needs the shape of the map table (one table per key or one shared table with the key's name as a column), and who may write it when several channels build. I did not guess.
- **K07 consumers:** whether the relationships appear in `project lineage` as column edges, as a new `relationships` command, or only in the semantic layer.
- **K11:** the id column's type on each engine (`uniqueidentifier` orders by its own byte order on SQL Server, so a clustered key on it is not time-ordered there; `BINARY(16)` or a `CHAR(36)` text id would be). The owner should decide with the store's first slice.
- Sections 2 and 3 of this plan (agent tests, agent-built mappings) are not sliced: they depend on the agent of plan 16, which does not exist yet.
