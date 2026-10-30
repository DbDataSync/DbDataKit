# Catalog (the semantic model)

The catalog describes what data exists, how it joins, and what each measure means. The agent writes queries
against its logical names; emitters resolve them to tables and columns.

**Status: Implemented** for loading, validation and export (this section's format is what `catalog/catalog.yaml` is
written in, and the engine reads it). Schema: `../spec/schemas/catalog.schema.json`; loader: `CatalogLoader`.

## Starting point: Apache Ossie

The structure follows the [Apache Ossie](https://github.com/apache/ossie) core spec (formerly Open Semantic
Interchange, version `0.2.0.dev0`, a draft). Ossie gives us a vendor-neutral way to describe physical models:

- a model document with `version`, `name`, `description`, `ai_context`
- `datasets[]`: `name`, `source` (a physical table, view or query), `primary_key`, `unique_keys`, `fields[]`
- `fields[]`: `name`, `expression` (a list of `{dialect, expression}` pairs), `dimension: {is_time}`, `datatype`,
  `label`, `description`, `ai_context`
- `relationships[]`: `from` (the many side), `to` (the one side), `from_columns`, `to_columns`
- `metrics[]` at model or dataset scope, with a `dialects` expression
- `custom_extensions[]`: `{vendor_name, data}` where `data` is a JSON string

We keep these shapes and extend or replace Ossie wherever it limits what the query specs need. The words differ: Ossie's *dataset* is a
**table** here and its *field* is a **column**, because a table is what a fact table and a dimension are, and a column is what an attribute and a
measure read ([overview.md](overview.md#terminology)). Export and import translate (see [Ossie export](#ossie-export)); an Ossie `dimension: {is_time}` on a
field is `is_time: true` on the column. The table below lists each limit and the decision. Anything marked Replace is a proposal for review.

| Ossie today | Limit for us | Decision |
| --- | --- | --- |
| One model per document; `source` is just `db.schema.table` | A catalog spans engines (SQL Server and SSAS) and must say which engine runs what | **Extend.** Add a `sources` registry and `x_source` on each table. Export splits the catalog into one Ossie document per source. |
| Metric = a SQL expression such as `SUM(f.net_amount)`; no metric may reference another | Emitters need the aggregation, column, filters and semantics as data, not SQL text, to build T-SQL, DAX and rollups; ratios need to reference measures | **Replace** metrics with structured `measures` (`agg`, `of`, `filters`, or a closed `compute`). The Ossie metric expression becomes an optional per-dialect override. |
| No additivity, rollup, spread or native grain | These drive union, rollup, spread and totals | **Extend.** `semantics` block on each measure ([below](#measure-semantics)). |
| `dimension` is only `{is_time}` on a field | A dimension is a table, and its columns are attributes. Dimensions are shared across sources (conformed); their attributes need sort keys, member lists and hierarchies | **Replace** with logical `dimensions`, each holding `attributes` bound to table columns per source. Keep `is_time` on the column. Ossie's field-level `dimension` is read as marking a column that is an attribute, not a dimension. |
| Relationships are many-to-one column lists, unnamed roles | Role-playing dimensions (order date vs ship date: one table read in two roles), verified cardinality | **Extend.** Add `x_role` and `x_cardinality`; keep the rest. |
| No calendar | Fiscal periods, prior-period and to-date relations | **Extend.** `calendars` ([below](#calendars)). |
| Dialects are a closed enum with only `OSSIE_SQL_2026` defined; no T-SQL or DuckDB | We emit T-SQL, DAX and DuckDB | **Extend.** Accept `TSQL` and `DUCKDB` as dialect names in the authoring format. Export maps them to `ANSI_SQL` where portable and drops them otherwise. |
| `custom_extensions[].data` is a JSON string | Awkward to author and review in YAML | **Replace in authoring, keep in export.** Authoring uses `x_` keys directly; export serializes them into `custom_extensions` (`vendor_name: COMMON`). |
| Hierarchies are not defined | `share_of_parent` needs them | **Extend.** `hierarchies` ([below](#hierarchies)). |

Authoring format is therefore a superset of Ossie. A strict Ossie document can always be exported from it; the
reverse needs the QIS blocks added.

## Document shape

```yaml
version: "0.3"            # QIS catalog version; Ossie version on export
name: sales_catalog
description: Governed sales and budget model.

sources:                  # extension
  sqlserver_sales: {kind: sqlserver, dialects: [TSQL, DUCKDB]}
  ssas_finance:    {kind: ssas, dialects: [DAX, DUCKDB]}   # DUCKDB = test stand-in

tables: []                # Ossie datasets, each tagged with x_source
relationships: []         # many-to-one joins between tables
dimensions: {}            # logical, cross-source: each a table with its attributes (columns)
measures: {}              # structured, replaces Ossie metrics
hierarchies: {}
calendars: {}
segments: {}
```

The repository's sample data is the working example: [`../catalog/catalog.yaml`](../catalog/catalog.yaml).

## Tables, columns and relationships

Ossie's shapes in the catalog's words, with one added key:

```yaml
tables:
  - name: fact_sales
    x_source: sqlserver_sales
    source: dbo.fact_sales
    primary_key: [order_id]
    columns:
      - name: net_amount
        datatype: Decimal
        expression: {dialects: [{dialect: ANSI_SQL, expression: net_amount}]}
      - name: date_key
        datatype: Integer
        expression: {dialects: [{dialect: ANSI_SQL, expression: date_key}]}
relationships:
  - name: sales_to_date
    from: fact_sales
    to: dim_date
    from_columns: [date_key]
    to_columns: [date_key]
    x_role: order_date          # needed when a fact table has two relationships to the same dimension table
```

Rules:

- A relationship is many-to-one from `from` to `to`. The validator checks that `to_columns` are a primary or unique
  key of `to` (`relationship_not_many_to_one`). Join conditions are built from the column lists, not written as SQL text, which
  removes a SQL injection and typo surface.
- Fan-out is rejected by construction: a measure only reaches an attribute along many-to-one paths from its fact table, however many hops
  (`unreachable_attribute`).

**Snowflake.** A dimension table can hang off another (fact, product, category): every table reachable by many-to-one relationships is
joined, and a join that hangs off another comes after it (`JoinDef.Parent`). Fixture `level2/16`.

**Role-playing dimensions.** When a fact table reaches one table by two relationships (the date a row happened, the date it shipped), each
relationship gets an `x_role`, and the table is joined once per role. The dimension declares which role its own name reads by default, once, with `default_role`
(`dimensions.date.default_role: order_date`); its attributes' bindings don't repeat it. Without roles the catalog is rejected (`ambiguous_relationship`), as is a dimension
bound to such a table that names no `default_role`.

The dimension (`date`) is the one definition, and its roles are the only other names it answers to: `order_date` and `ship_date`. The loader derives them from the
relationships, so `ship_date.fiscal_month` is the `date` dimension's `fiscal_month` attribute reached through the ship relationship, and nothing else needs declaring.
A request can use the dimension's own name or a role name, and the role decides which join is read:

1. A role name (`ship_date.fiscal_month`, `order_date.fiscal_quarter`) overrides everything, for every measure in the request.
2. The dimension's own name (`date.fiscal_month`) reads the measure's role: a measure's `roles` (`roles: {date: ship_date}`) applies to every `date.*` attribute and filter of that measure.
3. A measure with no `roles` entry for the dimension reads the dimension's `default_role` (`order_date`).

A role can't be named like a dimension (`role_name_collision`); that is why the sample's default role is `order_date`, not `date`.

Different measures in one query can read the same bare attribute in different roles. The engine scans each role combination as its own result set and combines them
on the shared column, and says so (warning `role_differs_by_measure`), because one column then holds two meanings; a descriptor's measure field carries
`x_roles` for what it read. Name a role to read one role for every measure. A role named in `roles` that its dimension doesn't have is rejected (`unknown_role`),
as is a derived name that is already a dimension (`role_name_collision`). Fixtures `level2/17`, `18` and `25` to `27`.

**Several fact tables in one source.** A source may have more than one fact table (sales and returns in one database), found from the
measures' `of.table`. The first fact table keeps the source's name; each further one is named `source.table` in results. A conformed attribute
binds to every fact table that can reach its table and not to one that can't: grouped by an attribute a fact table can't reach, that fact table's measures are
spread (blank by default) like a coarser source. Fixtures `level2/19` to `21`.
- Column expressions are scalar, with no aggregation.
- Physical names live only in `source` and `expression`. Everything else uses logical names.

## Dimensions and attributes

A dimension is a table (customers, products, date; `orders` is a degenerate one held in the fact table), and its columns are its **attributes**. A dimension is shared across sources;
each attribute binds to one table column per source, and a request names it as `dimension.attribute`:

```yaml
dimensions:
  customers:
    label: Customers
    attributes:
      region:
        type: string
        label: Region
        synonyms: [region, sales region]
        sort_key: region_sort                  # optional; another attribute of this dimension giving catalog order, such as calendar order for months
        members: {from: sqlserver_sales}       # optional; where the canonical member list comes from
        bindings:
          sqlserver_sales: {table: dim_customer, column: region}
          ssas_finance:    {column: region, native: {DAX: "'Customer'[Region]"}}
  date:
    default_role: order_date                   # the role the bare name reads (see role-playing dimensions)
    attributes: {}
```

- The binding for a source says which column answers the attribute there. A measure can be grouped by an attribute
  only if its source has a binding for it. Missing bindings are what make a result set coarser than requested and
  trigger a spread.
- `members` supplies the canonical value list used for value maps and for allocating equally over members
  (`members: catalog`). **Implemented** (fixture `level4/56`), in two forms: a list, `members: [East, South, West]`, or
  `members: {from: sqlserver_sales}`, which reads the distinct values from that source's dimension table, so a member with
  no fact rows still counts (North has a customer and no sales in the sample). Without `members`, an attribute's members are the
  values that appear in a source's fact rows. `members_unknown_source` and `members_source_unbound` are checked on load.
  They also make `invalid_filter_value` possible: a filter on a member the list doesn't have is refused with the closest members
  ([query.md](query.md#validation-and-canonicalization)). **Open**: where the list is stored for a very large attribute.
- `native: {DAX: ...}` on a binding is the native expression for sources whose model uses different names (the same key as on a measure).
- A DAX measure that is filtered by a request also tests that its group has fact rows, so SSAS keeps the groups SQL would keep (`SUMMARIZECOLUMNS` drops a group whose measures are all blank). The table it tests with `NOT ISEMPTY` is the one in the DAX expression of the field the measure aggregates (`'Budget'[Amount]`), or `nonempty: {DAX: ...}` on the measure when the model keeps its measures on a measures table or the field has no DAX expression. The check returns blank when the table is empty, so it adds no groups.
- Registered ad hoc tables add dimensions and attributes with the same shape; see [combine.md](combine.md#registering-ad-hoc-tables).

## Measures

```yaml
measures:
  orders.net_revenue:
    type: number
    source: sqlserver_sales
    agg: sum                                # sum, avg, min, max, count, count_distinct
    of: {table: fact_sales, column: net_amount}
    synonyms: [revenue, sales, net revenue]
    semantics:
      grain: [customers.region, products.product, products.category, date.fiscal_month, date.fiscal_quarter]
      rollup: {default: sum}
      spread: blank
      status: governed
    native:
      DAX: "[Net Revenue]"                  # the measure's name in the SSAS model
```

A derived measure is a closed `compute` over other measures (**Implemented**, level 4, fixtures `level4/47` to `55`); it is evaluated after
rollup at every grain so ratios are never summed, and a total is a ratio of totals:

```yaml
  orders.avg_order_value:
    type: number
    compute: {op: divide, args: [orders.net_revenue, orders.order_count]}
  finance.pct_to_budget:                      # reads measures from two sources
    type: number
    compute: {op: divide, args: [orders.net_revenue, budget.budget_amount]}
  orders.net_after_returns:                   # operands can be numbers or nested operations
    type: number
    compute: {op: subtract, args: [orders.net_revenue, {op: coalesce, args: [returns.return_amount, 0]}]}
```

Operations are `divide` and `subtract` (exactly two operands), `add` and `multiply` (two or more), and `coalesce`. Division by zero is NULL. A
derived measure has no source, no rollup rule of its own (it is recomputed, so rule `none`) and takes only an alias in a query; its
components are queried as hidden measures, each by its own source, rollup and spread rules. It can be the base of `rank`, but not of a
transform that needs it to add up (`transform_not_allowed`).

A named filtered measure carries filters, with the same grammar as a query filter (**Implemented**: `filters` on a measure in
`catalog.yaml`, fixture `level2/11`):

```yaml
  orders.completed_revenue:
    type: number
    source: sqlserver_sales
    agg: sum
    of: {table: fact_sales, column: net_amount}
    filters: [{attribute: orders.status, operator: equals, values: [Completed]}]
```

Named measures are the preferred path; an `x_filtered`-style entry in a request is canonicalized to a named
measure when one matches (see [query.md](query.md#filtered-measures)).

### Measure format

```yaml
budget.budget_amount:
  format: {style: currency, currency: USD, decimals: 0}   # style: number (default) | currency | percent
```

How a person reads the measure's numbers. The engine copies it to the result's descriptor (`x_format`) and views use it ([presentation.md](presentation.md#matrix)). A percent
style means the value is a fraction. A currency style without a `currency` is rejected (`format_currency_missing`).

### Measure semantics

| Key | Meaning |
| --- | --- |
| `grain` | The native grain: attributes the value is actually defined on |
| `rollup` | How the measure combines when an attribute is removed. A map from attribute to rule, with `default` for the rest |
| `spread` | What the measure shows when an attribute it has no value for is added: `repeat`, `blank`, or `allocate` with a weight source |
| `status` | `governed` for catalog definitions, `inferred` for ad hoc and agent-created measures |

Rollup rules:

| Rule | Meaning |
| --- | --- |
| `sum`, `min`, `max`, `avg` | Aggregate across the removed attribute |
| `first`, `last` | Value at the first or last member in scope (time attributes only); `last` means the last period in scope for the whole group, like DAX `LASTDATE` |
| `none` | Cannot be combined; must be recomputed at the source at the coarser grain |

```yaml
inventory.on_hand:
  semantics:
    rollup: {inventory.warehouse: sum, date.fiscal_month: last}
```

With on-hand quantities per warehouse and month, grouping by month sums over warehouses, grouping by warehouse
takes the last month, and grouping by neither takes the last month then sums warehouses (never the sum of both).

**Additivity is derived, not stored.** `additive` means every rule is `sum`; `semi_additive` means `sum` for
non-time attributes and `last`, `first` or `avg` for time; `non_additive` means any `none`. This replaces the
`x_additivity` field in the descriptor ([open-questions.md](open-questions.md#additivity-and-rollup)).

**Differences from the engine today:** `rollup` is only `{default: sum}` or `{default: none}`; `first`, `last`
and per-attribute rules are not read; `spread: repeat` and `blank` exist but `allocate` does not.

## Hierarchies

**Implemented** as declarations (`hierarchies:` in `catalog.yaml`): `share_of_parent` checks them. The many-to-one check on the data
is `QisEngine.CheckHierarchies()`, which an operator or a scheduled job runs; it isn't run on every query.

```yaml
hierarchies:
  products.by_category:
    levels: [products.category, products.product]    # coarse to fine
```

Each level must be many-to-one onto the one above, verified on the data. `CheckHierarchies()` reads each adjacent pair of levels, from the
dimension table when both are columns of one table (so a member with no fact rows counts), and returns a finding for every child member with
more than one parent: `{hierarchy, parent, child, kind: many_to_one_violated, examples: ["A -> Laptops, Monitors"]}`. A pair no source binds
both of is `unchecked`. Needed by `share_of_parent`, and usable as a relation when rolling up.

## Time zones

```yaml
timezone: America/Chicago          # the business zone, IANA; the default is UTC
sources:
  sqlserver_sales: {kind: sqlserver, datetime_zone: UTC}   # how its datetime columns are stored: UTC (default) or an IANA zone's wall-clock time
```

`timezone` is the zone in which a day, week, month, quarter or year of a datetime is read unless a request says otherwise. An attribute of type `datetime` (bound to a `DateTime`
column) gets a bucket attribute for each of hour, day, week, month, quarter and year, derived by the loader (`orders.ordered_at_month`), so none are declared. Details and the
filter operators are in [query.md](query.md#datetime-and-time-zones). `unknown_timezone` rejects a zone that isn't an IANA name; `datetime_unsupported_source` rejects a datetime bound to
an SSAS model.

## A datetime and a date table

```yaml
orders.ordered_at:
  type: datetime
  date: date.calendar_date      # an attribute of a date dimension, one value per day: the datetime's local date is one of its members
  bindings:
    sqlserver_sales: {table: fact_sales, column: ordered_at}
```

The loader derives, for every attribute of that date dimension, an attribute of the datetime named `datetime_attribute`: `orders.ordered_at_fiscal_month`, `orders.ordered_at_fiscal_quarter`,
`orders.ordered_at_calendar_date_prior_year`, and so on. They are read by joining the date table on the datetime's local date in the request's zone (a left join: a day the table lacks leaves the
attributes blank). The datetime's calendar then has the date dimension's levels as well as its own, and the date dimension's shifts apply to the derived attributes. `date_link_not_datetime`
and `date_link_unknown_attribute` reject a link from something that isn't a datetime, or to something that isn't a date attribute. Details in [query.md](query.md#datetime-and-time-zones).

## Source settings

A source can carry `totals: native | engine | auto`, how it computes subtotals unless a request's `totals.compute` says otherwise
([combine.md](combine.md#native-subtotals)). Set `engine` for a source that shouldn't run `ROLLUP`, `auto` to use it only for
measures that can't otherwise be totalled from detail rows. It survives the Ossie export and import.

## Calendars

**Implemented.** A calendar does two jobs, and a catalog can use either: it names the date attribute and its levels, so queries can
use relative periods on them ([query.md](query.md#relative-periods)), and it says how to move a time attribute back one unit.

```yaml
calendars:
  fiscal:
    date: date.calendar_date          # type: date, one value per day
    levels:                            # a period: the attribute holding that level's member for each day
      day: date.calendar_date
      fiscal_month: date.fiscal_month
      fiscal_quarter: date.fiscal_quarter
      fiscal_year: date.fiscal_year
    shifts:
      fiscal_year:
        date.fiscal_month: date.fiscal_month_prior_year
        date.fiscal_quarter: date.fiscal_quarter_prior_year
```

Because the counterpart is data (a column in the date table), it works for any fiscal calendar, including a 4-4-5 one, if the
column is filled in that way; no date arithmetic is assumed. Checked on load: `calendar_unknown_attribute`, `calendar_not_time`,
`calendar_conflict`, and for levels `calendar_not_date` (the date attribute must have type `date`). Used by `prior_period` and by allocation
weights from the previous period.

For `shifts`, a calendar names, for each unit and time attribute, the attribute that holds each member's counterpart one unit earlier;
those attributes are ordinary (bound to a column such as `prior_year_fiscal_month`). The `levels` work the same way: a level is an attribute
bound to a column of the date table, so fiscal and calendar levels are alike, and the engine reads each member's first and last day from the table.

A date attribute can have a counterpart too (`date.calendar_date: date.calendar_date_prior_year`, a date column in the date table), which lets a
filter on dates move back for `prior_period` ([query.md](query.md#prior_period-and-the-calendar)).

A year to date at each month, or a trailing twelve months, is the `window` transform ([query.md](query.md#transforms)), which uses the calendar's levels and needs no relation of its own. A "to date" relation in the catalog (each month related to the months before it in its year) is not needed for it and is not planned.

**Roles.** A calendar is written once, for the dimension's own name, and it applies to every role of the date dimension. `ship_date.calendar_date` is a time attribute with the
same levels read in the ship role (`ship_date.fiscal_month`, ...), and each offset moves a role's attribute back through the same role of its counterpart
(`ship_date.fiscal_month` to `ship_date.fiscal_month_prior_year`). The loader derives these from the base declaration, so nothing is repeated. With the dimension's own name
(`date.calendar_date`, `date.fiscal_month`) a measure reads its own role, so a measure with `roles: {date: ship_date}` has its relative periods, its breakdowns, its
prior period and the weights of an allocation all read by ship date. Fixtures `level1/54`, `level4/48` to `50`.

## Segments

**Implemented** (fixtures `level1/20` to `22`). Named filters a query can name; their filters are added to the query's own, combined with AND:

```yaml
segments:
  orders.completed:
    filters:
      - {attribute: orders.status, operator: equals, values: [Completed]}
```

```json
{"measures": ["orders.net_revenue"], "attributes": ["customers.region"], "segments": ["orders.completed"], "limit": 100}
```

An unknown segment is `unknown_segment` (with the catalog's segments as suggestions); a segment filtering on something that isn't an attribute is
rejected when the catalog loads (`segment_unknown_member`).

## Saved queries

A model can offer named queries (plan 19, J26): a semantic SQL text or a request, and what its parameters are. A server that hosts the model (the Proxy) offers each as a **view** (no required parameter) or a **function** (parameters) on the model's source, and it runs as the caller like any query, so the caller's own access applies.

```yaml
queries:
  revenue_by_region:
    description: Net revenue and orders by region for a fiscal quarter
    sql: >-
      SELECT customers.region, MEASURE(orders.net_revenue), MEASURE(orders.order_count) FROM sales_catalog
      WHERE date.fiscal_quarter = :quarter GROUP BY ALL ORDER BY MEASURE(orders.net_revenue) DESC LIMIT 100
    parameters:
      quarter: {for: date.fiscal_quarter, type: string, default: FY27-Q1}
  all_regions:
    request: {spec_version: "0.3", kind: query, query: {measures: [orders.net_revenue], attributes: [customers.region], limit: 100}}
```

Exactly one of `sql` and `request`. `parameters` are the `parameter_definitions` of the request (`for`, `type`, `many`, `default`, `required`, `empty`; see query.md, "Parameters"); a positional `?` marker is named `1`, `2`, .... A saved query is called by selecting from it, and nothing else is allowed in the statement: `SELECT * FROM revenue_by_region`, `SELECT * FROM revenue_by_region('FY27-Q1')` (positional: the parameters in the order written), `SELECT * FROM revenue_by_region(quarter := 'FY27-Q1')`, or with a marker the statement binds (`revenue_by_region(:q)`). A prepared call reports its markers as the parameter schema, typed from the saved query's definitions. The model's description lists each saved query as a view with its `call` form. A saved query that does not translate is rejected when the catalog loads (`query_invalid`, naming it). Because they are part of the catalog, saved queries are published, versioned and rolled back with the model (J12, J13).

## Validation codes

Catalog validation runs when the catalog is loaded, before any query. **Implemented** codes (negative tests in `CatalogTests`):

| Code | Rejects |
| --- | --- |
| `catalog_schema_invalid` | A document that fails the catalog JSON Schema |
| `duplicate_member` | Two members with the same logical name |
| `unknown_table`, `unknown_column` | A reference to a table or column that does not exist |
| `unknown_source` | A table, binding or measure naming a source that is not in `sources` |
| `relationship_not_many_to_one` | `to_columns` that are not a key of `to` |
| `unknown_timezone` | A catalog `timezone` or source `datetime_zone` that isn't an IANA name |
| `date_link_not_datetime`, `date_link_unknown_attribute` | A `date` link on something that isn't a datetime, or to an attribute that isn't a date |
| `datetime_unsupported_source` | A datetime attribute bound to an SSAS model |
| `binding_type_mismatch` | An attribute whose type differs from its bound column |
| `measure_source_missing` | A measure with no source and no `compute` |
| `compute_cycle` | A derived measure that refers to itself |
| `unknown_rollup_attribute` | A rollup rule naming something that is not an attribute |
| `rollup_first_last_not_time` | `first` or `last` on a non-time attribute |
| `many_to_one_violated` | A hierarchy finding, not a load error: a child member with more than one parent (`CheckHierarchies`) |

## Loading and validation

`CatalogLoader` reads the YAML (typing plain scalars so it can be checked as JSON), validates it against
`catalog.schema.json`, checks it as a model, and builds the resolved model the emitters use: a fact table per source,
the star joins from the relationships' columns, and generated aliases (`f` for the fact, then a letter from each joined
table's name). An invalid catalog throws `CatalogException` with these codes (negative tests in `CatalogTests`):

| Code | Rejects |
| --- | --- |
| `catalog_schema_invalid` | A document that fails `catalog.schema.json` |
| `duplicate_member` | A table declared twice; a name that is both an attribute and a measure |
| `unknown_source`, `unknown_table`, `unknown_column` | A reference that doesn't resolve |
| `relationship_not_many_to_one` | `to_columns` that aren't a primary or unique key of `to`, or columns of different lengths |
| `binding_type_mismatch` | An attribute whose type differs from its bound column's datatype |
| `column_expression_missing`, `column_expression_unsupported` | A used column with no ANSI_SQL expression, or one that isn't a plain column (`UPPER(region)`) |
| `measure_table_source_mismatch` | A measure over a table that belongs to another source |
| `format_currency_missing` | A measure with the currency style and no `currency` |
| `unknown_role` | A measure's `roles` names a role its dimension doesn't have, or a dimension's `default_role` isn't one of its roles |
| `role_name_collision` | A role named like a dimension (a role `date` next to `date.*`), or a derived `role.attribute` name that is already a dimension |
| `ambiguous_relationship` | Two relationships from one fact table to the same table without roles; a dimension on such a table with no `default_role` |
| `unreachable_attribute` | An attribute bound to a table that no fact of the source reaches by many-to-one relationships |
| `unknown_rollup_attribute` | A rollup rule naming something that isn't an attribute |
| `rollup_first_last_not_time` | `first` or `last` on an attribute no bound column marks `is_time` |
| `hierarchy_unknown_level` | A hierarchy level that isn't an attribute |
| `segment_unknown_member` | A segment filtering on something that isn't an attribute |
| `segment_relative_period` | A segment using `last_complete`, `current` or `period_to_date`, which are relative to today: a request says them |
| `compute_unknown_measure`, `compute_cycle`, `compute_arity` | A derived measure reading a measure that doesn't exist, itself (directly or through others), or the wrong number of operands |

The many-to-one check needs the data, so it is `CheckHierarchies()`, not a load-time code.

## Export to Ossie

**Implemented** (`CatalogExport.ToOssie`, tested against Ossie's own JSON Schema, kept in `spec/third_party/ossie/`).`x_source` splits tables into one document per source, written as Ossie datasets with fields (`is_time` goes back to `dimension: {is_time}`). Structured `measures` become dataset-scoped Ossie
`metrics` with an `ANSI_SQL` expression where the measure can be written over its own table's columns (`SUM(net_amount)`,
`COUNT(DISTINCT order_id)`, `SUM(CASE WHEN status = 'Completed' THEN net_amount END)`), and a `DAX` one where a native
name is given. A measure whose filters need another table is listed in the extension as not exported. Everything
QIS-specific (logical dimensions and their attributes, rollup and spread semantics, hierarchies) goes into one `custom_extensions` entry as a JSON
string, with only that source's bindings. Ossie tools see only the physical model.

## Import from Ossie

**Implemented** (`CatalogImport.FromOssie`): the Ossie documents an export wrote are read back into a catalog: datasets and relationships from the Ossie
parts, everything else from each document's extension, with a dimension's attributes shared by several sources put back together from their per-source bindings. A
test checks that exporting, writing the documents as JSON text and importing them gives back the original catalog, property for property, and that
the result loads. `FromOssie` rejects a document without the extension, because it has no logical layer to read back.

### A plain Ossie model

**Implemented** (`CatalogImport.FromPlainOssie(document, source, kind)`, `PlainOssieTests`). A model written elsewhere has no logical layer, so the
import infers one and says what it left out. It is lossy by nature, and what it makes is a starting point for someone to review:

- Datasets and relationships come across as tables and relationships, on one source the caller names. A table that is only on the many side of relationships is a fact table.
- A field becomes an attribute if it is marked `dimension`, or is text, a date or a boolean, and is not a key (primary, unique, or a join column). It is
  named `table.column` (the table, without a `dim_` or `fact_` prefix, becomes a dimension and the field one of its attributes). Where a fact reaches one dataset by several relationships, each relationship becomes a role
  of that dimension, which reads the first by default (`default_role`); `ordered.column` and `shipped.column` read each (a date table joined as `ordered` and `shipped`).
- A dataset metric becomes a measure when its ANSI SQL is one aggregate over one of the dataset's fields (`SUM`, `AVG`, `MIN`, `MAX`, `COUNT`,
  `COUNT(DISTINCT)`); its DAX expression is kept as the native text. Rollup follows the aggregate (sum and count add, min and max repeat, the rest are
  recomputed at the source), spread is `blank`, and `status` is `inferred`, never `governed`.
- Everything else is returned in `Notes` and not imported: metrics that are a `CASE`, a ratio or span datasets, and model-level metrics.

Nothing the import infers has been checked against the data. Hierarchies, calendars, segments, synonyms and member lists are for the catalog's owner to add.
