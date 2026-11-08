import type { ParameterDefinition, ParameterValue, ParameterValues } from '@dbdatakit/visuals'

/**
 * The address of an open board (plan 32 DBR23): the layout the person picked is `?layout=name` (absent: the one the window size selects), and each parameter that differs from its default is
 * `p.<name>=value` (a list: the key repeated). Reading and writing are pure, so the page logic is tested without a browser.
 */
export const LAYOUT_KEY = 'layout'
export const PARAMETER_PREFIX = 'p.'

function one(definition: ParameterDefinition, raw: string): string | number | boolean | undefined {
  switch (definition.type) {
    case 'integer': return /^-?\d+$/.test(raw) ? Number(raw) : undefined
    case 'number': return raw.trim() !== '' && Number.isFinite(Number(raw)) ? Number(raw) : undefined
    case 'boolean': return raw === 'true' ? true : raw === 'false' ? false : undefined
    default: return raw
  }
}

/** The default of one parameter: its `default`, else a list for `many`, else nothing. */
export function defaultOf(definition: ParameterDefinition): ParameterValue {
  return definition.default !== undefined ? definition.default : definition.many ? [] : null
}

/**
 * The parameter values an address holds over the document's defaults. A value of the wrong type (a letter for an integer) is ignored and the default stays; a name the document does not declare is ignored.
 * An empty value is "nothing chosen": null, or the empty list of a `many` parameter.
 */
export function readParameters(search: URLSearchParams, definitions: ParameterDefinition[] | undefined): ParameterValues {
  const values: ParameterValues = {}
  for (const d of definitions ?? []) {
    const key = PARAMETER_PREFIX + d.name
    const raw = search.getAll(key)
    if (raw.length === 0) { values[d.name] = defaultOf(d); continue }
    if (d.many) {
      const parsed = raw.filter((r) => r !== '').map((r) => one(d, r))
      values[d.name] = parsed.some((p) => p === undefined) ? defaultOf(d) : (parsed as Array<string | number | boolean>)
    } else if (raw[0] === '') values[d.name] = null
    else values[d.name] = one(d, raw[0]!) ?? defaultOf(d)
  }
  return values
}

const same = (a: unknown, b: unknown) => JSON.stringify(a ?? null) === JSON.stringify(b ?? null)

/** The address with the parameters written: only those that differ from their default, in the document's order; the other keys (`layout`, ...) are kept. */
export function writeParameters(search: URLSearchParams, definitions: ParameterDefinition[] | undefined, values: ParameterValues): URLSearchParams {
  const next = new URLSearchParams()
  for (const [k, v] of search) if (!k.startsWith(PARAMETER_PREFIX)) next.append(k, v)
  for (const d of definitions ?? []) {
    const value = values[d.name]
    if (value === undefined || same(value, defaultOf(d))) continue
    const key = PARAMETER_PREFIX + d.name
    if (Array.isArray(value)) { if (value.length === 0) next.append(key, ''); else for (const v of value) next.append(key, String(v)) }
    else next.append(key, value === null ? '' : String(value))
  }
  return next
}

/** The address with the layout named (null: the window decides), parameters untouched. */
export function writeLayout(search: URLSearchParams, layout: string | null): URLSearchParams {
  const next = new URLSearchParams(search)
  if (layout === null) next.delete(LAYOUT_KEY)
  else next.set(LAYOUT_KEY, layout)
  return next
}

/** The layout an address names, if the document has a layout of that name; an unknown name is `unknown` (the page says so and the window decides). */
export function readLayout(search: URLSearchParams, layouts: Array<{ name: string }>): { layout: string | null; unknown: string | null } {
  const named = search.get(LAYOUT_KEY)
  if (named === null || named === '') return { layout: null, unknown: null }
  return layouts.some((l) => l.name === named) ? { layout: named, unknown: null } : { layout: null, unknown: named }
}

/** The one-line form of a value for the parameter summary. */
export function describeValue(value: ParameterValue | undefined): string {
  if (value === null || value === undefined || (Array.isArray(value) && value.length === 0)) return 'none chosen'
  return Array.isArray(value) ? value.join(', ') : String(value)
}
