import type { ParameterDefinition, ParameterValue, ParameterValues, ViewKind } from '@dbdatakit/visuals'
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

// ---- kiosk, drill and the way back (plan 32 DBR08) ----

export const MODE_KEY = 'mode'
export const TRAIL_KEY = 'trail'

/** `?mode=kiosk`: the board without the console's chrome. Any other value is the ordinary page. */
export function readKiosk(search: URLSearchParams): boolean {
  return search.get(MODE_KEY) === 'kiosk'
}

/** The address with kiosk mode on or off (off removes the key); everything else is kept. */
export function writeKiosk(search: URLSearchParams, on: boolean): URLSearchParams {
  const next = new URLSearchParams(search)
  if (on) next.set(MODE_KEY, 'kiosk')
  else next.delete(MODE_KEY)
  return next
}

/** Where a board is in the console. */
export function viewPath(kind: ViewKind, name: string): string {
  return `/views/${kind === 'dashboard' ? 'dashboards' : 'reports'}/${encodeURIComponent(name)}`
}

/** One step of the way back: a board and the address state (layout, parameters, mode) it was left in. */
export interface Crumb { kind: ViewKind; name: string; search: string }

const encodeCrumb = (c: Crumb) => `${c.kind}/${encodeURIComponent(c.name)}?${c.search}`
function decodeCrumb(text: string): Crumb | null {
  const m = /^(dashboard|report)\/([^?]+)\?(.*)$/.exec(text)
  if (!m) return null
  try {
    return { kind: m[1] as ViewKind, name: decodeURIComponent(m[2]!), search: m[3]! }
  } catch {
    return null
  }
}

/** The boards a drill came through, oldest first (the repeated `trail` key). An entry that is not one is left out. */
export function readTrail(search: URLSearchParams): Crumb[] {
  return search.getAll(TRAIL_KEY).map(decodeCrumb).filter((c): c is Crumb => c !== null)
}

/** The address with the trail written (an empty trail removes the key). */
export function writeTrail(search: URLSearchParams, trail: Crumb[]): URLSearchParams {
  const next = new URLSearchParams()
  for (const [k, v] of search) if (k !== TRAIL_KEY) next.append(k, v)
  for (const c of trail) next.append(TRAIL_KEY, encodeCrumb(c))
  return next
}

/** The address state of a board without its trail: what a crumb remembers. */
export function withoutTrail(search: URLSearchParams): URLSearchParams {
  return writeTrail(search, [])
}

/** The address of the board a crumb stands for, with the trail up to it (what is before it): going back is this address. */
export function crumbAddress(trail: Crumb[], index: number): string {
  const c = trail[index]!
  const search = writeTrail(new URLSearchParams(c.search), trail.slice(0, index)).toString()
  return `${viewPath(c.kind, c.name)}${search ? `?${search}` : ''}`
}

/**
 * The address a drill goes to: the target board with the parameters it gives (written as the target's own `p.<name>`, each read as the target's type: a value that is not one is left to the default), the
 * mode kept (a drill in kiosk stays in kiosk), and the trail grown by the board it leaves, in the state it leaves it (`here`).
 */
export function drillAddress(args: {
  drill: { kind: ViewKind; name: string; parameters: Record<string, ParameterValue> }
  targetParameters: ParameterDefinition[] | undefined
  here: { kind: ViewKind; name: string; search: URLSearchParams }
}): string {
  const { drill, targetParameters, here } = args
  const given: ParameterValues = readParameters(new URLSearchParams(), targetParameters)
  for (const d of targetParameters ?? []) {
    const v = drill.parameters[d.name]
    if (v === undefined || v === null) continue
    // through the address's own reader, so a value is accepted exactly as it would be typed into a link
    const raw = Array.isArray(v) ? v : [v]
    const probe = new URLSearchParams()
    for (const r of raw) probe.append(PARAMETER_PREFIX + d.name, String(r))
    if (raw.length === 0) probe.append(PARAMETER_PREFIX + d.name, '')
    given[d.name] = readParameters(probe, [d])[d.name]!
  }
  let search = writeParameters(new URLSearchParams(), targetParameters, given)
  if (readKiosk(here.search)) search = writeKiosk(search, true)
  const fromSource = readSource(here.search)
  if (fromSource !== null) search = writeSource(search, fromSource)
  const trail = [...readTrail(here.search), { kind: here.kind, name: here.name, search: withoutTrail(here.search).toString() }]
  const text = writeTrail(search, trail).toString()
  return `${viewPath(drill.kind, drill.name)}${text ? `?${text}` : ''}`
}

/** `?source=`: where the documents come from (`fixtures`, or the id of a server this console manages); absent, the console chooses. */
export const SOURCE_KEY = 'source'

export const readSource = (search: URLSearchParams): string | null => search.get(SOURCE_KEY) || null

/** The address with the source named (null: the console chooses); the other keys are kept. */
export function writeSource(search: URLSearchParams, source: string | null): URLSearchParams {
  const next = new URLSearchParams(search)
  if (source === null) next.delete(SOURCE_KEY)
  else next.set(SOURCE_KEY, source)
  return next
}

/** A path of the Views page that keeps the source the person chose (a link from a list to a document, and back). */
export const withSource = (path: string, source: string | null): string => (source ? `${path}${path.includes('?') ? '&' : '?'}${SOURCE_KEY}=${encodeURIComponent(source)}` : path)
