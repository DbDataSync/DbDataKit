import type { PageGeometry } from './page'

/**
 * The theme font stack of a report (plan 32 section 4.6): the host fonts the console allows, then the bundled `DbDataKit` families (the exporter serves them with `@font-face` from its own origin), then the generic
 * family. The bundled files are not in the repository yet (DBR16): until they are, the second group names a family nothing defines and the stack falls through to the generic one.
 */
export const FLOW_FONT_STACK = "system-ui, -apple-system, 'Segoe UI', 'DbDataKit Sans', sans-serif"

/** The vertical space between blocks, in CSS pixels: the screen view and the estimate share it. */
export const BLOCK_GAP = 12

/**
 * The paged-media stylesheet of a flow layout (plan 32 section 4.1): the page, the breaks, the table rules, the print rules. One string used by the screen view (so the browser's own Print works on it) and, later,
 * by the exporter (so both draw the same HTML). Class names are the view's own (`vz-flow-*`); nothing in it depends on the host page.
 */
export function flowCss(page: PageGeometry, fontStack: string = FLOW_FONT_STACK): string {
  const m = page.margins
  return `
@page { size: ${page.cssSize}; margin: ${m.top}mm ${m.right}mm ${m.bottom}mm ${m.left}mm; }
.vz.vz-flow-outer { font-family: ${fontStack}; }
.vz-flow-item { margin: 0 0 ${BLOCK_GAP}px; }
.vz-flow-section[data-break-before] { break-before: page; }
.vz-flow-section[data-break-after] { break-after: page; }
.vz-flow-title { break-after: avoid; margin: 0 0 ${BLOCK_GAP}px; font-size: 18px; }
.vz-flow-item[data-keep] { break-inside: avoid; }
.vz-flow-break { break-after: page; height: 0; margin: 0; }
.vz-flow-cover { break-after: page; }
.vz-flow-item .vz-text p, .vz-flow-item .vz-text li { orphans: 2; widows: 2; }
.vz-flow .vz-widget { display: block; height: auto; overflow: visible; }
.vz-flow .vz-widget-body { overflow: visible; }
.vz-flow-item[data-fixed] > .vz-widget { display: flex; height: 100%; }
.vz-flow table { table-layout: fixed; width: 100%; }
.vz-flow .vz-table-widget th, .vz-flow .vz-table-widget td { height: auto; min-height: 28px; white-space: normal; overflow-wrap: anywhere; }
.vz-flow table thead { display: table-header-group; }
.vz-flow [data-no-repeat] table thead { display: table-row-group; }
.vz-flow table tfoot { display: table-row-group; }
.vz-flow table tr { break-inside: avoid; }
.vz-flow-row { display: grid; grid-template-columns: repeat(12, minmax(0, 1fr)); gap: ${BLOCK_GAP}px; }
@media print {
  /* only the report prints: whatever is not it, not inside it and not one of its ancestors goes; the ancestors lose their fixed heights and scrolling so the pages can run on */
  body *:not(.vz-flow-outer):not(.vz-flow-outer *):not(:has(.vz-flow-outer)) { display: none !important; }
  html, body, *:has(.vz-flow-outer) { display: block !important; position: static !important; width: auto !important; height: auto !important; min-height: 0 !important; max-height: none !important; overflow: visible !important; padding: 0 !important; margin: 0 !important; border: 0 !important; transform: none !important; }
  .vz-flow-outer { display: block !important; position: static !important; width: auto !important; height: auto !important; overflow: visible !important; background: none; }
  .vz-flow-scroll { overflow: visible !important; height: auto !important; padding: 0 !important; }
  .vz-flow-fit { width: auto !important; height: auto !important; margin: 0 !important; }
  .vz-flow-sheet { transform: none !important; width: auto !important; padding: 0 !important; box-shadow: none !important; background: none !important; min-height: 0 !important; }
  .vz-flow-guide, .vz-flow-bar, .vz-flow-running, .vz-flow-note { display: none !important; }
}
`.trim()
}
