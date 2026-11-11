import { describe, expect, it } from 'vitest'
import { familyOf, generatedValue, shapePreview } from './catalogPreview'

describe('the shape preview', () => {
  it('reads the family of a type from the words engines use', () => {
    expect(['bigint', 'serial', 'int4', 'smallint'].map(familyOf)).toEqual(['integer', 'integer', 'integer', 'integer'])
    expect(['numeric(12,2)', 'decimal', 'double precision', 'money'].map(familyOf)).toEqual(['decimal', 'decimal', 'decimal', 'decimal'])
    expect(['varchar(50)', 'nvarchar(max)', 'text', 'jsonb'].map(familyOf)).toEqual(['text', 'text', 'text', 'text'])
    expect(['bit', 'boolean', 'date', 'datetime2', 'timestamp without time zone', 'uuid', 'geometry', 'interval'].map(familyOf)).toEqual(['boolean', 'boolean', 'date', 'timestamp', 'timestamp', 'uuid', 'other', 'other'])
  })

  it('makes the same rows every time, unique for a key, and never null for a key or a not null column', () => {
    const table = { schema: 's', name: 't', kind: 'table' as const, columns: [{ name: 'id', type: 'int', nullable: true, key: true }, { name: 'n', type: 'text', nullable: true, key: false }, { name: 'z', type: 'int', nullable: false, key: false }] }
    const a = shapePreview(table)
    expect(a).toEqual(shapePreview(table))
    expect(a.rows).toHaveLength(5)
    expect(new Set(a.rows.map((r) => r[0])).size).toBe(5)
    expect(a.rows.map((r) => r[1])).toEqual(['n_1', 'n_2', null, 'n_4', 'n_5'])
    expect(a.rows.every((r) => r[2] !== null)).toBe(true)
    expect(generatedValue({ name: 'x', type: 'weird', nullable: false, key: false }, 0)).toBe('<weird>')
  })
})
