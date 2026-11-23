import type { CheckForm } from '../types'

/** A kind of quality check, what it is for and which keys of the file it takes. The names are the file's (`check/1`). */
export interface CheckKindInfo { kind: string; label: string; hint: string; fields: CheckField[] }

export type CheckField = 'columns' | 'values' | 'min' | 'max' | 'references' | 'max_age_column' | 'max_age' | 'max_increase_percent' | 'max_decrease_percent'

export const CHECK_KINDS: CheckKindInfo[] = [
  { kind: 'freshness', label: 'Freshness', hint: 'The newest data is no older than a limit. With no column, the age is the replication\'s lag behind its source.', fields: ['max_age_column', 'max_age'] },
  { kind: 'not_null', label: 'Not null', hint: 'No row has a missing value in the column (or columns).', fields: ['columns'] },
  { kind: 'unique', label: 'Unique', hint: 'No two rows share the value of the column (or the combination of columns).', fields: ['columns'] },
  { kind: 'accepted_values', label: 'Accepted values', hint: 'The column holds only the values listed.', fields: ['columns', 'values'] },
  { kind: 'range', label: 'Range', hint: 'Every value is between a minimum and a maximum (numbers, or dates as 2026-01-31).', fields: ['columns', 'min', 'max'] },
  { kind: 'referential', label: 'Referential', hint: 'Every value exists in a column of another dataset.', fields: ['columns', 'references'] },
  { kind: 'row_count_change', label: 'Row count change', hint: 'The row count has not grown or shrunk by more than a percentage since the last evaluation.', fields: ['max_increase_percent', 'max_decrease_percent'] },
]

export const kindInfo = (kind: string | null | undefined) => CHECK_KINDS.find((k) => k.kind === kind)

/** A check as the editor holds it: every value is the text the person typed, and a list is one text. `wire` turns it into the file's keys. */
export interface CheckDraft {
  id: string
  kind: string
  dataset: string
  severity: string
  onError: string
  description: string
  columns: string
  values: string
  min: string
  max: string
  maxAge: string
  referencesDataset: string
  referencesColumns: string
  maxIncrease: string
  maxDecrease: string
}

/** A new check of a kind: severity `warn`, and a check that cannot run alerts, as the file's own defaults. */
export function blankDraft(kind = 'freshness', dataset = ''): CheckDraft {
  return { id: '', kind, dataset, severity: 'warn', onError: 'alert', description: '', columns: '', values: '', min: '', max: '', maxAge: '', referencesDataset: '', referencesColumns: '', maxIncrease: '', maxDecrease: '' }
}

const list = (text: string, separator: RegExp) => text.split(separator).map((v) => v.trim()).filter((v) => v !== '')
const orNull = (text: string) => (text.trim() === '' ? null : text.trim())

/**
 * What the person typed, as the file's keys: only the keys the kind has (changing the kind drops the old kind's values), a list split at commas
 * (values: one per line). Nothing is judged here: the draft's check reads the section with the same strict reader a Runner loads it with.
 */
export function wire(draft: CheckDraft): CheckForm {
  const fields = kindInfo(draft.kind)?.fields ?? []
  const columns = list(draft.columns, /,/)
  const referenceColumns = list(draft.referencesColumns, /,/)
  const form: CheckForm = { id: draft.id.trim(), kind: draft.kind, dataset: draft.dataset.trim(), severity: draft.severity, on_error: draft.onError, description: orNull(draft.description) }
  if (fields.includes('columns') || fields.includes('max_age_column')) {
    if (columns.length === 1) form.column = columns[0]
    else if (columns.length > 1) form.columns = columns
  }
  if (fields.includes('values')) form.values = list(draft.values, /\r?\n/)
  if (fields.includes('min')) form.min = orNull(draft.min)
  if (fields.includes('max')) form.max = orNull(draft.max)
  if (fields.includes('max_age')) form.max_age = orNull(draft.maxAge)
  if (fields.includes('references')) form.references = { dataset: orNull(draft.referencesDataset), ...(referenceColumns.length > 1 ? { columns: referenceColumns } : { column: referenceColumns[0] ?? null }) }
  if (fields.includes('max_increase_percent')) form.max_increase_percent = orNull(draft.maxIncrease)
  if (fields.includes('max_decrease_percent')) form.max_decrease_percent = orNull(draft.maxDecrease)
  return form
}

/** A number or a date the file holds unquoted (`min: 0`, `max_increase_percent: 50`) as the editor's text. */
const text = (v: unknown): string => (v === null || v === undefined ? '' : String(v))

/** A check as the file has it, as the editor holds it. */
export function draftOf(form: CheckForm): CheckDraft {
  return {
    id: text(form.id), kind: form.kind ?? 'freshness', dataset: text(form.dataset), severity: form.severity ?? 'warn', onError: form.on_error ?? 'alert', description: text(form.description),
    columns: (form.columns ?? (form.column ? [form.column] : [])).join(', '), values: (form.values ?? []).map(text).join('\n'), min: text(form.min), max: text(form.max), maxAge: text(form.max_age),
    referencesDataset: text(form.references?.dataset), referencesColumns: (form.references?.columns ?? (form.references?.column ? [form.references.column] : [])).join(', '),
    maxIncrease: text(form.max_increase_percent), maxDecrease: text(form.max_decrease_percent),
  }
}

/** What a check says, in a line for the list. */
export function summary(form: CheckForm): string {
  const columns = (form.columns ?? (form.column ? [form.column] : [])).join(', ')
  const on = columns ? `${form.dataset}.${columns}` : form.dataset
  switch (form.kind) {
    case 'freshness': return `${form.dataset}${columns ? ` by ${columns}` : ''}: no older than ${form.max_age ?? '?'}`
    case 'not_null': return `${on} has no missing value`
    case 'unique': return `${on} is unique`
    case 'accepted_values': return `${on} is one of ${(form.values ?? []).join(', ')}`
    case 'range': return `${on} is ${[form.min ? `at least ${form.min}` : '', form.max ? `at most ${form.max}` : ''].filter(Boolean).join(' and ')}`
    case 'referential': return `${on} exists in ${form.references?.dataset ?? '?'}`
    case 'row_count_change': return `${form.dataset}: row count ${[form.max_increase_percent ? `up at most ${form.max_increase_percent}%` : '', form.max_decrease_percent ? `down at most ${form.max_decrease_percent}%` : ''].filter(Boolean).join(', ')}`
    default: return form.dataset ?? ''
  }
}

/** Whether two lists of checks are the same: the order and every key count. */
export const sameChecks = (a: CheckForm[], b: CheckForm[]) => JSON.stringify(a) === JSON.stringify(b)

/** Why a check cannot be put in the list yet, or null: an id, unique in the replication, and a dataset. */
export function applyBlockedReason(draft: CheckDraft, checks: CheckForm[], index: number): string | null {
  const id = draft.id.trim()
  if (id === '') return 'A check needs an id.'
  if (checks.some((c, i) => i !== index && (c.id ?? '').trim() === id)) return `The replication has a check '${id}' already.`
  if (draft.dataset.trim() === '') return 'A check needs the dataset it checks.'
  return null
}
