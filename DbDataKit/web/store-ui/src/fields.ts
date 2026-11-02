import type { AttributeSchema, EntitySchema, RowChange, RowValues } from './types'

/** Which input an attribute is edited with. The server's rules decide what is accepted; this only chooses a control and parses what was typed. */
export type InputKind = 'checkbox' | 'select' | 'integer' | 'number' | 'date' | 'time' | 'datetime' | 'text' | 'textarea' | 'json'

export function inputKind(attribute: AttributeSchema): InputKind {
  if (attribute.allowed?.length) return 'select'
  switch (attribute.kind) {
    case 'boolean': return 'checkbox'
    case 'int8': case 'int16': case 'int32': case 'int64': return 'integer'
    case 'decimal': case 'float': case 'double': return 'number'
    case 'date': return 'date'
    case 'time': return 'time'
    case 'timestamp': return 'datetime'
    case 'json': return 'json'
    case 'string': return attribute.length === null || attribute.length > 255 ? 'textarea' : 'text'
    default: return 'text'
  }
}

export type Parsed = { ok: true; value: unknown } | { ok: false; message: string }

/** What a typed value means for the wire. Empty means "no value" (null); the server's `required` and defaults say whether that is allowed. */
export function parseInput(attribute: AttributeSchema, text: string): Parsed {
  const kind = inputKind(attribute)
  if (kind === 'checkbox') return { ok: true, value: text === 'true' }
  if (text.trim() === '') return { ok: true, value: null }
  switch (kind) {
    case 'integer':
      return /^-?\d+$/.test(text.trim()) ? { ok: true, value: Number(text.trim()) } : { ok: false, message: 'A whole number.' }
    case 'number': {
      const n = Number(text.trim())
      return Number.isFinite(n) ? { ok: true, value: n } : { ok: false, message: 'A number.' }
    }
    case 'json':
      try { return { ok: true, value: JSON.parse(text) } } catch { return { ok: false, message: 'Not valid JSON.' } }
    case 'datetime': {
      const t = Date.parse(text)
      return Number.isNaN(t) ? { ok: false, message: 'A date and time.' } : { ok: true, value: new Date(t).toISOString() }
    }
    default: return { ok: true, value: text }
  }
}

/** A stored value as the text of its input. */
export function toInput(attribute: AttributeSchema, value: unknown): string {
  if (value === null || value === undefined) return ''
  if (inputKind(attribute) === 'json') return typeof value === 'string' ? value : JSON.stringify(value, null, 2)
  if (inputKind(attribute) === 'datetime' && typeof value === 'string') return value.replace(/Z$/, '').slice(0, 19)
  return String(value)
}

/** A stored value as a grid cell shows it. */
export function formatCell(_attribute: AttributeSchema, value: unknown): string {
  if (value === null || value === undefined) return ''
  if (typeof value === 'boolean') return value ? 'yes' : 'no'
  if (typeof value === 'object') return JSON.stringify(value)
  return String(value)
}

export function keyOf(entity: EntitySchema, values: RowValues): RowValues {
  return Object.fromEntries(entity.key.map((name) => [name, values[name]]))
}

export function titleOf(item: { name: string; title: string | null }): string {
  return item.title ?? item.name.replace(/_/g, ' ').replace(/^./, (c) => c.toUpperCase())
}

/** The attributes a person edits: all of them, the key's only when the row is new (a key never changes). */
export function editable(entity: EntitySchema, isNew: boolean): AttributeSchema[] {
  return entity.attributes.filter((a) => isNew || !entity.key.includes(a.name))
}

export interface DraftResult {
  /** The change to send, or null when nothing changed (an update) or a value did not parse. */
  change: RowChange | null
  /** Parse problems by attribute. */
  problems: Record<string, string>
}

/**
 * The change a form's text makes. An insert carries every value that is not empty (so the model's defaults apply to the rest); an update carries only the attributes that differ from the row
 * it read, on the version it read. Rules are the server's: nothing is checked here beyond what the text can mean.
 */
export function draftChange(entity: EntitySchema, original: { values: RowValues; version: number } | null, text: Record<string, string>): DraftResult {
  const problems: Record<string, string> = {}
  const values: RowValues = {}
  for (const attribute of editable(entity, original === null)) {
    const typed = text[attribute.name] ?? ''
    const parsed = parseInput(attribute, typed)
    if (!parsed.ok) { problems[attribute.name] = parsed.message; continue }
    if (original === null) {
      if (parsed.value !== null && !(attribute.kind === 'boolean' && typed === '' )) values[attribute.name] = parsed.value
    } else if (!sameValue(original.values[attribute.name], parsed.value)) values[attribute.name] = parsed.value
  }
  if (Object.keys(problems).length > 0) return { change: null, problems }
  if (original === null) return { change: { action: 'insert', entity: entity.name, values }, problems }
  if (Object.keys(values).length === 0) return { change: null, problems }
  return { change: { action: 'update', entity: entity.name, key: keyOf(entity, original.values), expected_version: original.version, values }, problems }
}

function sameValue(a: unknown, b: unknown): boolean {
  if (a === null || a === undefined) return b === null || b === undefined
  if (b === null || b === undefined) return false
  if (typeof a === 'object' || typeof b === 'object') return JSON.stringify(a) === JSON.stringify(b)
  return String(a) === String(b)
}
