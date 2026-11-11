import type { DataValue } from '../types'
import type { Box } from '../views/model'
import type { DensityClass } from './density'

type Json = Record<string, any>

/**
 * A chart spec drawn **at a box** (plan 32 section 3.3 item 3): the renderer sets the size and `autosize: fit`, and the spec's own `width` and `height` are ignored in a dashboard. Density takes away,
 * as the box shrinks: the legend (`m`), then the axis titles (`s`), then it thins the ticks (`xs`); a layout's `legend: show | hide` pins the legend. A chart with `column` or `row` facets cannot fit as a
 * whole (Vega-Lite refuses `fit` with facets), so each cell gets its share of the box. The spec has passed `validateRenderedView`; the changes are the renderer's and never part of a document.
 */
export function fitChartSpec(spec: Record<string, unknown>, box: Box, density: DensityClass, legend?: 'show' | 'hide'): Record<string, unknown> {
  const copy = structuredClone(spec) as Json
  const encoding: Json = copy.encoding ?? {}
  const hideLegend = legend ? legend === 'hide' : density !== 'l'
  const dropTitles = density === 's' || density === 'xs'
  const thin = density === 'xs'
  if (hideLegend && encoding.color && typeof encoding.color === 'object') encoding.color.legend = null
  for (const channel of ['x', 'y']) {
    const c = encoding[channel]
    if (!c || typeof c !== 'object' || c.axis === null) continue
    if (dropTitles || thin) c.axis = { ...c.axis, ...(dropTitles ? { title: null } : {}), ...(thin ? { tickCount: 3, labelOverlap: 'parity' } : {}) }
  }
  const values = (copy.data?.values ?? []) as Array<Record<string, DataValue>>
  const cells = (channel: 'column' | 'row') => {
    const field = encoding[channel]?.field
    return typeof field === 'string' ? Math.max(1, new Set(values.map((v) => v[field])).size) : 0
  }
  const columns = cells('column')
  const rows = cells('row')
  const width = Math.max(1, Math.floor(box.width))
  const height = Math.max(1, Math.floor(box.height))
  if (columns > 0 || rows > 0) {
    copy.width = Math.max(40, Math.floor((width - 80) / Math.max(1, columns)))
    copy.height = Math.max(40, Math.floor((height - 60) / Math.max(1, rows)))
    delete copy.autosize
  } else {
    copy.width = width
    copy.height = height
    copy.autosize = { type: 'fit', contains: 'padding' }
  }
  return copy
}
