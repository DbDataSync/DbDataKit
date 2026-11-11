import type { ValidateFunction } from 'ajv'
import {
  CHART_CHANNELS, COMPUTING, Issues, MARKS, NAME, VIEW, ajv, at, defs, isObject, isSafeTextLink, join, part, run,
} from './common'
import type { Json, ViewsIssue } from './common'
import { checkFormat } from './format'
import coreSchema from '../../schemas/core.schema.json'

/**
 * The validator of the widget vocabulary (plan 32 section 2, DBR01): the shape check of a widgets map, as `ViewsLoader.LoadWidgets` does it in .NET. A widget is a type, its encodings and a query; the
 * Vega-Lite subset of a chart is closed and computes nothing; text is the safe markdown subset; an image is a raster held by hash; an explicit query must output every field the widget encodes.
 */

/** The parameters of a document a drill may lead to. */
export interface DrillTarget { name: string; required?: boolean; default?: unknown }

const WIDGET_TYPES = ['metric', 'chart', 'matrix', 'table', 'text', 'image', 'control'] as const
const noProperties = (...names: string[]) => (copy: Json) => { for (const n of names) (copy.properties as Json)[n] = {} }
const widgetChecks: Record<string, ValidateFunction> = {
  metric: part(defs.widget_metric!, noProperties('query')),
  chart: part(defs.widget_chart!, noProperties('query', 'mark', 'encodings')),
  matrix: part(defs.widget_matrix!, noProperties('query')),
  table: part(defs.widget_table!, noProperties('query')),
  text: part(defs.widget_text!),
  image: part(defs.widget_image!),
  control: part(defs.widget_control!),
}
const queryChecks: Record<string, ValidateFunction> = {
  derived: part(defs.query_derived!), semantic: part(defs.query_explicit_semantic!), saved: part(defs.query_explicit_saved!), store: part(defs.query_explicit_store!), ref: part(defs.query_explicit_ref!),
}
const channelCheck = ajv.getSchema(`${(coreSchema as { $id: string }).$id}#/$defs/chart_channel`)!

export const RAW_HTML = /<[A-Za-z/!?]/
export const MARKDOWN_IMAGE = /!\[[^\]]*\]\([^)]*\)/
export const MARKDOWN_LINK = /\[[^\]]*\]\(([^)]*)\)/g
export const PARAM_USE = /\{\{\s*params\.([A-Za-z0-9_-]+)\s*\}\}/g

export interface WidgetInfo { id: string; type: string; raw: Json }

/** What reading widgets needs of the rest of the document. */
export interface Context { declared: Set<string>; targets?: Record<string, DrillTarget[]>; widgetIds: Set<string> | null }

/**
 * The shape check of a widgets map alone: the same checks a document runs on its `widgets`, for a map an assistant's answer, a portal block or a pin holds. `parameters` are the names the widgets may use.
 * What needs the catalog (an encoded field the model lacks) is the engine's to say at publish.
 */
export function validateWidgets(widgets: unknown, parameters: string[] = [], base = 'widgets', targets?: Record<string, DrillTarget[]>): ViewsIssue[] {
  const issues = new Issues()
  checkWidgets(widgets, { declared: new Set(parameters), widgetIds: null, targets }, issues, base)
  return issues.list
}

// ---- widgets ----

export function checkWidgets(raw: unknown, context: Context, issues: Issues, base = 'widgets'): WidgetInfo[] | null {
  if (!isObject(raw)) { issues.add('DBD-106', base, '`widgets` must be a mapping.'); return null }
  const entries = Object.entries(raw)
  if (entries.length === 0) { issues.add('DBD-106', base, '`widgets` defines at least one widget.'); return null }
  if (entries.length > 100) { issues.add('DBD-106', base, 'A document has at most 100 widgets.'); return null }
  const list: WidgetInfo[] = []
  for (const [id, widget] of entries) {
    if (!NAME.test(id)) { issues.add('DBD-106', join(base, id), `\`${id}\` is not a widget id.`); continue }
    const info = checkWidget(id, widget, join(base, id), context, issues)
    if (info) list.push(info)
  }
  return list
}

interface Encoded { channel: string; field: string; path: string }

function checkWidget(id: string, raw: unknown, path: string, context: Context, issues: Issues): WidgetInfo | null {
  if (!isObject(raw)) { issues.add('DBD-106', path, `The widget \`${id}\` must be a mapping.`); return null }
  const type = raw.type
  if (type === undefined) { issues.add('DBD-105', join(path, 'type'), 'Required key `type` is missing.'); return null }
  if (type === 'embed') { issues.add(VIEW('006'), join(path, 'type'), 'The widget type `embed` is reserved: this format does not read it yet.'); return null }
  if (typeof type !== 'string' || !(WIDGET_TYPES as readonly string[]).includes(type)) { issues.add('DBD-107', join(path, 'type'), `\`${String(type)}\` is not a widget type.`); return null }

  run(widgetChecks[type]!, raw, { kind: 'widget', type }, path, issues)
  formats(raw, type, path, issues)
  const fields: Encoded[] = []
  const encodings = isObject(raw.encodings) ? raw.encodings : undefined
  const ep = join(path, 'encodings')
  switch (type) {
    case 'metric':
      for (const channel of ['value', 'compare'] as const) if (encodings && typeof encodings[channel] === 'string') fields.push({ channel, field: encodings[channel] as string, path: join(ep, channel) })
      break
    case 'chart': chart(raw, path, fields, issues); break
    case 'matrix':
      for (const axis of ['rows', 'columns'] as const) {
        const list = encodings?.[axis]
        if (Array.isArray(list)) list.forEach((f, i) => {
          if (typeof f === 'string') fields.push({ channel: axis, field: f, path: at(join(ep, axis), i) })
          else if (isObject(f) && typeof f.field === 'string') fields.push({ channel: axis, field: f.field, path: join(at(join(ep, axis), i), 'field') })
        })
        if (Array.isArray(list)) {
          // one level of an axis carries a sort: it is the sort of the axis (the second is the one refused, as the .NET loader does)
          const sorted = list.map((f, i) => (isObject(f) && f.sort !== undefined ? i : -1)).filter((i) => i >= 0)
          if (sorted.length > 1) issues.add('DBD-106', join(at(join(ep, axis), sorted[1]!), 'sort'), `Only one level of the ${axis} carries a sort: it is the sort of the axis.`)
        }
      }
      if (Array.isArray(encodings?.values)) encodings.values.forEach((v, i) => { if (isObject(v) && typeof v.field === 'string') fields.push({ channel: 'values', field: v.field, path: join(at(join(ep, 'values'), i), 'field') }) })
      break
    case 'table':
      if (Array.isArray(encodings?.columns)) encodings.columns.forEach((c, i) => { if (isObject(c) && typeof c.field === 'string') fields.push({ channel: 'columns', field: c.field, path: join(at(join(ep, 'columns'), i), 'field') }) })
      break
    case 'text': text(raw, path, context, issues); break
    case 'control':
      if (typeof raw.parameter === 'string' && NAME.test(raw.parameter) && !context.declared.has(raw.parameter))
        issues.add(VIEW('008'), join(path, 'parameter'), `The control edits \`${raw.parameter}\`, a parameter the document does not declare.`)
      break
  }
  if (type === 'metric' || type === 'chart' || type === 'matrix' || type === 'table') {
    if (raw.query === undefined) issues.add('DBD-105', join(path, 'query'), 'Required key `query` is missing.')
    else {
      const query = checkQuery(raw.query, join(path, 'query'), context, issues)
      if (query.explicit && query.outputs)
        for (const f of fields.filter((f) => !query.outputs!.has(f.field)))
          issues.add(VIEW('005'), f.path, `\`${f.field}\` is encoded as ${f.channel} but is not an output of the query (it gives ${[...query.outputs].sort().join(', ')}).`)
    }
    if (raw.on_select !== undefined) onSelect(raw.on_select, join(path, 'on_select'), context, issues)
    if (raw.drill !== undefined) drill(raw.drill, join(path, 'drill'), context, issues)
  }
  return { id, type, raw }
}

/** The formats a widget names: a metric's, a table column's, a matrix value's and axis level's, a chart channel's; the words are `ViewFormat.Check`'s in .NET. */
function formats(raw: Json, type: string, path: string, issues: Issues): void {
  const one = (format: unknown, where: string) => {
    if (format === undefined) return
    const problem = checkFormat(format)
    if (!problem) return
    issues.add('DBD-106', problem.key !== '' && isObject(format) ? join(where, problem.key) : where, problem.message)
  }
  const ep = join(path, 'encodings')
  const encodings = isObject(raw.encodings) ? raw.encodings : undefined
  if (type === 'metric') one(raw.format, join(path, 'format'))
  const columns = encodings?.columns
  if (type === 'table' && Array.isArray(columns))
    columns.forEach((c, i) => { if (isObject(c)) one(c.format, join(at(join(ep, 'columns'), i), 'format')) })
  if (type === 'matrix') {
    if (Array.isArray(encodings?.values)) encodings.values.forEach((v, i) => { if (isObject(v)) one(v.format, join(at(join(ep, 'values'), i), 'format')) })
    for (const axis of ['rows', 'columns'] as const) {
      const list = encodings?.[axis]
      if (Array.isArray(list)) list.forEach((f, i) => { if (isObject(f)) one(f.format, join(at(join(ep, axis), i), 'format')) })
    }
  }
  if (type === 'chart' && encodings)
    for (const [channel, value] of Object.entries(encodings)) {
      const items = channel === 'tooltip' && Array.isArray(value) ? value.map((v, i) => [v, at(join(ep, channel), i)] as const) : ([[value, join(ep, channel)]] as const)
      for (const [item, where] of items) if (isObject(item)) one(item.format, join(where, 'format'))
    }
}

/** A chart's mark and encodings: the Vega-Lite subset, closed. Anything that fetches or computes is refused by name (no computation in the chart spec). */
function chart(raw: Json, path: string, fields: Encoded[], issues: Issues): void {
  const computing = (p: string, key: string) => issues.add(VIEW('004'), p, `\`${key}\` isn't allowed in a chart: it fetches data or computes a value; computation belongs in the widget's query.`)
  const mark = raw.mark
  const mp = join(path, 'mark')
  if (isObject(mark)) {
    for (const key of Object.keys(mark)) {
      if (COMPUTING.includes(key)) computing(join(mp, key), key)
      else if (!['type', 'tooltip', 'point'].includes(key)) issues.add('DBD-104', join(mp, key), `Unknown key \`${key}\` in a mark.`)
    }
    if (mark.type === undefined) issues.add('DBD-105', join(mp, 'type'), 'Required key `type` is missing.')
    else if (typeof mark.type !== 'string' || !MARKS.includes(mark.type)) issues.add(VIEW('004'), join(mp, 'type'), `A mark's type is one of: ${MARKS.join(', ')}.`)
    for (const k of ['tooltip', 'point']) if (k in mark && typeof mark[k] !== 'boolean') issues.add('DBD-106', join(mp, k), `\`${k}\` is true or false.`)
  } else if (mark !== undefined && !(typeof mark === 'string' && MARKS.includes(mark))) issues.add(VIEW('004'), mp, `\`mark\` is one of: ${MARKS.join(', ')}.`)

  const encodings = raw.encodings
  const ep = join(path, 'encodings')
  if (encodings === undefined) return
  if (!isObject(encodings)) { issues.add('DBD-106', ep, '`encodings` must be a mapping.'); return }
  for (const [channel, value] of Object.entries(encodings)) {
    const cp = join(ep, channel)
    if (!CHART_CHANNELS.includes(channel)) {
      if (COMPUTING.includes(channel)) computing(cp, channel)
      else issues.add('DBD-104', cp, `Unknown key \`${channel}\` in the encodings of a chart.`)
      continue
    }
    const items: [unknown, string][] = channel === 'tooltip' && Array.isArray(value) ? value.map((v, i) => [v, at(cp, i)]) : [[value, cp]]
    for (const [item, p] of items) {
      if (typeof item === 'string' && item.length > 0) fields.push({ channel, field: item, path: p })
      else if (isObject(item)) {
        run(channelCheck, item, { kind: 'channel' }, p, issues)
        if (typeof item.field === 'string' && item.field.length > 0) fields.push({ channel, field: item.field, path: join(p, 'field') })
      } else issues.add('DBD-106', p, 'A channel is a field name, or a mapping with `field`, `type`, `sort`, `stack` and `title`.')
    }
  }
}

function text(raw: Json, path: string, context: Context, issues: Issues): void {
  if (typeof raw.text !== 'string' || raw.text.length === 0) return
  const tp = join(path, 'text')
  for (const line of raw.text.split('\n')) {
    if (RAW_HTML.test(line)) issues.add(VIEW('002'), tp, 'Text has no HTML or script: this looks like a tag.')
    if (MARKDOWN_IMAGE.test(line)) issues.add(VIEW('002'), tp, 'Text carries no image: use an `image` widget, which holds the picture by hash.')
    for (const m of line.matchAll(MARKDOWN_LINK)) {
      const index = m.index ?? 0
      if (index > 0 && line[index - 1] === '!') continue
      if (!isSafeTextLink(m[1]!)) issues.add(VIEW('002'), tp, `\`${m[1]}\` is not a link text may carry: only a path on the host that starts with a single \`/\`.`)
    }
    for (const m of line.matchAll(PARAM_USE)) if (!context.declared.has(m[1]!)) issues.add(VIEW('008'), tp, `\`{{params.${m[1]}}}\` names a parameter the document does not declare.`)
  }
}

function onSelect(raw: unknown, path: string, context: Context, issues: Issues): void {
  if (!isObject(raw)) { issues.add('DBD-106', path, '`on_select` must be a mapping.'); return }
  for (const key of Object.keys(raw)) if (!['set', 'from'].includes(key)) issues.add('DBD-104', join(path, key), `Unknown key \`${key}\` in \`on_select\`.`)
  for (const key of ['set', 'from']) if (raw[key] === undefined) issues.add('DBD-105', join(path, key), `Required key \`${key}\` is missing.`)
  if (typeof raw.set === 'string' && NAME.test(raw.set) && !context.declared.has(raw.set)) issues.add(VIEW('008'), join(path, 'set'), `Selecting sets \`${raw.set}\`, a parameter the document does not declare.`)
  else if (raw.set !== undefined && !(typeof raw.set === 'string' && NAME.test(raw.set))) issues.add('DBD-106', join(path, 'set'), '`set` is a name.')
}

function drill(raw: unknown, path: string, context: Context, issues: Issues): void {
  if (!isObject(raw)) { issues.add('DBD-106', path, '`drill` must be a mapping.'); return }
  for (const key of Object.keys(raw)) if (!['to', 'with'].includes(key)) issues.add('DBD-104', join(path, key), `Unknown key \`${key}\` in \`drill\`.`)
  let target: string | undefined
  let targetKind: string | undefined
  if (raw.to === undefined) issues.add('DBD-105', join(path, 'to'), 'Required key `to` is missing.')
  else if (!isObject(raw.to)) issues.add('DBD-106', join(path, 'to'), '`to` must be a mapping.')
  else {
    const tp = join(path, 'to')
    for (const key of Object.keys(raw.to)) if (!['dashboard', 'report', 'view'].includes(key)) issues.add('DBD-104', join(tp, key), `Unknown key \`${key}\` in \`to\`.`)
    const keys = Object.keys(raw.to)
    if (keys.length !== 1) issues.add('DBD-106', tp, '`to` names exactly one of `dashboard`, `report` or `view`.')
    else if (['dashboard', 'report', 'view'].includes(keys[0]!)) {
      const v = raw.to[keys[0]!]
      if (typeof v === 'string' && NAME.test(v)) { target = v; targetKind = keys[0] } else issues.add('DBD-106', join(tp, keys[0]!), 'A drill target is a name.')
    }
  }
  const given = new Set<string>()
  if (raw.with !== undefined) {
    if (!isObject(raw.with)) issues.add('DBD-106', join(path, 'with'), '`with` must be a mapping.')
    else for (const [name, value] of Object.entries(raw.with)) {
      const wp = join(join(path, 'with'), name)
      if (!NAME.test(name)) issues.add('DBD-106', wp, 'A parameter name is lower-case letters, digits, \'-\' and \'_\'.')
      given.add(name)
      if (typeof value !== 'string') { issues.add('DBD-106', wp, 'A value is `$field` of the clicked row, `$params.name`, or a literal.'); continue }
      if (value.startsWith('$params.') && !context.declared.has(value.slice(8))) issues.add(VIEW('008'), wp, `\`${value}\` names a parameter the document does not declare.`)
    }
  }
  const theirs = target && context.targets?.[`${targetKind}:${target}`]
  if (theirs) for (const p of theirs.filter((p) => p.required === true && p.default === undefined && !given.has(p.name)))
    issues.add(VIEW('037'), path, `The drill to ${targetKind} \`${target}\` does not give its required parameter \`${p.name}\`.`)
}

// ---- queries ----

interface QueryInfo { explicit: boolean; outputs: Set<string> | null }

function checkQuery(raw: unknown, path: string, context: Context, issues: Issues): QueryInfo {
  const none: QueryInfo = { explicit: false, outputs: null }
  if (!isObject(raw)) { issues.add('DBD-106', path, '`query` must be a mapping.'); return none }
  const forms = ['semantic', 'saved', 'store', 'ref'].filter((k) => raw[k] !== undefined)
  const mode = raw.mode
  const modeOk = mode === undefined || mode === 'derived' || mode === 'explicit'
  if (!modeOk) issues.add(VIEW('007'), join(path, 'mode'), '`mode` is one of: derived, explicit.')
  if (forms.length > 1) { issues.add(VIEW('007'), path, `A query is one form: ${forms.join(', ')} are written together.`); return none }
  if (forms.length === 0) {
    if (mode === 'explicit') { issues.add(VIEW('007'), join(path, 'mode'), '`mode: explicit` needs the query itself: `semantic`, `saved`, `store` or `ref`.'); return none }
    run(queryChecks.derived!, raw, { kind: 'query' }, path, issues)
    paramMap(raw.params, path, context, issues)
    markers(raw, path, isObject(raw.params) ? raw.params : undefined, context, issues)
    return none
  }
  if (mode !== 'explicit') { issues.add(VIEW('007'), mode === undefined ? path : join(path, 'mode'), `A written query says \`mode: explicit\` (the query has \`${forms[0]}\`).`); return none }
  const form = forms[0]!
  run(queryChecks[form]!, raw, { kind: 'query' }, path, issues)
  const outputs = (value: unknown): Set<string> | null => (Array.isArray(value) && value.length > 0 && value.every((v) => typeof v === 'string' && v.length > 0) ? new Set(value as string[]) : null)
  let found: Set<string> | null = null
  if (form === 'semantic' && isObject(raw.semantic)) {
    const hasRequest = raw.semantic.request !== undefined
    const hasSql = raw.semantic.sql !== undefined
    if (hasRequest === hasSql) issues.add(VIEW('007'), join(path, 'semantic'), '`semantic` has exactly one of `request` (a semantic request) and `sql` (semantic SQL).')
    else if (isObject(raw.semantic.request)) {
      found = new Set<string>()
      for (const key of ['measures', 'attributes'] as const) {
        const entries = raw.semantic.request[key]
        if (Array.isArray(entries)) for (const e of entries) {
          if (typeof e === 'string') found.add(e)
          else if (isObject(e)) { const name = e.as ?? e.measure; if (typeof name === 'string') found.add(name) }
        }
      }
    }
  } else if (form === 'saved' || form === 'ref') found = outputs(raw.outputs)
  else if (form === 'store' && isObject(raw.store)) found = outputs(raw.store.columns)
  paramMap(raw.params, path, context, issues)
  markers(raw, path, isObject(raw.params) ? raw.params : undefined, context, issues)
  return { explicit: true, outputs: found }
}

/** The document parameter each entry of a query's `params` names must be declared. */
function paramMap(raw: unknown, path: string, context: Context, issues: Issues): void {
  if (!isObject(raw)) return
  for (const [name, value] of Object.entries(raw))
    if (typeof value === 'string' && NAME.test(value) && !context.declared.has(value)) issues.add(VIEW('008'), join(join(path, 'params'), name), `\`${value}\` is not a parameter of the document.`)
}

function markers(node: unknown, path: string, map: Json | undefined, context: Context, issues: Issues): void {
  if (Array.isArray(node)) node.forEach((n, i) => markers(n, at(path, i), map, context, issues))
  else if (isObject(node)) {
    const keys = Object.keys(node)
    if (keys.length === 1 && keys[0] === 'param' && typeof node.param === 'string') {
      if (!context.declared.has(node.param) && map?.[node.param] === undefined) issues.add(VIEW('008'), join(path, 'param'), `\`{param: ${node.param}}\` names a parameter the document does not declare.`)
    } else for (const [k, v] of Object.entries(node)) markers(v, join(path, k), map, context, issues)
  }
}

