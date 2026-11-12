import { VIEW, isObject, join, at } from './common'
import type { Json, ViewsIssue } from './common'
import type { GroupDef } from './groups'
import { GROUP_CONFLICT, UNKNOWN_GROUP } from './groups'

/**
 * `deriveRequest` (plan 32 section 2.3, DBR03): a widget's encodings, with the catalog's roles, become the semantic request they need. The twin of `DeriveRequest.Derive` in `DbDataKit.Views` (.NET): the two run
 * the same goldens (`Shared/conformance/views/derive`) and give the same request, key for key, so the editor's field wells (DBR25) show exactly what the server will ask.
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
export interface DerivableWidget { id: string; raw: Json; /** The groups of the widget's document (plan 32 DBR52). */ groups?: Record<string, GroupDef> }

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
/** An encoded field: its name, its place, the well it goes to and, for the object form (DBR50), the entry (`alias`, `filters`) and its path. */
interface Slot { field: string; path: string; well: Well; entry?: Json; entryPath?: string; group?: string }

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
  const slots = slotsOf(type, encodings, join(wp, 'encodings'), widget.groups, issue)
  // a field is in one place of a widget when a group is involved: the group's levels are the field's place, so a second place (an axis, a channel, a tooltip, another group) is refused naming both
  const byField = new Map<string, Slot[]>()
  for (const sl of slots) byField.set(sl.field, [...(byField.get(sl.field) ?? []), sl])
  for (const [field, list] of byField)
    if (list.length > 1 && list.some((sl) => sl.group !== undefined))
      for (const again of list.slice(1).filter((sl) => sl.group !== undefined || list[0]!.group !== undefined))
        issue(GROUP_CONFLICT, again.path, `${field} is in two places of the widget (${list[0]!.path} and ${again.path}), and one of them is a group: a field is in one place.`)
  const measures: { name: string; node: unknown }[] = []
  const attributes: string[] = []

  const placeEntry = (slot: Slot) => {
    const { field, path, well, entry, entryPath } = slot
    // a name that an entry already gives the output (an alias) is that output: a sort or an order may name it
    if (entry === undefined && measures.some((m) => m.name === field)) return
    const role = roleOf(roles, field)
    const problem = memberProblem(roles, field, well)
    if (problem) issue(problem.code, path, problem.message)
    const toMeasures = well === 'measure' || (well === 'either' && role === 'measure')
    const alias = entry && typeof entry.alias === 'string' && entry.alias.length > 0 && entry.alias !== field ? entry.alias : undefined
    const filters = entry && Array.isArray(entry.filters) && entry.filters.length > 0 ? entry.filters : undefined
    if (!toMeasures) {
      if (alias !== undefined || filters !== undefined)
        issue('wrong_member_kind', filters !== undefined ? `${entryPath}.filters` : `${entryPath}.alias`, `${field} is an attribute: only a measure takes \`alias\` and \`filters\`.`)
      if (attributes.includes(field)) return
      attributes.push(field)
      origins.set(`query.attributes[${attributes.length - 1}]`, path)
      return
    }
    // the object form (DBR50): the engine's own rule, in its words
    if (filters !== undefined && alias === undefined) issue('alias_required', `${entryPath}.filters`, "A measure with its own filters needs an 'as' alias, so it can't be mistaken for the unfiltered measure.")
    let node: unknown = field
    if (alias !== undefined || filters !== undefined) {
      const o: Json = { measure: field }
      if (alias !== undefined) o.as = alias
      if (filters !== undefined) o.filters = structuredClone(filters)
      node = o
    }
    const name = alias ?? field
    const at = measures.findIndex((m) => m.name === name)
    if (at >= 0) {
      if (JSON.stringify(measures[at]!.node) !== JSON.stringify(node)) issue('duplicate_measure', alias !== undefined ? `${entryPath}.alias` : path, `${name} is listed more than once: give one of the entries an \`alias\`.`)
      return
    }
    measures.push({ name, node })
    origins.set(`query.measures[${measures.length - 1}]`, path)
    if (entry !== undefined) {
      if (alias !== undefined) origins.set(`query.measures[${measures.length - 1}].as`, `${entryPath}.alias`)
      if (filters !== undefined) origins.set(`query.measures[${measures.length - 1}].filters`, `${entryPath}.filters`)
    }
  }
  const place = (field: string, path: string, well: Well) => placeEntry({ field, path, well })
  for (const s of slots) placeEntry(s)

  // ---- order: the query's own, else a table's sort ----
  let order: unknown[] | undefined
  if (Array.isArray(query.order) && query.order.length > 0) {
    order = structuredClone(query.order)
    order.forEach((_, i) => origins.set(`query.order[${i}]`, `${wp}.query.order[${i}]`))
  } else if (type === 'chart' && Array.isArray(encodings.x) && encodings.x.length > 1) {
    order = nestedOrder(encodings.x, join(join(wp, 'encodings'), 'x'), origins, place, issue)
  } else if (type === 'chart' && isObject(encodings.x) && encodings.x.group !== undefined) {
    // a group on a chart's `x` (DBR52): its levels in turn, so a line over year and month is in calendar order whatever order the rows arrive in
    order = groupOrder(slots.filter((sl) => sl.group !== undefined && sl.path === `${join(join(wp, 'encodings'), 'x')}.group`), encodings.x.sort === 'descending', origins)
  } else if (type === 'matrix' && slots.some((sl) => sl.group !== undefined && (sl.path.includes('.rows[') || sl.path.includes('.columns[')))) {
    order = groupOrder(slots.filter((sl) => sl.group !== undefined), false, origins)
  } else if (type === 'table' && isObject(encodings.sort) && isString(encodings.sort.by)) {
    const sortField = encodings.sort.by
    const direction = typeof encodings.sort.dir === 'string' ? encodings.sort.dir : 'asc'
    const sortPath = join(join(wp, 'encodings'), 'sort.by')
    if (!measures.some((m) => m.name === sortField) && !attributes.includes(sortField)) place(sortField, sortPath, 'either')
    order = [[sortField, direction]]
    origins.set('query.order[0]', sortPath)
  }

  // ---- totals ----
  let totals: Json | undefined
  if (type === 'matrix') totals = matrixTotals(encodings, widget.groups, (n) => roleOf(roles, n) === 'measure' || measures.some((m) => m.name === n), cellAttributes(encodings), wp, query, issue)
  else if (isObject(query.totals)) {
    totals = structuredClone(query.totals)
    if (totals.subtotal_by === undefined && attributes.length > 0) totals = { subtotal_by: [...attributes], ...totals }
  }

  if (measures.length === 0 && issues.length === 0) issue(VIEW('041'), join(wp, 'encodings'), 'The encodings name no measure, and a request needs at least one.')
  if (issues.length > 0) return refused()

  // ---- the request, its keys in the order the .NET twin writes them ----
  const written = typeof query.limit === 'number' && Number.isInteger(query.limit) ? Math.min(query.limit, MAXIMUM_LIMIT) : defaultLimit(type)
  const q: Json = { measures: measures.map((m) => m.node) }
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

function slotsOf(type: string, encodings: Json, path: string, groups: Record<string, GroupDef> | undefined, refuse: (code: string, path: string, message: string) => void): Slot[] {
  const slots: Slot[] = []
  // a field group in a channel or an axis (DBR52): its levels, outermost first, are the attributes it asks for
  const group = (node: unknown, p: string): boolean => {
    if (!isObject(node) || typeof node.group !== 'string') return false
    const def = groups?.[node.group]
    if (!def) { refuse(UNKNOWN_GROUP, `${p}.group`, `\`${node.group}\` is not a group of this document.`); return true }
    for (const level of def.levels) slots.push({ field: level.field, path: `${p}.group`, well: 'attribute', group: node.group })
    return true
  }
  // a field name, or the object form (DBR50): `{field, alias, label, filters, format}` where a measure goes
  const entry = (node: unknown, p: string, well: Well) => {
    if (group(node, p)) return
    if (isString(node)) slots.push({ field: node, path: p, well })
    else if (isObject(node) && isString(node.field)) slots.push({ field: node.field, path: join(p, 'field'), well, entry: node, entryPath: p })
  }
  switch (type) {
    case 'metric': entry(encodings.value, join(path, 'value'), 'measure'); entry(encodings.compare, join(path, 'compare'), 'measure'); break
    case 'chart':
      for (const c of CHART_CHANNELS) {
        const v = encodings[c]
        // the tooltip's list and a `y` of several measures (one series each)
        if ((c === 'tooltip' || c === 'y') && Array.isArray(v)) v.forEach((item, i) => entry(item, at(join(path, c), i), 'either'))
        // a nested axis (DBR42): every level is an attribute, outermost first
        else if (c === 'x' && Array.isArray(v)) v.forEach((level, i) => entry(level, at(join(path, c), i), 'attribute'))
        else entry(v, join(path, c), 'either')
      }
      break
    case 'matrix':
      for (const axis of ['rows', 'columns'] as const) {
        const list = encodings[axis]
        if (Array.isArray(list)) list.forEach((f, i) => entry(f, at(join(path, axis), i), 'attribute'))
      }
      if (Array.isArray(encodings.values)) encodings.values.forEach((v, i) => {
        if (!isObject(v)) return
        // an attribute in the cell area (DBR51) is asked for at the cells' grain, after the axes
        if (isString(v.attribute)) slots.push({ field: v.attribute, path: join(at(join(path, 'values'), i), 'attribute'), well: 'attribute' })
        else entry(v, at(join(path, 'values'), i), 'measure')
      })
      break
    case 'table':
      if (Array.isArray(encodings.columns)) encodings.columns.forEach((c, i) => { if (isObject(c)) entry(c, at(join(path, 'columns'), i), 'either') })
      break
  }
  return slots
}

/** The fields of a matrix axis in order, a group expanded to its levels (DBR52); the twin of `DeriveRequest.AxisFields`. */
export function axisFields(encodings: Json, axis: string, groups: Record<string, GroupDef> | undefined): string[] {
  const list = encodings[axis]
  if (!Array.isArray(list)) return []
  const out: string[] = []
  for (const n of list) {
    if (isObject(n) && typeof n.group === 'string') { for (const l of groups?.[n.group]?.levels ?? []) out.push(l.field) }
    else out.push(isObject(n) ? String(n.field ?? '') : String(n ?? ''))
  }
  return [...new Set(out)]
}

/** The `order` of the group levels a widget's slots hold, each level ascending (descending when asked). */
function groupOrder(slots: Slot[], descending: boolean, origins: Map<string, string>): unknown[] | undefined {
  const order: unknown[][] = []
  for (const slot of slots) {
    if (order.some((o) => o[0] === slot.field)) continue
    origins.set(`query.order[${order.length}]`, slot.path)
    order.push([slot.field, descending ? 'desc' : 'asc'])
  }
  return order.length > 0 ? order : undefined
}

// ---- a nested axis's order (DBR42) ----

/** The widest a nested `x` may be before the shaper cuts the members and says so: 200 member combinations. */
export const LEAF_LIMIT = 200

/**
 * The `order` of a nested axis, one entry per level from the outermost (the twin of `DeriveRequest.NestedOrder`): `sort_key` (the default) and `label` ask for the level's attribute in its direction, a measure
 * (the innermost level only) asks for the measure.
 */
function nestedOrder(levels: unknown[], xPath: string, origins: Map<string, string>, place: (field: string, path: string, well: Well) => void, issue: (code: string, path: string, message: string) => void): unknown[] | undefined {
  const order: unknown[] = []
  levels.forEach((level, i) => {
    const field = isObject(level) ? level.field : level
    if (!isString(field)) return
    const sort = isObject(level) && isObject(level.sort) ? level.sort : undefined
    const by = typeof sort?.by === 'string' ? sort.by : 'sort_key'
    const direction = typeof sort?.dir === 'string' ? sort.dir : 'asc'
    const sortPath = `${at(xPath, i)}.sort.by`
    let target: string = field
    let path = at(xPath, i) + (isObject(level) ? '.field' : '')
    if (by !== 'sort_key' && by !== 'label') {
      if (i !== levels.length - 1) { issue('DBD-106', sortPath, 'Only the innermost level of a nested axis sorts by a measure: an outer level would need its own total.'); return }
      place(by, sortPath, 'measure')
      target = by
      path = sortPath
    }
    origins.set(`query.order[${order.length}]`, path)
    order.push([target, direction])
  })
  return order.length > 0 ? order : undefined
}

// ---- a matrix's totals ----

/**
 * The attributes of a matrix's cell area (DBR51; the twin of `DeriveRequest.CellAttributes`): the `attribute` entries of `values` that are not already on an axis, asked for at the cells' grain and rolled up last in the
 * request's totals, so the answer holds the cell's own row (the attribute rolled up) beside the rows that say which values the cell has.
 */
export function cellAttributes(encodings: Json): string[] {
  const entryField = (n: unknown): string => (isObject(n) ? String(n.field ?? '') : String(n ?? ''))
  const onAxes = new Set(['rows', 'columns'].flatMap((a) => (Array.isArray(encodings[a]) ? (encodings[a] as unknown[]).map(entryField) : [])))
  const list = Array.isArray(encodings.values) ? (encodings.values as unknown[]).map((v) => (isObject(v) && isString(v.attribute) ? v.attribute : '')) : []
  return [...new Set(list.filter((a) => a.length > 0 && !onAxes.has(a)))]
}

function matrixTotals(encodings: Json, groups: Record<string, GroupDef> | undefined, isMeasure: (name: string) => boolean, cellAttrs: string[], wp: string, query: Json, issue: (code: string, path: string, message: string) => void): Json | undefined {
  const axis = (name: string): string[] => axisFields(encodings, name, groups)
  const rows = axis('rows')
  const columns = axis('columns')
  const totalsOf = isObject(encodings.totals) ? encodings.totals : {}
  const wantRow = totalsOf.rows === true // a total row: the row fields rolled up, the column fields kept
  const wantColumn = totalsOf.columns === true
  // sorting an axis by a measure reads each member's value in the axis total (the view's `sort_needs_totals`), so the request asks for that total too
  const sort = isObject(encodings.sort) ? encodings.sort : {}
  const axisSort = (name: string): unknown => sort[name] ?? (Array.isArray(encodings[name]) ? (encodings[name] as unknown[]).map((n) => (isObject(n) ? n.sort : undefined)).find((x) => x !== undefined) : undefined)
  const byMeasure = (name: string) => { const s = axisSort(name); return isObject(s) && typeof s.by === 'string' && isMeasure(s.by) }
  const needRow = wantRow || (byMeasure('columns') && rows.length > 0)
  const needColumn = wantColumn || (byMeasure('rows') && columns.length > 0)
  if (!needRow && !needColumn && cellAttrs.length === 0) return undefined

  if (query.totals !== undefined) { issue(VIEW('042'), `${wp}.query.totals`, "A matrix's totals are made from its encodings (`totals`, and a sort by a measure); the query asks for them too."); return undefined }
  if (wantRow && rows.length === 0) { issue(VIEW('042'), `${wp}.encodings.totals.rows`, 'A total row needs at least one row field to roll up.'); return undefined }
  if (wantColumn && columns.length === 0) { issue(VIEW('042'), `${wp}.encodings.totals.columns`, 'A total column needs at least one column field to roll up.'); return undefined }

  const all = [...rows, ...columns, ...cellAttrs]
  if (needRow && needColumn) return { subtotal_by: all, grand_total: true, cube: true }
  // a roll-up takes the innermost attribute first: the axis to roll up goes last, and the attributes of the cell area after it
  return needRow || !needColumn ? { subtotal_by: [...columns, ...rows, ...cellAttrs] } : { subtotal_by: all }
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
