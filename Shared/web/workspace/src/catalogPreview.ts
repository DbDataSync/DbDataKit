import type { CacheColumn, CacheTable } from './catalogCache'

/** How many generated rows a preview shows. */
export const PREVIEW_ROWS = 5

/** What kind of value a type text asks for, read loosely from the words engines use; anything unrecognised is `other` and is shown as its type in angle brackets. */
export function familyOf(type: string): 'integer' | 'decimal' | 'text' | 'boolean' | 'date' | 'timestamp' | 'uuid' | 'other' {
  const t = type.toLowerCase()
  if (/\b(bool|boolean|bit)\b/.test(t)) return 'boolean'
  if (/uuid|uniqueidentifier/.test(t)) return 'uuid'
  if (/timestamp|datetime|smalldatetime/.test(t)) return 'timestamp'
  if (/^date\b|^date$/.test(t)) return 'date'
  if (/int|serial/.test(t) && !/interval|point/.test(t)) return 'integer'
  if (/numeric|decimal|number|money|float|double|real/.test(t)) return 'decimal'
  if (/char|text|string|clob|json|xml/.test(t)) return 'text'
  return 'other'
}

/** One generated value: deterministic, so the same table always previews the same, and unique for a key column. */
export function generatedValue(column: CacheColumn, row: number): string | null {
  // a nullable column shows one null so a person sees that it may be, a key column never does
  if (column.nullable && !column.key && row === 2) return null
  const n = row + 1
  switch (familyOf(column.type)) {
    case 'integer': return String(n)
    case 'decimal': return (n * 10.5).toFixed(2)
    case 'text': return `${column.name}_${n}`
    case 'boolean': return row % 2 === 0 ? 'true' : 'false'
    case 'date': return `2026-01-${String(n).padStart(2, '0')}`
    case 'timestamp': return `2026-01-${String(n).padStart(2, '0')} 08:00:00`
    case 'uuid': return `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`
    default: return `<${column.type}>`
  }
}

/** The preview of a table: its columns and a few rows made from their types. These are not the database's rows (the cache holds none, D20); they show the shape a mapping or a model would see. */
export function shapePreview(table: CacheTable, rows = PREVIEW_ROWS): { columns: CacheColumn[]; rows: (string | null)[][] } {
  return { columns: table.columns, rows: Array.from({ length: rows }, (_, r) => table.columns.map((c) => generatedValue(c, r))) }
}
