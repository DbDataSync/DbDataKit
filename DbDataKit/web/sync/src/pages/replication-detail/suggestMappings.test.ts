import { describe, expect, it } from 'vitest'
import type { ColumnMetadata } from '../../api/types'
import { nextMappings, suggestColumnMappings } from './suggestMappings'

const col = (name: string): ColumnMetadata => ({ name, nativeType: 'int', isNullable: false, isPrimaryKey: false, isIdentity: false })
const items = [col('Id'), col('Name')]
const dotted = [col('Id'), col('Label')]

describe('suggestColumnMappings', () => {
  it('pairs the columns that have the same name on both sides', () => {
    expect(suggestColumnMappings(items, [col('Name'), col('Other')]).map((m) => m.sourceColumn)).toEqual(['Name'])
  })
})

describe('nextMappings', () => {
  it('suggests when nothing is mapped yet', () => {
    const suggested = suggestColumnMappings(items, items)
    expect(nextMappings([], null, suggested)).toBe(suggested)
  })

  it('follows a source that was picked and then replaced, while nobody has touched the mappings', () => {
    const first = suggestColumnMappings(dotted, dotted)
    const second = suggestColumnMappings(items, items)
    expect(nextMappings(first, first, second)).toBe(second)
  })

  it('leaves what was edited, and what was saved, alone', () => {
    const first = suggestColumnMappings(dotted, dotted)
    const second = suggestColumnMappings(items, items)
    const edited = first.map((m, i) => (i === 1 ? { ...m, targetColumn: 'Renamed' } : m))
    expect(nextMappings(edited, first, second)).toBeNull()
    expect(nextMappings(first, null, second)).toBeNull() // a saved mapping: nothing was suggested for it
  })

  it('keeps the mappings when the suggestion is empty or the same', () => {
    const same = suggestColumnMappings(items, items)
    expect(nextMappings(same, same, same)).toBeNull()
    expect(nextMappings(same, same, [])).toBeNull()
  })
})
