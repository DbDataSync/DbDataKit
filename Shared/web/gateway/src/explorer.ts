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

/** The engine's refusal as it should read: its code first, then the path when the Gateway names one (the message already holds it when the engine wrote it in), in the engine's own words (D38 (2)). */
export function refusal(message: string, code: unknown, path?: unknown): string {
  const where = typeof path === 'string' && path !== '' && !message.includes(path) ? ` at ${path}` : ''
  return typeof code === 'string' && code !== '' && !message.startsWith(code) ? `${code}${where}: ${message}` : where === '' ? message : `${where.trim()}: ${message}`
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

const text = (v: unknown): string => (v === null || v === undefined ? '' : String(v))
const format = (v: number): string => (Number.isInteger(v) ? v.toLocaleString('en-US') : v.toLocaleString('en-US', { maximumFractionDigits: 2 }))

/** The view document of the closed subset (`docs/semantic/presentation.md`) that asks the Gateway for a matrix or a chart of a result: only what is chosen here, never a cell. */
export interface ViewSpec { spec_version: '0.3'; kind: 'view'; result: { ref: string }; views: Array<Record<string, unknown>> }

/**
 * The totals a matrix may ask for: a total row when the result has a level that rolled up exactly the row attributes, a total column for the column attributes. The result says which levels it has (`totals`), the Gateway's renderer
 * reads the rows; nothing is added up here. Undefined when the result has none of them (the renderer would refuse a total the result lacks).
 */
export function totalsOf(result: RunResult, e: Encoding): { rows?: true; columns?: true } | undefined {
  const levels = result.totals?.levels.filter((l) => l.level > 0)
  if (!levels || levels.length === 0) return undefined
  const logical = (names: string[]) => new Set(names.map((n) => result.columns.find((c) => c.name === n)?.binding ?? n))
  const rolled = (names: string[]) => names.length > 0 && levels.some((l) => { const want = logical(names); return l.rolled_up.length === want.size && l.rolled_up.every((r) => want.has(r)) })
  const totals = { ...(rolled(e.rows) ? { rows: true as const } : {}), ...(rolled(e.columns) ? { columns: true as const } : {}) }
  return Object.keys(totals).length > 0 ? totals : undefined
}

/**
 * The minimal precheck before the Gateway is asked to draw (plan 14a D38 (2)): the slots a shape cannot do without, named as the page labels them, that are still empty. This is the whole of the client's rules about a view: a matrix needs a Value, a bar
 * or line chart an Along and a Value; whether the choice is right (an attribute not placed, a stacked count, a total the result lacks) is the engine's to say, in its words, with its code and path.
 */
export function missingSlots(shape: ViewShape, e: Encoding): string[] {
  if (shape === 'table') return []
  return [...(shape !== 'matrix' && e.rows.length === 0 ? ['Along'] : []), ...(e.value === '' ? ['Value'] : [])]
}

/**
 * What to send the Gateway's renderer for the shape and encodings chosen: a matrix (rows, columns, the value, the totals the result has) or a bar or line chart (the value along the first row attribute, coloured by the first column
 * attribute) in the closed Vega-Lite subset. A table is drawn by the page itself (null). A choice the result cannot give (an unknown field, a stacked count, totals it lacks) is the renderer's refusal, in its words.
 */
export function viewSpecFor(result: RunResult, shape: ViewShape, e: Encoding): ViewSpec | null {
  if (shape === 'table') return null
  const id = 'result'
  const field = (name: string) => ({ field: name, type: isNumber(result.columns.find((c) => c.name === name)?.type ?? '') ? 'quantitative' : 'nominal' })
  const totals = totalsOf(result, e)
  const view: Record<string, unknown> = shape === 'matrix'
    ? { type: 'matrix', id, rows: e.rows, columns: e.columns, values: e.value === '' ? [] : [{ field: e.value }], ...(totals ? { totals } : {}) }
    : { type: 'chart', id, vega_lite: { mark: shape, encoding: { ...(e.rows[0] ? { x: field(e.rows[0]) } : {}), y: { field: e.value, type: 'quantitative' }, ...(e.columns[0] ? { color: field(e.columns[0]) } : {}) } } }
  return { spec_version: '0.3', kind: 'view', result: { ref: 'result' }, views: [view] }
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
