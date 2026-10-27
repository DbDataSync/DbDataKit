# Combining sources

How results from several sources, including ad hoc tables the user brings, become one result with correct
numbers. This covers the ad hoc mapping spec (bindings and value maps) and the mashup spec (combine stages), with
the rollup and spread terms used throughout.

The sources are of two kinds:

- **Enterprise sources**: governed fact-to-dimension models (SQL Server star schemas, SSAS models) described in the
  catalog. Each is queried in its own dialect.
- **Ad hoc tables**: rows with a descriptor, such as an uploaded territory list. They have no governed meaning
  until their columns are bound to catalog attributes.

Everything crosses sources only as descriptors and rows in DuckDB. No fact-table row is ever joined to a row of another fact table.

## What exists today

| Piece | Status |
| --- | --- |
| Several enterprise sources in one query: one native query each, then union | **Implemented** (level 2) |
| Spread `repeat` and `blank` of a coarser source onto finer rows | **Implemented** ([below](#spread)) |
| Totals (subtotals and grand total), rollup by per-attribute rules, source repeat for `none` | **Implemented** (level 3) |
| Agent-written DuckDB completion over the result sets, with replay | **Implemented** |
| Ad hoc tables, bindings with confirmation, value maps (catalog at level 3; fuzzy and llm at level 4), a verified many-to-one bridge, rollup through it | **Implemented** |
| Weighted relations: a key's value split across groups by a share column | **Implemented** (level 3) |
| `allocate` by a weight measure or equal shares (level 4), with weights from the previous period | **Implemented** |
| Explicit join, hierarchies as relations | **Specified** or **Proposed**; not implemented |

## Registering ad hoc tables

**Implemented.** An ad hoc table is rows plus a core descriptor, registered under a name and version with a scope
(`session` by default; see [core.md](core.md#scopes)). The engine takes rows that are already extracted; getting
rows out of a spreadsheet is the extraction spec's job and is independent of this one. Schema:
`../spec/schemas/table.schema.json`. The scope is stored but not yet enforced, and the registry belongs to one engine
(one session): registered attributes are added to that session's copy of the catalog (`Catalog.Clone`).

```json
{
  "kind": "table",
  "name": "adhoc.user_territories@v3",
  "scope": "session",
  "descriptor": {"descriptor_version": "1.0", "name": "user_territories",
                 "schema": {"fields": [{"name": "territory", "type": "string"},
                                       {"name": "region_label", "type": "string"},
                                       {"name": "notes", "type": "string"}]},
                 "x_grain": null},
  "rows": [{"territory": "Pacific", "region_label": "WST-01", "notes": ""}]
}
```

Rules:

- Fields start as `x_kind: attribute`. An attribute can be displayed but not joined or grouped on.
- A change creates a new version (`@v4`); old versions stay readable, and a result's provenance names the exact
  version it used.
- Row caps apply to registered tables as to any step; a registered table is never truncated silently.

## Bindings

**Implemented.** A binding document declares what each column of a registered table means. The agent proposes it
from the descriptor's profile and the user confirms it (`confirmed_by`). Schema: `../spec/schemas/bindings.schema.json`.
A document may bind one column to the catalog (`binds_to`); the columns registered with `as` are the ad hoc
attributes related to it.

```json
{
  "spec_version": "0.3",
  "kind": "bindings",
  "table": "adhoc.user_territories@v3",
  "bindings": [
    {"field": "territory",    "role": "attribute", "as": "user_territories.territory"},
    {"field": "region_label", "role": "attribute", "binds_to": "customers.region", "via_map": "terr_region"},
    {"field": "notes",        "role": "attribute"}
  ],
  "grain": ["user_territories.territory", "customers.region"],
  "confirmed_by": "user"
}
```

- `as` registers a new logical name for a column with no catalog equivalent.
- `binds_to` declares that a column corresponds to a catalog attribute. If its values are not already canonical,
  `via_map` names the value map that translates them.
- Unconfirmed bindings can be displayed but never used as join keys (`unconfirmed_binding`).
- Applying bindings produces a new version of the table's descriptor with `x_kind`, `x_binding` and `x_grain`
  filled in.

The declared grain is verified against the rows (`grain_unverified`). An attribute registered with `as` and no `binds_to`
is rejected (`adhoc_attribute_unrelated`): without a key the measures have no way to reach it.

## Value maps

**Implemented** (schema `../spec/schemas/value_map.schema.json`). A value map translates a column's values onto the
canonical members of a catalog attribute. The members are the attribute's distinct values in the first source that
binds it, so an attribute member with no fact rows isn't a target. Catalog maps run at level 3; `fuzzy` and `llm` maps at
level 4 (`level_not_implemented` below that). The target is always
the governed member list, never another table's values, so every mapping lands on a canonical value.

```json
{"spec_version": "0.3", "kind": "value_map", "id": "terr_region",
 "table": "adhoc.user_territories@v3", "field": "region_label", "target": "customers.region",
 "mode": "fuzzy", "threshold": 0.92, "on_unmatched": "keep"}
```

| Mode | How it matches | When to use |
| --- | --- | --- |
| `catalog` | A reviewed mapping stored at `shared` or `governed` scope | Default. Required if one exists. |
| `fuzzy` | Deterministic code: normalize (trim, case-fold, strip punctuation and accents), exact match, then Jaro-Winkler similarity at or above `threshold` (default 0.92). Ties at the best score count as unmatched. The score is the standard Jaro-Winkler (prefix weight 0.1, prefix at most 4, boost only above 0.7), rounded to 4 places, implemented in `Fuzzy` with no library. | Values differ by spelling or formatting |
| `llm` | The agent supplies explicit pairs; every target is checked against the member list | Codes or abbreviations with no textual similarity |

An `llm` map carries its pairs as data, not hidden reasoning:

```json
{"kind": "value_map", "id": "terr_region", "mode": "llm",
 "table": "adhoc.user_territories@v3", "field": "region_label", "target": "customers.region",
 "pairs": [{"from": "WST-01", "to": "West", "confidence": 0.9}, {"from": "CTR", "to": "Central", "confidence": 0.7}]}
```

Executing a map produces a mapping table with its own descriptor:

| Field | Type | Notes |
| --- | --- | --- |
| `from` | string | Source value as it appears, unique per map |
| `to` | string | Canonical member, or NULL when unmatched |
| `confidence` | number | 1.0 for exact and catalog matches |
| `method` | string | `exact`, `catalog`, `fuzzy` or `llm` |

Rules:

- Unmatched and below-threshold values are kept with `to` NULL and listed in the result. Nothing is dropped silently.
- `on_unmatched` tells consumers what to do: `keep` (default; rows join to an "Unmapped" member), `drop` (excluded,
  with a count reported) or `fail`.
- Many source values may map to one target. One source value may not map to several targets (`duplicate_from`).
- A `catalog` map for the same table field and target replaces any `fuzzy` or `llm` map when a bridge is built (fixture `level4/08`). A catalog map must be at `shared` or `governed` scope (`catalog_map_scope_invalid`).
- Saved maps follow scopes and versioning, are versioned with their table, and re-run when a new table version
  arrives; any value that newly fails to match is flagged for review. Promoting a map to `governed` makes it a
  catalog conformance rule.

The source table is never rewritten in place; the mapping table is what combine consumes.

## Relations

A relation is a table that maps member values to other member values, with a **verified** cardinality and an
optional weight column. Verified means checked on the actual rows at run time, not just declared.

| Kind | Cardinality | Example | Comes from |
| --- | --- | --- | --- |
| Value map | many-to-one | `WST-01 → West` | Mapping |
| Bridge | many-to-one | `West → Pacific` | Mapping (**Implemented**; built when a query names the ad hoc attribute) |
| Hierarchy | many-to-one | `Laptops → Computers` | Catalog |
| Weighted | many-to-many | `Central → Pacific 0.6, Central → Mountain 0.4` | A user-maintained table (**Implemented**) |
| Time offset | one-to-one | `2026-08 → 2025-08` | Calendar (**Implemented**, for the previous period, as a shifted scan) |
| Time to-date | one-to-many | `2026-09 → 2026-07, 2026-08, 2026-09` | Calendar (**Implemented** as the `window` transform, one scan per member) |

Only the value map, bridge and weighted relation are implemented. The planner uses relations to move a measure between grains. A rollup through a many-to-one relation is safe; a
many-to-many relation needs weights and an `allocate`.

## The two moves: rollup and spread

A table moves to a target grain by **rolling up** the attributes it has that the target lacks, and **spreading**
across the attributes the target has that the table lacks. Each measure uses its own rules from the catalog (or
the request's overrides).

### Rollup

Coarser grain, by the measure's rollup rule per removed attribute (`sum`, `min`, `max`, `avg`, `first`, `last`,
`none`; see [catalog.md](catalog.md#measure-semantics)). `none` means the value cannot be combined and must be
recomputed at the source at the coarser grain. Through a relation:

```sql
-- [region] to [territory] through a many-to-one bridge
SELECT b.territory, SUM(s.revenue) AS revenue
FROM sales s JOIN terr_bridge b ON b.region = s.region
GROUP BY b.territory;
```

**Implemented.** A rollup through a bridge is allowed only when the bridge is many-to-one, checked on the actual rows before the main query (fixture `level3/13`):

```sql
SELECT region, COUNT(*) AS territories FROM terr_bridge GROUP BY region HAVING COUNT(*) > 1;
-- any row fails the plan with bridge_not_many_to_one
```

The engine queries each source at the bridge's catalog key (`customers.region`), joins the result to the bridge, and
aggregates by the measure's rollup rule for the key. Regions the bridge doesn't cover follow `on_unmatched`: `keep`
(default) groups them as "Unmapped", so totals equal the source totals; `drop` excludes them; `fail` stops the query
(`unmapped_values`). A measure whose rule is `none` (a distinct count) can't be rolled up this way, because the
source has no ad hoc grouping to recompute it by, and is rejected (`non_additive_rollup`). If the key is also
requested, nothing is rolled up and the bridge only attaches the group.

**Status: Implemented** for rollup through a bridge (below) and for rollup over removed attributes in the engine's combine step (used by totals): `sum`, `min`, `max`, `avg`,
`first` and `last` roll up from detail rows one attribute at a time, `first` and `last` first, so "last month, then sum
over warehouses" is never "sum, then last". Rule `none` is recomputed by asking the source again at the coarser grain
(`d1_t1`, … in the result's `result_sets`). Rollup through other relation kinds (hierarchy, weighted, time) is **not implemented**.

### Spread

Finer grain than the measure is defined at. Budget West 600, defined at `[region]`, requested at
`[region, product]`:

| product | `repeat` | `blank` | `allocate` (weights 0.6 / 0.4) |
| --- | --- | --- | --- |
| A | 600 | none | 360 |
| B | 600 | none | 240 |

- **repeat**: the value is copied onto every finer row that matches on the shared attributes. It is joined from a
  table unique on those attributes, which the planner emits only when that uniqueness holds. Repeated values are
  never summed again: a later total reads the measure at its native grain (budget 600, not 1,200).
- **blank**: detail rows are empty, and the value shows only on total rows at or above the measure's native grain.
  Without total rows in the result a `blank` measure has no values, and the result carries a `spread_blank` warning.
- **allocate**: the value is split across the finer members by weights. See [Allocate](#allocate).

**Implemented.** `repeat` joins the coarser value onto the finer rows (the *spine*: distinct keys of the set from
result sets that have every attribute of it). `blank` contributes nothing to a set the measure's result set can't cover.
This follows the design document's draft; see [open-questions.md](open-questions.md#blank-spread).

**Fallback when nothing supplies the missing members:** if no result set has the full grain to repeat over, `repeat`
cannot know the members, so the measure is left blank and the result carries a `spread_blank` warning. **Implemented.**

## Combine stages

A plan is an ordered list of named stages, each reading earlier stages or sources. The planner generates it from a
semantic request ([query.md](query.md)); it can be inspected but is not written by the agent. The engine's
`remaining` steps are the unexecuted tail of a plan.

| Stage | Purpose | Key fields |
| --- | --- | --- |
| `scan` | One source query at its native grain | source, measures, attributes, filters |
| `rollup` | Re-aggregate to coarser attributes, possibly through a relation | `group_by`, `through` |
| `spread` | Move to finer attributes | `to`, `spread` rule per measure |
| `union` | Stack inputs by column name, then sum each measure at the target grain | `inputs`, `group_by`, `through`, `label_as` |
| `join` | Attach a lookup (value map, bridge, dimension table) | `left`, `right`, `on`, `how`, `via` |
| `bridge` | Build a distinct key-to-group relation from a table and optional value map | `from`, `via`, `key`, `to` |
| `allocate` | Split additive measures across groups by explicit weights | `input`, `through`, `weights`, `group_by` |
| `compute` | Add columns from a closed expression set | `divide`, `subtract`, `add`, `multiply`, `coalesce`, `pct_change` |
| `sql` | Escape hatch: raw DuckDB SQL | `sql`, `reason` |

Step names in today's `remaining` lists are `spread`, `union`,
`measure_expression`, `finalize` and `combine`.

### Union

The default for combining measures: stack by column name, with NULL for measures an input does not have, then sum
each measure at the target grain. A sales row and a budget row never meet, so neither can duplicate the other.

```sql
SELECT region, product, SUM(revenue) AS revenue, SUM(budget) AS budget
FROM (SELECT region, product, revenue FROM sales
      UNION ALL BY NAME
      SELECT region, product, budget FROM budget_spread)
GROUP BY region, product;
```

Rules:

- `group_by` may contain only attributes bound in every input after any bridge. An attribute present in one input and
  not another is rejected with `union_attribute_not_shared`: roll up or spread the input first, or drop the attribute.
- Each input must already be aggregated at or below the target grain, as its descriptor's `x_grain` shows.
- Only additive measures, and semi-additive ones over non-time attributes, can be summed by a union. Non-additive
  measures must arrive at the final grain from their source.
- A measure appears in exactly one input (the designated-source rule), so a union never adds the same measure from
  two places (`duplicate_measure_source`).
- `SUM` over the NULLs from the other inputs' rows returns NULL rather than 0, which keeps "no budget" distinct from
  "zero budget".
- `label_as` adds a column naming each row's source step, for drill-through and debugging.

### Join

Joins attach lookups only (the bridge, a value map, a dimension table). Join keys must be fields bound to the same
catalog member in both tables, directly or through a value map named in `via` (`unbound_join_key`). Each joined
table's `x_grain` must equal the join keys after mapping, so a join cannot fan out (`grain_mismatch`). Two
measure-bearing tables are never joined before both reach the final grain (`cross_fact_join_before_rollup`).

### Allocate

Used when a key legitimately belongs to several groups, where a plain rollup would double count and fail with
`bridge_not_many_to_one`.

**Implemented for weighted relations** (fixtures `level3/23` to `27`): bind a numeric column of the registered table with
`{"field": "share", "role": "weight"}`. The bridge may then be many-to-many, and every measure rolled up through it is
multiplied by the row's share before the groups are summed, so actuals and budget are split the same way and every total
is preserved. Weights must be non-negative and sum to 1 (within 0.0001) for each key (`allocation_weights_invalid`). Only a
value that adds up (rollup rule `sum`) can be split (`non_additive_rollup`), even when the key is also requested. A key
with no row follows `on_unmatched`, with weight 1.

**Implemented at request level** (level 4; fixtures `level4/09` to `19`): `{measure, spread: {allocate: {by, ...}}}` splits a
measure onto the finer rows of the requested grain. `by: measure` weights by another measure at that grain (a result set that
already has it, or its source queried for it); `by: equal` with `members: present` weights by the rows present in the other
inputs, and with `members: catalog` by every member of the missing attributes. The weights are normalized within each of
the measure's own keys, so each key's value is preserved, and a total reads the measure at its own grain. A key with no
weights stops the query (`allocation_weights_invalid`, `fallback: fail`, the default) or is left empty (`fallback: blank`).
The weights used appear as result sets in the result (`w1`, …). `period: {unit, offset: -1}` takes the weights from the previous period
through a shifted scan ([query.md](query.md#prior_period-and-the-calendar)): budget split by last year's sales mix is fixture
`level4/42` (West 600 as 360 and 240, East 500 as 250 and 250). Any non-zero offset is implemented (a later period too, `level4/45`: with no sales that late the allocation names the missing weights), and it needs a time filter or
attribute to move (`shift_needs_time`). Negative is earlier in `prior_period` too
([open-questions.md](open-questions.md#period-shifts)).

Weights name their source:

| `by` | Weights come from | Typical use |
| --- | --- | --- |
| `relation` | A weight column in a relation table | Territory or cost-centre splits someone maintains |
| `measure` | Another measure at the finer grain, optionally time-shifted | Budget split by last year's sales mix |
| `equal` | An even split over the members present | Quick approximation, explicitly requested |

```json
{"measure": "budget.budget_amount",
 "spread": {"allocate": {"by": "measure", "measure": "orders.net_revenue",
                         "period": {"unit": "fiscal_year", "offset": -1}}}}
```

```sql
SELECT b.territory, SUM(bu.budget_amount * b.share) AS budget_amount
FROM budget bu JOIN terr_shares b ON b.region = bu.region
GROUP BY b.territory;
```

Rules:

- Weights for each key must be non-negative and sum to 1 within 0.0001, or the plan fails with
  `allocation_weights_invalid`. That guarantees allocated totals equal the source totals. (**Implemented** for weighted relations.)
- `by: equal` has a required `members`: `present` (members that appear in the other inputs at the target grain) or
  `catalog` (every member of the attribute). It has no default because the two give different answers.
- A key with no weights, such as a region with no prior-year sales, follows `fallback`: `fail` (default), `equal`
  or `blank`. The fallback used is reported in `assumptions`.
- Only additive measures can be allocated. Ratios and distinct counts are rebuilt from allocated additive parts.
- Every measure in a plan that goes through a bridge uses the same bridge and weights, so actuals and budget split the
  same way and their ratio stays meaningful.
- The weight source and its version are recorded in provenance. An even split is an approximation and goes in
  `assumptions`.

### Compute

**Implemented** for derived measures and transforms. Adds columns from a closed set (`divide`, `subtract`, `add`, `multiply`, `coalesce`),
evaluated after rollup so ratios are never summed: a derived measure is computed from the aggregated components at every grouping set,
including total rows, from different sources if need be ([catalog.md](catalog.md#measures)). Percent of total is a compute over a spread with `repeat`:

```sql
SELECT region, product, revenue,
       revenue / NULLIF(SUM(revenue) OVER (PARTITION BY region), 0) AS pct_of_region
FROM agg;
```

### Escape hatch

A `sql` stage is one DuckDB `SELECT` over stage ids and map ids. It must parse, may not reference anything else
(`escape_hatch_invalid`), and runs with external access disabled and configuration locked. Every use is logged with
its `reason`. **Implemented** for completions ([core.md](core.md#completing-a-partial-result)); as a plan stage it
is **Specified**.

## Totals

**Implemented** (level 3), computed by the source where its rollup rules allow ([native subtotals](#native-subtotals)) and by the
engine otherwise. Subtotal and grand-total rows are requested with `totals`:

```json
"totals": {"subtotal_by": ["customers.region", "products.product"], "grand_total": true}
```

`subtotal_by` lists attributes to roll up, outermost first, like SQL `ROLLUP`; query attributes not listed always
partition the rows. A **chain** (the default) has a grouping set for each count of innermost attributes rolled up,
so each set keeps a prefix of the list. With `"cube": true` every subset of the list is rolled up, like SQL `CUBE`: region totals *and* product totals, which is what a matrix with a total row and a total
column needs ([presentation.md](presentation.md)). A cube has 2 to the power n sets, so at most five attributes can be listed (`cube_too_wide`).
`grand_total: true` adds the set that keeps no attribute at all.

**One level column.** Every row of a totals result carries `_rollup_level`: 0 for a detail row, and for a chain k for a row that has
rolled up the k innermost listed attributes, so with `rollup: [region, product]` level 1 is a region subtotal (product rolled
up) and level 2 is the grand total. A cube numbers its sets by how many attributes are rolled up, then by position in the list: with two
attributes 0 is detail, 1 rolls up the first, 2 the second, 3 both (fixture `level3/34`). The numbering is one rule, shared by the engine's own sets and by the SQL and DAX
the sources are asked for, and the level map in the descriptor is the thing to read. It is the only marker: a rolled-up attribute is NULL on the row, and the level
separates that from a member that really is NULL. The result says what each level means, so a reader needs no knowledge of
the numbering:

```json
"x_totals": {"level_field": "_rollup_level", "subtotal_by": ["customers.region", "products.product"],
  "levels": [{"level": 0, "kind": "detail",      "kept": ["customers.region", "products.product"], "rolled_up": []},
             {"level": 1, "kind": "subtotal",    "kept": ["customers.region"], "rolled_up": ["products.product"]},
             {"level": 2, "kind": "grand_total", "kept": [], "rolled_up": ["customers.region", "products.product"]}]}
```

Only levels the result contains are listed (without `grand_total`, level 2 is absent), and attributes not listed in `subtotal_by` are in
every level's `kept`. This replaces the earlier `_grouping` bitmask, whose meaning had to be worked out from the list order.

- Each grouping set is built on its own and the sets are unioned.
- A renderer never sums cells. Totals are computed by a producer.
- A total for a measure comes from its rollup rule. Rules computable from detail rows (`sum`, `min`, `max`, `avg`,
  `first`, `last`) are rolled up in DuckDB; rule `none` (distinct counts, ratios) is recomputed by the source at the
  coarser grain. A repeated measure's subtotal is read at its own grain, never summed from repeated rows. See
  [open-questions.md](open-questions.md#totals-and-non-additive-measures).
- With `totals`, rows are ordered by attribute (requested attribute order first), `NULLS LAST`, so a subtotal follows
  its detail rows; `order` may name only attributes (`totals_order_not_attribute`). The limit counts total rows.
- Transforms are evaluated on total rows too: `pct_of_total` on the grand total is 100%, `rank` is NULL.

### Native subtotals

**Implemented** (fixtures `level3/01`, `31`, `32`). A source can compute its own subtotals, so for each result set the engine asks
for them in one query instead of rolling detail rows up itself or repeating the query at every coarser grain. In SQL that is
`GROUP BY <partition attributes>, ROLLUP(<listed attributes>)` with the level as `GROUPING(a) + GROUPING(b)`; in DAX each listed
attribute is wrapped in `ROLLUPADDISSUBTOTAL`, the combinations that skip a level are filtered out, and the level is the number of
flags set. For a cube, SQL uses `CUBE(...)` and DAX keeps every combination of the flags; both look the level up from which attributes are rolled
up (`CASE` in SQL, `SWITCH` in DAX), so it is the same number the engine would have given (fixtures `level3/34`, `35`). The source aggregates base rows, so a distinct count or any `none` measure is exact with no extra query per grain.

A measure's subtotals come from the source when every attribute it rolls up has a rule that agrees with the source: `none`, or
`sum` over a `sum` or `count`, `min` over a `min`, `max` over a `max`. A rule that does something else on the way up (`avg`, `first`,
`last`) stays with the engine, because the source's plain aggregate would be a different number. A result set may use both. The native
query is a result set of its own in the result (`d1_g`, with the level column and its own level map), the combine reads each grain from
it (`WHERE _rollup_level = k`), and it is not used for per-entity (`via`) or shifted result sets. The DAX is emitted as text and not run here (the stand-in runs the SQL equivalent).

**Choosing where subtotals are computed.** There are three modes, and the same totals fixtures run in each in the test suite and
must give the same rows:

| Mode | Subtotals come from |
| --- | --- |
| `native` | The source, for every measure whose rules allow it (above). The default |
| `engine` | The engine: detail rows rolled up here, and a repeat query per grain for a measure with rule `none`. No native query is made |
| `auto` | The source only for a measure that would otherwise need a query of its own (a rule of `none` on a rolled-up attribute); the engine rolls up the rest |

The mode is a preference, set at three levels, the most specific winning: the request's `totals.compute` (a query hint), then the
source's `totals` in the catalog's `sources` block (for a source that can't or shouldn't run `ROLLUP`), then `QisEngine.TotalsCompute`
(the engine's default). A measure the source can't subtotal correctly stays with the engine whatever the mode says; the result's
result sets show what ran (`d1_g` covers the measures the source subtotalled). Fixtures `level3/33` to `35`.

```yaml
sources:
  sqlserver_sales: {kind: sqlserver, dialects: [TSQL, DUCKDB], totals: auto}
```
```json
"totals": {"subtotal_by": ["customers.region"], "grand_total": true, "compute": "engine"}
```

## Worked example

Question: "Q1 revenue vs budget by region and product, with each product's share of its region's revenue. Split
budget to products using last year's sales mix." Sales come from SQL Server at `[region, product, month]`; budget
from SSAS at `[region, month]`.

Semantic request:

```json
{"spec_version": "0.3", "kind": "query",
 "query": {"measures": ["orders.net_revenue",
                        {"measure": "budget.budget_amount",
                         "spread": {"allocate": {"by": "measure", "measure": "orders.net_revenue",
                                                 "period": {"unit": "fiscal_year", "offset": -1}}}},
                        {"as": "pct_of_region", "transform": "pct_of_total",
                         "measure": "orders.net_revenue", "over": ["products.product"]}],
           "attributes": ["customers.region", "products.product"],
           "filters": [{"attribute": "date.fiscal_quarter", "operator": "equals", "values": ["FY27-Q1"]}],
           "limit": 100}}
```

Plan: scan sales at `[region, product]` for FY27-Q1; scan sales again for FY26-Q1 as the weight source; scan
budget at `[region]`; compute weights (`revenue_py` over its region's total); spread budget by those weights;
union; add totals; compute `pct_of_region` over a `repeat` of the region revenue. Steps 1 to 3 push down (T-SQL, T-SQL,
DAX); the rest run in DuckDB.

Expected rows (the sample data in `../data/seed.sql` matches):

| region | product | revenue | budget | pct_of_region |
| --- | --- | --- | --- | --- |
| West | A | 300 | 360 | 60% |
| West | B | 200 | 240 | 40% |
| West | Total | 500 | 600 | 100% |
| East | A | 100 | 250 | 25% |
| East | B | 300 | 250 | 75% |
| East | Total | 400 | 500 | 100% |
| Total | | 900 | 1,100 | 100% |

West's 600 splits 360/240 from last year's 60/40 mix; East's 500 splits 250/250 from 50/50. The budget totals equal
the source budget exactly because each region's weights sum to 1. **Proposed:** turn this into a level fixture once
`allocate`, totals and `pct_of_total` exist.

A second scenario, actuals vs budget by an uploaded territory list, uses a value map and bridge:

| Step | Detail |
| --- | --- |
| Register | `adhoc.user_territories@v3` with columns `territory`, `region_label` |
| Bind | `region_label` binds to `customers.region` via map `terr_region`; `territory` becomes `user_territories.territory` |
| Map | `terr_region` runs in `fuzzy` mode; unmatched values are listed |
| Bridge | Distinct `region → territory`, checked many-to-one |
| Scan | Sales and budget by `customers.region` |
| Union through the bridge | Sum both measures per territory |
| Compute | `pct_to_budget = net_revenue / budget_amount` from the sums |
| Unmapped | Regions with no territory roll into "Unmapped", so totals equal the source totals |

## Validation codes

**Specified** or **Proposed** unless marked.

**Implemented** for ad hoc tables and maps: `unconfirmed_binding`, `grain_unverified`, `unknown_target`, `unknown_table`,
`unknown_field`, `unknown_map`, `name_collision`, `adhoc_attribute_unrelated`, `multiple_keys_unsupported`,
`target_value_invalid`, `duplicate_from`, `catalog_map_scope_invalid`, `bridge_not_many_to_one`, `unmapped_values`,
`non_additive_rollup` (the design document's `non_additive_regroup`), `adhoc_filter_unsupported`, `allocation_weights_invalid`,
`weight_not_numeric`, `multiple_weights`, `allocation_weights_unavailable` (the weight measure can't be broken down by the
requested attributes), `allocate_weight_is_target`.

**Implemented** for totals: `totals_member_missing` (a rolled-up attribute that isn't a query attribute),
`totals_member_duplicate`, `totals_order_not_attribute`.

| Code | Rejects |
| --- | --- |
| `unknown_step_ref` | A reference that does not resolve to a document or table version |
| `truncated_input` | A step result with `x_truncated: true` |
| `unbound_join_key` | A join key not bound to a catalog member in both tables |
| `unconfirmed_binding` | A binding used as a join key before confirmation |
| `grain_mismatch` | A joined table whose grain differs from the join keys |
| `grain_unverified` | A declared grain that the table's rows violate |
| `non_additive_rollup` | A rollup that would sum a non-additive measure (the design document calls this `non_additive_regroup`) |
| `duplicate_measure_source` | The same catalog measure supplied by two steps |
| `union_attribute_not_shared` | A union grouped on an attribute not bound in every input |
| `cross_fact_join_before_rollup` | Two measure-bearing tables joined before both reach the final grain (design document: `cross_fact_join_before_regroup`) |
| `bridge_not_many_to_one` | A rollup through a bridge where a key maps to more than one group |
| `allocation_weights_invalid` | Allocation weights for a key that do not sum to 1, or are negative |
| `unknown_target` | A `binds_to` or `target` that is not a catalog attribute |
| `target_value_invalid` | An `llm` pair whose `to` is not a member of the target |
| `duplicate_from` | A source value mapped to more than one target |
| `escape_hatch_invalid` | SQL that fails parsing or references anything other than steps and maps (**Implemented** for completions) |

## Testing

Conformance fixtures for this spec use hand-written descriptors and rows, with no real queries, files or mapping
runs. They cover each stage type, every validation code, and each `on_unmatched` setting. Fuzzy-mode fixtures pin
exact scores at the threshold boundary, so changes to normalization or the similarity function are caught.
End-to-end scenarios use the sample data in `../data` with T-SQL on SQL Server and the DuckDB stand-in.
