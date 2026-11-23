import { parseYaml, removeAt, setAt, toYaml, type YamlPath } from '@dbdatakit/shell'
import { SYNC_TASK_CURRENT, withCurrentTaskVersion } from '@dbdatakit/workspace'
import type {
  ColumnMapping, ConnectionView, EndpointRef, ReplicationTaskConfig, RelationshipConfig, SourceTableSpec, TableMappingConfig, TableSpec,
} from './types'

/** The newest format of a table mapping this reads and writes (`ConfigSchema.MappingCurrent`). */
export const SYNC_MAPPING_CURRENT = 1

/** A file of the draft as a typed view, or why the Designer cannot show it (Files can: a link names the file). */
export type Read<T> = { value: T; problem: null } | { value: null; problem: string }

type Raw = Record<string, unknown>
const isMap = (v: unknown): v is Raw => typeof v === 'object' && v !== null && !Array.isArray(v)
const str = (v: unknown, otherwise = ''): string => (typeof v === 'string' ? v : typeof v === 'number' ? String(v) : otherwise)
const strOrNull = (v: unknown): string | null => (typeof v === 'string' && v !== '' ? v : typeof v === 'number' ? String(v) : null)
const num = (v: unknown, otherwise: number): number => (typeof v === 'number' && Number.isFinite(v) ? v : otherwise)
const numOrNull = (v: unknown): number | null => (typeof v === 'number' && Number.isFinite(v) ? v : null)
const options = (v: unknown): Record<string, string> => (isMap(v) ? Object.fromEntries(Object.entries(v).map(([k, x]) => [k, str(x)])) : {})

function parsed(path: string, text: string): Read<Raw> {
  const doc = parseYaml(text)
  if (doc.errors.length > 0) return { value: null, problem: `${path} is not valid YAML (${doc.errors[0]}); fix it in Files.` }
  if (doc.value === undefined) return { value: {}, problem: null }
  if (!isMap(doc.value)) return { value: null, problem: `${path} is not a mapping of settings; open it in Files.` }
  return { value: doc.value, problem: null }
}

function newer(path: string, raw: Raw, current: number): string | null {
  const v = raw.schemaVersion
  if (typeof v === 'number' && v > current) {
    return `${path} is in format ${v}, which is newer than the Designer reads (${current}); open it in Files, or update the software.`
  }
  return null
}

const endpoint = (v: unknown): EndpointRef | null =>
  (isMap(v) ? { ...v, connectionName: strOrNull(v.connectionName), database: strOrNull(v.database) } : null)

/** A replication's `task.yaml` as the screens edit it: Sync's defaults filled in where the file says nothing (enabled, 4 at once). */
export function readTask(path: string, name: string, text: string): Read<ReplicationTaskConfig> {
  const read = parsed(path, text)
  if (read.value === null) return read
  const raw = read.value
  const too = newer(path, raw, SYNC_TASK_CURRENT)
  if (too) return { value: null, problem: too }
  const scheduling = isMap(raw.scheduling) ? raw.scheduling : {}
  const cp = isMap(raw.changeProcessing) ? raw.changeProcessing : {}
  const stage = (v: unknown) => (isMap(v) ? { ...v, kind: str(v.kind), options: options(v.options) } : { kind: '', options: {} })
  const endpoints = isMap(raw.endpoints) ? raw.endpoints : {}
  return {
    value: {
      ...raw,
      name: str(raw.name, name),
      enabled: raw.enabled !== false,
      scheduling: {
        ...scheduling,
        mode: scheduling.mode === 'Periodic' ? 'Periodic' : 'Continuous',
        frequencySeconds: numOrNull(scheduling.frequencySeconds),
        idleTimeoutSeconds: numOrNull(scheduling.idleTimeoutSeconds),
        cronExpression: strOrNull(scheduling.cronExpression),
      },
      changeProcessing: {
        ...cp,
        reader: stage(cp.reader),
        cache: stage(cp.cache),
        writer: stage(cp.writer),
        degreeOfParallelism: num(cp.degreeOfParallelism, 4),
        bulkLoadDegreeOfParallelism: num(cp.bulkLoadDegreeOfParallelism, 4),
      },
      endpoints: { ...endpoints, source: endpoint(endpoints.source), target: endpoint(endpoints.target) },
    },
    problem: null,
  }
}

const tableSpec = (v: Raw): TableSpec => ({ ...v, connectionName: strOrNull(v.connectionName), database: strOrNull(v.database), schema: str(v.schema), table: str(v.table) })
const sourceSpec = (v: Raw): SourceTableSpec => ({ ...tableSpec(v), filter: strOrNull(v.filter), query: typeof v.query === 'string' ? v.query : null })
const maps = (v: unknown): Raw[] => (Array.isArray(v) ? v.filter(isMap) : [])

/** A table mapping file as the screens edit it. */
export function readMapping(path: string, text: string): Read<TableMappingConfig> {
  const read = parsed(path, text)
  if (read.value === null) return read
  const raw = read.value
  const too = newer(path, raw, SYNC_MAPPING_CURRENT)
  if (too) return { value: null, problem: too }
  const fallback = /([^/]+)\.ya?ml$/i.exec(path)?.[1] ?? ''
  return {
    value: {
      ...raw,
      name: str(raw.name, fallback),
      sources: maps(raw.sources).map(sourceSpec),
      targets: maps(raw.targets).map(tableSpec),
      columnMappings: maps(raw.columnMappings).map((m): ColumnMapping => ({ ...m, sourceColumn: str(m.sourceColumn), targetColumn: str(m.targetColumn) })),
      relationships: maps(raw.relationships).map((r): RelationshipConfig => ({
        ...r, name: str(r.name), schema: str(r.schema), table: str(r.table),
        joinKeys: maps(r.joinKeys).map((k) => ({ ...k, localColumn: str(k.localColumn), foreignColumn: str(k.foreignColumn) })),
      })),
      primaryKey: Array.isArray(raw.primaryKey) ? raw.primaryKey.filter((k): k is string => typeof k === 'string') : [],
    },
    problem: null,
  }
}

/** A connection file, read only: its name, engine, the database its address names and the scripts it binds. */
export function readConnection(path: string, text: string): Read<ConnectionView> {
  const read = parsed(path, text)
  if (read.value === null) return read
  const raw = read.value
  const fallback = /([^/]+)\.ya?ml$/i.exec(path)?.[1] ?? ''
  const address = isMap(raw.address) ? raw.address : {}
  const sync = isMap(raw.extensions) && isMap(raw.extensions.sync) ? raw.extensions.sync : {}
  const scripts = isMap(sync.scripts) ? (sync.scripts as ConnectionView['scripts']) : {}
  return { value: { name: str(raw.name, fallback), path, engine: str(raw.engine), database: strOrNull(address.database), scripts }, problem: null }
}

const absent = (v: unknown) => v === null || v === undefined

function same(a: unknown, b: unknown): boolean {
  if (absent(a) && absent(b)) return true
  if (Array.isArray(a) && Array.isArray(b)) return a.length === b.length && a.every((x, i) => same(x, b[i]))
  if (isMap(a) && isMap(b)) return [...new Set([...Object.keys(a), ...Object.keys(b)])].every((k) => same(a[k], b[k]))
  return a === b
}

/** A value as it is written into the file: the null and undefined members left out (a missing key is how a file says "not set"). */
function written(v: unknown): unknown {
  if (Array.isArray(v)) return v.map(written)
  if (isMap(v)) return Object.fromEntries(Object.entries(v).filter(([, x]) => !absent(x)).map(([k, x]) => [k, written(x)]))
  return v
}

/**
 * The file's text with what changed between two views of it written in, one path edit per changed value: a value set where it changed, a key
 * removed where it became null, a list written whole when its length changed. Everything else in the text stays as it was, comments and keys no
 * screen knows included.
 */
export function editText(text: string, before: unknown, after: unknown, path: YamlPath = []): string {
  if (same(before, after)) return text
  if (absent(after)) return path.length === 0 ? text : removeAt(text, path)
  if (isMap(before) && isMap(after)) {
    let out = text
    for (const key of new Set([...Object.keys(before), ...Object.keys(after)])) out = editText(out, before[key], after[key], [...path, key])
    return out
  }
  if (Array.isArray(before) && Array.isArray(after) && before.length === after.length) {
    let out = text
    after.forEach((item, i) => { out = editText(out, before[i], item, [...path, i]) })
    return out
  }
  return setAt(text, path, written(after))
}

/** A replication's `task.yaml` after a screen's change: the change written in, and the file in the current format (`schemaVersion: 2`). */
export function editTask(text: string, before: ReplicationTaskConfig, after: ReplicationTaskConfig): string {
  const out = editText(text, before, after)
  return out === text ? text : withCurrentTaskVersion(out)
}

/** The paths of a replication's folder: its task and its mappings. */
export const taskPath = (replication: string) => `config/replications/${replication}/task.yaml`
export const mappingsFolder = (replication: string) => `config/replications/${replication}/table-mappings/`
export const mappingPath = (replication: string, mapping: string) => `${mappingsFolder(replication)}${mapping}.yaml`

/** The names of a replication's mappings, from the draft's paths. */
export function mappingsOf(paths: readonly string[], replication: string): string[] {
  const folder = mappingsFolder(replication)
  return paths.filter((p) => p.startsWith(folder) && /\.ya?ml$/i.test(p) && !p.slice(folder.length).includes('/'))
    .map((p) => p.slice(folder.length).replace(/\.ya?ml$/i, '')).sort()
}

/** A new replication's `task.yaml`: disabled until someone turns it on, its endpoints chosen on its Overview, a pipeline every driver offers. */
export function newTaskText(name: string, scheduling: { mode: 'Continuous'; frequencySeconds: number } | { mode: 'Periodic'; cronExpression: string }): string {
  return toYaml({
    schemaVersion: SYNC_TASK_CURRENT,
    name,
    enabled: false,
    scheduling,
    changeProcessing: { reader: { kind: 'BatchReload', options: {} }, cache: { kind: 'StagingTable', options: {} }, writer: { kind: 'DeleteInsert', options: {} } },
    bulkLoad: { reader: { kind: 'BatchReload', options: {} } },
    endpoints: {},
    reconcile: { enabled: false, afterChange: { mode: 'none' }, deleteGuard: { mode: 'ratio', maxRatio: 0.5 } },
  }) + '\n'
}

/**
 * A new mapping that states only its tables: the endpoints, provisioning, scripts and hooks follow the replication. Each side is written as a mapping
 * of its own, even when the caller passes one object for both: the writer would otherwise write the target as an alias of the source (`- *a1`),
 * which a later edit of one side cannot change without the other.
 */
export function newMappingText(name: string, source: { schema: string; table: string }, target: { schema: string; table: string }): string {
  const side = (t: { schema: string; table: string }) => ({ schema: t.schema, table: t.table })
  return toYaml({ schemaVersion: SYNC_MAPPING_CURRENT, name, sources: [side(source)], targets: [side(target)], columnMappings: [] }) + '\n'
}
