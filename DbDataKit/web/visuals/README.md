# @dbdatakit/visuals

The shared renderer of rendered views (plan 19, Y01 to Y03): the view schema and its validator, the matrix grid, the table and Vega-Lite charts, in the console and in anything else that shows a view.

## Rendering without a browser (plan 19, Y05)

`chartToSvg` (`@dbdatakit/visuals/svg`) draws a chart to SVG text with **no browser**: the same Vega-Lite compile, interpreter and refusing loader the component uses, drawn by Vega's own headless renderer (`renderer: 'none'`, then `View.toSVG()`). The component tests snapshot it (`src/__snapshots__/chart-light.svg`, `chart-dark.svg`), so a fixture is rendered to SVG in CI on every run of `npm test -w @dbdatakit/visuals`, and a server that needs a chart as an image (a report, an email, an agent's answer) calls it in a Node process.

**Decided (the spike was Y02's own work): this is the path; headless Chromium is not used.** It is deterministic (the same spec and data give the same text), has no browser to install, start or keep alive, and reaches the same limits (the refusing loader: no URL, no custom expression) as the component.

What it gives up, and when that matters:

- **Text width is an estimate.** Without a canvas Vega measures text with a rule of thumb (`vega-scenegraph`'s `estimateWidth`), not the font's own metrics, so a label can sit a few pixels off where a browser would put it, and a long label may be cut or overlap where a browser would not. It does not matter for a chart read on a screen or in an email; it matters for a pixel-faithful print.
- **No PNG or PDF.** The output is SVG. A client that needs a raster draws the SVG itself.

Headless Chromium would be needed only for a pixel-faithful raster or PDF with real font metrics. If that is asked for, add it as a second renderer behind the same function, and compare it with these snapshots; do not replace this one.
