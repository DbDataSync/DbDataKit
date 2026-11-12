# Dashboards and reports

Plan 32. A document defines its **widgets once** and carries **layouts** over them. Two kinds share one core: a **dashboard** (`dashboard/1`) fits one page, a **report** (`report/1`) scrolls and splits into pages.
A file is YAML or JSON, strict (an unknown key is an error), and every problem is reported with the file, line and column. This page is what a file looks like; the plan has the reasons
([32](plans/32-dashboards-and-reports.md)). The resolver (below) turns a layout into rendered widgets; the renderers, the layout engines and the module that serves documents are other slices.

Where the files live: in an app's source under `views/` (the apps validator dispatches on `kind`) and in a Build project under `dashboards/` and `reports/` (both come with the slices that wire them in).
The loaders are in `DbDataKit.Views` (.NET) and `@dbdatakit/visuals` (TypeScript); the JSON Schemas for editors are in `Shared/web/visuals/schemas`.

## A widget

A widget is `{type, title, encodings, query}` and some options of its type. The **encodings** say what is drawn; the **query** says what to ask. A widget never carries a computation: the Vega-Lite of a chart is a closed
subset (marks `bar line area point rule text`; channels `x y color column row tooltip`) with **no** `data`, `transform`, `aggregate`, `bin`, `timeUnit`, `calculate`, `expr`, `condition`... anywhere. Aggregates, grains and
shares are part of the **query**, run on the server as the viewer.

| Type | Keys | Notes |
| --- | --- | --- |
| `metric` | `encodings: {value, compare}`, `label`, `format` (a preset or a mapping, see Formats), `query` | `compare` is a second measure of the same request |
| `chart` | `mark`, `encodings` (channels, each a field name or the subset's `{field, type, sort, stack, title}` and `format`), `query`, `on_select`, `drill`, `on_click` | the subset is the QIS view's own (`vega-lite-subset.schema.json`); `format` is the widget's (see Formats) |
| `matrix` | `encodings` (a QIS matrix view's own: `rows columns values values_on totals sort column_limit`; a `rows` or `columns` entry is a field or `{field, format, sort}`, a `values` entry takes a `format` mapping), `query`, `on_select`, `drill`, `on_click` | the encodings are `view.schema.json`'s definitions, referenced and not copied, except the axes and the values, which also take a format |
| `table` | `encodings: {columns: [{field, label, format (a preset or a mapping), align, width}], sort}`, `query`, `drill`, `on_click` | a total row is a row of the result |
| `text` | `text` | markdown, **no raw HTML, no image, links only to a path on the host** (`/docs/x`); `{{params.name}}` is substituted as text |
| `image` | `hash: sha256:<64 hex>`, `media_type` (`image/png image/jpeg image/webp`), `alt`, `fit` | held in the definition's artifacts by hash; **no URL, no SVG** |
| `control` | `parameter`, `control` (`select multiselect date daterange number toggle text`), `label`, `values: {attribute, limit}` | edits one parameter; no query |
| `custom` | `component` (`<app>/<Export>`), `params` (a declared parameter's value: `$params.name` or a literal; no row field), `links` (the document's links it may open, by name) | code of an app, drawn by the app's own host in its sandboxed frame; what it declares (queries, parameters) is in the app's manifest ([apps.md](apps.md), "Custom views"); a document with one is checked against the manifest by `app validate`; a host that serves the app draws it in a sandboxed frame ([Custom widgets on screen](#custom-widgets-on-screen)); the console's Views page, which serves no app, shows a placeholder |
| `embed` | `url` (an absolute https address, no credentials), `with` (query-string pairs: a name to `$params.name` or a literal), `link_text` | an external report server's report (plan 32 DBR19, plan 26 AP17), **dashboards only**: a report refuses it (`DBD-VIEW-038`). See [An embedded report](#an-embedded-report-dbr19) |

### An embedded report (DBR19)

```yaml
widgets:
  sales_server:
    type: embed
    title: Sales report
    url: https://reports.example.com/Reports/report/Sales?rs:Embed=true
    with: {Region: $params.region, Fiscal: "2026"}       # added to the address's query string; a pair whose parameter has no value is left out
    link_text: Open the sales report                     # the words of the link when the address is not framed
```

The resolver gives `{type: embed, url, origin, framed, link_text}`: the address with the pairs filled in (escaped), its origin (scheme, host and port), and **`framed`, true only when the host approves that origin**: `ResolverOptions.FrameOrigins`
(the Gateway's views module passes the list `views.frameOrigins` of `server.yaml`, read on every request, so a change applies without a restart; a host that sets none shows every embed as a link). `EmbedBlock` draws a `framed` result as an `iframe` with `sandbox="allow-scripts allow-same-origin allow-forms allow-popups"`
and `referrerpolicy="no-referrer"` (the person's own browser signs in to the report server; this page sends it nothing), always with the report's link under the frame, and anything else as a link that opens in a new tab with no opener. An address that is not plain https draws as an error tile.
**A sign-in that fails cannot be seen from the page.** A browser tells a page nothing about a cross-origin frame that was refused or challenged (no event, no status, no content): the frame's `load` event is the only sign. So a frame that has not loaded after `EMBED_FRAME_TIMEOUT_MS` (15 s) is replaced by the link and the line "Cannot show this report here", and a frame that loaded a sign-in failure page looks like a report, which is why the link is always beside it. Windows sign-in
(Kerberos or NTLM) in a frame works only on a domain-joined machine whose browser trusts the report server's zone; off the domain the person uses the link.
Dashboards only: a **report** with an `embed` is refused (`DBD-VIEW-038`), because a report is paged and exported and a frame is neither. The widget has no query, so it is never cached and the editor shows no derived request. An app's own documents frame a report server through the instance's approved `frame` exception (plan 26 AP17: a trusted app whose manifest asks for `egress.frame` and whose approval covers it; its `Content-Security-Policy` `frame-src` lists the approved origins and nothing else, a sandboxed app has none); the console's own policy is the console's. A dashboard of an app's `view-spec/1` file takes `{kind: embed, title, url, link_text}` as well (`framed` is decided from the approval, not from `views.frameOrigins`; the manifest must ask for the address's origin under `egress.frame`, or `app validate` refuses the file naming app, view and origin): [apps.md](apps.md), "A report server in a frame".

### The query

* **Derived** (the default): `query: {model, filters, segments, order, limit, totals, timezone, params}`. The request is made from the encodings (every encoded measure goes to `measures`, every other field to
  `attributes`), so what is drawn and what is asked cannot drift ([Deriving the request](#deriving-the-request)).
* **Explicit**: `mode: explicit` and one form: `semantic: {model, request}`, `semantic: {model, sql}`, `saved: name` (`params`), `store: {model, entity, columns, filter, sort, limit}` or `ref: result.name`.
  The encodings then do not drive the request, so **every encoded field must be an output** of the query: a name in `measures` or `attributes` or an `as` alias; for a saved query, a store read and a ref the file
  lists them (`outputs`, `columns`) because it cannot see them. Semantic SQL has no outputs the file can read: that check is the engine's, at publish.
* A `{param: name}` marker in a request names a document parameter, or the one the query's `params` maps it to (`params: {q: quarter}`: the request's `q` is supplied by the document's `quarter`).

`on_select: {set: region, from: customers.region}` sets a parameter as a control would. `drill: {to: {report: name}, with: {region: $customers.region, quarter: $params.quarter}}` leads to another document;
`$params.x` must be declared, and when the loader is given the target's parameters it checks that every required one is given.

### Formats (plan 32 section 9g, DBR40)

A metric's and a table column's `format` is a **preset** (`catalog`, `full`, `compact`, `percent`: the catalog's own format, thousands separators, K/M/B, a fraction as a percentage) or a **mapping**:

```yaml
format: {style: currency, currency: EUR, decimals: 0}     # style: number | currency | percent | compact; decimals 0 to 10; grouping: false drops the separators
format: {date: month-year}                                # presets: year quarter month month-year date datetime
format: {date: "dd/MM/yyyy HH:mm"}                        # or a pattern: yyyy yy Q MMMM MMM MM dd HH mm ss, other characters literal, 'quoted' text literal
```

A number mapping's `decimals` defaults to the catalog's, a currency's to 2 (0 for JPY and KRW); the minus sign never shows on a value that rounds to zero; rounding is half away from zero on the value as written (its
15 significant digits). A date reads an ISO date or date-time **as written** (no time zone is applied); text that is not a date is shown as it is. A format that cannot be read is refused with the key inside it
(`DBD-106`, for example `` `date` pattern has the unknown token `yyy` ``); a pattern is a closed set of tokens, never a free format string. Month names and separators are the **locale's** (below). **One formatter in two languages**: `ViewFormat` in `DbDataKit.Views` writes a cell's text on the server and `formatValue` in `@dbdatakit/visuals` is its twin, both run against `Shared/conformance/views/format/cases.json`.

#### Locale (DBR46)

A document may say `locale: de` (one of `en de fr es it pt nl`, a closed table: `Shared/web/visuals/src/views/locales.json`, which `DbDataKit.Views` embeds as the same file and the TypeScript twin imports). The locale sets a number's
**decimal and group separators** (`1.234,5` in `de`, `1 234,5` with a narrow no-break space in `fr`) and the **month names** of a date (`MMMM`, `MMM`, so `März 2026`, `févr. 2026`); the numerals of a numeric pattern (`dd/MM/yyyy`),
the minus sign and the currency symbol's place (before the number) do not change. The locale of a render is the request's (`ResolveAsync(..., locale:)`, an export's or a subscriber's choice), else the document's, else the host's
`ResolverOptions.Locale`, else English; an unknown code in a request reads as English, in a document the loader refuses it with the list. The locale is not part of a cache key (the rows are the same; only their text changes).
The table is checked against a vendored CLDR snapshot by a unit test in each language; what is checked and what a person must still judge is in [`locale-data.md`](locale-data.md).
**Not localised:** a chart's quantitative axis and legend (d3 strings the client draws with English separators), the QIS view of the semantic library, and a currency symbol's place.

#### The catalog's date format and a bare year (DBR47, DBR48)

An **attribute** of the catalog may carry a `format` of its own, `{date: month-year}` (same presets and patterns as above; `docs/semantic/catalog.md`, "Attribute format"). The engine puts it in the result's descriptor as `x_format`, as it
does for a measure, and a widget that gives a date attribute **no** `format` (or says `catalog`) reads it: a matrix axis, a chart's categorical channel and a table column all show `Mar 2026`, and a widget's own `format` wins.
A whole number of four digits (1000 to 9999) is read as a **year** when a date format reads it, so a `year` attribute held as an integer (`2026`) formats as `{date: year}`, `{date: "'FY'yy"}` (`FY26`) or
through the catalog's format; any other number stays a number. Both languages do the same (`ViewFormat.Text`, `ViewFormat.Label`, `formatValue`), with goldens in `cases.json`.

#### Formats on attributes and axes (DBR41)

`format` is read wherever a field is shown. The QIS view (`view 0.3`) and its Vega-Lite subset are **unchanged**: the format belongs to the widget vocabulary and the renderer maps it.

```yaml
encodings:                     # a chart
  x: {field: orders.month, format: {date: MMM}}                       # January to December, in calendar order
  y: {field: orders.net_revenue, format: {style: currency, currency: USD, decimals: 0}}
  tooltip: [{field: orders.month, format: {date: month-year}}, {field: orders.net_revenue, format: compact}]
encodings:                     # a matrix
  rows: [{field: orders.year, format: {date: year}, sort: {by: sort_key, dir: desc}}, {field: orders.month, format: {date: MMM}}]
  columns: [customers.region]
  values: [{field: orders.net_revenue, format: {style: currency, currency: EUR, decimals: 0}}]
```

* A **chart channel** that is categorical (nominal, ordinal, a date) has its members written as the format reads them, into a column of its own (`<field>__<channel>`) in the chart's data, in the order of the raw members
  (the engine's `sort_key` order, the channel's `sort` list, or ascending and descending by value): `MMM` labels the months in calendar order, never alphabetically. A quantitative channel keeps its field and the
  rendered chart carries `formats`, a d3-format string per channel (`$,.0f`, `.0%`, `,.2~f`, `.2~s`) that `ViewFormat.D3Number` makes from the format's own closed vocabulary; the client checks each against one fixed
  pattern before it gives it to Vega-Lite as the axis, legend, header or tooltip format (`applyChartFormats`). **What d3 cannot say:** `$` is the dollar, so another currency draws its digits without a symbol on an axis
  (the cell text of a table and a matrix has it); `compact` uses d3's SI prefixes (`k`, `M`, `G`) where the cell text says `K`, `M`, `B`; d3's minus is the typographic one. An author's expression is still refused (`vega_disallowed`).
* A **matrix axis entry** is a field or `{field, format, sort}`: the format applies to the labels of that level; one level of an axis may carry a `sort`, which is the axis's sort (the same as `sort.rows` or `sort.columns`,
  and writing both is that widget's error). A **matrix value** takes a preset or a mapping; a mapping is applied to the cell's number after the engine has arranged the grid.
* The data of a chart that is exported (its sheet) holds the label columns beside the raw ones.

#### A nested axis (DBR42)

A bar or point chart's `x` may be a **list of levels, outermost first** (at most three), each a field or `{field, format, sort, title}`:

```yaml
encodings:
  x:
    - {field: orders.ordered_at_year, format: {date: year}}
    - {field: orders.ordered_at_month, format: {date: MMM}, sort: {by: orders.net_revenue, dir: desc}}
  y: orders.net_revenue
```

* **Refused** (`DBD-106`, at the key): a **line or an area** mark (a line breaks between the groups: use one temporal field with a grain and a format such as `MMM yyyy`), a **`column`** channel (the nested axis is drawn
  with the facet), more than **three** levels. A list of one level is the plain channel. `sort` is the QIS view's `{by, dir}`: `sort_key`, `label`, or a measure, and a measure only on the **innermost** level (an outer level
  would need its own total: refused in the derivation, `DBD-106` at `x[i].sort.by`).
* **Asking.** `DeriveRequest` asks for every level as an attribute, outermost first, and adds `order` per level from the outermost (`[[year, asc], [month, asc]]`; a level sorted by a measure orders by that measure, which
  is added to `measures` if the chart does not show it), unless the query writes its own `order`. The order of the request never depends on how the rows arrive.
* **Drawing.** `ResultShaper` gives the engine the innermost level as `x` and the outermost as `column` (a facet it writes itself; an author's `facet` stays refused), writes each level's members as its format reads them, and
  orders them: ascending by value unless the level sorts by `sort_key` (the rows' own order), `label` or a measure (the rows' order, which the request made); `dir: desc` reverses. With three levels the two outer ones are one column
  of labels joined by ` / `. The rendered chart says `nested_axis: true` and the client (`applyNestedAxis`) draws the facet with zero spacing, the headers below the inner labels, a thin edge between groups and **an independent
  `x` scale per group**, so each year shows its own months. Member combinations over **200** are cut (the first ones in the request's order are kept) and the chart's `notice` says `Showing the first 200 of N member combinations on the axis.`
* **Limits, stated:** an inner level sorted by a measure has **one** order for the whole axis (the first group's), not one per group; a third level is joined into the group label, not drawn as a third header row; the
  PDF is drawn from the same rendered chart by the same client code, so it carries the group headers (not checked here: the exporter's tests need Chromium and fonts).

### Filtered and repeated measures (DBR50)

A measure may be written as an **object** where a name was: `{field, alias, label, filters, format}`. It is how the **same measure is put in a visual several times** with different filters (two series, two matrix
value columns, two metrics, two table columns): in a metric's `value` and `compare`, a chart channel (`y`, `color`, `tooltip`...; the channel object's `title` names it, as before), a matrix `values` entry and a table column.
A chart's `y` may be a **list** (one to eight entries, one series each); the answer is folded into a `series` and a `value` column and drawn as one chart whose color is the series (the series keep the list's order and are named by the entry's `title`, else its alias or field; a `color` beside several measures is refused, `DBD-106`).

```yaml
encodings:
  columns:
    - customers.region
    - {field: orders.net_revenue, alias: open_revenue, label: Open, filters: [{attribute: orders.status, operator: equals, values: [Open, Pending]}]}
    - {field: orders.net_revenue, alias: completed_revenue, label: Completed, filters: [{attribute: orders.status, operator: equals, values: [{param: status}]}]}
    - {field: orders.net_revenue, label: All}
```

* `DeriveRequest` writes the request's `measures` entry in the engine's own form, `{measure, as, filters}` (the widget's `alias` is the request's `as`); an entry with neither is the plain name. The **output** of an entry is its
  alias, so a matrix's sort, a table's `sort.by`, `order` and an explicit query's outputs name the **alias** (`DBD-VIEW-005` says `open_revenue` is not an output, not `orders.net_revenue`).
* The checks are the engine's, in its codes and words, at the document's path: `alias_required` (filters with no alias, at `filters`), `duplicate_measure` (one output name twice with different definitions, at the second entry; the
  same entry twice is asked once), `wrong_member_kind` (`alias` or `filters` on an attribute), then the catalog check's `filter_member_not_in_source`, `alias_invalid` (an alias that is a catalog member name) and the rest.
  The loaders refuse an alias that is not a snake_case name (`DBD-106` at `alias`), `filters` that are not a list of mappings (one to fifty), a marker of an undeclared parameter in them (`DBD-VIEW-008`), and `filters` on an
  entry of a **written** query (`DBD-VIEW-007`: the query says its own measures).
* A filter value may be `{param: name}`; a filter on a parameter the viewer left unset drops out of that entry, as it does from the query's filters (the request, and so the cache key, differ only for the entries that use it).

### Attributes in the cell area (DBR51)

A matrix's `values` entry may be an **attribute** where a measure was: it changes with the column headers like a measure does, and shows that attribute's value for each cell.

```yaml
encodings:
  rows: [customers.region]
  columns: [orders.ordered_at_month]
  values:
    - {field: orders.net_revenue, label: Revenue}
    - {attribute: orders.status, display: icon, icons: {Open: circle, Completed: check}, many: mixed, link: set-status, label: Status}
```

| Key | Meaning |
| --- | --- |
| `attribute` | the catalog attribute shown (instead of `field`; an entry is one or the other); `format` reads it as it reads an axis member (a date format on a date attribute) |
| `display` | `text` (default), `icon` (the icon `icons` names for the value; a value the map does not list shows as text) or `badge` |
| `icons` | value to icon, from a **closed vocabulary** of 24 names (`circle dot check cross warning info clock flag star pause play stop lock unlock arrow-up arrow-down arrow-right plus minus pin bell eye user tag`); an icon display needs it and it needs the icon display |
| `many` | what a cell with several values shows: `mixed` (default: the text `Mixed`, a marker, the values in a tooltip), `first` or `last` (in the order of the answer), `count` (the number of distinct values, also the cell's `v`), `list` (comma-joined, five at most, then `…`) |
| `link` | a named link of the document (`links`, [below](#links-one-vocabulary-for-a-click-plan-32-section-9f-dbr35)) that a click on the cell follows; a cell with no text has none |

**The one-row rule.** An attribute is well defined only where a cell holds one row. `DeriveRequest` asks for the attribute **at the cells' grain** (after the axes) and rolls it up **last** in the request's totals, so the answer holds,
for each cell, the **rows that name its values** and the **cell's own row with the attribute rolled up**, whose measures are the engine's: a measure shown beside an attribute is **never added across the attribute's values** by the
view, so a cell with four values still holds one honest number. The shaper reads the values from the detail rows, gives the engine's matrix a synthetic measure in the attribute's place (so the grid, its labels, its totals and its
column limit are the engine's own), and fills each attribute cell: one value is shown as `display` says, several by `many`, none is empty. **A total row or column, and an Other column, leave an attribute cell empty**: there is no one value.
An attribute that is already on an axis adds nothing to the request (it is the same in every row of its cell). Every policy can be said, so there is no refusal for a `many` the cell cannot show; the engine's own refusals of the request (a
cube too wide, an unknown member) land on the entry's `attribute` path.

**Refused** (`DBD-106` at the key unless said): `field` and `attribute` together; `alias` or `filters` on an attribute (they belong to a measure); `display: icon` without `icons`, `icons` without it, an icon outside the vocabulary,
`many` outside the five; `values_on: rows` with an attribute among the values; a `link` the document does not declare (`DBD-VIEW-036`); an attribute entry in a matrix over a **written** query (`DBD-VIEW-007`: the
request cannot be asked at the cells' grain); a matrix of attributes only (`DBD-VIEW-041`, a request needs a measure).

**A table column** takes the same `display`, `icons` and `link` (a table row has one value, so no `many`).

**Rendering.** A rendered cell carries `icon`, `badge`, `mixed`, `values` and `link` beside `text` (`rendered-widgets.schema.json`). The client draws an icon as a glyph of the closed set (the bundled icon font of DBR37 is not
built; the glyphs are text characters every font has, with the cell's text as the accessible name), and a cell that names a link as a button **only when the host follows links**: `DashboardView` takes `onLink({name, widget, row, column?})`;
without it the cell is plain text, which is what print and export draw: **the value prints, the link is dropped** (`print: hidden | normal | link` of the link says more; the PDF annotation of `print: link` is DBR37's, not built).

### Field groups (DBR52)

A **group** is an ordered list of attributes used as **one field**. It is declared once in the document and named where a field of an attribute role goes:

```yaml
groups:
  period:
    fields:
      - orders.ordered_at_year
      - {field: orders.ordered_at_month, format: {date: MMM}}
    label: Period
    template: "{2} {1}"          # the levels' texts: month, then year -> Mar 2026 (the default joins them with a space)
widgets:
  trend: {type: chart, mark: line, encodings: {x: {group: period, title: Period}, y: orders.net_revenue}, query: {model: sales}}
  detail:
    type: matrix
    encodings:
      rows: [customers.region, {group: details, show: always}]
      columns: [{group: period}]
      values: [{field: orders.net_revenue}]
    query: {model: sales}
```

* **A group** has two to four `fields` (outermost first, each a name or `{field, format}`), an optional `label` and `template` (`{1}` to `{n}`). A group cannot hold a group; a field is listed once in it. Declared in both kinds.
* **In a chart** (`x`, `color`, `column`, `row`; not `y` or `tooltip`) `{group: name}` is **one field**: `DeriveRequest` asks for its levels as attributes in order and, for `x`, orders by the levels in turn (calendar order whatever the
  rows' arrival order); the answer's rows get one member key per combination, the member is labelled by the template, and the axis is **one** axis, so a line over a year-and-month group is one continuous line labelled `Mar 2026`.
  A group channel takes a `title` and a `sort` (`ascending`, `descending`, `sort_key`) and no `field`, `format`, `alias`, `filters`, `type` or `stack`.
* **In a matrix** an entry of `rows` or `columns` is `{group: name}` (the same combined field: one header per combination, a total rolls every level up together) or, in `rows` only, `{group: name, show: always}`: the group's attributes
  (at or below the row's grain: an order's number, its customer) shown **as extra header columns of every row**, the request asking for them after the row's own fields. `show: drill` (a row that expands on request) is described below.
* **Checks.** `DBD-VIEW-047`: a group the document does not declare. `DBD-VIEW-048`: a field in a group and also in another place of the same widget (an axis, a channel, a tooltip, another group), naming both places. A level that is
  not an attribute of the catalog is the engine's `unknown_member` or `wrong_member_kind`, at the entry's `group` path. A group in a written query (`mode: explicit`) is `DBD-VIEW-007`. The shape (`DBD-106`, `DBD-104`): one level, five
  levels, a template naming `{3}` where the group has two, a field twice, a bad level format, a group in `y`, a `field` beside a `group`, `show` on a column entry or on a plain entry.

### The editor's controls for entries and groups (DBR54)

In the widget editor each entry of a well has **options** (a disclosure under it): a **measure** has its `alias`, a **Repeat this measure** button (a copy follows it with an alias nothing else has, `net_revenue_2`; one measure may be in a well
as often as its aliases differ), its own **filters** (an attribute, an operator the field's type takes, a value or a parameter; the filters need the alias, and the derived request beside the wells shows `{measure, as, filters}` at once),
and a chart channel's `title` or a column's `label`; an **attribute** entry (in a matrix's `values`, offered beside the measures when the field the catalog says is an attribute is put there, or a table column) has its **display**, an
**icon map** (a value and an icon of the closed vocabulary, added and removed one at a time), the **many** policy (a matrix entry), its **link** (a choice among the document's declared links) and a matrix entry's label. Each change is
a `setPath` of that entry, so it is one undo step, and what is wrong is said in the loaders' words as it is typed. The **document settings** hold the **field groups**: a group's levels (each with a date format, up, down, remove, one
to add), its label and template, and the loaders' words about it; a group a widget names is not removed (the editor says which widget). A group is put in a well by the pick list (**Groups**; for the rows of a matrix also **Details of
every row** and **Details of a row that expands**, DBR55). The pure parts (`entryParts.ts`, `wells.ts`) are `@dbdatakit/visuals` and are tested against the goldens: the chart of two series of one measure, the matrix with a status per month and the line over a group built through
the controls are the golden widgets (`entryParts.test.ts`); the screens are `WidgetEditor.tsx` and `GroupEditor.tsx` of `@dbdatakit/views-editor` (`entries.test.tsx`, the story `views-editor-entries.spec.ts`, written and not run).

#### Drill detail groups: a row that expands (DBR55)

`{group: name, show: drill}` in `rows` makes each row **expandable**: the rows are asked for **without** the group's attributes, and a click on a row's toggle reads that member's details.

```yaml
widgets:
  orders:
    type: matrix
    encodings: {rows: [customers.region, {group: who, show: drill}], columns: [orders.status], values: [{field: orders.net_revenue}]}
    query: {model: sales}
    expanded: [[West]]            # what print and export show; a viewer's own choice replaces it on screen
```

* **The request** for a row's details is `DeriveRequest.DeriveExpansion` (and `deriveExpansion`, the same golden `derive/expansions.json`): the widget's request with the drill group asked for like `show: always` (its attributes after the
  row's own fields, the order by their levels), **no totals**, and the row's own fields filtered to its member values (the row's typed `key`). It runs as the viewer, like every request, through the same cache.
* **The rendered row** of an expandable matrix carries `expandable` and `key`; an expanded one carries `expanded` and `children` (the labels after the row's own, and a cell for every column of the matrix, empty where the details have
  none). The resolver reads at most **20** expansions per widget (the rest are not read and `notice` says so); a member that is no longer a row is not expanded; the widget's cache key holds the expansions.
* **Asking.** The module's render request takes `expand`: `{"orders": [["West"], ["East"]]}` (widget id to rows, each the list of the member values of the row's own fields; `DBD-VIEW`-less `400` when it is not that shape). A widget not
  listed shows the rows its document's `expanded` names (matched to the base rows by the text of their values), which is what a print or an export draws. `ResolveOptions.expand` is the client's side, `expandsOnServer` says a source honours it
  (the module does; the fixtures do not, and show no toggle).
* **On screen.** The first label cell of an expandable row is a button with `aria-expanded` (`Expand West` / `Collapse West`, Enter and Space work, it is a button), and the details are rows under it, indented. `DashboardView` keeps the
  viewer's open rows, sends them with every later request of the view and runs only that widget when one is toggled; two open rows keep their own details. `onExpand` of `MatrixGrid` and `WidgetView` is the hook for a host with another
  way to read them.
* **Refused** (`DBD-106`): `show: drill` beside another group in the rows or a group on the columns (the row's key is its plain fields' values); `expanded` without a `show: drill` entry, or a row that does not name a value for each of
  the rows' own fields. A drill group in a written query is `DBD-VIEW-007`, as every group.

### Sorting by a click (DBR45)

A viewer may sort a table or a matrix by clicking a header, **on screen only** (the host sets `sortable` on `DashboardView`; the print and export routes never do): a click sorts ascending, a second click reverses, a third gives the
document's order back; the header carries `aria-sort` and is a button, so the keyboard works. Numbers sort by value, text by code point, an empty cell goes last, a total row stays after the rows. It is never stored: the
document's `sort` is the default and is what print and export use. A table the server cut at its limit is sorted over the rows loaded and says so, unless the source sorts on the server.

**On the server.** The module's render request takes `order`, a viewer's sort by widget id: `{"order": {"top": {"by": "sales.units", "dir": "desc"}}}` (`dir` is `asc` or `desc`). It replaces the table's own `order` for that one run
(a derived or a semantic query; the column must be one the request asks for, else that widget's error entry `DBD-VIEW-056`; another widget type or query form is `DBD-VIEW-052`), is part of the request and so of the cache key, and is never stored.
`moduleClient` sends it (`ResolveOptions.order`, `sortsOnServer: true`); `DashboardView` keeps the viewer's sorts and sends them with every later request of the view, so a parameter change or a refresh keeps them. The fixture source holds recorded results
and sorts the rows loaded. The console's board sets `sortable`.

### Diagnostics of widgets

| Code | Meaning |
| --- | --- |
| `DBD-104` / `DBD-105` / `DBD-106` | unknown key, missing key, a value of the wrong shape (shared with the other formats) |
| `DBD-107` | a widget type that does not exist |
| `DBD-VIEW-002` | text with raw HTML, an image, or a link that leaves the host |
| `DBD-VIEW-003` | an image by URL, an SVG, or anything but a raster held by hash |
| `DBD-VIEW-004` | a chart outside the subset: data, a transform, an aggregate, a bin, an expression, a mark or type that is not listed |
| `DBD-VIEW-005` | an encoded field that is not an output of the explicit query |
| `DBD-VIEW-006` | a widget type the resolver does not run |
| `DBD-VIEW-038` | an `embed` widget in a report |
| `DBD-VIEW-007` | a query that is not one of the forms, or a mode that does not fit it |
| `DBD-VIEW-008` | a parameter that is not declared (a marker, a `params` entry, a control, `on_select`, a drill, `{{params.x}}`) |
| `DBD-VIEW-036` | a widget's `on_click` names a link the document does not declare |
| `DBD-VIEW-037` | a drill or a link that does not give a required parameter of its target |
| `DBD-VIEW-039` | a link to a dashboard, report, view, page or form the app does not have |
| `DBD-VIEW-044` / `-045` | a name in a link's `with`, `new`, `edit` or `lock` that the target lacks; a value of the wrong type for it |
| `DBD-VIEW-046` | `print: link` on a link shown in a dialog or panel (no address of its own) |
| `DBD-VIEW-047` / `-048` | a field group the document does not declare; a field in a group and also in another place of the widget (DBR52) |
| `DBD-VIEW-040` to `-043` | the derivation and the catalog check, [below](#deriving-the-request) (`-040` nothing to derive, `-041` no measure, `-042` totals twice or with nothing to roll up, `-043` a model the document does not depend on) |

`ViewsLoader.LoadWidgets` (.NET) and `validateWidgets` (TypeScript) check a widgets map alone, as an assistant's answer, a portal block or a pin holds it. The shared documents and the expected code and path of each
refusal are in `Shared/conformance/views/widgets`; both suites run them.

## Deriving the request

`DeriveRequest.Derive(widget, roles)` (.NET, `DbDataKit.Views`) and `deriveRequest(widget, roles)` (TypeScript, `@dbdatakit/visuals`, for the editor's field wells) turn a **derived** widget into the semantic request of
[`docs/semantic/query.md`](semantic/query.md): the envelope `{spec_version: "0.3", kind: "query", query: {...}}`. Both are pure and give the same request, key for key (`Shared/conformance/views/derive`).
`roles` is all they need of the catalog: which names are measures and which are attributes (a dimension's attribute, a calendar level, a datetime bucket such as `orders.ordered_at_month`, so a grain is just the
attribute that has it).

| Part of the request | Where it comes from |
| --- | --- |
| `measures` | a metric's `value` and `compare`; a matrix's `values`; a chart's channel or a table's column that the catalog says is a measure; a table's `sort` field if the table does not show it |
| `attributes` | a matrix's `rows` then `columns`; every other chart channel and table column; each field **once**, in a fixed order (chart: `x y color column row tooltip`; the order the file wrote the channels in does not matter) |
| `filters`, `segments`, `timezone` | the query's, as written; `{param: name}` markers stay markers |
| `order` | the query's `order` when it has one, else a table's `sort` (`{by, dir}` becomes `[by, dir]`, `asc` by default), else a nested axis's levels in order (DBR42, above). A chart channel's `sort` and a matrix's `sort` and `column_limit` are the view's and add nothing |
| `limit` | the query's, else the type's default: metric 1, chart 500, table 100, matrix 10,000; never above 10,000 |
| `totals` | a matrix: `totals.rows` is a total row (the row fields rolled up), `totals.columns` a total column, both is `{subtotal_by: rows + columns, grand_total, cube}`; a sort of an axis by a measure asks for the other axis's total too (the view's `sort_needs_totals`). A table or chart with `totals: {grand_total: true}` and no `subtotal_by` rolls up every attribute it shows |

The result also lists the **parameter bindings** the request leaves open (`{request: "q", document: "quarter"}`: the request's marker `q` is supplied by the document's `quarter`, as the query's `params` maps it),
which the resolution of DBR04 binds with the viewer's values, and **where in the document** each place of the request comes from (`DocumentPath("$.query.measures[1]")` is `widgets.by_region.encodings.y`), so
a refusal of the engine lands on the field or the filter the person wrote.

**Refusals** (nothing is returned, every one has the file, line and path):

| Code | Meaning |
| --- | --- |
| `alias_required` / `duplicate_measure` | a measure with `filters` and no `alias`; one output name twice with different definitions (DBR50, above) |
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

## The widget editor's field wells

The console's editor (`?edit=1` on a dashboard, form view; `Shared/web/views-editor/src/WidgetEditor.tsx`) edits a widget through **wells**, the places of its encodings: a metric's `value` and `compare`; a chart's `x`, `y`, `color`,
`column`, `row` and `tooltip`; a matrix's `rows`, `columns` and `values`; a table's `columns`; a control's `values.attribute`. The pure logic is in `@dbdatakit/visuals` (`src/editor/wells.ts`, `src/views/catalog.ts`) and is the same
`deriveRequest` the server runs, so the panel "The request this asks for" shows what will be asked, each part with the place of the document it comes from (`documentPath`) and the parameter bindings.

- A field comes from the model's **catalog** (`ViewsClient.catalog(model)`; `catalogFromModel` reads the Gateway's model JSON; `catalogRoles` gives `deriveRequest` its roles) by dragging it into a well, picking it from the well's list
  (which offers only what the well takes) or typing its name.
- A **measure well** (metric, matrix `values`) takes measures only, an **attribute well** (matrix `rows` and `columns`, a control's values) attributes only; a field of the other kind is refused in the engine's words
  (`customers.region is a attribute, not a measure.`) and nothing changes. A chart's channels and a table's columns take both.
- A field the catalog lacks is **placed** and flagged `unknown_member` in the well and in the derivation (the derivation puts it in the request's `attributes`), so a typo can be seen and fixed.
- Every well change is the edit command `setWell` (one undo step); the type chooser is `changeWidgetType` (the fields go to the wells of the new type that take them, by role: an attribute to a chart's `x`, a measure to its `y`;
  what no well takes is dropped); a layout's `widget_options` are `setWidgetOption`.
- What the browser cannot check (a filter's attribute, an order entry, totals) goes through `ViewsClient.validate`, whose answer is shown on the widget ("the source says ..."). The fixture source stands in for the server with
  `catalogDiagnostics`; a real source answers with the engine's codes at the file, line and path of the entry.
- **Write the query out** switches the widget to `mode: explicit` with the derived request as its starting text, a raw editor, and the binding check on every keystroke (`DBD-VIEW-005`: an encoded field that is not an output of
  the query); "Derive it from the encodings again" gives the written request up.

## The report editor

A report opens in the same editor as a dashboard (`?edit=1`, form view). Its **flow layout** has its own form (`Shared/web/views-editor/src/FlowEditor.tsx`, the commands in `@dbdatakit/visuals` `src/editor/flow.ts`); the YAML view
is the same text. A new report starts with one flow layout (A4 portrait, 15 mm margins) and one empty section, which the loader does not accept until it has a block.

- **Page**: size (a named one, or a custom width and height in millimetres), orientation and the four margins; the form says the printable area in millimetres and CSS pixels, the same numbers the guides are drawn from. An empty
  field removes the key (and `page`, `margins`, `header` or `footer` when it is left empty), so the file keeps the defaults.
- **Header and footer**: left, centre and right text. Tokens are `{title}`, `{page}`, `{pages}`, `{asof}`, `{viewer}` and `{params.<name>}` of a declared parameter; any other `{token}` is refused by the loader at its field
  (`DBD-VIEW-030`, an undeclared parameter `DBD-VIEW-008`) and the message is shown under the field. `cover` is a checkbox.
- **Sections and blocks**: a section has an optional title and `break` (`before` or `after`) and blocks; a block is a widget (`keep`, `repeat_header` for a table or matrix, `height` in millimetres), a **row** of cells with spans
  (a row's spans add up to at most 12: the loader says so at the row) or a **page break**. Sections and blocks move up and down; the last section cannot be removed. A layout places a widget **once** (`DBD-VIEW-020`).
  Removing a widget takes its blocks and cells with it (a row left with no cell goes whole); renaming follows every place.
- **Report options**: `strict`, `limits.pages`, `limits.rows` and the cover default (`cover.parameters`). A report has no refresh, so the form does not offer it. Every key a command adds is written at its canonical place
  (`strict`, `limits` and `cover` follow `title` and `parameters` and precede `widgets`), so a report built in the editor is the text `canonicalText` writes.
- **Preview**: the draft's flow layout drawn by `PagedFrame` (the guides where a page starts, the running text with its tokens, the cover), from what is written, saved or not. Each widget it places is asked of
  `ViewsClient.preview`; the fixture source answers with the recorded result of a saved document of that name and an error entry otherwise. Print is the browser's. **Print preview** is the exporter's PDF of the **saved** report (`ViewsClient.exportPdf`, below): it is on when the source has an exporter and the draft has no unsaved change, and says why when it is off.
- A report may also carry canvas or reflow layouts for the screen (`Add reflow layout`, `Add canvas layout`, `Add flow layout`); the dashboard's grid and canvas editors work on them unchanged.

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

The documents are in `Shared/conformance/views` (`good`, `bad`, `warnings` and `cases.json`, which says the code, path and line of each refusal); the .NET (`DbDataKit.Views.Tests`) and TypeScript
(`@dbdatakit/visuals`) suites run the same files. The TypeScript validators report the **path** of a problem (`layouts[1].tiles[0].x`), not a line: `JSON.parse` has no positions. The .NET loaders give both.
What needs the catalog (an encoded field the model does not have, an invalid request) is `CatalogCheck`, at publish, through the engine's validator, not the loaders.

### Format and freeze

`dashboard/1` and `report/1` are **open** (unreleased) and share **one number**, `ViewsFormat.Current`: a new widget type, query form, layout type or selector condition is `/2` of both kinds in one change, and a document is
written in the lowest number that can say it. `schemas/bundled/dashboard.schema.json` and `report.schema.json` are the same schemas as one file each (references inlined under the prefixes `core__`, `view__`, `vega_lite_subset__`), for an editor that reads one file; `python3 -I scripts/bundle_views_schemas.py` writes them, and a test of the scripts fails when they differ from the sources. `schemas/views-format.lock.json` records the number and the hashes of `core`, `dashboard` and `report`; a test fails when a schema changes without the lock (while the format
is open the lock is edited with the schema; once frozen a change is the next number of both kinds).

## Resolving a layout

`WidgetResolver.ResolveAsync(context, document, layout, parameters, only)` (`DbDataKit.Views`) is steps b to d of plan 32 section 2.6: for a document, the layout in play, the viewer's parameter values and optionally the widget ids to
resolve again (`only`, when a parameter changed: `WidgetResolver.UsedParameters(widget)` says which widgets read it), it gives the **rendered widgets**. Only the widgets the layout places are run, a few at a time
(`MaxParallelism`), each **as the viewer**: every request goes through the serving core's `IServingQueries` with the viewer's `ServingContext` (identity, roles, attributes, channel, trace), so access rules, limits and audit are the
host's and the resolver knows no rule. The result is `{kind: "rendered_widgets", layout, as_of, widgets: {id: ...}}`, the shape of `schemas/rendered-widgets.schema.json` (typed in `@dbdatakit/visuals`, `src/views/rendered.ts`).

| Widget | What it becomes |
| --- | --- |
| `metric` | `{value: {v, text}, compare: {v, text, change: {text, direction}}}`: the text is the number preset's (`catalog`, `full`, `compact`, `percent`, shaped by the catalog's format of the measure); the change is `(value - compare) / abs(compare)` of the two numbers the server gave, in percent |
| `table` | `columns` (field, label, `align`: right for a measure, left otherwise) and `rows` of `{v, text}`; a total row of the result (a rolled-up row) is `total: true`, the rolled-up cells read "Total", and total rows come after the rows they total |
| `matrix`, `chart` | the engine's `ViewRenderer` over the answer (the one place they become data); its refusal (`stack_not_additive`, `totals_missing`...) is the widget's error, at the document's encoding path; the Gateway's query explorer draws its matrix and chart through the same renderer (`POST /api/v1/my/runs` with a `view`, `.../{result_id}/render`; plan 14a D37), its refusal is the explorer's error (shown with its code, path and message; the explorer has no view rules of its own beyond turning Draw off while a required slot, a Value for a matrix, an Along and a Value for a chart, is empty, D38; a replay takes `keep` and `view` too, `POST /api/v1/my/runs/{id}/replay`); an answer with a column the renderer cannot read (a time of day, a binary) is answered as a table with a `view_unavailable` notice that names the column and its type, D38 |
| `text` | `{text}` with `{{params.x}}` substituted as text (a list joined by commas, nothing as empty); the renderer substitutes again and never makes markup from a value |
| `image` | a `data:` URI of the bytes the host holds under the hash (`ImageBytes`), at most 1 MiB |
| `control` | the values it lists, as the viewer may see them (`{"kind": "members", "attribute": ...}` on the model), `{value}` each; a control with no list has nothing to resolve |
| error | `{type: "error", code, message, path?, suggestions?}`: the core's own code and words (`denied`, `unknown_member`, `limit_rows`, `timeout`...), or a `DBD-VIEW-05x` of the resolver; an unexpected exception shows only its kind (the host's `OnUnexpected` has the rest) |

**Parameters.** The viewer's values are read as each parameter's type (a URL carries text), a `many` parameter is a list, a missing value is the default. They travel **beside the request** as its `parameters`, with the document's
definitions as `parameter_definitions` (type, `for`, `many`); the request keeps its `{"param": name}` markers (the ones `DeriveRequest` lists as bindings), so the request hash and the cache key are the literal form's.
**A filter on a parameter the viewer left unset (no value, or an empty list) is dropped** from the request ("no region chosen means all regions"): the engine refuses an empty list (`param_empty_list`), and the sample
documents say so in their text. A parameter that is `required` with no value and no default is the error `DBD-VIEW-050` of the widgets that use it (its control still draws); a value that does not fit its type, or names a
parameter the document does not declare, refuses the resolution (`DBD-VIEW-051`).

**Query forms.** *Derived* and *semantic request*: `ServingRequest.Semantic` on `semantic:<model>`. *Semantic SQL*: `ServingRequest.SemanticSql` with its markers (`:name`) bound by name. *Saved query*:
`SELECT * FROM name(arg := :arg)` on the saved query's model, which the host supplies (`SavedQueryModel`; a saved query's widget does not say). *Store read*: `IServingEntities.ReadAsync`, with the read's `limit`
applied. **Not run here, with `DBD-VIEW-052` as the widget's entry:** a saved query with no model, a store `filter` or `sort` (the entity read gives the rows whole), a `ref` (a held result belongs to the session that made it),
a store read when the host offers no Store. These wait for the host that has them (DBR11) and the Store's filtered read.

**Limits.** `MaxRows` (an answer over it is the error `DBD-VIEW-054`, never a silently cut table; it is also asked of the core as the request's own limit), `WidgetTimeout` (counted on an injected `TimeProvider`; a source that
ignores its token is not waited for past it; the entry is `timeout`), `MaxWidgets` (the rest get `DBD-VIEW-055`), `MaxParallelism`. A cancelled call stops the whole resolution.

**The cache key.** `RenderedWidgets.CacheKeys[widget]` is `WidgetCacheKey.Of(context, source, request)`: channel, preview, the caller's id, roles and attributes, the source and the request as it ran. Two viewers whose access differs
never share one; the cache itself is plan 24's and the host's.

| Code | Meaning |
| --- | --- |
| `DBD-VIEW-050` | a required parameter the widget uses has no value and no default |
| `DBD-VIEW-051` | a parameter value of the wrong type, a list for a single value, or a parameter the document does not declare (the resolution is refused) |
| `DBD-VIEW-052` | a query form (or part of one) the resolver does not run here |
| `DBD-VIEW-053` | no catalog of the widget's model, so its request cannot be derived |
| `DBD-VIEW-054` | an answer over `MaxRows`, or an image over 1 MiB |
| `DBD-VIEW-055` | the layout places more widgets than `MaxWidgets`; this one was not run |
| `DBD-VIEW-056` | the answer does not hold a field the widget draws, or a column type it cannot |
| `DBD-VIEW-057` | the layout is not one of the document's (the resolution is refused) |
| `DBD-VIEW-058` | the bytes of an image held by hash were not found |

## Publishing: the kinds `dashboard` and `report`

A document is published as a definition of plan 30's registry: `ViewDefinitions.Register(kinds, ViewPublishingSources.From(store, payloads))` registers **`dashboard`** and **`report`** for `serving` targets (they serve at once;
a document is read, not code), both in format `ViewsFormat.Current` (the one number; current 1, previous 1). A host runs `DefinitionFollower` over `PublishedViews` to read what a channel serves.

- **Format.** `FormatOf` is the document's `schemaVersion`. A newer one is refused by the publisher (`PublishRefusal.Format`) naming kind, name and both numbers, and by the follower the same way; the version served before keeps serving.
- **Dependencies.** `depends_on` is `semantic-model:name@version` for each model the widgets query (`ViewDefinitions.DependsOn` writes the list from the document and the versions a channel serves). A model a widget queries
  and the list does not name, a model with no version, a version the group does not have and a catalog that does not load are refused naming the model (`DBD-VIEW-060`). Activation needs the pinned version active in the channel
  (the registry's own check), so a dashboard cannot be served against a model version its channel does not serve.
- **The validator** is the loader, then the catalog check with the production adapter over the engine's validator (`SemanticRequestValidator`, per model version: the roles come from the published catalog), then three checks that
  need the models and the files: a saved query a widget runs is defined by a model it depends on (`DBD-VIEW-061`), a control's attribute is one of the models its widgets query (`-062`), and an image is a file of the version
  named by its SHA-256 (`-063`), stored as the media type the widget says, at most 1 MiB, and a png, jpeg or webp **by its first bytes** (`ImageCheck`, the words of `checkImageBytes`; `DBD-VIEW-003`). Nothing is stored for a refused version.
- **Reading.** `PublishedViews` builds a `PublishedView` from the served version before it replaces the one it served: the document, the pinned model versions' catalogs (`CatalogRoles` by model), the saved queries' models and the
  pictures' bytes from the version's files. `PublishedView.Resolver(IServingQueries)` is a `WidgetResolver` with all of it filled in; `WidgetResolver` also refuses an image whose bytes are not the raster the widget names.
- **Listing.** `ViewDefinitions.List(publisher, kind?, channel)` lists the documents of one kind or both with what the channel has active, before and staged.
- **Promotion and rollback.** `DefinitionPublisher.PromoteWithDependencies` points the pinned dependencies (and theirs) at their pinned versions and then the document, in one transaction (a plain switch still refuses a model that is
  not active); `RollbackWithDependencies` returns the document and the models it was published against, and refuses, moving nothing, while another active definition pins a model's current version.

| Code | Meaning |
| --- | --- |
| `DBD-VIEW-060` | a model the document queries is not a dependency, is not pinned, has no such version or does not load |
| `DBD-VIEW-061` | a saved query is in none of the models the document depends on |
| `DBD-VIEW-062` | a control lists an attribute none of the models its widgets query has |
| `DBD-VIEW-063` | an image's hash is not among the files of the version |

## Serving: the module `views`

`DbDataKit.Views.Hosting` (`ViewsModule`, an `IServingModule` named `views`) serves the published kinds over REST, as the caller. It has **a listener of its own** (`ViewsOptions.Listen`: address, port, certificate; J28's decision that REST is its own
listener, off unless enabled) or its routes can be mounted by a host (`ViewsModule.Map`). The host supplies three things: the serving core (`IServingServices`, whose `Queries` run every widget), `IViewsCallers` (a request's token to a
`ViewsPrincipal`: the identity home's tokens) and `IViewsAudit` (`StateViewsAudit` over the install's audit).

| Route | Gives |
| --- | --- |
| `GET /api/v1/dashboards`, `GET /api/v1/reports` | `{items: [{name, title, version}]}` of what the caller may open |
| `GET /api/v1/{dashboards,reports}/{name}` | `{kind, name, title, version, as_of, text, document, layouts: [{name, type, default}]}`: the file as published, the document as JSON, the version as text (the registry's number) and `as_of` (when that version was published) |
| `POST /api/v1/{dashboards,reports}/{name}/render` | the `rendered-widgets` document of the layout in play; body `{parameters?, layout?, only?, order?, expand?, viewport?: {width, height}, medium?}` (`order`: a viewer's sort of a table, DBR45; `expand`: the rows a viewer expanded in a matrix with a `show: drill` group, DBR55); with no `layout`, `LayoutSelector` chooses for the viewport (the default layout without one); an unknown key is refused |
| `POST /api/v1/views/preview` | the **live preview of an unsaved draft** (plan 32 DBR28), resolved as the caller and stored nowhere: body `{kind?, document \| text, layout?, only?, parameters?, viewport?, medium?}` for a whole document (the answer is the layout's `rendered-widgets`), or `{kind?, widget, widget_id?, document?: {parameters}, parameters?}` for one widget placed alone (the answer holds its entry); see the bullet below |
| `GET .../{name}/export?format=pdf&layout=&p.name=` | a **PDF** (`application/pdf`, an attachment `name-layout.pdf`): the layout named, else the one the **print** medium selects (a print layout, or the default), resolved as the caller and drawn in the pinned Chromium; `p.region=West` (repeated for a list) sets a parameter. No exporter or browser: **501 `export_unavailable`** naming the setting; 413 `export_too_large` over `max_pages`, 504 `export_timeout`, 503 `export_busy`, 500 `export_failed` (the page said it could not be drawn). Audited with the format, layout, pages and the browser's version |
| `GET .../{name}/export?format=csv\|xlsx\|svg&widget=&layout=&p.name=` | the **data exports** (DBR17), resolved as the caller for the layout chosen as above (`layout=wall` names another) and audited like the PDF (`view.export`, with `format`, `widget`, rows, bytes). **`csv`**: the rows of the table or matrix `widget` (or a chart's data) as the caller was shown them, RFC 4180 (comma, CRLF), UTF-8 **with a byte-order mark** (Excel needs it, every other reader skips it), the engine's values not the formatted text (a total row says "Total"), and a **text** cell or header starting with `=` `+` `-` `@`, a tab or a carriage return gets a leading apostrophe so a spreadsheet never runs it (a number is never changed). **`xlsx`**: with `widget` one sheet; without, a sheet for each table, matrix and chart the layout places (named by the widget id, safe and unique ignoring case; a control, text and image have no rows) and, first, a `Metrics` sheet with a row for each metric of the layout (`Widget, Label, Value, Compare, Change`; one metric named by `widget` is still refused, it has no rows), header bold and frozen, totals bold, text always text, never a formula. **`svg`**: the chart `widget` as the print page draws it, taken out of the browser (the module has no JavaScript of its own, so it needs the exporter like the PDF does; 501 `export_unavailable` otherwise). `csv` and `xlsx` need **no browser**; the Gateway sets the limit in `views: {maxExportRows}` (default 100,000, 1 to 10,000,000). A failed widget is its error block: an xlsx sheet of `code, message, path`, and for `csv` and `svg` a 422 `widget_failed` with the widget's code and words. Refused in words: 400 `invalid` (`csv` and `svg` need `widget`, `pdf` takes none, an unknown format), 404 `widget_not_found`, 400 `widget_not_placed` (the layout does not place it), `widget_not_tabular`, `widget_not_chart`, `nothing_tabular`, 413 `export_too_large` over `ViewsOptions.MaxExportRows` (100,000 rows in all, and the 1,048,575 of an xlsx sheet) |
| `GET .../{name}/print?layout=&p.name=` | the same print page as HTML, for the browser's own Print (and for looking at what the exporter draws); its files are loaded from the next row |
| `GET /api/v1/views/print/{print.js,print.css,fonts/...}` | the visuals' print bundle and the bundled fonts: code and fonts, **no token**, no data |
| `GET /health`, `/health/ready`, `/health/live` | `{module, state, detail, draining, in_flight, served, not_served, cache, export: {available, reason, browser, mode}, requests, refused}`; 503 while draining or down |

A refusal is `{code, message}`: 400 `invalid` (or the resolver's own code, `DBD-VIEW-057` an unknown layout, `-051` a parameter), 401 `unauthenticated`, 403 `denied`, 404 `not_found`, 413 `too_large`, 500 `error` and `audit_unavailable`, 503 `draining`.

- **As the caller.** `WidgetResolver.ResolveAsync` runs each widget through `IServingQueries` with the caller's `ServingContext` (their roles and claims, the channel, a trace id, the client address). Nothing in the module knows a row rule; a caller whose rows differ gets other rows.
- **An app's token narrows.** A `ViewsPrincipal` for an app's token carries `ViewsAppScope` (the instance and the semantic models its approved manifest names). A document that queries a model outside the list is refused 403 for `open` and `render` and left out of
  the list; the context carries the claim `app` (the instance), so the person's own session and the same person inside an app never share a cached result.
- **Audited.** `view.list`, `view.open`, `view.render`, `view.export` and `view.preview`: actor (the person), `via` (`api`, or `app` and the instance), target (`kind`, `name@version`), outcome (`ok`, `refused`, `failed`) and a detail of the layout, the number of widgets
  and errors, the error codes, the **names** of the parameters (never a value) and the trace id. An entry that cannot be written stops the answer (500 `audit_unavailable`, no data in it). A call without a valid token is not audited here (the host's throttle counts it).
- **Preview** (plan 32 DBR28, `ViewsApi.PreviewAsync`). What the editor holds, **before it is saved**: `document` is the document as a JSON object and `text` its YAML or JSON (not both; a body over `ViewsOptions.MaxPreviewBytes`, 256 KiB, is 413 and is not read);
  `widget` places one widget alone on a layout of its own (`widget_id` names it, `preview` by default; `document` gives the parameters it may use, nothing else of it is read). `kind` is `dashboard` (the default, or the `document`'s own `kind`) or `report`.
  It **refuses what a publish would refuse**: the draft goes through the registry's validator for the kind with its models pinned to the versions the channel serves now (`ViewsPublications.Draft`: the loader, the catalog binding check, saved queries, control attributes, pictures; a document with an image widget is refused as a publish refuses it, `DBD-VIEW-063`),
  and a draft with a problem is a 400 `invalid` in the publish's words with the loader's `diagnostics` (`{code, severity, path?, message, line?, column?}`) placed in the text. It resolves as the caller with the module's limits and an app's narrowed token is held to its manifest's models (403). It **never** touches the cache, the registry or the files:
  the same request gives fresh rows, and `only: []` checks the draft and asks the core for nothing. What the caller may not read is an error entry in its widget, as in a render. Audited as `view.preview` (target `kind` and `draft`, the shape, layout, counts and error codes, parameter **names**; never the text or a value).
  The console's relay carries it (DBR28b): the Gateway's management API answers `POST /api/v1/views/preview` in process (`GatewayViewsManagement`), as the person the console's token names, with the viewer role and the same limit (413 over `MaxPreviewBytes`); a Gateway without the module answers 404 in words. The console turns on `moduleClient`'s `serverCheck` for a Gateway's documents, so the editor's text is also checked by the module as a publish would check it (each check is an audited `view.preview` with `only: []`).
- **Export** (plan 32 DBR15; `DbDataKit.Views.Export`). Off unless the host gives the exporter (`ViewsOptions.Exporter`, `views.export` in settings: `mode: pinned|system`, `path`, `pin`, `page_assets`, `deadline_seconds`, `max_pages`, `max_concurrent`, `no_sandbox`, `fonts: {directory, pin, allowed, required}`; strict, an unknown key fails naming the file; `ExportSettings.Read` is the strict reader for a host that keeps them in snake_case; the Gateway's `server.yaml` has the same keys in its own camelCase: `views: {export: {mode, path, pin, pageAssets, deadlineSeconds, maxPages, maxConcurrent, noSandbox, fonts: {directory, pin, allowed, required}}}`, validated with the rest of the file).
  The module checks the browser at start and says in `/health` `export: {available: false, reason: "export unavailable: ..."}` (no browser at the path, a different version than the pin, the bundled fonts differ from `fonts.pin`, a promised host font missing, the print page's files missing) and **keeps serving screens**.
  The page is the module's `PrintPages`: a shell with the document, the layout in play and the rendered widgets (as the caller) in one JSON block, the bundled fonts as `@font-face`, and the visuals' print bundle (`npm run build:print -w @dbdatakit/visuals`, the folder is `page_assets`); nothing in it points outside and the exporter blocks every request that is not its own file. The paper is a flow layout's `page`, a canvas layout's own size, any other layout A4 landscape with 10 mm margins; a flow layout's `header` and `footer` become Chromium's header and footer templates (`{page}` and `{pages}` are the browser's page fields).
  A download over the Gateway's management API (the console's relay) is JSON, since the relay carries nothing else: the same route and query as the module's own (`GET /api/v1/dashboards/{name}/export?format=...`), called in process as the person, answered as
  `{file_name, content_type, size, content_base64, headers}` (the module's `X-DbData-*` headers; up to 24 MiB, else 413 pointing at the module's own listener); its refusals are the module's own. `ViewsClient.exportPdf(ref, {layout, parameters})` (`moduleClient`) reads either answer (the module's bytes, or the relay's JSON with the file in base64) into `{fileName, contentType, bytes, pages, browser}`; the page count is the header `X-DbData-Export-Pages`. `DashboardView` gives a report's paged frame a Print preview that opens it in a dialog (`PrintPreviewDialog`, `usePrintPreview`) with the page count; a refusal (`501 export_unavailable` names the `views.export` setting) is shown in the module's words. **Not built yet:** a Playwright check that the dialog's page count equals the exporter's needs the browser and fonts on a machine that has them.
- **Result cache.** `WidgetResultCache` (`ViewsOptions.CacheTtl`, 30 seconds by default, 0 off): the rows of a successful answer, kept under `WidgetCacheKey` (channel, preview, who, roles, claims, source, the request as it ran) plus the definition's scope
  (`PublishedView.CacheScope`: kind, name and what it depends on). A refusal, a timeout and an answer over the row limit are never kept; what is handed out is a copy. Control member lists are cached the same way.
- **Draining and health.** `DrainAsync` stops taking requests (503 `draining`, `Retry-After`) and waits for the ones in flight (the suite's `IDrainCoordinator`); `Health()` is `Ok`, `Degraded` (draining, or the channel serves a version this instance does not: a
  model version it does not have, a newer format) or `Down` (not started, stopped).
- **Following.** `ViewsPublications` holds the two `PublishedViews`; the module follows the group's definitions itself (`ViewsOptions.Follow`, every `PollInterval`, or `Refresh()`), or a host that runs one follower adds `Subscribers` to it.
  `ViewsPublications.Register(kinds, store, payloads)` registers `dashboard` and `report` in the registry of the publisher that validates a publish.

### Scheduled and subscribed delivery (plan 32 DBR18, plan 22a GP17)

A person subscribes to a dashboard or a report (`DbDataKit.Delivery`: `SubscriptionSpec`, `SubscriptionStore`, the console's subscriptions page) with the document's parameters, a layout, a format (`pdf`, `csv` of one widget, `xlsx`), a delivery channel and a schedule (a cron in a time zone; at most hourly). The runner's task `subscriptions` of the system package `views-export` (`DbDataKit.Delivery.Runner`) takes what is due and, for each, calls `SubscriptionRenderer` (here, `DbDataKit.Views.Hosting`), which is `ViewsApi.ExportAsync` **under the subscriber's own principal** (`ISubscriberPrincipals`: their roles and attributes, never the runner's), then hands the file to the delivery service as an attachment.

- **As the subscriber.** The export is resolved, cached and audited as that person (`view.export` names them), so two subscribers of one document get their own rows and the audit shows both.
- **The layout.** A subscription's `layout` is a named layout and wins; with none, the export rule of the layout selection (the print medium) applies, as for a person pressing Export.
- **A failed widget** is in the PDF as its error block and the message names it (`X-DbData-Export-Widgets-Failed` of the export, `id=code`). A subscriber for whom **every** widget of the layout is denied, a document the module refuses (403, 404) and an identity the host cannot act as lose access: nothing is sent, the subscription stops, and the subscriber and the owner are told.
- **The inline picture.** A PDF subscription may set `inline_preview: true` (the console's subscription form has the choice). The renderer then asks the module for the same document once more as `format=png` (`ViewsApi.PngFormat`: the print page's picture at the paper's width, the print medium, as tall as its content up to `PreviewMaxHeight` = 4,000 pixels, audited as `view.export` with `format: png`, as the subscriber) and `RenderResult.Rendered.Preview` carries it. **An email** shows it in the message: the picture is an attachment with a content id (`DeliveryAttachment.ContentId`, `preview@dbdatakit`), the HTML body names it as `cid:`, and `MailMessageBuilder` writes it as `multipart/related` beside the body, not among the downloads. **Teams, Slack and the webhook** cannot hold a file: they get no picture and the message's link to the live view (they already say an attachment is not included). A picture that cannot be made (a host whose exporter has no `png`) does not stop the PDF: the message carries a note.
- **A custom page** (DBR34): a subscription of kind `page` has `view: <instance>.<name>` (the app instance and the view's name in the manifest; neither contains a dot), is a `pdf` (`inline_preview` adds the picture of the same page, drawn once more by `ExportPngAsync`) and has no layout or widget. The renderer calls `ViewsApi.ExportPageAsync` **as the subscriber**: the queries the manifest entry declares run as that person (two subscribers with different access get different rows), the app's page is drawn as the top-level document (`CustomViewPrint.Page`, DBR32) and audited as `view.export` of kind `page`, target `<instance>.<name>`. The message carries one link to the live view (`SubscriptionRendererOptions.PageLinkTemplate`, with `{instance}` and `{name}`). A custom widget inside a dashboard or report needs nothing of its own: the subscription is the dashboard's, and the export draws the widget with the subscriber's rows (DBR32). **Failures send nothing:** a person without a role on the instance, a page the host does not serve and a query the serving core denies lose access (the subscription stops, the owner is told); a page that cannot be drawn (a view that failed or never said it was ready, no browser, a deadline) is a failed render in the module's words and the owner is told, as for any subscription. `ICustomViewApps` (`FindPage`, `MayOpen`) is what a host gives; the Gateway's `GatewayCustomViewApps` reads the group's app versions (`AppInstanceStore`) and takes **any role on the instance's server** as the reading rule (a stricter per-instance check is the apps host's to add).
- **No exporter or no browser** is `RenderResult.Failed("unavailable")` in the module's own words (they name `views.export`); the task then fails and the subscription shows the same problem.
- **Where it runs:** in the process that has this module and the install's state: the Gateway (`GatewaySubscriptions`, a hosted service that runs the task every minute over the Gateway's state, whose `delivery` and `runs` modules are install scope, so a group's SQL Server or PostgreSQL state is the one the console keeps the subscriptions in). It claims nothing unless the instance can export (`views.export`). **The Gateway has no delivery channels of its own** (a channel name is the console's binding and connection) **and needs none:** a subscription stores the **kind** of its channel (`subscription.channel_kind`, migration 6 of the `delivery` module; the console writes it when the subscription is saved and its sender keeps it current), the Gateway draws the document as the subscriber, decides the message by that kind (an email gets the inline picture) and writes it to the **shared outbox** (`EnqueueOnlyChannels`, no connection or secret, no sender of its own); the console's sender claims it and sends it through its own channel, keeps the retries and the dead letters. This needs the console and the Gateway on one state (a group's SQL Server or PostgreSQL). A host that registers real `IDeliveryChannels` (a test) also sends the outbox in the Gateway. A runner on another machine calling the module over HTTP for a subscriber is not built (plan 22a, open).

### The export component (plan 32 DBR16)

A host that exports PDFs needs the pinned Chromium, the bundled fonts with their licences and the print page. They are one folder, the **export component**, installed beside the host and not inside its program folder (an upgrade replaces the program files and leaves it):

```
export/  chromium.pin  fonts.pin  .chromium/<rid>/...  .fonts/<family>/...(+ OFL.txt)  print/print.js  print/print.css  export-component.json
```

`python3 dist/export_component.py stage --rid linux-x64 --out <dir> [--zip <file>]` makes it from a checkout whose fetch scripts have run (`scripts/fetch-chromium.sh`, `scripts/fetch-fonts.sh`, `npm run build:print -w @dbdatakit/visuals`), and `export_component.py verify <dir> --rid linux-x64` checks any copy: the browser executable, every font file and licence against the pins and the print files against `export-component.json`; a changed byte fails naming the file. The Gateway is pointed at the folder with `views.export`: `mode: pinned`, `pin: <dir>/chromium.pin`, `page_assets: <dir>/print`, `fonts: {directory: <dir>/.fonts, pin: <dir>/fonts.pin}` (a host that installs the folder over its program folder needs only `mode` and `page_assets`: the pins are found above the program). The container layer is `dist/export/Dockerfile` (`docker build -f dist/export/Dockerfile --build-arg BASE_IMAGE=dbdatagateway -t dbdatagateway-export .`): it adds Chromium's shared libraries, the staged component under `/opt/dbdatakit/export`, the settings as environment variables, `HOST_FONT_PACKAGES` only for host fonts the image promises in `views.export.fonts.required` (none by default), and fails the build when the browser cannot start. The runner process of a Gateway needs nothing more: the `views-export` task runs inside the Gateway, so it uses the Gateway's `views.export`.

**Getting it onto a host (owner decision 2026-11-12): a release asset and an install in the product, never a script a person runs.** `dist/export_component.py` is a build-time tool only; the Gateway's release (`dbdatagateway-release.yml`, job `package-export`) runs it for `win-x64` and `linux-x64` and attaches `dbdatagateway-export-v<version>-<rid>.zip` beside the product zips, listed in the release's `SHA256SUMS`; no product zip or MSI contains it. A host installs it itself:

- `dbdatagateway export install [--version <release>] [--source <folder or https address>] [--dir <folder>] [--force]` downloads the zip of this platform from the release of **this program's version** (the print page and the program are one release; `--version` names another), refuses an archive whose SHA-256 is not the one the release's `SHA256SUMS` lists, extracts it into `export-component` in the data directory (not the program folder, so an upgrade keeps it), verifies the result as `export_component.py verify` does (the browser, every font and licence against the pins inside it, the print files against the manifest: a changed file is refused naming it and nothing is installed), moves it into place (an older one goes aside first) and sets `views.export` (`mode: pinned`, `pin`, `pageAssets`, `fonts.directory`, `fonts.pin`) in `server.yaml`. `export status` says whether it is installed, whole and in use. The views module reads `views.export` when the gateway starts, so a restart follows an install; in a container set `views.export.noSandbox: true`.
- **Where the files come from:** the release's GitHub download address by default (the repository of `DBDATAKIT_UPGRADE_REPO`); `--source` or `DBDATAKIT_EXPORT_RELEASE_BASE` names a folder holding the release's files (`SHA256SUMS` and the zip, or the mirror layout `dbdatagateway/v<version>/`; a `file://` address works too) or an https address that serves them, for a host with no internet; `DBDATAKIT_UPGRADE_MIRROR` (the console's and the servers' upgrade mirror) is read when neither is set. What is trusted is the release's `SHA256SUMS` from that source and the pins inside the zip.
- **Installers and the image:** `install.sh --with-pdf-export`, `install.ps1 -WithPdfExport` and `msiexec ... ADDPDFEXPORT=1` call `export install` for the release they installed (`dist/README.md`); the suite release also builds the image variant `ghcr.io/<owner>/dbdatagateway:<version>-export` (`latest-export` for a stable release): the Gateway image plus `dist/export/Dockerfile` with the component and `views.export` on.
- **In the console:** the **Server settings** screen of the Gateway has a card, "PDF export component": when it is not installed it says so and offers **Install**, which calls `POST /api/v1/views/export/install` (administrator only, audited as `export.install` with its outcome) and runs the same installer in the Gateway, showing the step and the megabytes and, on a refusal, the Gateway's words naming the file. A finished install sets `views.export` as a commit of the configuration by the person and says a restart is needed. The release address is the host's, never the caller's. `GET /api/v1/views/export` is the card's state.

### The report catalog (plan 32 DBR19, section 9e)

One interface answers what a source of reports has, the same for our own documents and for an external report server: `IReportCatalogProvider` (`DbDataKit.Views`) with **browse** (the items under a folder, or a search by name, path or description, narrowed by item type),
**find**, the item's **parameters** (name, type, default, the values it may take, the attribute the values come from, what it depends on), **open**, the **export** formats and the **permissions** the catalog states. Every call is for a `ReportCatalogViewer` (the serving caller and, for an
app's token, the app instance and the models its manifest names); a provider never lists or opens what that person may not, and a refusal and a missing item look the same.
`ReportCatalogs` is the **merged view**: the items of every provider in the order given, an item's key is `provider:id` (`views:dashboard/sales`, `ssrs:{guid}`), a provider that is down is a `ReportCatalogProblem` in the listing and the others are still listed (an unexpected failure shows its
kind, never its words), a provider's `denied` or `not_found` to a browse is silence, and **an external report is framed only from an approved origin** (scheme, host and port, never the path; a host takes the list from the app instance's `frame` exception): an opening a provider offers as `frame`
from any other origin is handed on as a `link`, and an address that is not `http` or `https` is refused.
**Our own provider** is `ViewsCatalog` (`DbDataKit.Views.Hosting`, name `views`): the served documents under `/Dashboards` and `/Reports` (ids `dashboard/<name>`, `report/<name>`, folders `folder/dashboards`, `folder/reports`), narrowed by an app's models as the routes are, opened as
`{mode: document, kind, name, version, layouts, layout}` (the layout and the parameters are checked against the document's own; the render, export and print are the module's, as the person), the formats of `ViewsApi.Formats` with `pdf` and `svg` unavailable and saying why when the host has no
browser, and permissions stated as unknown (who may see a document is decided when it renders). Its browse and open are audited (`view.list`, `view.open` with `catalog` in the detail; never a search text or a parameter's value). The owner is empty. The `embed` widget is built (above). **Create by kind** is an optional capability of a provider (`CreatableKinds`, `CreateAsync`): a provider without it says `unsupported` (the report server's provider: its reports are made in its own tools); `ReportCatalogs.CreateAsync` routes to the named provider.
Our own provider does not make items yet (it would save a person's own document, which the catalog does not list).

**The external report servers** (`DbDataKit.ReportCatalogs`, plan 32 RC01 to RC04). A connection of kind `report-catalog` (`endpoint` the web portal, `report_catalog: {provider: ssrs|pbirs, name?, report_server_address?}`, the service account as a sign-in) gives `ReportCatalogConnections.Create` the provider over the portal's REST API
(`ReportServerCatalogProvider`). The runner's system package `report-catalogs` (task `read`, every fifteen minutes, `DbDataKit.ReportCatalogs.Runner`) reads each catalog as its service account into a `report-catalog/1` snapshot and publishes it through plan 30's registry (kind `report-catalog`, one definition per catalog, one version per change, per channel;
`DefinitionStoreSnapshotSink`); a failing read leaves the served version and the run says which catalog and why. `SnapshotCatalogProvider` serves a person the snapshot **filtered by what the report server's policies say about their names** (`ReportCatalogAccess`; the names are the external groups the sign-in recorded in the identity home, the same values
`HomeDirectory.MapExternalGroup` maps to roles, from `ReportCatalogNames.For`): a person with no mapped policy lists nothing, an item they may not see is `not_found`, and only **open** goes to the report server's provider (the address with the parameters filled in; an export format is the parameter `rs:Format` and answers a `link` to the file).
The same snapshot is generated knowledge (`GeneratedCatalogKnowledge`, subject `report-catalog` narrowed by `item`; `ReportCatalogKnowledge.MayRead` answers the access with the same filter), so a search for a report's title finds an external report beside a dashboard, each marked with its catalog.

**The routes** (`ViewsApi.Catalog.cs`, on the module's listener or a host's routes; `ViewsOptions.Catalogs` null answers 404 saying so): `GET /api/v1/catalog?folder=&search=&types=` (items by key, `unavailable` providers, `creatable` kinds), `GET /api/v1/catalog/item?key=` (item, parameters, exports, permissions), `POST /api/v1/catalog/open {key, parameters?, layout?, format?}`
(`{mode: document|frame|link, ...}`) and `POST /api/v1/catalog/create {provider, kind, name, folder?}` (201, or 501 `unsupported`). Browse and open are audited (`view.list`, `view.open` with `catalog` and the key, never a value). The console's **Catalog** tab of Views is the report browser (`docs/ui-editors.md`). Not built: the Gateway relaying these routes and
setting `Catalogs` from its `report-catalog` connections (the host half), the app manifest's `catalogs` list, the instance's `frame` origins reaching the merge, the Windows sign-in of the report server (NTLM or Negotiate: the connection signs in with basic, an API key, OAuth 2 or a client certificate).

### In a Build project (plan 32 DBR29)

A dashboard or report can live as a **file of a Build project**: `dashboards/<name>.yaml` and `reports/<name>.yaml` (also `.yml` and `.json`; the folder is the kind, the file's name the document's name, which the Gateway takes as the registry name). They travel with the semantic model they depend on, are versioned by the project's git, and are exactly what an agent writes.

- **Check.** `project validate` (so `project compile --check`, the Edit tab and the Views tab's **Check the project**) reads each file with the views loader and checks its widgets against the project's own compiled semantic models (`ProjectViews`): the loader's findings and the engine's (`unknown_member`, `wrong_member_kind`, a filter the model cannot take) come back as DDB-106 at the file, line and column. Saved queries, images and the model versions a Gateway pins are the Gateway's to check at publish.
- **Edit.** The Views tab of a project (`Shared/web/build/src/pages/ViewsTab.tsx`) opens the shared editor (`@dbdatakit/views-editor`, the same screens as the console's Views page) on the files of a **workspace**: `workspaceViewsClient` is a `ViewsClient` over Build's file commands, with the file's hash as the version (a stale save is a `version_conflict` naming the stored hash, as in the other stores), the wells' catalog from `GET .../workspaces/{id}/models/{model}/catalog`, and rows from a paired Gateway through the console's relay (none chosen: the preview says how to get rows). The Edit tab lists the files and links to the editor. Not done: opening them in the Edit tab's form (a `FileSchema` association with the editor as a `FormOverride`); the Edit tab shows the YAML and the loader's markers instead.
- **Publish.** `POST /api/v1/projects/{p}/proxies/{proxy}/publish-view` (`{kind, name, expected_version?, note?}`, approver) reads the file of the project **as merged**, refuses it with the findings when it has errors (nothing is sent), and otherwise `PUT`s it to the Gateway's `/api/v1/dashboards|reports/{name}` with the Publisher role and the person. The Gateway pins the models the document queries to the versions it serves, so publish the model first (`publish-model`).

### In the Gateway (plan 32 DBR11b)

The Gateway host runs the module when `views` is in its `modules:` (`DbDataGateway/README.md`, "The `views` module"): `GatewayViewsModule` mounts `ViewsModule.Map` on an https listener of its own (`views: {address, port}`, 8820), `GatewayViews` supplies the callers (`IViewsCallers` over `HomeCallers`: a
personal access token or an access token of the identity home becomes a `GatewayCaller`, so the serving core, the data source groups and the audit see the same person as for Flight SQL), the publications and the audit; the Gateway's one `DefinitionFollower` carries the two `PublishedViews`
(`ViewsOptions.Follow` is off); the readiness component `views` reports `Degraded` for a version this instance does not serve. The console's relay reaches the same routes on the **management API**
(`GatewayViewsManagement`: `GET|POST /api/v1/dashboards...` and `/reports...`, `GET /api/v1/views/status`, and `PUT /api/v1/dashboards/{name}` which publishes; the Versions panel uses `GET .../{name}/versions`, `POST .../{name}/switch`, `POST .../{name}/rollback` and `DELETE .../{name}` (retire), each needing the author role, quoting the served version as `expected_version` and audited), as the person the console vouches for. **Publishing** is `GatewayViewsPublishing`:
`ViewDefinitions.DependsOn` pins each model a document queries to the version of the published `semantic-model` the production channel serves, and the registry's validator refuses what the models cannot carry; the management route and `dbdatagateway views publish` call it.
Not there: row-level access rules (plan 22; today a person's data source groups decide, and a refusal is a `denied` widget entry), an app's narrowed token over REST (the apps host calls `ViewsApi` in process with a `ViewsAppScope`; not wired), export, and removing a published document.
A tester's script is [`views-testing.md`](views-testing.md).

## Your own dashboards and reports (plan 32 DBR12)

A person can keep dashboards and reports **of their own**, beside the group's published ones: nothing is published, there is no registry entry or approval, and the document is theirs until they share it. The store is the state module `user_views`
(`DbDataKit.State`, kept per group, so every instance of a serving group shares it; SQLite, SQL Server and PostgreSQL) and `UserViewStore` (`DbDataKit.Views`) over it:

- **`user_view`** holds the current version of a document: its **owner** (the person's *id*, lower case: an id is unique and stays, a display name is neither; `owner_name` is their name as of the last save, for lists), kind, name (lower case letters, digits, `_` and `-`), text (YAML or JSON), title, version and who saved it.
  **`user_view_version`** holds the versions that were replaced (history, so an edit can be undone and an undo undone; **retention: 365 days**, then the shared retention job removes them; a document's current version is never in this table, so a document nobody touches for years stays). **`user_view_share`** says who else may open it.
- **A save is a version** and quotes the version it read: a stale save, or a second create of one name, is a conflict (409 `version_conflict`, with `current_version`). Restore brings an earlier version back as a **new** version.
- **Sharing** is to `user:<id or handle>` (a person's id, or their name in lower case with spaces as `-`: `Ada Lovelace` is `ada-lovelace`), `group:<group>` (a role of the caller; in the Gateway a group of the identity home) or `*` (everyone who signs in). Sharing gives people the **document**, never the data: it is resolved **as whoever opens it**, through the serving core with *their* roles and attributes, so a person it is shared with sees only the rows their own access gives (a person with no access to the data source sees an error entry per widget, in the data source's words). A document that is not shared with you is a 404, the same answer as for a name that does not exist.
- **What is checked.** A text is checked as a publish would check it when it is **saved** and when it is **drawn** (`ViewsPublications.Draft`: the loader, then the catalog check over the model versions the channel serves, saved queries and control attributes), in a publish's words with the loader's diagnostics placed in the text; nothing is stored when it fails. A document whose model went away since is still **opened** (with `problem`, so its owner can mend it) and is refused in words when drawn. Pictures are not kept (a personal document has no files); an app's token has no personal documents (403).
- **Promote** (`GET .../promote`) gives `{path, text}`: `dashboards/<name>.yaml` or `reports/<name>.yaml` and the stored text, read with the same loader the files are, so a promoted file always loads; put it in an app's `views/` folder or a Build project and publish it as any other document. The person's own copy is not changed or removed.

Routes (on the module's listener and, relayed, on the Gateway's management API as `/api/v1/my/...`; the viewer role is enough, the person is the owner of what they save):
`GET /api/v1/my/views?scope=mine|shared|all&kind=` (items with `owner`, `owner_name`, `name`, `title`, `version`, `own`, and for an own document `shared_with`); under `/api/v1/my/dashboards/{name}` and `/api/v1/my/reports/{name}`:
`GET [?owner=<id>]` open, `PUT {text, expected_version?}` save, `DELETE`, `POST /render[?owner=<id>]` (a render request's body), `GET /history[?version=n]`, `POST /restore {version, expected_version}`, `GET|PUT /shares` (`PUT {grantees: [...]}` replaces the list) and `GET /promote`.
Only open and render take an `owner`; every other call is on the **caller's own** document (a call on a name you do not own is a call on your own, which does not exist: 404). Every call is audited as `view.my.<verb>` (target `<owner id>/<name>@<version>`; never the text).

**In the console** (the Views page): each Gateway that runs the module also appears as a source, **My dashboards on `<server>`**, beside its published documents. Its list shows your documents (with who they are shared with) and what others shared with you (owner by name, *Make my copy*);
the existing editor edits an own document (a shared one is `name@<owner id>` and opens read-only: the editor and the module both refuse a change); **Share**, **History** (restore), **Promote to a file** and **Delete** are on the row. **Pin to my dashboard** is in the toolbar of any opened dashboard or report: one of its widgets (a copy as the document has it, with the parameters it uses) goes into a new or an existing dashboard of yours, placed in its default layout,
saved on the version it was opened at. **Pin a query result** (under the list) keeps a semantic request, as it stands, as a widget (a table, a bar chart or a number of its outputs): the console has no screen that runs a request yet (J14 and J28 are the serving layer's), so the request is the one a person pastes.

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

The renderers are in `@dbdatakit/visuals` (`Shared/web/visuals`, its README has the calls). A document's widgets are resolved **through a `ViewsClient`** (`open`, `resolve`, `validate`, `save`, `preview`): the console runs
on `fixtureClient` (the sample documents of the retail template, `DbDataBuild/templates/retail/dashboards` and `reports`, with recorded results in `Shared/conformance/views/widget-results`) until the `views` module
answers, and `moduleClient` is the same interface over its REST (the routes above; `save` is the Gateway's `PUT` on the management API, which the console's relay reaches; `preview` is not in the module yet). The console's Views page uses both: `?source=` names the fixtures or a Gateway that runs the module, and with no choice the first such Gateway is used.

**The reflow layout fills its container.** `layoutReflow` lays the grid out in whole pixels so the cells fill the box with no gap and no overlap; a tile is never drawn below the smallest box of its type
(`metric` 160 x 88, `text` 160 x 56, `control` 180 x 56, `chart` 280 x 180, `table` 320 x 200, `matrix` 360 x 220; a tile's `min` may raise it). If any tile would be below its minimum the layout is the **compact stack**
(one column in document order, the page scrolls: the one place a reflow dashboard scrolls). Each tile picks a **density** (`xs`, `s`, `m`, `l`) from its box: a metric drops its label, then its comparison; a chart its
legend, then its axis titles, then thins its ticks; a table and a matrix show the rows that fit and say "12 of 40 rows" with an action that maximizes the tile (where the widget scrolls). `widget_options: {density}` pins it.

**Text and images.** Text is markdown without raw HTML, images or links off the host, `{{params.name}}` substituted as text; an image is drawn only from the `data:` URI of its raster, at most 1 MiB, never an SVG.

## Links: one vocabulary for a click (plan 32 section 9f, DBR35)

A document declares its **links** once, by name, and a widget names the one it follows. `drill` (above) is the same thing written in place and stays; a widget has `on_click` **or** `drill`, not both.

```yaml
links:
  region-page: {page: regional-detail, with: {region: $customers.region, year: $params.year}, print: link}
  order-form:  {form: orders, new: {customer_id: $customers.id, status: open}, as: dialog, then: refresh, lock: [status], print: hidden}
  order:       {form: orders, edit: {id: $orders.id}, as: panel, then: [orders_table], label: Open the order}
  year-report: {report: region_detail, with: {region: $params.region, year: 2026}}
widgets:
  by_region: {type: chart, ..., on_click: region-page}
  orders_table: {type: table, ..., on_click: order}
```

| Key | Meaning |
| --- | --- |
| `dashboard`, `report`, `view`, `page`, `form` | the target: **exactly one**, a name in the **same app instance** (a view is named by its file without folder and extension). `page` is a custom page or a code page of the app; `form` is a form view |
| `with` | the target's parameters (dashboard, report, view, page) |
| `new` / `edit` | a form only: `new` the fields a row to create starts with, `edit` the key of the row to change (one of the two) |
| values | `$field` of the clicked row (`$orders.id`, or the alias of a query output), `$params.name` of the document (it must be declared), or a literal (a string, a number, a boolean) |
| `as` | `navigate` (default), `dialog` or `panel`: **the shell presents it**; a widget only sends the event, the shell opens the target in its own frame with the values in the route |
| `then` | what re-runs when a dialog or panel closes or saves: `refresh` (the source widget's queries; the default for a form), `none` (the default for the others) or a list of widgets of the document |
| `lock` | a form only: fields that `new` or `edit` gave and the form shows read-only. **A convenience of the screen, never a rule**: a prefilled value is a default, the form validates and the Store enforces its rules as the viewer |
| `print`, `image` | what a PDF (`print`) and a PNG snapshot (`image`, defaults to `print`) draw: `hidden`, `normal` (as on screen, no link behaviour) or `link` (a PDF hyperlink to the target's address; **the address is never printed as text**). In a PNG `link` is drawn as `normal`. Left out, the element's meaning decides: an element that shows a value (text, a status icon chosen by a field's value) is `normal`, a pure affordance (a pencil, an "open" arrow) is `hidden`; nothing is `link` unless written |
| `label` | the text of the affordance |

**Publish checks.** Everything above is checked where the document is read. What needs the app is checked when the loader is given the `targets` of the app (`dashboard:name`, `report:name`, `view:name`, `page:name`, `form:name`, each with its parameters or, for a form, its fields and its key as required; `app validate` makes them, DBR13): the target exists in the app (`DBD-VIEW-039`), every name in `with`, `new`, `edit` and `lock` is the target's own (`DBD-VIEW-044`), each value fits the type of the parameter or field (`DBD-VIEW-045`: the same type, an integer for a number, a string for a date, a list only for a list; a `$field` of the row is not checked, its type is the catalog's), a required parameter (an edit form's key) that is not given is an error naming the link (`DBD-VIEW-037`), and `print: link` where the target has no address of its own (`as` is `dialog` or `panel`) is refused (`DBD-VIEW-046`). A widget names only links the document declares (`DBD-VIEW-036`).

In TypeScript the same checks are `checkLinks` of `@dbdatakit/visuals`, with the pure parts a host and the exporter use: `describeLink`, `linkValues(link, row, params)` (what a click carries; a value that has nothing is left out, never the text `undefined`), `linkQuery` (the values as the route's query string) and `printTreatment(link, meaning, medium)`. The refusals are cases of `conformance/views`, run by both languages. A report's links are read the same way; what the viewer's shell does with `as` and `then` is [below](#the-shells-dialog-and-panel-dbr36), and what the exporter draws for `print` is DBR37.

## Interaction: cross-filter, drill, refresh and kiosk

Pure logic is in `@dbdatakit/visuals` (`src/dashboard/interaction.ts`, `refresh.ts`), the host's part (the address, the way back, the chrome) in the console's Views page (`viewsUrl.ts`, `DashboardsPanel.tsx`).

**Cross-filter (`on_select: {set: region, from: customers.region}`)** on a `chart` or a `matrix`: pointing at a mark (a chart's bar, a matrix's row label or cell) sets the declared parameter exactly as a `control` would, so the address
carries it (`?p.region=West`) and only the widgets that use `region` are resolved again (`dependentWidgets`, the client's `only`). The value is the pointed-at row's `from` field read as the parameter's type (a chart's datum
is read by `customers.region` or its chart spelling `customers_region`; a matrix's row label by its position in the `rows` encoding, a cell also by `columns`; a total row or column stands for no one value and selects nothing).
A plain click sets the parameter (a list of one for a `many` parameter); clicking the one chosen value again, or the **Clear** button of the chip in the tile, puts the parameter back to its default; shift, control or command
adds a value to a `many` list or takes it out. The tile shows what it has chosen ("Region: West") whatever layout draws it; the selection is the parameter, so it survives a switch between a reflow and a canvas layout. A table has no
`on_select` (the loader refuses it): its rows can only drill. A row with no value for `from`, or a value that is not the parameter's type, selects nothing and says why in the tile area.

**Drill (`drill: {to: {dashboard: name}, with: {region: $customers.region, year: $params.year, tag: fixed}}`)** on a chart, matrix or table: `with` is `$field` of the pointed-at row, `$params.name` (a parameter of this document, its value now) or a literal.
A click drills where the widget has no `on_select`; where it has both, a click selects and **Alt+click drills**. A table row is focusable and Enter drills. The host is given the target and the values (`onDrill`); without a host that
follows drills nothing is clickable for it. The console opens the target, **refuses** a drill that does not give a parameter the target requires (and has no default for), or that gives one the target does not declare
(`checkDrill`; the refusal names them and the board stays), and goes to the target's address: its own `?p.<name>=` values, `trail=` entries for the way back and `mode=kiosk` kept. The page shows the way back as a
breadcrumb (`Back`, and each board left, restoring it as it was left: layout, parameters, selection). A drill to a `view` is not followed yet; a drill to a report opens its screen view when reports are drawn.

**Refresh (`refresh: {every: 5m}`)** re-resolves every placed widget of the shown layout at that interval and says "as of hh:mm" in each data tile. Limits: never below the **floor** (15 s, the server's setting; a shorter `every` is
raised to it); up to 10 % is added at random to each wait (never subtracted); **nothing is requested while the page is hidden**, and when it is shown again a refresh that came due is made at once; a refresh never starts while one
is running; a **failed refresh keeps the rows shown** and marks each tile "stale" until one succeeds. The clock and the page's visibility are injected (`clock`, `visibility`), so tests use `fakeClock` and no real time; in a browser
test, Playwright's clock drives the page's own. The interval starts again when the layout changes.

**Kiosk (`kiosk: {layout: wall}`, address `?mode=kiosk`)**: the board alone in the whole window, none of the console's chrome (no rail, header or toolbar), the document's kiosk layout (`?layout=` still wins; a window-size selector
does not), no maximize action, refresh and cross-filter as usual. A canvas layout (a wall) is scaled as one picture to the window. In a corner, faint until the pointer or the keyboard comes near, are **Exit kiosk** (Escape does the same)
and, after a drill, **Back**. The toolbar's Kiosk button turns it on. Rotating several boards (`?rotate=a,b&every=60`) is a host feature not built yet.

## Custom widgets on screen (plan 32 section 9f, DBR31)

A `custom` widget is drawn by **code of the app** in a frame the host makes (`<iframe sandbox="allow-scripts">`: an opaque origin, no cookies, no storage, no reach into the page; the page loads from the app's own built files). The code never fetches. The host:

1. **resolves the widget as the viewer.** `WidgetResolver` runs the queries the app's manifest entry declares for the component (`views:` entry with `queries:`; a `semantic` request or a Store read) through the same serving core every widget uses, so access, limits and audit are the host's. The widget's `params` (a literal or `$params.name`) bind the request's `{param: name}` markers as a standard widget's do (a filter whose marker has no value drops out). The result is `{type: custom, component, params, data: {<query>: {columns, rows}}}` (`rendered-widgets` schema); two viewers with different access get different rows. A query that fails (denied, over a limit) makes the whole widget an error entry with the core's own code and words, naming the query ("The query `by_region` of `sales-portal/RegionGrid`: ..."); the other widgets still draw. A component the host serves no manifest for, or a `page` placed as a widget, is `custom_not_served`. `ResolverOptions.CustomViews` is how the host that serves the app gives the manifest entry (`component` to `CustomView`).
2. **passes the rows into the frame** (`views:init`, then `views:props` whenever the rows, the parameters, the size or the theme change). The theme is `--sui-*` tokens made from the chart palette.
3. **answers what the frame asks, by the document's rules.** `readToHost` reads only the SDK's shapes (`views:hello`, `views:ready`, `views:error`, `views:size`, `views:setParam`, `views:open`) and only from this frame's window. `setParam` is a **cross-filter**: it must name a parameter the document declares, of its type (otherwise a notice and nothing changes), and it changes the parameter exactly as a control does, so only the widgets that use it resolve again (a custom widget uses a parameter through its `params`). `open(link, row)` must name a link in the widget's `links` list; the host resolves the target and the values (`linkValues`, the same rule as for a standard widget) and gives them to the page's `onLink` handler.
4. **shows the failure.** A view that sends `views:error`, a render error in it (the SDK's frame runtime turns it into `views:error`), or a frame that sends no `views:hello` within 15 s draws an **error tile**; nothing else on the page is touched.

In TypeScript: `DashboardView` takes `customViews` (a `CustomViewsHost`: `frameSrc(component)` is the address of the app's page that mounts views, `route(link)` the address of a link's target), `onLink` and `closedLink` (a dialog the widget opened closed; the widget is told with `views:closed`). Without `customViews` a custom widget draws "Drawn by the app that owns it". In the app, the entry calls `startViewFrame()` of `@dbdatakit/views-sdk` after its `defineWidget` calls: in a document whose address fragment is `#view=<Export>&mode=<screen|print|image>` it waits for the host's `views:init`, mounts the registered component and returns true (the entry must then not mount the app's own screens); anywhere else it does nothing and returns false.

**What the host that serves the app must add** (the apps host, plan 26; not in this module): the address of the built page the frame loads (`CustomViewsHost.frameSrc`) with the app's own policy (the frame is served from the app's files, `default-src 'self'`), a `ResolverOptions.CustomViews` that finds the `CustomView` of the app instance's manifest, and the view's route for `route`. Run a custom view from the Workspace's preview the same way.

### Custom views in the export (DBR32)

The print page serves the app's built files **from the exporter's one-shot origin** (`http://print.localhost/`, in memory, no network; a request to anything else is blocked and named in the export's warnings), so a custom view prints from the same bytes the app was approved with.

- **A widget** in a dashboard or report is drawn in its box by the same sandboxed frame as on screen, in the `print` mode. `ViewsOptions.CustomApps` (an `ICustomViewApps`: the manifest entry of a component, and the app's built files and the page that mounts views) is what the apps host gives the module; the module adds the files under `_app/<app>/`, puts the component's page address in the print page's JSON (`custom.frames`), and the **rows are embedded as JSON** in the same block (the rendered widgets, resolved as the person who exports). The page is drawn when every frame has called `ready()` (it waits for `document.fonts.ready` first, so page breaks are those of the fonts) and the SDK sends the content's final height with it. The export is **refused, naming the widget**, when its query failed, its code called `fail(...)` or threw, it never said it was ready (30 s), the host has no files for its component, or **its content is taller than a page** (`The custom widget `tall` (...) is taller than a page: its content is 6000px high and a page holds 900px`). Chromium fragments an iframe's box across pages instead of keeping it whole (measured, `CustomViewExportTests`), so a widget taller than a page can only be printed cut; make it shorter or make it a `page`.
- **A page** is the top-level document: `CustomViewPrint.Page(view, files, resolved, paper, title, fonts)` takes the app's page, embeds `{name, props}` as JSON (`<script id="dbdatakit-view-init" type="application/json">`; `<` is escaped, so a value cannot close the block), adds the paper's `@page` rule from the manifest's `page: {size, orientation}` (A4 portrait with 20 mm and 15 mm margins by default) and the bundled fonts, and serves the app's files at the origin's root. `startViewFrame()` of the SDK finds the block and mounts the view at once with no host (`views:*` messages go nowhere); `<PageBreak/>` is `break-after: page`; Chromium paginates it like any document (three sections with `PageBreak` are three pages). `WidgetResolver.ResolveCustomViewAsync` gives a page its rows as the viewer; the route that serves and exports a page is DBR33.
- **Fonts and requests.** The bundled families are on the page (`@font-face`); an app's own font files are among its files and load from the origin; a font that falls back to a host font prints with the host's (the export warns only for fonts the administrator did not list). A frame's files are served with `Access-Control-Allow-Origin: *` (an opaque-origin frame loads module scripts with CORS).

## The shell's dialog and panel (DBR36)

A link whose `as` is `dialog` or `panel` opens its target **inside the page**, in a frame of its own, instead of navigating. `DashboardView` takes a `linkHost` (`route(link)`: the address of the link's target inside the app, from the apps host that knows its pages and forms; `navigate(address, event)`: how a navigating link goes there). With it:

- **Following a link.** A click on a mark, row or cell of a widget with an `on_click` (a widget with an `on_select` too selects on a click and follows the link on Alt+click, as for a `drill`), or a custom widget's `open(link, row)` for a link it lists, reads the link, takes its values from the row (`linkValues`) and opens the target at `route?values&_lock=<names>`: the link's values in the query string, sorted, and the field names its `lock` lists. A target with no address in the host is a notice, not a dead click. A host that wants to open links itself passes `onLink` (the event has the link, the values and the address) and the built-in surface is not used.
- **A dialog** (`role="dialog"`, `aria-modal`) is modal: the page behind it is `inert`, a scrim and a Close button close it, Escape closes it. **A panel** (`role="complementary"`) is a side sheet that leaves the page usable. Each holds the target in a **sandboxed frame of its own** (`sandbox="allow-scripts allow-forms"`: an opaque origin, no cookies of the page, no reach into it); focus moves to the Close button when it opens and **returns to the element that had it** (a custom widget's frame included) when it closes.
- **What comes back.** The target frame says `{type: 'views:saved'}` when its write was accepted (the surface closes, and remembers it saved) or `{type: 'views:close'}`; only those two shapes, only from that frame's window. **`then`**: after a save `then: refresh` (the default of a form) resolves the widget the click came from again, `then: [widgets]` those, `none` nothing; a close without a save never refreshes. A custom widget is told with `views:closed` (`{link, saved}`, SDK `useClosed`).
- **Locked fields.** `lock: [status]` puts `_lock=status` in the target's address: the target's form shows those fields read-only (the apps host's form view reads `_lock`). It is the form's courtesy and never the authority: the Store still refuses a value that was not allowed to change, however the request was made.

**What the apps host must add** (session A's `AppShell`, `ViewsApp`; not in this module): the `route` for each target kind (`page`, `form`, `view`, `dashboard`, `report`) in the instance, a form view that reads the query's values as its starting values and `_lock`, and its `views:saved` / `views:close` messages to the parent page when a write was accepted or the person cancels; and the page must let its own frames be framed by the app's pages (`frame-src 'self'`).
