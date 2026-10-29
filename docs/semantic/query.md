# Semantic query

The one document an agent writes. It says what the user wants in catalog terms: measures, attributes, filters,
order and limit, plus optional overrides and transforms. It never mentions grains, joins, sources or which engine
runs what. The planner and emitters derive those from the catalog ([catalog.md](catalog.md)).

Schema: `../spec/schemas/semantic_request.schema.json`.

## Envelope

```json
{
  "spec_version": "0.3",
  "kind": "query",
  "query": {},
  "assumptions": ["Treated 'revenue' as orders.net_revenue (catalog default)."]
}
```

| Field | Status | Notes |
| --- | --- | --- |
| `spec_version` | **Implemented** | The schema pins `0.3`. The native query tab of the design document calls this `ir_version`; see [open-questions.md](open-questions.md#naming-and-versions). |
| `kind` | **Implemented**: `query`, `clarify`, `unsupported` | See [Clarify and unsupported](#clarify-and-unsupported) |
| `assumptions` | **Implemented** | Interpretations the agent made, shown to the user. Copied into the combined result's `x_provenance.assumptions` next to the `overrides` the engine recorded. |

## Query

```json
{
  "measures": ["orders.net_revenue", "budget.budget_amount"],
  "attributes": ["customers.region", "products.product"],
  "filters": [{"attribute": "date.fiscal_quarter", "operator": "equals", "values": ["FY27-Q1"]}],
  "order": [["orders.net_revenue", "desc"]],
  "limit": 100
}
```

| Field | Required | Status | Notes |
| --- | --- | --- | --- |
| `measures` | yes | **Implemented** | At least one entry: a catalog measure name, or an override, ad hoc aggregate or transform ([below](#measure-entries)). |
| `attributes` | no | **Implemented** | Catalog attribute names, or registered ad hoc members (see [combine.md](combine.md)). |
| `filters` | no | **Implemented** | The core grammar ([core.md](core.md#filter-grammar)); top-level entries combine with AND. Includes the relative period operators ([Relative periods](#relative-periods)). |
| `order` | no | **Implemented** | `[[member_or_alias, "asc"\|"desc"]]`, applied in sequence. A member must be in the query. |
| `limit` | yes | **Implemented** | 1 to 10,000. The agent must always state a limit. |
| `segments` | no | **Implemented** | Names of catalog segments; their filters are added to the query's, with AND ([catalog.md](catalog.md#segments)). |
| `totals` | no | **Implemented** (level 3) | `{subtotal_by: [attributes, outermost first], grand_total, cube, compute}`; `cube` rolls up every subset of the attributes, not only the innermost in turn; `compute` (`native`, `engine`, `auto`) is a hint for where subtotals are computed. Subtotal and total rows; see [combine.md](combine.md#totals). Order may name only attributes. |
| `post_filters` | no | **Implemented** (with transforms) | Filters on transform outputs, applied after the transform (see [rank](#rank)). |
| `timezone` | no | **Implemented** | An IANA zone (`America/Chicago`) in which dates, days, weeks, months, quarters and years of a datetime are read. Defaults to the catalog's `timezone`, then `UTC`. `unknown_timezone` otherwise ([below](#datetime-and-time-zones)). |

**Deterministic order.** Every attribute is appended to the `ORDER BY` as a tiebreak so a limit cuts in the same
place on every run and on every dialect (fixture `level1/02`). **Implemented.** Under spread, NULLs sort last.

**What the engine does with a query today.** Plain measure names are compiled per source. Everything else in
`measures`, and any source that cannot supply a requested attribute, is handled as described in
[overview.md](overview.md#adoption-levels): returned as `remaining` steps below the level that executes it.

## Measure entries

A `measures` entry is one of four forms.

### 1. A catalog measure

`"orders.net_revenue"`. Rollup, spread and additivity come from the catalog. A **derived** measure (`orders.avg_order_value`, a ratio or
difference defined in the catalog, [catalog.md](catalog.md#measures)) is requested the same way, from level 4; it takes only an alias.

### 2. Aggregate a column directly

**Specified**, schema-defined, not executed (returned as a remaining `measure_expression` step).

```json
{"as": "avg_unit_price", "agg": "avg", "of": "orders.unit_price"}
```

`agg` is one of `sum`, `avg`, `min`, `max`, `count`, `count_distinct`, `median`. `of` must be a numeric column
exposed in the catalog or a descriptor. The new measure's rollup defaults to `none`, so totals are recomputed at
the source and never summed. `status` is `inferred`.

### 3. Override how a measure is aggregated

Applies to this request only; the catalog is never changed. **Implemented:** `as`, `filters` (level 2) and `spread` of
`repeat` or `blank` (fixtures `level2/07` to `15`, `level3/22`), `spread: {allocate: {by, ...}}` from level 4 (fixtures
`level4/09` to `19`, `42` to `45`), and `rollup`, `empty` and `acknowledge` from level 5 ([below](#rollup-overrides)). An allocation's `period` is implemented for the previous period (see [combine.md](combine.md#allocate)). A request-scoped measure is a
copy of the catalog measure registered under its output name for the length of the request, so the planner, emitters
and combine treat it like any other; the result's `x_binding` names the catalog measure it came from.

```json
{"measure": "orders.net_revenue", "as": "avg_monthly_revenue", "rollup": {"date.fiscal_month": "avg"}}
{"measure": "budget.budget_amount", "spread": "blank"}
{"measure": "budget.budget_amount",
 "spread": {"allocate": {"by": "measure", "measure": "orders.net_revenue",
                         "period": {"unit": "fiscal_year", "offset": -1}}}}
```

| Key | Meaning |
| --- | --- |
| `measure` | The catalog measure |
| `as` | Alias, snake_case, unique in the query |
| `rollup` | A map from attribute to rule (`sum`, `avg`, `min`, `max`, `first`, `last`, `none`). Always names the attribute it applies to. |
| `spread` | `repeat`, `blank`, or `{allocate: ...}`; see [combine.md](combine.md#spread) for weight sources |
| `filters` | Extra filters for this measure only; see [Filtered measures](#filtered-measures) |
| `empty` | `skip` (default: average only over members that have values) or `zero` (count empty members as zero) |
| `acknowledge` | `true` to override a governed measure's `none` rollup with `sum` |

Guards:

- A rollup override always names the attribute.
- Every override is recorded in the result's provenance (`overrides`, with its filters, spread and rollup), and the request's `assumptions` are
  copied there, so a reader sees that "average" or "blank" was a choice.
- Overriding a governed `none` rollup with `sum` needs `"acknowledge": true` (`acknowledge_required`), because summing averages or
  distinct counts is a common mistake.
- Overrides use the same rule vocabulary as the catalog; there is no override-only syntax.

#### Rollup overrides

**Implemented** from level 5 (fixtures `level5/07` to `18`, `level4/46`). `{measure, as, rollup: {dim: rule}}` asks for the measure at the
query's grain plus `dim`, then combines across `dim` by the rule (`sum`, `avg`, `min`, `max`): "average monthly revenue by region" is revenue per
region and month, averaged over the months. It is the per-entity mechanism generalized, so totals repeat the finer scan at the coarser grain
rather than averaging averages (the grand total of fixture `level5/10` is 300, not 150). Attributes must not already be in the query
(`rollup_attribute_in_query`). Several attributes are combined **one at a time, in the order written**: `{products.product: sum, date.fiscal_month: avg}`
adds the products within each month, then averages the months; written the other way round it averages each product's months and adds the
products, which is a different number whenever a product is missing from some months (fixtures `level5/16` and `17`). `first`, `last` and `none`
as override rules aren't implemented and come back as a remaining step. `empty` is `skip` (default: average over the members that have a value) or `zero` (count a member with no value as zero);
the members counted are those the source has under the query's filters, not every member the catalog could name. `empty` without a rollup is
`empty_needs_rollup`, and `empty: zero` over several attributes is `empty_needs_one_attribute`.

### 4. Transform

See [Transforms](#transforms).

## Filtered measures

**Implemented** (level 2; a level 1 engine returns the entry as a remaining step). Extra filters that apply to one measure only, so it can sit beside unfiltered measures. They
intersect with the query's filters and never replace them.

```json
{"measure": "orders.net_revenue", "as": "completed_revenue",
 "filters": [{"attribute": "orders.status", "operator": "equals", "values": ["Completed"]}]}
```

Rules:

- The measure's aggregation must be `sum`, `count`, `count_distinct`, `min`, `max` or `avg`. Derived and ratio
  measures are rejected, because the filter must apply to every component.
- Filter syntax is the query-level grammar including `and` and `or`. A filter member must be an attribute available in the
  measure's own source (`filter_member_not_in_source`), including joined attributes reachable many-to-one from its fact table. A
  filtered measure needs an alias (`alias_required`); output names are unique (`duplicate_measure`).
- **Canonicalization:** if a catalog measure has the same base measure and filters, the entry is replaced by that
  named measure. Named measures stay the preferred path.
- Dialects: SQL compiles to a conditional aggregate (`SUM(CASE WHEN … THEN … END)`); DAX wraps the measure in
  `CALCULATE` with `KEEPFILTERS`, so a query-level filter of `status = Cancelled` still applies. Filter values are
  bound as parameters, never concatenated.

The native query tab spells this `x_filtered`; the schema uses `measure` with `filters`, and that is the form the engine
accepts (see [open-questions.md](open-questions.md#naming-and-versions)). A catalog measure can also carry its own
`filters` (a named filtered measure, fixture `level2/11`); only the filters a request adds are wrapped around the model's
DAX measure, since the model measure already applies the catalog's.

## Relative periods

**Implemented** (fixtures `level1/31` to `46`, `78`, `79`, `level2/22`, `level4/46`, `47`, `57` to `59`). A period is an attribute whose members are stretches of the calendar: a level of
a calendar in the catalog (`date.fiscal_month`, `date.fiscal_year`; [catalog.md](catalog.md#calendars)), the calendar's date attribute (a day), or a datetime's bucket
(`orders.ordered_at_month`). Which dates a query covers is a filter on one of them, and how finely the rows break down is that attribute in `attributes`, like any other:

```json
{"attributes": ["date.fiscal_month"],
 "filters": [{"attribute": "date.fiscal_month", "operator": "last_complete", "count": 2}]}
```

Three operators are relative to the engine's clock, and `between` and `equals` cover what is written out:

| Filter | Meaning |
| --- | --- |
| `{attribute: <period>, operator: last_complete, count: n}` | The `n` periods before the current one. The current period is partial, so it is not included |
| `{attribute: <period>, operator: current}` | The whole of the current period |
| `{attribute: <period>, operator: period_to_date}` | From the start of the current period through today |
| `{attribute: <period>, operator: equals, values: [FY26]}` | One period by name. An ordinary filter, not resolved |
| `{attribute: date.calendar_date, operator: between, values: [start, end]}` | Dates written out, ISO dates, both ends included ([core.md](core.md#filter-grammar)) |

The relative operators take no `values`, and `count` belongs to `last_complete` alone. Relative text such as "last 6 months" is not allowed: the operators are a closed
set, resolved against the date table and the engine's clock before any query is compiled. The unit is the attribute itself, so no `unit` is needed. Units are data, so
`fiscal_month` and a calendar `month` can both exist, and a 4-4-5 calendar needs no special handling: the engine reads each level's first and last day from the date table and never computes with them.
The relative operators work anywhere a filter does: in `and`/`or`, and in a filtered measure's own `filters`.

- **Whole periods filter on the level's members, not on dates.** `last_complete`, `current` and `equals` become
  `date.fiscal_month equals 2026-07, 2026-08`. That applies to every source that has the level (the budget source has fiscal
  months and no dates), and the calendar can move it back a year for `prior_period`. `period_to_date` and written dates stop partway
  through a period, so they filter `date.calendar_date` with `between`, which only sources with a date column can apply
  (the others report it in `filters_not_applied`). A `between` on dates can be moved back for `prior_period` if the calendar gives the date attribute a
  counterpart ([catalog.md](catalog.md#calendars)); without one it is `shift_filter_unsupported`.
- **The result says what it resolved to.** `resolved` lists each relative filter with its path, attribute and operator, the clock date, the dates it covers, and for
  whole periods the members. A run record keeps the clock date, so replaying it gives the same rows tomorrow.
- **Where the clock comes from.** `QisEngine.Today` (UTC today by default). Fixtures pin it with a top-level `"today"`.
- **`period_to_date` twice.** The filter operator above selects the rows of the current period so far. The [`window` transform](#transforms) computes a measure over a frame of members at each member (year to date at each month among them); the two do different jobs and share only the words "to date".
- Errors: `period_operator_not_period_attribute` (the attribute isn't a period; the periods are the suggestions), `period_count_invalid` (`last_complete` without a count of at least 1, or a count on another operator),
  `period_operator_takes_values`, `invalid_date` and `between_invalid` for written dates, and `range_unresolved` (the date table has no period containing today, or fewer complete
  periods than asked for). The date table is read directly, so a period with no sales still counts. The date table is read from a source where the level and the date are columns of one table: SQL Server (T-SQL), or an Analysis Services model (DAX over its Date table), whichever the catalog has first (`calendar_unavailable` when none does). The operators then filter whichever source has the level as a column, including the SSAS model, whose Date table is at day grain with the fiscal month, quarter and year (fixtures `level4/126`, `127`). `period_to_date` and written-date `between` apply to the SSAS source too: a month's budget is dated on the month's first day, so a range holds the months whose first day it holds (fixtures `level4/128`, `129`, `130`).

### Datetime and time zones

**Implemented** (fixtures `level1/55` to `65`). A `datetime` attribute holds instants. Everything about *days* is a question about a zone, so the zone is part of the question:

- **Which zone.** The request's `timezone`, else the catalog's top-level `timezone` ([catalog.md](catalog.md#time-zones)), else `UTC`. The relative clock follows it: with no pinned
  date, "today" is the date it is now in that zone (it is already tomorrow in Tokyo while it is still evening in Chicago).
- **Values.** A datetime attribute's values leave the engine as ISO 8601 UTC instants, whatever the source stored. A source that stores local wall-clock time says so
  (`datetime_zone`), and the engine converts.
- **Filters.** The operators are the same as on any ordered type (`equals`, `gt`, `gte`, `lt`, `lte`, `between`). A value is an instant (`2026-07-01T04:30:00Z`, or with an offset; no offset means UTC)
  or a date, and a date is the whole of that day in the zone: `between [2026-08-01, 2026-08-01]` in Chicago is 05:00 UTC to 05:00 UTC the next day, `lt 2026-08-01` is before that day starts,
  `lte 2026-08-01` is through its end, `gt` is after it, and `equals 2026-08-01` is anything in it. `between` includes both ends. The engine turns the days into instants and compares the raw column with them, so no row is converted and an index can be used. A zone's day that has no
  midnight (a daylight-saving gap) starts an hour later.
- **Breakdowns.** Every datetime attribute has a bucket attribute per granularity: `orders.ordered_at_hour`, `_day`, `_week`, `_month`, `_quarter`, `_year`. Each is a string label in the zone:
  `2026-07-01 13:00`, `2026-07-01`, the Monday of the week (`2026-06-29`), `2026-07`, `2026-Q3`, `2026`. The source computes them (SQL Server `AT TIME ZONE`, DuckDB `timezone()`), so
  a group-by never leaves the source.
- **As periods.** A datetime is a calendar of its own, with no date table: its buckets are periods. `last_complete`, `current` and `period_to_date` work on `orders.ordered_at_day`, `_week`, `_month`, `_quarter` and `_year`
  (not `_hour`), counted in the zone (weeks start Monday, quarters in January, April, July and October), and a `between` on the datetime itself takes written dates, as above.
- **Tied to a date table.** A datetime attribute can say which date attribute of a date dimension its local date is a member of (`date: date.calendar_date`, [catalog.md](catalog.md#a-datetime-and-a-date-table)). Every
  attribute of that attribute is then also an attribute of the datetime: `orders.ordered_at_fiscal_month`, `_fiscal_quarter`, `_calendar_date_prior_year` and so on, read for the day the
  datetime falls on in the request's zone. The date table's own periods work on the datetime: the relative operators take the datetime's own copy of a calendar level
  (`orders.ordered_at_fiscal_month`, ...) as their period, and `prior_period` moves them back through the calendar's counterparts (fixtures `level1/66` to `68`, `level4/51`). A `between` on
  dates of the datetime itself can't be moved back (`shift_filter_unsupported`): filter on a fiscal period instead. A day the date table doesn't have leaves its attributes blank, because
  the join is a left join.
- **Not in SSAS.** DAX has no time zone conversion, so binding a datetime to an SSAS model is rejected when the catalog loads (`datetime_unsupported_source`); bind the model's date or text
  columns instead.
- Errors: `unknown_timezone`, `invalid_datetime` (a comparison value that isn't an instant), `invalid_date` and `between_invalid` for the whole-day operators.

## Transforms

**Implemented in DuckDB** over the combined rows (levels 4 and 5, below). A transform computes a measure at a different level of
detail or relative to other rows. The entry is flat: `as` names the output, `transform` is the kind, and the rest are that kind's fields, validated by the schema (`../spec/schemas/semantic_request.schema.json`).

```json
{"as": "region_share", "transform": "pct_of_total",
 "measure": "orders.net_revenue", "over": ["customers.region"]}
```

| `transform` | Purpose | Accepts | Required fields |
| --- | --- | --- | --- |
| `pct_of_total` | Value divided by the total across chosen attributes | rollup rule sum across each `over` attribute | `over` |
| `share_of_parent` | Value divided by its parent level in a hierarchy | additive | `attribute`, `parent` |
| `per_entity` | Aggregate per entity, then re-aggregate | any | `entity`, `agg` |
| `prior_period` | The measure shifted in time | any | `period`, `output` (`value`, `delta`, `pct_change`) |
| `running_total` | Cumulative sum along an ordered attribute: the cheap form for an additive measure, summing the rows shown (`window` is the general form, computed at the source) | additive | `along`, optional `reset_by` |
| `window` | The measure over a frame of calendar members around each member of `along`: year to date at each month, the trailing twelve months, this month and the next | any | `along`, and `start` with `end` or `offset`; optional `output` (`value`, `delta`, `pct_change`) |
| `index` | The measure as an index to one base member of `along`: each row's value divided by the value at the base member, times `scale` (the base reads 100) | any | `along`, `base`, optional `scale` |
| `rank` | Rank rows by the measure; `keep` also returns only the best N | any | optional `within`, `order`, `ties` (`skip`, `dense` or `break`), `keep` |

General rules:

- `measure` is a catalog measure, or the `as` of a transform written earlier in the same `measures` array
  ([Chaining transforms](#chaining-transforms)). A transform can't wrap a filtered measure or an override (its `as` isn't a
  transform): define a named catalog measure instead.
- **Filter preservation:** a transform is evaluated inside the query's filters. Any step that clears a filter to
  compute a denominator or window must put the query's own filters on the cleared columns back, so every dialect
  computes over the same rows.
- Additivity is checked from the measure's rollup rules (`transform_not_allowed`).
- `share_of_parent` is rewritten to `pct_of_total` over the hierarchy level during canonicalization, so there is
  one template.
- `over`, `along`, `within`, `attribute` and `parent` must name attributes in the query
  (`transform_member_missing`). `prior_period` and `running_total` need a time attribute or a filter on one
  (`transform_needs_time`), and a `window`'s `along` has to be a period attribute (`transform_needs_time` too).
- The schema has one branch per type with its required fields; the validator adds the checks below.
- **Where they run.** The base measure is computed through the normal plan (a measure asked for only as a transform's base is
  computed and not returned), then the transform is a window expression over the aggregated rows, so ratios are never
  summed. Compiling transforms into each native query is a later optimization ([open-questions.md](open-questions.md#where-transforms-run)).

| Type | Status | How |
| --- | --- | --- |
| `pct_of_total` | **Implemented**, level 4 | `value / SUM(value) OVER (PARTITION BY the attributes not in over)`, on detail rows (fixtures `level4/20` to `23`) |
| `share_of_parent` | **Implemented**, level 4 | Rewritten to `pct_of_total` over the attribute, after checking the catalog's `hierarchies` (`hierarchy_not_declared`) |
| `per_entity` | **Implemented**, level 4 | Scans the source one grain finer (with the entity), then aggregates over the entity here; totals repeat that scan at the coarser grain, so a grand total is the average over all entities, not an average of averages (fixture `level4/29`) |
| `running_total` | **Implemented**, level 4 | `SUM(value) OVER (PARTITION BY the other attributes ORDER BY along)`; it restarts for each combination of the other attributes, and `reset_by` must be one of them. Order is the attribute's value order |
| `index` | **Implemented**, level 4, in any role and on a datetime tied to a date dimension (`level4/104` to `121`) | `along` is a period attribute in the query's `attributes`; `base` is one of its members. The base is a scan of its own for each combination of the query's other attributes (see [Index to a base member](#index-to-a-base-member)) |
| `rank` | **Implemented**, level 5 | `RANK()` or `DENSE_RANK()` over detail rows, within `within`, by value (`order`, default desc) |
| `prior_period` | **Implemented**, level 4 | The same measure one calendar unit earlier (`period: {unit, offset}`, `-1` the previous one); see below |

| `window` | **Implemented**, level 4, in any role and on a datetime tied to a date dimension (`level4/56` to `79`) | `along` is a period attribute in the query's `attributes`; the window is read at each of its members. `start` is `0` or a negative number of members back, or `{period_start: <coarser level>}` (the first member inside the current member's period, so `{period_start: date.fiscal_year}` with `along: date.fiscal_month` is year to date); `end` is `current` or a number of members (negative earlier, positive later, `0` the current member). Members are counted in the date table's own order, so a member with no fact rows still counts, and a window that runs off either end covers the members that exist, not zeros. The source is asked for the measure over every member of the window, one scan per member shown (at most 60, `window_too_many_periods`) and labeled with that member: the right answer for a distinct count, which `running_total` (a sum of the rows shown) is not. The filters on the calendar choose which members are shown, not which are counted; every other filter, and every other attribute, partitions the scans. A coarser level of the same calendar beside `along` (the fiscal year beside the fiscal month) is read from the date table for each member, not scanned (`window_attribute_not_coarser` for one that isn't coarser). No total rows (`window_totals_unsupported`). Invalid frames are `window_frame_invalid` (start after the current member, end before start, a period start that ends before the current member, `offset` together with `start` or `end`, or neither an `offset` nor both a `start` and an `end`) and `window_period_not_coarser` (the period isn't another, coarser level of `along`'s calendar). Fixtures `level4/56` to `91` |

### Chaining transforms

A transform's `measure` can name the output of an earlier transform, so one transform builds on another. Cumulative share is a
share of the quarter, then its running total:

```json
{"measures": ["orders.net_revenue",
              {"as": "share",     "transform": "pct_of_total",  "measure": "orders.net_revenue", "over": ["date.fiscal_month"]},
              {"as": "cum_share", "transform": "running_total", "measure": "share", "along": "date.fiscal_month"}],
 "attributes": ["date.fiscal_month"]}
```

A name with a dot is a catalog measure and a name without one is a transform's `as`. A transform reads only transforms written
**before** it, so there are no cycles and output columns stay in the order written. A name that isn't an earlier transform
(a forward or self reference, a typo, the `as` of an override) is `transform_base_unknown`, with the earlier names as suggestions.
Fixtures `level4/92` to `102` and `level5/19`, `20`.

- **What can read a transform.** `pct_of_total`, `share_of_parent`, `running_total` and `rank` are window expressions over the
  aggregated rows, so they can read another transform's output; each is computed as its own step over the rows of the step
  before. `window`, `index`, `prior_period` and `per_entity` scan the source again, and a transform's output isn't something a source
  has, so they need a catalog measure: `transform_base_not_scannable`.
- **What adds up.** A transform whose output is read by a `pct_of_total`, `share_of_parent` or `running_total` must be additive,
  as a catalog measure must have rollup rule `sum` across the attribute (`transform_not_allowed`):

| Output of | Additive? |
| --- | --- |
| `pct_of_total`, `share_of_parent` | Yes (parts of a whole) |
| `prior_period` with `value` or `delta` | Yes, when the measure it shifts is |
| `prior_period` with `pct_change` | No |
| `window`, `index`, `running_total`, `rank` | No |

  A `rank` can read anything, since it only orders the values.
- **Totals.** A chain is computed on detail rows. A total row has NULL for every step but the `pct_of_total` rule (100% on a
  row that has rolled up every attribute it totals over), so `running_total` of a share is NULL on total rows and the share
  itself is 100% (fixture `level4/93`).
- **Levels.** Each step runs at its own level: a level 4 engine computes the share a rank reads and returns the rank as a
  remaining step (`level4/102`). A transform whose base doesn't run doesn't run.
- **Filters, `post_filters` and `order`** work as for single transforms: the chain is evaluated inside the query's filters, and
  `post_filters` and `order` can name any step's output (fixture `level4/94`).
- **A derived measure as a base.** A catalog derived measure (a ratio) is computed from its components after rollup, so
  `rank` can read it today (`level5/15`). `pct_of_total` and `running_total` can't: a ratio doesn't add up
  (`transform_not_allowed`, `level4/55`), and `prior_period` and `per_entity` can't wrap one because it has no source column of
  its own to scan again. The alternative is to chain: a transform that reads a transform computes from the aggregated rows,
  which a derived measure also is.

### Window offsets and outputs

`offset` is the shorthand for a window of one member: `{"as": "prev", "transform": "window", "measure": "orders.net_revenue", "along": "date.fiscal_month", "offset": -1}` is the same as `start: -1, end: -1`. A negative offset is the previous member (lag) and a positive one the next (lead); it can't be combined with `start` or `end` (`window_frame_invalid`). Unlike `start`, an `offset` may be positive.

`output` says what the entry returns, for any window (`offset` or `start` and `end`): `value` (default, the window's value), `delta` (the current member's own value minus the window's value) or `pct_change` (the delta over the window's value). A window with nothing in it gives a blank value, delta and pct_change: the first member of the data has no previous member, a member whose previous member has no fact rows has nothing before it, and a window value of zero has no `pct_change`. A blank is never shown as zero.

```json
{"as": "mom", "transform": "window", "measure": "orders.net_revenue", "along": "date.fiscal_month", "offset": -1, "output": "pct_change"}
```

**`window` offset or `prior_period`?** An offset counts *members of the date table*: `-1` on `date.fiscal_month` is the month before, in the date table's own order (so a gap in the table shows up as a gap in the order). `prior_period` moves the query's filters back through the calendar's counterpart column (`prior_year_fiscal_month`) and relabels what it reads, so it is the same period a year earlier even when the table has holes, and it needs the calendar's `shifts`. Use `offset` for "the previous row", `prior_period` for "the same month last year". Fixtures `level4/80` to `91`.

### Index to a base member

An index shows each row as a multiple of the value at one chosen member, so groups of different sizes can be compared by how they grew. The base member reads 100 (or `scale`):

```json
{"as": "growth", "transform": "index", "measure": "orders.net_revenue", "along": "date.fiscal_month", "base": "2025-07", "scale": 100}
```

- `along` is a period attribute (a calendar level, the calendar's date attribute, or a datetime bucket) that is in the query's `attributes`, as for a `window`. `base` is one member of it: a level's label (`2025-07`), an ISO date for the date attribute, or a bucket label. A member the date table doesn't have is `index_base_unknown`, with the closest members as suggestions (`level4/108`). `scale` is a number above zero and defaults to 100; `scale: 1` gives a plain ratio (`level4/112`, `113`).
- **Each group is divided by its own base.** The other attributes in the query partition the calculation: with the region beside the month, each region is divided by its own value in the base month, so every group reads 100 there (`level4/106`). A group with no value at the base member, or a base of zero, has a blank index, never zero or infinity (`level4/107`).
- **The base is read from the source, not from the rows shown.** It is a scan of its own with the query's filters on the calendar left out and every other filter kept, so a query limited to 2026 can still be indexed to a month of 2025 (`level4/105`), and a distinct count or any measure that can't be added up is right because the source computes it (`level4/109`). The roles, a datetime tied to a date dimension, and a calendar level beside the member (the fiscal year next to the fiscal month, `index_attribute_not_coarser` for a finer one) work as for a `window` (`level4/110`, `111`, `119`, `120`).
- **`resolved` says what the base was:** an entry with `operator: index`, the `attribute`, the base member in `members`, and the dates it covers, with the clock date, so the run can be explained and repeated.
- **Totals.** The detail rows are indexed; a subtotal or grand total row has no base of its own, so its index is blank (`level4/121`).
- **What can read it.** An index is a ratio to one value and doesn't add up, so `running_total`, `pct_of_total` and `share_of_parent` can't read it (`transform_not_allowed`, `level4/118`); a `rank` can (`level5/33`). It needs a catalog measure: a transform's output is `transform_base_not_scannable` (`level4/116`), and a derived measure is `transform_not_allowed` (`level4/117`).
- **Sources.** A source needs the `along` attribute (and the `period_start` level, if one is named) bound as columns, and the calendar's order is read from a SQL or an Analysis Services date table. The SSAS model qualifies for `fiscal_month`, `fiscal_quarter` and `fiscal_year` (fixtures `level4/123`, `124`, `125`); a source without those columns, or a datetime bucket with no date table behind it, is `calendar_unavailable`.

**How it differs from the others.** A `window` with an `offset` reads a value *relative* to each row (the previous member); `prior_period` reads the same period a year earlier through the calendar's counterpart column; an index reads *one fixed member* for every row. `pct_change` against a window gives growth as a fraction (`index / scale - 1`), but the base of a window moves with the row.

### prior_period and the calendar

The catalog's `calendars` say, for each unit (`fiscal_year`) and time attribute, the attribute that holds each member's counterpart one
unit earlier ([catalog.md](catalog.md#calendars)). A *shifted scan* moves the query's time filters back through it (a filter on
`date.fiscal_quarter = FY27-Q1` asks for FY26-Q1), scans the source, and relabels each earlier row with the current member it
corresponds to (2025-07 becomes 2026-07), so the earlier value lines up with the current row. `output` is `value`, `delta` (current
minus earlier) or `pct_change` (delta over earlier). Fixtures `level4/35` to `41`.

- Any non-zero `offset` works: `-1` is the previous period, `-2` the one before that (the counterpart of the counterpart), and a positive offset reads the calendar the other way (`+1` is the same period a year later, which needs the counterparts to be one-to-one, `calendar_not_one_to_one` otherwise). Every member between has to be in the date table, and a period with no data on the other side is blank (fixtures `level4/53` to `55`). An unknown unit is `unknown_period_unit`; no time attribute or
  time filter the calendar can move is `transform_needs_time`; a source that lacks the counterpart column is
  `calendar_unavailable` (the SSAS model has `Fiscal Month Prior Year` and `Fiscal Quarter Prior Year`, so month and quarter shifts work there, fixtures `level4/41`, `122`; its Date table is at day grain with a `Date Prior Year` column, so a day shift works too); two current members sharing one counterpart is `calendar_not_one_to_one`. Filters on a shifted *period* attribute
  can use any operator that applies to the attribute: an `equals` moves each member, and anything else (`not_equals`, `gte`, `starts_with`, ...) selects the date table's members that pass it and moves those, so "from 2026-07 on" becomes the counterparts of every month from 2026-07 on (fixture `level4/61`). Labels are compared as text, so period labels must sort in time order, as ISO labels do; that is also why a string attribute whose field is a time field accepts `gt`, `gte`, `lt` and `lte`. Filters on a shifted *date* attribute can use any date operator: each boundary moves to its day's
  counterpart, read from the date table; a boundary that isn't a day of the table moves to the nearest day inside the filter (so a range ending on
  the 31st of a short month, or a `period_to_date` past the table's last day, still selects the same days), and a single `equals` date must be a day of
  the table (`shift_date_unmapped`). This assumes the counterparts keep the order of the days (fixtures `level4/47`, `57` to `59`).
- The earlier value is a hidden measure, summed like any other, so total rows have one and `prior_period` is computed on them too. A
  measure that can't be added up can't be totalled this way (`prior_period_totals_unsupported`).
- **Row presence:** a period with an earlier value but no current one is dropped, so every emitter agrees (this settles the open
  question about SUMMARIZECOLUMNS returning such rows).

With `totals`, the other transforms are computed on detail rows. A total row gets NULL, except that `pct_of_total` is 100% on a row
that has rolled up every attribute it totals over. Below the level the engine returns the entry as a remaining step; the
base data still comes back.

### Rank

```json
"measures": ["orders.net_revenue",
  {"as": "rev_rank", "transform": "rank", "measure": "orders.net_revenue",
   "within": ["products.category"], "order": "desc", "ties": "skip"}],
"attributes": ["products.category", "products.product_name"],
"post_filters": [{"measure": "rev_rank", "operator": "lte", "values": [5]}]
```

`post_filters` may reference only transform aliases (`alias_invalid` otherwise), with numeric operators. They apply after
the transform, so "top 5" is the top 5 by the full ranking and not a rank recomputed after filtering. Rank is NULL on total
rows, so a post-filter on it drops them. Fixtures `level5/01` to `06`.

**Top and bottom N: `keep`.** `keep` is a whole number, 1 or more: the number of best-ranked rows to keep in each `within` group, best
meaning first in the direction of `order` (`desc` is the top N, `asc` the bottom N). It is a filter on the rank's own output, applied last,
after every transform and after totals and before `limit`. So the other transforms in the query still read every row (a `pct_of_total` beside
a `keep` is a share of all the rows, not of the ones kept), a total row (which has no rank) is never removed, and a rank's own value is
the rank among all rows, not among those a `post_filter` leaves. `keep` below 1 or not a whole number is `schema_invalid`.

```json
{"as": "top_regions", "transform": "rank", "measure": "orders.net_revenue", "keep": 3}
```

| `ties` | Rank column | Rows kept for `keep: N` |
| --- | --- | --- |
| `skip` (default) | 1, 1, 3, 4 | Every row with rank ≤ N, so a tie at the boundary keeps all of the tied rows and may return more than N |
| `dense` | 1, 1, 2, 3 | Every row with one of the top N distinct values |
| `break` | 1, 2, 3, 4 | Exactly N rows per group: the order is the value, then the query's attributes ascending, so the same rows come back every time |

A rank that another rank reads (`rank` of a `rank` with `keep`) still reads all rows, because `keep` only filters the final rows.
Fixtures `level5/21` to `32`.

## Combining with ad hoc data

**Implemented** (level 3; fixtures `level3/09` to `19`). A query may name an attribute that comes from a registered ad hoc table, such as
`user_territories.territory`. The planner finds the relation from the ad hoc member to the catalog attribute the
measures are defined on (here `customers.region`, through a value map and bridge) and uses it to roll the
measures up. The query does not name tables, maps or bridges. See [combine.md](combine.md).

Limits today: filters name the catalog attribute, not the ad hoc one (`adhoc_filter_unsupported`); a measure whose rollup
rule is `none` can't be taken to an ad hoc attribute (`non_additive_rollup`); an unconfirmed binding can't be used
(`unconfirmed_binding`). Below level 3 the engine returns each source at its catalog key with a `rollup` step through
the relation.

## Clarify and unsupported

**Implemented** (schemas `../spec/schemas/clarify.schema.json` and `unsupported.schema.json`; fixtures `level1/14` to `19`). The
agent returns `clarify` instead of guessing when a term maps to more than one catalog member, and `unsupported` when the
question needs something the spec cannot express.

```json
{"spec_version": "0.3", "kind": "clarify",
 "clarify": {"question": "Which revenue do you mean?",
             "options": [{"label": "Gross revenue (before returns and discounts)", "measure": "orders.gross_revenue"},
                         {"label": "Net revenue (after returns and discounts)", "measure": "orders.net_revenue"}],
             "partial": {"attributes": ["customers.region"], "limit": 100}}}
```

Two to four options, each resolving to concrete members; `partial` is an optional query the answer completes. If
the catalog marks one candidate as the default for an ambiguous term, the agent uses it and records the choice in
`assumptions`; clarify is for genuine ambiguity.

```json
{"spec_version": "0.3", "kind": "unsupported",
 "unsupported": {"reason_code": "set_filter",
                 "detail": "Filtering to customers who bought product X needs a set-based filter, which isn't supported yet."}}
```

Reason codes: `set_filter`, `cohort`, `funnel`, `nested_transform` (an engine that doesn't chain transforms), `custom_expression`, `unknown_concept`,
`out_of_domain` (a closed list; anything else is `schema_invalid`). Every unsupported response is counted by code
(`QisEngine.UnsupportedCounts`); the counts are the roadmap.

The engine checks a `clarify` document: two to four options, each naming a catalog measure or attribute of the right kind
(`unknown_member` with suggestions, `wrong_member_kind`), and a `partial` query whose members and filters are valid. A valid
one returns `status: partial` with `reason_code: clarification_needed`; the user's answer becomes a new query. An
`unsupported` document returns `status: failed` with one `unsupported` error carrying the reason code and detail, so the
user is told.

## Parameters

A request may leave a value to be bound: a value node `{"param": "name"}` stands for a value, and `parameters` beside `query` binds it (`{"regions": ["East", "West"], "quarter": "FY27-Q1", "n": 5}`). A parameter stands for a **value, never for a name**: a filter value, or an argument that is a number, a member of a fixed set or a boolean (the request's `limit`, a transform's `keep` or offset). It never stands for an attribute, a measure, an operator, a transform's name or a calendar level.

`parameter_definitions` gives a name more: `for` (the attribute it is a value of), `type` (`string`, `integer`, `number`, `boolean`), `many` (it is a list), `default` and `required`.

* In a filter, `"values": [{"param": "quarter"}]` takes one value or splices a list; `"values": {"param": "regions"}` is the whole list (a single value is a list of one). A list is valid where the argument takes several values; elsewhere it is `param_type_mismatch`.
* The empty list is refused (`param_empty_list`): "no values" silently meaning "no filter" is how a dashboard shows everything by mistake.
* A parameter in a filter's values is for that filter's attribute; a definition naming another attribute is `param_for_mismatch`. Every bound value is checked before any source is read: against the definition's type or the attribute's type (`param_type_mismatch`), and against the member list of a catalog-member attribute (`param_unknown_member`). `param_missing` is a name with no binding and no default; `param_unknown` is a binding no value uses.
* Parameters are resolved once, before the request is validated, hashed or planned: the request hash, the cache key and a replay are those of the literal form, and a parameterized request returns the rows of its literal form.

Not yet: `?`, `$n` and `:name` markers in semantic SQL, prepared statements on a model, the `members` call, `empty: matches_none`, and the range check of an argument's parameter (plan 19, J25).

## Validation and canonicalization

Validation runs after JSON Schema checks and before any emitter. An invalid request returns errors to the agent for
at most two retries; after that the user sees a failure message, never a guessed query.

**Implemented:** `schema_invalid`, `unknown_member`, `wrong_member_kind`, `invalid_operator`, `missing_values`,
`order_member_missing`, `duplicate_measure`, `alias_required`, `alias_invalid`, `transform_not_allowed`,
`transform_member_missing`, `hierarchy_not_declared`, `transform_entity_in_query`, `unknown_segment`, `derived_measure_override`,
`acknowledge_required`, `rollup_attribute_in_query`, `empty_needs_rollup`, `unknown_period_unit`, `transform_needs_time` (for
`prior_period`, `running_total` and `window`), `window_frame_invalid`, `window_period_not_coarser`, `window_attribute_not_coarser`, `window_too_many_periods`, `window_totals_unsupported`, `index_base_unknown`, `index_attribute_not_coarser` (and `transform_needs_time` for an `index`), `unconfirmed_binding`, `adhoc_filter_unsupported` (see [core.md](core.md#errors)).

An attribute a measure's source can't reach isn't an error: that source's measure is spread like a coarser one. Fan-out can't happen, because
only many-to-one paths are joined.

**Also implemented:** `invalid_filter_value` (fixtures `level1/54`, `55`): an `equals` or `not_equals` value that isn't a member of an attribute
whose catalog lists its `members` ([catalog.md](catalog.md#attributes)), with the three closest members as suggestions. An attribute without a
`members` setting is never checked, so a free-text attribute costs no query. Also `filter_mixes_kinds`, `having_measure_missing`,
`having_value_not_number` (filters on measures; see [core.md](core.md#filter-grammar)).

**Request shape**, checked before the schema so the error says what is wrong (fixtures `level1/69` to `72`):

| Code | Rejects |
| --- | --- |
| `limit_missing` | A query with no `limit`; there is no default |
| `limit_exceeded` | A limit above 10,000 |
| `extension_misplaced` | Any field of a request whose name starts with `x_`. The prefix marks extensions of the descriptor and the catalog; a request's own fields are plain. A field that used to carry the prefix (`x_totals`, `x_post_filters`, `x_transform`, `x_assumptions`) is refused with the name it has now |

A limit that is below 1 or not an integer is still `schema_invalid`.

Canonicalization runs on valid requests and produces one form per meaning:

1. Resolve relative period operators to the members or dates they mean.
2. Normalize filter values to the catalog's casing and sort the values within each filter.
3. Replace filtered-measure entries that match a named catalog measure with that measure.
4. Rewrite `share_of_parent` to `pct_of_total`.
5. Add the deterministic tiebreak sort key.

Measure and attribute order is kept, since it sets output column order. A hash of the canonical request is the
cache key and the comparison key in evaluations (**Implemented** for the hash and the deterministic tiebreak; segment expansion happens on a copy, so the hash is of the request as written).

## Totals

Subtotal and grand-total rows are requested with `totals` and described in [combine.md](combine.md#totals). Below level 3
the engine returns the base result sets with a `totals` step in `remaining`.
