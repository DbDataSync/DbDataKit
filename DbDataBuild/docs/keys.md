# Surrogate keys

A key is a 64-bit integer computed from a business key. It comes out the same on every engine, in every channel and on every rebuild, which matters because a test build reads unchanged production tables (plan 21): keys must match across channels. A key is **defined once, on the model that owns it**, and **computed in any query by a function named after the key**.

## The hash

The key of a business key is the **first 8 bytes of SHA-256 over its normalized form, read as a signed big-endian integer and stored as `BIGINT`**. The reference is `SurrogateKey` in `DbDataKit.Dialect` (`Hash`, `Compute`); the same module writes the SQL for DuckDB, SQL Server, Fabric and PostgreSQL. A test runs the reference and each engine over the same values (text with spaces at the ends, accents, CJK, a character outside the Basic Multilingual Plane, the empty string, the 64-bit limits, a leap day, three parts, both signs): `SurrogateKeyOnEnginesTests` on SQL Server 2022 and PostgreSQL 17, `SurrogateKeyTests` on DuckDB. Fabric's text is the SQL Server text and has never run on an engine.

**Normalization.** Each part is written as text by its type, the parts are joined with **U+001F** (the unit separator), and the result is encoded as UTF-8.

| Part type | Written as |
| --- | --- |
| text | with the **spaces at both ends removed**, nothing else changed: **no case folding** and no Unicode normalization (upper-casing differs between engines outside ASCII; a key that must ignore case is made from a column the source has already folded) |
| whole number | decimal, no leading zeros, `-` for a negative one |
| date | `yyyy-MM-dd` |
| timestamp | `yyyy-MM-dd HH:mm:ss`, **cut, not rounded, to the second** (engines keep different fractions; a `TIMESTAMPTZ` stays text because zones are written differently) |

A **NULL part gives a NULL key**. Decimals, booleans and GUIDs are not defined as parts; a key over one is refused. A text part is cut at 4000 characters on SQL Server and Fabric. A text part that itself holds U+001F can collide with another key, which the generated uniqueness check finds.

## Defining a key

In the owning model's YAML (closed schema, `keys` in `schemas/model.schema.json`, diagnostics with file and line). The key's name is also the name of its function.

```yaml
# dim_customer.yml
keys:
  customer_key: {from: [source_system, customer_id]}
  product_key:  {from: [product_id], history: valid_from, version_key: product_version_key}
```

| Setting | Meaning |
| --- | --- |
| `from` | the **arguments** of the function, in order (not columns of this model: a fact often lacks some of a dimension's business-key columns, or reaches them through a join) |
| `method` | `hash` (the default), or `key_map`: the key is an integer a table of the target gives the business key (see "The key map") |
| `unknown_member` | `true`: a NULL argument gives the reserved unknown key and Build adds that row to the owning model; default: NULL gives NULL |
| `history`, `version_key` | together: `history` names the valid-from argument and `version_key` the function that keys a version |

## Calling a key

Queries call the function, in the owning model and in every model that refers to it, with arguments from any column the query can see:

```sql
-- dim_customer.sql
SELECT customer_key(c.source_system, c.customer_id) AS customer_key, ...
-- fct_sales_lines.sql: an argument can come from a join
SELECT customer_key(st.source_system, l.customer_id) AS customer_key, ...
FROM staging.sales_lines l JOIN staging.stores st ON st.store_id = l.store_id
```

- **Macro and lowering.** Build generates one DuckDB macro per key (`MacroLibrary.WithKeys`), so bodies stay plain DuckDB and run as they are (`sample`, `test`, describing a model). The part types come from the owner's column of the same argument name (a whole number, a date or a timestamp), otherwise text. When a query is lowered, the macro calls a marker function (`ddb_key_<one letter per part>`, registered on the planning connection so DuckDB cannot fold it); `TargetRules.Finish` replaces the marker in the text for SQL Server, Fabric and PostgreSQL with that engine's hash expression. `lowered.sql` therefore holds the marker: it is a review artifact, not runnable DuckDB. On SQL Server the UTF-8 collation goes on the text before the cast to `VARCHAR`.
- **Argument checks.** A call with the wrong number of arguments is refused: "`customer_key` takes 2 arguments (`source_system`, `customer_id`), but this call passes 1". Only the count is checked; types are cast by DuckDB and the owner's column types decide how a part is written. A key named like a DuckDB built-in, a name two models define and a name a user macro has are refused.
- **Relationships are inferred, not declared.** A call with an alias records a relationship from the calling model's output column to the owning model; the **role comes from the column name** (`order_date_key` and `return_date_key` are two relationships to `dim_date`; a column named like the key itself has no role). `KeyRelationships.Infer` computes it. Nothing reads the result yet.

## Keys in pure SQL

The owning model's query can define the key by calling the generic `surrogate_key(c.source_system, c.customer_id) AS customer_key`. `KeyInference` (`DbDataBuild.Models`) finds the calls (outside strings, quoted names and comments), takes the key's name from the output column and its `from` from the argument columns' names (an argument that is an expression is refused: select it as a column first), and `project model update` writes `keys: {customer_key: {from: [...]}}` into the model's YAML through the same confirm-and-hash-checked write as every other edit (`--check` reports a missing key as a difference). The YAML stays the stored form and the only one the rest of the tool reads: `BuiltModel` reads the query with the call of a stored key rewritten to the key's own function (only the function's name changes, so lines stay), after which macros, lowering, argument checks, relationships and checks are those of `keys:`. A key stored with other arguments than the query passes is a refusal: changing the arguments changes every value of the key, so it is done by hand. Part types come from the owner's columns of the same name, as for every key, so a model's columns are defined (the same run does this) before its key's text is right on a first build.

## The key map

`method: key_map` keeps the key as an integer that **a table in the target** gives each business key, instead of a hash. The map is **one table per key** in the schema of the model that owns it, `<owner schema>.<key>_map` (`marts.customer_key_map`), with `key_value BIGINT` (the engine counts it up, from 1; 0 stays the unknown member) and `key_text` (the normalized business key of the hash, the parts joined by U+001F, compared byte for byte: a binary collation on SQL Server, so `SAP` and `sap` are two keys, as they are for a hash; at most 850 characters on SQL Server, which fails the load loudly beyond that). **Each channel is its own map**: a connection is one target, so its map is in its database and its integers are its own; a test build that must match production's keys uses `hash`. A version key of a key with history is a key of its own and has its own map.

- **Calling it** is as for a hash key: `customer_key(c.source_system, c.customer_id) AS customer_key`, in the owning model and in every model that refers to it. The call must be **a selected column of the query** (the load adds new business keys to the map from the column; a call in an expression, a join or a filter is refused, DDB-324, naming this rule). Lowering keeps the call as a marker (`ddb_keymap_<parts>_<key>('<owner schema>', ...)`, and a second lowering where it is `ddb_keytext_...`, the business key's text), and each engine's text reads the map with a scalar subquery (`TargetRules.Finish`).
- **New keys on load.** The rendered load script of every operation of a model that calls a mapped key starts, inside its transaction, with `CREATE TABLE IF NOT EXISTS` for the map (`IF OBJECT_ID(...) IS NULL CREATE TABLE` on SQL Server) and an `INSERT` of the business keys the query yields that the map has not got yet; the load's own statements then read the map. A load that fails rolls the insert back (the engine's counter may skip numbers; no number is given twice). Two loads adding the same business key at once end with one row (`IGNORE_DUP_KEY` on SQL Server, `ON CONFLICT DO NOTHING` on PostgreSQL). A view that calls a mapped key only reads the map, so a business key nothing has loaded has the key NULL there.
- **Never reused.** A row of the map is never changed or deleted by the tool, so a business key keeps its integer across reloads, even when the business key disappears from the source and comes back. **Keys are not retired** (owner decision, 2026-11-12): nothing removes a map row; revisit only if churn becomes a problem.
- **Who writes it.** Only the Build write login writes a map, through the load scripts above, which go through `MutationGate` like every statement of a load; no other login is given the table. An administrator may still repair a map in an emergency, a recovery or after a bug (owner decision, 2026-11-12), and that is an explicit, audited command, not a normal write: see "Repairing a map" below. Editing the table with a login of one's own stays possible for the database's administrator and is not audited by the tool.
- **Limits.** `sample` and `test` run queries on DuckDB, which has no map: a query that calls a mapped key is refused there with a message saying so. Fabric is refused (no enforced unique key) and the engines other than SQL Server and PostgreSQL have no text. The uniqueness check of a hash key does not apply to a mapped key (the map makes the integer unique to its business key). Run on SQL Server 2025 and PostgreSQL 17 (`ApplyConformanceTests`, a key map's behaviour across refreshes); two loads adding the same key at once were not run.

## Repairing a map

`dbdatabuild connection key-map show [connection] <key>` reads a map, and `connection key-map repair [connection] <key> --operation <op>` repairs one. `<key>` is the key's function name (a key with history has a map for the version key too); a hash key, a key of a model that is not on the connection, a DuckDB or Fabric connection and a map no load has created yet are refused with the reason.

- **`show`** prints the number of mappings, the largest integer and where the identity is (and says when the identity is **below** the largest integer, which is the state a reseed repairs). Like `connection check` it reads no business key unless `--show-values` asks (`--limit`, default 20); `--value`, `--text` (the stored text), `--part` (once per part, written as the key writes it) and `--contains` filter, and the count of what matches is always given. An agent's tool has no `--show-values`.
- **`repair`** does **one** operation per run:

| `--operation` | What it does | Refused when |
| --- | --- | --- |
| `add` | inserts the mapping `--value` -> the business key (`--part` per part, or `--text` exactly): a mapping a restore lost | the integer or the business key is already in the map, the integer is below 1 (0 is the reserved unknown member), a SQL Server text is over 850 characters |
| `set-text` | replaces the business key of the mapping `--value`; the integer stays | no mapping has the integer, another mapping has that business key |
| `remove` | deletes the mapping `--value`: one a bug created | no mapping has the integer, or **a table uses the integer** (below) and `--force` is not given |
| `reseed` | raises the identity to the largest integer of the map, so no load hands out one that is already there; it never lowers it | nothing to do (the identity is already high enough, or the map is empty: said, and nothing is written) |

- **Dry run by default.** Without `--apply` it prints the change, the rows it would touch (the integers; the business keys only with `--show-values`), the tables that use the integer and the exact statements, and connects with the read login only. `--apply` makes the change on the write login and needs `--reason`. Exit codes: 0 for done, a dry run and "nothing to do", 1 for a refusal (nothing is changed), 2 for a usage error.
- **A used integer.** `remove` (and a change of text, which only warns) looks for the integer in the columns named like the key, or that the project's queries select it as (a role such as `order_date_key`), of type `BIGINT`, in the base tables of the connection, and counts the rows. It names each table and column; a table it cannot count counts as used. It cannot find a table the project does not know under another column name, which the output says. `--force` removes the mapping anyway and is recorded as forced; the fact rows keep an integer no business key has until they are reloaded.
- **One transaction, one named key.** The script is one transaction (`BEGIN`/`COMMIT`, `SET XACT_ABORT ON` on SQL Server) that locks the map (`TABLOCKX`, `SHARE ROW EXCLUSIVE`), checks with a guard (a `SELECT` that divides by zero, so the transaction rolls back) that the map is still as it was read, makes the change, reads it back, and writes the audit row. It touches the named key's map and its repair log and no other table, and no other row of the map than the one named; the values are bound, never written into the text. On PostgreSQL an added integer is followed by the sequence (`setval`, only upward); on SQL Server `IDENTITY_INSERT` moves the identity itself.
- **The audit record.** Build's own audit has no place for a change of data in a target, so the repair writes its record in the target, in the **repair log** beside the map, `<owner schema>.<key>_map_repair` (created by the first repair, in the same transaction): `repair_id`, `repaired_at` (the server's clock), `repaired_by` (the person the command acts for: on a terminal the login's user or the operating-system user, on the Build server the person who called it through the console, "Ada via Main console"), `db_login` (the database login that wrote it), `run_id` (the id of the statement log file of the run, `.dbdatabuild/statement-log/`, which holds the statements and the values bound), `operation`, `key_value`, `text_before`, `text_after`, `identity_before`, `identity_after`, `forced`, `reason`. The loads never touch the log and nothing deletes from it. Through the Build server every applied repair, and every refusal, is also a line `keymap.repair` in the server's audit log with the operation, the integer and the reason.
- **Who may run it.** On a terminal, whoever holds the connection's write login: that is the administrator of the project's database, as it is for `connection seed`. On the Build server the role is **admin**, for a dry run too (not the approver's: an approver applies plans), checked on every call; the console hides it from anyone else. An agent through `ui mcp` is offered the repair only with `--allow-writes` (each run is put to the person, who approves it), never `--show-values`, and `show` is offered without `--show-values` too.
- **Run on** SQL Server 2025 and PostgreSQL 17 (`KeyMapConformanceTests`: every operation, the dry run changing nothing and not creating the log, the audit row, the identity after an addition and a reseed, a refused used integer, a forced removal, a rolled-back guard); the unit tests check the statements of both engines as text.

## History keys

For `history`/`version_key`, `product_version_key(product_id, valid_from)` keys one version of a row and `product_key(product_id)` is the **durable key** beside it, so facts and reports can join across versions. A changed attribute gets a new version key and the same durable key. (This is unrelated to Build's own `ColumnHistory`, the history of column changes.)

## The unknown member

With `unknown_member: true` the macro wraps the hash in `coalesce(..., 0)`, so every engine's lowered text carries it. **The reserved unknown key is `0`** (`SurrogateKey.UnknownKey`; the owner decided it stays 0). The owning model's query gets the unknown row before it is lowered (`SELECT * FROM (<query>) UNION ALL BY NAME SELECT <row>`): key columns `0`, a nullable column NULL, a non-nullable one the plainest value of its type (`'Unknown'`, `0`, `false`, `1900-01-01`), so the view, the full table and the unique-key table all hold it. The definition is refused (DDB-106) on a kind that loads by period (the row would be dropped) and on a model with no `columns`.

## The uniqueness check

Every key a model holds as a column gets a generated check per connection the model is on: `model.<model>.key.<column>.unique@<connection>`, a SELECT of the key values on more than one row (a NULL key is not a repeat; for a key with history the version key is checked, since the durable key repeats by design). `connection check` runs it (a failing check is DDB-601 and exit 1) and `project tests list --kind semantic` lists it. The message names the three causes: a business key that appears twice, two business keys whose hashes collided, a part that holds the separator. The same test is also a check of plan 23's framework with severity `block` (`key.<column>.unique`, added by `ModelQuality` to every model that owns a hashed key): a refresh given `--check-results` evaluates it with the model's own checks, records the result and blocks the dependents of a model whose key repeats.

## Not built

Types beyond the four part types, the key map on Fabric, the retirement of a key, and any consumer of the inferred relationships: see plan 25.
