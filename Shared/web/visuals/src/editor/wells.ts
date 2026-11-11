import type { ViewsDoc, WidgetDoc, WidgetType } from '../views/model'
import { deriveRequest, memberProblem, roleOf } from '../views/derive'
import type { CatalogRoles, ParameterBinding, Well } from '../views/derive'
import { validateWidgets } from '../views/widgets'
import type { Json } from '../views/common'
import type { Op, Path } from './yamlEdit'

/**
 * The field wells of the widget editor (plan 32 DBR25), as pure functions of a widget: which wells a widget type has, what is in them, and the change a drop, a pick, a removal or a move makes. Nothing here draws.
 * A well is a place of the widget's encodings: a chart's `x`, a matrix's `rows`, a table's `columns`. A measure well takes measures only, an attribute well attributes only (a field of the wrong kind is refused in the
 * engine's own words, `memberProblem`); a well that takes either (a chart's channels, a table's columns) takes both. A field the catalog lacks is **placed** and flagged `unknown_member`, so a typo or a renamed
 * member shows in the well, in the derived request and in the validator's answer, where it can be fixed (the derivation puts it in the request's `attributes`).
 */

export interface WellSpec {
  id: string
  label: string
  kind: Well
  multiple: boolean
  /** Where the well is in the widget: `['encodings', 'x']`. */
  path: string[]
  /** How the fields are written: a field, a list of fields (a one-field list of `tooltip` is the field), or a list of `{field}` objects that hold more (a table column's label, a matrix value's format). */
  shape: 'field' | 'fields' | 'objects'
  /** For an `either` well: the role a field of it usually has (a chart's `x` is an attribute, its `y` a measure): the type chooser fills wells by it. */
  prefer?: 'measure' | 'attribute'
  scalarWhenOne?: boolean
  /** The most entries a list well takes (a chart's nested `x` has three levels at most). */
  max?: number
}

const CHART: WellSpec[] = [
  { id: 'x', label: 'x (levels, outermost first)', kind: 'either', multiple: true, path: ['encodings', 'x'], shape: 'fields', prefer: 'attribute', scalarWhenOne: true, max: 3 },
  { id: 'y', label: 'y', kind: 'either', multiple: false, path: ['encodings', 'y'], shape: 'field', prefer: 'measure' },
  { id: 'color', label: 'color', kind: 'either', multiple: false, path: ['encodings', 'color'], shape: 'field', prefer: 'attribute' },
  { id: 'column', label: 'column (facet)', kind: 'either', multiple: false, path: ['encodings', 'column'], shape: 'field', prefer: 'attribute' },
  { id: 'row', label: 'row (facet)', kind: 'either', multiple: false, path: ['encodings', 'row'], shape: 'field', prefer: 'attribute' },
  { id: 'tooltip', label: 'tooltip', kind: 'either', multiple: true, path: ['encodings', 'tooltip'], shape: 'fields', prefer: 'measure', scalarWhenOne: true },
]
const WELLS: Record<WidgetType, WellSpec[]> = {
  metric: [
    { id: 'value', label: 'value', kind: 'measure', multiple: false, path: ['encodings', 'value'], shape: 'field' },
    { id: 'compare', label: 'compare with', kind: 'measure', multiple: false, path: ['encodings', 'compare'], shape: 'field' },
  ],
  chart: CHART,
  matrix: [
    { id: 'rows', label: 'rows', kind: 'attribute', multiple: true, path: ['encodings', 'rows'], shape: 'fields' },
    { id: 'columns', label: 'columns', kind: 'attribute', multiple: true, path: ['encodings', 'columns'], shape: 'fields' },
    { id: 'values', label: 'values', kind: 'measure', multiple: true, path: ['encodings', 'values'], shape: 'objects' },
  ],
  table: [{ id: 'columns', label: 'columns', kind: 'either', multiple: true, path: ['encodings', 'columns'], shape: 'objects', prefer: 'attribute' }],
  control: [{ id: 'attribute', label: 'list its values from', kind: 'attribute', multiple: false, path: ['values', 'attribute'], shape: 'field' }],
  text: [],
  image: [],
  embed: [],
  custom: [],
}

export const WIDGET_TYPES: readonly WidgetType[] = ['metric', 'chart', 'matrix', 'table', 'text', 'image', 'control', 'embed']

/** The wells of a widget type, in the order the derivation reads them. */
export const wellsOf = (type: WidgetType): readonly WellSpec[] => WELLS[type] ?? []

const isObject = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v)
const fieldOf = (node: unknown): string | undefined => (typeof node === 'string' && node.length > 0 ? node : isObject(node) && typeof node.field === 'string' && node.field.length > 0 ? node.field : undefined)

function getAt(raw: unknown, path: readonly string[]): unknown {
  let node: unknown = raw
  for (const key of path) node = isObject(node) ? node[key] : undefined
  return node
}

/** What is in a well, in order. */
export function wellFields(widget: WidgetDoc, spec: WellSpec): string[] {
  const node = getAt(widget, spec.path)
  if (spec.shape === 'field') { const f = fieldOf(node); return f ? [f] : [] }
  const list = Array.isArray(node) ? node : node === undefined ? [] : [node]
  return list.flatMap((item) => { const f = fieldOf(item); return f ? [f] : [] })
}

/** The value a well holds once its fields are `fields`, keeping what each entry carried (a column's label, a channel's own keys); undefined when it is empty. */
function valueWith(widget: WidgetDoc, spec: WellSpec, fields: string[]): unknown {
  if (fields.length === 0) return undefined
  const node = getAt(widget, spec.path)
  const old = (Array.isArray(node) ? node : [node]).filter(isObject).filter((o) => typeof o.field === 'string')
  const entry = (field: string): unknown => {
    const existing = old.find((o) => o.field === field)
    return spec.shape === 'objects' ? existing ?? { field } : existing ?? field
  }
  if (spec.shape === 'field') return entry(fields[0]!)
  if (spec.scalarWhenOne && fields.length === 1) return entry(fields[0]!)
  return fields.map(entry)
}

export type WellChange =
  | { op: 'add'; field: string; index?: number }
  | { op: 'remove'; index: number }
  | { op: 'move'; from: number; to: number }
  | { op: 'clear' }

/** The refusals of a well change; `wrong_member_kind` carries the engine's words. */
export class WellError extends Error {
  readonly code: 'wrong_member_kind' | 'bad_field' | 'unknown_well' | 'bad_position'
  constructor(code: WellError['code'], message: string) { super(message); this.name = 'WellError'; this.code = code }
}

/** The fields a well holds after a change. A single well is replaced; a field already in a list well is not added twice; a field of the wrong kind is refused. */
export function changedFields(fields: string[], spec: WellSpec, change: WellChange, roles?: CatalogRoles): string[] {
  switch (change.op) {
    case 'clear': return []
    case 'remove':
      if (change.index < 0 || change.index >= fields.length) throw new WellError('bad_position', `The well \`${spec.id}\` has no entry ${change.index + 1}.`)
      return fields.filter((_, i) => i !== change.index)
    case 'move': {
      if (change.from < 0 || change.from >= fields.length || change.to < 0 || change.to >= fields.length) throw new WellError('bad_position', `The well \`${spec.id}\` has no such position.`)
      const next = [...fields]
      const [moved] = next.splice(change.from, 1)
      next.splice(change.to, 0, moved!)
      return next
    }
    case 'add': {
      const field = change.field.trim()
      if (field.length === 0) throw new WellError('bad_field', 'Name a field to put in the well.')
      const problem = roles ? memberProblem(roles, field, spec.kind) : null
      if (problem?.code === 'wrong_member_kind') throw new WellError('wrong_member_kind', problem.message)
      if (!spec.multiple) return [field]
      if (fields.includes(field)) return fields
      if (spec.max !== undefined && fields.length >= spec.max) throw new WellError('bad_position', `The well \`${spec.id}\` takes at most ${spec.max} fields.`)
      const next = [...fields]
      next.splice(Math.max(0, Math.min(fields.length, change.index ?? fields.length)), 0, field)
      return next
    }
  }
}

/**
 * The rules of a nested axis (plan 32 DBR42), in the words of the loaders: more than one level on a chart's `x` needs a bar or a point mark (a line breaks between the groups) and no `column` (the nested axis is
 * drawn with the facet); and a `column` cannot be added to a chart whose `x` is already nested.
 */
function nestedAxisRules(widget: WidgetDoc, spec: WellSpec, after: string[], change: WellChange): void {
  if (widget.type !== 'chart' || change.op === 'remove' || change.op === 'clear') return
  const encodings = (widget as unknown as { encodings?: Record<string, unknown> }).encodings ?? {}
  const mark = (widget as unknown as { mark?: unknown }).mark
  const markType = isObject(mark) ? mark.type : mark
  if (spec.id === 'x' && after.length > 1) {
    if (markType === 'line' || markType === 'area') throw new WellError('bad_field', `A ${String(markType)} chart has no nested axis: a line breaks between the groups. Use one temporal field with a grain and a format such as MMM yyyy, or a bar or point mark.`)
    if (encodings.column !== undefined) throw new WellError('bad_field', 'A chart with a nested `x` has no `column`: the nested axis is drawn with the facet. Remove the column first.')
  }
  if (spec.id === 'column' && change.op === 'add' && wellFields(widget, WELLS.chart[0]!).length > 1)
    throw new WellError('bad_field', 'A chart with a nested `x` has no `column`: the nested axis is drawn with the facet.')
}

/** The changes of the text a well change makes (none when nothing changes): the well's value set, or removed when it is empty. */
export function wellOps(widget: WidgetDoc, id: string, spec: WellSpec, change: WellChange, roles?: CatalogRoles): Op[] {
  const before = wellFields(widget, spec)
  const after = changedFields(before, spec, change, roles)
  nestedAxisRules(widget, spec, after, change)
  if (JSON.stringify(before) === JSON.stringify(after) && change.op !== 'add') return []
  const value = valueWith(widget, spec, after)
  const path: Path = ['widgets', id, ...spec.path]
  if (value === undefined) {
    if (getAt(widget, spec.path) === undefined) return []
    // an emptied `values` of a control goes with its limit only when nothing else is in it
    const parent = getAt(widget, spec.path.slice(0, -1))
    if (spec.path[0] === 'values' && isObject(parent) && Object.keys(parent).every((k) => k === spec.path[1])) return [{ op: 'remove', path: ['widgets', id, 'values'] }]
    return [{ op: 'remove', path }]
  }
  if (JSON.stringify(getAt(widget, spec.path)) === JSON.stringify(value)) return []
  return [{ op: 'set', path, value }]
}

// ---- a widget of another type ----

const MARKER_QUERY_KEYS = ['model', 'filters', 'segments', 'timezone', 'params']
const IMAGE_PLACEHOLDER = `sha256:${'0'.repeat(64)}`

export interface Conversion { widget: WidgetDoc; /** Fields of the old widget that no well of the new one takes. */ dropped: string[] }

function shell(to: WidgetType, from: WidgetDoc, model: string, parameter: string | undefined): Record<string, unknown> {
  const title = (from as { title?: string }).title
  const label = (from as { label?: string }).label
  const queryOf = (): Record<string, unknown> => {
    const old = (from as { query?: unknown }).query
    const query: Record<string, unknown> = { model }
    if (isObject(old) && old.mode !== 'explicit') for (const k of MARKER_QUERY_KEYS) if (old[k] !== undefined) query[k] = old[k]
    return query
  }
  const named = (key: 'title' | 'label'): Record<string, unknown> => (key === 'label' ? (label ?? title ? { label: label ?? title } : {}) : (title ?? label ? { title: title ?? label } : {}))
  switch (to) {
    case 'metric': return { type: to, ...named('label'), encodings: {}, query: queryOf() }
    case 'chart': return { type: to, ...named('title'), mark: 'bar', encodings: {}, query: queryOf() }
    case 'matrix': return { type: to, ...named('title'), encodings: {}, query: queryOf() }
    case 'table': return { type: to, ...named('title'), encodings: {}, query: queryOf() }
    case 'text': return { type: to, ...named('title'), text: 'Write a note.' }
    case 'image': return { type: to, ...named('title'), hash: IMAGE_PLACEHOLDER, media_type: 'image/png', alt: 'Describe the image' }
    case 'control': return { type: to, ...named('title'), parameter: parameter ?? '', control: 'select', ...(label ? { label } : {}) }
    case 'embed': return { type: to, ...named('title'), url: 'https://reports.example.com/' }
    case 'custom': throw new WellError('bad_field', 'A custom widget is code of an app: it is written in the app, not made from another widget.')
  }
}

/**
 * A widget changed to another type (the chooser): the title, the model and the query's filters stay; the fields go to the wells of the new type that take them, each to the well that wants its role first (a measure
 * to a chart's `y`, an attribute to its `x`), then to any free well that takes it; what no well takes is reported. The query's own order, limit and totals are the old type's and go. Refuses a control without a parameter.
 */
export function convertWidget(widget: WidgetDoc, to: WidgetType, context: { roles?: CatalogRoles; model: string; parameter?: string }): Conversion {
  if (widget.type === to) return { widget, dropped: [] }
  if (to === 'control' && !context.parameter) throw new WellError('unknown_well', 'A control edits a parameter of the document: add a parameter first.')
  const fields: string[] = []
  for (const spec of wellsOf(widget.type)) for (const f of wellFields(widget, spec)) if (!fields.includes(f)) fields.push(f)
  const explicitModel = (widget as { query?: { semantic?: { model?: string } } }).query?.semantic?.model
  const model = (widget as { query?: { model?: string } }).query?.model ?? explicitModel ?? context.model
  let next = shell(to, widget, model, context.parameter) as unknown as WidgetDoc
  const roleFor = (f: string) => (context.roles ? roleOf(context.roles, f) : 'unknown')
  const free = (spec: WellSpec) => spec.multiple || wellFields(next, spec).length === 0
  const compatible = (spec: WellSpec, f: string) => { const r = roleFor(f); return r === 'unknown' || spec.kind === 'either' || spec.kind === r }
  const dropped: string[] = []
  const placed = new Set<string>()
  const put = (spec: WellSpec, f: string) => {
    const ops = wellOps(next, 'w', spec, { op: 'add', field: f })
    const op = ops[0]
    if (op?.op !== 'set') return
    const copy = structuredClone(next) as unknown as Record<string, unknown>
    let node = copy
    for (const key of spec.path.slice(0, -1)) node = (node[key] ??= {}) as Record<string, unknown>
    node[spec.path[spec.path.length - 1]!] = op.value
    next = copy as unknown as WidgetDoc
    placed.add(f)
  }
  const specs = wellsOf(to)
  for (const f of fields) {
    const role = roleFor(f)
    const spec = specs.find((s) => free(s) && compatible(s, f) && (s.kind === role || s.prefer === role)) ?? specs.find((s) => free(s) && compatible(s, f))
    if (spec) put(spec, f)
  }
  for (const f of fields) if (!placed.has(f)) dropped.push(f)
  return { widget: next, dropped }
}

// ---- the derived request and the written one ----

export interface Derivation {
  /** `request`: derived; `explicit`: the query is written out; `none`: this widget type asks for nothing; `refused`: the encodings cannot be derived (the issues say why). */
  state: 'request' | 'explicit' | 'none' | 'refused'
  request?: Json
  /** The request as JSON text, two-space indented. */
  text?: string
  bindings: ParameterBinding[]
  issues: Array<{ code: string; path: string; message: string; documentPath: string }>
  /** Each part of the request and the place of the document it comes from. */
  sources: Array<{ requestPath: string; documentPath: string }>
}

const REQUEST_PARTS = ['measures', 'attributes', 'filters', 'segments', 'order'] as const

/** What the read-only panel shows of a widget: the derived request, its parameter bindings, and for each part of it the place of the document it comes from (`deriveRequest`'s `documentPath`). */
export function describeDerivation(id: string, widget: WidgetDoc, roles: CatalogRoles): Derivation {
  const derived = deriveRequest({ id, raw: widget as unknown as Json }, roles)
  const issues = derived.issues.map((i) => ({ code: i.code, path: i.path, message: i.message, documentPath: i.path }))
  const query = (widget as { query?: unknown }).query
  if (!derived.request) {
    const nothing = issues.length === 1 && issues[0]!.code === 'DBD-VIEW-040'
    if (nothing && ['text', 'image', 'control', 'embed'].includes(widget.type)) return { state: 'none', bindings: [], issues: [], sources: [] }
    if (nothing && isObject(query) && (query.mode === 'explicit' || ['semantic', 'saved', 'store', 'ref'].some((k) => query[k] !== undefined))) return { state: 'explicit', bindings: [], issues: [], sources: [] }
    return { state: 'refused', bindings: [], issues, sources: [] }
  }
  const body = (derived.request as { query: Record<string, unknown> }).query
  const sources: Derivation['sources'] = []
  for (const part of REQUEST_PARTS) {
    const value = body[part]
    if (Array.isArray(value)) value.forEach((_, i) => { const p = `$.query.${part}[${i}]`; sources.push({ requestPath: p, documentPath: derived.documentPath(p) }) })
  }
  for (const part of ['totals', 'limit', 'timezone'] as const) if (body[part] !== undefined) { const p = `$.query.${part}`; sources.push({ requestPath: p, documentPath: derived.documentPath(p) }) }
  return { state: 'request', request: derived.request, text: JSON.stringify(derived.request, null, 2), bindings: derived.bindings, issues, sources }
}

/** The query written out (`mode: explicit`) from what the encodings derive: the semantic request as it stands, kept to be edited by hand. Refused with the derivation's words when it cannot be derived. */
export function explicitQuery(id: string, widget: WidgetDoc, roles: CatalogRoles): Record<string, unknown> {
  const d = describeDerivation(id, widget, roles)
  const query = (widget as { query?: { model?: string; params?: unknown } }).query
  if (d.state !== 'request') throw new WellError('bad_field', d.issues[0]?.message ?? 'There is no derived request to write out.')
  const written: Record<string, unknown> = { mode: 'explicit', semantic: { model: query?.model ?? roles.model ?? '', request: (d.request as { query: unknown }).query } }
  if (isObject(query?.params)) written.params = query.params
  return written
}

/** The query back to derived: the model stays, the written request is given up (the encodings drive it again). */
export function derivedQuery(widget: WidgetDoc): Record<string, unknown> {
  const query = (widget as { query?: { semantic?: { model?: string }; saved?: unknown; store?: unknown; ref?: unknown; model?: string; params?: unknown } }).query
  const model = query?.semantic?.model ?? query?.model ?? ''
  return isObject(query?.params) ? { model, params: query.params } : { model }
}

/** The binding check of a written query that the browser can make: every encoded field must be an output of the query (the validator's `DBD-VIEW-005`), in its words and at the field's path. */
export function bindingIssues(id: string, widget: WidgetDoc, parameters: string[] = []): Array<{ code: string; path: string; message: string }> {
  return validateWidgets({ [id]: widget }, parameters).filter((i) => i.code === 'DBD-VIEW-005').map((i) => ({ code: i.code, path: i.path, message: i.message }))
}

/** The widget of a document with a request edited by hand, for the raw editor: JSON text to an object, or the words of why not. */
export function parseRequestText(text: string): { value: Record<string, unknown> } | { error: string } {
  try {
    const value: unknown = JSON.parse(text)
    return isObject(value) ? { value } : { error: 'A semantic request is a JSON object.' }
  } catch (e) {
    return { error: `Not JSON: ${e instanceof Error ? e.message : String(e)}` }
  }
}

/** The name of the model a widget asks of: its query's, or its written query's, or the document's first. */
export function widgetModel(widget: WidgetDoc, doc: ViewsDoc | null | undefined): string | undefined {
  const q = (widget as { query?: { model?: unknown; semantic?: { model?: unknown } } }).query
  if (typeof q?.model === 'string') return q.model
  if (typeof q?.semantic?.model === 'string') return q.semantic.model
  for (const w of Object.values(doc?.widgets ?? {})) {
    const m = (w as { query?: { model?: unknown } }).query?.model
    if (typeof m === 'string') return m
  }
  return undefined
}
