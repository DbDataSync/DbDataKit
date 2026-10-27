import Ajv2020 from 'ajv/dist/2020'
import type { ErrorObject, ValidateFunction } from 'ajv'
import renderedViewSchema from '../schemas/rendered-view.schema.json'
import vegaSubsetSchema from '../schemas/vega-lite-subset.schema.json'
import viewSchema from '../schemas/view.schema.json'

/** One reason a document is refused. The codes and paths are those of the server's presentation errors (docs/semantic/presentation.md), so a message reads the same wherever it was found. */
export interface ViewIssue {
  code: 'schema_invalid' | 'vega_disallowed' | 'duplicate_view_id' | 'rendered_invalid'
  /** Where, as `$.views[0].vega_lite.mark`. */
  path: string
  message: string
}

const AjvCtor = ((Ajv2020 as unknown as { default?: typeof Ajv2020 }).default ?? Ajv2020) as typeof Ajv2020
// Formats are not validated (the schemas use none), and the schemas are fixed files of this package, so strict mode would only reject the `$id` hosts the semantic library gave its own.
const ajv = new AjvCtor({ strict: false, allErrors: true, validateFormats: false })
const checkView: ValidateFunction = ajv.compile(viewSchema as object)
const checkSubset: ValidateFunction = ajv.compile(vegaSubsetSchema as object)
const checkRendered: ValidateFunction = ajv.compile(renderedViewSchema as object)

// The Vega-Lite subset, as ChartView.cs in DbDataKit.Semantic states it.
const MARKS = ['area', 'bar', 'line', 'point', 'rule', 'text']
const CHANNELS = ['x', 'y', 'color', 'column', 'row', 'tooltip']
const ENCODING_PROPERTIES = ['field', 'type', 'sort', 'stack', 'title']
const TYPES = ['nominal', 'ordinal', 'quantitative', 'temporal']
const TOP_LEVEL = ['mark', 'encoding', 'title', 'width', 'height', '$schema', 'description']
/** Keys that fetch data or compute a value: refused wherever they appear, however deep. */
const COMPUTING = ['data', 'transform', 'aggregate', 'bin', 'timeUnit', 'url', 'datasets', 'layer', 'facet', 'repeat', 'concat', 'params', 'selection']

const isObject = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v)

/** `/views/0/vega_lite` as `$.views[0].vega_lite`. */
function pathOf(instancePath: string, base = '$'): string {
  return base + instancePath.split('/').filter(Boolean).map((s) => (/^\d+$/.test(s) ? `[${s}]` : `.${s.replace(/~1/g, '/').replace(/~0/g, '~')}`)).join('')
}

function ajvIssues(errors: ErrorObject[] | null | undefined, code: ViewIssue['code'], base: string): ViewIssue[] {
  // `oneOf` reports every branch it tried; a repeated place and message is said once.
  const seen = new Set<string>()
  const out: ViewIssue[] = []
  for (const e of errors ?? []) {
    if (e.keyword === 'oneOf') continue
    const extra = e.keyword === 'additionalProperties' ? ` (${String((e.params as { additionalProperty?: string }).additionalProperty)})` : ''
    const issue: ViewIssue = { code, path: pathOf(e.instancePath, base), message: `${e.message ?? 'is not valid'}${extra}` }
    const key = `${issue.path}|${issue.message}`
    if (!seen.has(key)) { seen.add(key); out.push(issue) }
  }
  return out
}

/**
 * The errors of a document that does not match view.schema.json. A view is a matrix or a chart by its `type`, and the schema's `oneOf` reports what both kinds
 * lack; the person wants what the kind they wrote lacks, so each view of a known type is checked against that kind's definition alone.
 */
function viewDocumentErrors(document: unknown): ViewIssue[] {
  const views = isObject(document) && Array.isArray(document.views) ? document.views : []
  const kindOf = (i: number) => { const t = (views[i] as { type?: unknown } | undefined)?.type; return t === 'matrix' || t === 'chart' ? t : undefined }
  const rest = (checkView.errors ?? []).filter((e) => {
    const m = /^\/views\/(\d+)(\/|$)/.exec(e.instancePath)
    return !(m && kindOf(Number(m[1])))
  })
  const issues = ajvIssues(rest, 'schema_invalid', '$')
  views.forEach((_, i) => {
    const kind = kindOf(i)
    if (!kind) return
    const check = ajv.getSchema(`${(viewSchema as { $id: string }).$id}#/$defs/${kind}`)!
    if (!check(views[i])) issues.push(...ajvIssues(check.errors, 'schema_invalid', `$.views[${i}]`))
  })
  return issues
}

/**
 * Whether a chart spec is inside the Vega-Lite subset: marks, channels and encodings, no data and nothing that computes or fetches. The first pass names each
 * offence the way the server does (`vega_disallowed`: a computing key anywhere, a mark, channel or property outside the lists); the second is the subset's JSON
 * Schema, which is closed and also fixes the shape of the values (a sort is a keyword or a list, never an object that aggregates; a mark has no other setting).
 * A spec that passes both has no URL in it and no expression the interpreter could be asked to run.
 */
export function validateVegaLiteSubset(spec: unknown, path = '$.vega_lite'): ViewIssue[] {
  if (!isObject(spec)) return [{ code: 'vega_disallowed', path, message: 'A chart spec is an object.' }]
  const issues: ViewIssue[] = []
  const add = (where: string, message: string) => issues.push({ code: 'vega_disallowed', path: where, message })

  const scan = (node: unknown, where: string) => {
    if (isObject(node)) {
      for (const [key, child] of Object.entries(node)) {
        if (COMPUTING.includes(key)) add(`${where}.${key}`, `'${key}' isn't allowed in a chart spec: it fetches data or computes values, and the renderer supplies the data from the result.`)
        else scan(child, `${where}.${key}`)
      }
    } else if (Array.isArray(node)) node.forEach((child, i) => scan(child, `${where}[${i}]`))
  }
  scan(spec, path)
  for (const key of Object.keys(spec).filter((k) => !TOP_LEVEL.includes(k) && !COMPUTING.includes(k)))
    add(`${path}.${key}`, `'${key}' isn't in the allowed Vega-Lite subset (mark, encoding, title).`)

  const mark = typeof spec.mark === 'string' ? spec.mark : isObject(spec.mark) && typeof spec.mark.type === 'string' ? spec.mark.type : undefined
  if (mark === undefined) add(`${path}.mark`, 'A chart needs a mark.')
  else if (!MARKS.includes(mark)) add(`${path}.mark`, `Mark '${mark}' isn't allowed; use one of ${MARKS.join(', ')}.`)

  if (isObject(spec.encoding)) {
    for (const [channel, definition] of Object.entries(spec.encoding)) {
      const where = `${path}.encoding.${channel}`
      if (!CHANNELS.includes(channel)) { add(where, `Channel '${channel}' isn't allowed; use one of ${CHANNELS.join(', ')}.`); continue }
      // A tooltip may be a list of definitions; every one is held to the same lists.
      const definitions = Array.isArray(definition) ? definition.map((d, i) => [d, `${where}[${i}]`] as const) : [[definition, where] as const]
      for (const [def, defWhere] of definitions) {
        if (!isObject(def)) continue
        for (const key of Object.keys(def).filter((k) => !ENCODING_PROPERTIES.includes(k) && !COMPUTING.includes(k)))
          add(`${defWhere}.${key}`, `Encoding property '${key}' isn't allowed; use field, type, sort, stack or title.`)
        if (typeof def.type === 'string' && !TYPES.includes(def.type)) add(`${defWhere}.type`, `Type '${def.type}' isn't one of ${TYPES.join(', ')}.`)
      }
    }
  }
  if (issues.length > 0) return issues
  return checkSubset(spec) ? [] : ajvIssues(checkSubset.errors, 'vega_disallowed', path)
}

/**
 * A view document (the schema the semantic library embeds, view.schema.json) and the Vega-Lite subset of each chart in it. As on the server, a document that does not
 * match the schema is reported for that alone; the view ids and the charts are examined only when it does. What needs the result (a field the result does not have,
 * a stack of a measure that cannot be added, an attribute on neither axis) is the server's to say, because only it has the descriptor.
 */
export function validateViewDocument(document: unknown): ViewIssue[] {
  if (!checkView(document)) return viewDocumentErrors(document)
  const issues: ViewIssue[] = []
  const seen = new Set<string>()
  const views = (document as { views: Record<string, unknown>[] }).views
  views.forEach((view, i) => {
    const path = `$.views[${i}]`
    const id = view.id as string
    if (seen.has(id)) issues.push({ code: 'duplicate_view_id', path: `${path}.id`, message: `Another view is already called ${id}.` })
    seen.add(id)
    if (view.type === 'chart') issues.push(...validateVegaLiteSubset(view.vega_lite, `${path}.vega_lite`))
  })
  return issues
}

/**
 * What the browser renderer is willing to draw: a rendered view in the schema's shape, whose charts carry inline values and a spec inside the subset. The
 * renderer calls this before it hands anything to Vega, whoever wrote the document (a server, an assistant's reply, a file a person opened).
 */
export function validateRenderedView(document: unknown): ViewIssue[] {
  if (!checkRendered(document)) return ajvIssues(checkRendered.errors, 'rendered_invalid', '$')
  const issues: ViewIssue[] = []
  const views = ((document as { views?: Record<string, unknown>[] | null }).views ?? [])
  views.forEach((view, i) => {
    if (view.type !== 'chart') return
    const { data: _data, ...spec } = view.vega_lite as Record<string, unknown>
    issues.push(...validateVegaLiteSubset(spec, `$.views[${i}].vega_lite`))
  })
  return issues
}
