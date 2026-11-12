/**
 * When a print page is drawn: the fonts are loaded, no widget is busy, every widget the layout placed is on the page and every chart has its SVG (or says why not), and the picture has not changed for a few frames.
 * Polled on animation frames, no timer: the exporter's deadline is what ends a page that never gets there.
 */
export interface DrawnOptions { widgets: number; stableFrames?: number }

const frame = () => new Promise<void>((resolve) => requestAnimationFrame(() => resolve()))

export function isDrawn(root: ParentNode, expected: number): boolean {
  if (root.querySelector('[aria-busy="true"]')) return false
  // a custom widget's frame is drawn when its code says it is ready (or has failed: the page reports it)
  if (root.querySelector('[data-testid^="vz-custom-frame-"][data-state="loading"]')) return false
  if (root.querySelectorAll('[data-testid^="vz-widget-"]').length < expected) return false
  for (const chart of Array.from(root.querySelectorAll('[data-testid^="vz-chart-"]'))) {
    const drawn = chart.querySelector('svg') !== null || chart.querySelector('[role="alert"]') !== null
    if (!drawn) return false
  }
  return true
}

export async function whenDrawn(root: ParentNode, options: DrawnOptions): Promise<void> {
  await document.fonts.ready
  const needed = options.stableFrames ?? 3
  let stable = 0
  let last = ''
  for (;;) {
    await frame()
    const html = (root as Element).innerHTML
    if (isDrawn(root, options.widgets) && html === last) stable += 1
    else stable = 0
    last = html
    if (stable >= needed) break
  }
  await document.fonts.ready
}
