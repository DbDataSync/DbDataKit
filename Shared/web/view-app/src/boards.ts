/** What an app does with a dashboard or report file of its own (plan 32 DBR13): the route's query is the document's parameters, and a link's target is a file of the app. Nothing here depends on a browser. */
import type { LinkInfo, ParameterDefinition, ParameterValue, ParameterValues } from '@dbdatakit/visuals'
import type { DocumentSpec, ViewsDoc } from './spec'

/** The query name of the layout a route names (`?_layout=print`); the document's own parameters are written by their names. */
export const LAYOUT_PARAM = '_layout'

const one = (type: ParameterDefinition['type'], raw: string): string | number | boolean | undefined => {
  switch (type) {
    case 'integer': return /^-?\d+$/.test(raw) ? Number(raw) : undefined
    case 'number': return raw.trim() !== '' && Number.isFinite(Number(raw)) ? Number(raw) : undefined
    case 'boolean': return raw === 'true' ? true : raw === 'false' ? false : undefined
    default: return raw
  }
}

/** The default of a parameter: its `default`, else the empty list of a `many` one, else nothing chosen. */
export const defaultOf = (d: ParameterDefinition): ParameterValue => (d.default !== undefined ? d.default : d.many ? [] : null)

/**
 * The values a route's query gives the document's parameters, over their defaults. **A name the document does not declare is ignored** (owner decision, 2026-11-12), and so is a value of the wrong type (the default stays).
 * A `many` parameter's values are written with commas (`?region=West,East`); an empty value is "nothing chosen".
 */
export function parametersFromQuery(query: Record<string, string>, definitions: ParameterDefinition[] | undefined): ParameterValues {
  const values: ParameterValues = {}
  for (const d of definitions ?? []) {
    const raw = query[d.name]
    if (raw === undefined) { values[d.name] = defaultOf(d); continue }
    if (d.many) {
      const parsed = raw.split(',').filter((r) => r !== '').map((r) => one(d.type, r))
      values[d.name] = parsed.some((p) => p === undefined) ? defaultOf(d) : (parsed as Array<string | number | boolean>)
    } else if (raw === '') values[d.name] = null
    else values[d.name] = one(d.type, raw) ?? defaultOf(d)
  }
  return values
}

const same = (a: unknown, b: unknown) => JSON.stringify(a ?? null) === JSON.stringify(b ?? null)

/** The query for the values that differ from their defaults, in the document's order (the route a person can copy). */
export function queryFromParameters(definitions: ParameterDefinition[] | undefined, values: ParameterValues, layout?: string): Record<string, string> {
  const query: Record<string, string> = {}
  for (const d of definitions ?? []) {
    const value = values[d.name]
    if (value === undefined || same(value, defaultOf(d))) continue
    query[d.name] = Array.isArray(value) ? value.join(',') : value === null ? '' : String(value)
  }
  if (layout) query[LAYOUT_PARAM] = layout
  return query
}

/** A file's name without folder and extension: what a link's target names. */
export const stemOf = (file: string): string => file.replace(/^.*\//, '').replace(/\.(ya?ml|json)$/i, '')

/**
 * The file of the app a link's target names (a name is the file's name without folder and extension, as the publish check reads it): a `form` is a form view, a `view` a grid or a detail, a `dashboard` or a `report` a document of that kind.
 * A `page` is a custom page, which the shell serves at its own address, not a screen of this runtime: null, as for a name the app has no file for.
 */
export function targetFile(doc: Pick<ViewsDoc, 'files' | 'views'>, link: Pick<LinkInfo, 'targetKind' | 'target'>): string | null {
  const wanted = (spec: ViewsDoc['views'][string]): boolean => {
    switch (link.targetKind) {
      case 'form': return spec.kind === 'form'
      case 'view': return spec.kind === 'grid' || spec.kind === 'detail'
      case 'dashboard': case 'report': return spec.kind === 'document' && (spec as DocumentSpec).document === link.targetKind
      default: return false
    }
  }
  return doc.files.find((f) => stemOf(f) === link.target && doc.views[f] !== undefined && wanted(doc.views[f]!)) ?? null
}
