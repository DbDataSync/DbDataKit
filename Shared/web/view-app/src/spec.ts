/** `views.json`: the app's `view-spec/1` files as the apps host reads them (`ViewsApp.Json`). Nothing here depends on a browser. */

export type ParamType = 'string' | 'int' | 'bool' | 'date'
export interface SpecParam { name: string; type: ParamType; required: boolean; default: string | null }
export type ValueKind = 'literal' | 'attribute' | 'param'
export interface LinkSpec { label: string; to: string; row: boolean; with: Record<string, { kind: ValueKind; text: string }> }
export interface AttrRef { name: string; label: string | null }

interface Base { title: string; model: string; params: SpecParam[]; links: LinkSpec[] }
export interface GridSpec extends Base {
  kind: 'grid'; entity: string
  columns: { attribute: string; label: string | null; sortable: boolean }[]
  filters: AttrRef[]; sort: { attribute: string; direction: 'asc' | 'desc' }[]; pageSize: number
}
export interface FormSpec extends Base {
  kind: 'form'; entity: string; mode: 'create' | 'edit'; key: string | null; submit: string | null
  fields: { attribute: string; label: string | null; readOnly: boolean; required: boolean; help: string | null }[]
}
export interface DetailSpec extends Base { kind: 'detail'; entity: string; key: string; sections: { title: string; attributes: AttrRef[] }[] }
export interface WidgetSpec {
  kind: 'metric' | 'bar' | 'line' | 'table' | 'embed'; title: string; entity: string; aggregate: 'count' | 'sum' | 'avg' | 'min' | 'max'
  measure: AttrRef | null; by: AttrRef | null; columns: AttrRef[]; limit: number
  /** An `embed` widget (plan 26 AP17), which has no entity and no data: the report's address, the words of its link, and whether the host frames it (the origin is one the instance's approval allows). */
  url?: string; link_text?: string | null; framed?: boolean
}
/** The widgets that read rows of an entity: every kind but an `embed`. */
export const readsRows = (w: WidgetSpec): boolean => w.kind !== 'embed'
export interface DashboardSpec extends Base { kind: 'dashboard'; widgets: WidgetSpec[] }
export type ViewSpec = GridSpec | FormSpec | DetailSpec | DashboardSpec

export interface ViewsDoc {
  format: string
  app: string
  instance: string
  title: string
  home: string | null
  files: string[]
  views: Record<string, ViewSpec>
  /** Files that did not read, each with the loader's file:line:column message. */
  problems: string[]
}

export const SUPPORTED_FORMAT = 'view-spec/1'

/** A route is what the shell keeps in the address after `#`: `/<view file>` and, after `?`, the view's parameters. `/` is the home view. */
export interface Route { file: string | null; query: Record<string, string> }

export function parseRoute(route: string): Route {
  const [path, qs = ''] = route.split('?', 2)
  const file = path.replace(/^\/+/, '')
  const query: Record<string, string> = {}
  new URLSearchParams(qs).forEach((v, k) => { query[k] = v })
  return { file: file === '' ? null : file, query }
}

export function routeOf(file: string, query: Record<string, string | number | boolean> = {}): string {
  const qs = new URLSearchParams(Object.entries(query).map(([k, v]) => [k, String(v)])).toString()
  return `/${file}${qs ? `?${qs}` : ''}`
}

export interface ResolvedParams { values: Record<string, string>; errors: string[] }

/** The parameters a view declared, from the route's query: defaults applied, required ones present, each of its type. Undeclared names are dropped. */
export function resolveParams(params: SpecParam[], query: Record<string, string>): ResolvedParams {
  const values: Record<string, string> = {}
  const errors: string[] = []
  for (const p of params) {
    const raw = query[p.name] ?? p.default ?? undefined
    if (raw === undefined || raw === '') {
      if (p.required) errors.push(`This view needs the parameter '${p.name}'.`)
      continue
    }
    const ok = p.type === 'int' ? /^-?\d+$/.test(raw) : p.type === 'bool' ? raw === 'true' || raw === 'false' : p.type === 'date' ? /^\d{4}-\d{2}-\d{2}$/.test(raw) : true
    if (ok) values[p.name] = raw
    else errors.push(`The parameter '${p.name}' is ${p.type === 'int' ? 'a whole number' : p.type === 'bool' ? 'true or false' : 'a date (yyyy-mm-dd)'}, not '${raw}'.`)
  }
  return { values, errors }
}

/**
 * Where a link goes. A `with` value is a literal, an attribute of the row the link belongs to, or a parameter of the view it is in. A link whose value cannot be had (a null attribute, an unset parameter) is not drawn:
 * null says so, rather than a link to a view that would refuse it.
 */
export function linkRoute(link: LinkSpec, row: Record<string, unknown> | null, params: Record<string, string>): string | null {
  const query: Record<string, string> = {}
  for (const [name, value] of Object.entries(link.with)) {
    // for an attribute or a parameter the loader keeps the bare name (`$id` is `id`, `$params.region` is `region`)
    const got = value.kind === 'literal' ? value.text : value.kind === 'param' ? params[value.text] : row?.[value.text]
    if (got === null || got === undefined || got === '') return null
    query[name] = String(got)
  }
  return routeOf(link.to, query)
}

/** The query names the host adds when a form is the target of a link the shell opens in a dialog or panel (plan 32 DBR36): `_lock` is the fields it makes read-only (a courtesy of the screen: the Store still decides), `_as` says it is in one. */
export const LOCK_PARAM = '_lock'
export const AS_PARAM = '_as'

/** Whether a form was opened by a link as a dialog or a panel: it then reports a saved write and a cancel to the page that framed it. */
export const openedByLink = (query: Record<string, string>): boolean => query[AS_PARAM] === 'dialog' || query[AS_PARAM] === 'panel'

/** The starting values of a form that creates a row (a link's `new: {field: value}` arrives as `?field=value`): the listed fields the query names, as the text a person would type. A name the form does not list gives nothing. */
export function startingValues(spec: FormSpec, query: Record<string, string>): Record<string, string> {
  if (spec.mode !== 'create') return {}
  return Object.fromEntries(spec.fields.filter((f) => Object.hasOwn(query, f.attribute) && query[f.attribute] !== '').map((f) => [f.attribute, query[f.attribute]!]))
}

/** The fields a link locks (`_lock=customer_id,status`), those the form lists: read-only on screen, but still part of what is saved. */
export function lockedFields(spec: FormSpec, query: Record<string, string>): ReadonlySet<string> {
  const listed = new Set(spec.fields.map((f) => f.attribute))
  return new Set((query[LOCK_PARAM] ?? '').split(',').map((n) => n.trim()).filter((n) => listed.has(n)))
}

/** What the form says to the page that framed it: only these two shapes, the ones the shell's surface reads (`@dbdatakit/visuals`, the links). */
export type FrameMessage = { type: 'views:saved' } | { type: 'views:close' }

/** The part of a window the form needs to speak to the page above it. */
export interface FrameWindow { parent: { postMessage(message: unknown, targetOrigin: string): void } }

/** Posts to the framing page (the shell, which owns the dialog); nothing happens in a document with no page above it. */
export function notifyParent(message: FrameMessage, win: FrameWindow | undefined = typeof window === 'undefined' ? undefined : (window as unknown as FrameWindow)): void {
  if (!win || (win as unknown) === (win.parent as unknown)) return
  win.parent.postMessage(message, '*')   // this document is sandboxed (an opaque origin), so the page's origin cannot be named as the target
}
