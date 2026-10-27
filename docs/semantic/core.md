# Core

The contracts every spec shares. It contains no execution logic. Machine-readable schemas are in
`../spec/schemas`.

## Logical names

Members are named `<owner>.<member>`, such as `orders.net_revenue` (a measure of the `orders` fact table) or `customers.region` (the `region` attribute of the `customers` dimension). A dimension is a table and an attribute is one of its columns; a fact table is a table whose rows are facts, a measure is an aggregation over one of its columns, and the values aggregated are not themselves called facts. The pattern is
`^[a-z][a-z0-9_]*\.[a-z][a-z0-9_]*$`. Registered ad hoc tables use the same form (`user_territories.territory`, an attribute of an ad hoc dimension).
Names never refer to physical tables or columns; executors resolve them through the catalog.

**Implemented.** In result rows and descriptors a member's column name is its logical name with `.` replaced by
`_`: `customers.region` becomes `customers_region`. Aliases (`as`) are snake_case, unique within the query, and
must not collide with a catalog member name.

## Table descriptor

Every result set passed between specs carries a descriptor. The base is a Frictionless Table Schema extended with `x_`
fields. A descriptor keeps Frictionless's `schema.fields` (they describe the columns of a result set; the catalog's physical layer says `columns`).

```json
{
  "descriptor_version": "1.0",
  "name": "d1",
  "schema": {
    "fields": [
      {"name": "customers_region", "type": "string", "x_kind": "attribute", "x_binding": "customers.region"},
      {"name": "orders_net_revenue", "type": "number", "x_kind": "measure", "x_binding": "orders.net_revenue",
       "x_semantics": {"grain": ["customers.region", "products.product"], "rollup": {"default": "sum"},
                       "spread": "blank", "status": "governed"}}
    ],
    "primaryKey": ["customers_region"]
  },
  "x_grain": ["customers.region"],
  "x_row_count": 2,
  "x_truncated": false,
  "x_provenance": {"spec": "native_query", "spec_version": "0.3", "source": "sqlserver_sales",
                   "request_hash": "9f2c…", "engine_level": 1}
}
```

| Field | Status | Meaning |
| --- | --- | --- |
| `x_kind` | **Implemented** | `attribute` (a catalog attribute, which can be grouped and joined on), `measure`, or `column` (a value that is shown but not joined or grouped on). |
| `x_binding` | **Implemented** | The catalog member a field corresponds to. Only bound fields can be join keys or mapped. |
| `x_semantics` | **Implemented** | For measures: the catalog's `rollup`, `spread`, `grain` and `status` (`governed` or `inferred`). |
| `x_grain` | **Implemented** | The bindings that make a non-total row unique. Producers guarantee it; consumers check joins against it. `null` when unknown (level 0). |
| `x_row_count`, `x_truncated` | **Implemented** | Row count; true if a row cap or limit cut the result. Consumers that need complete data reject truncated tables. |
| `x_provenance` | **Implemented** | Producing spec and version, source, request hash, engine level, and `mode` (`agent_native`, `agent_completion`, `engine`). |
| `x_additivity` | **Implemented** (derived) | `additive`, `semi_additive` or `non_additive`, written into each measure field from its rollup rules (`Catalog.Additivity`), so it can't disagree with them; a view reads it to decide whether cells may be combined. Earlier **proposal:** carry it inside `x_semantics` and derive it from the rollup rules instead of keeping a second field. See [open-questions.md](open-questions.md#additivity-and-rollup). |
| `x_decimal` | **Specified** | `[precision, scale]` for `number`. |
| `x_totals` | **Implemented** | `{level_field, subtotal_by, levels}`: present when the table holds subtotal rows. `level_field` (`_rollup_level`) holds each row's level, and `levels` maps every level to the attributes it keeps and rolls up. See [combine.md](combine.md#totals). |

Provenance of a combined result should list every step, map and spec version used. **Proposed** addition:
`x_provenance.overrides` and `assumptions` recording every rollup or spread override and every assumption the
agent made.

## Filter grammar

A filter is `{attribute, operator, values}` (a condition on an attribute), `{measure, operator, values}` (a condition on a measure's value), or `{and: [...]}` / `{or: [...]}`. A leaf has exactly one of `attribute` and `measure`. Values are always arrays and executors
bind them as parameters, never as concatenated text.

| Operator | Applies to | Status |
| --- | --- | --- |
| `equals`, `not_equals` | string, number, date, datetime | **Implemented** |
| `contains`, `not_contains`, `starts_with` | string | **Implemented** (`LIKE` with `ESCAPE`) |
| `gt`, `gte`, `lt`, `lte` | number, date, datetime, period labels | **Implemented** |
| `between` (low end first, both ends included) | number, date, datetime, period labels | **Implemented** (fixtures `level1/23` to `30`, `73` to `77`). A date's values are `YYYY-MM-DD` (`invalid_date`); a datetime's are dates or instants (`invalid_datetime`); the low end first, and exactly two (`between_invalid`) |
| `set`, `not_set` | any | **Implemented** |

A filter on an attribute becomes `WHERE`. A filter on a measure becomes `HAVING` (**Implemented**, fixtures `level1/48`, `50` to `53`,
`level2/23`, `24`, `level3/33`): its `measure` is the output name of a measure the query returns (its alias, if it has one), its values are
numbers, and one entry of `filters` tests measures or attributes, not both (`filter_mixes_kinds`; `having_measure_missing`,
`having_value_not_number`). With one source it is in the source query (`HAVING SUM(...) > ?`, or a `FILTER` over the DAX summary); across
sources, or with totals, it is applied to the combined rows, so with totals it can drop a subtotal row too. Where the engine doesn't
combine, it is left in the `finalize` step as `having`.

## Types

| Type | Notes | Status |
| --- | --- | --- |
| `string` | Unicode text; trailing spaces preserved | **Implemented** |
| `integer` | 64-bit | **Implemented** |
| `number` | Decimal by default; money is DECIMAL(19,4) | **Implemented** |
| `boolean` | | **Implemented** |
| `date` | No time zone; ISO 8601 `YYYY-MM-DD` in results and filters | **Implemented** |
| `datetime` | An instant: ISO 8601 in UTC with a `Z` in results (`2026-07-01T03:30:00Z`); a filter may write an offset or a date ([query.md](query.md#datetime-and-time-zones)) | **Implemented** |

Producers normalize at their boundary: DAX blanks become NULL, Excel serial dates become `date`, text numbers are
converted per the descriptor or reported as errors. The engine also normalizes DuckDB `HUGEINT` (for example a
`SUM` over `BIGINT`) to a 64-bit integer.

## Errors

All specs report errors in one shape, compatible with Frictionless report fields where they apply.

```json
{"errors": [{"spec": "native_query", "code": "unknown_member", "path": "$.query.measures[1]",
             "message": "orders.revenue is not a catalog member.", "suggestions": ["orders.net_revenue"]}]}
```

Codes are defined by each spec and unique within it. `path` is a JSON path into the document being validated.

**Implemented codes**

| Code | Meaning |
| --- | --- |
| `schema_invalid` | The document failed its JSON Schema |
| `unknown_member` | A name that is not in the catalog; `suggestions` come from names and synonyms |
| `wrong_member_kind` | An attribute used as a measure, or the reverse |
| `invalid_operator` | An operator that does not fit the member's type |
| `missing_values` | An operator that needs values has none |
| `order_member_missing` | `order` names a member that is not in the query |
| `unconfirmed_binding`, `adhoc_filter_unsupported`, `non_additive_rollup`, `bridge_not_many_to_one`, `unmapped_values` | Ad hoc attributes; see [combine.md](combine.md#validation-codes) |
| `totals_member_missing`, `totals_member_duplicate` | `totals.subtotal_by` names an attribute that is not in the query, or twice |
| `totals_order_not_attribute` | `order` names a measure while `totals` is set |
| `duplicate_measure` | The same output name listed twice |
| `unsupported` | The agent reported an unsupported question; the message starts with the reason code |
| `acknowledge_required`, `rollup_attribute_in_query`, `empty_needs_rollup` | A rollup override that sums a value the catalog says can't be added without saying so; one over an attribute already in the query; `empty` without a rollup |
| `alias_required`, `alias_invalid` | A filtered measure without an alias; an alias that collides with a catalog member, or a post-filter whose `measure` isn't a transform alias |
| `transform_not_allowed`, `transform_member_missing`, `hierarchy_not_declared`, `transform_entity_in_query`, `transform_entity_unavailable` | A transform on a measure, or on a transform output, that can't be added across its attribute, or a window, prior_period or per_entity on a derived measure; an attribute that isn't in the query; no declared hierarchy; the entity already in the query, or missing from the source |
| `transform_base_unknown`, `transform_base_not_scannable` | A transform whose `measure` isn't a catalog measure or an earlier transform's `as` (a forward or self reference, a typo); a `window`, `index`, `prior_period` or `per_entity` reading a transform's output ([query.md](query.md#chaining-transforms)) |
| `index_base_unknown`, `index_attribute_not_coarser` | An `index` whose `base` isn't a member of `along` in the date table (the closest members are suggested), or whose query lists a finer level of the same calendar beside `along` ([query.md](query.md#index-to-a-base-member)) |
| `filter_member_not_in_source` | A measure filter on an attribute the measure's source doesn't have |
| `measure_filter_misplaced` | A `measure` filter inside a measure's own `filters` or a clarify option, where there is no result to test |
| `row_cap_exceeded` | A source returned more rows than the row cap |
| `level_not_implemented` | The engine runs at a lower level than the request needs |
| `native_query_failed` | An agent-written native query failed |
| `native_execution_unavailable` | The executor cannot run agent-written native text |
| `dax_execution_unavailable` | DAX execution is not available |
| `escape_hatch_invalid` | Completion SQL that fails the sandbox rules |
| `completion_failed` | Completion SQL that failed to run |
| `unknown_request` | A completion for an unknown `request_hash` |
| `incomplete_completion` | A completion that does not cover every remaining step |

Further codes for ad hoc combining are listed in [combine.md](combine.md#validation-codes).

## Result contract

Every request gets the same response shape at every adoption level. **Implemented**; schema in
`../spec/schemas/result.schema.json`.

```json
{
  "kind": "result",
  "status": "partial",
  "request_hash": "a71e…",
  "engine_level": 1,
  "result_sets": [{"id": "d1", "source": "sqlserver_sales", "grain": ["customers.region", "products.product"],
                "covers": {"measures": ["orders.net_revenue"], "filters_applied": ["date.fiscal_quarter equals FY27-Q1"],
                           "filters_not_applied": []},
                "descriptor": {}, "native_query": {"dialect": "tsql", "text": "SELECT …"}, "rows": []}],
  "combined": null,
  "remaining": [{"step": 1, "op": "spread", "input": "d2", "to": ["customers.region", "products.product"],
                 "spread": {"budget.budget_amount": "repeat"}},
                {"step": 2, "op": "union", "inputs": ["d1", "step:1"]},
                {"step": 3, "op": "finalize"}],
  "reason_code": "level_not_implemented"
}
```

| Field | Meaning |
| --- | --- |
| `status` | `complete` when `combined` answers the whole request; `partial` when steps remain; `failed` with core errors |
| `engine_level` | The adoption level the engine ran at |
| `result_sets` | Every result set computed, with grain, coverage, descriptor, native query and rows |
| `covers` | Measures supplied, `filters_applied`, and `filters_not_applied` with a reason such as `not_in_source` |
| `combined` | The final result set when `status` is `complete`; otherwise null |
| `remaining` | Steps not yet executed, in plan order |
| `reason_code` | Why steps remain: `level_not_implemented` (**Implemented**), `agent_combines` (**Implemented**, level 0 with several queries), `clarification_needed` (**Implemented**, a valid `clarify`), `relation_missing`, `semantics_unconfirmed` (**Specified**) |
| `warnings` | `{code, message}`. **Implemented:** `filter_not_applied`, `spread_blank` (a measure left blank: no spine to repeat over, no weights for the grain, or blank with no total rows) |

Registration documents (`table`, `bindings`, `value_map`) are run through the same entry point and return a `complete` result whose
single result set is the registered table (id `name@vN`), the updated descriptor, or the mapping table (id `map_<id>`).

Step operations in the schema: `rollup`, `spread`, `union`, `totals`, `compute`, `measure_expression`, `finalize`,
`combine`. Today's engine emits `rollup` (through an ad hoc relation, below level 3), `spread`, `union`, `compute` (transforms it can't run yet at the level), `totals`, `measure_expression`,
`finalize` (levels 1 and above) and `combine` (level 0).

**Rules**

- A `partial` result is never shown to a user as final. Either the agent completes it, or the answer states what
  is missing.
- `filters_not_applied` must be shown to the user whenever it is not empty.
- The engine returns the most complete result it can: if a level supports some remaining steps it executes those
  and returns only the rest.
- When the engine starts executing a step type, completions for that type are still accepted during **shadow mode** and compared with the
  engine's own result (**Implemented**, `shadow: true`). The completion's SQL is validated and run like any other, but the engine's result comes
  back, with a `shadow_match` or `shadow_mismatch` warning; rows compare as a set. Each comparison is kept in `QisEngine.ShadowReports`, and
  disagreements are the test cases to write. Without shadow mode a completion for an already complete request is `unknown_request`.

### Completing a partial result

The agent completes remaining steps by submitting DuckDB SQL over the result set ids instead of combining results in
its own reasoning. **Implemented**; schema in `../spec/schemas/completion.schema.json`.

```json
{"kind": "completion", "request_hash": "a71e…", "completes": [1, 2, 3],
 "sql": "SELECT d1.customers_region, … FROM d1 LEFT JOIN d2 ON …", "maps": []}
```

The SQL must be one `SELECT` that references only the result set ids and its own CTEs. Table functions such as
`read_csv`, qualified names, multiple statements and non-`SELECT` statements are rejected with
`escape_hatch_invalid`. It runs in a fresh DuckDB with `enable_external_access = false` and
`lock_configuration = true`. **Implemented**: the parser is DuckDB's own (`json_serialize_sql`); the design
document names `sqlglot`, which is not available in .NET (see [open-questions.md](open-questions.md)).

### Run records

**Implemented.** A completed run is saved as `<request_hash>.json` holding the request, the completion without its hash
(absent when the engine computed the whole answer itself), each result set's native query, and the `setup` documents (tables, value maps,
bindings) registered before it. `QisEngine.Replay` registers the setup again, re-runs the request and, if there is one, the completion,
without the agent, in a session that knows nothing of the original; it must reproduce the same rows (checked for every fixture).

## Scopes

**Specified.** Apply to registered tables, saved extraction specs and saved mappings. Promotion moves up one step
at a time.

| Scope | Lifetime | Visible to | Review |
| --- | --- | --- | --- |
| `session` | Ends with the conversation | The user | None |
| `personal` | Until deleted | The user | None |
| `shared` | Until deleted | A team | Owner approval |
| `governed` | Permanent | Everyone | Data stewards; becomes part of the catalog |

## Versioning

- Specs use `spec_version` as major.minor. A minor version adds optional fields or new types only; a major version
  may change meaning. Executors reject unsupported major versions. The engine currently emits and accepts `0.3`.
- Saved artifacts (tables, extraction specs, mappings) are referenced as `name@vN`. A change creates a new version;
  old versions stay readable.
- Composite documents such as plans and views record the versions of every spec and artifact they used.
