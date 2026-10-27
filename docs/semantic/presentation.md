# Presentation

**Implemented** (`Qis.Engine.Presentation`, schema `../spec/schemas/view.schema.json`, fixtures in `../view-fixtures`). A view document says how a
result is displayed, as a matrix or a chart. Results arrive long and tidy, one row per attribute combination, and a view only arranges them.
**A view never computes a measure value, including totals**, so the same result shown two ways shows the same numbers. (The one exception is
the Other column of `top_n_other`, a sum, allowed only for measures that add up.)

```json
{"spec_version": "0.3", "kind": "view", "result": {"ref": "result.9f2c"},
 "views": [{"type": "matrix", "id": "m1", ...}, {"type": "chart", "id": "c1", ...}]}
```

`ViewRenderer.Render(viewDocument, result)` validates the document against the schema and the result's descriptor, then renders every view,
or none and the errors. `result` is a result document or a result set with a descriptor and rows. A field is named by its logical name
(`orders.net_revenue`) or by the name of an alias or transform output (`region_share`); `x_binding` and `x_kind` in the descriptor resolve it.
The output is data, not markup: a grid for a matrix, a Vega-Lite spec with its data for a chart. Drawing it is a renderer's job.

## Matrix

```json
{"type": "matrix", "id": "m1", "rows": ["customers.region"], "columns": ["date.fiscal_quarter"],
 "values": [{"field": "orders.net_revenue", "format": "compact"}, {"field": "region_share", "format": "percent", "empty": "zero", "label": "Share"}],
 "values_on": "columns", "totals": {"rows": true, "columns": true},
 "sort": {"rows": {"by": "orders.net_revenue", "dir": "desc"}, "columns": {"by": "sort_key"}},
 "column_limit": {"max": 50, "overflow": "error"}}
```

| Field | Meaning |
| --- | --- |
| `rows`, `columns` | Attributes on each axis, outermost first. Every attribute of the result must be on one (`attribute_not_placed`), or a cell would hold several rows |
| `values` | Measures in each cell. `empty` is `blank` (default) or `zero` for a combination the result has no row for; `format` is a named preset; `label` names the column |
| `values_on` | `columns` (default): each measure inside each column member. `rows`: each measure under each row member |
| `totals` | `rows: true` shows a total row, `columns: true` a total column; the corner too if both |
| `sort` | Per axis: `sort_key` (the order the result gives the members in), `label`, or a measure; `dir` is `asc` (default) or `desc` |
| `column_limit` | Cap on the column members: `overflow` is `error`, `truncate` (with a `notice`), or `top_n_other` |

**Totals are the result's own rows.** The result's descriptor says which rows are totals: `x_totals.level_field` holds each row's rollup level and
`x_totals.levels` says which attributes each level has rolled up ([combine.md](combine.md#totals)). A *total column* is the row set where every
column attribute is rolled up and the row attributes are kept; a *total row* the other way round; the corner has both rolled up. A total the
result doesn't contain is `totals_missing`: the agent asks for the query again with `totals`. Partial subtotals inside one axis (some of several
row attributes rolled up) aren't shown. Nothing is added: in fixture `02`, a region's distinct count of orders in the total column is 70 where adding its two
quarters would give 84.

**Sorting by a measure** uses each member's value in the axis total (a region's total column), which must be in the result (`sort_needs_totals`).
`sort_key` is the order of first appearance in the result, which is the producer's order (a time attribute arrives in calendar order).

**Column limit.** `error` is `column_limit_exceeded`. `truncate` keeps the first columns and says how many were dropped. `top_n_other` keeps the
largest `max - 1` columns by the first measure and adds up the rest as Other, and only if every measure in the view adds up (`other_not_additive`;
a measure adds up when its descriptor's rollup default is `sum`).

**Formats** are named presets, never free format strings: `full` (`4,200,000`), `compact` (`4.2M`) and `percent` (`38%`). A measure can also carry a
`format` in the catalog (`style`: `number`, `currency` or `percent`; `currency`: an ISO code; `decimals`), which the engine puts in the descriptor as `x_format`
and the view applies, so the person who owns the measure decides how it reads and a view author doesn't repeat it. Under `full` a currency measure reads
`$1,234.50` (symbol, then the catalog's decimals); `compact` keeps the symbol and one decimal (`$4.2M`); a measure whose style is `percent` reads as a percentage
under every preset, because its value is a fraction; `percent` on any other measure turns a fraction into a percentage. Negative numbers read `-$50.00`. A measure with no
format reads as before. Fixture `view-fixtures/25`, and the symbols known so far are USD, EUR, GBP, JPY, CNY, INR and KRW (any other code is written before the number: `CAD 1,200`).
Dimension formats (dates) and per-locale separators are not implemented.

The grid:

```json
{"id": "m1", "type": "matrix", "notice": null,
 "columns": [{"labels": ["FY27-Q1", "orders.net_revenue"], "total": false, "other": false}, ...],
 "rows": [{"labels": ["West"], "total": false, "cells": [{"v": 4200000, "text": "4.2M"}, ...]}, ...]}
```

## Chart

A chart is a restricted subset of Vega-Lite: encodings only. The spec contains no data and nothing that computes a value; the renderer supplies the
data from the result's detail rows (total rows and the level column are left out) and resolves each field to the result's column. `sort: "sort_key"`
becomes the members' order.

- Marks: `bar`, `line`, `area`, `point`, `rule`, `text`. Channels: `x`, `y`, `color`, `column`, `row`, `tooltip`. Encoding properties: `field`, `type`, `sort`,
  `stack`, `title`. Types: `quantitative`, `ordinal`, `nominal`, `temporal`.
- Not allowed (`vega_disallowed`, wherever they appear): `data`, `url`, `transform`, `aggregate`, `bin`, `timeUnit` (they compute or fetch), and also
  `layer`, `facet`, `repeat`, `concat`, `params`, `selection`, and any other mark, channel or property.
- `stack_not_additive`: a stacked chart of a measure that can't be added. A bar or area with a `color` stacks by default in Vega-Lite, so it counts unless
  `"stack": null`. Fixtures `19` and `20`.
- `unknown_field` for a field that isn't in the descriptor, with the closest names.

The agent's defaults by result shape (a line for a time attribute, a sorted bar for one category, a matrix for ranks) are guidance for writing a
view, not something the engine does.

## Errors

| Code | Rejects |
| --- | --- |
| `schema_invalid` | A view document that doesn't match the schema |
| `unknown_field`, `wrong_field_kind` | A field not in the descriptor (with suggestions), or a measure where an attribute goes and the reverse |
| `attribute_not_placed` | An attribute of the result on neither axis |
| `totals_missing` | A total row, column or corner the result doesn't contain |
| `sort_needs_totals` | A sort by a measure when the result has no total to read the value from |
| `column_limit_exceeded`, `other_not_additive` | Too many columns with `overflow: error`; `top_n_other` on a measure that doesn't add up |
| `vega_disallowed`, `stack_not_additive` | A Vega-Lite property outside the subset; a stack of a non-additive measure |
| `duplicate_view_id` | Two views with one id |

## Testing

`view-fixtures/*.json` hold a hand-written result and descriptor, a view document and what it must render to (a grid read as text, or a chart spec after
data injection) or the errors it must give; `ViewTests` runs them, and also renders a result the engine produced. The results are written by hand so they
contain a layout the tests control and so a renderer that sums cells gets the wrong number. Two tests render results the engine produced, one from a chain (each
region's total column) and one from a cube (total row, total column and corner: [combine.md](combine.md#totals)).

**Totals on both axes** need a result that has both, so the query asks for `totals` with `cube: true` over the two axes' attributes. A chain gives one of them.
