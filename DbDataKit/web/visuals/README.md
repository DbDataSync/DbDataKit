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
