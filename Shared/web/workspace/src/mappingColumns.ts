import { parseYaml, setAt } from '@dbdatakit/shell'
import type { CacheColumn, CacheTable, CatalogCache } from './catalogCache'

/** A table a mapping names: its connection of the set, and where in it. */
export interface TableRef { connection: string; schema: string; table: string }

const text = (v: unknown): string => (typeof v === 'string' ? v : '')

/** The first source and the first target table of a mapping text (a mapping may name several; the editor works on the first pair and says so), or null when the text is not a mapping that names them. */
export function tablesOfMapping(source: string): { source: TableRef; target: TableRef; more: boolean } | null {
  const parsed = parseYaml(source)
  const root = parsed.value
  if (parsed.errors.length > 0 || typeof root !== 'object' || root === null || Array.isArray(root)) return null
  const doc = root as Record<string, unknown>
  const ref = (list: unknown): TableRef | null => {
    const first = Array.isArray(list) ? list[0] : null
    if (typeof first !== 'object' || first === null) return null
    const t = first as Record<string, unknown>
    return text(t.table) === '' || text(t.connectionName) === '' ? null : { connection: text(t.connectionName), schema: text(t.schema), table: text(t.table) }
  }
  const from = ref(doc.sources)
  const to = ref(doc.targets)
  if (!from || !to) return null
  return { source: from, target: to, more: (Array.isArray(doc.sources) && doc.sources.length > 1) || (Array.isArray(doc.targets) && doc.targets.length > 1) }
}

/** The cached table a reference names: the connection, then schema and table without regard to case (a database folds names its own way); a reference with no schema matches the only table of that name. */
export function findCachedTable(cache: CatalogCache, ref: TableRef): { table: CacheTable | null; why: string | null } {
  const connection = cache.connections[ref.connection]
  if (!connection) return { table: null, why: `The cache has no connection called ${ref.connection} (it has ${Object.keys(cache.connections).join(', ') || 'none'}).` }
  const named = connection.tables.filter((t) => t.name.toLowerCase() === ref.table.toLowerCase())
  const exact = ref.schema === '' ? named : named.filter((t) => t.schema.toLowerCase() === ref.schema.toLowerCase())
  if (exact.length === 1) return { table: exact[0]!, why: null }
  if (exact.length > 1) return { table: null, why: `More than one table of ${ref.connection} is called ${ref.table}: name its schema in the mapping.` }
  return { table: null, why: `The cache has no table ${ref.schema ? ref.schema + '.' : ''}${ref.table} on ${ref.connection}.` }
}

/** What the mapping says now: the source column chosen for each target column and the primary key. */
export interface ColumnChoices { sourceOf: Record<string, string>; key: string[] }

export function choicesOfMapping(source: string): ColumnChoices {
  const root = parseYaml(source).value
  const doc = typeof root === 'object' && root !== null && !Array.isArray(root) ? (root as Record<string, unknown>) : {}
  const sourceOf: Record<string, string> = {}
  if (Array.isArray(doc.columnMappings)) {
    for (const m of doc.columnMappings) {
      if (typeof m === 'object' && m !== null && text((m as Record<string, unknown>).targetColumn) !== '') sourceOf[text((m as Record<string, unknown>).targetColumn)] = text((m as Record<string, unknown>).sourceColumn)
    }
  }
  return { sourceOf, key: Array.isArray(doc.primaryKey) ? doc.primaryKey.filter((k): k is string => typeof k === 'string') : [] }
}

/** The same-name pairs of the two column lists, without regard to case: what a mapping starts from (as Sync's own editor suggests it). */
export function suggestPairs(source: readonly CacheColumn[], target: readonly CacheColumn[]): Record<string, string> {
  const byName = new Map(source.map((c) => [c.name.toLowerCase(), c.name]))
  const out: Record<string, string> = {}
  for (const t of target) { const s = byName.get(t.name.toLowerCase()); if (s) out[t.name] = s }
  return out
}

/** The target columns whose chosen source column is not a column of the source table: they would fail at provisioning ("Source column was not found"), so the editor names them before anything is written. */
export function unknownSources(choices: ColumnChoices, source: readonly CacheColumn[]): string[] {
  const names = new Set(source.map((c) => c.name))
  return Object.entries(choices.sourceOf).filter(([, s]) => s !== '' && !names.has(s)).map(([t]) => t)
}

const cacheEntry = (c: CacheColumn, keyColumns: readonly string[]) => {
  const ordinal = keyColumns.indexOf(c.name)
  return ordinal >= 0 ? { name: c.name, nativeType: c.type, isPrimaryKey: true, keyOrdinal: ordinal + 1 } : { name: c.name, nativeType: c.type, isPrimaryKey: false }
}

/**
 * The mapping text with the column mappings (in the order of the target's columns), the primary key and the column cache (`sourceColumns`, `targetColumns`, each with its native type and its key columns) set from the choices
 * and the cached tables; nothing else in the file changes (`setAt` keeps comments and the rest as written). A target column with no source column is left out of the mappings.
 */
export function writeColumns(mapping: string, choices: ColumnChoices, source: CacheTable, target: CacheTable): string {
  const pairs = target.columns.filter((c) => (choices.sourceOf[c.name] ?? '') !== '').map((c) => ({ sourceColumn: choices.sourceOf[c.name]!, targetColumn: c.name }))
  const targetKey = choices.key.filter((k) => target.columns.some((c) => c.name === k))
  const sourceKey = pairs.filter((p) => targetKey.includes(p.targetColumn)).map((p) => p.sourceColumn)
  let out = setAt(mapping, ['columnMappings'], pairs)
  out = setAt(out, ['primaryKey'], targetKey)
  out = setAt(out, ['sourceColumns'], source.columns.map((c) => cacheEntry(c, sourceKey)))
  out = setAt(out, ['targetColumns'], target.columns.map((c) => cacheEntry(c, targetKey)))
  return out
}
