import type { ErrorObject, ValidateFunction } from 'ajv'
import dashboardSchema from '../../schemas/dashboard.schema.json'
import reportSchema from '../../schemas/report.schema.json'
import { NAME, VIEW, ajv, classifiers, defs, isObject, join, at, part, pathOf, props, run, Issues } from './common'
import type { Area, Json, ViewsIssue } from './common'
import { checkWidgets } from './widgets'
import type { Context, DrillTarget, WidgetInfo } from './widgets'
import { MINIMUMS } from './limits'

/**
 * The validators of `dashboard/1` and `report/1` (plan 32 section 3, DBR02): the same checks, codes and paths as the .NET loaders of `DbDataKit.Views`, from the same JSON Schemas (`core`, `dashboard`,
 * `report`) plus the rules a schema cannot say (the default layout places every widget, tiles do not overlap, a parameter is declared). `conformance/views` holds the documents and the expected code and path
 * of each refusal; the .NET and the TypeScript tests run the same files. What needs the catalog (an encoded field the model lacks) is not here: it is the engine's to say at publish.
 */

/** The one number of both kinds (`ViewsFormat.Current` in .NET); a test compares it with the schemas and the lock. */
export const VIEWS_FORMAT = { current: 1, previous: 1 } as const

export type ViewKind = 'dashboard' | 'report'

export interface ViewsOptions {
  /** The kind the caller wants: a file of the other kind is refused saying what it is. */
  kind?: ViewKind
  /** The file's name, which a refusal of a newer format names. */
  file?: string
  /** The parameters of the documents a drill may lead to, keyed `dashboard:name`, `report:name` or `view:name`; without it a drill's parameters are not checked. */
  targets?: Record<string, DrillTarget[]>
}

function top(schema: unknown): ValidateFunction {
  const copy = JSON.parse(JSON.stringify(schema)) as Json
  delete copy.$id
  const properties = copy.properties as Json
  properties.widgets = {}
  properties.layouts = {}
  return ajv.compile({ ...copy, $defs: defs })
}

const layoutChecks: Record<string, ValidateFunction> = { reflow: part(defs.layout_reflow!), canvas: part(defs.layout_canvas!), flow: part(defs.layout_flow!) }
const topChecks: Record<ViewKind, ValidateFunction> = { dashboard: top(dashboardSchema), report: top(reportSchema) }

const KEYS: Record<ViewKind, string[]> = { dashboard: props(dashboardSchema as Json), report: props(reportSchema as Json) }
const LAYOUT_KEYS: Record<string, string[]> = { reflow: props(defs.layout_reflow!), canvas: props(defs.layout_canvas!), flow: props(defs.layout_flow!) }
const LAYOUT_COMMON = LAYOUT_KEYS.reflow!.filter((k) => LAYOUT_KEYS.canvas!.includes(k) && LAYOUT_KEYS.flow!.includes(k))
const WIDGET_OWN_KEYS = ['type', 'title', 'encodings', 'query', 'format', 'formats', 'parameter', 'parameters', 'mark', 'columns', 'text', 'hash', 'label', 'values', 'control', 'link_text']
/** The tokens header and footer text may hold (plus `{params.name}` of a declared parameter). */
export const RUNNING_TOKENS = ['title', 'page', 'pages', 'asof', 'viewer']
export { MINIMUMS }
const EPSILON = 1e-9
const TOKEN = /\{([^{}]*)\}/g

/** The codes of the document and of its layouts: a key of the other kind, of another layout type; a selector, a page size, a canvas. */
classifiers.push((e: ErrorObject, area: Area, base: string) => {
  const rel = e.instancePath
  const prop = e.keyword === 'additionalProperties' ? String((e.params as { additionalProperty: string }).additionalProperty) : undefined
  if (area.kind === 'top') {
    if (prop === undefined) return undefined
    const of = area.of as ViewKind
    const other = KEYS[of === 'dashboard' ? 'report' : 'dashboard']
    if (rel === '' && other.includes(prop) && !KEYS[of].includes(prop))
      return { code: 'DBD-VIEW-035', path: pathOf(base, `${rel}/${prop}`), message: `\`${prop}\` belongs to a ${of === 'dashboard' ? 'report' : 'dashboard'}, not to a ${of}.` }
    return undefined
  }
  if (area.kind !== 'layout') return undefined
  const type = String(area.type)
  if (prop !== undefined) {
    const path = pathOf(base, `${rel}/${prop}`)
    const owner = ['reflow', 'canvas', 'flow'].find((t) => t !== type && LAYOUT_KEYS[t]!.includes(prop) && !LAYOUT_COMMON.includes(prop))
    if (rel === '' && owner) return { code: 'DBD-VIEW-024', path, message: `\`${prop}\` belongs to a ${owner} layout; this layout is a ${type} layout.` }
    if (type === 'reflow' && prop === 'layer' && /^\/tiles\/\d+$/.test(rel)) return { code: 'DBD-VIEW-024', path, message: '`layer` belongs to a canvas layout.' }
    if (/^\/widget_options\/[^/]+$/.test(rel) && WIDGET_OWN_KEYS.includes(prop)) return { code: 'DBD-VIEW-032', path, message: `A layout restyles a widget and never changes it: \`${prop}\` is part of the widget.` }
    return undefined
  }
  if (e.keyword === 'required') return undefined
  const path = pathOf(base, rel)
  const message = `${e.message ?? 'is not valid'}`
  if (rel.startsWith('/when')) return { code: 'DBD-VIEW-033', path, message: `A selector: ${message}.` }
  if (type === 'flow' && rel.startsWith('/page/size')) return { code: 'DBD-VIEW-030', path, message: `A page size: ${message}.` }
  if (type === 'canvas' && (rel.startsWith('/canvas/') || rel === '/overflow' || rel === '/min_scale' || rel === '/max_scale')) return { code: 'DBD-VIEW-029', path, message: `A canvas: ${message}.` }
  return undefined
})

// ---------------------------------------------------------------------------------------------------------------------------------------------
// the document

interface ParamInfo { name: string; required: boolean; hasDefault: boolean }
interface LayoutInfo { name: string; type: string; isDefault: boolean; placed: string[]; when: Json | null; path: string }

export function validateViewsDocument(document: unknown, options: ViewsOptions = {}): ViewsIssue[] {
  const issues = new Issues()
  if (!isObject(document)) { issues.add('DBD-106', '', 'A dashboard or a report is a mapping with `schemaVersion`, `kind`, `title`, `widgets` and `layouts`.'); return issues.list }
  const fileKind: ViewKind | undefined = document.kind === 'dashboard' || document.kind === 'report' ? document.kind : undefined
  const forKind = fileKind ?? options.kind ?? 'dashboard'
  const file = options.file ?? 'the file'

  // the format first, so a newer one is refused with its own words
  const version = document.schemaVersion
  if (version === undefined) { issues.add('DBD-105', 'schemaVersion', 'Required key `schemaVersion` is missing.'); return issues.list }
  if (typeof version !== 'number' || !Number.isInteger(version) || version < 1) { issues.add('DBD-111', 'schemaVersion', '`schemaVersion` must be a positive whole number.'); return issues.list }
  if (version > VIEWS_FORMAT.current || version < VIEWS_FORMAT.previous) {
    issues.add('DBD-110', 'schemaVersion', version > VIEWS_FORMAT.current
      ? `the ${forKind} '${file}' is in format ${version}, and this program reads format ${VIEWS_FORMAT.current} of ${forKind}. Upgrade this program; the file was written by a newer version.`
      : `the ${forKind} '${file}' is in format ${version}, which this program does not read (it reads format ${VIEWS_FORMAT.previous} to ${VIEWS_FORMAT.current} of ${forKind}).`)
    return issues.list
  }
  if (document.kind === undefined) { issues.add('DBD-105', 'kind', 'Required key `kind` is missing.'); return issues.list }
  if (!fileKind) { issues.add('DBD-106', 'kind', '`kind` is one of: dashboard, report.'); return issues.list }
  if (options.kind && options.kind !== fileKind) { issues.add(VIEW('001'), 'kind', `'${file}' is a ${fileKind}, not a ${options.kind}.`); return issues.list }

  run(topChecks[fileKind], document, { kind: 'top', of: fileKind }, '', issues)
  const declared = parameters(document.parameters, issues)
  const context: Context = { kind: fileKind, declared, targets: options.targets, widgetIds: null }
  const widgets = document.widgets === undefined ? null : checkWidgets(document.widgets, context, issues)
  if (widgets) context.widgetIds = new Set(widgets.map((w) => w.id))
  const layouts = document.layouts === undefined ? null : checkLayouts(document.layouts, widgets, context, issues)
  if (!widgets || !layouts || issues.hasErrors) return issues.list
  checkDocument(fileKind, document, widgets, layouts, parameterInfo(document.parameters), issues)
  return issues.list
}

export const validateDashboard = (document: unknown, options: Omit<ViewsOptions, 'kind'> = {}) => validateViewsDocument(document, { ...options, kind: 'dashboard' })
export const validateReport = (document: unknown, options: Omit<ViewsOptions, 'kind'> = {}) => validateViewsDocument(document, { ...options, kind: 'report' })

function parameterInfo(raw: unknown): ParamInfo[] {
  return Array.isArray(raw) ? raw.filter(isObject).map((p) => ({ name: String(p.name), required: p.required === true, hasDefault: 'default' in p })) : []
}

function parameters(raw: unknown, issues: Issues): Set<string> {
  const declared = new Set<string>()
  if (!Array.isArray(raw)) return declared
  raw.forEach((p, i) => {
    if (!isObject(p)) return
    const path = at('parameters', i)
    const name = typeof p.name === 'string' && NAME.test(p.name) ? p.name : undefined
    const type = p.type
    if ('default' in p && typeof type === 'string') {
      const fits = (v: unknown) => type === 'string' ? typeof v === 'string' : type === 'integer' ? typeof v === 'number' && Number.isInteger(v) : type === 'number' ? typeof v === 'number' : type === 'boolean' ? typeof v === 'boolean' : true
      const dp = join(path, 'default')
      if (p.many === true) {
        if (!Array.isArray(p.default)) issues.add('DBD-106', dp, 'A parameter with `many: true` has a list as its default.')
        else for (const v of p.default) if (isObject(v) || Array.isArray(v) || !fits(v)) issues.add('DBD-106', dp, `\`${String(v)}\` is not a value of type ${type}.`)
      } else if (Array.isArray(p.default)) issues.add('DBD-106', dp, 'A default is one value; make the parameter `many: true` for a list.')
      else if (isObject(p.default) || !fits(p.default)) issues.add('DBD-106', dp, `\`${String(p.default)}\` is not a value of type ${type}.`)
    }
    if (name && type !== undefined) {
      if (declared.has(name)) issues.add(VIEW('020'), join(path, 'name'), `The parameter \`${name}\` is declared twice.`)
      else declared.add(name)
    }
  })
  return declared
}

// ---- layouts ----

function checkLayouts(raw: unknown, widgets: WidgetInfo[] | null, context: Context, issues: Issues): LayoutInfo[] | null {
  if (!Array.isArray(raw)) { issues.add('DBD-106', 'layouts', '`layouts` must be a list.'); return null }
  if (raw.length === 0 || raw.length > 20) { issues.add('DBD-106', 'layouts', raw.length === 0 ? '`layouts` must have at least 1 entry.' : '`layouts` has at most 20 entries.'); return null }
  const list: LayoutInfo[] = []
  const names = new Set<string>()
  raw.forEach((l, i) => {
    const info = checkLayout(l, at('layouts', i), widgets, context, issues)
    if (!info) return
    if (names.has(info.name)) { issues.add(VIEW('020'), join(info.path, 'name'), `Two layouts are called \`${info.name}\`; a layout's name is unique.`); return }
    names.add(info.name)
    list.push(info)
  })
  return list
}

interface Tile { widget: string; x: number; y: number; w: number; h: number; layer: number; minW?: number; minH?: number; path: string }

function checkLayout(raw: unknown, path: string, widgets: WidgetInfo[] | null, context: Context, issues: Issues): LayoutInfo | null {
  if (!isObject(raw)) { issues.add('DBD-106', path, 'A layout must be a mapping.'); return null }
  const type = raw.type
  if (type === undefined) issues.add('DBD-105', join(path, 'type'), 'Required key `type` is missing.')
  else if (type !== 'reflow' && type !== 'canvas' && type !== 'flow') issues.add('DBD-106', join(path, 'type'), "A layout's `type` is one of: reflow, canvas, flow.")
  if (type !== 'reflow' && type !== 'canvas' && type !== 'flow') return null
  const before = issues.list.length
  run(layoutChecks[type]!, raw, { kind: 'layout', type }, path, issues)
  const schemaOk = issues.list.length === before
  const name = typeof raw.name === 'string' && NAME.test(raw.name) ? raw.name : undefined

  // selectors: a range with its minimum above its maximum matches nothing
  const when = isObject(raw.when) ? raw.when : null
  if (when) for (const key of ['width', 'height', 'aspect'] as const) {
    const r = when[key]
    if (isObject(r) && typeof r.min === 'number' && typeof r.max === 'number' && r.min > r.max) issues.add(VIEW('081'), join(join(path, 'when'), key), `The range has its minimum ${r.min} above its maximum ${r.max}, so it matches nothing.`, 'warning')
  }
  if (isObject(raw.widget_options)) for (const id of Object.keys(raw.widget_options))
    if (context.widgetIds && !context.widgetIds.has(id)) issues.add(VIEW('025'), join(join(path, 'widget_options'), id), `\`${id}\` is not a widget of this document.`)

  const placed: string[] = []
  const place = (id: unknown, p: string): boolean => {
    if (typeof id !== 'string' || !NAME.test(id)) return false
    if (context.widgetIds && !context.widgetIds.has(id)) { issues.add(VIEW('025'), p, `The layout places \`${id}\`, which is not a widget of this document.`); return false }
    if (placed.includes(id)) { issues.add(VIEW('020'), p, `The layout places \`${id}\` twice.`); return false }
    placed.push(id)
    return true
  }
  if (type === 'flow') flow(raw, path, place, context, issues)
  else if (schemaOk) {
    const canvas = type === 'canvas' ? canvasOf(raw, path, issues) : undefined
    const tiles: Tile[] = []
    ;(raw.tiles as Json[]).forEach((t, i) => {
      const tp = at(join(path, 'tiles'), i)
      if (!place(t.widget, join(tp, 'widget'))) return
      const min = isObject(t.min) ? t.min : {}
      tiles.push({ widget: t.widget as string, x: t.x as number, y: t.y as number, w: t.w as number, h: t.h as number, layer: typeof t.layer === 'number' ? t.layer : 0, minW: min.w as number | undefined, minH: min.h as number | undefined, path: tp })
    })
    geometry(type, raw, tiles, canvas, widgets, issues)
  }
  if (!name) return null
  return { name, type, isDefault: raw.default === true, placed, when, path }
}

interface Canvas { width: number; height: number; unit: string; ppu: number }

function canvasOf(raw: Json, path: string, issues: Issues): Canvas | undefined {
  const c = raw.canvas as Json
  const unit = typeof c.unit === 'string' ? c.unit : 'px'
  const ppu = unit === 'mm' ? 96 / 25.4 : unit === 'in' ? 96 : 1
  const width = c.width as number
  const height = c.height as number
  for (const [side, value] of [['width', width * ppu], ['height', height * ppu]] as const)
    if (value < 320 - EPSILON || value > 8000 + EPSILON) issues.add(VIEW('029'), join(join(path, 'canvas'), side), `A canvas is 320 to 8000 px on each side (96 px per inch); the ${side} here is ${value.toFixed(2)} px.`)
  const overflow = raw.overflow ?? 'scale'
  const min = raw.min_scale as number | undefined
  const max = raw.max_scale as number | undefined
  if (overflow === 'scroll' && (min !== undefined || max !== undefined)) issues.add(VIEW('029'), join(path, min !== undefined ? 'min_scale' : 'max_scale'), 'A canvas with `overflow: scroll` is shown at scale 1: `min_scale` and `max_scale` are for `overflow: scale`.')
  if ((min ?? 0.5) > (max ?? 4)) issues.add(VIEW('029'), join(path, min !== undefined ? 'min_scale' : 'max_scale'), `\`min_scale\` (${min ?? 0.5}) is above \`max_scale\` (${max ?? 4}).`)
  return { width, height, unit, ppu }
}

function geometry(type: string, raw: Json, tiles: Tile[], canvas: Canvas | undefined, widgets: WidgetInfo[] | null, issues: Issues): void {
  const grid = raw.grid as { columns: number; rows?: number } | undefined
  tiles.forEach((t, i) => {
    if (type === 'reflow' && grid) {
      if (t.x + t.w > grid.columns) issues.add(VIEW('026'), t.path, `The tile for \`${t.widget}\` ends at column ${t.x + t.w}; the grid has ${grid.columns} columns.`)
      else if (grid.rows !== undefined && t.y + t.h > grid.rows) issues.add(VIEW('026'), t.path, `The tile for \`${t.widget}\` ends at row ${t.y + t.h}; the grid has ${grid.rows} rows.`)
    }
    if (type === 'canvas' && canvas) {
      if (t.x + t.w > canvas.width + EPSILON || t.y + t.h > canvas.height + EPSILON) issues.add(VIEW('026'), t.path, `The tile for \`${t.widget}\` reaches (${t.x + t.w}, ${t.y + t.h}) and the canvas is ${canvas.width} by ${canvas.height} ${canvas.unit}.`)
      const widgetType = widgets?.find((w) => w.id === t.widget)?.type
      const min = widgetType ? MINIMUMS[widgetType] : undefined
      if (widgetType && min) {
        const needW = Math.max(min[0], t.minW ?? 0)
        const needH = Math.max(min[1], t.minH ?? 0)
        if (t.w * canvas.ppu < needW - EPSILON || t.h * canvas.ppu < needH - EPSILON)
          issues.add(VIEW('028'), t.path, `The ${widgetType} \`${t.widget}\` is ${(t.w * canvas.ppu).toFixed(2)} by ${(t.h * canvas.ppu).toFixed(2)} px at scale 1; a ${widgetType} needs at least ${needW} by ${needH}.`)
      }
    }
    for (let j = 0; j < i; j++) {
      const o = tiles[j]!
      if (type === 'canvas' && o.layer !== t.layer) continue
      if (t.x < o.x + o.w - EPSILON && o.x < t.x + t.w - EPSILON && t.y < o.y + o.h - EPSILON && o.y < t.y + t.h - EPSILON)
        issues.add(VIEW('027'), t.path, `The tiles for \`${t.widget}\` and \`${o.widget}\` overlap.`)
    }
  })
}

function flow(raw: Json, path: string, place: (id: unknown, path: string) => boolean, context: Context, issues: Issues): void {
  for (const part of ['header', 'footer'] as const) {
    const h = raw[part]
    if (!isObject(h)) continue
    for (const [side, value] of Object.entries(h)) {
      if (typeof value !== 'string') continue
      const p = join(join(path, part), side)
      for (const m of value.matchAll(TOKEN)) {
        const token = m[1]!
        if (RUNNING_TOKENS.includes(token)) continue
        if (token.startsWith('params.')) { if (!context.declared.has(token.slice(7))) issues.add(VIEW('008'), p, `\`{${token}}\` names a parameter the document does not declare.`); continue }
        issues.add(VIEW('030'), p, `\`{${token}}\` is not a token of header or footer text.`)
      }
    }
  }
  if (!Array.isArray(raw.sections)) return
  raw.sections.forEach((s, i) => {
    if (!isObject(s) || !Array.isArray(s.blocks)) return
    s.blocks.forEach((b, j) => {
      if (!isObject(b)) return
      const bp = at(join(at(join(path, 'sections'), i), 'blocks'), j)
      if (b.widget !== undefined) place(b.widget, join(bp, 'widget'))
      if (Array.isArray(b.row)) {
        let total = 0
        b.row.forEach((item, k) => {
          if (!isObject(item)) return
          place(item.widget, join(at(join(bp, 'row'), k), 'widget'))
          if (typeof item.span === 'number') total += item.span
        })
        if (total > 12) issues.add(VIEW('030'), join(bp, 'row'), `The spans of a row add up to ${total}; a row has 12 columns.`)
      }
    })
  })
}

// ---- the rules across the document ----

function checkDocument(kind: ViewKind, document: Json, widgets: WidgetInfo[], layouts: LayoutInfo[], parameters: ParamInfo[], issues: Issues): void {
  const defaults = layouts.filter((l) => l.isDefault)
  if (defaults.length !== 1) {
    issues.add(VIEW('021'), defaults.length > 1 ? defaults[1]!.path : 'layouts', defaults.length === 0 ? 'No layout says `default: true`; exactly one does.' : `${defaults.length} layouts say \`default: true\`; exactly one does.`)
  } else {
    const def = defaults[0]!
    const allowed = kind === 'dashboard' ? ['reflow', 'canvas'] : ['flow']
    if (!allowed.includes(def.type))
      issues.add(VIEW('034'), def.path, kind === 'dashboard'
        ? `The default layout of a dashboard is a fit layout (reflow or canvas); \`${def.name}\` is a flow layout.`
        : `The default layout of a report is a flow layout; \`${def.name}\` is a ${def.type} layout.`)
    const missing = widgets.filter((w) => !def.placed.includes(w.id) && layouts.some((l) => l.placed.includes(w.id))).map((w) => w.id)
    if (missing.length > 0) issues.add(VIEW('022'), def.path, `The default layout \`${def.name}\` does not place ${missing.map((m) => `\`${m}\``).join(', ')}; the default places every widget.`)
  }
  for (const w of widgets.filter((w) => layouts.every((l) => !l.placed.includes(w.id)))) issues.add(VIEW('023'), `widgets.${w.id}`, `No layout places the widget \`${w.id}\`; it would never be shown.`)
  for (const p of parameters.filter((p) => p.required && !p.hasDefault)) {
    const controls = widgets.filter((w) => w.type === 'control' && w.raw.parameter === p.name).map((w) => w.id)
    if (controls.length === 0) continue
    for (const l of layouts.filter((l) => !controls.some((c) => l.placed.includes(c))))
      issues.add(VIEW('031'), l.path, `The layout \`${l.name}\` omits the control of the required parameter \`${p.name}\` (no default).`)
  }
  const kiosk = isObject(document.kiosk) ? document.kiosk.layout : undefined
  if (typeof kiosk === 'string' && layouts.every((l) => l.name !== kiosk)) issues.add('DBD-106', 'kiosk.layout', `\`kiosk.layout\` names \`${kiosk}\`; the layouts are ${layouts.map((l) => `\`${l.name}\``).join(', ')}.`)
  const sameWhen = (a: Json, b: Json) => JSON.stringify(normalizeWhen(a)) === JSON.stringify(normalizeWhen(b))
  for (let i = 0; i < layouts.length; i++)
    for (let j = 0; j < i; j++)
      if (layouts[i]!.when && layouts[j]!.when && sameWhen(layouts[i]!.when!, layouts[j]!.when!)) {
        issues.add(VIEW('080'), layouts[i]!.path, `The layouts \`${layouts[j]!.name}\` and \`${layouts[i]!.name}\` have equal selectors; \`${layouts[i]!.name}\` is never reached by selection.`, 'warning')
        break
      }
}

function normalizeWhen(when: Json): unknown {
  const medium = when.medium === undefined ? undefined : [...(Array.isArray(when.medium) ? (when.medium as string[]) : [when.medium as string])].sort()
  const range = (r: unknown) => (isObject(r) ? { min: r.min ?? null, max: r.max ?? null } : null)
  return { width: range(when.width), height: range(when.height), orientation: when.orientation ?? null, aspect: range(when.aspect), medium: medium ?? null }
}
