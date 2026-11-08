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
  `attributes`), so what is drawn and what is asked cannot drift ([Deriving the request](#deriving-the-request)).
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
| `DBD-VIEW-040` to `-043` | the derivation and the catalog check, [below](#deriving-the-request) (`-040` nothing to derive, `-041` no measure, `-042` totals twice or with nothing to roll up, `-043` a model the document does not depend on) |

`ViewsLoader.LoadWidgets` (.NET) and `validateWidgets` (TypeScript) check a widgets map alone, as an assistant's answer, a portal block or a pin holds it. The shared documents and the expected code and path of each
refusal are in `DbDataKit/conformance/views/widgets`; both suites run them.

## Deriving the request

`DeriveRequest.Derive(widget, roles)` (.NET, `DbDataKit.Views`) and `deriveRequest(widget, roles)` (TypeScript, `@dbdatakit/visuals`, for the editor's field wells) turn a **derived** widget into the semantic request of
[`docs/semantic/query.md`](semantic/query.md): the envelope `{spec_version: "0.3", kind: "query", query: {...}}`. Both are pure and give the same request, key for key (`DbDataKit/conformance/views/derive`).
`roles` is all they need of the catalog: which names are measures and which are attributes (a dimension's attribute, a calendar level, a datetime bucket such as `orders.ordered_at_month`, so a grain is just the
attribute that has it).

| Part of the request | Where it comes from |
| --- | --- |
| `measures` | a metric's `value` and `compare`; a matrix's `values`; a chart's channel or a table's column that the catalog says is a measure; a table's `sort` field if the table does not show it |
| `attributes` | a matrix's `rows` then `columns`; every other chart channel and table column; each field **once**, in a fixed order (chart: `x y color column row tooltip`; the order the file wrote the channels in does not matter) |
| `filters`, `segments`, `timezone` | the query's, as written; `{param: name}` markers stay markers |
| `order` | the query's `order` when it has one, else a table's `sort` (`{by, dir}` becomes `[by, dir]`, `asc` by default). A chart channel's `sort` and a matrix's `sort` and `column_limit` are the view's and add nothing |
| `limit` | the query's, else the type's default: metric 1, chart 500, table 100, matrix 10,000; never above 10,000 |
| `totals` | a matrix: `totals.rows` is a total row (the row fields rolled up), `totals.columns` a total column, both is `{subtotal_by: rows + columns, grand_total, cube}`; a sort of an axis by a measure asks for the other axis's total too (the view's `sort_needs_totals`). A table or chart with `totals: {grand_total: true}` and no `subtotal_by` rolls up every attribute it shows |

The result also lists the **parameter bindings** the request leaves open (`{request: "q", document: "quarter"}`: the request's marker `q` is supplied by the document's `quarter`, as the query's `params` maps it),
which the resolution of DBR04 binds with the viewer's values, and **where in the document** each place of the request comes from (`DocumentPath("$.query.measures[1]")` is `widgets.by_region.encodings.y`), so
a refusal of the engine lands on the field or the filter the person wrote.

**Refusals** (nothing is returned, every one has the file, line and path):

| Code | Meaning |
| --- | --- |
| `unknown_member` | an encoded field the catalog has no member of (the engine's code and words: `customers.regon is not a catalog member.`) |
| `wrong_member_kind` | a dimension in a measure well (`customers.region is a attribute, not a measure.`) or a measure in a matrix axis, in the engine's words |
| `DBD-VIEW-040` | nothing to derive: a widget with no query (`text`, `image`, `control`) or an explicit query (its encodings do not drive it) |
| `DBD-VIEW-041` | the encodings name no measure, and a request needs one (a table of attributes only is a `store` read, written out with `mode: explicit`) |
| `DBD-VIEW-042` | totals asked for twice (the matrix's encodings and the query), or a total row or column with no field to roll up |

## The catalog check

`CatalogCheck.Check(widgets, file, catalogs)` is the **second level** of the binding check, made at publish and by `app validate` against the model version the document depends on (the first, that an explicit
query outputs every field it encodes, is the loader's `DBD-VIEW-005`). It makes every widget's request (a derived query by `DeriveRequest`, a written `semantic.request` as it is) and sends it through
**the engine's validator** (`IRequestValidator`; the semantic library's `Validator.ValidateRequest` behind a small adapter, so this library does not carry the engine). What comes back is a diagnostic with
**the engine's code** (`unknown_member`, `wrong_member_kind`, `order_member_missing`, `invalid_operator`...), the document's file, line and column and the path of the field, filter or order entry
(`widgets.sales.query.filters[0].attribute`). A model the document does not depend on is `DBD-VIEW-043`. A written semantic SQL, a saved query, a store read and a ref are not looked into: the engine checks them when they run.
The adapter must resolve the request's parameters before it validates, as the engine does before it runs (the validator checks a literal request; the viewer's values are checked when the widget runs).
The engine's JSON Schema check reports every branch of every `oneOf` it tried; the check keeps the first `schema_invalid` and says how many more there were.

The **filter operators** are the engine's: `equals`, `not_equals`, `contains`, `not_contains`, `starts_with`, `gt`, `gte`, `lt`, `lte`, `set`, `not_set`, `between` and the relative periods. A list of values is
`equals` with a list (`values: {param: region}` for a `many` parameter); there is no `in`.

## A document: the shared core and the two kinds

```yaml
schemaVersion: 1
kind: dashboard
title: Sales today
parameters:
  - {name: region, type: string, for: customers.region, many: true, default: []}
refresh: {every: 5m}
widgets:
  regions: {type: control, parameter: region, control: multiselect}
  revenue:
    type: metric
    label: Revenue
    encodings: {value: orders.net_revenue, compare: orders.net_revenue_prev}
    query:
      model: sales
      filters: [{attribute: customers.region, operator: equals, values: {param: region}}]
  by_region:
    type: chart
    mark: bar
    encodings: {x: customers.region, y: orders.net_revenue, color: products.category}
    query: {model: sales, order: [[orders.net_revenue, desc]], limit: 20}
    on_select: {set: region, from: customers.region}
layouts:
  - name: desk
    type: reflow
    default: true
    grid: {columns: 12, rows: 8}
    tiles:
      - {widget: regions, x: 0, y: 0, w: 4, h: 1}
      - {widget: by_region, x: 0, y: 1, w: 8, h: 4, min: {w: 320, h: 200}}
      - {widget: revenue, x: 8, y: 1, w: 4, h: 2}
  - name: wall
    type: canvas
    when: {width: {min: 1700}, medium: screen}
    canvas: {width: 1920, height: 1080}
    overflow: scale
    tiles:
      - {widget: revenue, x: 40, y: 40, w: 400, h: 180}
      - {widget: by_region, x: 40, y: 260, w: 1200, h: 700}
  - name: print
    type: flow
    when: {medium: [print, export]}
    page: {size: A4, orientation: portrait, margins: {top: 20, right: 15, bottom: 20, left: 15}}
    header: {left: "{title}"}
    footer: {center: "Page {page} of {pages}"}
    sections:
      - title: Summary
        blocks:
          - {widget: revenue}
          - {widget: by_region, height: 80}
```

* `schemaVersion: 1`, `kind: dashboard | report`, `title`, `parameters` (J25's definitions: `name type for many required default label`; types `string integer number boolean`), `theme: {font: sans | serif | mono}`,
  `widgets` (a map of id to widget) and `layouts` (a list).
* **Layouts** place widgets by id and never change one. A layout has a unique `name`, a `type` (`reflow`, `canvas`, `flow`) and the placement data of its type, an optional `when` selector and `default: true`.
  Exactly one layout is the default and **the default places every widget**; any other layout may omit widgets (an omitted widget is not queried when that layout is shown) except the control of a `required`
  parameter that has no default. A widget that **no** layout places is refused.
* A layout restyles a widget only through `widget_options: {widget_id: {density: compact | comfortable, show_title, legend: show | hide}}` and the placement hints of its type. It cannot change a widget's type,
  encodings, query, formats or parameters (`DBD-VIEW-032`).

| Layout | Placement data |
| --- | --- |
| `reflow` | `grid: {columns, rows}` and `tiles: [{widget, x, y, w, h, min: {w, h}}]` in cells; tiles stay inside the grid and do not overlap |
| `canvas` | `canvas: {width, height, unit: px \| mm \| in}` (320 to 8000 px a side, 96 px per inch), `overflow: scale \| scroll`, `min_scale` (default 0.5), `max_scale` (default 4; neither with `scroll`), `tiles: [{widget, x, y, w, h, layer, min}]` in the canvas's unit; tiles stay inside the canvas, overlap only on different `layer`s, and are at least their type's minimum **at scale 1** (metric 160 x 88, text 160 x 56, control 180 x 56, chart 280 x 180, table 320 x 200, matrix 360 x 220 px; a tile's `min`, in px, raises it) |
| `flow` | `page: {size: A3 \| A4 \| A5 \| Letter \| Legal \| {w, h}, orientation, margins}` (mm), `header` and `footer: {left, center, right}` with `{title} {page} {pages} {asof} {viewer} {params.name}`, `cover: {parameters}`, `sections: [{title, break: before \| after, blocks}]`; a block is `{widget, height, keep, repeat_header}`, `{row: [{widget, span}]}` (spans add up to at most 12) or `{pagebreak: true}` |

**The two kinds** differ only in these rules (one core, one format number: `ViewsFormat.Current`):

| | `dashboard/1` | `report/1` |
| --- | --- | --- |
| default layout | `reflow` or `canvas` | `flow` |
| other layouts | any type (a dashboard may carry a `flow` print layout) | any type (a report may carry fit layouts for the screen) |
| own keys | `refresh: {every: 30s \| 5m \| 1h}`, `kiosk: {layout}` | `strict`, `limits: {pages, rows}`, `cover: {parameters}` (defaults for a flow layout with no cover) |
| the other kind's keys | refused (`DBD-VIEW-035`) | `refresh` is refused: a report is a run |

A report:

```yaml
schemaVersion: 1
kind: report
title: Regional sales
parameters:
  - {name: quarter, type: string, for: date.fiscal_quarter, default: FY27-Q1}
strict: true
limits: {pages: 50}
cover: {parameters: true}
widgets:
  intro: {type: text, text: "Net revenue for **{{params.quarter}}** by region."}
  lines:
    type: table
    title: Orders
    encodings:
      columns:
        - {field: customers.region, label: Region}
        - {field: orders.net_revenue, label: Net revenue, format: full, align: right}
      sort: {by: orders.net_revenue, dir: desc}
    query:
      model: sales
      filters: [{attribute: date.fiscal_quarter, operator: equals, values: [{param: quarter}]}]
      totals: {grand_total: true}
      limit: 1000
layouts:
  - name: print
    type: flow
    default: true
    page: {size: A4, orientation: landscape}
    header: {left: "{title}", right: "{params.quarter}"}
    footer: {center: "Page {page} of {pages}"}
    sections:
      - title: Detail
        blocks:
          - {widget: intro}
          - {widget: lines, repeat_header: true}
  - name: wall
    type: canvas
    when: {width: {min: 1700}, medium: screen}
    canvas: {width: 1920, height: 1080}
    tiles:
      - {widget: intro, x: 40, y: 40, w: 600, h: 80}
      - {widget: lines, x: 40, y: 140, w: 1200, h: 700}
```

`DashboardLoader.Load` and `ReportLoader.Load` are `ViewsLoader.Load` with the kind fixed: a report read as a dashboard is refused saying it is a report (`DBD-VIEW-001`). A `schemaVersion` this program does not
read is refused naming the kind, the file and both numbers (`DBD-110`).

### Diagnostics of documents

| Code | Meaning |
| --- | --- |
| `DBD-110` / `DBD-111` | a newer format; `schemaVersion` that is not a positive whole number |
| `DBD-VIEW-001` | the file is of the other kind |
| `DBD-VIEW-020` | a name used twice (a layout, a parameter, a widget placed twice in a layout) |
| `DBD-VIEW-021` | not exactly one default layout |
| `DBD-VIEW-022` | the default layout does not place a widget that another layout places |
| `DBD-VIEW-023` | a widget no layout places |
| `DBD-VIEW-024` | a key of another layout type (a `canvas` key in a reflow layout; `layer` in a reflow tile) |
| `DBD-VIEW-025` | a layout places, or restyles, a widget the document does not define |
| `DBD-VIEW-026` / `-027` / `-028` | a tile outside its grid or canvas; two tiles that overlap; a canvas tile below its minimum at scale 1 |
| `DBD-VIEW-029` | a canvas out of range, `min_scale` above `max_scale`, scale limits with `overflow: scroll` |
| `DBD-VIEW-030` | a page size or `{token}` that does not exist; a row wider than 12 columns |
| `DBD-VIEW-031` | a layout omits the control of a required parameter that has no default |
| `DBD-VIEW-032` | a layout option that would change a widget |
| `DBD-VIEW-033` | a selector value that is not one of its choices |
| `DBD-VIEW-034` | the default layout is of a type the kind does not allow |
| `DBD-VIEW-035` | a key of the other kind |
| `DBD-VIEW-080` / `-081` | **warnings**: two layouts with equal selectors (the later is never reached by selection); a range with its minimum above its maximum |

The documents are in `DbDataKit/conformance/views` (`good`, `bad`, `warnings` and `cases.json`, which says the code, path and line of each refusal); the .NET (`DbDataKit.Views.Tests`) and TypeScript
(`@dbdatakit/visuals`) suites run the same files. The TypeScript validators report the **path** of a problem (`layouts[1].tiles[0].x`), not a line: `JSON.parse` has no positions. The .NET loaders give both.
What needs the catalog (an encoded field the model does not have, an invalid request) is `CatalogCheck`, at publish, through the engine's validator, not the loaders.

### Format and freeze

`dashboard/1` and `report/1` are **open** (unreleased) and share **one number**, `ViewsFormat.Current`: a new widget type, query form, layout type or selector condition is `/2` of both kinds in one change, and a document is
written in the lowest number that can say it. `schemas/views-format.lock.json` records the number and the hashes of `core`, `dashboard` and `report`; a test fails when a schema changes without the lock (while the format
is open the lock is edited with the schema; once frozen a change is the next number of both kinds).

## Choosing a layout

`LayoutSelector.Select(document, context)` (.NET) and `selectLayout(document, context)` (TypeScript) are pure functions with the same goldens (`conformance/views/select-layout`). The context is the container's
`width` and `height` in CSS pixels (absent for an export, which has no viewport), the `medium` (`screen`, `print`, `export`; `screen` by default) and, optionally, a layout **named** by a URL (`?layout=print`), an export
option or a subscription.

1. A **named** layout wins; an unknown name is refused naming the layouts the document has.
2. Otherwise the layouts whose `when` **matches** are the candidates. The conditions are ANDed and an omitted one matches anything: `width` and `height` (`{min, max}`, inclusive), `orientation` (`portrait` when the height
   is above the width, else `landscape`), `aspect` (width divided by height) and `medium` (one, or a list: a layout meant for print and for export lists both). A condition on a dimension the context lacks does not match.
3. The **most specific** candidate wins (the most conditions); a tie goes to **document order**.
4. With no candidate, the layout with `default: true`. A layout with no `when` is never a candidate, even the default: it is what is left.

## Showing a document

The renderers are in `@dbdatakit/visuals` (`DbDataKit/web/visuals`, its README has the calls). A document's widgets are resolved **through a `ViewsClient`** (`open`, `resolve`, `validate`, `save`, `preview`): the console runs
on `fixtureClient` (the sample documents of the retail template, `DbDataBuild/templates/retail/dashboards` and `reports`, with recorded results in `DbDataKit/conformance/views/widget-results`) until the `views` module
answers, and `moduleClient` is the same interface over its REST.

**The reflow layout fills its container.** `layoutReflow` lays the grid out in whole pixels so the cells fill the box with no gap and no overlap; a tile is never drawn below the smallest box of its type
(`metric` 160 x 88, `text` 160 x 56, `control` 180 x 56, `chart` 280 x 180, `table` 320 x 200, `matrix` 360 x 220; a tile's `min` may raise it). If any tile would be below its minimum the layout is the **compact stack**
(one column in document order, the page scrolls: the one place a reflow dashboard scrolls). Each tile picks a **density** (`xs`, `s`, `m`, `l`) from its box: a metric drops its label, then its comparison; a chart its
legend, then its axis titles, then thins its ticks; a table and a matrix show the rows that fit and say "12 of 40 rows" with an action that maximizes the tile (where the widget scrolls). `widget_options: {density}` pins it.

**Text and images.** Text is markdown without raw HTML, images or links off the host, `{{params.name}}` substituted as text; an image is drawn only from the `data:` URI of its raster, at most 1 MiB, never an SVG.
