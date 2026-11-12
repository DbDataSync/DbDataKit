/**
 * What `dbdatabuild project import --from-cache` made of the draft's catalog cache (plan 33 SC20k), read from the tool's own `data` (`mapped_models`, `skipped`, `catalog_notes`, `written`) and the files the Workspace route added (`files`: the text the tool wrote on a copy).
 * The reader places what it knows and counts what it does not, so a change in the tool's shape is a sentence on the screen and not a wrong list. What the cache cannot say (indexes, foreign keys) is in `catalog_notes`, in the tool's words.
 */
export type ImportStatus = 'new' | 'changed' | 'unchanged' | 'stale' | 'invalid'
export interface ImportColumn { name: string; nativeType: string; logicalType: string | null; fit: string; reason: string | null; nullable: boolean }
export interface ImportChange { kind: string; column: string | null; detail: string }
export interface ImportModel { name: string; objectKind: string; file: string | null; status: ImportStatus; grain: string[]; changes: ImportChange[]; columns: ImportColumn[]; notes: string[] }
export interface ImportFile { path: string; content: string; exists: boolean; truncated: boolean }
export interface ImportRead { connection: string; models: ImportModel[]; skipped: { object: string; reason: string }[]; notes: string[]; files: ImportFile[]; dropped: number }

const STATUSES: readonly string[] = ['new', 'changed', 'unchanged', 'stale', 'invalid']
const obj = (v: unknown): Record<string, unknown> | null => (typeof v === 'object' && v !== null && !Array.isArray(v) ? (v as Record<string, unknown>) : null)
const str = (v: unknown): string | null => (typeof v === 'string' ? v : null)
const strings = (v: unknown): string[] => (Array.isArray(v) ? v.filter((x): x is string => typeof x === 'string') : [])

export function readImport(data: unknown): ImportRead {
  const root = obj(data)
  let dropped = 0
  const models: ImportModel[] = []
  for (const raw of Array.isArray(root?.mapped_models) ? root.mapped_models : []) {
    const m = obj(raw)
    const name = str(m?.name)
    const status = str(m?.status)
    if (!m || !name || !status || !STATUSES.includes(status)) { dropped++; continue }
    const columns: ImportColumn[] = []
    for (const c of Array.isArray(m.columns) ? m.columns : []) {
      const col = obj(c)
      const cname = str(col?.name)
      if (!col || !cname) { dropped++; continue }
      columns.push({ name: cname, nativeType: str(col.native_type) ?? '', logicalType: str(col.logical_type), fit: str(col.fit) ?? '', reason: str(col.reason), nullable: col.nullable === true })
    }
    const changes: ImportChange[] = []
    for (const c of Array.isArray(m.changes) ? m.changes : []) {
      const ch = obj(c)
      if (!ch || !str(ch.kind)) { dropped++; continue }
      changes.push({ kind: str(ch.kind)!, column: str(ch.column), detail: str(ch.detail) ?? '' })
    }
    models.push({ name, objectKind: str(m.object_kind) ?? 'table', file: str(m.file), status: status as ImportStatus, grain: strings(m.grain), changes, columns, notes: strings(m.notes) })
  }
  const skipped: { object: string; reason: string }[] = []
  for (const raw of Array.isArray(root?.skipped) ? root.skipped : []) {
    const s = obj(raw)
    if (s && str(s.object)) skipped.push({ object: str(s.object)!, reason: str(s.reason) ?? '' }); else dropped++
  }
  const files: ImportFile[] = []
  for (const raw of Array.isArray(root?.files) ? root.files : []) {
    const f = obj(raw)
    if (f && str(f.path) && str(f.content) !== null) files.push({ path: str(f.path)!, content: str(f.content)!, exists: f.exists === true, truncated: f.truncated === true }); else dropped++
  }
  return { connection: str(root?.connection) ?? '', models, skipped, notes: strings(root?.catalog_notes), files, dropped }
}

/** The files a person can write: those the tool wrote text for (a new or changed descriptor), whole, never one cut for its size. */
export const writableFiles = (read: ImportRead): ImportFile[] => read.files.filter((f) => !f.truncated)

/** One status in a word, with what a person would do about it. */
export const statusWord = (s: ImportStatus): string =>
  s === 'new' ? 'new: no descriptor yet' : s === 'changed' ? 'changed: differs from the cache' : s === 'unchanged' ? 'unchanged' : s === 'stale' ? 'stale: the cache shows no such table' : 'invalid: a file is there that is not a mapped model, left alone'

/** The columns the cache gave a type the mapping cannot hold, left out of the descriptor (fit `none`), with the tool's reason. */
export const leftOut = (m: ImportModel): ImportColumn[] => m.columns.filter((c) => c.logicalType === null)

/** The patterns that name every cached table of a connection (`schema.table`), for a person who wants them all. */
export const allTablesOf = (tables: readonly { schema: string; name: string }[]): string[] => tables.map((t) => `${t.schema}.${t.name}`)

/** The patterns typed in a box: separated by spaces or commas, empty ones dropped. */
export const patternsOf = (text: string): string[] => text.split(/[\s,]+/).filter((p) => p !== '')

/** Why a typed pattern would be refused before anything runs (the route checks it again): `schema.table`, with `*` and `?`, no option and no path. */
export function patternProblem(pattern: string): string | null {
  if (pattern.startsWith('-')) return `${pattern} looks like an option.`
  if (!/^[A-Za-z0-9_$.*?-]+$/.test(pattern) || !pattern.includes('.') || pattern.length < 3) return `${pattern} is not schema.table (letters, digits, "_", "$", "-", "*" and "?" only).`
  return null
}
