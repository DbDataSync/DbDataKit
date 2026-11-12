import type { ResolvedAction } from '../views/actions'
import type { ActionRequest } from './actions'

/**
 * The **pending set** (plan 32 DBR53b): the changes a person has staged in the shell and not yet submitted. It lives in the shell, never in the sandboxed widget (a widget can only ask to stage and read what is staged),
 * is one set of **one model** (the Store's transaction is per model), is limited (a setting), and is never part of an export. This file is the pure half: what a staged line is, whether one more may join the set, and the
 * merge the review shows; `PendingBar` and `PendingReview` draw it and `ActionHost.submit` sends it. `ViewsApi.RunBatchAsync` in .NET refuses the same sets with the same codes (`conformance/views/batches.json`).
 */

export const DEFAULT_PENDING_LIMIT = 1000

/** One staged change: what the shell derived of the link and the row (the popup's words, the typed values as text) and what the server is sent to derive it again. */
export interface StagedChange {
  /** Stable for the line: the link and the row's key. Staging the same link for the same row again replaces the line. */
  id: string
  link: string
  widget: string
  model: string
  entity: string
  operation: 'insert' | 'edit' | 'delete'
  /** The row's label for the review (the title of the declaration, with the row's values). */
  label: string
  key: { name: string; value: string }[]
  version: string | null
  /** The fields written, name and value as text. */
  writes: { name: string; value: string }[]
  /** The fields of the widget's row that name the row (from the link's `key` values that read a field of the row): the widget marks the rows that have a staged change. */
  rowKey: { field: string; value: string }[]
  /** What the server is sent: the link, the row, the parameters and the person's answers. */
  request: Pick<ActionRequest, 'link' | 'widget' | 'row' | 'parameters' | 'answers'>
}

export type StageRefusal =
  | { ok: false; code: 'not_a_change'; message: string }
  | { ok: false; code: 'missing_values'; message: string }
  | { ok: false; code: 'two_models'; message: string }
  | { ok: false; code: 'limit'; message: string }
  | { ok: false; code: 'staged_for_delete'; message: string }
export type StageResult = { ok: true; lines: StagedChange[]; replaced: boolean } | StageRefusal

const keyText = (key: { name: string; value: string }[]) => key.map((k) => `${k.name}=${k.value}`).join('&')

/** A staged line from the popup's derivation of the link for the row (`resolveAction`), or the reason it cannot be staged. */
export function stagedFrom(resolved: ResolvedAction, request: StagedChange['request'], rowKey: StagedChange['rowKey'] = []): StagedChange | StageRefusal {
  if (resolved.kind !== 'change') return { ok: false, code: 'not_a_change', message: `\`${resolved.link}\` runs a task, and a task is confirmed once, not staged.` }
  if (resolved.missing.length > 0) return { ok: false, code: 'missing_values', message: `The row does not give ${resolved.missing.join(', ')}, so this cannot be staged.` }
  const operation = resolved.operation as StagedChange['operation']
  return {
    id: `${resolved.link}|${resolved.target}|${operation === 'insert' ? `new-${Math.random().toString(36).slice(2, 10)}` : keyText(resolved.key)}`,
    link: resolved.link, widget: request.widget, model: resolved.target.slice(0, resolved.target.indexOf('.')), entity: resolved.target.slice(resolved.target.indexOf('.') + 1), operation,
    label: resolved.title, key: resolved.key, version: resolved.version, writes: resolved.writes, rowKey, request,
  }
}

/**
 * Adds a line to the set: the same link for the same row **replaces** its line (a cell edited twice is one edit); a delete of a row drops the edits staged for it, and an edit of a row staged for delete is refused; a
 * line of another model is refused naming both; a set over `limit` lines is refused. Returns the new set or the refusal; the set is never changed in place.
 */
export function stageChange(set: readonly StagedChange[], line: StagedChange, limit: number = DEFAULT_PENDING_LIMIT): StageResult {
  const first = set[0]
  if (first && first.model !== line.model)
    return { ok: false, code: 'two_models', message: `A set of changes is for one model: it already changes \`${first.model}\`, and this is a change to \`${line.model}\`. Submit or discard the set first.` }
  const same = (a: StagedChange) => a.entity === line.entity && keyText(a.key) === keyText(line.key) && line.operation !== 'insert'
  if (line.operation === 'edit' && set.some((a) => same(a) && a.operation === 'delete'))
    return { ok: false, code: 'staged_for_delete', message: `That row is staged for delete: remove the delete from the set to edit it.` }
  const replaced = set.some((a) => a.id === line.id)
  let next = set.filter((a) => a.id !== line.id)
  if (line.operation === 'delete') next = next.filter((a) => !(same(a) && a.operation === 'edit'))
  if (next.length >= limit) return { ok: false, code: 'limit', message: `A set has at most ${limit} changes; submit these first.` }
  return { ok: true, lines: [...next, line], replaced }
}

/** The lines the review shows, grouped by entity, in the order staged; a row several links changed is one group of its lines. */
export function groupLines(set: readonly StagedChange[]): { entity: string; rows: { key: string; lines: StagedChange[] }[] }[] {
  const entities = new Map<string, Map<string, StagedChange[]>>()
  for (const line of set) {
    const rows = entities.get(line.entity) ?? new Map<string, StagedChange[]>()
    const k = line.operation === 'insert' ? line.id : keyText(line.key)
    rows.set(k, [...(rows.get(k) ?? []), line])
    entities.set(line.entity, rows)
  }
  return [...entities].map(([entity, rows]) => ({ entity, rows: [...rows].map(([key, lines]) => ({ key, lines })) }))
}

/** The pending set as a small store a component reads with `useSyncExternalStore`: the lines, a subscription, and the changes the shell makes (stage, remove, clear). */
export interface PendingStore {
  get(): readonly StagedChange[]
  subscribe(listener: () => void): () => void
  stage(line: StagedChange | StageRefusal, limit?: number): StageResult
  remove(id: string): void
  clear(): void
}

export function createPendingStore(initial: readonly StagedChange[] = []): PendingStore {
  let lines: readonly StagedChange[] = initial
  const listeners = new Set<() => void>()
  const set = (next: readonly StagedChange[]) => { lines = next; listeners.forEach((l) => l()) }
  return {
    get: () => lines,
    subscribe(listener) { listeners.add(listener); return () => { listeners.delete(listener) } },
    stage(line, limit = DEFAULT_PENDING_LIMIT) {
      if ('ok' in line) return line
      const result = stageChange(lines, line, limit)
      if (result.ok) set(result.lines)
      return result
    },
    remove(id) { set(lines.filter((l) => l.id !== id)) },
    clear() { set([]) },
  }
}

/** Whether a staged line is about the row of a widget with these fields (the row's values by field name): every field of the line's row key has the line's value. A line with no row key marks nothing. */
export function lineMarksRow(line: Pick<StagedChange, 'rowKey'>, fields: Record<string, unknown>): boolean {
  return line.rowKey.length > 0 && line.rowKey.every((k) => {
    const v = k.field in fields ? fields[k.field] : fields[k.field.slice(k.field.lastIndexOf('.') + 1)]
    return v !== undefined && v !== null && String(v) === k.value
  })
}
