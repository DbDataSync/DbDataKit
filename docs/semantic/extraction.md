# Extraction

**Implemented** (`Qis.Engine.Extraction.Extractor`, schema `../spec/schemas/extraction_spec.schema.json`, fixtures in `../extraction-fixtures`). The extraction spec turns a
spreadsheet into clean tables. The agent writes a spec saying **what** to extract, never how, and an engine-neutral executor runs it. Binding the columns to catalog
attributes is not part of this spec; that is ad hoc mapping ([combine.md](combine.md#registering-ad-hoc-tables)). Each extracted table is a `table` document, so it registers as it is.

## Workflow

1. **Fetch.** Get the file (SharePoint through Microsoft Graph, or an upload). **Not implemented**: the executor takes a stream; fetching and change notifications belong to the host.
2. **Fingerprint check.** If a saved spec matches the file (`Extractor.Matches`: library and a `*`/`?` name pattern), `fingerprint` is checked: the sheets it must have and anchor cells that must
   `equal` or `match`. A failure stops the run and says what moved ("`Region` is now at C4: 1 column(s) were inserted before it"); the agent then re-inspects.
3. **Inspect.** `Extractor.Inspect` returns a sheet map for the agent: each sheet's non-empty cells (reference, text, number format, bold, filled), its merged regions, and sampling for a big
   sheet (the top 40 rows, the bottom 10, and every row a merged region touches).
4. **Specify.** The agent writes or revises the spec from the map.
5. **Extract and verify.** `Extractor.Extract(spec, workbook, context)` runs it and the spec's checks.
6. **Describe.** Each table gets a core descriptor: typed fields with `x_kind: attribute` (until mapping binds them), a profile per field (`x_profile`: nulls, distinct count, up to five samples),
   `x_row_count`, and provenance (`spec: extraction`, the spec id, the file name and version if the caller gave them).

## The spec

```json
{"spec_version": "0.3", "id": "regional_forecast",
 "match": {"library": "Finance/Forecasts", "name_pattern": "Forecast_*_FY*.xlsx"},
 "fingerprint": {"sheets": ["Summary", "Detail"],
                 "cells": [{"sheet": "Detail", "ref": "B4", "equals": "Region"}, {"sheet": "Detail", "ref": "D4", "matches": "^(Jul|July)"}]},
 "tables": [{"name": "forecast", "sheet": "Detail",
   "header": {"rows": [3, 4], "join": " "},
   "data": {"start": "B5", "end": {"until_blank_in": "C"}, "columns": "B:F"},
   "skip_rows": [{"column": "C", "matches": "(?i)^(sub)?total"}, {"blank_in": "D:F"}],
   "fill_down": ["B"],
   "unpivot": {"columns": "D:F", "name_as": "month", "value_as": "amount"},
   "types": {"month": "date", "amount": "number"},
   "checks": [{"type": "row_count", "min": 1},
              {"type": "total_matches", "column": "amount", "tolerance": 0.01,
               "against": {"sheet": "Detail", "find_row": {"column": "C", "equals": "Grand Total"}, "columns": "D:F"}}]}]}
```

| Field | Meaning |
| --- | --- |
| `match`, `fingerprint` | Which files the saved spec applies to; what must still hold before it is applied |
| `header.rows`, `join` | One row, or several combined into one name per column with `join`; merged cells are spread across their span, so a merged year over three months names all three. Names become snake_case field names |
| `data` | `start` cell, an `end` (a cell, or `until_blank_in` a column, which stops at the first blank), and the `columns` span |
| `skip_rows` | Rules for subtotal, blank and note rows: a column that `matches` a regex, or `blank_in` a span. Rules only, never row numbers, so they survive added rows |
| `fill_down` | Columns (by letter) whose value applies to the rows below until the next one, after skipped rows are dropped |
| `unpivot` | The month-per-column layout becomes rows: a column holding each header's text (`name_as`) and one holding the cell (`value_as`) |
| `types` | Field name to `string`, `integer`, `number`, `boolean`, `date` or `datetime`. Every cell is read as text first; a blank is null; `1,200.50`, `$80` and `(80)` read as numbers (accounting negative); dates take ISO, `Jul 2025`, `2025 Jul`, `Jul-25`, `7/1/2026` and real date cells. A value that doesn't convert is `type_conversion_failed`, naming the cells |
| `checks` | `row_count` (`min`, `max`, or `within_percent_of_previous`), `not_null` and `unique` on fields, and `total_matches` |

A sheet can hold several `tables` (two blocks on one report page become two tables, fixture `12`).

## Verification

`total_matches` compares the extracted sum with the total the sheet itself contains, found by a row rule (the row whose column equals "Grand Total"). It is the strongest automatic test of a report-style
extraction: a wrong range, a missing subtotal rule or a shifted column almost always breaks it. Fixture `03` leaves the subtotal rule out and the check fails. A failed check blocks the output and
returns `total_mismatch`, `row_count_failed`, `not_null_failed`, `unique_failed` or `total_row_not_found` to the agent, which can revise the spec at most twice. A failed table emits nothing: the result is
`complete` with its tables, or `failed` with errors and no tables.

## Reuse and drift

A saved spec follows the core scopes and `name@vN` versioning ([core.md](core.md#scopes)); a fingerprint failure means the layout changed, and a revised spec is saved as a new version, never over the old one.
The executor does the check and the hint; **saving, versioning and review of shared specs are not implemented** (the same gap as scope enforcement for tables).

## Execution

The executor is all C#: ClosedXML reads and flattens (merged regions, multi-row headers, fill-down), and typing, unpivoting and the checks are done in the same pass, which is one of the shapes the design
allows. ClosedXML is MIT-licensed. The spec doesn't depend on it, so another executor can replace it without touching saved specs.

**Not implemented:** the optional Frictionless compatibility (`x_frictionless`, Table Dialect names, a Data Package export), reading `.xls` or PDF, and Excel serial dates stored as plain numbers (a cell
without a date format is a number).

## Testing

`extraction-fixtures/*.json` hold a workbook written as cells, merges and date cells (built with ClosedXML in the test), a spec, and what it must give: tables with rows and descriptors, or the errors.
The set covers a clean typed table, a report layout (multi-row header, fill-down, subtotals, unpivot, grand-total check), drift (an inserted column fails the fingerprint, never a silent wrong extraction),
mistakes the checks must catch, and two blocks on one sheet. `ExtractionTests` also checks name patterns, the sheet map and its sampling, and that an extracted table registers.
