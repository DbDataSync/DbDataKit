import type { ColumnMapping, ColumnMetadata } from '../../api/types'

/** The same-name pairs of the two column lists: what a new mapping starts from. */
export function suggestColumnMappings(source: ColumnMetadata[], target: ColumnMetadata[]): ColumnMapping[] {
  const sourceNames = new Set(source.map((c) => c.name))
  return target
    .filter((tc) => sourceNames.has(tc.name))
    .map((tc) => ({ sourceColumn: tc.name, targetColumn: tc.name, transform: null }))
}

/**
 * What the mappings become when the columns on offer change. A new mapping's suggestion follows the
 * source and target tables for as long as nobody has touched it: picking one table, then another, must
 * not leave the first table's columns behind, because they are saved and then fail at provisioning
 * ("Source column … was not found"). Anything the operator edited, and any saved mapping, is left alone.
 * Returns null for "leave as is".
 */
export function nextMappings(current: ColumnMapping[], lastSuggested: ColumnMapping[] | null, suggested: ColumnMapping[]): ColumnMapping[] | null {
  const untouched = current.length === 0 || (lastSuggested !== null && sameMappings(current, lastSuggested))
  if (!untouched || suggested.length === 0 || sameMappings(current, suggested)) return null
  return suggested
}

function sameMappings(a: ColumnMapping[], b: ColumnMapping[]): boolean {
  return a.length === b.length && a.every((m, i) => m.sourceColumn === b[i].sourceColumn && m.targetColumn === b[i].targetColumn && (m.transform ?? null) === (b[i].transform ?? null))
}
