import type { RenderedChart } from '../types'

/**
 * The nested axis of a rendered chart (plan 32 section 9g, DBR42, the client half). A chart whose `x` lists several attributes is drawn by the renderer as a chart whose inner level is `x` and whose outer level is the
 * `column` channel (a facet the renderer itself writes: an author's `facet` stays refused), and it says so with `nested_axis: true`. This turns that column facet into group headers: zero spacing between the groups,
 * the headers **below** the inner labels, and a thin edge between groups, so the outer level reads as a second row of axis labels. Like `applyChartFormats` it works on a spec that has already passed `validateRenderedView`,
 * so the keys it adds (`spacing`, the header's orientation, the view's stroke) are the renderer's and never part of a document. A spec with no `column`, and a line or an area mark (a line breaks between groups: the
 * server refuses a nested axis for them), are returned unchanged.
 */
type Json = Record<string, any>

const NESTABLE = new Set(['bar', 'point'])

export function applyNestedAxis(spec: Record<string, unknown>, nested: RenderedChart['nested_axis']): Record<string, unknown> {
  if (nested !== true) return spec
  const copy = structuredClone(spec) as Json
  const mark = typeof copy.mark === 'string' ? copy.mark : copy.mark?.type
  const column: Json | undefined = copy.encoding?.column
  if (!NESTABLE.has(mark) || typeof column !== 'object' || column === null) return spec
  if (column.header === null) return spec
  column.header = { ...(column.header as Json | undefined), orient: 'bottom', labelOrient: 'bottom', titleOrient: 'bottom', labelPadding: 4 }
  copy.spacing = 0
  // each group draws only its own inner members (months of 2025 beside months of 2026), in the order the renderer gave the whole axis
  copy.resolve = { ...(copy.resolve as Json | undefined), scale: { ...((copy.resolve as Json | undefined)?.scale as Json | undefined), x: 'independent' } }
  copy.config = { ...(copy.config as Json | undefined), view: { ...((copy.config as Json | undefined)?.view as Json | undefined), stroke: '#8884' } }
  return copy
}
