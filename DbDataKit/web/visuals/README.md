# @dbdatakit/visuals

The shared renderer of rendered views (plan 19, Y01 to Y03): the view schema and its validator, the matrix grid, the table and Vega-Lite charts, in the console and in anything else that shows a view.

## Rendering without a browser (plan 19, Y05)

`chartToSvg` (`@dbdatakit/visuals/svg`) draws a chart to SVG text with **no browser**: the same Vega-Lite compile, interpreter and refusing loader the component uses, drawn by Vega's own headless renderer (`renderer: 'none'`, then `View.toSVG()`). The component tests snapshot it (`src/__snapshots__/chart-light.svg`, `chart-dark.svg`), so a fixture is rendered to SVG in CI on every run of `npm test -w @dbdatakit/visuals`, and a server that needs a chart as an image (a report, an email, an agent's answer) calls it in a Node process.

**Decided (the spike was Y02's own work): this is the path; headless Chromium is not used.** It is deterministic (the same spec and data give the same text), has no browser to install, start or keep alive, and reaches the same limits (the refusing loader: no URL, no custom expression) as the component.

What it gives up, and when that matters:

- **Text width is an estimate.** Without a canvas Vega measures text with a rule of thumb (`vega-scenegraph`'s `estimateWidth`), not the font's own metrics, so a label can sit a few pixels off where a browser would put it, and a long label may be cut or overlap where a browser would not. It does not matter for a chart read on a screen or in an email; it matters for a pixel-faithful print.
- **No PNG or PDF.** The output is SVG. A client that needs a raster draws the SVG itself.

Headless Chromium would be needed only for a pixel-faithful raster or PDF with real font metrics. If that is asked for, add it as a second renderer behind the same function, and compare it with these snapshots; do not replace this one.

## Dashboards and reports (plan 32)

`validateWidgets(widgets, parameters)` is the shape check of the widget vocabulary of `dashboard/1` and `report/1`, from `schemas/core.schema.json` (the widget types, encodings and query forms; the matrix and chart
encodings are `view.schema.json`'s and the Vega-Lite subset's own definitions, referenced) with the codes and paths of `DbDataKit.Views` in .NET. It refuses an image by URL or an SVG, raw HTML in text, any
computation in a chart (`transform`, `aggregate`, `bin`, `timeUnit`, an expression), an encoded field that is not an output of an explicit query, an unknown key and the reserved `embed`. The documents and the expected
refusals are `DbDataKit/conformance/views/widgets`, which the .NET suite runs too. The file format is in [`docs/views.md`](../../../docs/views.md).

`validateDashboard`, `validateReport` and `validateViewsDocument` check a whole document: the same widget checks plus the layouts (one default that places every widget, tiles inside the grid or canvas and not
overlapping, minimums at scale 1, page sizes and tokens, a declared parameter everywhere) and the rules of the kind. They return issues with a code and a **path** (`layouts[1].tiles[0].x`); the .NET loaders also give
file, line and column. `VIEWS_FORMAT` is the one number of both kinds.

`selectLayout(document, context)` picks the layout to show: a named layout, else the most specific `when` that matches (ties by order), else the default; the same pure function as `LayoutSelector` in .NET, with the same goldens.

### Showing a document (plan 32 DBR05, DBR06, DBR22)

```tsx
const client = fixtureClient(bundle)                         // or moduleClient({ baseUrl }) once the views module exists
const { document } = await client.open('dashboard', 'retail-sales-today')
<DashboardView name="retail-sales-today" document={document!} client={client} />   // fills its parent, follows its size
```

* `ViewsClient` is the one path for boards: `list`, `open`, `resolve(ref, layout, parameters, {only})` (the rendered widgets of a layout, as the viewer), `validate`, `save`, `preview`. `fixtureClient` serves documents and
  **recorded results** (`DbDataKit/conformance/views/widget-results/<kind>/<document>/<widget id>.json`, for the two sample documents of the retail template) with no server; a widget with no recorded result gets its own
  error entry. `moduleClient` is the same over the `views` module's REST (provisional routes are marked in its header). `validateText` reads YAML or JSON and places each diagnostic at its line and column.
* `WidgetView` draws one widget in its tile from its resolved result: `MetricTile`, `TableWidget`, `MatrixWidget`, `ChartWidget`, `TextBlock`, `ImageBlock`, `ControlWidget`, with loading, empty and error states.
  The tile's **density class** (`xs`, `s`, `m`, `l`) is chosen from its box (`densityClass`); a chart is drawn at the box (`fitChartSpec`: SVG, `autosize: fit`, legend, axis titles and ticks dropped as it shrinks), a table or
  matrix shows what fits and says "12 of 40 rows" with an action that maximizes the tile. Text is the safe markdown subset (no HTML, no image, links only to a path on the host); an image is drawn only from the `data:`
  URI of its raster (`checkImageBytes` caps it at 1 MiB where it enters the definition).
* `layoutReflow(layout, widgets, box)` is pure: a grid of fr tracks filling the box in whole pixels, each tile held to its type's minimum, the **compact stack** when one is below it. `DashboardFrame` draws that as
  markup (no state, any size can be tested); `DashboardView` adds the container's size (`ResizeObserver`), `selectLayout`, resolution through the client (switching layouts keeps the results both place) and maximize.
  `checkReflowSizes(document)` is the validator's size check as warnings (not part of `validateViewsDocument` until the .NET loader gives the same). The canvas and flow layouts are DBR07 and DBR14: until then a note says so.
