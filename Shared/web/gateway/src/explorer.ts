import type { RenderedChart, RenderedMatrix, RenderedViewDocument } from '@dbdatakit/visuals'
import type { RunResult } from './runTypes'
import type { SemanticCatalogView } from './types'

/** What the palette holds: the members a person picked from the model's catalog, and how many rows to ask for. */
export interface Picks { attributes: string[]; measures: string[]; limit: number }

export const DEFAULT_LIMIT = 100
export const MAX_LIMIT = 10_000

/** The request a palette builds: the envelope of `docs/semantic/query.md`, attributes first, then measures. The typed JSON of the same picks is this object, so the two never differ (tested). */
export function requestFromPicks(p: Picks): { spec_version: string; kind: 'query'; query: { attributes: string[]; measures: string[]; limit: number } } {
  return { spec_version: '0.3', kind: 'query', query: { attributes: [...p.attributes], measures: [...p.measures], limit: clampLimit(p.limit) } }
}

export const requestText = (p: Picks) => JSON.stringify(requestFromPicks(p), null, 2)

export function clampLimit(n: number): number {
  if (!Number.isFinite(n)) return DEFAULT_LIMIT
  return Math.min(MAX_LIMIT, Math.max(1, Math.trunc(n)))
}

/** The semantic SQL of the same picks, as the Gateway's own dialect writes it (`SELECT attributes, MEASURE(measure) FROM model GROUP BY ALL LIMIT n`). */
export function sqlFromPicks(model: string, p: Picks): string {
  const columns = [...p.attributes, ...p.measures.map((m) => `MEASURE(${m})`)]
  return `SELECT ${columns.length > 0 ? columns.join(', ') : '*'} FROM ${model}${p.attributes.length > 0 ? ' GROUP BY ALL' : ''} LIMIT ${clampLimit(p.limit)}`
}

/** Adds or removes a member from the picks, keeping the order they were picked in. */
export function toggle(p: Picks, kind: 'attributes' | 'measures', name: string): Picks {
  const has = p[kind].includes(name)
  return { ...p, [kind]: has ? p[kind].filter((n) => n !== name) : [...p[kind], name] }
}

/** The picks a request holds, when it is a plain palette request (names only); null when it is anything the palette cannot show (a filter, an override, a transform). */
export function picksOf(text: string): Picks | null {
  let doc: unknown
  try { doc = JSON.parse(text) } catch { return null }
  const query = (doc as { query?: Record<string, unknown> } | null)?.query
  if (!query || typeof query !== 'object') return null
  const names = (v: unknown) => (Array.isArray(v) ? v : [])
  const attributes = names(query.attributes).filter((a): a is string => typeof a === 'string')
  const measures = names(query.measures).filter((m): m is string => typeof m === 'string')
  const extra = Object.keys(query).filter((k) => !['attributes', 'measures', 'limit'].includes(k))
  if (extra.length > 0 || attributes.length !== names(query.attributes).length || measures.length !== names(query.measures).length) return null
  return { attributes, measures, limit: typeof query.limit === 'number' ? query.limit : DEFAULT_LIMIT }
}

/** The names of every member the catalog has, for the palette's two lists. */
export function members(view: SemanticCatalogView): { attributes: string[]; measures: string[] } {
  return { attributes: view.dimensions.flatMap((d) => d.attributes.map((a) => a.name)), measures: view.measures.map((m) => m.name) }
}

export type Mode = 'sql' | 'json'

/** What to send for the text in the box: a request when it is a JSON object, semantic SQL otherwise. A text that is neither says so before anything is sent. */
export function bodyFor(model: string, text: string, opts: { explain?: boolean; limit?: number; parameters?: Record<string, unknown> } = {}): { body: Record<string, unknown> } | { problem: string } {
  const trimmed = text.trim()
  if (trimmed === '') return { problem: 'Write semantic SQL or a JSON request, or pick members from the palette.' }
  const common: Record<string, unknown> = { model, ...(opts.explain ? { explain: true } : {}), ...(opts.limit ? { limit: opts.limit } : {}), ...(opts.parameters && Object.keys(opts.parameters).length > 0 ? { parameters: opts.parameters } : {}) }
  if (trimmed.startsWith('{')) {
    try {
      const request = JSON.parse(trimmed)
      if (request === null || typeof request !== 'object' || Array.isArray(request)) return { problem: 'A request is a JSON object.' }
      return { body: { ...common, request } }
    } catch (e) { return { problem: `The request is not valid JSON: ${(e as Error).message}` } }
  }
  return { body: { ...common, sql: trimmed.replace(/;\s*$/, '') } }
}

/** What an explain's rows read as: one line per scan, the source, the engine and the text. */
export function scansOf(result: RunResult): Array<{ source: string; engine: string; text: string }> {
  const at = (name: string) => result.columns.findIndex((c) => c.name === name)
  const [s, e, t] = [at('source'), at('engine'), at('text')]
  return result.rows.map((r) => ({ source: String(r[s] ?? ''), engine: String(r[e] ?? ''), text: String(r[t] ?? '') }))
}

/** The engine's refusal as it should read: its code first, in the engine's own words. */
export function refusal(message: string, code: unknown): string {
  return typeof code === 'string' && code !== '' && !message.startsWith(code) ? `${code}: ${message}` : message
}

// ---------- views of a result ----------

export const ROLLUP_COLUMN = '_rollup_level'

export type ViewShape = 'table' | 'matrix' | 'bar' | 'line'

export interface Encoding { rows: string[]; columns: string[]; value: string }

const isNumber = (type: string) => type === 'integer' || type === 'number'

/** The columns a result can be drawn by: the measures (numbers) and the attributes (the rest). The rollup level the engine adds to a totals request is neither. */
export function roles(result: RunResult): { attributes: string[]; measures: string[]; hasTotals: boolean } {
  const visible = result.columns.filter((c) => c.name !== ROLLUP_COLUMN)
  return { attributes: visible.filter((c) => !isNumber(c.type)).map((c) => c.name), measures: visible.filter((c) => isNumber(c.type)).map((c) => c.name), hasTotals: result.columns.some((c) => c.name === ROLLUP_COLUMN) }
}

/** The encoding a result suggests: the first attribute down the rows, the second across, the first measure as the value. */
export function suggest(result: RunResult): Encoding {
  const r = roles(result)
  return { rows: r.attributes.slice(0, 1), columns: r.attributes.slice(1, 2), value: r.measures[0] ?? '' }
}

/** Why an encoding cannot be drawn from this result, in words, or null. A channel that names a column the result lacks is refused here, as the renderer refuses it. */
export function encodingProblem(result: RunResult, shape: ViewShape, e: Encoding): string | null {
  const names = new Set(result.columns.map((c) => c.name))
  for (const c of [...e.rows, ...e.columns, e.value]) if (c !== '' && !names.has(c)) return `view_unknown_field: '${c}' is not a column of this result.`
  if (e.value === '') return 'view_missing_value: choose the measure to draw.'
  const value = result.columns.find((c) => c.name === e.value)
  if (value && !isNumber(value.type)) return `view_value_not_numeric: '${e.value}' is ${value.type}; a view draws a number.`
  if (shape !== 'table' && e.rows.length === 0) return 'view_missing_rows: choose the attribute to draw along.'
  if (e.rows.includes(e.value) || e.columns.includes(e.value)) return `view_value_in_axis: '${e.value}' cannot be both the measure and a heading.`
  return null
}

const text = (v: unknown): string => (v === null || v === undefined ? '' : String(v))
const format = (v: number): string => (Number.isInteger(v) ? v.toLocaleString('en-US') : v.toLocaleString('en-US', { maximumFractionDigits: 2 }))

/** The rows of a result as objects by column name. */
export function recordsOf(result: RunResult): Array<Record<string, unknown>> {
  return result.rows.map((r) => Object.fromEntries(result.columns.map((c, i) => [c.name, r[i] ?? null])))
}

/**
 * A matrix (`RenderedMatrix`): the rows' attributes down the left, the columns' attributes across, one value per cell. Totals are the rows the engine's own rollup column marks (a level above 0), shown as the
 * row or column that holds them; nothing is added up here. A row of the result whose attributes are all null at a rollup level is the grand total.
 */
export function matrixOf(result: RunResult, e: Encoding, id = 'result'): RenderedMatrix {
  const records = recordsOf(result)
  const rollup = result.columns.some((c) => c.name === ROLLUP_COLUMN)
  const isTotal = (r: Record<string, unknown>) => rollup && Number(r[ROLLUP_COLUMN] ?? 0) > 0
  const labelOf = (r: Record<string, unknown>, names: string[]) => names.map((n) => (r[n] === null || r[n] === undefined ? (isTotal(r) ? 'Total' : '') : text(r[n])))
  const rowKeys: string[] = []
  const rowLabels = new Map<string, { labels: string[]; total: boolean }>()
  const columnKeys: string[] = []
  const columnLabels = new Map<string, { labels: string[]; total: boolean }>()
  const cells = new Map<string, number | null>()
  for (const r of records) {
    // a total over the columns' attributes is a total column; over the rows', a total row
    const total = isTotal(r)
    const rl = labelOf(r, e.rows)
    const cl = e.columns.length > 0 ? labelOf(r, e.columns) : [e.value]
    const rk = rl.join('\u0000') + (total && e.rows.every((n) => r[n] === null) ? '#total' : '')
    const ck = cl.join('\u0000')
    if (!rowLabels.has(rk)) { rowKeys.push(rk); rowLabels.set(rk, { labels: rl, total: total && e.rows.every((n) => r[n] === null || r[n] === undefined) }) }
    if (!columnLabels.has(ck)) { columnKeys.push(ck); columnLabels.set(ck, { labels: cl, total: total && e.columns.length > 0 && e.columns.every((n) => r[n] === null || r[n] === undefined) }) }
    const v = r[e.value]
    cells.set(rk + '\u0001' + ck, typeof v === 'number' ? v : v === null || v === undefined ? null : Number(v))
  }
  // the total row last, the total column last
  const orderedRows = [...rowKeys.filter((k) => !rowLabels.get(k)!.total), ...rowKeys.filter((k) => rowLabels.get(k)!.total)]
  const orderedColumns = [...columnKeys.filter((k) => !columnLabels.get(k)!.total), ...columnKeys.filter((k) => columnLabels.get(k)!.total)]
  return {
    id, type: 'matrix', notice: null,
    columns: orderedColumns.map((k) => ({ labels: columnLabels.get(k)!.labels, ...(columnLabels.get(k)!.total ? { total: true } : {}) })),
    rows: orderedRows.map((rk) => ({
      labels: rowLabels.get(rk)!.labels, ...(rowLabels.get(rk)!.total ? { total: true } : {}),
      cells: orderedColumns.map((ck) => { const v = cells.get(rk + '\u0001' + ck); return v === undefined || v === null ? { v: null, text: '' } : { v, text: format(v) } }),
    })),
  }
}

/**
 * A chart (`RenderedChart`) in the closed Vega-Lite subset: a bar or a line of the value along the first row attribute, coloured by the first column attribute when there is one; encodings only, the rows inline as the
 * spec's data. A totals row is left out (a total is not a bar among its parts).
 */
export function chartOf(result: RunResult, shape: 'bar' | 'line', e: Encoding, id = 'result'): RenderedChart {
  const rollup = result.columns.some((c) => c.name === ROLLUP_COLUMN)
  const x = e.rows[0]!
  const keep = recordsOf(result).filter((r) => !(rollup && Number(r[ROLLUP_COLUMN] ?? 0) > 0))
  const field = (name: string) => ({ field: name, type: isNumber(result.columns.find((c) => c.name === name)?.type ?? '') ? 'quantitative' : 'nominal' })
  const encoding: Record<string, unknown> = { x: { ...field(x), ...(shape === 'bar' ? { sort: null } : {}) }, y: { field: e.value, type: 'quantitative' } }
  if (e.columns[0]) encoding.color = field(e.columns[0])
  const values = keep.map((r) => Object.fromEntries([x, e.value, ...(e.columns[0] ? [e.columns[0]] : [])].map((n) => [n, (r[n] ?? null) as string | number | boolean | null])))
  return { id, type: 'chart', vega_lite: { mark: shape === 'bar' ? 'bar' : 'line', encoding, data: { values } } }
}

/** The document `RenderedViews` draws, from a result and the choice made: a matrix or a chart; a table is drawn by the page itself. */
export function viewDocument(result: RunResult, shape: ViewShape, e: Encoding): RenderedViewDocument | null {
  if (shape === 'table') return null
  return { kind: 'rendered_view', status: 'complete', views: [shape === 'matrix' ? matrixOf(result, e) : chartOf(result, shape, e)] }
}

/** The cell a table shows for a value. */
export const cellText = (v: unknown): string => (typeof v === 'number' ? format(v) : text(v))

/** The link that opens the console's pin panel with this request filled in (the panel reads these keys). */
export function pinLink(model: string, requestJson: string, shape: 'table' | 'chart' | 'metric'): string {
  return `/views?pin_model=${encodeURIComponent(model)}&pin_shape=${shape}&pin_request=${encodeURIComponent(requestJson)}`
}

/** The query body the console's pin panel takes (`{measures, attributes, limit}`), from the statement in the box: the body of a JSON request, or the palette's picks for a statement written as SQL; null when there is nothing to pin. */
export function pinRequest(text: string, picks: Picks): string | null {
  const trimmed = text.trim()
  if (trimmed.startsWith('{')) {
    try {
      const doc = JSON.parse(trimmed) as Record<string, unknown>
      const body = (doc.query && typeof doc.query === 'object' ? doc.query : doc) as Record<string, unknown>
      return Array.isArray(body.measures) && body.measures.length > 0 ? JSON.stringify(body, null, 2) : null
    } catch { return null }
  }
  return picks.measures.length > 0 ? JSON.stringify(requestFromPicks(picks).query, null, 2) : null
}
