# DbDataKit.Quality

The check model of the suite (plan 23, slice FQ01): what a data quality check is, how it is written in a definition and how it is read. **Nothing here evaluates a check**; the evaluator is FQ07, the results are the state module `quality` (`DbDataKit.State`, FQ06).

## The model (`check/1`)

A check is `id`, `kind`, the dataset (or entity) it is about, the columns, the kind's parameters, `severity` and `on_error`.

| Kind | Takes | Passes when |
| --- | --- | --- |
| `not_null` | `column` or `columns` | no value of the columns is null |
| `unique` | `column` or `columns` | no combination of the columns' values occurs twice |
| `accepted_values` | `column`, `values` | every non-null value is one of `values` |
| `range` | `column`, `min` and/or `max` (numbers, or dates `2026-01-31`) | every non-null value is inside the bounds |
| `referential` | `column`/`columns`, `references: { dataset, column/columns }` | every value has a row in the target dataset |
| `freshness` | `max_age` (`90m`, `6h`, `1d`), `column` optional | the newest data is no older than `max_age`; without a column, the last successful load is meant (QF2) |
| `row_count_change` | `max_increase_percent` and/or `max_decrease_percent` | the row count moved less than the limits since the previous successful evaluation (QF3) |

- `severity`: `warn` (default) tells the owner; `block` stops the dependents of the dataset, never its own writers (D7).
- `on_error`: what a check that cannot run does (QF10). Its result is `error`; `alert` (default) alerts and does not block, `block` blocks all the same.
- `dataset` is the **lineage identity** of the dataset or entity (`LineageIdentity.Dataset(...).ToString()`, `LineageIdentity.SuiteObject(instance, "entity", name)`), so a result, a lineage node and a block line up (D3). Inside a definition that cannot know its server's id (a model file, a catalog) it is the name the definition uses (an entity, a table); the host turns it into the identity. This library does not reference `DbDataKit.Lineage`: the identity is text of the form `scheme://server/name`.

```yaml
checks:
  - id: orders_customer_present
    kind: not_null
    dataset: orders
    column: customer_id
    severity: block
  - id: orders_fresh
    kind: freshness
    dataset: orders
    max_age: 6h
    on_error: block
  - id: orders_customer_exists
    kind: referential
    dataset: orders
    column: customer_id
    references: { dataset: customers, column: id }
```

## Where it is stored

The section `checks:` is part of **format 2** of the semantic package, of DbDataStore's `model` (`schemaVersion: 2`) and of the serving data source (`schema: connection/2`); a format-1 file that has it is refused naming the file. A standalone file is `schema: check/1` with an optional default `dataset` and `checks`.

## Reading

`CheckSection.Read(node, file, diagnostics)` reads the section from a node of `DbDataKit.Config.Yaml`; `CheckLoader.Load(text, file)` reads a `check/1` file. Reading is strict: an unknown kind or key (with a suggestion for a near one), a range with `min` above `max`, a referential check that names no target, a severity that is not `warn` or `block`, a check id used twice: each is a diagnostic with the file, line and column. `schemas/check.schema.json` (`CheckSchema.Text()`) is the JSON Schema of the file; it cannot say what the loader checks across keys (min above max, duplicate ids, the column count of a referential check).
