import { parseYaml, removeAt, setAt } from '@dbdatakit/shell'

/**
 * The `queries:` of a semantic model file as the form edits them (plan 14a SL20): one entry per saved query, each exactly one of `sql` (semantic SQL, its markers are the parameters) or `request` (a semantic request of kind
 * query, `{"param": name}` nodes are the parameters), a description, and what each parameter is. Everything here is a function of the file's text, so the form and the YAML tab edit the same file; the schema is
 * `semantic.schema.json` and is not changed.
 */
export const parameterTypes = ['string', 'integer', 'number', 'boolean'] as const
export type ParameterType = (typeof parameterTypes)[number]

export interface ParameterDef {
  for?: string
  type?: ParameterType
  many?: boolean
  /** Any plain value; shown and set as the YAML text of the value. */
  default?: unknown
  required?: boolean
  empty?: 'matches_none'
  description?: string
}

export interface QueryEntry {
  name: string
  description: string
  kind: 'sql' | 'request'
  /** The semantic SQL, or the request as JSON text (what the form edits). */
  body: string
  parameters: { name: string; def: ParameterDef }[]
}

export interface ReadQueries {
  entries: QueryEntry[]
  /** Set when the section cannot be read by the form; the YAML tab is the way then. */
  problem: string | null
}

/** A semantic model file: a YAML file under `semantic/`. */
export const isSemanticFile = (path: string) => /^semantic\/.+\.ya?ml$/.test(path)

/** The model's name: its `name:` key, or the file's name without the extension. */
export function modelNameOf(text: string, path: string): string {
  const name = (parseYaml(text).value as { name?: unknown } | undefined)?.name
  return typeof name === 'string' && name !== '' ? name : path.slice(path.lastIndexOf('/') + 1).replace(/\.ya?ml$/, '')
}

export const queryNamePattern = /^[a-z][a-z0-9_]*$/

/** What is wrong with a name for a query, as a sentence; null when it is fine. */
export function nameProblem(name: string, others: string[]): string | null {
  if (!queryNamePattern.test(name)) return 'A name starts with a lower-case letter and has lower-case letters, digits and _.'
  if (others.includes(name)) return `There is already a query called ${name}.`
  return null
}

export function readQueries(text: string): ReadQueries {
  const parsed = parseYaml(text)
  if (parsed.errors.length > 0) return { entries: [], problem: 'The file is not valid YAML, so the queries cannot be shown here. Fix it in the YAML tab first.' }
  const queries = (parsed.value as { queries?: unknown } | undefined)?.queries
  if (queries === undefined || queries === null) return { entries: [], problem: null }
  if (typeof queries !== 'object' || Array.isArray(queries)) return { entries: [], problem: '`queries` must be a mapping of names to queries. Fix it in the YAML tab.' }
  const entries: QueryEntry[] = []
  for (const [name, raw] of Object.entries(queries as Record<string, unknown>)) {
    if (typeof raw !== 'object' || raw === null || Array.isArray(raw)) return { entries: [], problem: `The query ${name} must be a mapping. Fix it in the YAML tab.` }
    const q = raw as { description?: unknown; sql?: unknown; request?: unknown; parameters?: unknown }
    const parameters = q.parameters && typeof q.parameters === 'object' && !Array.isArray(q.parameters)
      ? Object.entries(q.parameters as Record<string, unknown>).map(([pname, def]) => ({ name: pname, def: (def && typeof def === 'object' ? def : {}) as ParameterDef }))
      : []
    const kind = q.request !== undefined && q.sql === undefined ? 'request' : 'sql'
    entries.push({
      name,
      description: typeof q.description === 'string' ? q.description : '',
      kind,
      body: kind === 'request' ? JSON.stringify(q.request, null, 2) : typeof q.sql === 'string' ? q.sql : '',
      parameters,
    })
  }
  return { entries, problem: null }
}

function cleanDef(def: ParameterDef): ParameterDef {
  const out: ParameterDef = {}
  if (def.for) out.for = def.for
  if (def.type) out.type = def.type
  if (def.many) out.many = true
  if (def.default !== undefined && def.default !== '') out.default = def.default
  if (def.required !== undefined) out.required = def.required
  if (def.empty) out.empty = def.empty
  if (def.description) out.description = def.description
  return out
}

/** The entry as the file holds it: `sql` or `request`, never both; a description with nothing in it is left out. A request that is not JSON gives null. */
export function entryValue(entry: QueryEntry): Record<string, unknown> | null {
  const out: Record<string, unknown> = {}
  if (entry.description.trim() !== '') out.description = entry.description
  if (entry.kind === 'sql') out.sql = entry.body
  else {
    try { out.request = JSON.parse(entry.body) } catch { return null }
  }
  if (entry.parameters.length > 0) {
    const defs: Record<string, ParameterDef> = {}
    for (const p of entry.parameters) defs[p.name] = cleanDef(p.def)
    out.parameters = defs
  }
  return out
}

/** The text with the entry written (a new one at the end, an existing one in place). A request that is not JSON is an error the caller shows. */
export function writeEntry(text: string, entry: QueryEntry): string {
  const value = entryValue(entry)
  if (value === null) throw new Error('The request is not valid JSON.')
  return setAt(text, ['queries', entry.name], value)
}

export function removeEntry(text: string, name: string): string {
  return removeAt(text, ['queries', name])
}

/** The text with a query called another name (it moves to the end of the section). */
export function renameEntry(text: string, from: string, to: string): string {
  const value = (parseYaml(text).value as { queries?: Record<string, unknown> } | undefined)?.queries?.[from]
  if (value === undefined) throw new Error(`There is no query ${from}.`)
  return removeAt(setAt(text, ['queries', to], value), ['queries', from])
}

/** The first line (counted from 1) of a query's `sql` or `request`, for a message that says where; the query's own line when it has neither; null when the text has no such query. */
export function lineOfEntry(text: string, name: string, kind: 'sql' | 'request' = 'sql'): number | null {
  const lines = text.split('\n')
  const start = lines.findIndex((l) => /^queries:\s*(#.*)?$/.test(l))
  if (start < 0) return null
  let own = -1
  let indent = -1
  for (let i = start + 1; i < lines.length; i++) {
    const line = lines[i]
    if (line.trim() === '' || line.trim().startsWith('#')) continue
    const depth = line.length - line.trimStart().length
    if (depth === 0) break
    if (own < 0) {
      if (new RegExp(`^\\s+${name}:\\s*(#.*)?$`).test(line)) { own = i; indent = depth }
      continue
    }
    if (depth <= indent) break
    if (new RegExp(`^\\s+${kind}:`).test(line)) return i + 1
  }
  return own >= 0 ? own + 1 : null
}

/**
 * A value the person typed for a parameter, as the call carries it: a list (one value for each comma) when the parameter takes many, a number or a boolean when the type says so. A value that is not of its type is an
 * error sentence; an empty text is no value at all (null).
 */
export function parameterValue(text: string, type: string | null | undefined, many: boolean): { value: unknown } | { error: string } | null {
  if (text.trim() === '') return null
  const one = (part: string): { value: unknown } | { error: string } => {
    const t = part.trim()
    if (type === 'integer') return /^-?\d+$/.test(t) ? { value: Number(t) } : { error: `${t} is not a whole number.` }
    if (type === 'number') return t !== '' && Number.isFinite(Number(t)) ? { value: Number(t) } : { error: `${t} is not a number.` }
    if (type === 'boolean') return t === 'true' || t === 'false' ? { value: t === 'true' } : { error: `${t} is not true or false.` }
    return { value: t }
  }
  if (!many) return one(text)
  const values: unknown[] = []
  for (const part of text.split(',')) {
    const parsed = one(part)
    if ('error' in parsed) return parsed
    values.push(parsed.value)
  }
  return { value: values }
}
