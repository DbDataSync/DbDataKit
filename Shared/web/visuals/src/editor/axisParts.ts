import { checkFormat, formatValue } from '../views/format'
import type { WidgetDoc } from '../views/model'

/**
 * The pure part of the axis and column controls of the widget editor (plan 32 DBR44): a format as a small form (preset, number mapping or date mapping) with a live sample, and the entries of an axis well (a chart's
 * nested `x`, a matrix's `rows` and `columns`) with the format, the sort and the title each level carries. Nothing here draws; a control calls these and applies the edit command `setPath` they describe.
 */

const isObject = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v)

export type FormatKind = 'default' | 'preset' | 'number' | 'date'

/** A format as the form shows it: every control has a value as text, so a half-typed number never loses what was typed. */
export interface FormatDraft {
  kind: FormatKind
  preset: string
  style: string
  decimals: string
  currency: string
  grouping: boolean
  date: string
}

export const NUMBER_STYLES = ['number', 'currency', 'percent', 'compact'] as const
export const NUMBER_PRESETS = ['catalog', 'full', 'compact', 'percent'] as const
export const DATE_PRESETS = ['year', 'quarter', 'month', 'month-year', 'date', 'datetime'] as const

/** The form's state for a stored `format` (undefined, a preset, a number mapping or a date mapping). */
export function draftOf(format: unknown): FormatDraft {
  const base: FormatDraft = { kind: 'default', preset: 'catalog', style: 'number', decimals: '', currency: '', grouping: true, date: 'month-year' }
  if (typeof format === 'string' && format !== '') return { ...base, kind: 'preset', preset: format }
  if (isObject(format) && typeof format.date === 'string') return { ...base, kind: 'date', date: format.date }
  if (isObject(format)) {
    return {
      ...base, kind: 'number',
      style: typeof format.style === 'string' ? format.style : 'number',
      decimals: typeof format.decimals === 'number' ? String(format.decimals) : '',
      currency: typeof format.currency === 'string' ? format.currency : '',
      grouping: format.grouping !== false,
    }
  }
  return base
}

/** The `format` a form's state writes: undefined for the default, the preset's name, or a mapping with only the keys that were given (`grouping` only when it is off, as it is true otherwise). */
export function formatOf(draft: FormatDraft): unknown {
  switch (draft.kind) {
    case 'default': return undefined
    case 'preset': return draft.preset
    case 'date': return { date: draft.date }
    case 'number': {
      const out: Record<string, unknown> = { style: draft.style }
      const decimals = draft.decimals.trim()
      if (decimals !== '') out.decimals = Number(decimals)
      if (draft.style === 'currency' || draft.currency.trim() !== '') out.currency = draft.currency.trim().toUpperCase()
      if (!draft.grouping) out.grouping = false
      return out
    }
  }
}

/** What is wrong with the format the form writes, in the loaders' words (`checkFormat`), or null. A number that is not a number is `decimals` read as text. */
export function draftProblem(draft: FormatDraft): string | null {
  if (draft.kind === 'number' && draft.decimals.trim() !== '' && !Number.isInteger(Number(draft.decimals))) return '`decimals` is a whole number.'
  const format = formatOf(draft)
  return format === undefined ? null : checkFormat(format)?.message ?? null
}

/** A sample of what the format does, written by the same formatter the page and the PDF use: a number for a number format and a preset, a date for a date mapping. */
export function formatSample(format: unknown): string {
  const date = isObject(format) && typeof format.date === 'string'
  const value = date ? '2026-03-07T14:05:09' : 1234567.891
  const problem = format === undefined ? null : checkFormat(format)
  if (problem) return ''
  return formatValue(value, format as never)
}

// ---- the levels of an axis ----

export type LevelKey = 'format' | 'sort' | 'title'
export type AxisChannel = 'x' | 'rows' | 'columns'

export interface Level { field: string; format?: unknown; sort?: { by: string; dir?: string }; title?: string }

/** The levels of an axis well as written: a field or `{field, ...}` each; a chart's `x` that is one channel is one level. */
export function levelsOf(widget: WidgetDoc, channel: AxisChannel): Level[] {
  const node = (widget as unknown as { encodings?: Record<string, unknown> }).encodings?.[channel]
  const list = Array.isArray(node) ? node : node === undefined ? [] : [node]
  return list.flatMap((entry): Level[] => {
    if (typeof entry === 'string' && entry.length > 0) return [{ field: entry }]
    if (isObject(entry) && typeof entry.field === 'string' && entry.field.length > 0) {
      const sort = isObject(entry.sort) && typeof entry.sort.by === 'string' ? { by: entry.sort.by, ...(typeof entry.sort.dir === 'string' ? { dir: entry.sort.dir } : {}) } : undefined
      return [{ field: entry.field, format: entry.format, ...(sort ? { sort } : {}), ...(typeof entry.title === 'string' ? { title: entry.title } : {}) }]
    }
    return []
  })
}

/** An entry with a key set (or removed: undefined): `{field, key: value}`, and the bare field again when nothing else is left. `field` stays first. */
export function levelEntry(entry: unknown, key: LevelKey, value: unknown): unknown {
  const object: Record<string, unknown> = isObject(entry) ? { ...entry } : { field: entry }
  if (value === undefined) delete object[key]
  else object[key] = value
  return Object.keys(object).every((k) => k === 'field') ? object.field : object
}

/**
 * The `setPath` of one level's format, sort or title: its place in the document and the entry to write there. A chart's `x` that is one channel (not a list) is written in place (`encodings.x`); a list's level at its index.
 * Only the innermost level of a list may sort by a measure (the derivation says so); a sort is `{by, dir}`, `dir` omitted when it is the default `asc`.
 */
export function levelCommand(id: string, widget: WidgetDoc, channel: AxisChannel, index: number, key: LevelKey, value: unknown): { path: Array<string | number>; value: unknown } {
  const node = (widget as unknown as { encodings?: Record<string, unknown> }).encodings?.[channel]
  const base = ['widgets', id, 'encodings', channel]
  if (!Array.isArray(node)) return { path: base, value: levelEntry(node, key, value) }
  return { path: [...base, index], value: levelEntry(node[index], key, value) }
}

/** A sort as the form's two controls write it: by (`sort_key`, `label` or a measure) and the direction. */
export function sortValue(by: string, dir: string | undefined): { by: string; dir?: 'desc' } | undefined {
  if (by === '') return undefined
  return dir === 'desc' ? { by, dir: 'desc' } : { by }
}

// ---- the format of a measure channel and of a matrix value (DBR49) ----

/** The channels that carry a measure or a category and take a `format` besides the axis levels: a chart's `y`, `color`, `column` and `row`, and each value of a matrix. */
export type FormatChannel = 'y' | 'color' | 'column' | 'row' | 'values'
export const FORMAT_CHANNELS: readonly FormatChannel[] = ['y', 'color', 'column', 'row', 'values']

/** One such channel as written: its place (`index` is the position in a matrix's `values`, 0 for a chart channel), its field and its `format`. */
export interface ChannelFormat { channel: FormatChannel; index: number; field: string; format?: unknown }

/** The channels of a chart or a matrix that take a format and name a field, in the order the form shows them. */
export function formatChannelsOf(widget: WidgetDoc): ChannelFormat[] {
  const encodings = (widget as unknown as { encodings?: Record<string, unknown> }).encodings
  if (!encodings) return []
  const found: ChannelFormat[] = []
  if (widget.type === 'chart') {
    for (const channel of ['y', 'color', 'column', 'row'] as const) {
      const entry = encodings[channel]
      if (typeof entry === 'string' && entry.length > 0) found.push({ channel, index: 0, field: entry })
      else if (isObject(entry) && typeof entry.field === 'string' && entry.field.length > 0) found.push({ channel, index: 0, field: entry.field, format: entry.format })
    }
  } else if (widget.type === 'matrix' && Array.isArray(encodings.values)) {
    encodings.values.forEach((entry, index) => {
      if (isObject(entry) && typeof entry.field === 'string' && entry.field.length > 0) found.push({ channel: 'values', index, field: entry.field, format: entry.format })
    })
  }
  return found
}

/**
 * The `setPath` that sets (or, with undefined, removes) the format of one such channel: a chart channel written as a bare field becomes `{field, format}` and goes back to the bare field when the format is removed
 * and nothing else is left on it; a matrix value stays an object (its schema has no bare form). The sibling keys (`type`, `sort`, `empty`, `label`) are kept.
 */
export function formatChannelCommand(id: string, widget: WidgetDoc, channel: FormatChannel, index: number, value: unknown): { path: Array<string | number>; value: unknown } {
  const encodings = (widget as unknown as { encodings?: Record<string, unknown> }).encodings ?? {}
  if (channel === 'values') {
    const entry = Array.isArray(encodings.values) ? encodings.values[index] : undefined
    const object: Record<string, unknown> = isObject(entry) ? { ...entry } : { field: entry }
    if (value === undefined) delete object.format
    else object.format = value
    return { path: ['widgets', id, 'encodings', 'values', index], value: object }
  }
  return { path: ['widgets', id, 'encodings', channel], value: levelEntry(encodings[channel], 'format', value) }
}
