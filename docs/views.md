# Dashboards and reports

Plan 32. A document defines its **widgets once** and carries **layouts** over them. Two kinds share one core: a **dashboard** (`dashboard/1`) fits one page, a **report** (`report/1`) scrolls and splits into pages.
A file is YAML or JSON, strict (an unknown key is an error), and every problem is reported with the file, line and column. This page is what a file looks like; the plan has the reasons
([32](plans/32-dashboards-and-reports.md)). Nothing here renders: the renderers, the layout engines and the module that serves documents are later slices.

Where the files live: in an app's source under `views/` (the apps validator dispatches on `kind`) and in a Build project under `dashboards/` and `reports/` (both come with the slices that wire them in).
The loaders are in `DbDataKit.Views` (.NET) and `@dbdatakit/visuals` (TypeScript); the JSON Schemas for editors are in `DbDataKit/web/visuals/schemas`.

## A widget

A widget is `{type, title, encodings, query}` and some options of its type. The **encodings** say what is drawn; the **query** says what to ask. A widget never carries a computation: the Vega-Lite of a chart is a closed
subset (marks `bar line area point rule text`; channels `x y color column row tooltip`) with **no** `data`, `transform`, `aggregate`, `bin`, `timeUnit`, `calculate`, `expr`, `condition`... anywhere. Aggregates, grains and
shares are part of the **query**, run on the server as the viewer.

| Type | Keys | Notes |
| --- | --- | --- |
| `metric` | `encodings: {value, compare}`, `label`, `format` (`catalog full compact percent`), `query` | `compare` is a second measure of the same request |
| `chart` | `mark`, `encodings` (channels, each a field name or the subset's `{field, type, sort, stack, title}`), `query`, `on_select`, `drill` | the subset is the QIS view's own (`vega-lite-subset.schema.json`) |
| `matrix` | `encodings` (a QIS matrix view's own: `rows columns values values_on totals sort column_limit`), `query`, `on_select`, `drill` | the encodings are `view.schema.json`'s definitions, referenced and not copied |
| `table` | `encodings: {columns: [{field, label, format, align, width}], sort}`, `query`, `drill` | a total row is a row of the result |
| `text` | `text` | markdown, **no raw HTML, no image, links only to a path on the host** (`/docs/x`); `{{params.name}}` is substituted as text |
| `image` | `hash: sha256:<64 hex>`, `media_type` (`image/png image/jpeg image/webp`), `alt`, `fit` | held in the definition's artifacts by hash; **no URL, no SVG** |
| `control` | `parameter`, `control` (`select multiselect date daterange number toggle text`), `label`, `values: {attribute, limit}` | edits one parameter; no query |
| `embed` | | **reserved**: refused until DBR19 |

### The query

* **Derived** (the default): `query: {model, filters, segments, order, limit, totals, timezone, params}`. The request is made from the encodings (every encoded measure goes to `measures`, every other field to
  `attributes`), so what is drawn and what is asked cannot drift. (`DeriveRequest` is DBR03; until then the file only holds the parts a person chooses.)
* **Explicit**: `mode: explicit` and one form: `semantic: {model, request}`, `semantic: {model, sql}`, `saved: name` (`params`), `store: {model, entity, columns, filter, sort, limit}` or `ref: result.name`.
  The encodings then do not drive the request, so **every encoded field must be an output** of the query: a name in `measures` or `attributes` or an `as` alias; for a saved query, a store read and a ref the file
  lists them (`outputs`, `columns`) because it cannot see them. Semantic SQL has no outputs the file can read: that check is the engine's, at publish.
* A `{param: name}` marker in a request names a document parameter, or the one the query's `params` maps it to (`params: {q: quarter}`: the request's `q` is supplied by the document's `quarter`).

`on_select: {set: region, from: customers.region}` sets a parameter as a control would. `drill: {to: {report: name}, with: {region: $customers.region, quarter: $params.quarter}}` leads to another document;
`$params.x` must be declared, and when the loader is given the target's parameters it checks that every required one is given.

### Diagnostics of widgets

| Code | Meaning |
| --- | --- |
| `DBD-104` / `DBD-105` / `DBD-106` | unknown key, missing key, a value of the wrong shape (shared with the other formats) |
| `DBD-107` | a widget type that does not exist |
| `DBD-VIEW-002` | text with raw HTML, an image, or a link that leaves the host |
| `DBD-VIEW-003` | an image by URL, an SVG, or anything but a raster held by hash |
| `DBD-VIEW-004` | a chart outside the subset: data, a transform, an aggregate, a bin, an expression, a mark or type that is not listed |
| `DBD-VIEW-005` | an encoded field that is not an output of the explicit query |
| `DBD-VIEW-006` | the reserved `embed` widget |
| `DBD-VIEW-007` | a query that is not one of the forms, or a mode that does not fit it |
| `DBD-VIEW-008` | a parameter that is not declared (a marker, a `params` entry, a control, `on_select`, a drill, `{{params.x}}`) |
| `DBD-VIEW-037` | a drill that does not give a required parameter of its target |

`ViewsLoader.LoadWidgets` (.NET) and `validateWidgets` (TypeScript) check a widgets map alone, as an assistant's answer, a portal block or a pin holds it. The shared documents and the expected code and path of each
refusal are in `DbDataKit/conformance/views/widgets`; both suites run them.

