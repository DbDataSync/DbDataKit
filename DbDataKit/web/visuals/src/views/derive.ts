import { VIEW, isObject, join, at } from './common'
import type { Json, ViewsIssue } from './common'

/**
 * `deriveRequest` (plan 32 section 2.3, DBR03): a widget's encodings, with the catalog's roles, become the semantic request they need. The twin of `DeriveRequest.Derive` in `DbDataKit.Views` (.NET): the two run
 * the same goldens (`DbDataKit/conformance/views/derive`) and give the same request, key for key, so the editor's field wells (DBR25) show exactly what the server will ask.
 *
 * A measure well (a metric's `value` and `compare`, a matrix's `values`) asks for measures; a matrix's `rows` and `columns` for attributes; every other encoded field (a chart's channels, a table's columns) goes to
 * `measures` when the catalog says it is a measure and to `attributes` otherwise. Fields are taken in a fixed order (chart: x, y, color, column, row, tooltip; matrix: rows, columns, values) and each once.
 * `filters`, `segments` and `timezone` are the query's, as written; `order` is the query's, or a table's own `sort`; `limit` is the query's or the widget type's default (metric 1, chart 500, table 100, matrix
 * 10,000), never above 10,000; a matrix's `totals` (and a sort by a measure, which needs the axis total) become the request's `totals`. A chart's channel `sort`, a matrix's `sort` and `column_limit` are the view's.
 */

/** What the catalog says the members of a model are. */
export interface CatalogRoles { model?: string; measures: readonly string[]; attributes: readonly string[] }

export type CatalogRole = 'measure' | 'attribute' | 'unknown'

/** A document parameter that supplies a request's `{param: name}` marker. */
export interface ParameterBinding { request: string; document: string }

/** A widget as the derivation reads it: its id, and the widget as written (a `widgets` map's entry). */
export interface DerivableWidget { id: string; raw: Json }

export interface DerivedRequest {
  /** The semantic request (the envelope of `docs/semantic/query.md`), or undefined when there are diagnostics. */
  request?: Json
  bindings: ParameterBinding[]
  issues: ViewsIssue[]
  /** Where in the document a place of the request comes from: `$.query.measures[1]` is the encoded field that asked for it, `$.query.filters[0].operator` is inside `widgets.w.query.filters[0]`. */
  documentPath(requestPath: string): string
}

export const SPEC_VERSION = '0.3'
export const MAXIMUM_LIMIT = 10_000
const CHART_CHANNELS = ['x', 'y', 'color', 'column', 'row', 'tooltip']
const DEFAULT_LIMITS: Record<string, number> = { metric: 1, chart: 500, table: 100, matrix: 10_000 }

export const defaultLimit = (widgetType: string): number => DEFAULT_LIMITS[widgetType] ?? 100

/** The role of a name in the catalog. */
export function roleOf(roles: CatalogRoles, name: string): CatalogRole {
  return roles.measures.includes(name) ? 'measure' : roles.attributes.includes(name) ? 'attribute' : 'unknown'
}

export type Well = 'either' | 'measure' | 'attribute'

/**
 * What is wrong with a field in a well, in the engine's own words (`Validator.CheckMember`, including its "a attribute"), so the derivation and a field well refuse in one voice: a field the catalog
 * lacks is `unknown_member`; a measure where an attribute goes, or the other way round, is `wrong_member_kind`. An `either` well takes both roles.
 */
export function memberProblem(roles: CatalogRoles, field: string, well: Well): { code: 'unknown_member' | 'wrong_member_kind'; message: string } | null {
  const role = roleOf(roles, field)
  if (role === 'unknown') return { code: 'unknown_member', message: `${field} is not a catalog member.` }
  if (well === 'measure' && role === 'attribute') return { code: 'wrong_member_kind', message: `${field} is a attribute, not a measure.` }
  if (well === 'attribute' && role === 'measure') return { code: 'wrong_member_kind', message: `${field} is a measure, not a attribute.` }
  return null
}
interface Slot { field: string; path: string; well: Well }

const isString = (v: unknown): v is string => typeof v === 'string' && v.length > 0

/** The path of a place of the request normalised the way the engine names it: `$.query.filters[0].attribute`. */
export function normaliseRequestPath(path: string): string {
  if (!path.startsWith('$/')) return path.startsWith('$.') || path === '$' ? path : `$.${path}`
  return path.slice(2).split('/').filter(Boolean).reduce((acc, s) => acc + (/^\d+$/.test(s) ? `[${s}]` : `.${s}`), '$')
}

export function deriveRequest(widget: DerivableWidget, roles: CatalogRoles): DerivedRequest {
  const wp = join('widgets', widget.id)
  const issues: ViewsIssue[] = []
  const origins = new Map<string, string>()
  const issue = (code: string, path: string, message: string) => issues.push({ code, severity: 'error', path, message })
  const documentPath = (requestPath: string): string => {
    const key = normaliseRequestPath(requestPath).slice(2)
    for (let k = key; k.length > 0;) {
      const found = origins.get(k)
      if (found !== undefined) return found + key.slice(k.length)
      const cut = Math.max(k.lastIndexOf('.'), k.lastIndexOf('['))
      k = cut < 0 ? '' : k.slice(0, cut)
    }
    return key.startsWith('query') ? `${wp}.${key}` : `${wp}.query`
  }
  const refused = (): DerivedRequest => ({ bindings: [], issues, documentPath })

  const raw = widget.raw
  const type = raw.type
  if (type !== 'metric' && type !== 'chart' && type !== 'matrix' && type !== 'table') {
    issue(VIEW('040'), wp, `A ${String(type)} widget asks for nothing: it has no query.`)
    return refused()
  }
  const query = isObject(raw.query) ? raw.query : undefined
  const isExplicit = !!query && (query.mode === 'explicit' || ['semantic', 'saved', 'store', 'ref'].some((k) => query[k] !== undefined))
  if (!query || isExplicit) {
    issue(VIEW('040'), join(wp, 'query'), 'This query is written out (`mode: explicit`), so the encodings do not drive it and there is nothing to derive.')
    return refused()
  }

  const encodings = isObject(raw.encodings) ? raw.encodings : {}
  const slots = slotsOf(type, encodings, join(wp, 'encodings'))
  const measures: string[] = []
  const attributes: string[] = []

  const place = (field: string, path: string, well: Well) => {
    const role = roleOf(roles, field)
    const problem = memberProblem(roles, field, well)
    if (problem) issue(problem.code, path, problem.message)
    const toMeasures = well === 'measure' || (well === 'either' && role === 'measure')
    const list = toMeasures ? measures : attributes
    if (list.includes(field)) return
    list.push(field)
    origins.set(`query.${toMeasures ? 'measures' : 'attributes'}[${list.length - 1}]`, path)
  }
  for (const s of slots) place(s.field, s.path, s.well)

  // ---- order: the query's own, else a table's sort ----
  let order: unknown[] | undefined
  if (Array.isArray(query.order) && query.order.length > 0) {
    order = structuredClone(query.order)
    order.forEach((_, i) => origins.set(`query.order[${i}]`, `${wp}.query.order[${i}]`))
  } else if (type === 'table' && isObject(encodings.sort) && isString(encodings.sort.by)) {
    const sortField = encodings.sort.by
    const direction = typeof encodings.sort.dir === 'string' ? encodings.sort.dir : 'asc'
    const sortPath = join(join(wp, 'encodings'), 'sort.by')
    if (!measures.includes(sortField) && !attributes.includes(sortField)) place(sortField, sortPath, 'either')
    order = [[sortField, direction]]
    origins.set('query.order[0]', sortPath)
  }

  // ---- totals ----
  let totals: Json | undefined
  if (type === 'matrix') totals = matrixTotals(encodings, roles, wp, query, issue)
  else if (isObject(query.totals)) {
    totals = structuredClone(query.totals)
    if (totals.subtotal_by === undefined && attributes.length > 0) totals = { subtotal_by: [...attributes], ...totals }
  }

  if (measures.length === 0 && issues.length === 0) issue(VIEW('041'), join(wp, 'encodings'), 'The encodings name no measure, and a request needs at least one.')
  if (issues.length > 0) return refused()

  // ---- the request, its keys in the order the .NET twin writes them ----
  const written = typeof query.limit === 'number' && Number.isInteger(query.limit) ? Math.min(query.limit, MAXIMUM_LIMIT) : defaultLimit(type)
  const q: Json = { measures }
  if (attributes.length > 0) q.attributes = attributes
  if (Array.isArray(query.filters) && query.filters.length > 0) {
    q.filters = structuredClone(query.filters)
    query.filters.forEach((_, i) => origins.set(`query.filters[${i}]`, `${wp}.query.filters[${i}]`))
  }
  if (Array.isArray(query.segments) && query.segments.length > 0) {
    q.segments = structuredClone(query.segments)
    query.segments.forEach((_, i) => origins.set(`query.segments[${i}]`, `${wp}.query.segments[${i}]`))
  }
  if (order) q.order = order
  q.limit = written
  if (totals) { q.totals = totals; origins.set('query.totals', type === 'matrix' ? `${wp}.encodings.totals` : `${wp}.query.totals`) }
  if (typeof query.timezone === 'string') q.timezone = query.timezone

  return { request: { spec_version: SPEC_VERSION, kind: 'query', query: q }, bindings: bindingsOf(q, isObject(query.params) ? query.params : undefined), issues, documentPath }
}

// ---- the fields of each widget type, in the fixed order ----

function slotsOf(type: string, encodings: Json, path: string): Slot[] {
  const slots: Slot[] = []
  const one = (channel: string, well: Well) => { const v = encodings[channel]; if (isString(v)) slots.push({ field: v, path: join(path, channel), well }) }
  const channel = (node: unknown, p: string) => {
    if (isString(node)) slots.push({ field: node, path: p, well: 'either' })
    else if (isObject(node) && isString(node.field)) slots.push({ field: node.field, path: join(p, 'field'), well: 'either' })
  }
  switch (type) {
    case 'metric': one('value', 'measure'); one('compare', 'measure'); break
    case 'chart':
      for (const c of CHART_CHANNELS) {
        const v = encodings[c]
        if (c === 'tooltip' && Array.isArray(v)) v.forEach((item, i) => channel(item, at(join(path, c), i)))
        else channel(v, join(path, c))
      }
      break
    case 'matrix':
      for (const axis of ['rows', 'columns'] as const) {
        const list = encodings[axis]
        if (Array.isArray(list)) list.forEach((f, i) => { if (typeof f === 'string') slots.push({ field: f, path: at(join(path, axis), i), well: 'attribute' }) })
      }
      if (Array.isArray(encodings.values)) encodings.values.forEach((v, i) => { if (isObject(v) && typeof v.field === 'string') slots.push({ field: v.field, path: join(at(join(path, 'values'), i), 'field'), well: 'measure' }) })
      break
    case 'table':
      if (Array.isArray(encodings.columns)) encodings.columns.forEach((c, i) => { if (isObject(c) && typeof c.field === 'string') slots.push({ field: c.field, path: join(at(join(path, 'columns'), i), 'field'), well: 'either' }) })
      break
  }
  return slots
}

// ---- a matrix's totals ----

function matrixTotals(encodings: Json, roles: CatalogRoles, wp: string, query: Json, issue: (code: string, path: string, message: string) => void): Json | undefined {
  const axis = (name: string): string[] => [...new Set(Array.isArray(encodings[name]) ? (encodings[name] as unknown[]).map((n) => String(n ?? '')) : [])]
  const rows = axis('rows')
  const columns = axis('columns')
  const totalsOf = isObject(encodings.totals) ? encodings.totals : {}
  const wantRow = totalsOf.rows === true // a total row: the row fields rolled up, the column fields kept
  const wantColumn = totalsOf.columns === true
  // sorting an axis by a measure reads each member's value in the axis total (the view's `sort_needs_totals`), so the request asks for that total too
  const sort = isObject(encodings.sort) ? encodings.sort : {}
  const byMeasure = (name: string) => { const s = sort[name]; return isObject(s) && typeof s.by === 'string' && roleOf(roles, s.by) === 'measure' }
  const needRow = wantRow || (byMeasure('columns') && rows.length > 0)
  const needColumn = wantColumn || (byMeasure('rows') && columns.length > 0)
  if (!needRow && !needColumn) return undefined

  if (query.totals !== undefined) { issue(VIEW('042'), `${wp}.query.totals`, "A matrix's totals are made from its encodings (`totals`, and a sort by a measure); the query asks for them too."); return undefined }
  if (wantRow && rows.length === 0) { issue(VIEW('042'), `${wp}.encodings.totals.rows`, 'A total row needs at least one row field to roll up.'); return undefined }
  if (wantColumn && columns.length === 0) { issue(VIEW('042'), `${wp}.encodings.totals.columns`, 'A total column needs at least one column field to roll up.'); return undefined }

  const all = [...rows, ...columns]
  if (needRow && needColumn) return { subtotal_by: all, grand_total: true, cube: true }
  // a roll-up takes the innermost attribute first: the axis to roll up goes last
  return needRow ? { subtotal_by: [...columns, ...rows] } : { subtotal_by: all }
}

// ---- parameter markers ----

function bindingsOf(request: Json, map: Json | undefined): ParameterBinding[] {
  const found: ParameterBinding[] = []
  const walk = (node: unknown): void => {
    if (Array.isArray(node)) { node.forEach(walk); return }
    if (!isObject(node)) return
    const keys = Object.keys(node)
    if (keys.length === 1 && keys[0] === 'param' && typeof node.param === 'string') {
      const name = node.param
      const mapped = map?.[name]
      if (!found.some((b) => b.request === name)) found.push({ request: name, document: typeof mapped === 'string' ? mapped : name })
      return
    }
    for (const v of Object.values(node)) walk(v)
  }
  walk(request)
  return found
}
