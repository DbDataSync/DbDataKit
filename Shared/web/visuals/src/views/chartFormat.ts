import type { RenderedChart } from '../types'

/**
 * The channel formats of a rendered chart (plan 32 section 9g, DBR41). The server's renderer writes, for each quantitative channel that has a `format`, a d3-format string it made from the format's own closed
 * vocabulary (`ViewFormat.D3Number` in `DbDataKit.Views`); a categorical channel's labels are already in the data. This gives each string to the Vega-Lite spec as the axis, legend, header or tooltip format.
 * The spec has passed `validateRenderedView`, which is closed, so the keys added here (`axis.format` and the like) are the renderer's and never part of a document; and a string is used only when it
 * matches the one fixed pattern below, so nothing a server sends can reach Vega as an expression.
 */

/** What `D3Number` can write: an optional `$`, an optional `,`, a precision of one or two digits, an optional `~` and `f`, `s` or `%`. */
export const D3_FORMAT = /^\$?,?\.[0-9]{1,2}~?[fs%]$/

type Json = Record<string, any>
export type ChartFormats = NonNullable<RenderedChart['formats']>

/** A copy of the spec with each valid format set on its channel; an invalid string, and a channel the spec does not have, are left alone. */
export function applyChartFormats(spec: Record<string, unknown>, formats: ChartFormats): Record<string, unknown> {
  const copy = structuredClone(spec) as Json
  const encoding: Json | undefined = copy.encoding
  if (!encoding) return copy
  const valid = (f: unknown): f is string => typeof f === 'string' && D3_FORMAT.test(f)
  const set = (def: unknown, key: 'axis' | 'legend' | 'header', f: string) => {
    if (typeof def !== 'object' || def === null) return
    const d = def as Json
    if (d[key] === null) return // an axis or a legend that is off stays off
    d[key] = { ...(d[key] as Json | undefined), format: f }
  }
  for (const channel of ['x', 'y'] as const) {
    const f = formats[channel]
    if (valid(f)) set(encoding[channel], 'axis', f)
  }
  const color = formats.color
  if (valid(color)) set(encoding.color, 'legend', color)
  for (const channel of ['column', 'row'] as const) {
    const f = formats[channel]
    if (valid(f)) set(encoding[channel], 'header', f)
  }
  const tips = formats.tooltip
  if (Array.isArray(tips)) {
    const defs: unknown[] = Array.isArray(encoding.tooltip) ? encoding.tooltip : encoding.tooltip ? [encoding.tooltip] : []
    tips.forEach((f, i) => {
      const def = defs[i]
      if (valid(f) && typeof def === 'object' && def !== null) (def as Json).format = f
    })
  }
  return copy
}
