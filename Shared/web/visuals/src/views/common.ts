import Ajv2020 from 'ajv/dist/2020'
import type { ErrorObject, ValidateFunction } from 'ajv'
import coreSchema from '../../schemas/core.schema.json'
import vegaSubsetSchema from '../../schemas/vega-lite-subset.schema.json'
import viewSchema from '../../schemas/view.schema.json'

/**
 * What the validators of widgets (widgets.ts) and of documents (document.ts) share: the compiled schemas, the issue type, the way a schema error becomes a code and a path, and the lists the .NET loader
 * (`DbDataKit.Views`) has too. The codes are those of `ViewsCodes` in .NET and the paths are the same places, so the two suites run the same documents (`conformance/views`).
 */

export type ViewsIssueCode = string

/** One reason a document is refused (or, for `warning`, noted). The codes are those of `ViewsCodes` in .NET; `path` is the place as `layouts[1].tiles[0].x` (no leading `$`). */
export interface ViewsIssue {
  code: ViewsIssueCode
  severity: 'error' | 'warning'
  path: string
  message: string
}

const AjvCtor = ((Ajv2020 as unknown as { default?: typeof Ajv2020 }).default ?? Ajv2020) as typeof Ajv2020
export const ajv = new AjvCtor({ strict: false, allErrors: true, validateFormats: false, verbose: true })
ajv.addSchema(viewSchema as object)
ajv.addSchema(vegaSubsetSchema as object)
ajv.addSchema(coreSchema as object)

export type Json = Record<string, unknown>
export const isObject = (v: unknown): v is Json => typeof v === 'object' && v !== null && !Array.isArray(v)
export const defs = (coreSchema as unknown as { $defs: Record<string, Json> }).$defs

/** A part of the core compiled alone: its `$defs` come along so the local references resolve. */
export function part(def: Json, change?: (copy: Json) => void): ValidateFunction {
  const copy = JSON.parse(JSON.stringify(def)) as Json
  change?.(copy)
  return ajv.compile({ ...copy, $defs: defs })
}

export const props = (def: Json): string[] => Object.keys(def.properties as Json)
export const MARKS = ['bar', 'line', 'area', 'point', 'rule', 'text']
export const CHART_CHANNELS = props(defs.chart_encodings!)
export const COMPUTING = ['data', 'transform', 'aggregate', 'bin', 'timeUnit', 'url', 'datasets', 'layer', 'facet', 'repeat', 'concat', 'params', 'selection', 'calculate', 'expr', 'condition', 'datum', 'value', 'filter']
export const IMAGE_URL_KEYS = ['url', 'src', 'href', 'uri', 'data', 'base64']
export const NAME = /^[a-z][a-z0-9_-]{0,63}$/
export const VIEW = (n: string) => `DBD-VIEW-${n}`

/** Whether a link in a widget's text may be followed: a path on the host that starts with a single `/`. */
export function isSafeTextLink(href: string): boolean {
  return href.length > 0 && href.length <= 500 && ![...href].some((c) => c.charCodeAt(0) < 32 || c.charCodeAt(0) === 127) && !href.includes(' ') && href.startsWith('/') && !href.startsWith('//') && !href.includes('\\')
}

export const join = (path: string, key: string) => (path === '' ? key : `${path}.${key}`)
export const at = (path: string, index: number) => `${path}[${index}]`

/** `/a/0/b` under `base` as `base.a[0].b`. */
export function pathOf(base: string, instancePath: string): string {
  const tail = instancePath.split('/').filter(Boolean).map((s) => (/^\d+$/.test(s) ? `[${s}]` : `.${s.replace(/~1/g, '/').replace(/~0/g, '~')}`)).join('')
  return base === '' ? tail.replace(/^\./, '') : base + tail
}

export class Issues {
  readonly list: ViewsIssue[] = []
  add(code: string, path: string, message: string, severity: 'error' | 'warning' = 'error') { this.list.push({ code, severity, path, message }) }
  get hasErrors() { return this.list.some((i) => i.severity === 'error') }
}

/** What a schema error is about: a `widget` of a type, a `query`, a chart `channel` (here), a document's `top` level or a `layout` of a type (document.ts). */
export interface Area { kind: 'widget' | 'query' | 'channel' | 'top' | 'layout'; type?: string; of?: string }

/** A classifier gives a schema error its code and path, or lets the next one try. document.ts adds the classifiers of the document and its layouts. */
export type Classifier = (e: ErrorObject, area: Area, base: string) => { code: string; path: string; message: string } | undefined
export const classifiers: Classifier[] = []

/** A schema error as an issue: the code says what the refusal is about, as the .NET loader does. */
export function classify(e: ErrorObject, area: Area, base: string): { code: string; path: string; message: string } {
  for (const c of classifiers) {
    const found = c(e, area, base)
    if (found) return found
  }
  const rel = e.instancePath
  const prop = e.keyword === 'additionalProperties' ? String((e.params as { additionalProperty: string }).additionalProperty) : undefined
  const missing = e.keyword === 'required' ? String((e.params as { missingProperty: string }).missingProperty) : undefined
  if (missing !== undefined) return { code: 'DBD-105', path: pathOf(base, `${rel}/${missing}`), message: `Required key \`${missing}\` is missing.` }
  if (prop !== undefined) {
    const path = pathOf(base, `${rel}/${prop}`)
    const unknown = `Unknown key \`${prop}\`.`
    if (area.kind === 'widget') {
      if (rel === '' && area.type === 'image' && IMAGE_URL_KEYS.includes(prop)) return { code: 'DBD-VIEW-003', path, message: `An image is held in the definition's artifacts by hash; \`${prop}\` would fetch it from outside.` }
      if (rel === '' && area.type === 'chart' && COMPUTING.includes(prop)) return { code: 'DBD-VIEW-004', path, message: `\`${prop}\` isn't allowed in a chart: it fetches data or computes a value.` }
    }
    if (area.kind === 'channel' && COMPUTING.includes(prop)) return { code: 'DBD-VIEW-004', path, message: `\`${prop}\` isn't allowed in a chart: it fetches data or computes a value.` }
    return { code: 'DBD-104', path, message: unknown }
  }
  const path = pathOf(base, rel)
  const message = `${e.message ?? 'is not valid'}`
  if (area.kind === 'widget') {
    if (area.type === 'image' && (rel === '/hash' || rel === '/media_type')) return { code: 'DBD-VIEW-003', path, message: `An image is a raster held by hash (sha256:...), never a URL or an SVG: ${message}.` }
  }
  if (area.kind === 'channel') {
    if (rel === '/type') return { code: 'DBD-VIEW-004', path, message }
    if (rel === '/sort' && isObject(e.data)) return { code: 'DBD-VIEW-004', path, message: 'A sort that aggregates computes a value: it belongs in the query.' }
  }
  return { code: 'DBD-106', path, message: `${message}.` }
}

/** Runs a compiled part and adds its errors; the branches a `oneOf` tried are dropped for the one error at the `oneOf`. */
export function run(check: ValidateFunction, value: unknown, area: Area, base: string, issues: Issues): void {
  if (check(value)) return
  const errors = check.errors ?? []
  const oneOfAt = errors.filter((e) => e.keyword === 'oneOf' || e.keyword === 'anyOf').map((e) => e.instancePath)
  const seen = new Set<string>()
  for (const e of errors) {
    if (e.keyword !== 'oneOf' && e.keyword !== 'anyOf' && e.schemaPath.includes('/oneOf/') && oneOfAt.some((p) => e.instancePath.startsWith(p))) continue
    if (e.keyword === 'if') continue
    const c = classify(e, area, base)
    const key = `${c.code}|${c.path}`
    if (seen.has(key)) continue
    seen.add(key)
    issues.add(c.code, c.path, c.message)
  }
}

